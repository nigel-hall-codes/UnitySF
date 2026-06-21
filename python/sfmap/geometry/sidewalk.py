"""numpy quad-strip sidewalk mesh generation (4 verts/point)."""
from __future__ import annotations

import math
from typing import Dict, List, Optional, Tuple

from ..elevation import HeightmapData
from ..osm import StreetEdge, StreetGraph
from .road import MeshArrays, _anchor_centerline, _cross_up, _forward, _sample_elevation

_WIDTH = 1.5    # metres per sidewalk strip (left and right)
_RAISE = 0.10   # metres above terrain — slightly higher than road surface


def build_sidewalk_meshes(
    graph: StreetGraph,
    hmap: HeightmapData,
    boundaries: Optional[Dict[int, Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]]] = None,
) -> Dict[int, MeshArrays]:
    """Build combined left+right sidewalk meshes for every driveable edge.

    Returns a dict keyed by edge.osm_way_id → (vertices, uvs, indices).
    Each mesh contains both the left and right sidewalk strips (4 verts per
    cross-section: left-outer, left-inner, right-inner, right-outer).
    Vertices are in Unity left-handed coords. Triangles are CW from above.
    """
    result: Dict[int, MeshArrays] = {}
    for edge in graph.edges:
        if edge.width <= 0.0:
            continue
        bd_from, bd_to = (boundaries or {}).get(edge.osm_way_id, (None, None))
        arrays = _build_single_sidewalk(edge, hmap, bd_from, bd_to)
        if arrays is not None:
            result[edge.osm_way_id] = arrays
    return result


def _build_single_sidewalk(
    edge: StreetEdge,
    hmap: HeightmapData,
    bd_from: Optional[Tuple[float, float]],
    bd_to: Optional[Tuple[float, float]],
) -> Optional[MeshArrays]:
    cl_xz = edge.centerline
    sampled = [
        (x, _sample_elevation(hmap, x, z), z)
        for x, z in cl_xz
    ]

    from_pt = None
    to_pt = None
    if bd_from is not None:
        from_pt = (bd_from[0], _sample_elevation(hmap, bd_from[0], bd_from[1]), bd_from[1])
    if bd_to is not None:
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

        y = cy + _RAISE
        v_coord = arc_len[i] / total_len

        # left-outer (index i*4)
        verts.append((cx - rx * outer_offset, y, cz - rz * outer_offset))
        uvs.append((1.0, v_coord))
        # left-inner (index i*4 + 1)
        verts.append((cx - rx * half_w, y, cz - rz * half_w))
        uvs.append((0.0, v_coord))
        # right-inner (index i*4 + 2)
        verts.append((cx + rx * half_w, y, cz + rz * half_w))
        uvs.append((0.0, v_coord))
        # right-outer (index i*4 + 3)
        verts.append((cx + rx * outer_offset, y, cz + rz * outer_offset))
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
