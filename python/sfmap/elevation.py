"""CSV → numpy heightmap via scipy Delaunay interpolation."""
from __future__ import annotations

import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Optional, Tuple

import numpy as np
from scipy.ndimage import gaussian_filter
from scipy.spatial import Delaunay

from .projection import GeoOrigin, OsmBounds, to_world_xz

_FEET_TO_METERS = 0.3048
_CLIP_BUFFER_METERS = 200.0
_MIN_SPACING_SQ = 8.0 ** 2  # thin contour points to ~8m minimum spacing
_CACHE_MAGIC = 0x454D4843  # "EMHC" — matches C# cache

# Default source low-pass radius (metres, σ). The rasterizer fills the grid by
# linear barycentric interpolation per Delaunay triangle, which is only C⁰ — the
# gradient jumps across every triangle edge, seeding crease lines and the road
# waviness #230 is about. A small Gaussian low-pass over the rasterized grid
# attenuates those creases at the source (also smoothing buildings/sidewalks
# that sample the same field) while leaving real, longer-wavelength relief
# intact. Kept smaller than the ~8 m contour-point spacing so genuine grade is
# preserved. 0 disables. Validated/tuned on real terrain in #232. See #233.
_HMAP_SMOOTH_SIGMA_M = 2.0


@dataclass
class HeightmapData:
    """Normalised [0,1] heightmap in row-major (row=south→north, col=west→east) order."""
    values: np.ndarray          # shape (resolution, resolution), dtype float32
    resolution: int
    min_elevation_m: float
    max_elevation_m: float
    world_x_min: float
    world_z_min: float
    world_width: float
    world_height: float

    def sample_bilinear(self, x: float, z: float) -> float:
        """Sample the heightmap at world position (x, z) using bilinear interpolation."""
        res = self.resolution
        cell_w = self.world_width / (res - 1)
        cell_h = self.world_height / (res - 1)
        nx = (x - self.world_x_min) / cell_w
        nz = (z - self.world_z_min) / cell_h
        col0 = int(max(0, min(res - 2, int(nx))))
        row0 = int(max(0, min(res - 2, int(nz))))
        tx = max(0.0, min(1.0, nx - col0))
        tz = max(0.0, min(1.0, nz - row0))
        v00 = float(self.values[row0,     col0    ])
        v10 = float(self.values[row0,     col0 + 1])
        v01 = float(self.values[row0 + 1, col0    ])
        v11 = float(self.values[row0 + 1, col0 + 1])
        return (v00 * (1 - tx) + v10 * tx) * (1 - tz) + (v01 * (1 - tx) + v11 * tx) * tz


def _cache_path(csv_path: str, resolution: int) -> Path:
    return Path(f"{csv_path}.r{resolution}.heightcache")


def _try_load_cache(
    csv_path: str,
    resolution: int,
    world_x_min: float,
    world_z_min: float,
    world_width: float,
    world_height: float,
) -> Optional[HeightmapData]:
    p = _cache_path(csv_path, resolution)
    if not p.exists():
        return None
    src = Path(csv_path)
    if src.stat().st_mtime > p.stat().st_mtime:
        return None
    try:
        with open(p, "rb") as f:
            data = f.read()
        offset = 0
        magic, = struct.unpack_from("<I", data, offset); offset += 4
        if magic != _CACHE_MAGIC:
            return None
        res, = struct.unpack_from("<i", data, offset); offset += 4
        if res != resolution:
            return None
        min_elev, max_elev = struct.unpack_from("<ff", data, offset); offset += 8
        rx, ry, rw, rh = struct.unpack_from("<ffff", data, offset); offset += 16
        if (abs(rx - world_x_min) > 1.0 or abs(ry - world_z_min) > 1.0
                or abs(rw - world_width) > 1.0 or abs(rh - world_height) > 1.0):
            return None
        vals_flat = struct.unpack_from(f"<{resolution * resolution}f", data, offset)
        values = np.array(vals_flat, dtype=np.float32).reshape(resolution, resolution)
        return HeightmapData(
            values=values,
            resolution=resolution,
            min_elevation_m=min_elev,
            max_elevation_m=max_elev,
            world_x_min=rx,
            world_z_min=ry,
            world_width=rw,
            world_height=rh,
        )
    except Exception:
        return None


