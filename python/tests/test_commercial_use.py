"""Tests for the per-building commercial-use signal (#486).

Covers the three pieces that produce ``ClassificationRecord.use``: the tag
predicates (``sfmap.tags``), the POI → footprint association (``sfmap.poi``), and
the enum derivation (``sfmap.classify.building_use``).
"""
import pytest

from sfmap import poi, tags
from sfmap.classify import (
    USE_COMMERCIAL,
    USE_MIXED,
    USE_RESIDENTIAL,
    USE_UNKNOWN,
    building_use,
    classify_building,
)

# --- tags.is_commercial_poi -------------------------------------------------

@pytest.mark.parametrize("t", [
    {"shop": "bakery"},
    {"shop": "vacant"},                 # an empty storefront is still a storefront
    {"amenity": "restaurant"},
    {"amenity": "cafe", "name": "Philz"},
    {"office": "insurance"},
    {"craft": "brewery"},
    {"tourism": "hotel"},
    {"leisure": "fitness_centre"},
])
def test_commercial_poi_recognised(t):
    assert tags.is_commercial_poi(t) is True

@pytest.mark.parametrize("t", [
    {},
    {"building": "yes"},
    {"amenity": "bench"},               # street furniture
    {"amenity": "parking"},
    {"amenity": "school"},              # institutional, not commercial
    {"amenity": "place_of_worship"},
    {"shop": "no"},
    {"office": "no"},
    {"tourism": "viewpoint"},
    {"leisure": "park"},
])
def test_non_commercial_poi_rejected(t):
    assert tags.is_commercial_poi(t) is False

# --- tags.building_use_tag --------------------------------------------------

def test_building_yes_asserts_nothing():
    # The 93%-of-San-Francisco case: the tag says a building exists, not what it is.
    assert tags.building_use_tag({"building": "yes"}) == ""

@pytest.mark.parametrize("value", ["retail", "commercial", "supermarket", "office", "hotel"])
def test_commercial_building_values(value):
    assert tags.building_use_tag({"building": value}) == "commercial"

@pytest.mark.parametrize("value", ["house", "apartments", "residential", "terrace"])
def test_residential_building_values(value):
    assert tags.building_use_tag({"building": value}) == "residential"

def test_commercial_wins_over_residential_on_the_same_way():
    # building=apartments + shop=* is a mixed-use block; the floor count resolves it.
    assert tags.building_use_tag({"building": "apartments", "shop": "florist"}) == "commercial"

def test_building_use_key_is_read():
    assert tags.building_use_tag({"building": "yes", "building:use": "retail"}) == "commercial"
    assert tags.building_use_tag({"building": "yes", "building:use": "residential"}) == "residential"

def test_ancillary_structures_are_not_residences():
    for value in ("garage", "garages", "shed", "roof", "carport"):
        assert tags.building_use_tag({"building": value}) == ""

# --- poi.commercial_poi_counts ----------------------------------------------

_UNIT_SQUARE = [(0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0)]
_FAR_SQUARE = [(500.0, 500.0), (510.0, 500.0), (510.0, 510.0), (500.0, 510.0)]

def test_counts_pois_inside_the_footprint():
    counts = poi.commercial_poi_counts(
        [_UNIT_SQUARE, _FAR_SQUARE],
        [(5.0, 5.0), (1.0, 9.0), (505.0, 505.0), (-40.0, -40.0)],
    )
    assert counts == [2, 1]

def test_poi_outside_every_footprint_is_dropped():
    assert poi.commercial_poi_counts([_UNIT_SQUARE], [(50.0, 50.0)]) == [0]

def test_poi_spanning_grid_cells_is_still_found():
    # A footprint far larger than the 50 m index cell must still match a POI at its centre.
    big = [(0.0, 0.0), (300.0, 0.0), (300.0, 300.0), (0.0, 300.0)]
    assert poi.commercial_poi_counts([big], [(150.0, 150.0)]) == [1]

def test_overlapping_footprints_credit_one_building_only():
    # A building mapped twice must not double-count its shop.
    inner = [(2.0, 2.0), (8.0, 2.0), (8.0, 8.0), (2.0, 8.0)]
    counts = poi.commercial_poi_counts([_UNIT_SQUARE, inner], [(5.0, 5.0)])
    assert counts == [1, 0]
    assert sum(counts) == 1

def test_closing_vertex_and_degenerate_rings_are_tolerated():
    closed = _UNIT_SQUARE + [_UNIT_SQUARE[0]]
    assert poi.commercial_poi_counts([closed], [(5.0, 5.0)]) == [1]
    assert poi.commercial_poi_counts([[(0.0, 0.0), (1.0, 1.0)]], [(0.5, 0.5)]) == [0]

