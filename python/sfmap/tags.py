"""OSM tag heuristics — the semantic interpretation of raw OSM tags.

Pure functions/tables that turn OSM key/value tags into the bake's domain values
(road class, width, lane count, one-way direction, parking permission, building
height). Extracted from ``osm.py`` (#425) so the parser (node/way/graph assembly)
is separate from the tag-meaning heuristics. Dependency-free — imports nothing
from the rest of ``sfmap`` — so ``osm.py`` can depend on it without a cycle.
"""
from __future__ import annotations

from enum import Enum
from typing import Dict, List, Optional, Tuple

# Highway tag values treated as driveable roads.
ROAD_HIGHWAY_VALUES = frozenset({
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


HIGHWAY_WIDTHS: Dict[HighwayType, float] = {
    HighwayType.PRIMARY: 10.0,
    HighwayType.SECONDARY: 9.0,
    HighwayType.TERTIARY: 8.0,
    HighwayType.RESIDENTIAL: 7.0,
    HighwayType.SERVICE: 4.0,
    HighwayType.UNCLASSIFIED: 6.0,
    HighwayType.FOOTWAY: 0.0,
}

# Width allotted per traffic lane, in meters. Used when an edge carries an
# explicit OSM `lanes` count; otherwise width falls back to HIGHWAY_WIDTHS.
LANE_WIDTH = 3.5

# Highway classes you never park on. These reach us as road edges (they're in
# ROAD_HIGHWAY_VALUES) but carry no parking, so parked-car placement must skip
# them — and they're not in HIGHWAY_TYPE_MAP, so they'd otherwise be mistaken
# for plain unclassified streets and get a parked-car row.
NO_PARKING_HIGHWAYS = frozenset({
    "motorway", "motorway_link", "trunk", "trunk_link",
})
# OSM `parking:*` / `parking:lane:*` values that forbid parking on a side.
NO_PARKING_TAG_VALUES = frozenset({"no", "no_parking", "no_stopping"})


HIGHWAY_TYPE_MAP: Dict[str, HighwayType] = {
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


def is_road(tags: Dict[str, str]) -> bool:
    return tags.get("highway") in ROAD_HIGHWAY_VALUES


def road_width(highway_type: HighwayType, lanes: Optional[int]) -> float:
    """Road width in metres: explicit lane count × lane width, else class default."""
    if lanes is not None:
        return lanes * LANE_WIDTH
    return HIGHWAY_WIDTHS.get(highway_type, 6.0)


def road_allows_parking(tags: Dict[str, str], highway: str) -> bool:
    """Whether parked cars belong on this road, from OSM tags alone.

    False for motorway/trunk (and their links) — you never park on a freeway —
    and for explicit OSM parking tags that forbid it on *both* sides. Conservative
    by design: a lone `parking:left=no` (with the other side unknown) still allows
    parking, so only an unambiguous both-sides "no" excludes the road. Everything
    unspecified defaults to allowed, preserving the existing placement behaviour.
    """
    if highway in NO_PARKING_HIGHWAYS:
        return False
    if (tags.get("parking:both") or tags.get("parking:lane:both") or "").strip().lower() \
            in NO_PARKING_TAG_VALUES:
        return False

    def _side(*keys: str) -> Optional[str]:
        for k in keys:
            v = (tags.get(k) or "").strip().lower()
            if v:
                return v
        return None

    left = _side("parking:left", "parking:lane:left")
    right = _side("parking:right", "parking:lane:right")
    if left in NO_PARKING_TAG_VALUES and right in NO_PARKING_TAG_VALUES:
        return False
    return True


def parse_lanes(raw: Optional[str]) -> Optional[int]:
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


def parse_oneway(
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


def parse_building_height(tags: Dict[str, str]) -> float:
    """Building height in metres from OSM tags, or 0.0 when unknown.

    ``building:levels`` (floor count × 3.5 m/floor) is preferred over an explicit
    ``height`` tag; both fall back to 0.0 on a missing or unparseable value.
    """
    height = 0.0
    lvl_str = tags.get("building:levels")
    h_str = tags.get("height")
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
    return height
