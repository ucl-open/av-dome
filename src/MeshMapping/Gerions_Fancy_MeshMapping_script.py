from scipy.interpolate import make_interp_spline
import numpy as np
import pandas as pd
import matplotlib.pyplot as plt
# from scipy.interpolate import interp1d
import os
from pathlib import Path


## Tomaso Muzzu - UCL - 09/03/2021
#  edits by EH (March 2021).
#  edits by Gerion Nabbefeld (2025) and translated to python.


# ── Variable assignment ────────────────────────────────────────────────────────
input_file_name = Path(r'C:\Users\CoenLabUsers\Documents\GitHub\dev-sandbox\Workflows\Examples\MeshMappingNew\BonVision_MeshMap2026-04-23T17_12_42.csv')

# angular_span = [270, 111]  # [azimuth_deg, elevation_deg]

n_interp_cols = 30
n_interp_rows = 30  # Currently it HAS to be equal!

save_folder = os.path.dirname(input_file_name)
fileBase = os.path.basename(input_file_name)
output_file_name = os.path.join(
    save_folder,
    f"{fileBase}_MeshMap_AzRes_{n_interp_cols}_ElRes_{n_interp_rows}_Gerions_fancy_interpolation_python.csv"
)

plot_flag = True


# # ── Helper: interpc (MATLAB-style closed-curve interpolation) ──────────────────
def interpc(Q: np.ndarray, Ns: int = 100, method: str = 'makima', overwrite_elevations: np.ndarray = None) -> np.ndarray:
    """
    Interpolation of a 2D or 3D curve Q with simple uniform parameterisation.
    Port of interpc.m by John S. Kearns.

    Parameters
    ----------
    Q      : (Nq, 2) or (Nq, 3) array — input curve
    Ns     : number of output points (default 100)
    method : interpolation method (default 'makima')
                'makima'  → Modified Akima (scipy make_interp_spline with akima)
                'linear'  → piecewise linear
                'cubic'   → cubic spline
                'pchip'   → PCHIP monotone cubic
    """
    from scipy.interpolate import PchipInterpolator, Akima1DInterpolator

    q = np.linspace(0, 1, len(Q))   # uniform parameter for source points
    s = np.linspace(0, 1, Ns)       # uniform parameter for target points

    if overwrite_elevations is not None:
        # Overwrite the parameter values based on the provided elevation values
        # Assuming overwrite_elevations are in the same order as Q's rows
        q = (np.array(overwrite_elevations) - np.min(overwrite_elevations)) / (np.max(overwrite_elevations) - np.min(overwrite_elevations))  # Normalize to [0, 1]                         
    #

    def _interp1(q, vals, s, method):
        if method == 'makima':
            return Akima1DInterpolator(q, vals, method='makima')(s)
        elif method == 'pchip':
            return PchipInterpolator(q, vals)(s)
        elif method == 'linear':
            return np.interp(s, q, vals)
        elif method == 'cubic':
            from scipy.interpolate import CubicSpline
            return CubicSpline(q, vals)(s)
        else:
            raise ValueError(f"Unknown method: {method}")

    sx = _interp1(q, Q[:, 0], s, method)
    sy = _interp1(q, Q[:, 1], s, method)
    S = np.column_stack([sx, sy])

    if Q.shape[1] == 3:
        sz = _interp1(q, Q[:, 2], s, method)
        S = np.column_stack([S, sz])

    return S
#



# ── Import CSV ─────────────────────────────────────────────────────────────────
def import_file(filename: str) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    data = pd.read_csv(filename, header=None).values
    x_pix  = data[:, 0]
    y_pix  = data[:, 1]
    az_deg = data[:, 2]
    el_deg = data[:, 3]
    return x_pix, y_pix, az_deg, el_deg
#

x_pix, y_pix, az_deg, el_deg = import_file(input_file_name)

# Overwrite elevation values
overwrite_elevations = np.array([-22.5, 0, 15, 30, 60, 82.5])  # Elecation points of calibration are spaced unevenly
el_deg_backup = el_deg.copy()
referenceElevations = np.sort(np.unique(el_deg))[::-1]
for i in range(referenceElevations.size):
    el_deg[el_deg_backup == referenceElevations[i]] = overwrite_elevations[i]
#

