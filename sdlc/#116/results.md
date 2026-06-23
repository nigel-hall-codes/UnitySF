# Chunk-size sweep — results (#116)

Baked `map.osm` (extent **3310 × 2414 m**) at three chunk sizes with matched `--hmap-res`
(~2 m/sample). All runs 2026-06-23. Baseline = 1000 m / hmap 513 (`chunks_test_full`).

## Measured

| Chunk | hmap | grid | nChunks | empty | bake | .bin total | avg bin | max bin | total meshes |
|---|---|---|---|---|---|---|---|---|---|
| 1000 m (base) | 513 | 4×3 | 12 | — | — | 23 MB | — | — | ~6,500* |
| **500 m** | 257 | 7×5 | **35** | 11 (31%) | 25.6 s | 20 MB | 555 KB | 1558 KB | 6,489 |
| **300 m** | 129 | 12×9 | **108** | 54 (50%) | 4.0 s | 18 MB | 161 KB | 593 KB | 6,619 |
| **150 m** | 65 | 23×17 | **391** | 225 (58%) | 28.3 s | 18 MB | 43 KB | 160 KB | 6,896 |

\* same geometry across all sizes, so total mesh count is ~constant (~6.5k); only how it's
*partitioned* changes.

## The metric that matters: what's resident in a 3×3 ring

| Chunk | 3×3 ring span | ≈ blocks across* | densest 3×3 meshes | terrain verts/chunk | resident terrain verts (3×3) |
|---|---|---|---|---|---|
| 1000 m (base) | 3000 m | ~30 | ~5,800 (≈whole map) | 263,169 | ~2,368,521 |
| 500 m | 1500 m | ~15 | 4,525 | 66,049 | 594,441 |
| **300 m** | **900 m** | **~9** | **2,002** | 16,641 | **149,769** |
| 150 m | 450 m | ~4.5 | 746 | 4,225 | 38,025 |

\* SF grid block ≈ ~100 m (assumption).

## Findings

1. **Resident load collapses as chunks shrink — far faster than file count grows.**
   1000 m → 150 m cuts resident terrain vertices **~62×** (2.37M → 38k) and resident
   meshes **~8×** (~5,800 → 746), while file count rises ~33× (12 → 391). The runtime win
   is real and large; the cost is bookkeeping/import, not GPU/memory at the viewer.

2. **Disk total is ~flat (~18–23 MB) at every size** — because `--hmap-res` was scaled
   down with chunk size. This confirms the prior-art point: *matched hmap-res is essential*.
   Left at 513, the 150 m set would have been ~60× more terrain data. Scaled, smaller
   chunks cost no extra disk.

3. **Empty-chunk overhead grows with smaller chunks** — 31% → 50% → 58% of chunks are
   empty (ocean / no-data) but still emit a heightmap-only `.bin` (43–1558 KB each). At
   150 m that's 225 dead files. Annoying but cheap, and the streamer already records misses.

4. **Bake time is not monotonic** (25.6 / 4.0 / 28.3 s) — dominated by per-`--hmap-res`
   heightcache (re)interpolation, not chunk count. Negligible at this map size; revisit for
   a full-SF re-bake (#117).

## Recommendation: **300 m / `--hmap-res 129`**

- 3×3 ring spans **~900 m ≈ ~9 blocks across** — squarely in the "5–10 blocks" target.
- Resident load **~16× lighter** than the 1000 m baseline (150k vs 2.37M terrain verts;
  2,002 vs ~5,800 meshes) — kills the "half a city resident" problem from #114.
- **108 chunks** — a sane file/prefab count (vs 391 at 150 m, with 58% empties).

**If a tighter neighborhood is wanted** (~4–5 blocks across), **150 m / hmap 65** delivers
it — 746 resident meshes, 38k verts — at the cost of 391 chunks (58% empty). I'd only go
there if 300 m still feels too wide once #112 lets us load it cleanly in-editor.

> `loadRadius` stays a secondary knob: at 300 m, `loadRadius=1` already hits the target, so
> no need to change it. If 300 m feels slightly wide, dropping to `loadRadius=0` (~3 blocks)
> is cheaper to try than a re-bake.

## Open / deferred

- **In-editor streaming-churn + hitch check (vs #104)** not yet run — blocked on #112
  (loading the small-chunk set over the whole area bakes the full hierarchy into the scene
  today). The bake-side numbers above are decisive enough to pick 300 m; confirm churn feel
  after #112.
- **Test bake dirs** `chunks_cs500/ chunks_cs300/ chunks_cs150/` (~56 MB) are at repo root,
  untracked — safe to delete after the decision; #117 re-bakes the chosen size for real.
