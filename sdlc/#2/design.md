# Design: Pipeline Architecture

**Issue:** #2  
**Phase:** drafting  
**Date:** 2026-06-19  
**Input:** sdlc/#1/analysis.md, sdlc/#1/prior-art.md, sdlc/#1/plan.md  

---

## Scope

This document defines the architecture of the OSM + elevation → Unity pipeline: the stage order, data flow between stages, asset output structure, and the chunk model that makes future streaming possible without rearchitecting.

The C# type definitions are in `data-model.md`.

---

## Principles

1. **Road wins over terrain.** Terrain is stamped to match road elevation, not the other way around. (BeamNG model, from prior-art.)
2. **All projection at stage boundary.** Lat/lon coordinates are converted to Unity world-space once, at the OSM parser stage. Everything downstream works in Unity coordinates only.
3. **In-editor, offline, no external services.** The pipeline reads local files and produces Unity asset files. No Mapbox, no ArcGIS, no runtime HTTP.
4. **Chunk-organized assets from day one.** Even with a single chunk in v1, all generated assets are placed under a per-chunk folder. The orchestrator can be extended to multi-chunk generation without changing asset paths.
5. **Stages are pure functions over typed data.** Each stage takes a typed input and produces a typed output. No stage reads another stage's intermediate files — data passes in memory.

---

## Stage Order and Data Flow

```
Raw files
  Assets/SFMapData/map.osm
  Assets/SFMapData/Elevation_Contours_20260619.csv
        │
        ▼
[Stage 1] GeoProjection setup
  Input:  OsmBounds (from <bounds> element in OSM file)
  Output: GeoOrigin (center lon/lat + meters-per-degree factors)
  Responsibility: compute the local coordinate origin and projection constants
        │
        ├──────────────────────────────────────┐
        ▼                                      ▼
[Stage 2] OSM Parser                  [Stage 3] Elevation Parser
  Input:  map.osm, GeoOrigin            Input:  Elevation CSV, GeoOrigin
  Output: StreetGraph                   Output: float[,] heightmap (normalized)
        │                                      │
        └───────────────────┬──────────────────┘
                            ▼
                  [Stage 4] Terrain Generator
                    Input:  float[,] heightmap, ChunkBounds
                    Output: TerrainData asset (saved to disk)
                    Side effect: Unity Terrain component placed in scene
                            │
                            ▼
                  [Stage 5] Road Mesh Generator
                    Input:  StreetGraph.Edges, TerrainData
                    Output: List<RoadMeshResult> (mesh + stamped terrain)
                    Side effect: terrain heightmap mutated (road stamp)
                            │
                    ┌───────┴────────────────────┐
                    ▼                            ▼
          [Stage 6] Intersection Generator  [Stage 7] Sidewalk Generator
            Input:  StreetGraph.Nodes,        Input:  List<RoadMeshResult>
                    List<RoadMeshResult>       Output: List<Mesh>
            Output: List<Mesh>
                    │
                    └──────────────┐
                                   │
          [Stage 8] Building Generator
            Input:  StreetGraph.Buildings, TerrainData
            Output: List<Mesh>
                    │
        ┌───────────┴───────────────────────┐
        ▼                                   │
[Stage 9] Orchestrator (Editor Window)      │
  Collects all stage outputs ◄──────────────┘
  Places GameObjects in scene
  Saves assets to Assets/Generated/
  Reports counts + time
```

Stages 2 and 3 run in parallel (no shared mutable state).  
Stages 6, 7, and 8 run in parallel after Stage 5 (each reads but does not mutate the terrain after Stage 5 is complete).

---

## Coordinate System

**Origin:** center of the OSM `<bounds>` bounding box, projected to `(0, 0, 0)` in Unity world space.

**Axes:**
- X = East (positive) / West (negative) in meters
- Y = Up (elevation in meters above the minimum contour elevation in the bounding box)
- Z = North (positive) / South (negative) in meters

**Projection formula (simplified Mercator, adequate for ~2km extent):**
```
worldX = (lon - originLon) × metersPerDegLon
worldZ = (lat - originLat) × metersPerDegLat
metersPerDegLon = cos(originLat × π/180) × 111,320
metersPerDegLat = 111,320
```

This is accurate to < 1m within the Castro/Noe Valley bounding box (~1.1km × 1.4km). No reprojection needed.

**Y (elevation):** Contour elevations are in feet (SF city data standard). Convert to meters. The minimum elevation within the bounding box becomes Y=0 for the terrain heightmap. Absolute elevation of any point = `heightmap[i,j] × (maxElev - minElev)`.

---

## Asset Output Structure

```
Assets/
  SFMapData/              # raw input — never modified
    map.osm
    Elevation_Contours_20260619.csv

  Generated/
    chunk_00_00/          # v1: single chunk, col=0, row=0
      Terrain.asset       # Unity TerrainData
      Roads/
        road_<osmWayId>.mesh
      Intersections/
        intersection_<osmNodeId>.mesh
      Sidewalks/
        sidewalk_<osmWayId>.mesh
      Buildings/
        building_<osmWayId>.mesh
```

