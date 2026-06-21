"""shapely miter/bevel intersection polygons + boundary setbacks."""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple

from shapely.geometry import Polygon

from ..osm import StreetEdge, StreetGraph, StreetNode

_BEVEL_THRESHOLD = 5.0   # metres; miter points beyond this become two-vertex bevels
_RAISE = 0.05            # metres; matches road generator to prevent z-fighting


@dataclass
class _Arm:
    dir_x: float
    dir_z: float
    half_width: float
    angle: float  # atan2(z, x) for CCW sort


@dataclass
class _EdgeArm:
    arm: _Arm
    edge: StreetEdge
    is_from: bool


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def compute_polygons(graph: StreetGraph) -> Dict[int, Polygon]:
    """Phase 1 — build a Shapely Polygon for every intersection node.

    Keys are node osm_ids. Polygon vertices are XZ offsets from the node
    centre, stored in CCW order as seen from above (Unity +Y up).
    """
    result: Dict[int, Polygon] = {}
    for node in graph.intersection_nodes:
        arms = _collect_arms(node, graph)
        if len(arms) < 2:
            continue
        arms.sort(key=lambda a: a.angle)

        pts: List[Tuple[float, float]] = []
        n = len(arms)
        for i in range(n):
            _compute_join(arms[i], arms[(i + 1) % n], pts)

        if len(pts) >= 3:
            result[node.osm_id] = Polygon(pts)

    return result


def compute_boundaries(
    graph: StreetGraph,
    polygons: Dict[int, Polygon],
) -> Dict[int, Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]]:
    """Phase 2 — compute road-endpoint setback positions from intersection polygons.

    Returns a dict keyed by edge osm_way_id. Each value is a (from_xz, to_xz)
    pair; an endpoint is None when the edge doesn't connect to an intersection
    at that end (dead-end stub — road runs all the way to the node centre).
    Coordinates are absolute world-space (x, z).
    """
    result: Dict[int, Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]] = {}

    for node in graph.intersection_nodes:
        if node.osm_id not in polygons:
            continue

        edge_arms = _collect_edge_arms(node, graph)
        if len(edge_arms) < 2:
            continue
        edge_arms.sort(key=lambda ea: ea.arm.angle)

        n = len(edge_arms)
        per_arm = [0.0] * n

        for i in range(n):
            j = (i + 1) % n
            t_a, t_b = _join_setbacks(edge_arms[i].arm, edge_arms[j].arm)
            per_arm[i] = max(per_arm[i], t_a)
            per_arm[j] = max(per_arm[j], t_b)

        for i, ea in enumerate(edge_arms):
            t = per_arm[i]
            bx = node.world_x + ea.arm.dir_x * t
            bz = node.world_z + ea.arm.dir_z * t
            boundary = (bx, bz)

            existing = result.get(ea.edge.osm_way_id, (None, None))
            if ea.is_from:
                result[ea.edge.osm_way_id] = (boundary, existing[1])
            else:
                result[ea.edge.osm_way_id] = (existing[0], boundary)

    return result


# ---------------------------------------------------------------------------
# Mesh geometry helpers (used by road/sidewalk generator)
# ---------------------------------------------------------------------------

def triangulate_fan(
    center_x: float,
    center_z: float,
    center_y: float,
    polygon: Polygon,
) -> Tuple[List[Tuple[float, float, float]], List[Tuple[float, float]], List[int]]:
    """Fan-triangulate an intersection polygon into mesh arrays.

    Returns (vertices_xyz, uvs_uv, indices) in Unity CW winding.
    Polygon coords are XZ offsets from the node centre.
    """
    pts = list(polygon.exterior.coords[:-1])  # drop repeated closing vertex
    n = len(pts)
    if n < 3:
        return [], [], []

    max_r = max(math.hypot(p[0], p[1]) for p in pts)
    if max_r < 0.001:
        max_r = 1.0

    verts: List[Tuple[float, float, float]] = [(center_x, center_y, center_z)]
    uvs: List[Tuple[float, float]] = [(0.5, 0.5)]
    indices: List[int] = []

    for ox, oz in pts:
        verts.append((center_x + ox, center_y + _RAISE, center_z + oz))
        uvs.append((ox / (2.0 * max_r) + 0.5, oz / (2.0 * max_r) + 0.5))

    for i in range(n):
        j = (i + 1) % n
        # CW winding: centre → next → current
        indices += [0, j + 1, i + 1]

    return verts, uvs, indices


