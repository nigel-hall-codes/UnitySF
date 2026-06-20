using UnityEngine;

namespace SFMap.Pipeline
{
    public struct HeightmapData
    {
        // [row, col] indexing: row=0 is south (min Z), col=0 is west (min X).
        // Matches Unity TerrainData.SetHeights(xBase, yBase, heights[y,x]) convention.
        public float[,] Values;
        public int Resolution;
        public float MinElevationMeters;
        public float MaxElevationMeters;
    }

    public readonly struct ChunkCoord
    {
        public readonly int Col;
        public readonly int Row;

        public ChunkCoord(int col, int row) { Col = col; Row = row; }
        public override string ToString() => $"chunk_{Col:00}_{Row:00}";
    }

    public struct ChunkBounds
    {
        public ChunkCoord Coord;
        public Rect WorldRect;
    }

    public static class GeneratedAssets
    {
        public const string Root = "Assets/Generated";

        public static string ChunkDir(ChunkCoord c)           => $"{Root}/{c}";
        public static string TerrainAsset(ChunkCoord c)       => $"{ChunkDir(c)}/Terrain.asset";
        public static string RoadMesh(ChunkCoord c, long id)  => $"{ChunkDir(c)}/Roads/road_{id}.mesh";
        public static string IntersectionMesh(ChunkCoord c, long id) => $"{ChunkDir(c)}/Intersections/intersection_{id}.mesh";
        public static string SidewalkMesh(ChunkCoord c, long id)     => $"{ChunkDir(c)}/Sidewalks/sidewalk_{id}.mesh";
        public static string BuildingMesh(ChunkCoord c, long id)     => $"{ChunkDir(c)}/Buildings/building_{id}.mesh";
    }
}
