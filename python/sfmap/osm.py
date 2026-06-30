"""pyosmium parser → StreetGraph dataclass."""
from __future__ import annotations

from dataclasses import dataclass, field
from enum import Enum
from typing import Dict, List, Optional, Tuple

from .projection import GeoOrigin, OsmBounds, to_world_xz

try:
    import osmium
    _HAS_OSMIUM = True
except ImportError:  # pragma: no cover
    _HAS_OSMIUM = False

# Highway tag values treated as driveable roads.
_ROAD_HIGHWAY_VALUES = frozenset({
    "motorway", "motorway_link", "trunk", "trunk_link",
    "primary", "primary_link", "secondary", "secondary_link",
    "tertiary", "tertiary_link", "residential", "living_street",
    "service", "unclassified", "road",
})


class HighwayType(Enum):
    RESIDENTIAL = "residential"
    PRIMARY = "primary"
    SECONDARY = "secondary"
    TERTIARY = "tertiary"
    SERVICE = "service"
    FOOTWAY = "footway"
    UNCLASSIFIED = "unclassified"


_HIGHWAY_WIDTHS: Dict[HighwayType, float] = {
    HighwayType.PRIMARY: 10.0,
    HighwayType.SECONDARY: 9.0,
    HighwayType.TERTIARY: 8.0,
    HighwayType.RESIDENTIAL: 7.0,
    HighwayType.SERVICE: 4.0,
    HighwayType.UNCLASSIFIED: 6.0,
    HighwayType.FOOTWAY: 0.0,
}

# Width allotted per traffic lane, in meters. Used when an edge carries an
# explicit OSM `lanes` count; otherwise width falls back to _HIGHWAY_WIDTHS.
_LANE_WIDTH = 3.5

# Highway classes you never park on. These reach us as road edges (they're in
# _ROAD_HIGHWAY_VALUES) but carry no parking, so parked-car placement must skip
# them — and they're not in _HIGHWAY_TYPE_MAP, so they'd otherwise be mistaken
# for plain unclassified streets and get a parked-car row.
_NO_PARKING_HIGHWAYS = frozenset({
    "motorway", "motorway_link", "trunk", "trunk_link",
})
# OSM `parking:*` / `parking:lane:*` values that forbid parking on a side.
_NO_PARKING_TAG_VALUES = frozenset({"no", "no_parking", "no_stopping"})


def _road_allows_parking(tags: Dict[str, str], highway: str) -> bool:
    """Whether parked cars belong on this road, from OSM tags alone.

    False for motorway/trunk (and their links) — you never park on a freeway —
    and for explicit OSM parking tags that forbid it on *both* sides. Conservative
    by design: a lone `parking:left=no` (with the other side unknown) still allows
    parking, so only an unambiguous both-sides "no" excludes the road. Everything
    unspecified defaults to allowed, preserving the existing placement behaviour.
    """
    if highway in _NO_PARKING_HIGHWAYS:
        return False
    if (tags.get("parking:both") or tags.get("parking:lane:both") or "").strip().lower() \
            in _NO_PARKING_TAG_VALUES:
        return False

    def _side(*keys: str) -> Optional[str]:
        for k in keys:
            v = (tags.get(k) or "").strip().lower()
            if v:
                return v
        return None

    left = _side("parking:left", "parking:lane:left")
    right = _side("parking:right", "parking:lane:right")
    if left in _NO_PARKING_TAG_VALUES and right in _NO_PARKING_TAG_VALUES:
        return False
    return True


def _parse_lanes(raw: Optional[str]) -> Optional[int]:
    """Parse an OSM `lanes` tag into a positive lane count, or None.

    OSM values are usually a plain integer ("2"), but the tag can also carry
    a decimal ("1.5") or a `;`-separated list ("2;3" for direction splits).
    Take the first numeric token, round to the nearest whole lane, and reject
    anything non-positive or unparseable.
    """
    if not raw:
        return None
    token = raw.split(";")[0].strip()
    try:
        lanes = int(round(float(token)))
    except ValueError:
        return None
    return lanes if lanes > 0 else None