# Realized later that the elevation values are not used for interpolation. This script assumes your samples are equal increments of elevation...
# Just for testing, I'm now only using the main levels...
if 0:  # just for testing, but actually looked more skewed in the end:/
    # Only keep the main levels (-22.5, 30, 82.5) for testing
    mask_ids = np.invert((el_deg == 0) | (el_deg == 15) | (el_deg == 60))
    x_pix = x_pix[mask_ids]
    y_pix = y_pix[mask_ids]
    az_deg = az_deg[mask_ids]
    el_deg = el_deg[mask_ids]
#

# ── Build MeshMapping ──────────────────────────────────────────────────────────
mesh_mapping = np.column_stack([
    x_pix, y_pix, az_deg, el_deg,
    np.ones(len(x_pix))
])
print(mesh_mapping)

# Normalise coordinates
mesh_mapping[:, 2] /= mesh_mapping[:, 2].max()
mesh_mapping[:, 3] /= mesh_mapping[:, 3].max()

if plot_flag:
    fig1, axes = plt.subplots(1, 2, figsize=(12, 5))
    axes[0].plot(mesh_mapping[:, 0], mesh_mapping[:, 1], 'or')
    axes[0].set_title('Original mesh mapping file')

# ── Interpolate azimuth rings ──────────────────────────────────────────────────
n_rows = len(np.unique(mesh_mapping[:, 3]))
n_cols = len(np.unique(mesh_mapping[:, 2]))

interp_rows = np.full((n_rows, n_interp_cols, 2), np.nan)
for i_row in range(n_rows):
    start = i_row * n_cols
    temp_ids = slice(start, start + n_cols)

    if plot_flag:
        plt.figure(2)
        plt.plot(mesh_mapping[temp_ids, 0], mesh_mapping[temp_ids, 1], 'ok')
    #

    pts = np.column_stack([mesh_mapping[temp_ids, 0], mesh_mapping[temp_ids, 1]])
    interp_rows[i_row] = interpc(pts, n_interp_cols)

# ── Interpolate between rows along elevation ───────────────────────────────────
interpolated_mesh = np.full((n_interp_rows, n_interp_cols, 2), np.nan)
for i_col in range(n_interp_cols):
    pts = interp_rows[:, i_col, :]          # shape (n_rows, 2)
    # interpolated_mesh[:, i_col, :] = interpc(pts, n_interp_rows, overwrite_elevations= -1* overwrite_elevations)
    interpolated_mesh[:, i_col, :] = interpc(pts, n_interp_rows)
#

if 0:  # recorded trop to bottom?
    interpolated_mesh = interpolated_mesh[::-1, :, :]  # Reverse the rows
#

# Fix outliers
interpolated_mesh = np.clip(interpolated_mesh, -1, 1)

# Plot grid
if plot_flag:
    plt.figure(2)
    for i_col in range(n_interp_cols):
        plt.plot(interpolated_mesh[:, i_col, 0], interpolated_mesh[:, i_col, 1])
    for i_row in range(n_interp_rows):
        plt.plot(interpolated_mesh[i_row, :, 0], interpolated_mesh[i_row, :, 1])

    plt.axis('equal')
    plt.xlim([-1, 1])
    plt.ylim([-1, 1])
    plt.show()
#

# ── Assemble output table ──────────────────────────────────────────────────────
az_range = np.linspace(0, 1, n_interp_cols)
el_range = np.linspace(0, 1, n_interp_rows)
new_deg_az, new_deg_el = np.meshgrid(az_range, el_range)

pos_x = interpolated_mesh[:, :, 0].ravel()
pos_y = interpolated_mesh[:, :, 1].ravel()
intensity = np.ones_like(pos_y)

data_cell = np.column_stack([pos_x, -pos_y, new_deg_az.ravel(), new_deg_el.ravel(), intensity])

# Sort by azimuth (column 3, index 2) — Row-major: sort by elevation (column 4, index 3)
mesh_norm_interp = data_cell[np.argsort(data_cell[:, 2], kind='stable')]  # sort by az first
mesh_norm_interp = mesh_norm_interp[np.argsort(mesh_norm_interp[:, 3], kind='stable')]  # then el

if plot_flag:
    axes[1].plot(mesh_mapping[:, 0], mesh_mapping[:, 1], 'ro', label='original data')
    axes[1].plot(mesh_norm_interp[:, 0], -mesh_norm_interp[:, 1], 'k.', label='interpolated data')
    axes[1].legend()
    axes[1].set_title('Interpolated mesh map file')
    plt.tight_layout()
    plt.show()

# ── Save output ────────────────────────────────────────────────────────────────
np.savetxt(output_file_name, mesh_norm_interp, delimiter=',', fmt="%0.16f")
print(f"Saved to: {output_file_name}")