def _save_cache(csv_path: str, hmap: HeightmapData) -> None:
    p = _cache_path(csv_path, hmap.resolution)
    try:
        header = struct.pack("<Ii", _CACHE_MAGIC, hmap.resolution)
        meta = struct.pack(
            "<ffffff",
            hmap.min_elevation_m,
            hmap.max_elevation_m,
            hmap.world_x_min,
            hmap.world_z_min,
            hmap.world_width,
            hmap.world_height,
        )
        vals_bytes = hmap.values.astype(np.float32).tobytes()
        p.write_bytes(header + meta + vals_bytes)
    except Exception:
        pass


def clear_cache(csv_path: str, resolution: int) -> None:
    p = _cache_path(csv_path, resolution)
    if p.exists():
        p.unlink()


def apply_vertical_exaggeration(hmap: HeightmapData, factor: float) -> None:
    """Exaggerate terrain relief by ``factor``, anchored at the lowest elevation.

    Stretches the elevation *range* (max - min) while keeping min fixed, so the
    lowest contour stays put and every slope gets ``factor``× steeper. The
    normalised [0,1] heightmap is untouched; only the min/max metres that map
    [0,1] → world Y are scaled. Both the terrain header and feature elevation
    sampling (``road._sample_elevation``) derive metres from these two values,
    so terrain and roads/sidewalks/buildings stay aligned. Building heights are
    added on top of the sampled ground, so they are unaffected.

    Mutates ``hmap`` in place. ``factor == 1.0`` is a no-op.
    """
    if factor == 1.0:
        return
    hmap.max_elevation_m = hmap.min_elevation_m + factor * (
        hmap.max_elevation_m - hmap.min_elevation_m
    )


def parse(
    csv_path: str,
    bounds: OsmBounds,
    origin: GeoOrigin,
    resolution: int = 513,
    smooth_sigma_m: float = _HMAP_SMOOTH_SIGMA_M,
) -> HeightmapData:
    """Parse elevation CSV and return a normalised heightmap.

    Reads from disk cache when the CSV hasn't changed since the last bake.
    CSV format: header row, then rows of `id, elev_feet, "LINESTRING (lon lat, ...)"`.

    ``smooth_sigma_m`` low-passes the rasterized grid to suppress triangle-edge
    creases at the source (0 disables). The cache stores the smoothed grid, so
    changing this value requires re-baking / ``clear_cache`` to take effect.
    """
    x_min, z_min, width, height = _world_rect(bounds, origin)

    cached = _try_load_cache(csv_path, resolution, x_min, z_min, width, height)
    if cached is not None:
        return cached

    clip_x_min = x_min - _CLIP_BUFFER_METERS
    clip_z_min = z_min - _CLIP_BUFFER_METERS
    clip_x_max = x_min + width  + _CLIP_BUFFER_METERS
    clip_z_max = z_min + height + _CLIP_BUFFER_METERS

    pts, elevs = _collect_contour_points(csv_path, origin, clip_x_min, clip_z_min, clip_x_max, clip_z_max)

    if len(pts) < 10:
        import warnings
        warnings.warn(f"[elevation] Only {len(pts)} contour vertices within bounds — sparse coverage")

    pts_arr = np.array(pts, dtype=np.float64)
    elevs_arr = np.array(elevs, dtype=np.float32)

    min_elev = float(elevs_arr.min()) if len(elevs_arr) > 0 else 0.0
    max_elev = float(elevs_arr.max()) if len(elevs_arr) > 0 else 1.0
    if max_elev - min_elev < 0.01:
        max_elev = min_elev + 1.0

    values, covered = _rasterize(pts_arr, elevs_arr, min_elev, max_elev, resolution, x_min, z_min, width, height)

    if smooth_sigma_m > 0.0 and resolution > 1:
        cell_w = width / (resolution - 1)
        cell_h = height / (resolution - 1)
        cell_m = 0.5 * (cell_w + cell_h)
        if cell_m > 1e-6:
            values = _low_pass_normalized(values, covered, smooth_sigma_m / cell_m)

    hmap = HeightmapData(
        values=values,
        resolution=resolution,
        min_elevation_m=min_elev,
        max_elevation_m=max_elev,
        world_x_min=x_min,
        world_z_min=z_min,
        world_width=width,
        world_height=height,
    )
    _save_cache(csv_path, hmap)
    return hmap


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

