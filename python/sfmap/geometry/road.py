"""numpy quad-strip road mesh generation."""
from __future__ import annotations

import math
from typing import Dict, List, Optional, Tuple

from ..elevation import HeightmapData
from ..osm import StreetEdge, StreetGraph

_RAISE = 0.20   # metres above terrain — clears bilinear interpolation bleed

MeshArrays = Tuple[
    List[Tuple[float, float, float]],  # vertices (x, y, z)
    List[Tuple[float, float]],          # UVs (u, v)
    List[int],                          # triangle indices (CW winding)
]


def build_road_meshes(
    graph: StreetGraph,
    hmap: HeightmapData,
    boundaries: Optional[Dict[Tuple[int, int, int], Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]]] = None,
    width_multiplier: float = 1.0,
) -> Dict[Tuple[int, int, int], MeshArrays]:
    """Build road quad-strip meshes for every driveable edge in the graph.

    Returns a dict keyed by (osm_way_id, from_node_id, to_node_id) → (vertices, uvs, indices).
    Vertices are in Unity left-handed coords (+X east, +Y up, +Z north).
    Triangles use CW winding when viewed from above (+Y).
    """
    result: Dict[Tuple[int, int, int], MeshArrays] = {}
    for edge in graph.edges:
        if edge.width <= 0.0:
            continue
        key = (edge.osm_way_id, edge.from_node.osm_id, edge.to_node.osm_id)
        bd_from, bd_to = (boundaries or {}).get(key, (None, None))
        arrays = _build_single_road(edge, hmap, bd_from, bd_to, width_multiplier)
        if arrays is not None:
            result[key] = arrays
    return result


def _build_single_road(
    edge: StreetEdge,
    hmap: HeightmapData,
    bd_from: Optional[Tuple[float, float]],
    bd_to: Optional[Tuple[float, float]],
    width_multiplier: float,
) -> Optional[MeshArrays]:
    bx0 = hmap.world_x_min
    bz0 = hmap.world_z_min
    bx1 = hmap.world_x_min + hmap.world_width
    bz1 = hmap.world_z_min + hmap.world_height

    # Clip centerline to chunk heightmap bounds so out-of-bounds points don't
    # get clamped to the wrong edge elevation.
    cl_xz = _clip_polyline_to_rect(edge.centerline, bx0, bz0, bx1, bz1)

    # Sample terrain elevation at each centerline point (heightmap is post-stamp).
    sampled = [
        (x, _sample_elevation(hmap, x, z), z)
        for x, z in cl_xz
    ]

    # Only use boundary anchors that lie within the heightmap bounds; anchors
    # from an intersection in an adjacent chunk would sample a clamped (wrong)
    # edge elevation and make the road float near the chunk boundary.
    from_pt = None
    to_pt = None
    if bd_from is not None and bx0 <= bd_from[0] <= bx1 and bz0 <= bd_from[1] <= bz1:
        from_pt = (bd_from[0], _sample_elevation(hmap, bd_from[0], bd_from[1]), bd_from[1])
    if bd_to is not None and bx0 <= bd_to[0] <= bx1 and bz0 <= bd_to[1] <= bz1:
        to_pt = (bd_to[0], _sample_elevation(hmap, bd_to[0], bd_to[1]), bd_to[1])

    centerline = _anchor_centerline(sampled, from_pt, to_pt)
    n = len(centerline)
    if n < 2:
        return None

    half_w = edge.width * width_multiplier * 0.5

    arc_len = [0.0] * n
    for i in range(1, n):
        dx = centerline[i][0] - centerline[i - 1][0]
        dy = centerline[i][1] - centerline[i - 1][1]
        dz = centerline[i][2] - centerline[i - 1][2]
        arc_len[i] = arc_len[i - 1] + math.sqrt(dx * dx + dy * dy + dz * dz)
    total_len = arc_len[-1] if arc_len[-1] > 0.001 else 1.0

    verts: List[Tuple[float, float, float]] = []
    uvs: List[Tuple[float, float]] = []

    for i, (cx, cy, cz) in enumerate(centerline):
        fwd = _forward(centerline, i)
        rx, _, rz = _cross_up(fwd)  # right = cross(up, fwd), y ignored (terrain is near-flat)
        length = math.hypot(rx, rz)
        if length > 1e-6:
            rx, rz = rx / length, rz / length

        v_coord = arc_len[i] / total_len
        lx, lz = cx - rx * half_w, cz - rz * half_w
        ex, ez = cx + rx * half_w, cz + rz * half_w
        verts.append((lx, _sample_elevation(hmap, lx, lz) + _RAISE, lz))  # left
        verts.append((ex, _sample_elevation(hmap, ex, ez) + _RAISE, ez))  # right
        uvs.append((0.0, v_coord))
        uvs.append((1.0, v_coord))

    # CW winding from above: bl→tl→br, tl→tr→br
    indices: List[int] = []
    for i in range(n - 1):
        bl = i * 2
        br = bl + 1
        tl = bl + 2
        tr = br + 2
        indices += [bl, tl, br, tl, tr, br]

    return verts, uvs, indices


# ---------------------------------------------------------------------------
# Shared helpers (also imported by sidewalk.py)
# ---------------------------------------------------------------------------

def _sample_elevation(hmap: HeightmapData, x: float, z: float) -> float:
    norm = hmap.sample_bilinear(x, z)
    return hmap.min_elevation_m + norm * (hmap.max_elevation_m - hmap.min_elevation_m)


