"""Tests for the shared road centerline vertical-profile smoother (#231).

The smoother (`sfmap.geometry.road._smooth_centerline_profile`) is applied
identically in the mesh pass and the stamp pass; these tests lock in the
properties the stamp/mesh consistency (#219) and the smoothing goal (#230)
depend on:

  - anchored endpoints + XZ stay exact,
  - the same smoothed grade drives the stamped terrain and the resampled mesh
    surface (no seam),
  - genuine straight grades pass through untouched,
  - high-frequency vertical noise is attenuated.

Runs under pytest, or standalone with the project venv:
    python tests/test_road_smoother.py
(pytest is not currently a project dependency, so the standalone runner exists
so these can be executed today.)
"""
from __future__ import annotations

import math
import os
import sys
from types import SimpleNamespace

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from sfmap.elevation import HeightmapData
from sfmap.geometry import road
from sfmap.geometry.road import (
    _RAISE,
    _smooth_centerline_profile,
    build_road_meshes,
)
from sfmap.osm import HighwayType, StreetEdge, StreetNode
from sfmap.stamping import stamp_roads


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

def _second_diff_energy(y):
    """Sum of squared discrete second differences over interior points.

    A proxy for high-frequency vertical wiggle: zero for a straight line,
    large for noisy profiles.
    """
    return sum(
        (y[i + 1] - 2.0 * y[i] + y[i - 1]) ** 2 for i in range(1, len(y) - 1)
    )


def _straight_xz(n, step=2.0):
    return [(float(i) * step, 0.0) for i in range(n)]


# ---------------------------------------------------------------------------
# unit tests on the smoother
# ---------------------------------------------------------------------------

def test_endpoints_preserved():
    xz = _straight_xz(20)
    y = [10.0 + math.sin(i) for i in range(20)]
    out = _smooth_centerline_profile(xz, y, window_m=12.0)
    assert out[0] == y[0]
    assert out[-1] == y[-1]
    assert len(out) == len(y)


def test_input_not_mutated():
    xz = _straight_xz(20)
    y = [10.0 + math.sin(i) for i in range(20)]
    xz_copy = list(xz)
    y_copy = list(y)
    _smooth_centerline_profile(xz, y, window_m=12.0)
    assert xz == xz_copy  # XZ untouched
    assert y == y_copy    # operates out-of-place


def test_short_polyline_passthrough():
    # Fewer than 3 points: nothing to smooth, return a copy unchanged.
    assert _smooth_centerline_profile([(0.0, 0.0), (4.0, 0.0)], [3.0, 5.0]) == [3.0, 5.0]


def test_straight_ramp_passthrough_uniform():
    # Constant grade, uniform spacing — must pass through exactly.
    xz = _straight_xz(25, step=2.0)
    slope = 0.12
    y = [5.0 + slope * (2.0 * i) for i in range(25)]
    out = _smooth_centerline_profile(xz, y, window_m=12.0)
    for a, b in zip(out, y):
        assert abs(a - b) < 1e-9


def test_straight_ramp_passthrough_nonuniform():
    # Constant grade in arc length, but vertices unevenly spaced — still exact,
    # because arc length (not index) is the smoothing coordinate.
    xs = [0.0, 1.3, 5.0, 6.1, 11.7, 15.0, 19.4, 23.0, 28.8, 33.0, 40.0]
    xz = [(x, 0.0) for x in xs]
    slope = -0.07
    y = [2.0 + slope * x for x in xs]
    out = _smooth_centerline_profile(xz, y, window_m=14.0)
    for a, b in zip(out, y):
        assert abs(a - b) < 1e-9


def test_noise_attenuated():
    # Flat profile + deterministic high-frequency wiggle -> attenuated.
    n = 41
    xz = _straight_xz(n, step=2.0)
    noise = [0.30 * math.sin(i * 1.7) + 0.18 * math.sin(i * 3.1) for i in range(n)]
    y = [10.0 + e for e in noise]
    out = _smooth_centerline_profile(xz, y, window_m=12.0)

    # Endpoints exact; high-frequency energy strongly reduced.
    assert out[0] == y[0] and out[-1] == y[-1]
    assert _second_diff_energy(out) < 0.15 * _second_diff_energy(y)


def test_window_zero_is_noop():
    xz = _straight_xz(10)
    y = [float(i * i % 5) for i in range(10)]
    assert _smooth_centerline_profile(xz, y, window_m=0.0) == y


# ---------------------------------------------------------------------------
# integration: stamp grade and resampled mesh surface stay consistent
# ---------------------------------------------------------------------------

def _noisy_flat_heightmap(res=101, world=100.0, base_norm=0.5, noise_norm=0.04):
    """Overall-flat normalized field plus a deterministic high-frequency ripple.

    min/max elevation span 20 m, so noise_norm=0.04 ~= 0.8 m of wiggle.
    """
    rows = np.arange(res)[:, None]
    cols = np.arange(res)[None, :]
    ripple = noise_norm * (
        np.sin(rows * 1.3) * np.cos(cols * 1.1) + np.sin(cols * 2.7)
    )
    values = (base_norm + ripple).astype(np.float32)
    return HeightmapData(
        values=values,
        resolution=res,
        min_elevation_m=0.0,
        max_elevation_m=20.0,
        world_x_min=0.0,
        world_z_min=0.0,
        world_width=world,
        world_height=world,
    )


def _single_road_graph():
    a = StreetNode(osm_id=1, world_x=10.0, world_z=50.0)
    b = StreetNode(osm_id=2, world_x=90.0, world_z=50.0)
    edge = StreetEdge(
        osm_way_id=100,
        from_node=a,
        to_node=b,
        highway_type=HighwayType.RESIDENTIAL,
        is_one_way=False,
        centerline=[(10.0, 50.0), (90.0, 50.0)],
        lanes=2,
    )
    # build_road_meshes / stamp_roads only touch .edges (+ .intersection_nodes
    # which we keep empty so no junction flattening runs).
    return SimpleNamespace(edges=[edge], intersection_nodes=[])


def test_stamp_grade_equals_mesh_surface():
    """The mesh resamples the post-stamp field, so the road surface must equal
    the stamped grade beneath it — the load-bearing #219 invariant."""
    hmap = _noisy_flat_heightmap()
    graph = _single_road_graph()

    stamp_roads(hmap, graph)
    meshes = build_road_meshes(graph, hmap)
    assert meshes, "expected one road mesh"
    verts, _normals, _uvs, _idx = next(iter(meshes.values()))

    for vx, vy, vz in verts:
        stamped = road._sample_elevation(hmap, vx, vz)
        assert abs((vy - _RAISE) - stamped) < 1e-4


def test_stamped_surface_smoother_than_raw_terrain():
    """End-to-end: the road surface profile carries far less high-frequency
    wiggle than the raw terrain it was sampled from."""
    raw = _noisy_flat_heightmap()
    stamped = _noisy_flat_heightmap()  # identical copy to stamp into
    graph = _single_road_graph()

    # Profile of the raw terrain along the road centerline (pre-stamp).
    xs = [10.0 + i * 2.0 for i in range(41)]
    raw_profile = [road._sample_elevation(raw, x, 50.0) for x in xs]

    stamp_roads(stamped, graph)
    smoothed_profile = [road._sample_elevation(stamped, x, 50.0) for x in xs]

    assert _second_diff_energy(smoothed_profile) < 0.4 * _second_diff_energy(raw_profile)


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