def _world_rect(bounds: OsmBounds, origin: GeoOrigin) -> Tuple[float, float, float, float]:
    sw_x, sw_z = to_world_xz(bounds.min_lon, bounds.min_lat, origin)
    ne_x, ne_z = to_world_xz(bounds.max_lon, bounds.max_lat, origin)
    return sw_x, sw_z, ne_x - sw_x, ne_z - sw_z


def _collect_contour_points(
    csv_path: str,
    origin: GeoOrigin,
    clip_x_min: float,
    clip_z_min: float,
    clip_x_max: float,
    clip_z_max: float,
) -> Tuple[list, list]:
    pts: list = []
    elevs: list = []

    with open(csv_path, "r", encoding="utf-8") as f:
        f.readline()  # skip header
        for line in f:
            line = line.strip()
            if not line:
                continue
            fields = _split_csv_line(line)
            if len(fields) < 3:
                continue
            try:
                elev_feet = int(fields[1])
            except ValueError:
                continue
            elev_m = elev_feet * _FEET_TO_METERS

            last_x = last_z = float("inf")
            for lon, lat in _parse_linestring(fields[2]):
                x, z = to_world_xz(lon, lat, origin)
                if not (clip_x_min <= x <= clip_x_max and clip_z_min <= z <= clip_z_max):
                    continue
                dx = x - last_x
                dz = z - last_z
                if dx * dx + dz * dz < _MIN_SPACING_SQ:
                    continue
                last_x, last_z = x, z
                pts.append((x, z))
                elevs.append(elev_m)

    return pts, elevs


def _split_csv_line(line: str) -> list:
    """Minimal CSV splitter that handles quoted fields containing commas."""
    fields = []
    i = 0
    while i < len(line):
        if line[i] == '"':
            i += 1
            start = i
            while i < len(line):
                if line[i] == '"' and (i + 1 >= len(line) or line[i + 1] != '"'):
                    break
                if line[i] == '"':
                    i += 1  # escaped quote
                i += 1
            fields.append(line[start:i])
            i += 1  # closing quote
            if i < len(line) and line[i] == ',':
                i += 1
        else:
            start = i
            while i < len(line) and line[i] != ',':
                i += 1
            fields.append(line[start:i])
            if i < len(line):
                i += 1
    return fields


def _parse_linestring(wkt: str):
    """Yield (lon, lat) pairs from a WKT LINESTRING."""
    open_p = wkt.find('(')
    close_p = wkt.rfind(')')
    if open_p < 0 or close_p < 0:
        return
    inner = wkt[open_p + 1:close_p]
    for raw_pair in inner.split(','):
        pair = raw_pair.strip()
        space = pair.find(' ')
        if space <= 0:
            continue
        try:
            lon = float(pair[:space])
            lat = float(pair[space + 1:])
            yield lon, lat
        except ValueError:
            continue


