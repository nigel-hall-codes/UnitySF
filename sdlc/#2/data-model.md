# Data Model: C# Type Sketches

**Issue:** #2  
**Phase:** drafting  
**Date:** 2026-06-19  

These are interface contracts, not implementations. Method bodies are illustrative.

---

## Coordinate System

```csharp
// Immutable projection origin, set once before any stage runs.
public readonly struct GeoOrigin
{
    public readonly double CenterLon;
    public readonly double CenterLat;
    public readonly float MetersPerDegLon;   // cos(lat) × 111,320
    public readonly float MetersPerDegLat;   // 111,320 (constant)
}

public static class GeoProjection
{
    public static GeoOrigin Origin { get; private set; }

    public static void Initialize(OsmBounds bounds)
    {
        // Sets Origin from the bounding box center.
    }

    // Lon/lat → Unity world XZ, Y=0. Call after Initialize().
    public static Vector3 ToWorldPoint(double lon, double lat);

    // Returns the Unity-space Rect covering the full OSM bounds.
    public static Rect WorldBounds(OsmBounds bounds);
}
```

---

## Raw OSM Entities (post-parse, pre-projection)

These types are internal to the parser and not passed downstream.

```csharp
internal struct OsmBounds
{
    public double MinLat, MaxLat, MinLon, MaxLon;

    public double CenterLat => (MinLat + MaxLat) / 2.0;
    public double CenterLon => (MinLon + MaxLon) / 2.0;
}

internal struct OsmRawNode
{
    public long Id;
    public double Lat, Lon;
    public IReadOnlyDictionary<string, string> Tags;
}

internal struct OsmRawWay
{
    public long Id;
    public long[] NodeIds;
    public IReadOnlyDictionary<string, string> Tags;
}

// Relations are parsed but only used for multi-polygon buildings in v1.
internal struct OsmRawRelation
{
    public long Id;
    public OsmRelationMember[] Members;
    public IReadOnlyDictionary<string, string> Tags;
}

internal struct OsmRelationMember
{
    public long Ref;
    public string Type;   // "node" | "way" | "relation"
    public string Role;   // "outer" | "inner" | "via" | ""
}
```

---

## Street Graph (output of OSM Parser, consumed by mesh stages)

```csharp
// A node in the projected street graph.
public class StreetNode
{
    public long OsmId;
    public Vector3 WorldPosition;     // Y=0 until terrain stage runs
    public bool IsIntersection;       // true if referenced by 2+ HighwayEdges
    public HighwayType? TrafficControl; // traffic_signals, stop_sign, or null
}

// One directional road segment between two StreetNodes.
// Ways with 3+ nodes are split into one StreetEdge per OSM node pair,
// with intersection nodes as the split points.
public class StreetEdge
{
    public long OsmWayId;
    public StreetNode From;
    public StreetNode To;
    public HighwayType Type;
    public float Width;              // derived from Type; see HighwayType notes
    public bool IsOneWay;
    public Vector3[] Centerline;     // projected world-space points along the edge
                                     // includes From.WorldPosition and To.WorldPosition
}

// A closed building footprint.
public class BuildingWay
{
    public long OsmId;
    public Vector3[] Footprint;      // world-space polygon, CCW winding, Y=0
    public float Height;             // in meters; derived from tags or default 10m
}

// Top-level output of the OSM parser.
public class StreetGraph
{
    public OsmBounds SourceBounds;
    public IReadOnlyDictionary<long, StreetNode> Nodes;
    public IReadOnlyList<StreetEdge> Edges;
    public IReadOnlyList<BuildingWay> Buildings;

    // Convenience: all nodes where IsIntersection=true
    public IEnumerable<StreetNode> IntersectionNodes =>
        Nodes.Values.Where(n => n.IsIntersection);
}
```

### HighwayType

