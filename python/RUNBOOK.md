# Map Pipeline Runbook — OSM + elevation → Unity map

Step-by-step for baking a map from raw data and getting it into the Unity scene.
Two tiers: **Python** bakes raw data into per-chunk `.bin` files; **Unity** imports
those into terrain/mesh assets and loads them into the scene.

---

## Stage 1 — Bake `.bin` files (Python)

### Inputs (in `My project (2)/Assets/SFMapData/`)
- **OSM**: `map.osm` (single SF region, ~13 MB) or `full_sf_map` (~496 MB, larger area)
- **Elevation CSV**: `Elevation_Contours_20260619.csv` (~174 MB contour lines)

First run interpolates the heightmap and writes a `*.r{res}.heightcache` next to the CSV;
later runs at the same `--hmap-res` reuse it (fast). Deleting the cache forces a re-interp.

### Dependencies
`osmium, scipy, numpy, shapely, triangle` (see `python/requirements.txt`).
Fresh setup from the repo `python/` dir:
`python -m venv .venv && .venv/Scripts/python.exe -m pip install -r requirements.txt`.

> ⚠️ The OSM library's PyPI project is **`osmium`** (provides `import osmium`), **not**
> `pyosmium` — `pip install pyosmium` now fails with "No matching distribution". The code
> targets osmium 4.x (tested on 4.3.1). `requirements.txt` already uses the right name.

### Run it
The CLI does `from sfmap import ...`, so **run from the `python/` directory** (or one with
`sfmap/` on the path). `--osm`/`--elev`/`--out` paths are relative to wherever you launch.

```bash
cd python

.venv/Scripts/python.exe -m sfmap_bake \
  --osm  "../My project (2)/Assets/SFMapData/map.osm" \
  --elev "../My project (2)/Assets/SFMapData/Elevation_Contours_20260619.csv" \
  --out  "../chunks_out/"
```

