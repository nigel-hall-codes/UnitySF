"""Tests for building classification (facts emission) and the buildings sidecar."""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from sfmap import classify
from sfmap.classify import (
    ClassificationRecord,
    StreetFacade,
    classify_building,
    footprint_hash,
    footprint_shape,
    rank_street_facades,
    _oriented_bbox,
)
from sfmap.serialize import ChunkData, write_buildings


# --- footprint_hash: normative algorithm (data-model.md §6.1) ---------------

_SQUARE = [(0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0)]


def test_footprint_hash_is_deterministic_and_8_hex():
    h = footprint_hash(_SQUARE)
    assert h == footprint_hash(list(_SQUARE))
    assert len(h) == 8
    int(h, 16)  # valid hex


def test_footprint_hash_invariant_to_start_vertex_and_winding():
    base = footprint_hash(_SQUARE)
    # Same ring, rotated to a different start vertex.
    rotated = _SQUARE[2:] + _SQUARE[:2]
    assert footprint_hash(rotated) == base
    # Same ring, reversed winding (clockwise).
    assert footprint_hash(list(reversed(_SQUARE))) == base
    # A trailing closing-duplicate vertex must not change the hash.
    assert footprint_hash(_SQUARE + [_SQUARE[0]]) == base


def test_footprint_hash_quantization_absorbs_subgrid_edits_but_not_larger():
    base = footprint_hash(_SQUARE)
    # A 0.1 m nudge (< 0.25 m grid) snaps back to the same cell → same hash.
    nudged = [(0.05, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0)]
    assert footprint_hash(nudged) == base
    # A 0.5 m move crosses a grid cell → different hash.
    moved = [(0.5, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0)]
    assert footprint_hash(moved) != base


# --- oriented bounding box --------------------------------------------------

def test_oriented_bbox_axis_aligned_rect():
    rect = [(0.0, 0.0), (10.0, 0.0), (10.0, 4.0), (0.0, 4.0)]
    long_m, short_m = _oriented_bbox(rect)
    assert round(long_m, 2) == 10.0
    assert round(short_m, 2) == 4.0


def test_oriented_bbox_recovers_dims_of_rotated_rect():
    # A 10×4 rectangle rotated 30°: the min-area box must still be ~10×4.
    import math
    c, s = math.cos(math.radians(30)), math.sin(math.radians(30))
    corners = [(0, 0), (10, 0), (10, 4), (0, 4)]
    rot = [(x * c - z * s, x * s + z * c) for x, z in corners]
    long_m, short_m = _oriented_bbox(rot)
    assert abs(long_m - 10.0) < 0.1
    assert abs(short_m - 4.0) < 0.1


# --- footprint_shape classifier ---------------------------------------------

def test_shape_square_is_rect():
    assert footprint_shape(_SQUARE) == "rect"


def test_shape_l_shape_is_L():
    # 10×10 with a 5×5 notch removed from one corner → fills 75% of its bbox.
    l = [(0, 0), (10, 0), (10, 5), (5, 5), (5, 10), (0, 10)]
    assert footprint_shape(l) == "L"


def test_shape_chamfered_square_is_corner():
    chamfered = [(0, 0), (10, 0), (10, 8), (8, 10), (0, 10)]
    assert footprint_shape(chamfered) == "corner"


def test_shape_triangle_is_irregular():
    tri = [(0, 0), (10, 0), (0, 10)]
    assert footprint_shape(tri) == "irregular"


# --- street facade ranking (data-model.md §1, design D2) --------------------

def test_facade_dedupes_to_strongest_edge_per_street():
    # A road parallel below a rectangle: bottom and top edges both face it, but the
    # nearer bottom edge must win and only one facade (one street) is reported.
    rect = [(0.0, 0.0), (10.0, 0.0), (10.0, 5.0), (0.0, 5.0)]
    road = (42, [(-5.0, -8.0), (15.0, -8.0)])
    facades = rank_street_facades(rect, [road])
    assert len(facades) == 1
    assert facades[0].edge_index == 0           # the bottom edge
    assert facades[0].street_osm_id == 42


