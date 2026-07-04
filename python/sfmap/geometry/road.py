"""numpy quad-strip road mesh generation."""
from __future__ import annotations

import math
from typing import Dict, List, Optional, Tuple

from ..elevation import HeightmapData
from ..osm import StreetEdge, StreetGraph
from .primitives import (
    MAX_SEG_M,
    anchor_centerline,
    clip_polyline_to_rect,
    cross_up,
    densify_polyline,
    forward,
    sample_elevation,
    smooth_centerline_profile,
    vertex_normals,
)

_RAISE = 0.20   # metres above terrain — clears bilinear interpolation bleed

# Roads additionally carry explicit per-vertex normals so their welded end-caps
# shade continuously with the intersection fans (the importer recomputes normals
# per mesh otherwise, which seams the junction).
RoadMeshArrays = Tuple[
    List[Tuple[float, float, float]],  # vertices (x, y, z)
    List[Tuple[float, float, float]],  # normals (x, y, z)
    List[Tuple[float, float]],          # UVs (u, v)
    List[int],                          # triangle indices (CW winding)
]


def build_road_meshes(
    graph: StreetGraph,
    hmap: HeightmapData,
    boundaries: Optional[Dict[Tuple[int, int, int], Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]]] = None,
    width_multiplier: float = 1.0,
) -> Dict[Tuple[int, int, int], RoadMeshArrays]:
    """Build road quad-strip meshes for every driveable edge in the graph.

    Returns a dict keyed by (osm_way_id, from_node_id, to_node_id) → (vertices, normals, uvs, indices).
    Vertices are in Unity left-handed coords (+X east, +Y up, +Z north).
    Triangles use CW winding when viewed from above (+Y).
    """
    result: Dict[Tuple[int, int, int], RoadMeshArrays] = {}
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
) -> Optional[RoadMeshArrays]:
    bx0 = hmap.world_x_min
    bz0 = hmap.world_z_min
    bx1 = hmap.world_x_min + hmap.world_width
    bz1 = hmap.world_z_min + hmap.world_height

    # Clip centerline to chunk heightmap bounds so out-of-bounds points don't
    # get clamped to the wrong edge elevation.
    cl_xz = clip_polyline_to_rect(edge.centerline, bx0, bz0, bx1, bz1)

    # Subdivide long segments so cross-sections track the heightfield up steep
    # grades instead of bridging the hill with one flat quad (#219).
    cl_xz = densify_polyline(cl_xz, MAX_SEG_M)

    # Sample terrain elevation at each centerline point (heightmap is post-stamp).
    sampled = [
        (x, sample_elevation(hmap, x, z), z)
        for x, z in cl_xz
    ]

    # Only use boundary anchors that lie within the heightmap bounds; anchors
    # from an intersection in an adjacent chunk would sample a clamped (wrong)
    # edge elevation and make the road float near the chunk boundary.
    from_pt = None
    to_pt = None
    if bd_from is not None and bx0 <= bd_from[0] <= bx1 and bz0 <= bd_from[1] <= bz1:
        from_pt = (bd_from[0], sample_elevation(hmap, bd_from[0], bd_from[1]), bd_from[1])
    if bd_to is not None and bx0 <= bd_to[0] <= bx1 and bz0 <= bd_to[1] <= bz1:
        to_pt = (bd_to[0], sample_elevation(hmap, bd_to[0], bd_to[1]), bd_to[1])

    centerline = anchor_centerline(sampled, from_pt, to_pt)
    n = len(centerline)
    if n < 2:
        return None

    # Smooth the interior vertical profile so the road stops reproducing
    # high-frequency terrain noise, while genuine grades pass through. Applied
    # identically in the stamp pass (stamping.stamp_roads) so the stamped grade
    # the mesh resamples below stays consistent — XZ and the anchored endpoints
    # are kept exact. See #230/#231.
    smooth_y = smooth_centerline_profile(
        [(cx, cz) for cx, _, cz in centerline],
        [cy for _, cy, _ in centerline],
    )
    centerline = [
        (centerline[i][0], smooth_y[i], centerline[i][2]) for i in range(n)
    ]

    # Whether each end is anchored to an intersection polygon. Anchored ends weld
    # to the intersection fan: the fan sticks its rim corners onto the exact same
    # XZ and samples the same terrain, so the two meshes share those vertices in
    # position. Here we additionally force the end-cap normals straight up to match
    # the fan's, so the shared edge shades as one continuous surface rather than a
    # seam between independently-recalculated meshes.
    anchored_start = from_pt is not None
    anchored_end = to_pt is not None

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
        fwd = forward(centerline, i)
        rx, _, rz = cross_up(fwd)  # right = cross(up, fwd), y ignored (terrain is near-flat)
        length = math.hypot(rx, rz)
        if length > 1e-6:
            rx, rz = rx / length, rz / length

        v_coord = arc_len[i] / total_len
        lx, lz = cx - rx * half_w, cz - rz * half_w
        ex, ez = cx + rx * half_w, cz + rz * half_w
        # Sample terrain per-vertex — including the welded end-caps. The fan rim
        # samples the same XZ and so lands at the same elevation, welding without
        # floating above or sinking below the stamped terrain.
        verts.append((lx, sample_elevation(hmap, lx, lz) + _RAISE, lz))  # left
        verts.append((ex, sample_elevation(hmap, ex, ez) + _RAISE, ez))  # right
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

    normals = vertex_normals(verts, indices)
    # Force welded end-caps straight up to match the flat fan exactly.
    if anchored_start:
        normals[0] = (0.0, 1.0, 0.0)
        normals[1] = (0.0, 1.0, 0.0)
    if anchored_end:
        normals[2 * (n - 1)] = (0.0, 1.0, 0.0)
        normals[2 * (n - 1) + 1] = (0.0, 1.0, 0.0)

    return verts, normals, uvs, indices
