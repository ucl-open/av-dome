from scipy.interpolate import make_interp_spline
import numpy as np
import pandas as pd
import matplotlib
matplotlib.use('QtAgg')  # required for fullscreen-on-second-display in the manual-tweak GUI
import matplotlib.pyplot as plt
# from scipy.interpolate import interp1d
import os
from pathlib import Path


## Tomaso Muzzu - UCL - 09/03/2021
#  edits by EH (March 2021).
#  edits by Gerion Nabbefeld (2025) and translated to python.


# ── Variable assignment ────────────────────────────────────────────────────────
input_file_name = Path(r'src/MeshMapping/BonVision_MeshMap2026-07-20T11_01_05.csv')

# angular_span = [270, 111]  # [azimuth_deg, elevation_deg]

n_interp_cols = 30
n_interp_rows = 30  # Currently it HAS to be equal!

# Manual-tweak step (between capture and fine interp)
manual_tweak       = False       # enable interactive drag-edit of a coarse mesh
coarse_factor      = 1          # midpoints inserted between captured nodes per dimension
fullscreen_second  = True       # GUI fullscreen on a chosen display vs windowed on primary
gui_screen_index   = 0          # which monitor to put the GUI on (0=primary, 1=secondary, ...)
save_tweaked_csv   = True       # write intermediate CSV after the GUI step
display_resolution = (1920, 1080)  # calibration-display resolution; sets GUI aspect ratio

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


# ── Coarse interpolation ───────────────────────────────────────────────────────
def coarse_interpolate(mesh_xy: np.ndarray, n_rows: int, n_cols: int,
                       n_coarse_rows: int, n_coarse_cols: int) -> np.ndarray:
    """
    Run the same two-pass Akima interpolation used for the fine stage, but at a
    coarser resolution, so the coarse mesh is consistent in character with the
    final 30x30 output.

    mesh_xy : (n_rows * n_cols, 2) array of (x, y) for the captured grid,
              ordered row-major (rows = elevation steps, cols = azimuth steps).
    Returns : (n_coarse_rows, n_coarse_cols, 2)
    """
    interp_rows = np.full((n_rows, n_coarse_cols, 2), np.nan)
    for i_row in range(n_rows):
        s = slice(i_row * n_cols, (i_row + 1) * n_cols)
        interp_rows[i_row] = interpc(mesh_xy[s], n_coarse_cols)

    coarse = np.full((n_coarse_rows, n_coarse_cols, 2), np.nan)
    for i_col in range(n_coarse_cols):
        coarse[:, i_col, :] = interpc(interp_rows[:, i_col, :], n_coarse_rows)

    return np.clip(coarse, -1, 1)
#