def _parse_oneway(
    tags: Dict[str, str], highway: str, node_refs: List[int]
) -> Tuple[bool, List[int]]:
    """Resolve a way's one-way status and node order in the legal travel direction.

    Returns ``(is_one_way, node_refs)`` where ``node_refs`` is reordered so that,
    for a one-way road, index order runs in the direction traffic is allowed to
    flow — downstream code takes from_node = node_refs[0] → to_node = node_refs[-1].

    Handles the explicit ``oneway`` tag (``yes``/``true``/``1`` forward, ``-1``
    reversed relative to node order, ``no``/``false``/``0`` two-way) and the
    implicit one-ways OSM defines through other tags: roundabouts and motorways
    are one-way even when the ``oneway`` tag is absent.
    """
    val = (tags.get("oneway") or "").strip().lower()
    if val == "-1":
        # Travel runs against node order; flip so node order == travel direction.
        return True, list(reversed(node_refs))
    if val in ("yes", "true", "1"):
        return True, node_refs
    if val in ("no", "false", "0"):
        return False, node_refs
    # Implicit one-ways: OSM treats these as oneway=yes when the tag is omitted.
    if tags.get("junction") == "roundabout":
        return True, node_refs
    if highway in ("motorway", "motorway_link"):
        return True, node_refs
    return False, node_refs


_HIGHWAY_TYPE_MAP: Dict[str, HighwayType] = {
    "primary": HighwayType.PRIMARY,
    "primary_link": HighwayType.PRIMARY,
    "secondary": HighwayType.SECONDARY,
    "secondary_link": HighwayType.SECONDARY,
    "tertiary": HighwayType.TERTIARY,
    "tertiary_link": HighwayType.TERTIARY,
    "residential": HighwayType.RESIDENTIAL,
    "living_street": HighwayType.RESIDENTIAL,
    "service": HighwayType.SERVICE,
    "footway": HighwayType.FOOTWAY,
    "path": HighwayType.FOOTWAY,
    "pedestrian": HighwayType.FOOTWAY,
}


class IntersectionType(Enum):
    STOP_SIGN = "stop_sign"
    TRAFFIC_SIGNALS = "traffic_signals"


@dataclass
class StreetNode:
    osm_id: int
    world_x: float
    world_z: float
    is_intersection: bool = False
    traffic_control: Optional[IntersectionType] = None

    @property
    def world_xz(self) -> Tuple[float, float]:
        return (self.world_x, self.world_z)


@dataclass
class StreetEdge:
    osm_way_id: int
    from_node: StreetNode
    to_node: StreetNode
    highway_type: HighwayType
    is_one_way: bool
    centerline: List[Tuple[float, float]]
    name: Optional[str] = None
    lanes: Optional[int] = None
    is_driveway: bool = False   # service=driveway — a vehicle access throat, not a street
    allows_parking: bool = True  # False on freeways/trunks & OSM parking:*=no — no parked cars

    @property
    def width(self) -> float:
        if self.lanes is not None:
            return self.lanes * _LANE_WIDTH
        return _HIGHWAY_WIDTHS.get(self.highway_type, 6.0)


@dataclass
class BuildingWay:
    osm_id: int
    footprint: List[Tuple[float, float]]
    height: float
    building_type: Optional[str] = None   # OSM building=* value (residential, apartments, …)
    footprint_hash: str = ""              # set in chunk.py after classification (data-model.md §6.1)


