using System;
using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline
{
    /// <summary>
    /// Builds a navigable road graph at runtime from the same chunk_CC_RR_names.json
    /// centerline data that <see cref="RoadNameIndex"/> uses for its spatial query.
    ///
    /// Each JSON entry is one StreetEdge centerline (the importer splits ways at
    /// intersections), so the first and last point of every polyline is an
    /// intersection — or a chunk-boundary crop point. Welding those endpoints
    /// together (within <see cref="SnapTolerance"/>) reconstructs a connected graph of
    /// nodes and directed edges that <see cref="TrafficCar"/> drives along.
    ///
    /// One-way / lane / width data isn't serialised, so every road becomes a forward
    /// and a reverse directed edge and cars drive the centerline. That's deliberately
    /// rudimentary — good enough for ambient traffic. Only <b>named</b> roads are
    /// serialised, so unnamed alleys/service roads are absent from the graph.
    ///
    /// Self-bootstraps after the scene loads, mirroring <see cref="RoadNameIndex"/>;
    /// by then an in-scene <see cref="ChunkStreamer"/> has set
    /// <see cref="GeneratedAssets.ActivePreset"/>, so the right preset's data loads.
    /// </summary>
    [DisallowMultipleComponent]
    public class RoadNetwork : MonoBehaviour
    {
        public static RoadNetwork Instance { get; private set; }

        // Endpoints within this many metres of each other are treated as one node.
        // Comfortably larger than the JSON's 3-decimal rounding, small enough never to
        // merge two genuinely distinct intersections.
        const float SnapTolerance = 0.6f;

        public readonly struct Edge
        {
            public readonly int FromNode;
            public readonly int ToNode;
            public readonly Vector2[] Points; // XZ centerline, FromNode → ToNode
            public readonly float Length;     // total XZ length, metres
            public readonly float Width;      // road width, metres (0 if not serialised)
            public readonly int Reverse;      // index of the opposite-direction edge

            public Edge(int from, int to, Vector2[] pts, float length, float width, int reverse)
            {
                FromNode = from; ToNode = to; Points = pts;
                Length = length; Width = width; Reverse = reverse;
            }
        }

        readonly List<Vector2> _nodes = new();
        readonly List<Edge> _edges = new();
        readonly List<List<int>> _outgoing = new();      // node index → outgoing edge indices
        readonly Dictionary<long, List<int>> _nodeHash = new(); // spatial hash for endpoint welding

        /// True once at least one drivable edge has been built.
        public bool IsReady => _edges.Count > 0;
        public int EdgeCount => _edges.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindFirstObjectByType<RoadNetwork>() != null) return;
            var go = new GameObject(nameof(RoadNetwork));
            go.AddComponent<RoadNetwork>();
            DontDestroyOnLoad(go);
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            Load();
        }

        void Load()
        {
            var manifest = Resources.Load<ChunkManifest>(GeneratedAssets.RuntimeChunkManifest());
            if (manifest == null || manifest.chunks == null)
            {
                Debug.LogWarning("[RoadNetwork] No chunk manifest; traffic graph is empty.");
                return;
            }

            int polylines = 0;
            foreach (var entry in manifest.chunks)
            {
                var coord = new ChunkCoord(entry.col, entry.row);
                var asset = Resources.Load<TextAsset>(GeneratedAssets.RuntimeChunkRoadNames(coord));
                if (asset == null) continue;

                RoadNamesJson parsed;
                try { parsed = JsonUtility.FromJson<RoadNamesJson>(asset.text); }
                catch (Exception e)
                {
                    Debug.LogWarning($"[RoadNetwork] Failed to parse {coord}_names: {e.Message}");
                    continue;
                }
                if (parsed?.roads == null) continue;

                foreach (var r in parsed.roads)
                {
                    if (r.xz == null || r.xz.Length < 4) continue;
                    AddPolyline(r.xz, r.w);
                    polylines++;
                }
            }

            _nodeHash.Clear(); // welding scratch no longer needed
            Debug.Log($"[RoadNetwork] {_edges.Count / 2} roads → {_edges.Count} directed edges, " +
                      $"{_nodes.Count} nodes (from {polylines} centerlines).");
        }

        void AddPolyline(float[] xz, float width)
        {
            int count = xz.Length / 2;
            var pts = new Vector2[count];
            for (int i = 0; i < count; i++)
                pts[i] = new Vector2(xz[i * 2], xz[i * 2 + 1]);

            int from = NodeAt(pts[0]);
            int to = NodeAt(pts[count - 1]);
            if (from == to) return; // closed loop or degenerate — nothing to navigate

            float length = 0f;
            for (int i = 0; i + 1 < count; i++)
                length += Vector2.Distance(pts[i], pts[i + 1]);
            if (length < 1f) return; // too short to bother driving

            var rev = new Vector2[count];
            for (int i = 0; i < count; i++) rev[i] = pts[count - 1 - i];

            int fwd = _edges.Count;
            int back = fwd + 1;
            _edges.Add(new Edge(from, to, pts, length, width, back));
            _edges.Add(new Edge(to, from, rev, length, width, fwd));
            _outgoing[from].Add(fwd);
            _outgoing[to].Add(back);
        }

        // Returns the index of an existing node within SnapTolerance of p, creating
        // one if none exists. A 3×3 spatial-hash neighbourhood keeps this near O(1).
        int NodeAt(Vector2 p)
        {
            long cx = (long)Mathf.Floor(p.x / SnapTolerance);
            long cz = (long)Mathf.Floor(p.y / SnapTolerance);

            float bestSq = SnapTolerance * SnapTolerance;
            int best = -1;
            for (long dx = -1; dx <= 1; dx++)
            for (long dz = -1; dz <= 1; dz++)
            {
                if (!_nodeHash.TryGetValue(Key(cx + dx, cz + dz), out var bucket)) continue;
                foreach (int ni in bucket)
                {
                    float d2 = (_nodes[ni] - p).sqrMagnitude;
                    if (d2 < bestSq) { bestSq = d2; best = ni; }
                }
            }
            if (best >= 0) return best;

            int index = _nodes.Count;
            _nodes.Add(p);
            _outgoing.Add(new List<int>());
            long key = Key(cx, cz);
            if (!_nodeHash.TryGetValue(key, out var list)) { list = new List<int>(); _nodeHash[key] = list; }
            list.Add(index);
            return index;
        }

        static long Key(long cx, long cz) => (cx << 32) ^ (cz & 0xffffffffL);

        /// Picks a random outgoing edge from <paramref name="node"/>, avoiding an
        /// immediate U-turn back down <paramref name="arrivedEdge"/> unless that's the
        /// only way out (a dead-end). Returns -1 if the node has no outgoing edges.
        public int NextEdge(int node, int arrivedEdge)
        {
            var outs = _outgoing[node];
            if (outs.Count == 0) return -1;

            int banned = arrivedEdge >= 0 ? _edges[arrivedEdge].Reverse : -1;

            int choices = 0;
            for (int i = 0; i < outs.Count; i++)
                if (outs[i] != banned) choices++;

            if (choices == 0) return banned >= 0 ? banned : outs[0]; // dead-end: U-turn

            int pick = UnityEngine.Random.Range(0, choices);
            for (int i = 0; i < outs.Count; i++)
            {
                if (outs[i] == banned) continue;
                if (pick-- == 0) return outs[i];
            }
            return outs[0]; // unreachable
        }

        /// Returns a random edge whose start node lies within [minDist, maxDist] of
        /// <paramref name="center"/> on the XZ plane, or -1 if none turns up within the
        /// sampling budget. Used to spawn traffic in a ring around the camera.
        public int RandomEdgeNear(Vector3 center, float minDist, float maxDist)
        {
            if (_edges.Count == 0) return -1;
            var c = new Vector2(center.x, center.z);
            float minSq = minDist * minDist, maxSq = maxDist * maxDist;
            for (int a = 0; a < 24; a++)
            {
                int idx = UnityEngine.Random.Range(0, _edges.Count);
                float d2 = (_nodes[_edges[idx].FromNode] - c).sqrMagnitude;
                if (d2 >= minSq && d2 <= maxSq) return idx;
            }
            return -1;
        }

        public Edge GetEdge(int index) => _edges[index];

        // Matches the JSON written by python/sfmap/serialize.py write_road_names().
        [Serializable] class RoadNamesJson { public RoadEntry[] roads; }
        [Serializable] class RoadEntry { public string n; public float[] xz; public float w; }
    }
}
