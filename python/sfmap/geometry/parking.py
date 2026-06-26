"""Parked-car placement along regulated curb segments (SF parking-regulations CSV).

The DataSF "Parking regulations" export gives a ``MULTILINESTRING`` per regulated
curb in WGS84 lon/lat. Each linestring traces the kerb where parking is allowed,
so it is a ready-made source of *real* parking locations: project the lines into
the map's world space, walk them, and drop a parked car every car-length.

Output is a list of :class:`ParkedCar` placements (position, heading, a model
selector, and the nearest street name) which the bake serialises to a per-chunk
JSON sidecar; the Unity importer instantiates a vehicle prefab per entry.

Cars are nudged off the kerb line toward the carriageway so the body sits in the
parking lane rather than straddling the kerb, and headed along the segment so
they read as parallel-parked / aligned to the sidewalk. Placement is randomised
(spacing jitter + skipped slots) but seeded by the source feature id, so a re-bake
is reproducible.
"""
from __future__ import annotations

import math
import random
import re
from dataclasses import dataclass, field
from typing import List, Optional, Sequence, Tuple

from ..elevation import HeightmapData
from ..osm import StreetEdge, StreetGraph
from ..projection import GeoOrigin, to_world_xz
from .road import _clip_polyline_to_rect, _cross_up, _sample_elevation

# The Awb low-poly vehicles are ~4.4 m long, ~2.0 m wide, but they're imported at
# half scale (see SFMapImporterWindow.ParkedCarScale), so the placement footprint
# is halved to match: ~2.25 m long, ~1.0 m wide.
_CAR_LENGTH = 2.25    # metres of kerb one parked car occupies
_CAR_GAP    = 0.4     # bumper-to-bumper gap between adjacent cars
_CAR_HALF_W = 0.5     # half the (scaled) car width
_RAISE      = 0.20    # sit on the road surface (roads/sidewalks are raised the same)

# Don't snap against a road further than this — the feature is then either stray
# data or its street isn't in this map extent. The car is still placed (on the
# kerb line) so coverage doesn't depend on the road graph.
_ROAD_SEARCH_M = 30.0
# Skip segments shorter than one car — nothing useful fits.
_MIN_SEG_LEN = _CAR_LENGTH
# Two cars closer than this (XZ, metres) are treated as a stack and one is dropped.
# Below the ~2.25 m min spacing in a row and the road-width gap between opposite
# kerbs, so only genuine overlaps (CSV vs fallback, or roads meeting) are removed.
_DEDUPE_DIST_M = 1.3


@dataclass
class ParkingSegment:
    """One regulated kerb feature, projected into world XZ."""
    object_id: int
    points: List[Tuple[float, float]]      # world-space (x, z) polyline
    neighborhood: str = ""


@dataclass
class ParkedCar:
    """One placed car. Coordinates are Unity world space (metres)."""
    x: float
    y: float
    z: float
    rot_y: float                            # heading in degrees about +Y
    model: float                            # [0,1) prefab selector (Unity maps to its list)
    street: Optional[str] = None            # nearest road name, for per-street toggling later
    source_id: int = 0                      # originating regulation feature id


# ---------------------------------------------------------------------------
# CSV parsing
# ---------------------------------------------------------------------------

_WKT_GROUP = re.compile(r"\(([^()]*)\)")


def _parse_multilinestring(wkt: str) -> List[List[Tuple[float, float]]]:
    """Parse a WKT ``MULTILINESTRING`` into a list of (lon, lat) polylines.

    Each inner ``(...)`` group is one linestring; coordinates are ``lon lat``
    pairs separated by commas. Returns ``[]`` for anything that isn't a
    multilinestring (e.g. blank shapes).
    """
    if not wkt or "MULTILINESTRING" not in wkt.upper():
        return []
    lines: List[List[Tuple[float, float]]] = []
    for group in _WKT_GROUP.findall(wkt):
        pts: List[Tuple[float, float]] = []
        for pair in group.split(","):
            parts = pair.split()
            if len(parts) < 2:
                continue
            try:
                pts.append((float(parts[0]), float(parts[1])))
            except ValueError:
                continue
        if len(pts) >= 2:
            lines.append(pts)
    return lines