@dataclass
class StreetGraph:
    source_bounds: OsmBounds
    origin: GeoOrigin
    nodes: Dict[int, StreetNode] = field(default_factory=dict)
    edges: List[StreetEdge] = field(default_factory=list)
    buildings: List[BuildingWay] = field(default_factory=list)
    adjacency: Dict[int, List[StreetEdge]] = field(default_factory=dict)

    @property
    def intersection_nodes(self) -> List[StreetNode]:
        return [n for n in self.nodes.values() if n.is_intersection]

    def crop_to_chunk(
        self, x_min: float, z_min: float, x_max: float, z_max: float
    ) -> "StreetGraph":
        """Return a new StreetGraph filtered to elements overlapping the rect.

        Edges are kept if any part of their centerline passes through the rect —
        not merely if their centroid lands inside it. A long road spanning several
        chunks must appear in *every* chunk it crosses; the road/sidewalk
        generators then clip its centerline to the chunk's exact bounds
        (``_clip_polyline_to_rect``), so each chunk renders its own portion with
        no overlap and no gap. Keying on the centroid alone assigned the whole
        edge to a single chunk and dropped the rest, leaving roads that stopped
        short of intersections in neighbouring chunks (#164). Buildings render
        whole (no clipping), so they stay keyed on their footprint centroid.
        """

        def in_rect(x: float, z: float) -> bool:
            return x_min <= x <= x_max and z_min <= z <= z_max

        def centroid(pts: List[Tuple[float, float]]) -> Tuple[float, float]:
            n = len(pts)
            return sum(p[0] for p in pts) / n, sum(p[1] for p in pts) / n

        kept_edges = [
            e for e in self.edges
            if _polyline_intersects_rect(e.centerline, x_min, z_min, x_max, z_max)
        ]

        kept_ids: set = set()
        for e in kept_edges:
            kept_ids.add(e.from_node.osm_id)
            kept_ids.add(e.to_node.osm_id)
        for n in self.nodes.values():
            if n.is_intersection and in_rect(n.world_x, n.world_z):
                kept_ids.add(n.osm_id)

        kept_nodes = {nid: self.nodes[nid] for nid in kept_ids if nid in self.nodes}
        kept_buildings = [b for b in self.buildings if in_rect(*centroid(b.footprint))]

        adj: Dict[int, List[StreetEdge]] = {}
        for e in kept_edges:
            adj.setdefault(e.from_node.osm_id, []).append(e)
            adj.setdefault(e.to_node.osm_id, []).append(e)

        return StreetGraph(
            source_bounds=self.source_bounds,
            origin=self.origin,
            nodes=kept_nodes,
            edges=kept_edges,
            buildings=kept_buildings,
            adjacency=adj,
        )


# ---------------------------------------------------------------------------
# Internal raw data holders
# ---------------------------------------------------------------------------

@dataclass
class _RawNode:
    node_id: int
    lat: float
    lon: float
    tags: Dict[str, str]


@dataclass
class _RawWay:
    way_id: int
    node_refs: List[int]
    tags: Dict[str, str]


# ---------------------------------------------------------------------------
# pyosmium handler
# ---------------------------------------------------------------------------

if _HAS_OSMIUM:
    class _OsmHandler(osmium.SimpleHandler):
        def __init__(self) -> None:
            super().__init__()
            self.raw_nodes: Dict[int, _RawNode] = {}
            self.raw_ways: List[_RawWay] = []

        def node(self, n: osmium.osm.Node) -> None:
            if not n.location.valid():
                return
            self.raw_nodes[n.id] = _RawNode(
                node_id=n.id,
                lat=n.location.lat,
                lon=n.location.lon,
                tags={t.k: t.v for t in n.tags},
            )

        def way(self, w: osmium.osm.Way) -> None:
            refs = [nd.ref for nd in w.nodes]
            if len(refs) < 2:
                return
            self.raw_ways.append(_RawWay(
                way_id=w.id,
                node_refs=refs,
                tags={t.k: t.v for t in w.tags},
            ))


# ---------------------------------------------------------------------------
# Public parse entry point
# ---------------------------------------------------------------------------

def parse(osm_path: str) -> StreetGraph:
    """Parse an .osm or .osm.pbf file and return a projected StreetGraph.

    Requires pyosmium. Raises ImportError if the package is not installed.
    Falls back to the stdlib XML parser only for .osm files when pyosmium is
    absent.
    """
    if _HAS_OSMIUM:
        return _parse_with_osmium(osm_path)
    if osm_path.endswith(".pbf"):
        raise ImportError("pyosmium is required to parse .osm.pbf files. Install with: pip install pyosmium")
    return _parse_xml_fallback(osm_path)


def _parse_with_osmium(osm_path: str) -> StreetGraph:
    bounds = _read_bounds_osmium(osm_path)

    handler = _OsmHandler()
    handler.apply_file(osm_path, locations=True)

    return _build_graph(bounds, handler.raw_nodes, handler.raw_ways)