def _clip_polyline_to_rect(
    cl: List[Tuple[float, float]],
    x_min: float, z_min: float,
    x_max: float, z_max: float,
) -> List[Tuple[float, float]]:
    """Clip a 2D XZ polyline to [x_min,x_max]×[z_min,z_max], inserting crossings.

    Segments that cross the boundary get an interpolated point at the crossing.
    Points outside are dropped. Returns the clipped polyline (may be empty).
    """
    def _inside(x: float, z: float) -> bool:
        return x_min <= x <= x_max and z_min <= z <= z_max

    def _clip_seg(p0x: float, p0z: float, p1x: float, p1z: float):
        """Parametric clip; returns (t_enter, t_exit) for the visible portion, or None."""
        t0, t1 = 0.0, 1.0
        dx, dz = p1x - p0x, p1z - p0z
        for p, d, lo, hi in ((p0x, dx, x_min, x_max), (p0z, dz, z_min, z_max)):
            if abs(d) < 1e-10:
                if not (lo <= p <= hi):
                    return None
            else:
                ta, tb = (lo - p) / d, (hi - p) / d
                if ta > tb:
                    ta, tb = tb, ta
                t0, t1 = max(t0, ta), min(t1, tb)
                if t0 > t1 + 1e-10:
                    return None
        return t0, t1

    result: List[Tuple[float, float]] = []
    if not cl:
        return result

    x0, z0 = cl[0]
    if _inside(x0, z0):
        result.append((x0, z0))

    for i in range(1, len(cl)):
        px, pz = cl[i - 1]
        x, z = cl[i]
        in_p = _inside(px, pz)
        in_c = _inside(x, z)

        if not in_p or not in_c:
            ts = _clip_seg(px, pz, x, z)
            if ts is not None:
                t0, t1 = ts
                dx, dz = x - px, z - pz
                if not in_p and t0 > 1e-10:
                    result.append((px + t0 * dx, pz + t0 * dz))
                if not in_c and t1 < 1.0 - 1e-10:
                    result.append((px + t1 * dx, pz + t1 * dz))

        if in_c:
            result.append((x, z))

    return result


def _anchor_centerline(
    cl: List[Tuple[float, float, float]],
    from_pt: Optional[Tuple[float, float, float]],
    to_pt: Optional[Tuple[float, float, float]],
) -> List[Tuple[float, float, float]]:
    """Trim centerline to intersection boundary points, dropping interior vertices."""
    if not cl:
        return cl
    if from_pt is None and to_pt is None:
        return cl

    n = len(cl)
    arc = [0.0] * n
    for i in range(1, n):
        dx = cl[i][0] - cl[i - 1][0]
        dy = cl[i][1] - cl[i - 1][1]
        dz = cl[i][2] - cl[i - 1][2]
        arc[i] = arc[i - 1] + math.sqrt(dx * dx + dy * dy + dz * dz)
    total = arc[-1]

    if from_pt is not None:
        dx = cl[0][0] - from_pt[0]; dy = cl[0][1] - from_pt[1]; dz = cl[0][2] - from_pt[2]
        start_arc = math.sqrt(dx * dx + dy * dy + dz * dz)
    else:
        start_arc = 0.0

    if to_pt is not None:
        dx = cl[-1][0] - to_pt[0]; dy = cl[-1][1] - to_pt[1]; dz = cl[-1][2] - to_pt[2]
        end_arc = total - math.sqrt(dx * dx + dy * dy + dz * dz)
    else:
        end_arc = total

    if end_arc - start_arc < 0.01:
        return cl

    result = [from_pt if from_pt is not None else cl[0]]
    for i in range(1, n - 1):
        if start_arc < arc[i] < end_arc:
            result.append(cl[i])
    result.append(to_pt if to_pt is not None else cl[-1])
    return result


def _forward(cl: List[Tuple[float, float, float]], i: int) -> Tuple[float, float, float]:
    n = len(cl)
    if i == 0:
        dx = cl[1][0] - cl[0][0]; dy = cl[1][1] - cl[0][1]; dz = cl[1][2] - cl[0][2]
    elif i == n - 1:
        dx = cl[-1][0] - cl[-2][0]; dy = cl[-1][1] - cl[-2][1]; dz = cl[-1][2] - cl[-2][2]
    else:
        dx = cl[i + 1][0] - cl[i - 1][0]; dy = cl[i + 1][1] - cl[i - 1][1]; dz = cl[i + 1][2] - cl[i - 1][2]
    length = math.sqrt(dx * dx + dy * dy + dz * dz)
    if length < 1e-6:
        return (1.0, 0.0, 0.0)
    return (dx / length, dy / length, dz / length)


def _cross_up(fwd: Tuple[float, float, float]) -> Tuple[float, float, float]:
    """cross(up=(0,1,0), fwd) — gives the right vector in XZ (y component is 0)."""
    # cross(up, fwd) = (up.y*fwd.z - up.z*fwd.y,  up.z*fwd.x - up.x*fwd.z,  up.x*fwd.y - up.y*fwd.x)
    #                = (1*fwd.z - 0,                0 - 0,                     0 - 1*fwd.x)
    #                = (fwd.z,                      0,                         -fwd.x)
    return (fwd[2], 0.0, -fwd[0])
