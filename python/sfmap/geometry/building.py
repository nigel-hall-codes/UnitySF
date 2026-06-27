"""triangle lib CDT triangulation + wall extrusion for building meshes."""
from __future__ import annotations

import math
from typing import Dict, List, Optional, Tuple

import numpy as np

from ..elevation import HeightmapData
from ..osm import BuildingWay, StreetGraph
from .road import MeshArrays, _sample_elevation

_DEFAULT_HEIGHT = 10.0  # metres when building has no height tag

# Metres the foundation is sunk below the lowest terrain sample under the
# footprint. Guarantees the walls bury into the ground at every corner — and
# across edge midpoints over concave terrain — so no gap or floating sliver
# shows beneath a building on a slope (#219).
_FOUNDATION_EMBED = 1.0


def build_building_meshes(
    graph: StreetGraph,
    hmap: HeightmapData,
    default_height: float = _DEFAULT_HEIGHT,
) -> Dict[int, MeshArrays]:
    """Build wall+roof meshes for every building in the graph.

    Returns a dict keyed by building osm_id → (vertices, uvs, indices).
    Vertices are in Unity left-handed coords (+X east, +Y up, +Z north).
    Triangles use CW winding when viewed from outside the surface.
    """
    result: Dict[int, MeshArrays] = {}
    for building in graph.buildings:
        arrays = _build_single(building, hmap, default_height)
        if arrays is not None:
            result[building.osm_id] = arrays
    return result


def _build_single(
    b: BuildingWay,
    hmap: HeightmapData,
    default_height: float,
) -> Optional[MeshArrays]:
    # Drop closing vertex if OSM polygon is closed.
    fp = list(b.footprint)
    if len(fp) > 1 and fp[0] == fp[-1]:
        fp = fp[:-1]
    n = len(fp)
    if n < 3:
        return None

    # Ensure CCW winding in XZ (shoelace area > 0) so wall normals face outward.
    if _signed_area_xz(fp) < 0:
        fp = fp[::-1]

    # Sample terrain across the whole footprint, not just the centroid: on a
    # slope the corners sit at very different elevations. Sink the base below the
    # lowest sample so the walls bury into the ground everywhere (no downhill
    # gap), and lift the roof off the highest sample so the uphill side keeps its
    # full height above ground (no sinking into the hill). See issue #219.
    elevs = [_sample_elevation(hmap, px, pz) for px, pz in fp]
    base_y = min(elevs) - _FOUNDATION_EMBED
    height = b.height if b.height > 0.0 else default_height
    top_y = max(elevs) + height

    verts: List[Tuple[float, float, float]] = []
    uvs: List[Tuple[float, float]] = []
    indices: List[int] = []

    _build_walls(fp, n, base_y, top_y, verts, uvs, indices)
    _build_roof(fp, n, top_y, verts, uvs, indices)

    return verts, uvs, indices


def _build_walls(
    fp: List[Tuple[float, float]],
    n: int,
    base_y: float,
    top_y: float,
    verts: List,
    uvs: List,
    indices: List,
) -> None:
    """Quad strip around the perimeter, CW winding for outward normals."""
    for i in range(n):
        ax, az = fp[i]
        bx, bz = fp[(i + 1) % n]
        seg_len = math.hypot(bx - ax, bz - az)

        v = len(verts)
        verts += [
            (ax, base_y, az),  # v+0  B0
            (bx, base_y, bz),  # v+1  B1
            (bx, top_y,  bz),  # v+2  T1
            (ax, top_y,  az),  # v+3  T0
        ]
        uvs += [
            (0.0, 0.0),
            (seg_len, 0.0),
            (seg_len, top_y - base_y),
            (0.0,     top_y - base_y),
        ]
        # CW from outside: B0→T1→B1, B0→T0→T1
        indices += [v, v + 2, v + 1,
                    v, v + 3, v + 2]