**Why per-chunk folders:** Future streaming adds more chunks (e.g., `chunk_01_00`, `chunk_00_01`). Each chunk maps to one Unity `Scene` + one `Terrain` tile. The orchestrator can be extended to generate N chunks by iterating over chunk coordinates — asset paths are already chunk-namespaced.

**Naming convention:** `<type>_<osmId>` provides a stable, deterministic name. Re-running the pipeline on the same OSM data always produces the same file names, making it safe to diff asset changes in git.

---

## Chunk Model

A **chunk** is the unit of potential future streaming:
- One Unity `Scene` file
- One `TerrainData` asset tiling the chunk's world-space rectangle
- All meshes (roads, intersections, sidewalks, buildings) that fall within the chunk bounds

**v1 chunk definition:**
- `ChunkCoord(0, 0)` — the entire OSM bounding box is one chunk
- Chunk world bounds = `GeoProjection.WorldBounds(osmBounds)` — the full projected extent

**Future streaming extension (not implemented in v1):**
- Subdivide the world bounds into an N×M grid
- Each cell becomes one `ChunkCoord(col, row)`
- Pipeline generates each chunk independently into its own scene + asset folder
- Runtime: additive scene loading/unloading based on player position

**Constraint this places on v1:** Do not hard-code "there is one terrain" anywhere. The orchestrator receives a `List<ChunkBounds>` (v1: length=1) and calls the terrain generator once per chunk. All downstream stage calls take a `ChunkBounds` parameter, not a global singleton.

---

## Stage Contracts

### Stage 1 — GeoProjection setup
- **Input:** `OsmBounds` (min/max lat/lon from `<bounds>` element)
- **Output:** `GeoOrigin` (static; stored on `GeoProjection` class)
- **Must do:** Set `GeoProjection.Origin` before any other stage calls `ToWorldPoint`.

### Stage 2 — OSM Parser
- **Input:** path to `map.osm`, `GeoOrigin`
- **Output:** `StreetGraph`
- **Must do:** All nodes projected to `Vector3` before output. No `double lat/lon` in `StreetNode`.
- **Must not do:** Generate any Unity objects or assets.

### Stage 3 — Elevation Parser
- **Input:** path to elevation CSV, `GeoOrigin`, `ChunkBounds`
- **Output:** `float[,] heightmap` normalized to [0, 1], plus `float minElevMeters`, `float maxElevMeters`
- **Must do:** Clip contour vertices to chunk bounds before triangulation. Emit a warning if fewer than 10 contour vertices exist within bounds (sparse coverage risk).

### Stage 4 — Terrain Generator
- **Input:** `float[,] heightmap`, `float minElevMeters`, `float maxElevMeters`, `ChunkBounds`
- **Output:** `TerrainData` (saved to `Assets/Generated/chunk_<col>_<row>/Terrain.asset`)
- **Must do:** Set `TerrainData.size` = `(worldWidth, heightRangeMeters, worldDepth)` so terrain Y values are in real meters.

### Stage 5 — Road Mesh Generator
- **Input:** `StreetGraph.Edges`, `TerrainData` (mutable reference)
- **Output:** `List<RoadMeshResult>` — each result contains the `Mesh`, OSM way ID, and the road footprint polygon (needed by Stage 6 and 7)
- **Must do:** Stamp terrain before generating road mesh. After stamping, road mesh quads are placed at the stamped elevation.
- **Must not do:** Generate intersection geometry. Roads extend to intersection node positions; trimming happens in Stage 6.

### Stage 6 — Intersection Generator
- **Input:** `StreetGraph.Nodes` where `IsIntersection=true`, `List<RoadMeshResult>`
- **Output:** `List<Mesh>` (intersection polygons)
- **Must do:** Trim `RoadMeshResult.Mesh` quads that overlap the computed intersection polygon. The road mesh objects in the scene are replaced with trimmed versions.
- **Algorithm:** A/B Street miter/bevel join (see `prior-art.md §4`).

### Stage 7 — Sidewalk Generator
- **Input:** `List<RoadMeshResult>`
- **Output:** `List<Mesh>` (one sidewalk strip per road edge, left and right)
- **Elevation:** sidewalk Y = road edge Y + 0.05m (follows road, no terrain sampling needed)

### Stage 8 — Building Generator
- **Input:** `StreetGraph.Buildings`, `TerrainData`
- **Output:** `List<Mesh>` with `MeshCollider` flag set
- **Base Y:** sample terrain at footprint centroid

### Stage 9 — Orchestrator
- **Input:** all stage outputs
- **Output:** placed scene GameObjects, saved assets, generation report
- **Must not do:** contain any geometry logic. It calls stages and places results.

---

## What This Design Leaves Open

The following questions are intentionally deferred to implementation issues:

- Exact Bowyer-Watson implementation or C# library choice for Delaunay triangulation (Stage 3)
- Width values per `HighwayType` enum entry (Stage 5) — need to check OSM `lanes` tag availability in `map.osm`
- Terrain heightmap resolution (default 513×513 — adjustable in orchestrator config)
- Road segment tessellation distance (~1m intervals along centerline — tunable)
- Material assignment (placeholder grey/white for v1)
