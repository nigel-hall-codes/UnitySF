"""Tests for the heightfield source low-pass (#233).

The rasterizer fills the grid by per-triangle linear barycentric interpolation,
which is only C0 — the gradient jumps across every triangle edge, seeding the
crease lines / waviness #230 targets. `elevation._low_pass_normalized` smooths
the rasterized grid at the source, using a coverage mask so it never bleeds the
outside-hull zeros into the valid terrain.

Runs under pytest, or standalone with the project venv:
    python tests/test_elevation_smoothing.py
"""
from __future__ import annotations

import os
import sys
import tempfile

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from sfmap import elevation
from sfmap.elevation import _low_pass_normalized, _rasterize, clear_cache, parse
from sfmap.projection import GeoOrigin, OsmBounds


# ---------------------------------------------------------------------------
# helpers
# ---------------------------------------------------------------------------

def _second_diff_energy(grid):
    """Sum of squared discrete Laplacian over interior cells — high-freq proxy."""
    g = np.asarray(grid, dtype=np.float64)
    lap = (
        g[2:, 1:-1] + g[:-2, 1:-1] + g[1:-1, 2:] + g[1:-1, :-2] - 4.0 * g[1:-1, 1:-1]
    )
    return float(np.sum(lap * lap))


# ---------------------------------------------------------------------------
# _low_pass_normalized
# ---------------------------------------------------------------------------

def test_low_pass_sigma_zero_is_noop():
    g = np.random.default_rng(0).random((16, 16)).astype(np.float32)
    covered = np.ones_like(g)
    out = _low_pass_normalized(g, covered, 0.0)
    assert np.array_equal(out, g)


def test_low_pass_reduces_high_frequency_energy():
    n = 40
    base = np.linspace(0.0, 1.0, n)[None, :] * np.ones((n, 1))  # smooth ramp
    rng = np.random.default_rng(1)
    noisy = (base + 0.05 * rng.standard_normal((n, n))).astype(np.float32)
    covered = np.ones((n, n), dtype=np.float32)

    out = _low_pass_normalized(noisy, covered, sigma_cells=2.0)

    assert _second_diff_energy(out) < 0.25 * _second_diff_energy(noisy)
    # Overall relief is preserved (mean roughly unchanged).
    assert abs(float(out.mean()) - float(noisy.mean())) < 0.02


def test_low_pass_preserves_linear_ramp_interior():
    # A Gaussian leaves a linear function unchanged except near the boundary.
    n = 40
    ramp = (np.arange(n)[None, :] * np.ones((n, 1)) * 0.01).astype(np.float32)
    covered = np.ones((n, n), dtype=np.float32)
    out = _low_pass_normalized(ramp, covered, sigma_cells=2.0)
    m = 6  # ignore boundary band
    assert np.allclose(out[m:-m, m:-m], ramp[m:-m, m:-m], atol=1e-4)


