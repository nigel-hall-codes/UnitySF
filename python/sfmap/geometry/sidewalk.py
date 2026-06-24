"""numpy quad-strip sidewalk mesh generation (4 verts/point)."""
from __future__ import annotations

import math
from typing import Dict, List, Optional, Tuple

from ..elevation import HeightmapData
from ..osm import StreetEdge, StreetGraph
from .road import MeshArrays, _anchor_centerline, _clip_polyline_to_rect, _cross_up, _forward, _sample_elevation

_WIDTH = 1.5    # metres per sidewalk strip (left and right)
_RAISE = 0.10   # metres above terrain — slightly higher than road surface


def build_sidewalk_meshes(
    graph: StreetGraph,
    hmap: HeightmapData,
    boundaries: Optional[Dict[Tuple[int, int, int], Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]]] = None,
) -> Dict[Tuple[int, int, int], MeshArrays]:
    """Build combined left+right sidewalk meshes for every driveable edge.

    Returns a dict keyed by (osm_way_id, from_node_id, to_node_id) → (vertices, uvs, indices).
    Each mesh contains both the left and right sidewalk strips (4 verts per
    cross-section: left-outer, left-inner, right-inner, right-outer).
    Vertices are in Unity left-handed coords. Triangles are CW from above.
    """
    result: Dict[Tuple[int, int, int], MeshArrays] = {}
    for edge in graph.edges:
        if edge.width <= 0.0:
            continue
        key = (edge.osm_way_id, edge.from_node.osm_id, edge.to_node.osm_id)
        bd_from, bd_to = (boundaries or {}).get(key, (None, None))
        arrays = _build_single_sidewalk(edge, hmap, bd_from, bd_to)
        if arrays is not None:
            result[key] = arrays
    return result


def _build_single_sidewalk(
    edge: StreetEdge,
    hmap: HeightmapData,
    bd_from: Optional[Tuple[float, float]],
    bd_to: Optional[Tuple[float, float]],
) -> Optional[MeshArrays]:
    bx0 = hmap.world_x_min
    bz0 = hmap.world_z_min
    bx1 = hmap.world_x_min + hmap.world_width
    bz1 = hmap.world_z_min + hmap.world_height

    cl_xz = _clip_polyline_to_rect(edge.centerline, bx0, bz0, bx1, bz1)
    sampled = [
        (x, _sample_elevation(hmap, x, z), z)
        for x, z in cl_xz
    ]

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

    half_w = edge.width * 0.5
    outer_offset = half_w + _WIDTH

    arc_len = [0.0] * n
    for i in range(1, n):
        dx = centerline[i][0] - centerline[i - 1][0]
        dy = centerline[i][1] - centerline[i - 1][1]
        dz = centerline[i][2] - centerline[i - 1][2]
        arc_len[i] = arc_len[i - 1] + math.sqrt(dx * dx + dy * dy + dz * dz)
    total_len = arc_len[-1] if arc_len[-1] > 0.001 else 1.0

    # 4 verts per cross-section: left-outer(0), left-inner(1), right-inner(2), right-outer(3)
    verts: List[Tuple[float, float, float]] = []
    uvs: List[Tuple[float, float]] = []

    for i, (cx, cy, cz) in enumerate(centerline):
        fwd = _forward(centerline, i)
        rx, _, rz = _cross_up(fwd)
        length = math.hypot(rx, rz)
        if length > 1e-6:
            rx, rz = rx / length, rz / length

        v_coord = arc_len[i] / total_len
        lo_x, lo_z = cx - rx * outer_offset, cz - rz * outer_offset
        li_x, li_z = cx - rx * half_w, cz - rz * half_w
        ri_x, ri_z = cx + rx * half_w, cz + rz * half_w
        ro_x, ro_z = cx + rx * outer_offset, cz + rz * outer_offset

        # left-outer (index i*4)
        verts.append((lo_x, _sample_elevation(hmap, lo_x, lo_z) + _RAISE, lo_z))
        uvs.append((1.0, v_coord))
        # left-inner (index i*4 + 1)
        verts.append((li_x, _sample_elevation(hmap, li_x, li_z) + _RAISE, li_z))
        uvs.append((0.0, v_coord))
        # right-inner (index i*4 + 2)
        verts.append((ri_x, _sample_elevation(hmap, ri_x, ri_z) + _RAISE, ri_z))
        uvs.append((0.0, v_coord))
        # right-outer (index i*4 + 3)
        verts.append((ro_x, _sample_elevation(hmap, ro_x, ro_z) + _RAISE, ro_z))
        uvs.append((1.0, v_coord))

    # 2 strips × (n-1) quads × 2 tris × 3 indices = (n-1) * 12
    indices: List[int] = []
    for i in range(n - 1):
        b = i * 4
        t = b + 4
        # Left strip (outer→inner): CW from above → normal up
        indices += [b,     t,     b + 1,
                    t,     t + 1, b + 1]
        # Right strip (inner→outer): CW from above → normal up
        indices += [b + 2, t + 2, b + 3,
                    t + 2, t + 3, b + 3]

    return verts, uvs, indices