# ── Manual-tweak GUI ───────────────────────────────────────────────────────────
def run_manual_tweak_gui(coarse_mesh: np.ndarray, aspect: float = 1920 / 1080,
                         fullscreen_second: bool = True,
                         screen_index: int = 1) -> np.ndarray:
    """
    Open a fullscreen black window showing the coarse mesh as dots connected by
    row/column lines, with a Bonsai-style red selection cursor on one dot.

    Two keyboard modes (toggle with Enter or Space):
      NAV     arrow keys move the red selection between dots in the grid
      ADJUST  arrow keys move the SELECTED dot's position
              Shift+arrow = large step (matches the Bonsai capture workflow)

    Mouse drag still works in either mode and also re-targets the selection.

    Keys:
      arrows        move (selection in NAV; dot in ADJUST)
      Shift+arrows  big-step move in ADJUST mode
      Enter / Space toggle NAV <-> ADJUST
      Tab / Shift+Tab  next / previous dot (NAV alternative)
      's'           save and close
      'r'           reset to the input mesh
      'esc'         cancel (raises RuntimeError so the caller can abort cleanly)

    Coordinates: the CSV stores X in pre-aspect-ratio normalized [-1, 1] (the
    Bonsai workflow multiplies X by aspect before rendering). To make the GUI
    WYSIWYG with the actual calibration display we plot at (x * aspect, y)
    over an axes range of [-aspect, +aspect] x [-1, 1], then divide X back by
    aspect on return.

    NOTE: callers MUST not have any other matplotlib figures open when invoking
    this — the manual event loop polls until this figure closes, and stray
    figures from earlier in the script will keep blocking after 's'.
    """
    from matplotlib.lines import Line2D

    # Matplotlib's default keymap binds 's' to "save figure" (which opens a
    # file dialog hidden behind the fullscreen window), 'r' to "home", 'f' to
    # "fullscreen toggle", etc. Disable the ones that collide with our scheme
    # so 's' doesn't pop a Save-Figure dialog instead of quitting the GUI.
    _saved_keymaps = {
        k: list(plt.rcParams[k]) for k in (
            'keymap.save', 'keymap.home', 'keymap.fullscreen',
            'keymap.quit', 'keymap.back', 'keymap.forward',
            'keymap.pan', 'keymap.zoom', 'keymap.grid', 'keymap.grid_minor',
            'keymap.xscale', 'keymap.yscale',
        )
    }
    for k in _saved_keymaps:
        plt.rcParams[k] = []

    initial = coarse_mesh.copy()
    work = coarse_mesh.copy()
    n_rows, n_cols, _ = work.shape

    fig = plt.figure(facecolor='black')
    ax = fig.add_subplot(111)
    ax.set_facecolor('black')
    ax.set_xlim(-aspect, aspect)
    ax.set_ylim(-1, 1)
    ax.set_aspect('equal')
    ax.set_axis_off()
    fig.subplots_adjust(0, 0, 1, 1)

    def display_xy(mesh):
        return mesh[..., 0] * aspect, mesh[..., 1]

    dx, dy = display_xy(work)
    row_lines = [Line2D(dx[r, :], dy[r, :], color=(0.4, 0.4, 0.4), lw=0.7)
                 for r in range(n_rows)]
    col_lines = [Line2D(dx[:, c], dy[:, c], color=(0.4, 0.4, 0.4), lw=0.7)
                 for c in range(n_cols)]
    for ln in row_lines + col_lines:
        ax.add_line(ln)

    flat_x = dx.ravel()
    flat_y = dy.ravel()
    scat = ax.scatter(flat_x, flat_y, c='white', s=18, zorder=3)

    # Bonsai-style crosshair on the currently selected dot. Colour signals
    # mode: green = NAV (arrows move selection), red = ADJUST (arrows move dot).
    sel_marker = ax.scatter([0], [0], color=(0.2, 1.0, 0.2), s=160, marker='+',
                            linewidths=2, zorder=5)

    mode_text = ax.text(0.01, 0.985, '', transform=ax.transAxes,
                        color=(0.2, 1.0, 0.2), va='top', ha='left',
                        family='monospace', fontsize=10)

    state = {'drag_idx': None, 'saved': False, 'cancelled': False,
             'sel_r': 0, 'sel_c': 0, 'mode': 'NAV'}
    pick_tol = 0.05    # axes units, for mouse picking
    small_step = 0.005  # matches Bonsai unshifted step
    big_step = 0.05     # matches Bonsai shifted step

    def update_selection_marker():
        ddx, ddy = display_xy(work)
        x = ddx[state['sel_r'], state['sel_c']]
        y = ddy[state['sel_r'], state['sel_c']]
        sel_marker.set_offsets([[x, y]])
        mode_color = (0.2, 1.0, 0.2) if state['mode'] == 'NAV' else 'red'
        sel_marker.set_color(mode_color)
        mode_text.set_color(mode_color)
        mode_text.set_text(
            f"[{state['mode']}]  dot ({state['sel_r']:>2},{state['sel_c']:>2})  "
            f"arrows: {'select' if state['mode']=='NAV' else 'move dot (Shift=big)'}  |  "
            f"Enter/Space: toggle  |  Tab: next  |  s: save  |  r: reset  |  esc: cancel"
        )

    def redraw_lines():
        ddx, ddy = display_xy(work)
        for r in range(n_rows):
            row_lines[r].set_data(ddx[r, :], ddy[r, :])
        for c in range(n_cols):
            col_lines[c].set_data(ddx[:, c], ddy[:, c])
        scat.set_offsets(np.column_stack([ddx.ravel(), ddy.ravel()]))
        update_selection_marker()
        fig.canvas.draw_idle()

    def move_selection(dr, dc):
        state['sel_r'] = int(np.clip(state['sel_r'] + dr, 0, n_rows - 1))
        state['sel_c'] = int(np.clip(state['sel_c'] + dc, 0, n_cols - 1))
        update_selection_marker()
        fig.canvas.draw_idle()

    def move_selected_dot(d_mesh_x, d_mesh_y):
        r, c = state['sel_r'], state['sel_c']
        work[r, c, 0] = np.clip(work[r, c, 0] + d_mesh_x, -1, 1)
        work[r, c, 1] = np.clip(work[r, c, 1] + d_mesh_y, -1, 1)
        redraw_lines()

    def on_press(event):
        if event.button != 1:
            return
        if event.inaxes != ax or event.xdata is None:
            return
        ddx, ddy = display_xy(work)
        d = np.hypot(ddx.ravel() - event.xdata, ddy.ravel() - event.ydata)
        idx = int(np.argmin(d))
        if d[idx] < pick_tol:
            state['drag_idx'] = idx
            state['sel_r'], state['sel_c'] = divmod(idx, n_cols)
            update_selection_marker()
            fig.canvas.draw_idle()

    def on_motion(event):
        if state['drag_idx'] is None or event.inaxes != ax:
            return
        if event.xdata is None or event.ydata is None:
            return
        i = state['drag_idx']
        r, c = divmod(i, n_cols)
        work[r, c, 0] = np.clip(event.xdata / aspect, -1, 1)
        work[r, c, 1] = np.clip(event.ydata, -1, 1)
        redraw_lines()

    def on_release(_event):
        state['drag_idx'] = None

    def on_key(event):
        k = event.key or ''
        # Strip 'shift+' prefix so we can detect the modifier easily.
        shift = k.startswith('shift+')
        bare = k[len('shift+'):] if shift else k

        if bare == 's':
            state['saved'] = True
            return
        if bare == 'r':
            work[...] = initial
            redraw_lines()
            return
        if bare == 'escape':
            state['cancelled'] = True
            return
        if bare in ('enter', ' ', 'space'):
            state['mode'] = 'ADJUST' if state['mode'] == 'NAV' else 'NAV'
            update_selection_marker()
            fig.canvas.draw_idle()
            return
        if bare == 'tab':
            i = state['sel_r'] * n_cols + state['sel_c']
            i = (i - 1) if shift else (i + 1)
            i %= (n_rows * n_cols)
            state['sel_r'], state['sel_c'] = divmod(i, n_cols)
            update_selection_marker()
            fig.canvas.draw_idle()
            return
        if bare in ('left', 'right', 'up', 'down'):
            if state['mode'] == 'NAV':
                if   bare == 'left':  move_selection(0, -1)
                elif bare == 'right': move_selection(0, +1)
                elif bare == 'up':    move_selection(-1, 0)
                elif bare == 'down':  move_selection(+1, 0)
            else:
                step = big_step if shift else small_step
                if   bare == 'left':  move_selected_dot(-step, 0)
                elif bare == 'right': move_selected_dot(+step, 0)
                elif bare == 'up':    move_selected_dot(0, +step)
                elif bare == 'down':  move_selected_dot(0, -step)
            return

    def on_close(_event):
        if not state['saved']:
            state['cancelled'] = True

    fig.canvas.mpl_connect('button_press_event', on_press)
    fig.canvas.mpl_connect('motion_notify_event', on_motion)
    fig.canvas.mpl_connect('button_release_event', on_release)
    fig.canvas.mpl_connect('key_press_event', on_key)
    fig.canvas.mpl_connect('close_event', on_close)

    update_selection_marker()

    try:
        win = plt.get_current_fig_manager().window
        if fullscreen_second:
            # Use the same Qt binding matplotlib's QtAgg backend already loaded;
            # importing PyQt6 directly when matplotlib chose PyQt5 (or vice
            # versa) gives a QGuiApplication from a different Qt with no app
            # instance, so .screens() returns nothing usable.
            from matplotlib.backends.qt_compat import QtGui as _qt_gui
            screens = _qt_gui.QGuiApplication.screens()
            if not screens:
                print("[manual_tweak] No screens reported by Qt — showing maximized on primary.")
                win.showMaximized()
            else:
                idx = screen_index if 0 <= screen_index < len(screens) else 0
                if idx != screen_index:
                    print(f"[manual_tweak] screen_index={screen_index} out of range "
                          f"({len(screens)} display(s) detected); falling back to {idx}.")
                geom = screens[idx].geometry()
                win.move(geom.x(), geom.y())
                win.showFullScreen()
        else:
            win.showMaximized()
        # Without explicit raise/activate, fullscreen on a second display may
        # never receive keyboard focus, so 's' silently does nothing.
        win.raise_()
        win.activateWindow()
    except Exception as e:
        print(f"[manual_tweak] Window placement failed: {e}")

    print("Manual tweak: NAV mode = arrows select dot, ADJUST mode = arrows move dot. "
          "Enter/Space toggles. Shift+arrow = big step in ADJUST. Mouse drag works in either mode. "
          "'s' save, 'r' reset, 'esc' cancel.")

    # Manual event loop: blocking plt.show() has been observed not to return
    # after plt.close(fig) when fullscreen on a second display, leaving the
    # script hung after 's'. Polling pause() instead lets us exit deterministically.
    try:
        plt.show(block=False)
        while not (state['saved'] or state['cancelled']):
            if not plt.fignum_exists(fig.number):
                break
            plt.pause(0.05)
    finally:
        if plt.fignum_exists(fig.number):
            plt.close(fig)
        # Restore the matplotlib keymaps we disabled, so callers' figures
        # behave normally afterwards.
        for k, v in _saved_keymaps.items():
            plt.rcParams[k] = v

    if state['cancelled']:
        raise RuntimeError("Manual tweak cancelled by user.")
    return np.clip(work, -1, 1)
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

