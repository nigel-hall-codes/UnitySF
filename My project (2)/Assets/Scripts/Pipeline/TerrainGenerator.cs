#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;

namespace SFMap.Pipeline
{
    // Stage 4: converts HeightmapData into a Unity TerrainData asset and saves it to disk.
    public static class TerrainGenerator
    {
        public static TerrainData Generate(HeightmapData heightmap, ChunkBounds bounds, ChunkCoord coord)
        {
            float worldWidth  = bounds.WorldRect.width;
            float worldLength = bounds.WorldRect.height;
            float heightRange = heightmap.MaxElevationMeters - heightmap.MinElevationMeters;

            var terrainData = new TerrainData();
            terrainData.heightmapResolution = heightmap.Resolution;
            terrainData.size = new Vector3(worldWidth, heightRange, worldLength);
            terrainData.SetHeights(0, 0, heightmap.Values);

#if UNITY_EDITOR
            string chunkDir = GeneratedAssets.ChunkDir(coord);
            EnsureFolder(chunkDir);
            string assetPath = GeneratedAssets.TerrainAsset(coord);
            AssetDatabase.CreateAsset(terrainData, assetPath);
            AssetDatabase.SaveAssets();
            Debug.Log($"[TerrainGenerator] Saved {assetPath}");
#endif

            return terrainData;
        }

#if UNITY_EDITOR
        static void EnsureFolder(string path)
        {
            // path = "Assets/Generated/chunk_00_00"
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }
#endif
    }
}