```csharp
public enum HighwayType
{
    Residential,   // OSM: residential          width: 7m
    Primary,       // OSM: primary              width: 10m
    Secondary,     // OSM: secondary            width: 9m
    Tertiary,      // OSM: tertiary             width: 8m
    Service,       // OSM: service              width: 4m
    Footway,       // OSM: footway/path         — excluded from road stages
    Unclassified,  // OSM: unclassified         width: 6m (fallback)
}

public static class HighwayTypeExtensions
{
    public static float RoadWidth(this HighwayType t) => t switch
    {
        HighwayType.Primary      => 10f,
        HighwayType.Secondary    => 9f,
        HighwayType.Tertiary     => 8f,
        HighwayType.Residential  => 7f,
        HighwayType.Service      => 4f,
        HighwayType.Unclassified => 6f,
        _                        => 0f,   // Footway: caller should skip
    };
}
```

---

## Elevation Stage Types

```csharp
// Output of the Elevation Parser stage.
public struct HeightmapData
{
    // Normalized [0,1] heightmap. [row, col] indexing matches Unity TerrainData.
    public float[,] Values;
    public int Resolution;           // width and height (must be 2^n + 1 for Unity)
    public float MinElevationMeters; // elevation at Values[i,j] = 0
    public float MaxElevationMeters; // elevation at Values[i,j] = 1
}
```

---

## Chunk Types

```csharp
// Identifies one spatial tile. v1: always (0, 0).
public readonly struct ChunkCoord
{
    public readonly int Col;
    public readonly int Row;

    public override string ToString() => $"chunk_{Col:00}_{Row:00}";
}

// The spatial extent of one chunk in Unity world space.
public struct ChunkBounds
{
    public ChunkCoord Coord;
    public Rect WorldRect;    // XZ extent in Unity world space
}
```

---

## Stage Output Types

```csharp
// Output of the Road Mesh Generator for one StreetEdge.
public struct RoadMeshResult
{
    public long OsmWayId;
    public Mesh Mesh;
    public Vector2[] FootprintPolygon;   // XZ outline of road surface (world space)
                                          // used by intersection stage for trimming
    public float[] CenterlineElevations; // Y at each centerline sample point
                                          // used by sidewalk stage
}

// Output of the Intersection Generator for one StreetNode.
public struct IntersectionMeshResult
{
    public long OsmNodeId;
    public Mesh Mesh;
    public HighwayType TrafficControl;   // for future traffic system use
}
```

---

## Asset Path Convention

```csharp
public static class GeneratedAssets
{
    public const string Root = "Assets/Generated";

    public static string ChunkDir(ChunkCoord c) =>
        $"{Root}/{c}";

    public static string TerrainAsset(ChunkCoord c) =>
        $"{ChunkDir(c)}/Terrain.asset";

    public static string RoadMesh(ChunkCoord c, long osmWayId) =>
        $"{ChunkDir(c)}/Roads/road_{osmWayId}.mesh";

    public static string IntersectionMesh(ChunkCoord c, long osmNodeId) =>
        $"{ChunkDir(c)}/Intersections/intersection_{osmNodeId}.mesh";

    public static string SidewalkMesh(ChunkCoord c, long osmWayId) =>
        $"{ChunkDir(c)}/Sidewalks/sidewalk_{osmWayId}.mesh";

    public static string BuildingMesh(ChunkCoord c, long osmWayId) =>
        $"{ChunkDir(c)}/Buildings/building_{osmWayId}.mesh";
}
```

---

## Design Decisions Recorded Here

| Decision | Choice | Rationale |
|---|---|---|
| Node projection timing | At parser output boundary | No double lat/lon after Stage 2; downstream stages can't misuse raw coordinates |
| Way splitting | At intersection nodes | Gives each `StreetEdge` exactly two endpoints; intersection algorithm works on edge endpoints, not raw way nodes |
| Footprint winding | CCW | Unity MeshCollider and ear-clipping triangulators expect consistent winding; CCW matches Unity's left-handed coordinate space convention |
| Chunk type | value struct | Chunks are compared by value, not reference; `ToString()` produces the asset folder name directly |
| HighwayType width | extension method, not field | Width is derived from type; storing it on the edge would allow inconsistencies; callers always see the canonical value |
| BuildingWay height | float field | Set at parse time from `building:levels × 3.5m` or default 10m; mesh stage is pure geometry and doesn't re-inspect tags |