def parse_parking_csv(path: str, origin: GeoOrigin) -> List[ParkingSegment]:
    """Read the parking-regulations CSV and project every kerb feature to world XZ.

    Uses the ``shape`` (WKT geometry) and ``objectid`` columns; a single feature
    with a multi-part geometry yields one ParkingSegment per part (the part index
    is folded into the object_id so seeds stay distinct).
    """
    import csv

    segments: List[ParkingSegment] = []
    with open(path, newline="", encoding="utf-8") as f:
        reader = csv.DictReader(f)
        for row in reader:
            shape = row.get("shape", "")
            lines = _parse_multilinestring(shape)
            if not lines:
                continue
            try:
                base_id = int(row.get("objectid") or 0)
            except ValueError:
                base_id = 0
            neighborhood = (row.get("analysis_neighborhood") or "").strip()
            for part, line in enumerate(lines):
                world = [to_world_xz(lon, lat, origin) for lon, lat in line]
                segments.append(ParkingSegment(
                    object_id=base_id * 100 + part,
                    points=world,
                    neighborhood=neighborhood,
                ))
    return segments


# ---------------------------------------------------------------------------
# Geometry helpers
# ---------------------------------------------------------------------------

def _closest_point_on_segment(
    px: float, pz: float, ax: float, az: float, bx: float, bz: float
) -> Tuple[float, float, float]:
    """Closest point on segment AB to P, returned as (x, z, squared_distance)."""
    abx, abz = bx - ax, bz - az
    denom = abx * abx + abz * abz
    if denom < 1e-12:
        dx, dz = px - ax, pz - az
        return ax, az, dx * dx + dz * dz
    t = ((px - ax) * abx + (pz - az) * abz) / denom
    t = max(0.0, min(1.0, t))
    cx, cz = ax + t * abx, az + t * abz
    dx, dz = px - cx, pz - cz
    return cx, cz, dx * dx + dz * dz


def _nearest_road(
    graph: StreetGraph, x: float, z: float
) -> Tuple[Optional[StreetEdge], float, Optional[Tuple[float, float]]]:
    """Return (edge, distance, closest_point) for the road nearest to (x, z)."""
    best_edge: Optional[StreetEdge] = None
    best_pt: Optional[Tuple[float, float]] = None
    best_d2 = float("inf")
    for edge in graph.edges:
        cl = edge.centerline
        for i in range(len(cl) - 1):
            cx, cz, d2 = _closest_point_on_segment(x, z, cl[i][0], cl[i][1], cl[i + 1][0], cl[i + 1][1])
            if d2 < best_d2:
                best_d2 = d2
                best_edge = edge
                best_pt = (cx, cz)
    return best_edge, math.sqrt(best_d2) if best_edge else float("inf"), best_pt


def _project_on_edge(edge: StreetEdge, x: float, z: float) -> Tuple[float, float]:
    """Closest point on ``edge``'s centerline to (x, z)."""
    best_pt = (edge.centerline[0][0], edge.centerline[0][1])
    best_d2 = float("inf")
    cl = edge.centerline
    for i in range(len(cl) - 1):
        cx, cz, d2 = _closest_point_on_segment(x, z, cl[i][0], cl[i][1], cl[i + 1][0], cl[i + 1][1])
        if d2 < best_d2:
            best_d2 = d2
            best_pt = (cx, cz)
    return best_pt


def _arc_lengths(poly: Sequence[Tuple[float, float]]) -> List[float]:
    arc = [0.0] * len(poly)
    for i in range(1, len(poly)):
        dx = poly[i][0] - poly[i - 1][0]
        dz = poly[i][1] - poly[i - 1][1]
        arc[i] = arc[i - 1] + math.hypot(dx, dz)
    return arc


def _sample_polyline(
    poly: Sequence[Tuple[float, float]], arc: Sequence[float], s: float
) -> Tuple[float, float, float, float]:
    """Point and unit forward direction at arc-length ``s`` along the polyline.

    Returns (x, z, fwd_x, fwd_z).
    """
    # Find the segment containing s.
    i = 1
    while i < len(arc) - 1 and arc[i] < s:
        i += 1
    a, b = poly[i - 1], poly[i]
    seg_len = arc[i] - arc[i - 1]
    t = 0.0 if seg_len < 1e-9 else (s - arc[i - 1]) / seg_len
    x = a[0] + t * (b[0] - a[0])
    z = a[1] + t * (b[1] - a[1])
    fx, fz = b[0] - a[0], b[1] - a[1]
    flen = math.hypot(fx, fz)
    if flen < 1e-9:
        fx, fz = 1.0, 0.0
    else:
        fx, fz = fx / flen, fz / flen
    return x, z, fx, fz


