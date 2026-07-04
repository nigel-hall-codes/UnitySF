# UnitySF

A drivable, walkable slice of San Francisco in Unity, generated from real OpenStreetMap +
elevation data. The repo holds three separate codebases that hand off to each other along a
single pipeline: **bake → import → runtime**, with an authoring **server** feeding the
building-template side.

```
 raw OSM + elevation CSV
          │
   ┌──────▼───────┐   per-chunk .bin      ┌───────────────┐   terrain/mesh assets
   │  1. BAKE     │ ────────────────────▶ │  2. IMPORT     │ ──────────────┐
   │  (python/)   │                       │  (Unity)       │               │
   └──────────────┘                       └───────────────┘               ▼
                                                                   ┌───────────────┐
   ┌──────────────┐   Assets/SFBuildingTemplates/ library drop     │  3. RUNTIME    │
   │  4. SERVER   │ ──────────────────────────────────────────────▶│  (Unity play)  │
   │  (server/)   │        (POST /export/unity)                     └───────────────┘
   └──────────────┘
```

## The stages

### 1. Bake — `python/`
Turns raw OSM (`map.osm` / `full_sf_map`) plus an elevation contour CSV into per-chunk
`.bin` files (terrain heightmaps, road/intersection meshes, buildings, and baked-in parked
cars). This is the `sfmap_bake` CLI ([`python/sfmap_bake.py`](python/sfmap_bake.py)).

- **How to run it:** [`python/RUNBOOK.md`](python/RUNBOOK.md) — full step-by-step, including
  full-city bakes and the heightcache.
- **Dependencies:** [`python/requirements.txt`](python/requirements.txt)
  (`osmium, scipy, numpy, shapely, triangle`).
- **Data & gazetteer:** [`python/data/README.md`](python/data/README.md). The bake also
  reads `python/landmarks.json` (landmark gazetteer, used by `chunk_at.py`) and
  `python/no_parking_roads.json` (manual no-parking street list; see
  `python/no_parking_roads.example.json`).
- **Helpers:** `python/chunk_at.py` resolves a landmark / intersection / lat-lon to the NxN
  chunk ring the `/baker` skill bakes; `python/heightcache_guard.py` keeps the elevation
  cache in sync with the smoothing pipeline.

Bake output goes to `chunks_*/` directories (gitignored — regenerate from source).

### 2. Import — `My project (2)/` (Unity)
The Unity `SFMapImporter` reads the baked `.bin` files under
[`My project (2)/Assets/SFMapData`](My%20project%20%282%29/Assets/SFMapData) and materialises
them into terrain, road/intersection meshes, and building/prop assets in the scene.

### 3. Runtime — `My project (2)/` (Unity, play mode)
The scene streams a 3×3 chunk ring around the camera. Parked cars are baked into the chunk
prefabs; traffic is spawned at runtime. Includes the on-foot player controller, graffiti
spray, and building facades.

### 4. Server — `server/`
A **separate** FastAPI + SQLite service that is the authoring source of truth for the SF
Building Template pipeline. Its only contract with the generation side is the library drop
that `POST /export/unity` writes into
[`My project (2)/Assets/SFBuildingTemplates`](My%20project%20%282%29/Assets/SFBuildingTemplates),
which the Unity importer consumes. See [`server/README.md`](server/README.md).

## Design docs
- [`docs/mvp_building-design-doc.txt`](docs/mvp_building-design-doc.txt) — building MVP design.
- [`docs/canvas-design-doc.txt`](docs/canvas-design-doc.txt) — facade canvas design.

## Development
Work is issue-driven via the `/sdlc` skill (GitHub issues + git worktrees). Bake output
(`chunks_*/`), Unity `Library/`, and Python caches are gitignored; regenerate them from source.
