#!/usr/bin/env python3
"""Calculate local digital resolution from a final BonVision mesh map.

Expected input columns:
    projector_x, projector_y, texture_u, texture_v, brightness

This measures digital sampling in BonVision's nominal orthographic coordinates.
It does not measure retinal visual angle or optical blur.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import sys
import zipfile
from collections.abc import Sequence
from dataclasses import dataclass
from datetime import UTC, datetime
from importlib.metadata import version
from pathlib import Path

import matplotlib.pyplot as plt
import numpy as np
import pandas as pd
from matplotlib.colors import BoundaryNorm, ListedColormap, Normalize

DEFAULT_PROJECTOR_WIDTH = 1920
DEFAULT_PROJECTOR_HEIGHT = 1080
DEFAULT_AZIMUTH_RANGE = (-135.0, 135.0)
DEFAULT_ELEVATION_RANGE = (-22.5, 82.5)
DEFAULT_EVALUATED_CPD = 0.3
IDEAL_NYQUIST_PIXELS_PER_CYCLE = 2.0
ANALYSIS_VERSION = 9

MESH_COLUMNS = [
    "projector_x",
    "projector_y",
    "texture_u",
    "texture_v",
    "brightness",
]
CANDIDATE_TRIANGLES = np.array(["v0_v1_v2", "v0_v2_v3", "v0_v1_v3", "v1_v2_v3"])


@dataclass(frozen=True)
class MeshGrid:
    texture_u: np.ndarray
    texture_v: np.ndarray
    projector_x: np.ndarray
    projector_y: np.ndarray
    brightness: np.ndarray


@dataclass(frozen=True)
class ResolutionGrid:
    azimuth_vertices: np.ndarray
    nominal_elevation_vertices: np.ndarray
    projector_pixel_x: np.ndarray
    projector_pixel_y: np.ndarray
    render_azimuth_pixels_per_degree: float
    render_nominal_elevation_pixels_per_degree: float
    candidate_azimuth: np.ndarray
    candidate_nominal_elevation: np.ndarray
    candidate_projector_pixel_x: np.ndarray
    candidate_projector_pixel_y: np.ndarray
    candidate_nominal_azimuth_pixels_per_degree: np.ndarray
    candidate_nominal_elevation_pixels_per_degree: np.ndarray
    candidate_minimum_nominal_pixels_per_degree: np.ndarray
    candidate_ideal_nyquist_bound_cpd: np.ndarray
    candidate_pixels_per_evaluated_cycle: np.ndarray


def satisfies_ideal_nyquist_sampling(
    pixels_per_cycle: np.ndarray | float,
) -> np.ndarray | bool:
    return pixels_per_cycle > IDEAL_NYQUIST_PIXELS_PER_CYCLE


def nominal_solid_angle_weights(
    resolution: ResolutionGrid,
) -> np.ndarray:
    azimuth_width = np.abs(
        np.diff(np.deg2rad(resolution.azimuth_vertices))
    )
    elevation_height = np.abs(
        np.diff(
            np.sin(
                np.deg2rad(
                    resolution.nominal_elevation_vertices
                )
            )
        )
    )
    return np.outer(elevation_height, azimuth_width)


def parse_arguments(arguments: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=__doc__,
        formatter_class=argparse.ArgumentDefaultsHelpFormatter,
    )
    parser.add_argument("mesh_map", type=Path, help="Final mesh-map XLSX or CSV file.")
    parser.add_argument("--width", type=int, default=DEFAULT_PROJECTOR_WIDTH)
    parser.add_argument("--height", type=int, default=DEFAULT_PROJECTOR_HEIGHT)
    parser.add_argument("--azimuth-min", type=float, default=DEFAULT_AZIMUTH_RANGE[0])
    parser.add_argument("--azimuth-max", type=float, default=DEFAULT_AZIMUTH_RANGE[1])
    parser.add_argument("--elevation-min", type=float, default=DEFAULT_ELEVATION_RANGE[0])
    parser.add_argument("--elevation-max", type=float, default=DEFAULT_ELEVATION_RANGE[1])
    parser.add_argument(
        "--task-cpd",
        dest="evaluated_cpd",
        type=float,
        default=DEFAULT_EVALUATED_CPD,
        help="Spatial frequency to evaluate, in cycles/nominal degree.",
    )
    parser.add_argument(
        "--summary-elevation",
        type=float,
        default=0.0,
        help="Elevation used for the azimuth summary.",
    )
    parser.add_argument("--sheet", help="XLSX worksheet name; default is the first sheet.")
    parser.add_argument(
        "--output-dir",
        type=Path,
        help="Default: <mesh-map-name>_resolution_analysis beside the input.",
    )
    settings = parser.parse_args(arguments)

    if settings.width <= 0 or settings.height <= 0:
        parser.error("--width and --height must be positive.")
    if settings.azimuth_max <= settings.azimuth_min:
        parser.error("--azimuth-max must be greater than --azimuth-min.")
    if settings.elevation_max <= settings.elevation_min:
        parser.error("--elevation-max must be greater than --elevation-min.")
    if settings.evaluated_cpd <= 0:
        parser.error("--task-cpd must be positive.")
    return settings


def read_mesh_map(
    path: Path,
    sheet_name: str | None,
) -> tuple[pd.DataFrame, str]:
    extension = path.suffix.lower()
    if extension == ".xlsx":
        with pd.ExcelFile(path) as workbook:
            selected_sheet = sheet_name if sheet_name is not None else workbook.sheet_names[0]
            raw_table = pd.read_excel(workbook, sheet_name=selected_sheet, header=None)
    elif extension in {".csv", ".tsv", ".txt"}:
        separator = "\t" if extension == ".tsv" else None
        raw_table = pd.read_csv(path, header=None, sep=separator, engine="python")
        selected_sheet = "not applicable"
    else:
        raise ValueError("Input must be XLSX, CSV, TSV or TXT.")

    if raw_table.shape[1] < len(MESH_COLUMNS):
        raise ValueError(
            "Input has fewer than five columns. Use the final interpolated (x, y, u, v, brightness) mesh map."
        )

    mesh_table = raw_table.iloc[:, :5].apply(
        pd.to_numeric,
        errors="coerce",
    )
    mesh_table.columns = MESH_COLUMNS
    mesh_table = mesh_table.dropna().astype(float).reset_index(drop=True)

    if mesh_table.empty:
        raise ValueError("No valid five-column mesh rows were found.")
    if not np.isfinite(mesh_table.to_numpy()).all():
        raise ValueError("Mesh contains non-finite values.")
    if not mesh_table["texture_u"].between(0, 1).all():
        raise ValueError("Texture U coordinates must be between 0 and 1.")
    if not mesh_table["texture_v"].between(0, 1).all():
        raise ValueError("Texture V coordinates must be between 0 and 1.")
    return mesh_table, selected_sheet


def build_mesh_grid(mesh_table: pd.DataFrame) -> MeshGrid:
    mesh_table = mesh_table.copy()
    mesh_table["u_key"] = mesh_table["texture_u"].round(12)
    mesh_table["v_key"] = mesh_table["texture_v"].round(12)

    if mesh_table.duplicated(["v_key", "u_key"]).any():
        duplicate = mesh_table.loc[
            mesh_table.duplicated(["v_key", "u_key"], keep=False),
            ["u_key", "v_key"],
        ].iloc[0]
        raise ValueError(f"Duplicate mesh vertex at U={duplicate['u_key']}, V={duplicate['v_key']}.")

    texture_u = mesh_table.groupby("u_key")["texture_u"].mean().sort_index()
    texture_v = mesh_table.groupby("v_key")["texture_v"].mean().sort_index()
    if len(texture_u) < 2 or len(texture_v) < 2:
        raise ValueError("Mesh must contain at least two U and two V coordinates.")

    expected_index = pd.MultiIndex.from_product(
        [texture_v.index, texture_u.index],
        names=["v_key", "u_key"],
    )
    indexed_mesh = mesh_table.set_index(["v_key", "u_key"])
    missing_vertices = expected_index.difference(indexed_mesh.index)
    if len(missing_vertices):
        missing_v, missing_u = missing_vertices[0]
        raise ValueError(f"Missing mesh vertex at U={missing_u}, V={missing_v}.")

    input_coordinates = list(zip(mesh_table["v_key"], mesh_table["u_key"]))
    if input_coordinates != list(expected_index):
        raise ValueError(
            "Mesh rows are not in BonVision row-major order "
            "(V increasing between rows, U increasing within each row)."
        )

    ordered_mesh = indexed_mesh.reindex(expected_index)
    grid_shape = (len(texture_v), len(texture_u))
    return MeshGrid(
        texture_u=texture_u.to_numpy(),
        texture_v=texture_v.to_numpy(),
        projector_x=ordered_mesh["projector_x"].to_numpy().reshape(grid_shape),
        projector_y=ordered_mesh["projector_y"].to_numpy().reshape(grid_shape),
        brightness=ordered_mesh["brightness"].to_numpy().reshape(grid_shape),
    )


def minimum_singular_value(
    azimuth_pixel_x: np.ndarray,
    elevation_pixel_x: np.ndarray,
    azimuth_pixel_y: np.ndarray,
    elevation_pixel_y: np.ndarray,
) -> np.ndarray:
    azimuth_length_squared = azimuth_pixel_x**2 + azimuth_pixel_y**2
    elevation_length_squared = elevation_pixel_x**2 + elevation_pixel_y**2
    column_dot_product = azimuth_pixel_x * elevation_pixel_x + azimuth_pixel_y * elevation_pixel_y
    trace = azimuth_length_squared + elevation_length_squared
    discriminant = np.sqrt(
        np.maximum(
            0,
            (azimuth_length_squared - elevation_length_squared) ** 2 + 4 * column_dot_product**2,
        )
    )
    return np.sqrt(np.maximum(0, (trace - discriminant) / 2))


def calculate_grating_pixels_per_degree(
    resolution: ResolutionGrid,
    grating_angle_degrees: float,
) -> np.ndarray:
    """Return local sampling for a fixed BonVision grating angle."""
    pixel_x = resolution.projector_pixel_x
    pixel_y = resolution.projector_pixel_y

    pixel_x_00 = pixel_x[:-1, :-1]
    pixel_x_01 = pixel_x[:-1, 1:]
    pixel_x_11 = pixel_x[1:, 1:]
    pixel_x_10 = pixel_x[1:, :-1]
    pixel_y_00 = pixel_y[:-1, :-1]
    pixel_y_01 = pixel_y[:-1, 1:]
    pixel_y_11 = pixel_y[1:, 1:]
    pixel_y_10 = pixel_y[1:, :-1]

    azimuth_change = np.diff(resolution.azimuth_vertices)[np.newaxis, :]
    elevation_change = np.diff(resolution.nominal_elevation_vertices)[:, np.newaxis]

    azimuth_pixel_x = np.stack(
        [
            (pixel_x_01 - pixel_x_00) / azimuth_change,
            (pixel_x_11 - pixel_x_10) / azimuth_change,
            (pixel_x_01 - pixel_x_00) / azimuth_change,
            (pixel_x_11 - pixel_x_10) / azimuth_change,
        ]
    )
    azimuth_pixel_y = np.stack(
        [
            (pixel_y_01 - pixel_y_00) / azimuth_change,
            (pixel_y_11 - pixel_y_10) / azimuth_change,
            (pixel_y_01 - pixel_y_00) / azimuth_change,
            (pixel_y_11 - pixel_y_10) / azimuth_change,
        ]
    )
    elevation_pixel_x = np.stack(
        [
            (pixel_x_11 - pixel_x_01) / elevation_change,
            (pixel_x_10 - pixel_x_00) / elevation_change,
            (pixel_x_10 - pixel_x_00) / elevation_change,
            (pixel_x_11 - pixel_x_01) / elevation_change,
        ]
    )
    elevation_pixel_y = np.stack(
        [
            (pixel_y_11 - pixel_y_01) / elevation_change,
            (pixel_y_10 - pixel_y_00) / elevation_change,
            (pixel_y_10 - pixel_y_00) / elevation_change,
            (pixel_y_11 - pixel_y_01) / elevation_change,
        ]
    )

    angle_radians = np.deg2rad(grating_angle_degrees)
    azimuth_weight = np.cos(angle_radians)
    elevation_weight = np.sin(angle_radians)

    determinant = (
        azimuth_pixel_x * elevation_pixel_y
        - elevation_pixel_x * azimuth_pixel_y
    )
    with np.errstate(divide="ignore", invalid="ignore"):
        frequency_per_pixel_x = (
            elevation_pixel_y * azimuth_weight
            - azimuth_pixel_y * elevation_weight
        ) / determinant
        frequency_per_pixel_y = (
            -elevation_pixel_x * azimuth_weight
            + azimuth_pixel_x * elevation_weight
        ) / determinant
        mapped_pixels_per_degree = 1 / np.hypot(
            frequency_per_pixel_x,
            frequency_per_pixel_y,
        )
    mapped_pixels_per_degree = np.where(
        np.isfinite(mapped_pixels_per_degree),
        mapped_pixels_per_degree,
        0.0,
    )

    render_pixels_per_degree = 1 / np.hypot(
        azimuth_weight
        / resolution.render_azimuth_pixels_per_degree,
        elevation_weight
        / resolution.render_nominal_elevation_pixels_per_degree,
    )
    candidate_pixels_per_degree = np.minimum(
        mapped_pixels_per_degree,
        render_pixels_per_degree,
    )
    return np.min(candidate_pixels_per_degree, axis=0)


def calculate_resolution(
    mesh: MeshGrid,
    width: int,
    height: int,
    azimuth_range: tuple[float, float],
    elevation_range: tuple[float, float],
    evaluated_cpd: float,
) -> ResolutionGrid:
    azimuth_span = azimuth_range[1] - azimuth_range[0]
    elevation_span = elevation_range[1] - elevation_range[0]
    azimuth_vertices = azimuth_range[0] + mesh.texture_u * azimuth_span
    nominal_elevation_vertices = elevation_range[1] - mesh.texture_v * elevation_span

    projector_pixel_x = (mesh.projector_x + 1) * width / 2
    # BonVision negates the mesh-map Y coordinate before rendering.
    projector_pixel_y = (1 + mesh.projector_y) * height / 2

    pixel_x_00 = projector_pixel_x[:-1, :-1]
    pixel_x_01 = projector_pixel_x[:-1, 1:]
    pixel_x_11 = projector_pixel_x[1:, 1:]
    pixel_x_10 = projector_pixel_x[1:, :-1]
    pixel_y_00 = projector_pixel_y[:-1, :-1]
    pixel_y_01 = projector_pixel_y[:-1, 1:]
    pixel_y_11 = projector_pixel_y[1:, 1:]
    pixel_y_10 = projector_pixel_y[1:, :-1]

    azimuth_change = np.diff(azimuth_vertices)[np.newaxis, :]
    elevation_change = np.diff(nominal_elevation_vertices)[:, np.newaxis]

    # GL_QUADS can be split along either diagonal. Evaluate both possible
    # splits and retain the most conservative result for each cell.
    azimuth_pixel_x = np.stack(
        [
            (pixel_x_01 - pixel_x_00) / azimuth_change,
            (pixel_x_11 - pixel_x_10) / azimuth_change,
            (pixel_x_01 - pixel_x_00) / azimuth_change,
            (pixel_x_11 - pixel_x_10) / azimuth_change,
        ]
    )
    azimuth_pixel_y = np.stack(
        [
            (pixel_y_01 - pixel_y_00) / azimuth_change,
            (pixel_y_11 - pixel_y_10) / azimuth_change,
            (pixel_y_01 - pixel_y_00) / azimuth_change,
            (pixel_y_11 - pixel_y_10) / azimuth_change,
        ]
    )
    elevation_pixel_x = np.stack(
        [
            (pixel_x_11 - pixel_x_01) / elevation_change,
            (pixel_x_10 - pixel_x_00) / elevation_change,
            (pixel_x_10 - pixel_x_00) / elevation_change,
            (pixel_x_11 - pixel_x_01) / elevation_change,
        ]
    )
    elevation_pixel_y = np.stack(
        [
            (pixel_y_11 - pixel_y_01) / elevation_change,
            (pixel_y_10 - pixel_y_00) / elevation_change,
            (pixel_y_10 - pixel_y_00) / elevation_change,
            (pixel_y_11 - pixel_y_01) / elevation_change,
        ]
    )

    mapped_azimuth_resolution = np.hypot(azimuth_pixel_x, azimuth_pixel_y)
    mapped_elevation_resolution = np.hypot(elevation_pixel_x, elevation_pixel_y)
    mapped_minimum_resolution = minimum_singular_value(
        azimuth_pixel_x,
        elevation_pixel_x,
        azimuth_pixel_y,
        elevation_pixel_y,
    )

    render_azimuth_resolution = width / azimuth_span
    render_elevation_resolution = height / elevation_span
    render_minimum_resolution = min(
        render_azimuth_resolution,
        render_elevation_resolution,
    )

    candidate_azimuth_resolution = np.minimum(
        mapped_azimuth_resolution,
        render_azimuth_resolution,
    )
    candidate_elevation_resolution = np.minimum(
        mapped_elevation_resolution,
        render_elevation_resolution,
    )
    candidate_minimum_resolution = np.minimum(
        mapped_minimum_resolution,
        render_minimum_resolution,
    )

    cell_shape = pixel_x_00.shape
    azimuth_left = np.broadcast_to(azimuth_vertices[:-1], cell_shape)
    azimuth_right = np.broadcast_to(azimuth_vertices[1:], cell_shape)
    elevation_top = np.broadcast_to(
        nominal_elevation_vertices[:-1, np.newaxis],
        cell_shape,
    )
    elevation_bottom = np.broadcast_to(
        nominal_elevation_vertices[1:, np.newaxis],
        cell_shape,
    )
    candidate_azimuth = np.stack(
        [
            (azimuth_left + 2 * azimuth_right) / 3,
            (2 * azimuth_left + azimuth_right) / 3,
            (2 * azimuth_left + azimuth_right) / 3,
            (azimuth_left + 2 * azimuth_right) / 3,
        ]
    )
    candidate_nominal_elevation = np.stack(
        [
            (2 * elevation_top + elevation_bottom) / 3,
            (elevation_top + 2 * elevation_bottom) / 3,
            (2 * elevation_top + elevation_bottom) / 3,
            (elevation_top + 2 * elevation_bottom) / 3,
        ]
    )
    candidate_projector_pixel_x = np.stack(
        [
            (pixel_x_00 + pixel_x_01 + pixel_x_11) / 3,
            (pixel_x_00 + pixel_x_11 + pixel_x_10) / 3,
            (pixel_x_00 + pixel_x_01 + pixel_x_10) / 3,
            (pixel_x_01 + pixel_x_11 + pixel_x_10) / 3,
        ]
    )
    candidate_projector_pixel_y = np.stack(
        [
            (pixel_y_00 + pixel_y_01 + pixel_y_11) / 3,
            (pixel_y_00 + pixel_y_11 + pixel_y_10) / 3,
            (pixel_y_00 + pixel_y_01 + pixel_y_10) / 3,
            (pixel_y_01 + pixel_y_11 + pixel_y_10) / 3,
        ]
    )

    return ResolutionGrid(
        azimuth_vertices=azimuth_vertices,
        nominal_elevation_vertices=nominal_elevation_vertices,
        projector_pixel_x=projector_pixel_x,
        projector_pixel_y=projector_pixel_y,
        render_azimuth_pixels_per_degree=render_azimuth_resolution,
        render_nominal_elevation_pixels_per_degree=(
            render_elevation_resolution
        ),
        candidate_azimuth=candidate_azimuth,
        candidate_nominal_elevation=candidate_nominal_elevation,
        candidate_projector_pixel_x=candidate_projector_pixel_x,
        candidate_projector_pixel_y=candidate_projector_pixel_y,
        candidate_nominal_azimuth_pixels_per_degree=candidate_azimuth_resolution,
        candidate_nominal_elevation_pixels_per_degree=candidate_elevation_resolution,
        candidate_minimum_nominal_pixels_per_degree=candidate_minimum_resolution,
        candidate_ideal_nyquist_bound_cpd=(
            candidate_minimum_resolution / IDEAL_NYQUIST_PIXELS_PER_CYCLE
        ),
        candidate_pixels_per_evaluated_cycle=(candidate_minimum_resolution / evaluated_cpd),
    )


def build_candidate_table(resolution: ResolutionGrid) -> pd.DataFrame:
    cell_shape = resolution.candidate_azimuth.shape[1:]
    row, column = np.indices(cell_shape)
    candidate_shape = resolution.candidate_azimuth.shape
    values = {
        "cell_row": np.broadcast_to(row, candidate_shape),
        "cell_column": np.broadcast_to(column, candidate_shape),
        "candidate_triangle": np.broadcast_to(
            CANDIDATE_TRIANGLES[:, np.newaxis, np.newaxis],
            candidate_shape,
        ),
        "nominal_azimuth_degrees": resolution.candidate_azimuth,
        "nominal_elevation_degrees": resolution.candidate_nominal_elevation,
        "projector_pixel_x": resolution.candidate_projector_pixel_x,
        "projector_pixel_y": resolution.candidate_projector_pixel_y,
        "nominal_azimuth_pixels_per_degree": (resolution.candidate_nominal_azimuth_pixels_per_degree),
        "nominal_elevation_pixels_per_degree": (resolution.candidate_nominal_elevation_pixels_per_degree),
        "minimum_nominal_pixels_per_degree": (resolution.candidate_minimum_nominal_pixels_per_degree),
        "ideal_nyquist_bound_cpd": (resolution.candidate_ideal_nyquist_bound_cpd),
        "pixels_per_evaluated_cycle": (resolution.candidate_pixels_per_evaluated_cycle),
    }
    return pd.DataFrame({column_name: column_values.ravel() for column_name, column_values in values.items()})


def build_azimuth_summary(
    resolution: ResolutionGrid,
    requested_elevation: float,
) -> tuple[pd.DataFrame, float]:
    cell_azimuth = (resolution.azimuth_vertices[:-1] + resolution.azimuth_vertices[1:]) / 2
    cell_elevation = (
        resolution.nominal_elevation_vertices[:-1]
        + resolution.nominal_elevation_vertices[1:]
    ) / 2
    selected_row = int(np.argmin(np.abs(cell_elevation - requested_elevation)))
    selected_elevation = float(cell_elevation[selected_row])
    cell_azimuth_resolution = np.min(
        resolution.candidate_nominal_azimuth_pixels_per_degree,
        axis=0,
    )
    cell_elevation_resolution = np.min(
        resolution.candidate_nominal_elevation_pixels_per_degree,
        axis=0,
    )
    cell_minimum_resolution = np.min(
        resolution.candidate_minimum_nominal_pixels_per_degree,
        axis=0,
    )
    cell_nyquist_bound = np.min(
        resolution.candidate_ideal_nyquist_bound_cpd,
        axis=0,
    )
    cell_pixels_per_cycle = np.min(
        resolution.candidate_pixels_per_evaluated_cycle,
        axis=0,
    )
    summary = pd.DataFrame(
        {
            "nominal_azimuth_degrees": cell_azimuth,
            "selected_nominal_elevation_degrees": selected_elevation,
            "nominal_azimuth_pixels_per_degree": cell_azimuth_resolution[selected_row],
            "nominal_elevation_pixels_per_degree": cell_elevation_resolution[selected_row],
            "minimum_nominal_pixels_per_degree": cell_minimum_resolution[selected_row],
            "ideal_nyquist_bound_cpd": cell_nyquist_bound[selected_row],
            "pixels_per_evaluated_cycle": cell_pixels_per_cycle[selected_row],
            "minimum_across_nominal_elevations": np.min(
                cell_minimum_resolution,
                axis=0,
            ),
            "median_across_nominal_elevations": np.median(
                cell_minimum_resolution,
                axis=0,
            ),
        }
    )
    return summary, selected_elevation


def calculate_coverage(
    resolution: ResolutionGrid,
    width: int,
    height: int,
) -> dict[str, float | int]:
    projector_x = resolution.projector_pixel_x
    projector_y = resolution.projector_pixel_y
    x_00, x_01, x_11, x_10 = (
        projector_x[:-1, :-1],
        projector_x[:-1, 1:],
        projector_x[1:, 1:],
        projector_x[1:, :-1],
    )
    y_00, y_01, y_11, y_10 = (
        projector_y[:-1, :-1],
        projector_y[:-1, 1:],
        projector_y[1:, 1:],
        projector_y[1:, :-1],
    )

    def triangle_area(
        first_x: np.ndarray,
        first_y: np.ndarray,
        second_x: np.ndarray,
        second_y: np.ndarray,
        third_x: np.ndarray,
        third_y: np.ndarray,
    ) -> np.ndarray:
        return 0.5 * ((second_x - first_x) * (third_y - first_y) - (second_y - first_y) * (third_x - first_x))

    candidate_triangle_areas = np.stack(
        [
            triangle_area(x_00, y_00, x_01, y_01, x_11, y_11),
            triangle_area(x_00, y_00, x_11, y_11, x_10, y_10),
            triangle_area(x_00, y_00, x_01, y_01, x_10, y_10),
            triangle_area(x_01, y_01, x_11, y_11, x_10, y_10),
        ]
    )

    nonzero_areas = candidate_triangle_areas[np.abs(candidate_triangle_areas) > 1e-9]
    dominant_sign = 1 if np.sign(nonzero_areas).sum() >= 0 else -1
    reversed_triangles = int(
        np.count_nonzero(
            (np.abs(candidate_triangle_areas) > 1e-9) & (np.sign(candidate_triangle_areas) != dominant_sign)
        )
    )
    degenerate_triangles = int(np.count_nonzero(np.abs(candidate_triangle_areas) <= 1e-9))
    summed_mapped_area = float(np.abs(candidate_triangle_areas[:2]).sum())
    outside_frame = (projector_x < 0) | (projector_x > width) | (projector_y < 0) | (projector_y > height)

    return {
        "bounding_width_pixels": float(np.ptp(projector_x)),
        "bounding_height_pixels": float(np.ptp(projector_y)),
        "summed_mapped_area_pixels": summed_mapped_area,
        "summed_mapped_area_fraction": summed_mapped_area / (width * height),
        "cell_count": int(x_00.size),
        "candidate_triangle_count": int(candidate_triangle_areas.size),
        "reversed_candidate_triangle_count": reversed_triangles,
        "degenerate_candidate_triangle_count": degenerate_triangles,
        "outside_frame_vertex_count": int(np.count_nonzero(outside_frame)),
    }


def summary_statistics(resolution: ResolutionGrid) -> dict[str, dict[str, float]]:
    measures = {
        "nominal_azimuth_pixels_per_degree": np.min(
            resolution.candidate_nominal_azimuth_pixels_per_degree,
            axis=0,
        ),
        "nominal_elevation_pixels_per_degree": np.min(
            resolution.candidate_nominal_elevation_pixels_per_degree,
            axis=0,
        ),
        "minimum_nominal_pixels_per_degree": np.min(
            resolution.candidate_minimum_nominal_pixels_per_degree,
            axis=0,
        ),
        "ideal_nyquist_bound_cpd": np.min(
            resolution.candidate_ideal_nyquist_bound_cpd,
            axis=0,
        ),
        "pixels_per_evaluated_cycle": np.min(
            resolution.candidate_pixels_per_evaluated_cycle,
            axis=0,
        ),
    }
    return {
        name: {
            "minimum": float(np.min(values)),
            "median": float(np.median(values)),
            "maximum": float(np.max(values)),
        }
        for name, values in measures.items()
    }


def save_overview(
    path: Path,
    resolution: ResolutionGrid,
    width: int,
    height: int,
    evaluated_cpd: float,
) -> None:
    cell_minimum_resolution = np.min(
        resolution.candidate_minimum_nominal_pixels_per_degree,
        axis=0,
    )
    cell_ideal_nyquist_bound = np.min(
        resolution.candidate_ideal_nyquist_bound_cpd,
        axis=0,
    )
    cell_pixels_per_evaluated_cycle = np.min(
        resolution.candidate_pixels_per_evaluated_cycle,
        axis=0,
    )
    satisfies_nyquist_sampling = satisfies_ideal_nyquist_sampling(
        cell_pixels_per_evaluated_cycle
    )
    solid_angle_percentage = 100 * np.average(
        satisfies_nyquist_sampling,
        weights=nominal_solid_angle_weights(resolution),
    )

    figure, axes = plt.subplots(
        2,
        2,
        figsize=(15, 10),
        constrained_layout=True,
    )
    resolution_axis = axes[0, 0]
    frequency_axis = axes[0, 1]
    adequacy_axis = axes[1, 0]
    footprint_axis = axes[1, 1]

    resolution_image = resolution_axis.pcolormesh(
        resolution.azimuth_vertices,
        resolution.nominal_elevation_vertices,
        cell_minimum_resolution,
        shading="flat",
        cmap="viridis",
    )
    resolution_axis.set(
        title=(
            "Local digital resolution\n"
            "Minimum pixels per nominal degree"
        ),
        xlabel="Nominal azimuth (degrees)",
        ylabel="Nominal elevation (degrees)",
    )
    figure.colorbar(
        resolution_image,
        ax=resolution_axis,
        label="minimum pixels/nominal degree",
    )

    frequency_image = frequency_axis.pcolormesh(
        resolution.azimuth_vertices,
        resolution.nominal_elevation_vertices,
        cell_ideal_nyquist_bound,
        shading="flat",
        cmap="viridis",
    )
    frequency_axis.set(
        title=(
            "Ideal Nyquist boundary frequency\n"
            "Local resolution divided by two pixels/cycle"
        ),
        xlabel="Nominal azimuth (degrees)",
        ylabel="Nominal elevation (degrees)",
    )
    figure.colorbar(
        frequency_image,
        ax=frequency_axis,
        label="cycles/nominal degree",
    )

    adequacy_colours = ListedColormap(["#c83e35", "#238b57"])
    adequacy_scale = BoundaryNorm([-0.5, 0.5, 1.5], adequacy_colours.N)
    adequacy_image = adequacy_axis.pcolormesh(
        resolution.azimuth_vertices,
        resolution.nominal_elevation_vertices,
        satisfies_nyquist_sampling.astype(int),
        shading="flat",
        cmap=adequacy_colours,
        norm=adequacy_scale,
    )
    adequacy_axis.set(
        title=(
            f"Nyquist check at {evaluated_cpd:g} cpd\n"
            f"{solid_angle_percentage:.1f}% of dome surface satisfies it"
        ),
        xlabel="Nominal azimuth (degrees)",
        ylabel="Nominal elevation (degrees)",
    )
    adequacy_colourbar = figure.colorbar(
        adequacy_image,
        ax=adequacy_axis,
        ticks=[0, 1],
        shrink=0.85,
    )
    adequacy_colourbar.ax.set_yticklabels(
        [
            "Does not satisfy",
            "Satisfies",
        ]
    )

    footprint_axis.plot(
        resolution.projector_pixel_x.T,
        resolution.projector_pixel_y.T,
        color="#2a6f97",
        linewidth=0.5,
    )
    footprint_axis.plot(
        resolution.projector_pixel_x,
        resolution.projector_pixel_y,
        color="#2a6f97",
        linewidth=0.5,
    )
    footprint_axis.set(
        title=f"Dome footprint in the {width} × {height} projector output",
        xlabel="Projector X (pixels)",
        ylabel="Projector Y (pixels)",
        xlim=(0, width),
        ylim=(height, 0),
        aspect="equal",
    )

    figure.suptitle("BonVision dome digital resolution", fontsize=16)
    figure.savefig(path, dpi=180)
    plt.close(figure)


def plot_dome_surface(
    axis: plt.Axes,
    resolution: ResolutionGrid,
    face_colours: np.ndarray,
    title: str,
    camera_azimuth: float,
) -> None:
    azimuth_grid, elevation_grid = np.meshgrid(
        np.deg2rad(resolution.azimuth_vertices),
        np.deg2rad(resolution.nominal_elevation_vertices),
    )
    dome_left_right = np.cos(elevation_grid) * np.sin(azimuth_grid)
    dome_front_back = np.cos(elevation_grid) * np.cos(azimuth_grid)
    dome_up_down = np.sin(elevation_grid)
    axis.plot_surface(
        dome_left_right,
        dome_front_back,
        dome_up_down,
        facecolors=face_colours,
        rstride=1,
        cstride=1,
        linewidth=0,
        antialiased=True,
        shade=False,
    )
    axis.plot_wireframe(
        dome_left_right,
        dome_front_back,
        dome_up_down,
        rstride=3,
        cstride=3,
        color="#333333",
        linewidth=0.25,
        alpha=0.25,
    )
    axis.scatter([0], [0], [0], color="#222222", s=12, depthshade=False)
    axis.text(0, 0, -0.08, "mouse", ha="center", va="top", fontsize=8)
    axis.set(
        title=title,
        xlim=(-1.05, 1.05),
        ylim=(-1.05, 1.05),
        zlim=(-0.5, 1.05),
    )
    axis.set_box_aspect((1, 1, 0.8), zoom=1.25)
    axis.set_proj_type("ortho")
    axis.view_init(elev=24, azim=camera_azimuth)
    axis.set_axis_off()


def save_dome_views(
    path: Path,
    resolution: ResolutionGrid,
    evaluated_cpd: float,
) -> None:
    cell_minimum_resolution = np.min(
        resolution.candidate_minimum_nominal_pixels_per_degree,
        axis=0,
    )
    cell_pixels_per_evaluated_cycle = np.min(
        resolution.candidate_pixels_per_evaluated_cycle,
        axis=0,
    )
    satisfies_nyquist_sampling = satisfies_ideal_nyquist_sampling(
        cell_pixels_per_evaluated_cycle
    )

    resolution_colour_scale = Normalize(
        vmin=float(np.min(cell_minimum_resolution)),
        vmax=float(np.max(cell_minimum_resolution)),
    )
    resolution_face_colours = plt.colormaps["viridis"](
        resolution_colour_scale(cell_minimum_resolution)
    )
    adequacy_colours = ListedColormap(["#c83e35", "#238b57"])
    adequacy_scale = BoundaryNorm([-0.5, 0.5, 1.5], adequacy_colours.N)
    adequacy_face_colours = adequacy_colours(
        adequacy_scale(satisfies_nyquist_sampling.astype(int))
    )

    figure, axes = plt.subplots(
        2,
        2,
        figsize=(12, 10),
        constrained_layout=True,
        subplot_kw={"projection": "3d"},
    )
    views = [
        (axes[0, 0], resolution_face_colours, "Local resolution — rear view", -55),
        (axes[0, 1], resolution_face_colours, "Local resolution — front view", 125),
        (
            axes[1, 0],
            adequacy_face_colours,
            f"{evaluated_cpd:g} cpd Nyquist check — rear view",
            -55,
        ),
        (
            axes[1, 1],
            adequacy_face_colours,
            f"{evaluated_cpd:g} cpd Nyquist check — front view",
            125,
        ),
    ]
    for axis, face_colours, title, camera_azimuth in views:
        plot_dome_surface(
            axis,
            resolution,
            face_colours,
            title,
            camera_azimuth,
        )

    resolution_colourbar = figure.colorbar(
        plt.cm.ScalarMappable(
            norm=resolution_colour_scale,
            cmap="viridis",
        ),
        ax=axes[0, :],
        label="minimum pixels/nominal degree",
        shrink=0.7,
        pad=0.02,
    )
    resolution_colourbar.ax.tick_params(labelsize=8)
    adequacy_colourbar = figure.colorbar(
        plt.cm.ScalarMappable(
            norm=adequacy_scale,
            cmap=adequacy_colours,
        ),
        ax=axes[1, :],
        ticks=[0, 1],
        shrink=0.7,
        pad=0.02,
    )
    adequacy_colourbar.ax.set_yticklabels(
        [
            "Does not satisfy",
            "Satisfies",
        ]
    )

    figure.suptitle("Resolution mapped onto a schematic dome", fontsize=16)
    figure.savefig(path, dpi=180)
    plt.close(figure)


def save_directional_diagnostics(
    path: Path,
    resolution: ResolutionGrid,
    width: int,
    height: int,
) -> None:
    figure, axes = plt.subplots(1, 3, figsize=(16, 5.5), constrained_layout=True)

    axes[0].plot(
        resolution.projector_pixel_x.T,
        resolution.projector_pixel_y.T,
        color="#2a6f97",
        linewidth=0.5,
    )
    axes[0].plot(
        resolution.projector_pixel_x,
        resolution.projector_pixel_y,
        color="#2a6f97",
        linewidth=0.5,
    )
    axes[0].set(
        title="Mesh footprint",
        xlabel="Projector X (pixels)",
        ylabel="Projector Y (pixels)",
        xlim=(0, width),
        ylim=(height, 0),
        aspect="equal",
    )

    heatmaps = [
        (
            axes[1],
            np.min(
                resolution.candidate_nominal_azimuth_pixels_per_degree,
                axis=0,
            ),
            "Azimuth-coordinate sampling",
        ),
        (
            axes[2],
            np.min(
                resolution.candidate_nominal_elevation_pixels_per_degree,
                axis=0,
            ),
            "Elevation-coordinate sampling",
        ),
    ]
    for axis, values, title in heatmaps:
        image = axis.pcolormesh(
            resolution.azimuth_vertices,
            resolution.nominal_elevation_vertices,
            values,
            shading="flat",
            cmap="viridis",
        )
        axis.set(
            title=title,
            xlabel="Nominal azimuth (degrees)",
            ylabel="Nominal elevation (degrees)",
        )
        figure.colorbar(
            image,
            ax=axis,
            label="pixels/nominal degree",
        )

    figure.suptitle("Directional mesh-map diagnostics", fontsize=16)
    figure.savefig(path, dpi=180)
    plt.close(figure)


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as input_file:
        for block in iter(lambda: input_file.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def run_analysis(arguments: Sequence[str] | None = None) -> int:
    settings = parse_arguments(arguments)
    input_path = settings.mesh_map.expanduser().resolve()
    if not input_path.is_file():
        raise FileNotFoundError(f"Mesh-map file not found: {input_path}")

    mesh_table, selected_sheet = read_mesh_map(input_path, settings.sheet)
    mesh = build_mesh_grid(mesh_table)
    resolution = calculate_resolution(
        mesh,
        settings.width,
        settings.height,
        (settings.azimuth_min, settings.azimuth_max),
        (settings.elevation_min, settings.elevation_max),
        settings.evaluated_cpd,
    )
    candidate_table = build_candidate_table(resolution)
    azimuth_table, selected_elevation = build_azimuth_summary(
        resolution,
        settings.summary_elevation,
    )
    coverage = calculate_coverage(resolution, settings.width, settings.height)
    if coverage["degenerate_candidate_triangle_count"]:
        raise ValueError(
            f"{coverage['degenerate_candidate_triangle_count']} degenerate candidate mesh triangles detected."
        )
    if coverage["reversed_candidate_triangle_count"]:
        raise ValueError(
            f"{coverage['reversed_candidate_triangle_count']} "
            "reversed candidate mesh triangles detected."
        )
    statistics = summary_statistics(resolution)
    cell_pixels_per_evaluated_cycle = np.min(
        resolution.candidate_pixels_per_evaluated_cycle,
        axis=0,
    )
    satisfies_nyquist_sampling = satisfies_ideal_nyquist_sampling(
        cell_pixels_per_evaluated_cycle
    )
    cells_satisfying_count = int(
        np.count_nonzero(satisfies_nyquist_sampling)
    )
    total_cell_count = int(cell_pixels_per_evaluated_cycle.size)
    nominal_solid_angle_satisfying_fraction = float(
        np.average(
            satisfies_nyquist_sampling,
            weights=nominal_solid_angle_weights(resolution),
        )
    )

    output_directory = (
        settings.output_dir.expanduser().resolve()
        if settings.output_dir
        else input_path.with_name(f"{input_path.stem}_resolution_analysis")
    )
    output_directory.mkdir(parents=True, exist_ok=True)

    output_stem = input_path.stem
    candidate_table_path = output_directory / f"{output_stem}_resolution_candidates.csv"
    azimuth_table_path = output_directory / f"{output_stem}_azimuth_summary.csv"
    overview_path = output_directory / f"{output_stem}_overview.png"
    dome_views_path = output_directory / f"{output_stem}_dome_views.png"
    directional_diagnostics_path = (
        output_directory / f"{output_stem}_directional_diagnostics.png"
    )
    metadata_path = output_directory / f"{output_stem}_analysis.json"

    candidate_table.to_csv(candidate_table_path, index=False)
    azimuth_table.to_csv(azimuth_table_path, index=False)
    save_overview(
        overview_path,
        resolution,
        settings.width,
        settings.height,
        settings.evaluated_cpd,
    )
    save_dome_views(
        dome_views_path,
        resolution,
        settings.evaluated_cpd,
    )
    save_directional_diagnostics(
        directional_diagnostics_path,
        resolution,
        settings.width,
        settings.height,
    )

    warnings = [
        (
            "Values are per BonVision orthographic coordinate degree, "
            "not spherical retinal visual-angle degree."
        ),
        (
            "Nominal solid-angle percentages assume the orthographic "
            "coordinates describe longitude and latitude on a sphere."
        ),
        (
            "The ideal Nyquist boundary is not a practical quality target; "
            "optical blur and contrast loss require a camera-based measurement."
        ),
        (
            "Both possible GL_QUADS diagonals are evaluated and the most "
            "conservative result is used for each cell."
        ),
        "Interpolating the captured grid does not add independent measurements.",
        (
            "Elevation remains nominal because the final mesh does not retain "
            "the uneven captured elevation levels."
        ),
        ("Summed mapped area is not unique framebuffer coverage if mesh cells overlap."),
        (
            f"Confirm {settings.width} × {settings.height} projector output "
            "with no Windows or projector scaling."
        ),
    ]
    if coverage["outside_frame_vertex_count"]:
        warnings.append(
            f"{coverage['outside_frame_vertex_count']} mesh vertices are outside the projector framebuffer."
        )

    metadata = {
        "analysis_version": ANALYSIS_VERSION,
        "analysis_method": (
            "Affine Jacobian for all four candidate triangles per mesh cell; "
            "cell summaries use the conservative minimum across both diagonals."
        ),
        "created_utc": datetime.now(UTC).isoformat(),
        "input_file": str(input_path),
        "input_sha256": file_sha256(input_path),
        "script_sha256": file_sha256(Path(__file__).resolve()),
        "input_sheet": selected_sheet,
        "software_versions": {
            "python": platform.python_version(),
            "numpy": np.__version__,
            "pandas": pd.__version__,
            "matplotlib": version("matplotlib"),
            "openpyxl": version("openpyxl"),
        },
        "grid_columns": int(mesh.projector_x.shape[1]),
        "grid_rows": int(mesh.projector_x.shape[0]),
        "projector_width": settings.width,
        "projector_height": settings.height,
        "azimuth_range": [settings.azimuth_min, settings.azimuth_max],
        "elevation_range": [settings.elevation_min, settings.elevation_max],
        "view_range_source": (
            "src/main.bonsai and src/VisualResolutionCheck.bonsai "
            "OrthographicView settings"
        ),
        "evaluated_spatial_frequency_cpd": settings.evaluated_cpd,
        "requested_nominal_summary_elevation": settings.summary_elevation,
        "selected_nominal_summary_elevation": selected_elevation,
        "coverage": coverage,
        "statistics": statistics,
        "ideal_nyquist_check": {
            "boundary_pixels_per_cycle": IDEAL_NYQUIST_PIXELS_PER_CYCLE,
            "boundary_pixels_per_nominal_degree": (
                IDEAL_NYQUIST_PIXELS_PER_CYCLE * settings.evaluated_cpd
            ),
            "mesh_cells_satisfying_count": cells_satisfying_count,
            "total_cell_count": total_cell_count,
            "mesh_cells_satisfying_fraction": (
                cells_satisfying_count / total_cell_count
            ),
            "nominal_solid_angle_satisfying_fraction": (
                nominal_solid_angle_satisfying_fraction
            ),
        },
        "warnings": warnings,
        "outputs": {
            "overview": overview_path.name,
            "dome_views": dome_views_path.name,
            "directional_diagnostics": directional_diagnostics_path.name,
            "resolution_candidates": candidate_table_path.name,
            "azimuth_summary": azimuth_table_path.name,
        },
    }
    metadata_path.write_text(json.dumps(metadata, indent=2), encoding="utf-8")

    conservative = statistics["minimum_nominal_pixels_per_degree"]
    print("Mesh-map digital resolution analysis")
    print(f"Input: {input_path}")
    print(f"Grid: {metadata['grid_columns']} × {metadata['grid_rows']}")
    print(
        "Conservative pixels/nominal degree, minimum / median / maximum: "
        f"{conservative['minimum']:.3f} / "
        f"{conservative['median']:.3f} / "
        f"{conservative['maximum']:.3f}"
    )
    print(
        "Reversed / degenerate candidate triangles: "
        f"{coverage['reversed_candidate_triangle_count']} / "
        f"{coverage['degenerate_candidate_triangle_count']}"
    )
    print(f"Overview: {overview_path}")
    print(f"Results by azimuth: {azimuth_table_path}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(run_analysis())
    except (FileNotFoundError, ValueError, ImportError, zipfile.BadZipFile) as error:
        print(f"Error: {error}", file=sys.stderr)
        raise SystemExit(2) from error