# ---------------------------------------------------------------------------
# Internal helpers
# ---------------------------------------------------------------------------

def _collect_edge_arms(node: StreetNode, graph: StreetGraph) -> List[_EdgeArm]:
    arms: List[_EdgeArm] = []
    node_edges = graph.adjacency.get(node.osm_id, [])
    for edge in node_edges:
        if edge.width <= 0.0:
            continue
        cl = edge.centerline
        if len(cl) < 2:
            continue
        if edge.from_node.osm_id == node.osm_id:
            dx = cl[1][0] - cl[0][0]
            dz = cl[1][1] - cl[0][1]
            is_from = True
        elif edge.to_node.osm_id == node.osm_id:
            dx = cl[-2][0] - cl[-1][0]
            dz = cl[-2][1] - cl[-1][1]
            is_from = False
        else:
            continue
        length = math.hypot(dx, dz)
        if length < 1e-4:
            continue
        nx, nz = dx / length, dz / length
        arms.append(_EdgeArm(
            arm=_Arm(dir_x=nx, dir_z=nz, half_width=edge.width * 0.5, angle=math.atan2(nz, nx)),
            edge=edge,
            is_from=is_from,
        ))
    return arms


def _collect_arms(node: StreetNode, graph: StreetGraph) -> List[_Arm]:
    return [ea.arm for ea in _collect_edge_arms(node, graph)]


def _compute_join(a: _Arm, b: _Arm, pts: List[Tuple[float, float]]) -> None:
    """Append 1 miter or 2 bevel vertices for the gap between arms A and B (CCW order)."""
    # Left perpendicular of A  = (-az, ax)
    # Right perpendicular of B = ( bz, -bx)
    pa_x = -a.dir_z * a.half_width
    pa_z =  a.dir_x * a.half_width
    pb_x =  b.dir_z * b.half_width
    pb_z = -b.dir_x * b.half_width

    det = -a.dir_x * b.dir_z + a.dir_z * b.dir_x
    if abs(det) < 1e-6:
        miter_x = (pa_x + pb_x) * 0.5
        miter_z = (pa_z + pb_z) * 0.5
    else:
        dx = pb_x - pa_x
        dz = pb_z - pa_z
        t = (-dx * b.dir_z + dz * b.dir_x) / det
        miter_x = pa_x + t * a.dir_x
        miter_z = pa_z + t * a.dir_z

    if math.hypot(miter_x, miter_z) <= _BEVEL_THRESHOLD:
        pts.append((miter_x, miter_z))
    else:
        t_a = math.sqrt(max(0.0, _BEVEL_THRESHOLD ** 2 - a.half_width ** 2))
        t_b = math.sqrt(max(0.0, _BEVEL_THRESHOLD ** 2 - b.half_width ** 2))
        pts.append((pa_x + a.dir_x * t_a, pa_z + a.dir_z * t_a))
        pts.append((pb_x + b.dir_x * t_b, pb_z + b.dir_z * t_b))


def _join_setbacks(a: _Arm, b: _Arm) -> Tuple[float, float]:
    """Return setback distances (t_A, t_B) along each arm to the polygon boundary."""
    pa_x = -a.dir_z * a.half_width
    pa_z =  a.dir_x * a.half_width
    pb_x =  b.dir_z * b.half_width
    pb_z = -b.dir_x * b.half_width

    det = -a.dir_x * b.dir_z + a.dir_z * b.dir_x
    bevel_a = math.sqrt(max(0.0, _BEVEL_THRESHOLD ** 2 - a.half_width ** 2))
    bevel_b = math.sqrt(max(0.0, _BEVEL_THRESHOLD ** 2 - b.half_width ** 2))

    if abs(det) < 1e-6:
        return bevel_a, bevel_b

    dx = pb_x - pa_x
    dz = pb_z - pa_z
    t = (-dx * b.dir_z + dz * b.dir_x) / det
    s = ( a.dir_x * dz - a.dir_z * dx) / det

    miter_x = pa_x + t * a.dir_x
    miter_z = pa_z + t * a.dir_z
    if math.hypot(miter_x, miter_z) <= _BEVEL_THRESHOLD:
        return max(0.0, t), max(0.0, s)

    return bevel_a, bevel_b
