# Design: Python+Unity Offline Preprocessing Split

**Issue**: #61  
**Workflow**: sysdesign  
**Status**: drafting  
**Persona**: systems-architect

---

## Context

The current pipeline runs entirely in C# as a Unity Editor tool. All stages — OSM parsing, elevation triangulation, intersection geometry, mesh generation, terrain stamping, and asset serialization — execute serially in a single editor process. Issue #58 confirmed that compute (Delaunay triangulation, miter/bevel polygon math, mesh vertex generation) dominates wall-clock time, not AssetDatabase I/O.

This design proposes a **hard language boundary**: Python owns computation, C# owns Unity asset creation.

---

## Decision: Where to Draw the Line

Three possible split points were considered:

| Option | Python owns | C# owns | Interchange |
|--------|------------|---------|-------------|
| A. Graph split | OSM parse, elevation parse | Everything else | StreetGraph + HeightmapData |
| B. Geometry split | A + intersection polygons + heightmap stamping | Mesh topology, asset creation | Stamped heightmap + annotated graph |
| C. Mesh split ✓ | A + B + mesh vertex/triangle generation | Asset creation only | Per-chunk binary with full mesh data |

**Decision: Option C (Mesh split).**

Rationale:
- The issue explicitly includes "mesh geometry" in the Python scope.
- Options A and B leave the most expensive operations (quad-strip generation, ear-clipping, fan triangulation) in C#.
- Mesh topology generation has no Unity API dependencies — it's pure arithmetic on float arrays.
- The Python libraries named in the issue (scipy, numpy, shapely, triangle) directly replace the C# algorithms: scipy.spatial.Delaunay replaces Bowyer-Watson, triangle replaces ear-clipping, shapely replaces the miter/bevel intersection math.

**Tradeoff accepted**: The interchange format must carry full vertex/triangle arrays per mesh, making it larger (~1–5MB per chunk). This is acceptable for an offline tool that runs once and imports.

---

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│  PYTHON PIPELINE  (sfmap_bake.py)                           │
│                                                             │
│  map.osm ──► OsmParser ──────► StreetGraph                 │
│              (pyosmium)         nodes, edges, buildings     │
│                                       │                     │
│  elevation.csv ──► ElevationProcessor─┤                     │
│                    (scipy Delaunay)   ▼                     │
│                               ┌─ Per-Chunk Loop ─┐          │
│                               │                  │          │
│                               │  CropToChunk     │          │
│                               │       │          │          │
│                               │       ▼          │          │
│                               │  IntersectionPolygons       │
│                               │  (shapely)       │          │
│                               │       │          │          │
│                               │       ▼          │          │
│                               │  StampHeightmap  │          │
│                               │  (numpy)         │          │
│                               │       │          │          │
│                               │       ▼          │          │
│                               │  MeshGeometry    │          │
│                               │  - Roads (numpy) │          │
│                               │  - Intersections │          │
│                               │  - Sidewalks     │          │
│                               │  - Buildings     │          │
│                               │    (triangle)    │          │
│                               │       │          │          │
│                               │       ▼          │          │
│                               │  Serialize       │          │
│                               │  → chunk_NN.bin  │          │
│                               └──────────────────┘          │
└─────────────────────────────────────────────────────────────┘
                        │
              chunks/default/
              ├── manifest.json
              ├── chunk_00_00.bin
              ├── chunk_01_00.bin
              └── ...
                        │
                        ▼
