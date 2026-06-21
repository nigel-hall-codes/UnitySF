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
    }
}
