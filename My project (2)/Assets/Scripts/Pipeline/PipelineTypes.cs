using UnityEngine;

namespace SFMap.Pipeline
{
    public readonly struct ChunkCoord
    {
        public readonly int Col;
        public readonly int Row;

        public ChunkCoord(int col, int row) { Col = col; Row = row; }
        public override string ToString() => $"chunk_{Col:00}_{Row:00}";
    }

    public static class GeneratedAssets
    {
        public static string ActivePreset = "default";

        public static string Root          => $"Assets/Generated/{ActivePreset}";
        public static string ResourcesRoot => $"Assets/Resources/Generated/{ActivePreset}";

        public static string ChunkDir(ChunkCoord c)                  => $"{Root}/{c}";
        public static string TerrainAsset(ChunkCoord c)              => $"{ChunkDir(c)}/Terrain.asset";
        public static string RoadMesh(ChunkCoord c, long id)         => $"{ChunkDir(c)}/Roads/road_{id}.mesh";
        public static string IntersectionMesh(ChunkCoord c, long id) => $"{ChunkDir(c)}/Intersections/intersection_{id}.mesh";
        public static string SidewalkMesh(ChunkCoord c, long id)     => $"{ChunkDir(c)}/Sidewalks/sidewalk_{id}.mesh";
        public static string BuildingMesh(ChunkCoord c, long id)     => $"{ChunkDir(c)}/Buildings/building_{id}.mesh";
        public static string RoadMaterial()                          => $"{Root}/Materials/RoadSurface.mat";
        public static string SidewalkMaterial()                      => $"{Root}/Materials/SidewalkSurface.mat";
        public static string ManifestPath()                          => $"{Root}/manifest.json";

        // Runtime Resources paths (prefab per chunk + manifest ScriptableObject)
        public static string ChunkPrefabPath(ChunkCoord c)  => $"{ResourcesRoot}/{c}.prefab";
        public static string ChunkManifestPath()            => $"{ResourcesRoot}/ChunkManifest.asset";

        // Paths passed to Resources.Load at runtime (no "Assets/Resources/" prefix, no extension)
        public static string RuntimeChunkPrefab(ChunkCoord c) => $"Generated/{ActivePreset}/{c}";
        public static string RuntimeChunkManifest()           => $"Generated/{ActivePreset}/ChunkManifest";

    }
}