┌─────────────────────────────────────────────────────────────┐
│  C# UNITY IMPORTER  (SFMapImporterWindow)                   │
│                                                             │
│  For each .bin:                                             │
│    Deserialize ──► MeshData[] + HeightmapData               │
│    new Mesh() ─────► vertices / triangles / normals / UVs  │
│    AssetDatabase.CreateAsset(mesh)                          │
│    new TerrainData() ──► SetHeights()                       │
│    AssetDatabase.CreateAsset(terrain)                       │
│    Build GameObject hierarchy                               │
│    PrefabUtility.SaveAsPrefabAsset()                        │
│                                                             │
│  Create ChunkManifest.asset ──► Resources/Generated/        │
│                                                             │
│  UNCHANGED: WorldStreamer (runtime async loading)           │
└─────────────────────────────────────────────────────────────┘
```

---

## Interchange Format

### Why binary over JSON

| Metric | Binary .bin | JSON |
|--------|------------|------|
| 513×513 heightmap | 1.06 MB | ~4.2 MB |
| 500-vertex mesh (roads) | ~18 KB | ~72 KB |
| Parse cost | memcpy | float parsing |
| Human-readable | No | Yes |

For offline preprocessing with ~16 chunks, binary saves ~50MB and eliminates float-parse overhead in the importer. The manifest.json (metadata only, no arrays) stays JSON for readability.

### chunk_NN.bin format

```
ChunkHeader:
  magic         u32   0x4B4E4843 ("CHNK")
  version       u32   1
  chunk_col     i32
  chunk_row     i32
  world_x       f32
  world_z       f32
  chunk_size_m  f32
  min_elev_m    f32
  max_elev_m    f32
  hmap_res      i32   e.g. 513
  hmap_data     f32[hmap_res * hmap_res]   row-major, normalized [0,1], post-stamp
  mesh_count    i32

MeshEntry (repeated mesh_count times):
  mesh_type     u8    0=road 1=intersection 2=sidewalk 3=building
  osm_id        i64
  vert_count    i32
  idx_count     i32   (triangle indices, always multiple of 3)
  vertices      f32[vert_count * 3]   interleaved x,y,z (Unity left-handed)
  normals       f32[vert_count * 3]   may be zero if C# recalculates
  uvs           f32[vert_count * 2]   interleaved u,v
  indices       u32[idx_count]        CW winding (Unity convention)
```

**Invariants Python must uphold:**
- Heightmap is fully stamped before serialization (roads + intersections have flattened their footprints)
- All vertices are in Unity's left-handed coordinate system: +X right, +Y up, +Z forward
- Triangle winding is clockwise (CW) when viewed from outside the surface
- osm_id is the raw OSM node/way ID as int64 (negative for synthetic elements)

### manifest.json format

No change from current format — same schema, different path:
```json
{
  "preset": "default",
  "generated": "<ISO timestamp>",
  "chunkSize": 1964,
  "chunksX": 2,
  "chunksZ": 2,
  "osmFile": "map.osm",
  "osmBounds": { "minLat": ..., "maxLat": ..., "minLon": ..., "maxLon": ... },
  "heightmapResolution": 513,
  "minElevation": 5.234,
  "maxElevation": 102.4,
  "chunks": [{ "col": 0, "row": 0, "worldX": -982.0, "worldZ": -982.0 }]
}
```

---

## CLI Interface

```bash
python sfmap_bake.py \
  --osm     map.osm \
  --elev    elevation.csv \
  --preset  default \
  --chunk-size 1964 \
  --chunks-x 2 \
  --chunks-z 2 \
  --out     ./chunks/
```

Output:
```
chunks/
  default/
    manifest.json
    chunk_00_00.bin
    chunk_01_00.bin
    chunk_00_01.bin
    chunk_01_01.bin
```

Optional flags:
- `--only 0,0 1,0` — bake specific chunks only (for incremental re-bakes)
- `--hmap-res 513` — heightmap resolution (must match across all chunks)
- `--no-sidewalks` — skip sidewalk geometry (faster for prototyping)

---

## Python Module Structure

```
python/
  sfmap_bake.py          # CLI entry point (argparse)
  sfmap/
    __init__.py
    osm.py               # pyosmium parser → StreetGraph dataclass
    elevation.py         # CSV → numpy heightmap via scipy Delaunay
    projection.py        # lat/lon → world XZ (mirrors GeoProjection.cs)
    geometry/
      intersection.py    # shapely miter/bevel polygons + boundary setbacks
      road.py            # numpy quad-strip mesh generation
      sidewalk.py        # numpy quad-strip (4 verts/point)
      building.py        # triangle lib ear-clipping extrusion
    stamping.py          # numpy heightmap stamping (road + intersection)
    chunk.py             # CropToChunk + per-chunk orchestration
    serialize.py         # binary .bin writer + manifest.json writer
