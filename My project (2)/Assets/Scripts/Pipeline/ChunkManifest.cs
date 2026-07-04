using System;
using UnityEngine;

namespace SFMap.Pipeline
{
    [Serializable]
    public class ChunkManifestEntry
    {
        public int   col;
        public int   row;
        public float worldX;
        public float worldZ;
    }

    [CreateAssetMenu(menuName = "SFMap/Chunk Manifest", fileName = "ChunkManifest")]
    public class ChunkManifest : ScriptableObject
    {
        public string               preset;
        public float                chunkSizeMeters;
        public float                minElevation;
        public ChunkManifestEntry[] chunks;

        // Grid origin — the world position of the (col 0, row 0) chunk corner. Derived from any
        // entry, since worldX/worldZ is that chunk's corner. Callers guarantee chunks is non-empty.
        public float OriginX => chunks[0].worldX - chunks[0].col * chunkSizeMeters;
        public float OriginZ => chunks[0].worldZ - chunks[0].row * chunkSizeMeters;

        /// <summary>Map a world position to the chunk (col, row) that contains it.</summary>
        public ChunkCoord WorldToChunk(Vector3 world)
        {
            int col = Mathf.FloorToInt((world.x - OriginX) / chunkSizeMeters);
            int row = Mathf.FloorToInt((world.z - OriginZ) / chunkSizeMeters);
            return new ChunkCoord(col, row);
        }
    }
}
