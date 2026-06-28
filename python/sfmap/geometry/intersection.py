"""shapely intersection polygons (offset road end-caps) + boundary setbacks."""
from __future__ import annotations

import math
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple

from shapely.geometry import Polygon

from ..elevation import HeightmapData
from ..osm import StreetEdge, StreetGraph, StreetNode
from .road import _sample_elevation

_BEVEL_THRESHOLD = 5.0   # metres; miter points beyond this become two-vertex bevels
_RAISE = 0.20            # metres; clears bilinear bleed, matches road.py
_SW_WIDTH = 1.5          # metres; sidewalk strip width, matches sidewalk.py
_MIN_CORNER_GAP_DEG = 30.0  # skip corner fills for near-parallel arm pairs


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
    centre, in CCW order as seen from above (Unity +Y up).

    The polygon is assembled from each road's *end-cap corners* at the same
    per-arm setback distance that ``compute_boundaries`` hands the road
    generator. For an arm with direction ``d``, half-width ``hw`` and setback
    ``t`` along the centerline, the two corners are::

        R = d * t - perp * hw     # clockwise (right) edge of the road end
        L = d * t + perp * hw     # counter-clockwise (left) edge

    where ``perp = (-d_z, d_x)`` is ``d`` rotated +90°. Emitting ``R`` then
    ``L`` for each arm in CCW order produces a polygon whose straight edges
    (``R_i → L_i``) coincide exactly with where each road ribbon stops — so
    the junction is watertight — and whose vertices are angularly ordered, so
    the polygon is simple by construction (no self-intersecting "stars").

    Genuinely degenerate junctions (two arms within a few degrees, where no
    non-overlapping planar fill exists) can still produce a self-touching ring;
    those fall back to the convex hull of the same corner points, which is
    always valid and visually indistinguishable since the arms nearly merge.
    """
    result: Dict[int, Polygon] = {}
    for node in graph.intersection_nodes:
        edge_arms, setbacks = _arms_and_setbacks(node, graph)
        if len(edge_arms) < 2:
            continue

        pts: List[Tuple[float, float]] = []
        for ea, t in zip(edge_arms, setbacks):
            a = ea.arm
            perp_x, perp_z = -a.dir_z, a.dir_x
            cx, cz = a.dir_x * t, a.dir_z * t
            pts.append((cx - perp_x * a.half_width, cz - perp_z * a.half_width))  # R
            pts.append((cx + perp_x * a.half_width, cz + perp_z * a.half_width))  # L

        pts = _dedupe_ring(pts)
        if len(pts) < 3:
            continue

        poly = Polygon(pts)
        if not poly.is_valid or not poly.is_simple:
            poly = Polygon(pts).convex_hull
            if not isinstance(poly, Polygon) or poly.is_empty:
                continue

        result[node.osm_id] = poly

    return result


def compute_boundaries(
    graph: StreetGraph,
    polygons: Dict[int, Polygon],
) -> Dict[Tuple[int, int, int], Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]]:
    """Phase 2 — compute road-endpoint setback positions from intersection polygons.

    Returns a dict keyed by (osm_way_id, from_node_id, to_node_id). Each value is a
    (from_xz, to_xz) pair; an endpoint is None when the edge doesn't connect to an
    intersection at that end (dead-end stub — road runs all the way to the node centre).
    Coordinates are absolute world-space (x, z).
    """
    result: Dict[Tuple[int, int, int], Tuple[Optional[Tuple[float, float]], Optional[Tuple[float, float]]]] = {}

    for node in graph.intersection_nodes:
        if node.osm_id not in polygons:
            continue

        edge_arms, per_arm = _arms_and_setbacks(node, graph)
        if len(edge_arms) < 2:
            continue

        for i, ea in enumerate(edge_arms):
            t = per_arm[i]
            bx = node.world_x + ea.arm.dir_x * t
            bz = node.world_z + ea.arm.dir_z * t
            boundary = (bx, bz)

            key = (ea.edge.osm_way_id, ea.edge.from_node.osm_id, ea.edge.to_node.osm_id)
            existing = result.get(key, (None, None))
            if ea.is_from:
                result[key] = (boundary, existing[1])
            else:
                result[key] = (existing[0], boundary)

    return result


# ---------------------------------------------------------------------------
# Mesh geometry helpers (used by road/sidewalk generator)
# ---------------------------------------------------------------------------

def triangulate_fan(
    center_x: float,
    center_z: float,
    hmap: "HeightmapData",
    polygon: Polygon,
) -> Tuple[List[Tuple[float, float, float]], List[Tuple[float, float, float]], List[Tuple[float, float]], List[int]]:
    """Fan-triangulate an intersection polygon into mesh arrays.

    Returns (vertices_xyz, normals_xyz, uvs_uv, indices). Polygon coords are XZ
    offsets from the node centre.

    **Welding.** Each rim corner sits at the exact XZ of the road end-cap it
    meets (same setback + half-width formula) and samples the *same* terrain, so
    the fan and the roads share those vertices in position — no Z-gap, and the
    fan tracks the stamped terrain across a sloped junction instead of floating
    flat at the single node elevation (which left a step/seam against the
    terrain-following roads). Every normal is forced straight up (0, 1, 0) to
    match the roads' welded end-caps, so the shared edge shades continuously
    rather than seaming between independently-recalculated meshes; a junction is
    near-flat enough that flat-up shading is imperceptible.

    Winding and apex keep every triangle visible (front-facing):

    * **Consistent winding, matching the roads.** A perimeter of *positive* XZ
      signed area faces up the same way the road ribbons do (Unity is
      left-handed, so this is the opposite of the textbook right-handed sign).
      We reverse the ring when needed so the sign is always positive.
    * **Apex inside the polygon.** The fan apex is the polygon *centroid*, not
      the node centre — for a node whose roads all leave to one side the node
      centre can fall outside the polygon, which flips some triangles. All
      intersection polygons here are convex, so the centroid is always interior.
    """
    pts = list(polygon.exterior.coords[:-1])  # drop repeated closing vertex
    n = len(pts)
    if n < 3:
        return [], [], [], []

    # Orient so the fan faces up (+Y), the same handedness as the road ribbons.
    # Signed area = Σ(x_i·z_{i+1} − x_{i+1}·z_i); positive is front-facing here.
    area = sum(pts[i][0] * pts[(i + 1) % n][1] - pts[(i + 1) % n][0] * pts[i][1]
               for i in range(n))
    if area < 0.0:
        pts = pts[::-1]

    max_r = max(math.hypot(p[0], p[1]) for p in pts)
    if max_r < 0.001:
        max_r = 1.0

    apex_x = sum(p[0] for p in pts) / n
    apex_z = sum(p[1] for p in pts) / n

    apex_y = _sample_elevation(hmap, center_x + apex_x, center_z + apex_z) + _RAISE
    verts: List[Tuple[float, float, float]] = [(center_x + apex_x, apex_y, center_z + apex_z)]
    uvs: List[Tuple[float, float]] = [(0.5, 0.5)]
    indices: List[int] = []

    for ox, oz in pts:
        wx, wz = center_x + ox, center_z + oz
        wy = _sample_elevation(hmap, wx, wz) + _RAISE
        verts.append((wx, wy, wz))
        uvs.append((ox / (2.0 * max_r) + 0.5, oz / (2.0 * max_r) + 0.5))

    for i in range(n):
        j = (i + 1) % n
        indices += [0, j + 1, i + 1]

    normals = [(0.0, 1.0, 0.0)] * len(verts)
    return verts, normals, uvs, indices


def build_sidewalk_corner_meshes(
    graph: StreetGraph,
    polygons: Dict[int, Polygon],
    hmap: "HeightmapData",
) -> Dict[int, Tuple[List[Tuple[float, float, float]], List[Tuple[float, float]], List[int]]]:
    """Generate sidewalk corner-fill quads at each intersection.

    For each pair of adjacent arms at a node, fills the gap between the two
    sidewalk strip ends with a flat quad.  Without this, sidewalk strips
    terminate with squared-off ends, leaving a bare triangle of terrain at
    every intersection corner.

    Each quad covers the area between:
      - the left outer sidewalk edge of arm A (the CCW arm of the pair)
      - the right outer sidewalk edge of arm B (the CW arm of the pair)
      - the two road-edge inner corners (which coincide with the polygon boundary)

    Near-parallel arm pairs (gap < _MIN_CORNER_GAP_DEG) are skipped: the two
    inner corners are nearly coincident in that case and the outer corners end
    up on opposite sides of the polygon, producing a huge degenerate strip
    rather than a small corner fill.

    Returns a dict keyed by node osm_id -> (vertices, uvs, indices).
    Emitted with MeshType.SIDEWALK so the C# importer applies sidewalk material.
    """
    result: Dict[int, Tuple[list, list, list]] = {}

    for node in graph.intersection_nodes:
        if node.osm_id not in polygons:
            continue
        edge_arms, setbacks = _arms_and_setbacks(node, graph)
        if len(edge_arms) < 2:
            continue

        verts: List[Tuple[float, float, float]] = []
        uvs: List[Tuple[float, float]] = []
        indices: List[int] = []
        n = len(edge_arms)

        for i in range(n):
            j = (i + 1) % n
            a = edge_arms[i].arm
            b = edge_arms[j].arm
            ta = setbacks[i]
            tb = setbacks[j]

            # Skip near-parallel pairs: outer corners land far apart, wrapping
            # around the wrong side of the intersection.
            gap_deg = (math.degrees(b.angle - a.angle)) % 360.0
            if gap_deg < _MIN_CORNER_GAP_DEG:
                continue

            # Left perpendicular of arm A (CCW side, faces toward the gap).
            pa_x = -a.dir_z
            pa_z =  a.dir_x
            # Right perpendicular of arm B (CW side, faces toward the gap).
            pb_x =  b.dir_z
            pb_z = -b.dir_x

            hw_a = a.half_width
            hw_b = b.half_width
            outer_a = hw_a + _SW_WIDTH
            outer_b = hw_b + _SW_WIDTH

            # Inner corners = polygon boundary corners (road edge at setback).
            li_ax = node.world_x + a.dir_x * ta + pa_x * hw_a
            li_az = node.world_z + a.dir_z * ta + pa_z * hw_a
            ri_bx = node.world_x + b.dir_x * tb + pb_x * hw_b
            ri_bz = node.world_z + b.dir_z * tb + pb_z * hw_b

            # Outer corners = sidewalk outer edges at same setback depth.
            lo_ax = node.world_x + a.dir_x * ta + pa_x * outer_a
            lo_az = node.world_z + a.dir_z * ta + pa_z * outer_a
            ro_bx = node.world_x + b.dir_x * tb + pb_x * outer_b
            ro_bz = node.world_z + b.dir_z * tb + pb_z * outer_b

            li_ay = _sample_elevation(hmap, li_ax, li_az) + _RAISE
            lo_ay = _sample_elevation(hmap, lo_ax, lo_az) + _RAISE
            ri_by = _sample_elevation(hmap, ri_bx, ri_bz) + _RAISE
            ro_by = _sample_elevation(hmap, ro_bx, ro_bz) + _RAISE

            # Sort the four corners into CW order (front-facing from above in Unity
            # left-handed coords) by angle from their centroid.  A fixed vertex
            # ordering (li,lo,ro,ri) forms a self-intersecting butterfly when the
            # inner corners are close and the outer corners diverge, so we always
            # sort dynamically.
            corners3d = [
                (li_ax, li_ay, li_az),
                (lo_ax, lo_ay, lo_az),
                (ri_bx, ri_by, ri_bz),
                (ro_bx, ro_by, ro_bz),
            ]
            cx4 = (li_ax + lo_ax + ri_bx + ro_bx) * 0.25
            cz4 = (li_az + lo_az + ri_bz + ro_bz) * 0.25
            corners3d.sort(key=lambda p: -math.atan2(p[2] - cz4, p[0] - cx4))

            # Verify the quad is non-degenerate (area >= 0.1 m²) before emitting.
            (ax, _, az), (bx, _, bz), (cx, _, cz), (dx, _, dz) = corners3d
            area2 = abs(
                (ax * (bz - dz) + bx * (cz - az) + cx * (dz - bz) + dx * (az - cz))
            )
            if area2 < 0.2:  # area2 = 2*area; threshold at 0.1 m²
                continue

            offset = len(verts)
            verts.extend(corners3d)
            uvs.extend([(0.0, 0.0), (1.0, 0.0), (1.0, 1.0), (0.0, 1.0)])
            indices.extend([offset, offset + 1, offset + 2,
                            offset, offset + 2, offset + 3])

        if verts and indices:
            result[node.osm_id] = (verts, uvs, indices)

    return result


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


def _dedupe_ring(pts: List[Tuple[float, float]], eps: float = 1e-4) -> List[Tuple[float, float]]:
    """Drop consecutive (and wrap-around) coincident points from a polygon ring.

    At a clean miter the left corner of one arm and the right corner of the
    next coincide exactly; without this the fan triangulator would emit a
    zero-area triangle there.
    """
    out: List[Tuple[float, float]] = []
    for p in pts:
        if not out or math.hypot(p[0] - out[-1][0], p[1] - out[-1][1]) > eps:
            out.append(p)
    if len(out) >= 2 and math.hypot(out[0][0] - out[-1][0], out[0][1] - out[-1][1]) <= eps:
        out.pop()
    return out


def _arms_and_setbacks(
    node: StreetNode, graph: StreetGraph
) -> Tuple[List[_EdgeArm], List[float]]:
    """Return this node's CCW-sorted arms and the per-arm centerline setback.

    Each arm's setback is the larger of the two requirements from its
    neighbouring joins, so adjacent road end-caps don't overlap. This is the
    single source of truth shared by ``compute_polygons`` (corner placement)
    and ``compute_boundaries`` (where the road generator stops each ribbon),
    which is what keeps the two watertight against each other.
    """
    edge_arms = _collect_edge_arms(node, graph)
    if len(edge_arms) < 2:
        return edge_arms, []
    edge_arms.sort(key=lambda ea: ea.arm.angle)

    n = len(edge_arms)
    per_arm = [0.0] * n
    for i in range(n):
        j = (i + 1) % n
        t_a, t_b = _join_setbacks(edge_arms[i].arm, edge_arms[j].arm)
        per_arm[i] = max(per_arm[i], t_a)
        per_arm[j] = max(per_arm[j], t_b)
    return edge_arms, per_arm


def _join_setbacks(a: _Arm, b: _Arm) -> Tuple[float, float]:
    """Return setback distances (t_A, t_B) along each arm to the polygon boundary."""
    pa_x = -a.dir_z * a.half_width
    pa_z =  a.dir_x * a.half_width
    pb_x =  b.dir_z * b.half_width
    pb_z = -b.dir_x * b.half_width

    # The bevel only exists to tame the runaway miter spike of two *near-parallel*
    # arms, so its trigger radius must sit outside the carriageways, not inside
    # them. A fixed 5 m threshold beveled legitimate ~perpendicular corners of any
    # street wider than ~7 m (half-width > 5/√2 ≈ 3.5 m): a 90° join of two
    # half-width-hw arms miters at radius hw·√2 ≤ hw_a + hw_b, so a constant 5 m
    # clamped that real corner to a smaller setback than the road's own half-width.
    # That pulled each road end inside its neighbour, collapsed the corner points,
    # and forced compute_polygons onto its convex-hull fallback — which no longer
    # welds to the carriageways, leaving the paving gap at the junction (#224).
    # Scaling the radius with the two half-widths keeps real perpendicular/obtuse
    # corners on the miter (watertight) and still bevels only genuine acute spikes.
    bevel_radius = max(_BEVEL_THRESHOLD, a.half_width + b.half_width)
    bevel_a = math.sqrt(max(0.0, bevel_radius ** 2 - a.half_width ** 2))
    bevel_b = math.sqrt(max(0.0, bevel_radius ** 2 - b.half_width ** 2))

    det = -a.dir_x * b.dir_z + a.dir_z * b.dir_x

    if abs(det) < 1e-6:
        return bevel_a, bevel_b

    dx = pb_x - pa_x
    dz = pb_z - pa_z
    t = (-dx * b.dir_z + dz * b.dir_x) / det
    s = ( a.dir_x * dz - a.dir_z * dx) / det

    miter_x = pa_x + t * a.dir_x
    miter_z = pa_z + t * a.dir_z
    if math.hypot(miter_x, miter_z) <= bevel_radius:
        return max(0.0, t), max(0.0, s)

    return bevel_a, bevel_b