def test_empty_inputs():
    assert poi.commercial_poi_counts([], [(1.0, 1.0)]) == []
    assert poi.commercial_poi_counts([_UNIT_SQUARE], []) == [0]

# --- classify.building_use --------------------------------------------------

def test_no_evidence_degrades_to_unknown():
    # building=yes with nothing inside it: the data does not say. Not "residential".
    assert building_use("", 0, 3) == USE_UNKNOWN

def test_residential_tag_with_no_commercial_evidence():
    assert building_use("residential", 0, 3) == USE_RESIDENTIAL

def test_single_storey_with_a_shop_is_all_commercial():
    assert building_use("", 1, 1) == USE_COMMERCIAL
    assert building_use("residential", 1, 1) == USE_COMMERCIAL

def test_multi_storey_bare_building_with_a_shop_is_mixed():
    # The canonical SF block: storefront at floor 0, flats above.
    assert building_use("", 1, 3) == USE_MIXED

def test_multi_storey_residential_with_a_shop_is_mixed():
    assert building_use("residential", 2, 4) == USE_MIXED

def test_commercial_way_tag_is_commercial_at_every_floor():
    assert building_use("commercial", 0, 6) == USE_COMMERCIAL
    assert building_use("commercial", 3, 6) == USE_COMMERCIAL

def test_commercial_and_mixed_both_mean_ground_floor_commercial():
    # The property storefront rules gate on — asserted so it can't silently drift.
    for way_use, count, floors in (("", 1, 1), ("", 1, 4), ("residential", 1, 4),
                                   ("commercial", 0, 3)):
        assert building_use(way_use, count, floors) in (USE_COMMERCIAL, USE_MIXED)

# --- end-to-end through classify_building -----------------------------------

def test_classify_building_carries_the_use_signal():
    rec = classify_building(
        1, _UNIT_SQUARE, 12.0, "yes", roads=[], way_use="", commercial_poi_count=2,
    )
    assert rec.use == USE_MIXED
    assert rec.commercial_poi_count == 2

def test_classify_building_defaults_to_unknown_without_signal_inputs():
    rec = classify_building(1, _UNIT_SQUARE, 12.0, "yes", roads=[])
    assert rec.use == USE_UNKNOWN
    assert rec.commercial_poi_count == 0

# --- end-to-end through the OSM parser --------------------------------------

# A three-storey building=yes footprint with a bakery node inside it (the SF
# mapping style this whole feature exists for), a bench node outside it (must not
# count), and a street so the file is a plausible extract.
_MINI_OSM = """<?xml version='1.0' encoding='UTF-8'?>
<osm version="0.6" generator="test">
  <bounds minlat="37.7500" minlon="-122.4300" maxlat="37.7520" maxlon="-122.4270"/>
  <node id="1" lat="37.75050" lon="-122.42950"/>
  <node id="2" lat="37.75050" lon="-122.42900"/>
  <node id="3" lat="37.75100" lon="-122.42900"/>
  <node id="4" lat="37.75100" lon="-122.42950"/>
  <node id="5" lat="37.75075" lon="-122.42925"><tag k="shop" v="bakery"/></node>
  <node id="6" lat="37.75150" lon="-122.42800"><tag k="amenity" v="bench"/></node>
  <node id="10" lat="37.75020" lon="-122.42950"/>
  <node id="11" lat="37.75020" lon="-122.42750"/>
  <way id="100">
    <nd ref="1"/><nd ref="2"/><nd ref="3"/><nd ref="4"/><nd ref="1"/>
    <tag k="building" v="yes"/><tag k="building:levels" v="3"/>
  </way>
  <way id="200">
    <nd ref="10"/><nd ref="11"/>
    <tag k="highway" v="residential"/><tag k="name" v="Test Street"/>
  </way>
</osm>
"""

def test_parser_attributes_a_contained_shop_node_to_its_building(tmp_path):
    from sfmap import osm

    path = tmp_path / "mini.osm"
    path.write_text(_MINI_OSM, encoding="utf-8")
    graph = osm.parse(str(path))

    assert len(graph.buildings) == 1
    b = graph.buildings[0]
    assert b.building_type == "yes"
    assert b.way_use == ""                  # the way's own tags say nothing
    assert b.commercial_poi_count == 1      # …but the bakery inside it does
    # 3 levels × 3.5 m → floor_count 4, so this reads as a mixed-use block.
    rec = classify_building(
        b.osm_id, b.footprint, b.height, b.building_type, roads=[],
        way_use=b.way_use, commercial_poi_count=b.commercial_poi_count,
    )
    assert rec.use == USE_MIXED
