using System;
using UnityEngine;

namespace SFMap.Pipeline
{
    // Matches the JSON written by python/sfmap/serialize.py write_road_names():
    //   {"roads":[{"n":"Market Street","xz":[x0,z0,x1,z1,...],"w":7.0,"o":1},...]}
    // One DTO shared by RoadNetwork (navigation graph) and RoadNameIndex (street HUD) so the
    // two runtime readers of the names sidecar can't drift (#429). "w"/"o" are only consumed by
    // RoadNetwork; RoadNameIndex ignores them (JsonUtility tolerates the extra fields).
    [Serializable]
    public class RoadNamesJson { public RoadEntry[] roads; }

    [Serializable]
    public class RoadEntry
    {
        public string  n;   // street name
        public float[] xz;  // flattened world-space XZ centreline [x0,z0,x1,z1,...]
        public float   w;   // road width in metres (0 when absent)
        public int     o;   // 1 = one-way in listed point order; 0/absent = two-way
    }

    public static class RoadNames
    {
        /// <summary>
        /// Load and parse a chunk's <c>chunk_CC_RR_names.json</c> TextAsset from Resources.
        /// Returns the road entries, or <c>null</c> when the sidecar is missing or unparseable
        /// (a missing sidecar is normal for chunks with no named roads).
        /// </summary>
        public static RoadEntry[] Load(ChunkCoord coord)
        {
            var asset = Resources.Load<TextAsset>(GeneratedAssets.RuntimeChunkRoadNames(coord));
            if (asset == null) return null;
            try
            {
                return JsonUtility.FromJson<RoadNamesJson>(asset.text)?.roads;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RoadNames] Failed to parse {coord}_names: {e.Message}");
                return null;
            }
        }
    }
}
