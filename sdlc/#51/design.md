# Design: Intersection-First Mesh Generation (#51)

## Current architecture (road-first)

Pipeline order in `SFMapPipelineWindow.RunGenerate()`:

```
OsmParser.Parse()                          → StreetGraph
ElevationParser.Parse()                    → HeightmapData
IntersectionMeshGenerator.ComputeSetbacks  → Dict<StreetEdge, (float from, float to)>
RoadMeshGenerator.Generate(setbacks)       → road meshes  ← roads stamped, trimmed
IntersectionMeshGenerator.Generate()       → intersection patches (covers road seams)
SidewalkMeshGenerator.Generate()           → sidewalk meshes  ← NO setback applied
TerrainGenerator.Generate()
BuildingGenerator.Generate()
```

### Problems

**P1 — Sidewalks bleed into intersections.**
`SidewalkMeshGenerator` accepts no setback parameter and runs the full centerline straight
to the node center. The intersection polygon covers the road seam but the sidewalk overruns it.

**P2 — CollectArms is O(N × E).**
`IntersectionMeshGenerator.CollectEdgeArms` iterates every edge in the graph for every
intersection node. No adjacency list exists on `StreetGraph`.

**P3 — Polygon geometry is computed twice.**
`ComputeSetbacks` and `BuildIntersectionMesh` both run the miter/bevel join math
independently — the arm sort, `ComputeJoin`/`JoinSetbacks` calls, everything.

**P4 — Setback is a scalar, not a position.**
`(float from, float to)` is the distance along the arm to trim. This forces
`TrimCenterline` to reproject from arc-length back to a world position. Storing a
`Vector3` boundary point directly eliminates that indirection and naturally propagates
to sidewalks without changing their geometry code.

**P5 — `setbacks` is optional in `RoadMeshGenerator.Generate()`.**
Making it optional is the design telling us roads don't structurally depend on
intersections. In intersection-first, the boundary is always required for intersection
endpoints (dead-end nodes stay null).

---

## Proposed architecture (intersection-first)

```
OsmParser.Parse()                                  → StreetGraph (+ Adjacency)
ElevationParser.Parse()                            → HeightmapData

// Phase 1 — intersection geometry drives everything
ComputePolygons(graph)                             → Dict<StreetNode, List<Vector2>>
ComputeBoundaries(graph, polygons)                 → Dict<StreetEdge, (Vector3? from, Vector3? to)>

// Phase 2 — road + sidewalk meshes anchored to intersection polygon boundaries
RoadMeshGenerator.Generate(graph, ..., boundaries)
SidewalkMeshGenerator.Generate(graph, ..., boundaries)

// Phase 3 — intersection meshes (reuse precomputed polygons)
IntersectionMeshGenerator.Generate(graph, polygons, ...)

// Phase 4 — terrain, buildings (unchanged)
TerrainGenerator.Generate()
BuildingGenerator.Generate()
```

The miter/bevel join math does not change. The intersection polygon shape does not change.
What changes is when and how the output is consumed.

---

## Decisions

### D1 — Boundary type: `Vector3?` not `float`

Replace `Dictionary<StreetEdge, (float from, float to)>` with
`Dictionary<StreetEdge, (Vector3? from, Vector3? to)>`.

- `null` = endpoint is not an intersection (dead-end node) — road runs to node center as today
- `Vector3` = exact world-space point on the intersection polygon boundary

**Why:** eliminates `TrimCenterline`'s arc-length-to-position reprojection.
`RoadMeshGenerator` and `SidewalkMeshGenerator` replace first/last centerline points
directly. Simpler, more accurate, and both generators share the same input type.

### D2 — Extract polygon computation from `IntersectionMeshGenerator`

Split `BuildIntersectionMesh` into two steps:

```csharp
// new public API
public static Dictionary<StreetNode, List<Vector2>> ComputePolygons(StreetGraph graph);
public static IReadOnlyList<Mesh> Generate(StreetGraph graph,
    Dictionary<StreetNode, List<Vector2>> polygons,
    HeightmapData heightmap, Rect worldRect, ChunkCoord coord);
```

`ComputeBoundaries` and `Generate` both receive the precomputed polygons dict.
No geometry is recomputed.

