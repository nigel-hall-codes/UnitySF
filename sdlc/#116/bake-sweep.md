# Chunk-size bake-and-measure sweep — #116

Goal: pick a chunk size that lands the 3×3 streaming ring on ~5–10 resident city blocks
(parent #114). This is a tuning experiment on the existing `--chunk-size` bake knob — no
production code change.

## Fixed test area

Use the small input `map.osm` (~13 MB, single SF region) — the same input the known-good
`--chunk-size 1000` 4×3 bake used. Its data extent is ~**4.0 × 3.0 km**, so every sweep
bakes the *same geography* at a different chunk size → an apples-to-apples comparison of
grid size, file count, and streaming behaviour.

## Candidate sizes + matched heightmap resolution

Unity terrain heightmaps must be `2^n + 1` (33, 65, 129, 257, 513). To keep terrain detail
roughly constant (~2 m/sample, matching the known-good 1000 m / 513 bake at 1.95 m/sample),
`--hmap-res` must scale **down** with chunk size — leaving it at 513 would make small chunks
~10× over-tessellated (the trap #114 prior-art flagged).

| Chunk size | `--hmap-res` | m / sample | Notes |
|---|---|---|---|
| 1000 m (baseline, already baked) | 513 | 1.95 | = `chunks_test_full` |
| **500 m** | **257** | 1.95 | |
| **300 m** | **129** | 2.34 | |
| **150 m** | **65** | 2.31 | |

(Optional 4th: **100 m / hmap 65** ≈ 1.56 m/sample — the 3×3 ring is ~9 blocks, right on
the low end of the target. Add it if 150 m reads as still slightly too big.)

## Bake commands (run from `python/`, using its `.venv`)

```bash
cd python

# 500 m
.venv/Scripts/python.exe -m sfmap_bake \
  --osm  "../My project (2)/Assets/SFMapData/map.osm" \
  --elev "../My project (2)/Assets/SFMapData/Elevation_Contours_20260619.csv" \
  --preset cs500 --chunk-size 500 --hmap-res 257 \
  --out  "../chunks_cs500/"

# 300 m
.venv/Scripts/python.exe -m sfmap_bake \
  --osm  "../My project (2)/Assets/SFMapData/map.osm" \
  --elev "../My project (2)/Assets/SFMapData/Elevation_Contours_20260619.csv" \
  --preset cs300 --chunk-size 300 --hmap-res 129 \
  --out  "../chunks_cs300/"

# 150 m
.venv/Scripts/python.exe -m sfmap_bake \
  --osm  "../My project (2)/Assets/SFMapData/map.osm" \
  --elev "../My project (2)/Assets/SFMapData/Elevation_Contours_20260619.csv" \
  --preset cs150 --chunk-size 150 --hmap-res 65 \
  --out  "../chunks_cs150/"
```

The bake console prints, per run: resolved grid (`grid CxR @ Nm`), node/edge/building
counts, per-chunk mesh counts, bake time. `manifest.json` records `chunkSize`, `chunksX`,
`chunksZ`, bounds. Note the `--out` `.bin` total size (`du -sh ../chunks_csNNN/`).

## Then import + observe each in Unity

For each preset: **Window → SF Map Importer** → point at `chunks_csNNN/` → Preset Name
`csNNN` → **Import Chunks**. Then **Preset Browser → Load**, enter Play, and move the
camera across a few cell boundaries.

> ⚠️ Smaller chunks = many more objects. Importing/loading `cs150` over the whole area
> will be heavy *until #112 lands* (it bakes the full hierarchy into the scene). For this
> measurement it's fine to import only a corner, or just read the bake-side numbers + the
> analytical ring math below and do the in-editor churn check on `cs300`/`cs500`.

## Predicted geometry (analytical — fill the measured columns when baking)

Extent ≈ 4.0 × 3.0 km; SF grid block ≈ ~100 m (assumption). 3×3 ring side = 3 × chunkSize.

| Chunk | Grid (pred.) | Chunks (pred.) | 3×3 ring side | ≈ blocks/side | ≈ resident blocks |
|---|---|---|---|---|---|
| 1000 m | 4×3 | 12 | 3000 m | ~30 | ~900 |
| 500 m | 8×6 | 48 | 1500 m | ~15 | ~225 |
| 300 m | 14×10 | 140 | 900 m | ~9 | ~81 |
| 150 m | 27×20 | 540 | 450 m | ~4.5 | ~20 |
| (100 m) | 40×30 | 1200 | 300 m | ~3 | ~9 |

What only the bake can tell us (the actual point of running it):

## Measurement template (fill from each run)

| Chunk size | hmap-res | grid (actual) | nChunks | bake time | .bin total (MB) | avg verts/chunk | resident blocks (3×3) | streaming churn | hitching vs #104 |
|---|---|---|---|---|---|---|---|---|---|
| 500 m | 257 | | | | | | | load/unload per min: | |
| 300 m | 129 | | | | | | | | |
| 150 m | 65 | | | | | | | | |

- **streaming churn**: in Play mode, count `ChunkStreamer` load/unload events per minute of
  typical movement (or note cell-crossings/min × ring perimeter).
- **hitching vs #104**: subjective — does crossing a boundary stutter? worse than 1964 m?

## Decision

Pick the largest chunk size whose 3×3 ring is ≤ ~10 resident blocks **and** whose file
count / churn / bake time stay acceptable. Record the chosen `(chunk-size, hmap-res)` here
and on the issue — that value unblocks #117 (set the default + re-bake), which is also
gated on #112.