# ---------------------------------------------------------------------------
# Placement
# ---------------------------------------------------------------------------

def _dedupe(cars: List[ParkedCar], min_dist: float) -> List[ParkedCar]:
    """Drop any car within ``min_dist`` (XZ) of one already kept, first-wins.

    CSV cars are appended before fallback cars, so this gives CSV placement
    priority: a fallback car that lands on the same kerb spot as a CSV car (the
    CSV segment spans several split road-edges, so fallback re-covers part of it)
    is removed, as are fallback-vs-fallback overlaps where two roads meet. The
    threshold sits well below the ~2.25 m minimum spacing of a legitimate row and
    the road-width gap between opposite kerbs, so real cars are never dropped.
    """
    from collections import defaultdict
    grid: dict = defaultdict(list)
    kept: List[ParkedCar] = []
    md2 = min_dist * min_dist
    for c in cars:
        gx, gz = int(c.x // min_dist), int(c.z // min_dist)
        clash = False
        for dx in (-1, 0, 1):
            for dz in (-1, 0, 1):
                for k in grid[(gx + dx, gz + dz)]:
                    if (k.x - c.x) ** 2 + (k.z - c.z) ** 2 < md2:
                        clash = True
                        break
                if clash:
                    break
            if clash:
                break
        if not clash:
            kept.append(c)
            grid[(gx, gz)].append(c)
    return kept


def place_parked_cars(
    segments: Sequence[ParkingSegment],
    graph: StreetGraph,
    hmap: HeightmapData,
    x_min: float,
    z_min: float,
    size: float,
    fill: float = 0.85,
    sidewalk_fallback: bool = False,
) -> List[ParkedCar]:
    """Place parked cars along the parking segments that fall inside this chunk.

    The kerb feature gives *where along* the street (and which side, and the
    street name); the actual lateral position is snapped to the rendered road:
    each car is projected onto the nearest road centerline and pushed back out,
    on the kerb's side, to just inside the road edge — so it parks against the
    kerb on the road side rather than wherever the (imprecise) kerb line falls.
    ``hmap`` provides per-car ground elevation. Segments are clipped to the chunk
    rect so a kerb crossing a seam contributes cars to each chunk without
    duplication. ``fill`` is the probability a candidate slot gets a car
    (1.0 = bumper-to-bumper; the issue asked for ~0.85, dense).

    When ``sidewalk_fallback`` is set, streets that no kerb feature covers also get
    cars — placed along the road's sidewalk edges (both sides) — so areas the
    regulations CSV omits (unregulated residential, park-adjacent blocks) aren't
    left empty. CSV placement wins where it exists, so the two never double-stack.
    """
    x_max, z_max = x_min + size, z_min + size
    slot = _CAR_LENGTH + _CAR_GAP
    jitter = _CAR_GAP * 0.5
    cars: List[ParkedCar] = []

    for seg in segments or ():
        clipped = _clip_polyline_to_rect(seg.points, x_min, z_min, x_max, z_max)
        if len(clipped) < 2:
            continue
        arc = _arc_lengths(clipped)
        total = arc[-1]
        if total < _MIN_SEG_LEN:
            continue

        # Pick the road this kerb belongs to once (nearest to the segment midpoint),
        # so all its cars share a street and snap to the same carriageway.
        mid_x, mid_z, _, _ = _sample_polyline(clipped, arc, total * 0.5)
        edge, road_dist, _ = _nearest_road(graph, mid_x, mid_z)
        use_road = edge is not None and road_dist <= _ROAD_SEARCH_M
        street = edge.name if use_road else None
        half_w = edge.width * 0.5 if use_road else 0.0

        rng = random.Random(seg.object_id)
        # Start half a car in (+ jitter) so cars don't cluster at clipped seam ends.
        s = _CAR_LENGTH * 0.5 + rng.uniform(0.0, slot)
        while s < total - _CAR_LENGTH * 0.5:
            if rng.random() <= fill:
                x, z, fx, fz = _sample_polyline(clipped, arc, s)
                cx, cz = x, z

                if use_road:
                    # Project onto the road, then step back out toward the kerb side
                    # to just inside the road edge: the car body sits in the road,
                    # hugging the kerb. side_dir = unit(kerb_point - road_point).
                    rpx, rpz = _project_on_edge(edge, x, z)
                    sdx, sdz = x - rpx, z - rpz
                    slen = math.hypot(sdx, sdz)
                    if slen > 1e-6:
                        sdx, sdz = sdx / slen, sdz / slen
                        # Outer (kerb-side) edge of the car at the road edge (half_w);
                        # centre is one car-half-width inside. Floor keeps it sane on
                        # very narrow roads.
                        dist = max(half_w - _CAR_HALF_W, half_w * 0.5)
                        cx = rpx + sdx * dist
                        cz = rpz + sdz * dist

                y = _sample_elevation(hmap, cx, cz) + _RAISE
                rot_y = math.degrees(math.atan2(fx, fz))  # Unity +Z forward → heading
                cars.append(ParkedCar(
                    x=cx, y=y, z=cz,
                    rot_y=rot_y,
                    model=rng.random(),
                    street=street,
                    source_id=seg.object_id,
                ))
            s += slot + rng.uniform(-jitter, jitter)

    if sidewalk_fallback:
        _place_along_roads(graph, hmap, x_min, z_min, size, fill, cars)
        # CSV cars come first, so dedupe keeps them and drops fallback cars that
        # re-cover the same kerb (CSV segments span several split road-edges) or
        # collide where two roads meet.
        cars = _dedupe(cars, _DEDUPE_DIST_M)

    return cars


def _place_along_roads(
    graph: StreetGraph,
    hmap: HeightmapData,
    x_min: float,
    z_min: float,
    size: float,
    fill: float,
    cars: List[ParkedCar],
) -> None:
    """Append cars along both sidewalk edges of every road in the chunk.

    Walks each road centerline (clipped to the chunk rect so seam roads aren't
    double-populated) and drops cars on each side at the road edge, hugging the
    kerb — the same road-side seating the CSV path produces. The two rows face
    opposite ways, as parallel-parked cars do on a two-way street. Seeded per
    (edge, side) so re-bakes are reproducible. Overlap with CSV cars (and between
    roads at a junction) is resolved by the caller's dedupe pass.
    """
    x_max, z_max = x_min + size, z_min + size
    slot = _CAR_LENGTH + _CAR_GAP
    jitter = _CAR_GAP * 0.5

    for edge in graph.edges:
        if edge.width <= 0.0:
            continue
        clipped = _clip_polyline_to_rect(edge.centerline, x_min, z_min, x_max, z_max)
        if len(clipped) < 2:
            continue
        arc = _arc_lengths(clipped)
        total = arc[-1]
        if total < _MIN_SEG_LEN:
            continue

        half_w = edge.width * 0.5
        dist = max(half_w - _CAR_HALF_W, half_w * 0.5)  # centre just inside the road edge
        street = edge.name

        for side in (1.0, -1.0):
            # Deterministic per edge+side; the +1 keeps the two sides' RNGs distinct.
            rng = random.Random((abs(edge.osm_way_id) << 1) + (1 if side > 0 else 0))
            s = _CAR_LENGTH * 0.5 + rng.uniform(0.0, slot)
            while s < total - _CAR_LENGTH * 0.5:
                if rng.random() <= fill:
                    x, z, fx, fz = _sample_polyline(clipped, arc, s)
                    rx, _, rz = _cross_up((fx, 0.0, fz))   # right of travel, unit XZ
                    cx = x + rx * side * dist
                    cz = z + rz * side * dist
                    y = _sample_elevation(hmap, cx, cz) + _RAISE
                    # Each side faces with its own kerb's traffic → opposite headings.
                    rot_y = math.degrees(math.atan2(fx, fz)) + (180.0 if side < 0 else 0.0)
                    cars.append(ParkedCar(
                        x=cx, y=y, z=cz,
                        rot_y=rot_y,
                        model=rng.random(),
                        street=street,
                        source_id=abs(edge.osm_way_id),
                    ))
                s += slot + rng.uniform(-jitter, jitter)
