"""Building classification — emit *facts*, never template choices (design #266).

Per building, the bake computes the classification facts Unity needs to match a
``BuildingTemplate`` and assemble parts: neighborhood, footprint shape, oriented
size, floor count, the **ranked street-facing facades**, and a ``footprint_hash``.
Python owns this because it holds the footprint, the road graph, and the
projection; Unity owns the matching. Schemas are normative in
``sdlc/#266/data-model.md`` §1 (the sidecar) and §6.1 (the hash algorithm).

Everything here is a pure function of its inputs (footprint vertices in world
metres, the candidate road centerlines, the neighborhood index), so a re-bake of
the same inputs yields byte-identical records — the determinism contract
(data-model.md §6). No randomness, no I/O.

The footprint-shape thresholds and the street-facade distance/score cutoffs are
tunable heuristics; the design (open questions) flags them as values to revisit
against real data, so they live as named module constants below.
"""
from __future__ import annotations

import hashlib
import math
from dataclasses import dataclass, field
from typing import List, Optional, Sequence, Tuple

Point = Tuple[float, float]

# --- tunable classification thresholds (design #266 open questions) ----------

# footprint_hash quantisation grid, metres (data-model.md §6.1 — part of the
# cross-component contract; changing it is a breaking sidecar-version change).
_HASH_GRID_M = 0.25

# Floors per storey (PDF: 3.0 m). floor_count = round(height_m / _FLOOR_HEIGHT_M).
_FLOOR_HEIGHT_M = 3.0
# Effective height when OSM carries none — mirrors geometry/building.py's default
# so the floor count agrees with the mass actually built (design D5 risk).
_DEFAULT_HEIGHT_M = 10.0

# A footprint vertex is a real corner only if the ring turns more than this at it;
# smaller turns are treated as collinear noise and dropped before shape analysis.
_CORNER_TURN_DEG = 20.0
# How fully the footprint fills its oriented bounding box (area ratio) to read as a
# clean rectangle, versus a notched/L shape, versus an irregular blob.
_RECT_FILL = 0.92
_L_FILL = 0.55
# A "corner" footprint is a near-rectangle with one clipped/chamfered corner: a
# short edge cutting diagonally across a bbox corner.
_CORNER_FILL = 0.80
_CHAMFER_MAX_EDGE_FRAC = 0.5     # chamfer edge is < this × the median edge length
_CHAMFER_DIAG_MIN_DEG = 25.0     # …and runs 25–65° off the nearest bbox axis
_CHAMFER_DIAG_MAX_DEG = 65.0

# A footprint edge counts as street-facing only if a road centerline passes within
# this distance of its midpoint; beyond it the road is "across the block", not a
# frontage. Score combines this proximity with parallelism to the road.
_FACADE_ROAD_DIST_M = 30.0
# Minimum combined score for an edge to be reported as a street facade at all.
_MIN_FACADE_SCORE = 0.15


@dataclass
class StreetFacade:
    """One street-facing footprint edge, scored for ranking (data-model.md §1).

    Carries the edge's world-space endpoints so the facade-decal importer (#279) can
    anchor a quad on this wall without the per-building mass (destroyed at mesh-combine).
    The wall's vertical extent is building-wide — ``ClassificationRecord.base_y`` (the
    flat foundation Y) and ``facade_height_m`` — so it is not duplicated per facade.
    """
    edge_index: int          # index into the footprint ring (closing vertex dropped)
    bearing_deg: float       # outward normal of the edge, degrees CW from world +Z
    street_osm_id: int       # osm way id of the faced road, -1 if none
    score: float             # proximity × parallelism, higher = stronger frontage
    x0: float = 0.0          # edge start vertex (world metres), = footprint[edge_index]
    z0: float = 0.0
    x1: float = 0.0          # edge end vertex (world metres), = footprint[edge_index+1]
    z1: float = 0.0


