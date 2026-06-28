using System;
using UnityEngine;

namespace SFMap.Pipeline
{
    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public readonly int Col;
        public readonly int Row;

        public ChunkCoord(int col, int row) { Col = col; Row = row; }

        public bool Equals(ChunkCoord other) => Col == other.Col && Row == other.Row;
        public override bool Equals(object obj) => obj is ChunkCoord o && Equals(o);
        public override int GetHashCode() => unchecked((Col * 397) ^ Row);
        public override string ToString() => $"chunk_{Col:00}_{Row:00}";
    }

    public static class GeneratedAssets
    {
        public static string ActivePreset = "test";

        public static string Root          => $"Assets/Generated/{ActivePreset}";
        public static string ResourcesRoot => $"Assets/Resources/Generated/{ActivePreset}";

        public static string ChunkDir(ChunkCoord c)                  => $"{Root}/{c}";
        public static string TerrainAsset(ChunkCoord c)              => $"{ChunkDir(c)}/Terrain.asset";
        public static string TerrainBaseLayer()                      => $"{Root}/Materials/TerrainBaseLayer.terrainlayer";
        public static string RoadMesh(ChunkCoord c, long id)         => $"{ChunkDir(c)}/Roads/road_{id}.mesh";
        public static string IntersectionMesh(ChunkCoord c, long id) => $"{ChunkDir(c)}/Intersections/intersection_{id}.mesh";
        public static string SidewalkMesh(ChunkCoord c, long id)     => $"{ChunkDir(c)}/Sidewalks/sidewalk_{id}.mesh";
        public static string BuildingMesh(ChunkCoord c, long id)     => $"{ChunkDir(c)}/Buildings/building_{id}.mesh";
        // Combined static geometry — one mesh per chunk per type (see SFMapImporterWindow).
        public static string BuildingsCombinedMesh(ChunkCoord c)     => $"{ChunkDir(c)}/Buildings/buildings_combined.mesh";
        public static string IntersectionsCombinedMesh(ChunkCoord c) => $"{ChunkDir(c)}/Intersections/intersections_combined.mesh";
        public static string RoadMaterial()                          => $"{Root}/Materials/RoadSurface.mat";
        public static string SidewalkMaterial()                      => $"{Root}/Materials/SidewalkSurface.mat";
        public static string BuildingMaterial()                      => $"{Root}/Materials/Building.mat";
        public static string ManifestPath()                          => $"{Root}/manifest.json";

        // Runtime Resources paths (prefab per chunk + manifest ScriptableObject)
        public static string ChunkPrefabPath(ChunkCoord c)  => $"{ResourcesRoot}/{c}.prefab";
        public static string ChunkManifestPath()            => $"{ResourcesRoot}/ChunkManifest.asset";

        // Paths passed to Resources.Load at runtime (no "Assets/Resources/" prefix, no extension)
        public static string RuntimeChunkPrefab(ChunkCoord c) => $"Generated/{ActivePreset}/{c}";
        public static string RuntimeChunkManifest()           => $"Generated/{ActivePreset}/ChunkManifest";

        // Road name sidecar — TextAsset imported from chunk_CC_RR_names.json
        public static string ChunkRoadNamesAsset(ChunkCoord c) => $"{ResourcesRoot}/{c}_names.json";
        public static string RuntimeChunkRoadNames(ChunkCoord c) => $"Generated/{ActivePreset}/{c}_names";

        // Parked-car position sidecar — TextAsset imported from chunk_CC_RR_parked.json
        public static string ChunkParkedCarsAsset(ChunkCoord c)   => $"{ResourcesRoot}/{c}_parked.json";
        public static string RuntimeChunkParkedCars(ChunkCoord c) => $"Generated/{ActivePreset}/{c}_parked";
    }

    // JSON layout produced by python/sfmap/serialize.py write_parked_cars().
    // Shared here (not Editor-only) so ParkedCarStreamer can deserialise at runtime.
    [Serializable]
    public class ParkedCarsJson { public ParkedCarJson[] cars; }

    [Serializable]
    public class ParkedCarJson
    {
        public float[] p;   // world position [x, y, z]
        public float   r;   // heading in degrees about +Y
        public float   m;   // [0,1) model selector → floor(m * prefabCount) = index
        public string  s;   // nearest street name (may be empty)
        public long    id;  // source OSM regulation feature id
        public float[] n;   // ground normal [x, y, z] for slope tilt; null/empty → level

        /// <summary>
        /// World rotation for this car: heading <see cref="r"/> about +Y, then tilted so
        /// its up-axis matches the baked ground normal <see cref="n"/> — so on a hill the
        /// car rests on the grade instead of sitting level with one side buried in the road.
        /// Falls back to a level heading when no normal was baked (flat ground) or it is
        /// degenerate. Shared by the runtime streamer and the prefab-bake importer so both
        /// orient cars identically.
        /// </summary>
        public Quaternion Rotation()
        {
            var heading = Quaternion.Euler(0f, r, 0f);
            if (n == null || n.Length < 3) return heading;
            var up = new Vector3(n[0], n[1], n[2]);
            if (up.sqrMagnitude < 1e-6f) return heading;
            up.Normalize();
            var fwd = Vector3.ProjectOnPlane(heading * Vector3.forward, up);
            if (fwd.sqrMagnitude < 1e-6f) return heading;
            return Quaternion.LookRotation(fwd.normalized, up);
        }
    }
}