Default run (`--chunk-size 300 --hmap-res 129`, chosen in #116): auto-fits a **12×9 = 108-chunk**
grid for the `map.osm` play area (~3.3 × 2.4 km). ~3 m/sample terrain; ~900 m 3×3 ring ≈ 9 city
blocks resident — the streaming target. Old baseline: `--chunk-size 1000 --hmap-res 513` (this
is how `chunks_test_full/` was produced; keep flag explicit if you need to reproduce it).

#### Baking the full city (`full_sf_map`)
The full-extent input is `full_sf_map` (~496 MB) and has **no `.osm` extension** — pyosmium
detects format by suffix and will fail without one. Make an `.osm`-named **hardlink** (no
496 MB copy; keep it out of `Assets/` so Unity doesn't import it):

```powershell
New-Item -ItemType HardLink -Path "full_sf_map.osm" `
  -Target "My project (2)/Assets/SFMapData/full_sf_map"
```

Then bake (run from `python/`):

```bash
.venv/Scripts/python.exe -m sfmap_bake \
  --osm  "../full_sf_map.osm" \
  --elev "../My project (2)/Assets/SFMapData/Elevation_Contours_20260619.csv" \
  --preset full_sf --chunk-size 300 --hmap-res 129 \
  --out  "../chunks_full/"
```

Expected result at 300 m / hmap 129: geometry extent **16.4 × 14.2 km** → grid **~55×48 ≈ 2,640
chunks** (101,681 nodes / 34,300 edges / 159,313 buildings, elev [−12.2, 278.9] m). ~50%
empty-chunk rate means ~1,320 real + ~1,320 ocean/no-data `.bin` files; each empty chunk is
~66 KB (129² heightmap only). Total output expected ~175–425 MB.

> **If 2,640 Unity prefabs exceeds editor budget**: bake `map.osm` (the play area, 108 chunks)
> instead, and treat `full_sf` as a future milestone. Coarse-LOD tier for far-field coverage is
> a deliberate follow-up — do not build it here.

Old result (2026-06-22, `--chunk-size 1964 --hmap-res 513`): grid **9×8 = 72 chunks**,
~3.8 min bake, ~451 MB output, ~32 m/px terrain.

### Flags (`-m sfmap_bake --help`)
| Flag | Default | Notes |
|------|---------|-------|
| `--osm FILE` | *required* | input `.osm` |
| `--elev FILE` | *required* | elevation CSV |
| `--preset NAME` | `default` | output set name; must match the Unity import preset |
| `--chunk-size METERS` | `300` | world size per chunk |
| `--chunks-x N` / `--chunks-z N` | **auto-fit** | omit to auto-size the grid to the data extent (#101 change) |
| `--out DIR` | `./chunks/` | where `.bin` + `manifest.json` go |
| `--only col,row ...` | all | bake a subset, e.g. `--only 0,0 1,0` |
| `--hmap-res N` | `129` | heightmap samples per chunk side; keys the `.heightcache` |
| `--no-sidewalks` | off | skip sidewalk meshes (faster) |

The grid is anchored at the data's **SW corner** (not world origin), so geometry that
straddles the projection origin still lands inside the grid.

### Output (in `--out`)
`chunk_CC_RR.bin` per chunk + `manifest.json` (preset, grid, bounds, elevation range,
per-chunk world origins). Sanity-check: console prints node/edge/building counts, the
resolved grid (e.g. `grid 4x3 @ 1000m`), and per-chunk mesh counts.

---

## Stage 2 — Import into Unity (Editor)

1. Open the Unity project (`My project (2)`).
2. Menu: **Window → SF Map Importer**.
3. **Chunk Directory** → browse to the `--out` dir from Stage 1 (e.g. `…/chunks_out`).
4. **Preset Name** → same string as `--preset` (default `default`).
5. Click **Import Chunks**. Produces:
   - `Assets/Generated/{preset}/` — editor assets: `TerrainData`, road/intersection/
     sidewalk/building `.mesh` files, shared materials, copied `manifest.json`.
   - `Assets/Resources/Generated/{preset}/` — one `chunk_CC_RR.prefab` per chunk +
     `ChunkManifest.asset` (runtime metadata).
   - Re-import skips chunks whose fingerprint is unchanged (instantiates the existing prefab).
   - **Clear Generated Assets** wipes the preset to force a clean re-import.

## Stage 3 — Load into the scene

1. Menu: **Window → SF Map Preset Browser**.
2. Pick the preset → **Load**. Destroys any existing `SF Map` root, instantiates all
   chunk prefabs (terrain + meshes) at their world origins, and saves the scene.

---

## Quick recipe (next time)

1. `cd python` (use its `.venv`).
2. Run `sfmap_bake` with `--osm map.osm --elev Elevation_Contours_*.csv --out chunks_out/`
   (defaults to `--chunk-size 300 --hmap-res 129` → **12×9 = 108 chunks** for `map.osm`).
3. Unity → **Window → SF Map Importer** → point at `chunks_out/` → **Import Chunks**.
4. Unity → **Window → SF Map Preset Browser** → **Load**.

---

## Key files
- `python/sfmap_bake.py` — CLI / orchestrator (`main()`)
- `python/sfmap/{osm,elevation,projection,stamping,serialize}.py`, `geometry/{road,sidewalk,building,intersection}.py`
- `python/sfmap/chunk.py` — `bake_chunk()` per-chunk pipeline
- `My project (2)/Assets/Scripts/Pipeline/Editor/SFMapImporterWindow.cs` — `.bin` → assets, menu `Window/SF Map Importer`
- `My project (2)/Assets/Scripts/Pipeline/Editor/SFMapPresetsWindow.cs` — menu `Window/SF Map Preset Browser`
- `My project (2)/Assets/Scripts/Pipeline/{PipelineTypes,ChunkManifest}.cs` — paths + runtime manifest
