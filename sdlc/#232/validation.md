# #232 — Tune & validate the road vertical-profile smoother

Child of #230. Depends on #231 (the smoother) and complemented by #233 (source
low-pass). This validates and tunes the smoother's **arc-length window**
(`road._SMOOTH_WINDOW_M`).

## Method (headless)

`python/road_smoothing_tuning.py` sweeps the window over two profiles that
bracket the real cases, at the **real densify spacing** (`_MAX_SEG_M` = 4 m — the
worst-case point spacing produced by #219's subdivision):

- **flat block** — flat road + multi-tone high-frequency noise (0.25 m). Surface
  roughness here is what a vehicle feels as vertical bounce (the NWH check); we
  minimise it. Metric: mean-squared 2nd-difference vs. the unsmoothed value.
- **Lombard** — a steep, consistent 31% grade + the same noise. We must keep the
  genuine grade. Metric: preserved fraction of the true slope.

## Results

```
  window_m | flat_rough | grade_kept | lombard_ripple
  --------- | ---------- | ---------- | --------------
        0.0 |      1.000 |     0.9951 |          1.000
        4.0 |      1.000 |     0.9951 |          1.000
        8.0 |      1.000 |     0.9951 |          1.000
       12.0 |      0.159 |     0.9963 |          0.159
       16.0 |      0.074 |     0.9968 |          0.074
       20.0 |      0.033 |     0.9969 |          0.033
       28.0 |      0.003 |     0.9970 |          0.002
```

## Finding

The window interacts critically with the densify spacing. At 4 m spacing a
tricube window **≤ 8 m is a near no-op** — neighbours land on the zero-weight
window edge, so nothing is fitted. **12 m is the smallest effective window**: it
cuts flat-block roughness to **15.9%** of raw (a ~6× reduction in bounce proxy)
while preserving **99.6%** of the 31% Lombard grade. Larger windows smooth more
(16 m → 7.4%, 20 m → 3.3%) with negligible extra grade loss, but 12 m already
clears the targets (flat ≤ 25% of raw, grade ≥ 99%) and is the most conservative
choice that doesn't risk rounding real grade changes on shorter blocks.

**Decision: keep `road._SMOOTH_WINDOW_M = 12.0 m`** — validated as the minimal
effective window, not arbitrary. `tests/test_road_smoother_tuning.py` locks this
(small windows are no-ops; the default is effective + grade-preserving; the
independent recommender agrees with the shipped constant), so a future change to
the smoother or the densify cap that quietly breaks the default will fail CI.

No seam/facet regression: the stamp and mesh apply the *same* smoother to the
*same* densified XZ (bit-identical), enforced by #231's
`test_stamp_grade_equals_mesh_surface`. The source low-pass (#233) further
reduces the creases feeding into this.

## Manual residual (Unity-only — same as #231/#233)

These require the editor and are the standard manual confirmation for any bake
change (cannot be driven headlessly):

1. Bake a flat ring and a Lombard ring — `/baker around "Lombard Street" --ring 3`
   and a flat block (e.g. `/baker around "<flat intersection>" --ring 3`) — and
   import to Unity.
2. Visually confirm flat-block smoothness and Lombard grade fidelity with no seam
   or faceting.
3. Measure in-engine NWH vehicle vertical bounce over a flat block as the live
   counterpart to the headless roughness metric above.