def test_corner_building_faces_two_streets_ranked():
    # Two perpendicular roads → a corner building reports two facades (D2).
    sq = [(0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0)]
    road_a = (100, [(-5.0, -8.0), (15.0, -8.0)])   # parallel to the bottom edge
    road_b = (200, [(-8.0, -5.0), (-8.0, 15.0)])   # parallel to the left edge
    facades = rank_street_facades(sq, [road_a, road_b])
    streets = {f.street_osm_id for f in facades}
    assert streets == {100, 200}
    # Sorted strongest-first, deterministic tie-break by edge_index.
    assert facades == sorted(facades, key=lambda f: (-f.score, f.edge_index))


def test_no_facade_when_roads_far_away():
    rect = [(0.0, 0.0), (10.0, 0.0), (10.0, 5.0), (0.0, 5.0)]
    far = (1, [(0.0, 500.0), (10.0, 500.0)])
    assert rank_street_facades(rect, [far]) == []


# --- top-level classify_building --------------------------------------------

def test_classify_building_floor_count_and_passthrough():
    rec = classify_building(
        osm_id=7, footprint=_SQUARE, height=12.0, building_type="retail",
        roads=[], neighborhood="Mission",
    )
    assert rec.osm_id == 7
    assert rec.neighborhood == "Mission"
    assert rec.building_type == "retail"
    assert rec.floor_count == 4            # round(12 / 3)
    assert rec.height_m == 12.0
    assert rec.footprint_shape == "rect"
    assert len(rec.footprint_hash) == 8


def test_classify_building_defaults_missing_height():
    rec = classify_building(
        osm_id=8, footprint=_SQUARE, height=0.0, building_type=None,
        roads=[], neighborhood="",
    )
    assert rec.height_m == 10.0            # default storey-stack height
    assert rec.floor_count == 3            # round(10 / 3)
    assert rec.building_type == ""         # None → "" passthrough


def test_building_centroid_matches_average():
    cx, cz = classify.building_centroid(_SQUARE)
    assert (round(cx, 3), round(cz, 3)) == (5.0, 5.0)


# --- write_buildings sidecar (data-model.md §1) -----------------------------

def _chunk_with(records):
    return ChunkData(
        col=3, row=4, world_x=0.0, world_z=0.0, chunk_size_m=300.0,
        heightmap=None, meshes=[], buildings=records,
    )


def test_write_buildings_schema_and_sorted(tmp_path):
    recs = [
        ClassificationRecord(
            osm_id=20, neighborhood="Sunset", building_type="house",
            footprint_shape="rect", width_m=8.1, depth_m=12.4, height_m=9.0,
            floor_count=3, street_facades=[StreetFacade(1, 90.0, 555, 0.8)],
            footprint_hash="abcd1234",
        ),
        ClassificationRecord(
            osm_id=10, neighborhood="", building_type="",
            footprint_shape="irregular", width_m=5.0, depth_m=5.0, height_m=10.0,
            floor_count=3, street_facades=[], footprint_hash="0011aabb",
        ),
    ]
    out = write_buildings(_chunk_with(recs), str(tmp_path))
    assert out is not None
    assert out.name == "chunk_03_04_buildings.json"
    data = json.loads(out.read_text(encoding="utf-8"))
    assert data["version"] == 1
    ids = [b["osm_id"] for b in data["buildings"]]
    assert ids == [10, 20]                 # ascending osm_id (deterministic)
    b20 = data["buildings"][1]
    assert b20["neighborhood"] == "Sunset"
    assert b20["footprint_hash"] == "abcd1234"
    f = b20["street_facades"][0]
    assert f == {"edge_index": 1, "bearing_deg": 90.0, "street_osm_id": 555, "score": 0.8}


def test_write_buildings_none_when_empty(tmp_path):
    assert write_buildings(_chunk_with([]), str(tmp_path)) is None
    assert not list(tmp_path.glob("*_buildings.json"))
