using System;
using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline
{
    // Loads chunk_CC_RR_names.json TextAssets from Resources at startup and answers
    // "what named road is nearest to world position X?" queries.
    [DisallowMultipleComponent]
    public class RoadNameIndex : MonoBehaviour
    {
        public static RoadNameIndex Instance { get; private set; }

        struct Segment
        {
            public string name;
            public float[] xz; // interleaved x,z pairs
        }

        readonly List<Segment> _segs = new List<Segment>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindObjectOfType<RoadNameIndex>() != null) return;
            var go = new GameObject(nameof(RoadNameIndex));
            go.AddComponent<RoadNameIndex>();
            DontDestroyOnLoad(go);
        }

        void Awake()
        {
            Instance = this;
            Load();
        }

        void Load()
        {
            var manifest = Resources.Load<ChunkManifest>(GeneratedAssets.RuntimeChunkManifest());
            if (manifest == null || manifest.chunks == null) return;

            foreach (var entry in manifest.chunks)
            {
                var coord = new ChunkCoord(entry.col, entry.row);
                var asset = Resources.Load<TextAsset>(GeneratedAssets.RuntimeChunkRoadNames(coord));
                if (asset == null) continue;

                try
                {
                    var parsed = JsonUtility.FromJson<RoadNamesJson>(asset.text);
                    if (parsed?.roads == null) continue;
                    foreach (var r in parsed.roads)
                    {
                        if (string.IsNullOrEmpty(r.n) || r.xz == null || r.xz.Length < 4) continue;
                        _segs.Add(new Segment { name = r.n, xz = r.xz });
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[RoadNameIndex] Failed to parse {coord}_names: {e.Message}");
                }
            }
        }

        // Returns the name of the nearest named road within maxDist meters, or null.
        public string FindNearest(Vector3 worldPos, float maxDist = 25f)
        {
            float px = worldPos.x, pz = worldPos.z;
            float best = maxDist * maxDist;
            string result = null;

            foreach (var seg in _segs)
            {
                float[] xz = seg.xz;
                for (int i = 0; i + 3 < xz.Length; i += 2)
                {
                    float d2 = SegDistSq(px, pz, xz[i], xz[i + 1], xz[i + 2], xz[i + 3]);
                    if (d2 < best)
                    {
                        best = d2;
                        result = seg.name;
                    }
                }
            }
            return result;
        }

        // Returns the two nearest *distinct* named roads at a point, formatted "A St & B St" —
        // the cross streets of the corner it sits on. Falls back to the single name in range, or
        // null if no named road is within <paramref name="radius"/>. Gives taxi fares a street
        // address (#388). Called only when a new fare is dispatched, so the per-call dictionary
        // is not a hot-path allocation.
        public string CrossStreetsNear(Vector3 worldPos, float radius = 30f)
        {
            float px = worldPos.x, pz = worldPos.z;
            float r2 = radius * radius;

            // Closest squared distance seen per street name within the radius.
            var nearest = new Dictionary<string, float>();
            foreach (var seg in _segs)
            {
                float[] xz = seg.xz;
                float segBest = float.MaxValue;
                for (int i = 0; i + 3 < xz.Length; i += 2)
                {
                    float sd = SegDistSq(px, pz, xz[i], xz[i + 1], xz[i + 2], xz[i + 3]);
                    if (sd < segBest) segBest = sd;
                }
                if (segBest > r2) continue;
                if (!nearest.TryGetValue(seg.name, out float cur) || segBest < cur)
                    nearest[seg.name] = segBest;
            }

            if (nearest.Count == 0) return null;

            // Pick the two closest distinct names (a full sort is overkill for two slots).
            string n1 = null, n2 = null;
            float d1 = float.MaxValue, d2 = float.MaxValue;
            foreach (var kv in nearest)
            {
                if (kv.Value < d1) { n2 = n1; d2 = d1; n1 = kv.Key; d1 = kv.Value; }
                else if (kv.Value < d2) { n2 = kv.Key; d2 = kv.Value; }
            }
            return n2 == null ? n1 : $"{n1} & {n2}";
        }

        static float SegDistSq(float px, float pz, float ax, float az, float bx, float bz)
        {
            float dx = bx - ax, dz = bz - az;
            float len2 = dx * dx + dz * dz;
            float t = len2 < 1e-6f ? 0f : Mathf.Clamp01(((px - ax) * dx + (pz - az) * dz) / len2);
            float cx = ax + t * dx, cz = az + t * dz;
            float ex = px - cx, ez = pz - cz;
            return ex * ex + ez * ez;
        }

        // Matches the JSON written by python/sfmap/serialize.py write_road_names()
        [Serializable] class RoadNamesJson { public RoadEntry[] roads; }
        [Serializable] class RoadEntry    { public string n; public float[] xz; }
    }
}