def _read_bounds_osmium(osm_path: str) -> OsmBounds:
    reader = osmium.io.Reader(osm_path)
    box = reader.header().box()
    reader.close()

    if box.valid():
        return OsmBounds(
            min_lat=box.bottom_left.lat,
            max_lat=box.top_right.lat,
            min_lon=box.bottom_left.lon,
            max_lon=box.top_right.lon,
        )
    # Header bounds absent (common in hand-cropped PBF exports); compute from nodes.
    # We'll return a sentinel and fix up after node parsing.
    return None  # type: ignore[return-value]


def _parse_xml_fallback(osm_path: str) -> StreetGraph:
    """Pure-stdlib XML parser for .osm files when pyosmium is unavailable."""
    import xml.etree.ElementTree as ET

    tree = ET.parse(osm_path)
    root = tree.getroot()

    bounds_el = root.find("bounds")
    if bounds_el is None:
        raise ValueError(f"No <bounds> element found in {osm_path}")
    bounds = OsmBounds(
        min_lat=float(bounds_el.get("minlat")),
        max_lat=float(bounds_el.get("maxlat")),
        min_lon=float(bounds_el.get("minlon")),
        max_lon=float(bounds_el.get("maxlon")),
    )

    raw_nodes: Dict[int, _RawNode] = {}
    for el in root.iter("node"):
        nid = int(el.get("id"))
        lat = el.get("lat")
        lon = el.get("lon")
        if lat is None or lon is None:
            continue
        tags = {t.get("k"): t.get("v") for t in el.iter("tag")}
        raw_nodes[nid] = _RawNode(node_id=nid, lat=float(lat), lon=float(lon), tags=tags)

    raw_ways: List[_RawWay] = []
    for el in root.iter("way"):
        wid = int(el.get("id"))
        refs = [int(nd.get("ref")) for nd in el.iter("nd")]
        if len(refs) < 2:
            continue
        tags = {t.get("k"): t.get("v") for t in el.iter("tag")}
        raw_ways.append(_RawWay(way_id=wid, node_refs=refs, tags=tags))

    return _build_graph(bounds, raw_nodes, raw_ways)


# ---------------------------------------------------------------------------
# Graph construction (shared between both parsers)
# ---------------------------------------------------------------------------