### D3 — Add adjacency list to `StreetGraph`

```csharp
public IReadOnlyDictionary<StreetNode, IReadOnlyList<StreetEdge>> Adjacency { get; }
```

`OsmParser.BuildEdges` already has all the information to populate this during edge
construction — no second pass needed. `CollectEdgeArms` becomes an O(degree) lookup.

**Constraint:** `StreetNode` is a class (reference type), so dictionary keying by
reference is correct. No equality override needed.

### D4 — `AnchorCenterline` replaces `TrimCenterline`

```csharp
// New in RoadMeshGenerator (internal)
static Vector3[] AnchorCenterline(Vector3[] cl, Vector3? fromPt, Vector3? toPt)
```

If `fromPt` is non-null, replace `cl[0]` with it (or insert between `cl[0]` and `cl[1]`
if `fromPt` is beyond `cl[0]`). Same for `toPt` / `cl[^1]`.

This is simpler than arc-length trimming: the boundary point is guaranteed to lie
along the arm direction from the node, so replacing the endpoint vertex directly is
geometrically correct.

**Edge case:** if the boundary point is *past* an interior centerline vertex (short
road between two close intersections), the same degenerate guard applies —
drop interior points between the two anchors.

### D5 — `SidewalkMeshGenerator` gets boundaries parameter (non-optional)

```csharp
public static IReadOnlyList<Mesh> Generate(
    StreetGraph graph,
    HeightmapData heightmap,
    Rect worldRect,
    ChunkCoord coord,
    IReadOnlyDictionary<StreetEdge, (Vector3? from, Vector3? to)> boundaries)
```

Sidewalk uses the same `AnchorCenterline` logic. The intersection polygon already
covers the intersection area so there is no need for separate sidewalk corner geometry.

---

## File-by-file changes

| File | Change |
|---|---|
| `StreetGraph.cs` | Add `Adjacency` property |
| `OsmParser.cs` | Populate `Adjacency` in `BuildEdges` |
| `IntersectionMeshGenerator.cs` | Extract `ComputePolygons`; add `ComputeBoundaries`; update `Generate` to accept polygons dict; remove `ComputeSetbacks` |
| `RoadMeshGenerator.cs` | Change param type to `(Vector3? from, Vector3? to)`; replace `TrimCenterline` with `AnchorCenterline`; remove `setbacks = null` default |
| `SidewalkMeshGenerator.cs` | Add `boundaries` param; apply `AnchorCenterline` |
| `SFMapPipelineWindow.cs` | Update call order and types; remove `ComputeSetbacks` call |

**No changes to:** `ElevationParser`, `TerrainGenerator`, `BuildingGenerator`,
`PipelineTypes`, `GeoProjection`.

---

## What the miter/bevel math produces as a boundary point

For each arm pair (A, B) the join solves:

```
pa + t * a.Dir = miter   (left edge of arm A)
pb + s * b.Dir = miter   (right edge of arm B)
```

`t` is the setback distance along `a.Dir`. The boundary point in world space is:

```
Vector3 boundaryPt = node.WorldPosition
    + new Vector3(a.Dir.x, 0, a.Dir.y) * t;
```

`ComputeBoundaries` computes this for each arm at each intersection and stores it.
The existing `JoinSetbacks` math is unchanged — only the output is `Vector3` not `float`.

---

## Blast radius

- Miter/bevel join math: **unchanged**
- Heightmap stamping: **unchanged**
- Mesh topology (quad-strips, fan triangulation): **unchanged**
- Intersection polygon shape: **unchanged**
- TerrainGenerator, BuildingGenerator: **untouched**
- No new asset types or serialization formats

The pipeline orchestrator (`SFMapPipelineWindow`) gets two fewer steps (no separate
`ComputeSetbacks`) and one intermediate variable (`polygons`) that feeds both
`ComputeBoundaries` and `Generate`.

---

## Visual outcome

- Sidewalks terminate at the intersection polygon edge — no bleed-through (fixes P1)
- Road mesh endpoints match intersection polygon exactly — same as today but structurally guaranteed
- No regression risk on intersection polygon shape or terrain stamping