def _build_roof(
    fp: List[Tuple[float, float]],
    n: int,
    top_y: float,
    verts: List,
    uvs: List,
    indices: List,
) -> None:
    """Flat roof triangulated with the triangle library (CDT).

    Falls back to ear-clipping if the triangle package is unavailable.
    """
    v_base = len(verts)
    for x, z in fp:
        verts.append((x, top_y, z))
        uvs.append((x, z))

    try:
        roof_tris = _triangulate_polygon_triangle(fp)
    except Exception:
        roof_tris = _ear_clip(list(range(n)), fp)

    for a, b, c in roof_tris:
        # Emit CW (c, b, a) for +Y upward normal.
        indices += [v_base + c, v_base + b, v_base + a]


def _triangulate_polygon_triangle(
    fp: List[Tuple[float, float]],
) -> List[Tuple[int, int, int]]:
    """Use the triangle library to CDT-triangulate a simple polygon."""
    import triangle as tri_lib  # noqa: PLC0415

    n = len(fp)
    pts = np.array(fp, dtype=np.float64)
    segs = np.array([(i, (i + 1) % n) for i in range(n)], dtype=np.int32)

    result = tri_lib.triangulate({"vertices": pts, "segments": segs}, "p")
    tris = result.get("triangles", [])

    # Filter out any triangles with a centroid outside the polygon (holes/Steiner).
    from shapely.geometry import Polygon, Point
    poly_shape = Polygon(fp)
    out = []
    for t in tris:
        a, b, c = int(t[0]), int(t[1]), int(t[2])
        cx = (fp[a][0] + fp[b][0] + fp[c][0]) / 3
        cz = (fp[a][1] + fp[b][1] + fp[c][1]) / 3
        if poly_shape.contains(Point(cx, cz)):
            out.append((a, b, c))
    return out


def _ear_clip(
    ring: List[int],
    fp: List[Tuple[float, float]],
) -> List[Tuple[int, int, int]]:
    """Fallback ear-clipper — O(n²), fine for typical building footprint sizes."""
    tris: List[Tuple[int, int, int]] = []
    remaining = list(ring)
    while len(remaining) >= 3:
        clipped = False
        m = len(remaining)
        for i in range(m):
            pi = remaining[(i - 1) % m]
            ci = remaining[i]
            ni = remaining[(i + 1) % m]
            pa, pb, pc = fp[pi], fp[ci], fp[ni]
            cross = (pb[0] - pa[0]) * (pc[1] - pa[1]) - (pb[1] - pa[1]) * (pc[0] - pa[0])
            if cross <= 0.0:
                continue
            is_ear = all(
                not _point_in_tri(fp[r], pa, pb, pc)
                for r in remaining
                if r not in (pi, ci, ni)
            )
            if not is_ear:
                continue
            tris.append((pi, ci, ni))
            remaining.pop(i)
            clipped = True
            break
        if not clipped:
            break
    return tris


def _signed_area_xz(fp: List[Tuple[float, float]]) -> float:
    n = len(fp)
    area = 0.0
    for i in range(n):
        ax, az = fp[i]
        bx, bz = fp[(i + 1) % n]
        area += ax * bz - bx * az
    return area * 0.5


def _point_in_tri(
    p: Tuple[float, float],
    a: Tuple[float, float],
    b: Tuple[float, float],
    c: Tuple[float, float],
) -> bool:
    d1 = (p[0] - a[0]) * (b[1] - a[1]) - (p[1] - a[1]) * (b[0] - a[0])
    d2 = (p[0] - b[0]) * (c[1] - b[1]) - (p[1] - b[1]) * (c[0] - b[0])
    d3 = (p[0] - c[0]) * (a[1] - c[1]) - (p[1] - c[1]) * (a[0] - c[0])
    return (d1 >= 0 and d2 >= 0 and d3 >= 0) or (d1 <= 0 and d2 <= 0 and d3 <= 0)