def test_low_pass_no_zero_bleed_from_uncovered():
    # Left half covered & constant 0.8; right half uncovered (outside hull).
    n = 30
    values = np.zeros((n, n), dtype=np.float32)
    covered = np.zeros((n, n), dtype=np.float32)
    values[:, : n // 2] = 0.8
    covered[:, : n // 2] = 1.0

    out = _low_pass_normalized(values, covered, sigma_cells=3.0)

    # Covered cells (even right at the boundary) keep the constant value — a
    # plain blur would have dragged them toward the uncovered 0.0.
    cov = covered > 0.0
    assert np.allclose(out[cov], 0.8, atol=1e-5)
    # Uncovered cells are left exactly as they were.
    assert np.array_equal(out[~cov], values[~cov])


# ---------------------------------------------------------------------------
# _rasterize coverage mask
# ---------------------------------------------------------------------------

def test_rasterize_returns_coverage_mask():
    # Four corners + centre of a unit square -> hull covers the whole grid.
    pts = np.array(
        [[0.0, 0.0], [10.0, 0.0], [0.0, 10.0], [10.0, 10.0], [5.0, 5.0]],
        dtype=np.float64,
    )
    elevs = np.array([0.0, 10.0, 10.0, 20.0, 10.0], dtype=np.float32)
    values, covered = _rasterize(pts, elevs, 0.0, 20.0, 11, 0.0, 0.0, 10.0, 10.0)

    assert values.shape == (11, 11) and covered.shape == (11, 11)
    # Hull spans the whole square, so the vast majority of cells are covered
    # (a few may miss on the shared triangle diagonal due to float edges).
    assert covered.max() == 1.0 and covered.mean() > 0.9
    assert float(values.min()) >= 0.0 and float(values.max()) <= 1.0


def test_rasterize_marks_outside_hull_uncovered():
    # A small triangle in the corner leaves far cells outside the hull.
    pts = np.array([[0.0, 0.0], [2.0, 0.0], [0.0, 2.0]], dtype=np.float64)
    elevs = np.array([0.0, 1.0, 1.0], dtype=np.float32)
    _values, covered = _rasterize(pts, elevs, 0.0, 1.0, 11, 0.0, 0.0, 10.0, 10.0)
    assert covered[0, 0] == 1.0       # inside the triangle
    assert covered[-1, -1] == 0.0     # far opposite corner, outside hull


# ---------------------------------------------------------------------------
# parse() integration (exercises the cache + CLI default path)
# ---------------------------------------------------------------------------

_CSV = (
    "id,elevation,geometry\n"
    '1,0,"LINESTRING (-122.4000 37.8000, -122.3990 37.8000, -122.3980 37.8000)"\n'
    '2,20,"LINESTRING (-122.4000 37.8005, -122.3990 37.8005, -122.3980 37.8005)"\n'
    '3,40,"LINESTRING (-122.4000 37.8010, -122.3990 37.8010, -122.3980 37.8010)"\n'
    '4,60,"LINESTRING (-122.4000 37.8015, -122.3990 37.8015, -122.3980 37.8015)"\n'
)


def _bounds_origin():
    bounds = OsmBounds(min_lat=37.8000, max_lat=37.8015, min_lon=-122.4000, max_lon=-122.3980)
    return bounds, GeoOrigin.from_bounds(bounds)


def _parse_csv(text, **kwargs):
    """Parse a throwaway CSV, clearing its cache so smoothing actually re-runs."""
    bounds, origin = _bounds_origin()
    fd, path = tempfile.mkstemp(suffix=".csv")
    os.close(fd)
    try:
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
        res = kwargs.get("resolution", 65)
        clear_cache(path, res)
        hmap = parse(path, bounds, origin, **kwargs)
        return hmap
    finally:
        try:
            clear_cache(path, kwargs.get("resolution", 65))
        except OSError:
            pass
        if os.path.exists(path):
            os.remove(path)


def test_parse_smoothing_stays_normalized_and_smooths():
    raw = _parse_csv(_CSV, resolution=65, smooth_sigma_m=0.0)
    smoothed = _parse_csv(_CSV, resolution=65, smooth_sigma_m=4.0)

    assert raw.resolution == smoothed.resolution == 65
    for h in (raw, smoothed):
        assert np.isfinite(h.values).all()
        assert float(h.values.min()) >= -1e-6 and float(h.values.max()) <= 1.0 + 1e-6

    # Smoothing must not increase high-frequency content; on this creased input
    # it should reduce it.
    assert _second_diff_energy(smoothed.values) <= _second_diff_energy(raw.values) + 1e-9


def test_parse_default_smoothing_enabled():
    # The module default is on; the CLI flag mirrors it.
    assert elevation._HMAP_SMOOTH_SIGMA_M > 0.0


def test_cli_default_matches_library_default():
    # sfmap_bake duplicates the default as a literal (to avoid importing scipy
    # for --help); guard against the two drifting apart.
    import sfmap_bake

    assert sfmap_bake._DEFAULT_HMAP_SMOOTH_M == elevation._HMAP_SMOOTH_SIGMA_M


# ---------------------------------------------------------------------------
# standalone runner
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
        except Exception as exc:  # noqa: BLE001
            failures += 1
            print(f"FAIL {name}: {exc!r}")
    print(f"\n{len(tests) - failures}/{len(tests)} passed")
    return failures


if __name__ == "__main__":
    sys.exit(1 if _run_all() else 0)