@dataclass
class ClassificationRecord:
    """The per-building facts written to chunk_CC_RR_buildings.json (data-model.md §1)."""
    osm_id: int
    neighborhood: str
    building_type: str
    footprint_shape: str             # rect | L | corner | irregular
    width_m: float                   # oriented-bbox long edge
    depth_m: float                   # oriented-bbox short edge
    height_m: float
    floor_count: int
    street_facades: List[StreetFacade] = field(default_factory=list)
    footprint_hash: str = ""
    base_y: float = 0.0              # foundation Y (mass wall flat bottom); facade decal anchor (#279)
    facade_height_m: float = 0.0     # floor_count × floor height — the canvas's facade UV height (#279)


# ---------------------------------------------------------------------------
# Footprint geometry primitives
# ---------------------------------------------------------------------------

def _drop_closing(ring: Sequence[Point]) -> List[Point]:
    """Return the ring without a trailing vertex duplicating the first.

    OSM closed ways repeat the first node as the last; the hash and edge model
    both want each vertex once. Tolerates an already-open ring.
    """
    pts = list(ring)
    if len(pts) >= 2 and pts[0] == pts[-1]:
        pts = pts[:-1]
    return pts


def _signed_area(ring: Sequence[Point]) -> float:
    """Shoelace signed area; > 0 for counter-clockwise winding (world XZ)."""
    n = len(ring)
    s = 0.0
    for i in range(n):
        x0, z0 = ring[i]
        x1, z1 = ring[(i + 1) % n]
        s += x0 * z1 - x1 * z0
    return 0.5 * s


def _convex_hull(points: Sequence[Point]) -> List[Point]:
    """Andrew's monotone chain hull, counter-clockwise, no repeated endpoint.

    Pure-Python (no scipy) so classification stays import-light and deterministic.
    Collinear hull points are dropped. Degenerate (< 3 unique) inputs return the
    unique points as-is.
    """
    pts = sorted(set(points))
    if len(pts) < 3:
        return pts

    def cross(o: Point, a: Point, b: Point) -> float:
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower: List[Point] = []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    upper: List[Point] = []
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]


def _oriented_bbox(ring: Sequence[Point]) -> Tuple[float, float]:
    """Minimum-area oriented bounding box of the footprint → (long_edge, short_edge).

    Rotating-calipers via the convex hull: the min-area enclosing rectangle of a
    convex polygon always has one side flush with a hull edge, so test each hull
    edge's orientation and keep the smallest-area box. Returns the box's longer and
    shorter side in metres (width_m, depth_m in the sidecar).
    """
    hull = _convex_hull(ring)
    if len(hull) < 3:
        # Degenerate footprint (collinear/sliver): fall back to an axis bbox.
        xs = [p[0] for p in ring]
        zs = [p[1] for p in ring]
        w, h = (max(xs) - min(xs)), (max(zs) - min(zs))
        return (max(w, h), min(w, h))

    best_area = math.inf
    best_dims = (0.0, 0.0)
    n = len(hull)
    for i in range(n):
        ax, az = hull[i]
        bx, bz = hull[(i + 1) % n]
        ex, ez = bx - ax, bz - az
        elen = math.hypot(ex, ez)
        if elen < 1e-9:
            continue
        ux, uz = ex / elen, ez / elen          # edge unit (box's long axis candidate)
        px, pz = -uz, ux                        # perpendicular
        min_u = min_v = math.inf
        max_u = max_v = -math.inf
        for hx, hz in hull:
            du = hx * ux + hz * uz
            dv = hx * px + hz * pz
            min_u, max_u = min(min_u, du), max(max_u, du)
            min_v, max_v = min(min_v, dv), max(max_v, dv)
        w, h = (max_u - min_u), (max_v - min_v)
        area = w * h
        if area < best_area:
            best_area = area
            best_dims = (max(w, h), min(w, h))
    return best_dims


def _polygon_area(ring: Sequence[Point]) -> float:
    return abs(_signed_area(ring))


def _quantize(v: float) -> float:
    """Snap a coordinate to the 0.25 m hash grid (data-model.md §6.1 step 2).

    Rounding is pinned to half-away-from-zero rather than Python's banker's
    ``round`` so the hash is reproducible across languages: the server that authors
    building-specific overrides must quantise identically (design D3), and it should
    not depend on the host's default rounding mode. Exact half-cases (a coordinate
    landing on a 0.125 m boundary) are vanishingly rare in projected float coords,
    but the contract is byte-for-byte so the tie-break is fixed here regardless.
    """
    n = v / _HASH_GRID_M
    n = math.floor(n + 0.5) if n >= 0 else math.ceil(n - 0.5)
    return n * _HASH_GRID_M + 0.0  # +0.0 collapses a possible -0.0 for stable text


