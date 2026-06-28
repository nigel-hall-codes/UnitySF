"""Headless tuning harness for the road vertical-profile smoother (#232).

Sweeps the smoother's arc-length window over two profiles that bracket the real
cases #232 cares about:

  * "flat block"  — flat road + high-frequency terrain noise. Roughness here is
    what a vehicle feels as vertical bounce (the NWH check in the issue); we want
    it minimised.
  * "Lombard"     — a steep, *consistent* grade + the same noise. We must keep
    the genuine grade (real hills stay hills); we want the slope preserved.

For each window it reports:
  - flat roughness  = mean squared 2nd-difference of the surface (bounce proxy),
    as a fraction of the unsmoothed value (lower = smoother);
  - grade kept      = preserved fraction of the true Lombard slope (1.0 = exact);
  - Lombard ripple  = residual 2nd-diff energy on the steep profile (the noise we
    still want gone there too).

The recommendation is the smallest window that drops flat roughness below
`_FLAT_TARGET` while keeping >= `_GRADE_FLOOR` of the Lombard grade.

This is a *mathematical* validation of the filter's behaviour and the basis for
the chosen `road._SMOOTH_WINDOW_M`. It deliberately does NOT cover the in-Unity
visual smoothness / seam check or the in-engine NWH bounce measurement on a real
Lombard bake — those need the editor and are the manual residual noted in
sdlc/#232/validation.md.

Run:  .venv/Scripts/python.exe road_smoothing_tuning.py
"""
from __future__ import annotations

import math

from sfmap.geometry.road import _MAX_SEG_M, _SMOOTH_WINDOW_M, _smooth_centerline_profile

# Use the real densify spacing (the _MAX_SEG_M cap, #219) as the worst-case
# point spacing. This matters: the tricube window must comfortably exceed 2x the
# spacing or neighbours fall on/under the zero-weight window edge and the filter
# is a near no-op. Tuning at an unrealistically fine spacing would recommend a
# window too small to do anything on real, 4 m-densified centerlines.
_STEP_M = _MAX_SEG_M
_N = 41                       # ~160 m of road
_LOMBARD_GRADE = 0.31        # ~17 deg — Lombard's crooked block is ~27%; steep.
_NOISE_AMP_M = 0.25          # high-freq terrain wiggle the smoother should kill
_WINDOWS = [0.0, 4.0, 8.0, 12.0, 16.0, 20.0, 28.0]
_FLAT_TARGET = 0.25          # want flat roughness <= 25% of unsmoothed
_GRADE_FLOOR = 0.99          # keep >= 99% of the genuine grade


def _xz(n, step=_STEP_M):
    return [(i * step, 0.0) for i in range(n)]


def _noise(n):
    # Deterministic multi-tone "terrain" wiggle (no RNG -> reproducible).
    return [
        _NOISE_AMP_M * (math.sin(i * 1.7) + 0.6 * math.sin(i * 3.3) + 0.4 * math.sin(i * 5.1))
        for i in range(n)
    ]


def _second_diff_energy(y):
    return sum((y[i + 1] - 2.0 * y[i] + y[i - 1]) ** 2 for i in range(1, len(y) - 1))


def _mean_slope(y, step=_STEP_M):
    # Average interior slope magnitude (per metre).
    return sum(abs(y[i + 1] - y[i - 1]) / (2.0 * step) for i in range(1, len(y) - 1)) / (len(y) - 2)


def evaluate():
    xz = _xz(_N)
    noise = _noise(_N)

    flat = [10.0 + e for e in noise]
    lombard = [10.0 + _LOMBARD_GRADE * (i * _STEP_M) + noise[i] for i in range(_N)]

    flat_raw = _second_diff_energy(flat)
    lombard_true_slope = _LOMBARD_GRADE

    rows = []
    for w in _WINDOWS:
        fs = _smooth_centerline_profile(xz, flat, window_m=w)
        ls = _smooth_centerline_profile(xz, lombard, window_m=w)
        rows.append({
            "window_m": w,
            "flat_roughness_frac": (_second_diff_energy(fs) / flat_raw) if flat_raw else 0.0,
            "grade_kept": _mean_slope(ls) / lombard_true_slope,
            "lombard_ripple_frac": _second_diff_energy(ls) / _second_diff_energy(lombard),
        })
    return rows


def recommend(rows):
    eligible = [
        r for r in rows
        if r["window_m"] > 0.0
        and r["flat_roughness_frac"] <= _FLAT_TARGET
        and r["grade_kept"] >= _GRADE_FLOOR
    ]
    if not eligible:
        return None
    return min(eligible, key=lambda r: r["window_m"])


def main():
    rows = evaluate()
    print(f"Road smoother window sweep  (current default _SMOOTH_WINDOW_M = {_SMOOTH_WINDOW_M} m)")
    print(f"  flat noise amp = {_NOISE_AMP_M} m, Lombard grade = {_LOMBARD_GRADE*100:.0f}%, "
          f"step = {_STEP_M} m, n = {_N}")
    print()
    print(f"  {'window_m':>9} | {'flat_rough':>10} | {'grade_kept':>10} | {'lombard_ripple':>14}")
    print(f"  {'-'*9} | {'-'*10} | {'-'*10} | {'-'*14}")
    for r in rows:
        print(f"  {r['window_m']:>9.1f} | {r['flat_roughness_frac']:>10.3f} | "
              f"{r['grade_kept']:>10.4f} | {r['lombard_ripple_frac']:>14.3f}")
    print()
    rec = recommend(rows)
    if rec:
        print(f"Recommended window: {rec['window_m']:.1f} m  "
              f"(flat roughness {rec['flat_roughness_frac']*100:.1f}% of raw, "
              f"grade kept {rec['grade_kept']*100:.2f}%)")
        print(f"  targets: flat <= {_FLAT_TARGET*100:.0f}% of raw, grade >= {_GRADE_FLOOR*100:.0f}%")
    else:
        print("No window met both targets — loosen targets or revisit the filter.")
    return 0


if __name__ == "__main__":
    import os
    import sys
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    raise SystemExit(main())