def _build_graph(
    bounds: Optional[OsmBounds],
    raw_nodes: Dict[int, _RawNode],
    raw_ways: List[_RawWay],
) -> StreetGraph:
    highway_ways = [w for w in raw_ways if _is_road(w.tags)]
    building_ways = [w for w in raw_ways if "building" in w.tags]

    # Derive bounds from node extents if header didn't supply them.
    if bounds is None:
        lats = [n.lat for n in raw_nodes.values()]
        lons = [n.lon for n in raw_nodes.values()]
        bounds = OsmBounds(
            min_lat=min(lats), max_lat=max(lats),
            min_lon=min(lons), max_lon=max(lons),
        )

    origin = GeoOrigin.from_bounds(bounds)

    # Count how many distinct highway ways each node appears in → intersections.
    ref_counts: Dict[int, int] = {}
    for w in highway_ways:
        seen: set = set()
        for nid in w.node_refs:
            if nid not in seen:
                seen.add(nid)
                ref_counts[nid] = ref_counts.get(nid, 0) + 1

    # Project street nodes.
    street_nodes: Dict[int, StreetNode] = {}
    for nid, count in ref_counts.items():
        raw = raw_nodes.get(nid)
        if raw is None:
            continue
        is_signal = raw.tags.get("highway") == "traffic_signals"
        is_intersection = count >= 2
        wx, wz = to_world_xz(raw.lon, raw.lat, origin)
        street_nodes[nid] = StreetNode(
            osm_id=nid,
            world_x=wx,
            world_z=wz,
            is_intersection=is_intersection,
            traffic_control=(
                IntersectionType.TRAFFIC_SIGNALS if is_intersection and is_signal
                else IntersectionType.STOP_SIGN if is_intersection
                else None
            ),
        )

    # Build edges (split ways at intersections).
    edges: List[StreetEdge] = []
    for w in highway_ways:
        hw_str = w.tags.get("highway", "unclassified")
        hw_type = _HIGHWAY_TYPE_MAP.get(hw_str, HighwayType.UNCLASSIFIED)
        is_one_way, node_refs = _parse_oneway(w.tags, hw_str, w.node_refs)
        way_lanes = _parse_lanes(w.tags.get("lanes"))
        is_driveway = w.tags.get("service") == "driveway"
        allows_parking = _road_allows_parking(w.tags, hw_str)

        way_name = w.tags.get("name") or None
        for segment in _split_at_intersections(node_refs, street_nodes):
            if len(segment) < 2:
                continue
            centerline = []
            valid = True
            for nid in segment:
                node = street_nodes.get(nid)
                if node is None:
                    valid = False
                    break
                centerline.append((node.world_x, node.world_z))
            if not valid or len(centerline) < 2:
                continue

            edges.append(StreetEdge(
                osm_way_id=w.way_id,
                from_node=street_nodes[segment[0]],
                to_node=street_nodes[segment[-1]],
                highway_type=hw_type,
                is_one_way=is_one_way,
                centerline=centerline,
                name=way_name,
                lanes=way_lanes,
                is_driveway=is_driveway,
                allows_parking=allows_parking,
            ))

    # Build adjacency.
    adjacency: Dict[int, List[StreetEdge]] = {}
    for e in edges:
        adjacency.setdefault(e.from_node.osm_id, []).append(e)
        adjacency.setdefault(e.to_node.osm_id, []).append(e)

    # Build buildings.
    buildings: List[BuildingWay] = []
    for w in building_ways:
        footprint = []
        for nid in w.node_refs:
            raw = raw_nodes.get(nid)
            if raw is None:
                continue
            footprint.append(to_world_xz(raw.lon, raw.lat, origin))
        if len(footprint) < 3:
            continue

        height = 0.0
        lvl_str = w.tags.get("building:levels")
        h_str = w.tags.get("height")
        if lvl_str is not None:
            try:
                height = float(lvl_str) * 3.5
            except ValueError:
                pass
        elif h_str is not None:
            try:
                height = float(h_str)
            except ValueError:
                pass

        buildings.append(BuildingWay(
            osm_id=w.way_id, footprint=footprint, height=height,
            building_type=w.tags.get("building"),
        ))

    return StreetGraph(
        source_bounds=bounds,
        origin=origin,
        nodes=street_nodes,
        edges=edges,
        buildings=buildings,
        adjacency=adjacency,
    )


def _split_at_intersections(
    node_refs: List[int], street_nodes: Dict[int, StreetNode]
) -> List[List[int]]:
    """Split a way's node list into segments that begin and end at intersections or endpoints."""
    segments: List[List[int]] = []
    current: List[int] = [node_refs[0]]

    for i in range(1, len(node_refs)):
        nid = node_refs[i]
        current.append(nid)
        is_last = i == len(node_refs) - 1
        node = street_nodes.get(nid)
        if not is_last and node is not None and node.is_intersection:
            segments.append(current)
            current = [nid]

    if len(current) >= 2:
        segments.append(current)

    return segments


def _is_road(tags: Dict[str, str]) -> bool:
    return tags.get("highway") in _ROAD_HIGHWAY_VALUES


def _polyline_intersects_rect(
    cl: List[Tuple[float, float]],
    x_min: float, z_min: float, x_max: float, z_max: float,
) -> bool:
    """True if any part of the XZ polyline lies within [x_min,x_max]×[z_min,z_max].

    Liang-Barsky parametric clip per segment — matches the segment test in
    ``road._clip_polyline_to_rect``, so the chunks an edge is *kept* in agree
    exactly with the chunks its centerline clips to a non-empty portion.
    """
    for i in range(1, len(cl)):
        p0x, p0z = cl[i - 1]
        p1x, p1z = cl[i]
        dx, dz = p1x - p0x, p1z - p0z
        t0, t1 = 0.0, 1.0
        inside = True
        for p, d, lo, hi in ((p0x, dx, x_min, x_max), (p0z, dz, z_min, z_max)):
            if abs(d) < 1e-10:
                if p < lo or p > hi:
                    inside = False
                    break
            else:
                ta, tb = (lo - p) / d, (hi - p) / d
                if ta > tb:
                    ta, tb = tb, ta
                t0, t1 = max(t0, ta), min(t1, tb)
                if t0 > t1:
                    inside = False
                    break
        if inside:
            return True
    return False