def footprint_hash(ring: Sequence[Point]) -> str:
    """Normative footprint hash (data-model.md §6.1) — the override match guard.

    Computed identically by the Python bake and the authoring server, so a
    building-specific override authored against this footprint matches at Unity
    import. Steps: drop the closing vertex, quantise to a 0.25 m grid, canonicalise
    ordering (force CCW winding, then rotate to start at the lexicographically
    smallest vertex — winding *before* rotation so opposite-wound copies of the
    same ring converge), serialise ``"x.xx,z.zz;"`` per vertex, take the first 8 hex
    of SHA-256. Winding is fixed first because rotating first would leave the
    smallest vertex out of position after a reversal.
    """
    pts = [(_quantize(x), _quantize(z)) for (x, z) in _drop_closing(ring)]
    if not pts:
        return hashlib.sha256(b"").hexdigest()[:8]

    # Force counter-clockwise winding (on the quantised ring) before rotating.
    if _signed_area(pts) < 0:
        pts = list(reversed(pts))

    # Rotate to start at the lexicographically smallest (x, z) vertex.
    start = min(range(len(pts)), key=lambda i: pts[i])
    pts = pts[start:] + pts[:start]

    serialized = "".join(f"{x:.2f},{z:.2f};" for (x, z) in pts)
    return hashlib.sha256(serialized.encode("utf-8")).hexdigest()[:8]


# ---------------------------------------------------------------------------
# Footprint shape classifier
# ---------------------------------------------------------------------------

def _simplify(ring: Sequence[Point]) -> List[Point]:
    """Drop near-collinear vertices so corner counts reflect real corners.

    Keeps a vertex only when the ring turns more than ``_CORNER_TURN_DEG`` there.
    Operates on the open ring (closing vertex already dropped).
    """
    pts = _drop_closing(ring)
    n = len(pts)
    if n < 4:
        return pts
    kept: List[Point] = []
    for i in range(n):
        ax, az = pts[i - 1]
        bx, bz = pts[i]
        cx, cz = pts[(i + 1) % n]
        v1 = (bx - ax, bz - az)
        v2 = (cx - bx, cz - bz)
        l1 = math.hypot(*v1)
        l2 = math.hypot(*v2)
        if l1 < 1e-9 or l2 < 1e-9:
            continue
        cosang = max(-1.0, min(1.0, (v1[0] * v2[0] + v1[1] * v2[1]) / (l1 * l2)))
        turn = math.degrees(math.acos(cosang))
        if turn > _CORNER_TURN_DEG:
            kept.append((bx, bz))
    return kept if len(kept) >= 3 else pts


