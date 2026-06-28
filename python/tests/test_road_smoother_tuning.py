"""Locks the road-smoother window tuning rationale (#232).

The default `road._SMOOTH_WINDOW_M` is not arbitrary: at the real densify
spacing (`_MAX_SEG_M`, 4 m) the tricube window must comfortably exceed 2x the
spacing or it is a near no-op, and it must stay small enough to preserve genuine
grades. These tests guard the chosen value against silent regressions in either
the smoother or the densify cap.

Standalone:  .venv/Scripts/python.exe tests/test_road_smoother_tuning.py
"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

import road_smoothing_tuning as rst
from sfmap.geometry.road import _SMOOTH_WINDOW_M


def _row(rows, window):
    return next(r for r in rows if r["window_m"] == window)


def test_small_windows_are_noops_at_densify_spacing():
    # At 4 m spacing, windows up to 8 m put neighbours on the zero-weight edge,
    # so the filter does effectively nothing — proves the default can't be small.
    rows = rst.evaluate()
    for w in (4.0, 8.0):
        assert _row(rows, w)["flat_roughness_frac"] > 0.95


def test_default_window_is_effective_and_grade_preserving():
    row = _row(rst.evaluate(), _SMOOTH_WINDOW_M)
    assert row["flat_roughness_frac"] <= rst._FLAT_TARGET   # smooths the flat block
    assert row["grade_kept"] >= rst._GRADE_FLOOR            # keeps the steep grade


def test_recommended_window_matches_default():
    # The independent recommender should land on the shipped default, so the
    # constant and the tuning evidence stay in agreement.
    rec = rst.recommend(rst.evaluate())
    assert rec is not None
    assert rec["window_m"] == _SMOOTH_WINDOW_M


def _run_all():
    tests = sorted(
        (n, o) for n, o in globals().items() if n.startswith("test_") and callable(o)
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