```

---

## C# Changes

### What gets deleted

| File | Fate |
|------|------|
| `OsmParser.cs` | Delete |
| `ElevationParser.cs` | Delete |
| `GeoProjection.cs` | Delete |
| `RoadMeshGenerator.cs` | Delete |
| `IntersectionMeshGenerator.cs` | Delete |
| `SidewalkMeshGenerator.cs` | Delete |
| `TerrainGenerator.cs` | Delete |
| `BuildingGenerator.cs` | Delete |
| `SFMapPipelineWindow.cs` | Replace with `SFMapImporterWindow.cs` |

### What stays

| File | Fate |
|------|------|
| `ChunkManifest.cs` | Unchanged (ScriptableObject, runtime) |
| `WorldStreamer.cs` | Unchanged (runtime async load) |
| `PipelineTypes.cs` | Trim to types still needed by importer |

### SFMapImporterWindow responsibilities

1. Path picker: chunk directory + preset name
2. Read manifest.json → chunk list
3. For each chunk:
   - Read binary .bin → `ChunkData` struct
   - `new Mesh()` per MeshEntry → assign arrays → `Mesh.RecalculateNormals()` if normals zero
   - `AssetDatabase.CreateAsset(mesh, path)`
   - `new TerrainData()` → `SetHeights()` from heightmap → `CreateAsset`
   - Build `GameObject` hierarchy (same structure as before)
   - `PrefabUtility.SaveAsPrefabAsset()`
4. Create `ChunkManifest` ScriptableObject → `CreateAsset` in Resources

The importer is simpler than the generator: no algorithms, just data → API calls.

---

## Ordering Constraint: Heightmap Stamping

The current pipeline stamps the heightmap in-place across three stages (roads, then intersections, then the final TerrainData consumes it). In the Python design, stamping order is:

1. Initialize float heightmap from Delaunay interpolation
2. Stamp all intersections first (circular regions)
3. Stamp all roads (linear regions, may overlap intersection stamps — that's fine)
4. Serialize the resulting heightmap once

Buildings read the stamped heightmap for base elevation (centroid sample). This ordering is preserved.

---

## Key Risks

| Risk | Mitigation |
|------|------------|
| Winding order mismatch (Python CW vs Unity convention) | Document invariant; add validation step in importer that checks normal direction on a test mesh |
| Coordinate system mismatch (Python +Y vs Unity +Y up) | Mirror GeoProjection.cs math exactly in `projection.py`; unit-test against known OSM coordinates |
| pyosmium not available on Windows | Test on dev machine; provide fallback to pure-Python XML parser for Windows users |
| triangle library license (GPL) | Verify license compatibility; alternative: implement ear-clipping in numpy |
| Binary format version drift | Include `version` field in header; importer rejects mismatched versions with clear error |
| Per-chunk .bin files grow large (many meshes) | Benchmark; if >20MB/chunk, consider splitting mesh arrays into separate files |

---

## Implementation Issues

The following leaf issues should be created to implement this design:

1. `chore(python): set up sfmap Python package and CLI entry point` — directory structure, argparse, venv/requirements.txt
2. `feat(python): implement OSM parser using pyosmium → StreetGraph` — replaces OsmParser.cs
3. `feat(python): implement elevation processor using scipy Delaunay` — replaces ElevationParser.cs
4. `feat(python): implement intersection polygon geometry using shapely` — replaces IntersectionMeshGenerator phase 1/2
5. `feat(python): implement heightmap stamping in numpy` — replaces stamping calls in road/intersection generators
6. `feat(python): implement road and sidewalk mesh generation in numpy` — replaces RoadMeshGenerator + SidewalkMeshGenerator
7. `feat(python): implement building mesh generation using triangle lib` — replaces BuildingGenerator
8. `feat(python): implement chunk binary serializer (.bin format)` — defines interchange format
9. `feat(pipeline): implement C# SFMapImporterWindow for .bin → Unity assets` — replaces SFMapPipelineWindow
10. `refactor(pipeline): remove old C# generator classes after Python pipeline validates` — cleanup

Issues 1–8 can proceed in parallel (Python work). Issue 9 can begin once issue 8's format is finalized. Issue 10 is last.