def _has_chamfer(ring: Sequence[Point]) -> bool:
    """True if a short edge cuts diagonally across a corner (a clipped corner).

    Looks for an edge markedly shorter than the median edge whose direction runs
    well off both bbox axes — the signature of a chamfered building corner.
    """
    pts = _simplify(ring)
    n = len(pts)
    if n < 5:
        return False
    lengths = []
    dirs = []
    for i in range(n):
        ax, az = pts[i]
        bx, bz = pts[(i + 1) % n]
        dx, dz = bx - ax, bz - az
        ln = math.hypot(dx, dz)
        lengths.append(ln)
        dirs.append((dx, dz, ln))
    s = sorted(lengths)
    median = s[len(s) // 2]
    if median < 1e-9:
        return False
    for dx, dz, ln in dirs:
        if ln >= _CHAMFER_MAX_EDGE_FRAC * median or ln < 1e-9:
            continue
        # Angle off the nearest axis: 0° = axis-aligned, 45° = pure diagonal.
        ang = math.degrees(math.atan2(abs(dz), abs(dx)))
        off_axis = min(ang, 90.0 - ang)
        if _CHAMFER_DIAG_MIN_DEG <= off_axis <= _CHAMFER_DIAG_MAX_DEG:
            return True
    return False


def footprint_shape(ring: Sequence[Point]) -> str:
    """Classify a footprint as ``rect | L | corner | irregular`` (heuristic).

    Driven by how fully the footprint fills its oriented bounding box plus a
    chamfer test. Thresholds are tunable (design open question): a near-bbox-filling
    quad is ``rect``; a rectangle with a clipped corner is ``corner``; a moderately
    notched shape (e.g. an L) is ``L``; anything sparser is ``irregular``.
    """
    pts = _drop_closing(ring)
    if len(pts) < 3:
        return "irregular"
    bbox_long, bbox_short = _oriented_bbox(pts)
    bbox_area = bbox_long * bbox_short
    if bbox_area < 1e-9:
        return "irregular"
    fill = _polygon_area(pts) / bbox_area
    corners = len(_simplify(pts))

    if fill >= _RECT_FILL and corners <= 4:
        return "rect"
    if fill >= _CORNER_FILL and _has_chamfer(pts):
        return "corner"
    if fill >= _L_FILL and corners <= 8:
        return "L"
    return "irregular"


# ---------------------------------------------------------------------------
# Street facade ranking
# ---------------------------------------------------------------------------

def _closest_on_polyline(
    px: float, pz: float, poly: Sequence[Point]
) -> Tuple[float, Tuple[float, float]]:
    """Distance from (px,pz) to polyline ``poly`` and the unit direction of the
    nearest segment. Returns (inf, (0,0)) for a degenerate polyline."""
    best_d2 = math.inf
    best_dir = (0.0, 0.0)
    for i in range(1, len(poly)):
        ax, az = poly[i - 1]
        bx, bz = poly[i]
        dx, dz = bx - ax, bz - az
        seg2 = dx * dx + dz * dz
        if seg2 < 1e-12:
            continue
        t = ((px - ax) * dx + (pz - az) * dz) / seg2
        t = max(0.0, min(1.0, t))
        cx, cz = ax + t * dx, az + t * dz
        d2 = (px - cx) ** 2 + (pz - cz) ** 2
        if d2 < best_d2:
            best_d2 = d2
            inv = 1.0 / math.sqrt(seg2)
            best_dir = (dx * inv, dz * inv)
    return (math.sqrt(best_d2) if best_d2 < math.inf else math.inf), best_dir


def _outward_bearing(a: Point, b: Point, ccw: bool) -> float:
    """Compass bearing (deg CW from +Z) of edge A→B's outward normal.

    For a CCW ring the interior lies left of A→B, so the outward normal is the
    right-hand normal (dz, -dx); for a CW ring it is the left-hand normal. Winding
    is passed in so edge indices stay in the footprint's original order.
    """
    dx, dz = b[0] - a[0], b[1] - a[1]
    nx, nz = (dz, -dx) if ccw else (-dz, dx)
    return (math.degrees(math.atan2(nx, nz)) + 360.0) % 360.0


def rank_street_facades(
    ring: Sequence[Point],
    roads: Sequence[Tuple[int, Sequence[Point]]],
) -> List[StreetFacade]:
    """Rank a footprint's street-facing edges, primary first (data-model.md §1, D2).

    Each footprint edge is scored against the road centerlines by proximity (its
    midpoint within ``_FACADE_ROAD_DIST_M`` of a road) × parallelism (how parallel
    the edge runs to that road). The best-scoring road per edge is kept; entries are
    then deduped to the strongest edge **per street** (so one wall split into several
    collinear OSM segments yields a single facade, while a corner building facing two
    streets yields two) and sorted by descending score. ``edge_index`` refers to the
    footprint ring with its closing vertex dropped, in OSM vertex order — the same
    order the mass mesh is built from. ``roads`` is ``(osm_way_id, centerline)`` per
    candidate road, already projected to world XZ.
    """
    pts = _drop_closing(ring)
    n = len(pts)
    if n < 3 or not roads:
        return []
    ccw = _signed_area(pts) > 0

    best_per_edge: List[Optional[StreetFacade]] = []
    for i in range(n):
        ax, az = pts[i]
        bx, bz = pts[(i + 1) % n]
        elen = math.hypot(bx - ax, bz - az)
        if elen < 1e-9:
            best_per_edge.append(None)
            continue
        edx, edz = (bx - ax) / elen, (bz - az) / elen
        mx, mz = (ax + bx) * 0.5, (az + bz) * 0.5

        best: Optional[StreetFacade] = None
        for way_id, centerline in roads:
            dist, (sdx, sdz) = _closest_on_polyline(mx, mz, centerline)
            if dist > _FACADE_ROAD_DIST_M:
                continue
            parallelism = abs(edx * sdx + edz * sdz)          # 1 = parallel, 0 = perpendicular
            proximity = 1.0 - dist / _FACADE_ROAD_DIST_M       # 1 = on the centerline
            score = proximity * parallelism
            if best is None or score > best.score:
                best = StreetFacade(
                    edge_index=i,
                    bearing_deg=round(_outward_bearing((ax, az), (bx, bz), ccw), 1),
                    street_osm_id=way_id,
                    score=score,
                    x0=round(ax, 3), z0=round(az, 3),
                    x1=round(bx, 3), z1=round(bz, 3),
                )
        best_per_edge.append(best)

    # Keep the strongest edge per street, above the score floor.
    by_street: dict = {}
    for f in best_per_edge:
        if f is None or f.score < _MIN_FACADE_SCORE:
            continue
        cur = by_street.get(f.street_osm_id)
        if cur is None or f.score > cur.score:
            by_street[f.street_osm_id] = f

    facades = list(by_street.values())
    # Deterministic order: strongest first, edge_index as a stable tie-break.
    facades.sort(key=lambda f: (-f.score, f.edge_index))
    for f in facades:
        f.score = round(f.score, 2)
    return facades


# ---------------------------------------------------------------------------
# Top-level per-building classification
# ---------------------------------------------------------------------------

def classify_building(
    osm_id: int,
    footprint: Sequence[Point],
    height: float,
    building_type: Optional[str],
    roads: Sequence[Tuple[int, Sequence[Point]]],
    neighborhood: str = "",
    base_y: float = 0.0,
) -> ClassificationRecord:
    """Assemble the full ClassificationRecord for one building (facts only).

    ``footprint`` is the projected world-XZ ring; ``height`` is OSM height (0 if
    absent → the default storey-stack height, matching the built mass); ``roads`` is
    the candidate ``(osm_way_id, centerline)`` set for street-facade scoring;
    ``neighborhood`` is resolved by the caller from the centroid (``""`` if outside
    every polygon); ``base_y`` is the mass foundation Y from the heightmap (the caller
    supplies it via ``geometry.building.building_base_y`` — the facade-decal anchor,
    #279). Pure function of these inputs.
    """
    eff_height = height if height > 0.0 else _DEFAULT_HEIGHT_M
    long_m, short_m = _oriented_bbox(_drop_closing(footprint))
    floor_count = max(1, round(eff_height / _FLOOR_HEIGHT_M))
    return ClassificationRecord(
        osm_id=osm_id,
        neighborhood=neighborhood,
        building_type=(building_type or ""),
        footprint_shape=footprint_shape(footprint),
        width_m=round(long_m, 1),
        depth_m=round(short_m, 1),
        height_m=round(eff_height, 1),
        floor_count=floor_count,
        street_facades=rank_street_facades(footprint, roads),
        footprint_hash=footprint_hash(footprint),
        base_y=round(base_y, 3),
        facade_height_m=round(floor_count * _FLOOR_HEIGHT_M, 3),
    )


def building_centroid(footprint: Sequence[Point]) -> Point:
    """Average of the footprint's distinct vertices — the point fed to the
    neighborhood point-in-polygon lookup.

    Drops the closing duplicate first, so for a closed OSM ring this differs
    microscopically from crop_to_chunk's centroid (which averages the raw ring
    including the duplicate). That only nudges the neighborhood result for a building
    sitting exactly on a boundary; it never changes which chunk classifies the
    building — chunk membership is decided solely by crop_to_chunk.
    """
    pts = _drop_closing(footprint)
    n = len(pts) or 1
    return (sum(p[0] for p in pts) / n, sum(p[1] for p in pts) / n)
