"""Tests for the neighborhood point-in-polygon lookup and the vendored GeoJSON."""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from sfmap.geometry.neighborhood import (
    NeighborhoodIndex,
    NeighborhoodPolygon,
    _point_in_ring,
    load_neighborhoods,
)
from sfmap.projection import GeoOrigin, OsmBounds, to_world_xz

_DATA = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "data", "sf_analysis_neighborhoods.geojson",
)

# An SF-wide origin so the vendored polygons project into a sane world space; the
# exact centre is immaterial to containment as long as the test points use the same one.
_ORIGIN = GeoOrigin.from_bounds(
    OsmBounds(min_lat=37.70, max_lat=37.83, min_lon=-122.52, max_lon=-122.35)
)


# --- algorithm, independent of real data -----------------------------------

def _square(cx, cz, half):
    return [(cx - half, cz - half), (cx + half, cz - half),
            (cx + half, cz + half), (cx - half, cz + half)]


def test_point_in_ring_inside_and_outside():
    ring = _square(0.0, 0.0, 10.0)
    assert _point_in_ring(0.0, 0.0, ring) is True
    assert _point_in_ring(9.9, 9.9, ring) is True
    assert _point_in_ring(10.1, 0.0, ring) is False
    assert _point_in_ring(-50.0, -50.0, ring) is False


def test_contains_respects_holes_and_bbox():
    exterior = _square(0.0, 0.0, 10.0)
    hole = _square(0.0, 0.0, 3.0)
    poly = NeighborhoodPolygon(
        name="Test", parts=[(exterior, [hole])], bbox=(-10.0, -10.0, 10.0, 10.0)
    )
    assert poly.contains(7.0, 0.0) is True     # in exterior, outside hole
    assert poly.contains(0.0, 0.0) is False    # inside the hole
    assert poly.contains(100.0, 0.0) is False  # outside bbox (cheap reject)


def test_index_lookup_first_match_else_empty():
    a = NeighborhoodPolygon("A", [(_square(0.0, 0.0, 5.0), [])], (-5.0, -5.0, 5.0, 5.0))
    b = NeighborhoodPolygon("B", [(_square(20.0, 0.0, 5.0), [])], (15.0, -5.0, 25.0, 5.0))
    idx = NeighborhoodIndex([a, b])
    assert idx.lookup(0.0, 0.0) == "A"
    assert idx.lookup(20.0, 0.0) == "B"
    assert idx.lookup(50.0, 50.0) == ""


# --- the vendored dataset ---------------------------------------------------

def test_vendored_geojson_loads():
    idx = load_neighborhoods(_DATA, _ORIGIN)
    assert len(idx) == 41
    names = {p.name for p in idx.polygons}
    assert "Mission" in names
    assert "Twin Peaks" in names
    assert "" not in names  # every feature carries an nhood


def test_known_points_map_to_expected_neighborhoods():
    idx = load_neighborhoods(_DATA, _ORIGIN)

    # Twin Peaks summit — unambiguously inside the "Twin Peaks" polygon.
    x, z = to_world_xz(-122.4475, 37.7544, _ORIGIN)
    assert idx.lookup(x, z) == "Twin Peaks"

    # Ferry Building — Financial District / South Beach.
    x, z = to_world_xz(-122.3937, 37.7955, _ORIGIN)
    assert idx.lookup(x, z) == "Financial District/South Beach"

    # Out in the Pacific, west of the city — outside every polygon.
    x, z = to_world_xz(-122.5500, 37.7500, _ORIGIN)
    assert idx.lookup(x, z) == ""


# ---------------------------------------------------------------------------
# standalone runner (pytest not yet a project dependency)
# ---------------------------------------------------------------------------

def _run_all():
    tests = sorted(
        (name, obj)
        for name, obj in globals().items()
        if name.startswith("test_") and callable(obj)
    )
    failures = 0
    for name, fn in tests:
        try:
            fn()
            print(f"PASS {name}")
        except Exception as exc:  # noqa: BLE001 - report and continue
            failures += 1
            print(f"FAIL {name}: {exc!r}")
    print(f"\n{len(tests) - failures}/{len(tests)} passed")
    return failures


if __name__ == "__main__":
    sys.exit(1 if _run_all() else 0)