# Snapshot of the raw 42-point capture for the final QA plot. We can't open
# fig1 here: if the manual-tweak GUI runs, plt.show() blocks until *all* open
# figures close, so any pre-existing figure traps the user after pressing 's'.
raw_mesh_xy = mesh_mapping[:, 0:2].copy()

# ── Interpolate azimuth rings ──────────────────────────────────────────────────
n_rows = len(np.unique(mesh_mapping[:, 3]))
n_cols = len(np.unique(mesh_mapping[:, 2]))

# ── Optional manual tweak step ─────────────────────────────────────────────────
# Coarse-interpolate, let the user drag points on the calibration display, then
# replace mesh_mapping with the corrected coarse grid so the existing fine
# interp block below sees a denser (and corrected) input.
if manual_tweak:
    n_coarse_cols = (n_cols - 1) * (coarse_factor + 1) + 1
    n_coarse_rows = (n_rows - 1) * (coarse_factor + 1) + 1
    print(f"[manual_tweak] Coarse interpolating to {n_coarse_rows} x {n_coarse_cols} ...")

    coarse_mesh = coarse_interpolate(
        mesh_mapping[:, 0:2], n_rows, n_cols, n_coarse_rows, n_coarse_cols
    )

    aspect = display_resolution[0] / display_resolution[1]
    coarse_mesh = run_manual_tweak_gui(
        coarse_mesh, aspect=aspect, fullscreen_second=fullscreen_second,
        screen_index=gui_screen_index,
    )

    # Rebuild mesh_mapping from the tweaked coarse grid in row-major order, with
    # az/el columns matching the new (denser) grid resolution. The fine interp
    # block below recomputes n_rows/n_cols from the data, so it adapts.
    az_grid = np.linspace(0, 1, n_coarse_cols)
    el_grid = np.linspace(0, 1, n_coarse_rows)
    az_full, el_full = np.meshgrid(az_grid, el_grid)
    mesh_mapping = np.column_stack([
        coarse_mesh[:, :, 0].ravel(),
        coarse_mesh[:, :, 1].ravel(),
        az_full.ravel(),
        el_full.ravel(),
        np.ones(n_coarse_rows * n_coarse_cols),
    ])
    n_rows = n_coarse_rows
    n_cols = n_coarse_cols

    if save_tweaked_csv:
        tweaked_path = os.path.join(
            save_folder,
            f"{fileBase}_coarse_tweaked_{n_coarse_rows}x{n_coarse_cols}.csv"
        )
        np.savetxt(
            tweaked_path,
            np.column_stack([
                mesh_mapping[:, 0], mesh_mapping[:, 1],
                mesh_mapping[:, 2], mesh_mapping[:, 3],
            ]),
            delimiter=',', fmt="%0.16f",
        )
        print(f"[manual_tweak] Saved tweaked coarse mesh to: {tweaked_path}")
#

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
    fig1, axes = plt.subplots(1, 2, figsize=(12, 5))
    axes[0].plot(raw_mesh_xy[:, 0], raw_mesh_xy[:, 1], 'or')
    axes[0].set_title('Original mesh mapping file')
    axes[1].plot(raw_mesh_xy[:, 0], raw_mesh_xy[:, 1], 'ro', label='original data')
    axes[1].plot(mesh_norm_interp[:, 0], -mesh_norm_interp[:, 1], 'k.', label='interpolated data')
    axes[1].legend()
    axes[1].set_title('Interpolated mesh map file')
    plt.tight_layout()
    plt.show()

# ── Save output ────────────────────────────────────────────────────────────────
np.savetxt(output_file_name, mesh_norm_interp, delimiter=',', fmt="%0.16f")
print(f"Saved to: {output_file_name}")