def _rasterize(
    pts: np.ndarray,
    elevs: np.ndarray,
    min_elev: float,
    max_elev: float,
    resolution: int,
    x_min: float,
    z_min: float,
    width: float,
    height: float,
) -> Tuple[np.ndarray, np.ndarray]:
    """Triangulate pts and rasterize elevation into a (resolution × resolution) grid.

    Returns ``(values, covered)`` where ``covered`` marks cells a triangle
    actually filled (1.0) vs. cells left at 0.0 because they fall outside the
    contour convex hull. The mask lets the low-pass smooth only real data
    instead of bleeding the outside-hull zeros inward.
    """
    values = np.zeros((resolution, resolution), dtype=np.float32)
    covered = np.zeros((resolution, resolution), dtype=np.float32)

    if len(pts) < 3:
        return values, covered

    tri = Delaunay(pts)
    elev_range = max_elev - min_elev
    cell_w = width / (resolution - 1)
    cell_h = height / (resolution - 1)

    for simplex in tri.simplices:
        ia, ib, ic = simplex
        ax, az = pts[ia]
        bx, bz = pts[ib]
        cx, cz = pts[ic]
        ea, eb, ec = elevs[ia], elevs[ib], elevs[ic]

        col_min = max(0,            int((min(ax, bx, cx) - x_min) / cell_w))
        col_max = min(resolution - 1, int((max(ax, bx, cx) - x_min) / cell_w) + 1)
        row_min = max(0,            int((min(az, bz, cz) - z_min) / cell_h))
        row_max = min(resolution - 1, int((max(az, bz, cz) - z_min) / cell_h) + 1)

        for row in range(row_min, row_max + 1):
            wz = z_min + row * cell_h
            for col in range(col_min, col_max + 1):
                wx = x_min + col * cell_w
                u, v, w = _barycentric(wx, wz, ax, az, bx, bz, cx, cz)
                if u < 0 or v < 0 or w < 0:
                    continue
                elev = u * ea + v * eb + w * ec
                values[row, col] = (elev - min_elev) / elev_range
                covered[row, col] = 1.0

    return values, covered


def _low_pass_normalized(
    values: np.ndarray,
    covered: np.ndarray,
    sigma_cells: float,
) -> np.ndarray:
    """Gaussian low-pass that ignores outside-hull (uncovered) cells.

    Plain blurring would pull the 0.0 in uncovered cells into the valid region,
    darkening the terrain near the convex-hull boundary. Normalized convolution
    — ``blur(values·mask) / blur(mask)`` — weights each output only by covered
    neighbours, so the edge stays put. Uncovered cells are left at their original
    value. Returns a float32 grid the same shape as ``values``.
    """
    if sigma_cells <= 0.0:
        return values

    masked = (values * covered).astype(np.float64)
    mask = covered.astype(np.float64)
    num = gaussian_filter(masked, sigma_cells, mode="constant", cval=0.0)
    den = gaussian_filter(mask, sigma_cells, mode="constant", cval=0.0)

    out = values.astype(np.float64).copy()
    valid = (covered > 0.0) & (den > 1e-6)
    out[valid] = num[valid] / den[valid]
    return out.astype(np.float32)


def _barycentric(
    px: float, pz: float,
    ax: float, az: float,
    bx: float, bz: float,
    cx: float, cz: float,
) -> Tuple[float, float, float]:
    """Compute barycentric coords (u, v, w) of point p in triangle (a, b, c)."""
    v0x, v0z = bx - ax, bz - az
    v1x, v1z = cx - ax, cz - az
    v2x, v2z = px - ax, pz - az

    d00 = v0x * v0x + v0z * v0z
    d01 = v0x * v1x + v0z * v1z
    d11 = v1x * v1x + v1z * v1z
    d20 = v2x * v0x + v2z * v0z
    d21 = v2x * v1x + v2z * v1z

    denom = d00 * d11 - d01 * d01
    if abs(denom) < 1e-10:
        return -1.0, -1.0, -1.0
    v = (d11 * d20 - d01 * d21) / denom
    w = (d00 * d21 - d01 * d20) / denom
    u = 1.0 - v - w
    return u, v, w
