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
    /// Two-way roads become a forward and a reverse directed edge; one-way roads
    /// (the JSON "o" flag) get only the forward edge, so cars can't drive against
    /// the flow or U-turn back down them (#187). Lane/width detail beyond road width
    /// isn't serialised and cars drive the centerline — deliberately rudimentary,
    /// good enough for ambient traffic. Only <b>named</b> roads are serialised, so
    /// unnamed alleys/service roads are absent from the graph.
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

        // A controlled node whose widest incident road is at least this wide reads as an
        // arterial and is classified Signal rather than an all-way Stop. SF arterials are
        // signalised, residential junctions are all-way stops — a width proxy (#244 design).
        const float SignalWidthThreshold = 11f;

        /// How a junction regulates traffic, derived from graph topology at build time.
        /// <see cref="None"/> for through-nodes (degree ≤ 2 — mid-block welds, bends and
        /// chunk-crop joints); junctions (degree ≥ 3) are <see cref="Stop"/> or
        /// <see cref="Signal"/> by a road-width proxy. v1 traffic treats Signal as Stop.
        public enum IntersectionControl { None, Stop, Signal }

        public readonly struct Edge
        {
            public readonly int FromNode;
            public readonly int ToNode;
            public readonly Vector2[] Points; // XZ centerline, FromNode → ToNode
            public readonly float Length;     // total XZ length, metres
            public readonly float Width;      // road width, metres (0 if not serialised)
            public readonly int Reverse;      // index of the opposite-direction edge, or -1 if one-way

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

        // Per-node intersection classification, filled once by Classify() after Load().
        int[] _degree;                       // distinct neighbour count per node
        IntersectionControl[] _control;      // derived control per node

        // Drivable-road width range across the whole graph, captured once by Classify().
        // The bake collapses OSM highway class into width (primary≈10 … residential≈7 …
        // service≈4), so width is the runtime road-class proxy used to bias spawn density
        // toward arterials (#249). _widthMax <= _widthMin means no usable widths (all-equal
        // or unserialised) → callers treat every road as neutral.
        float _widthMin;
        float _widthMax;

        // Per-node FIFO single-occupancy crossing reservation, created lazily for the
        // controlled nodes that cars actually queue at. See RequestCross/ReleaseCross.
        readonly Dictionary<int, Junction> _junctions = new();

        // A controlled junction's right-of-way state: one car may occupy the crossing at a
        // time; the rest wait in arrival (FIFO) order. Cars are stored as the opaque tokens
        // the contract takes (object), so RoadNetwork never depends on TrafficCar.
        sealed class Junction
        {
            public readonly List<object> Waiting = new(); // arrival-ordered queue (head = front)
            public object Occupant;                        // the car currently crossing, or null
        }

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
            // The road graph must come from the SAME preset the ChunkStreamer streams, but
            // ChunkStreamer only sets GeneratedAssets.ActivePreset from its async OnEnable —
            // which races (and loses) against this Awake. So read the streamer's serialized
            // preset directly (available synchronously at Awake) and pin ActivePreset to it,
            // otherwise we'd load the wrong/default map's graph and no car could spawn on the
            // visible roads. Falls back to the existing ActivePreset when there's no streamer.
            var streamer = FindFirstObjectByType<ChunkStreamer>();
            if (streamer != null && !string.IsNullOrEmpty(streamer.preset))
                GeneratedAssets.ActivePreset = streamer.preset;

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
                    AddPolyline(r.xz, r.w, r.o != 0);
                    polylines++;
                }
            }

            _nodeHash.Clear(); // welding scratch no longer needed
            Classify();        // needs the final _edges/_outgoing, so run after welding
            Debug.Log($"[RoadNetwork] {polylines} centerlines → {_edges.Count} directed edges, " +
                      $"{_nodes.Count} nodes (one-way roads contribute a single edge).");
        }

        // Derives each node's IntersectionControl from graph topology in one O(nodes+edges)
        // pass: degree (distinct neighbours, counting both directions so one-way roads still
        // contribute) decides controlled vs through, and the widest incident road splits
        // Stop from Signal. Pure topology — stable and independent of which chunks stream.
        void Classify()
        {
            int n = _nodes.Count;
            _degree = new int[n];
            _control = new IntersectionControl[n];
            if (n == 0) return;

            var neighbours = new HashSet<int>[n];
            var widest = new float[n];
            for (int i = 0; i < n; i++) neighbours[i] = new HashSet<int>();

            float wmin = float.MaxValue, wmax = 0f;
            foreach (var e in _edges)
            {
                neighbours[e.FromNode].Add(e.ToNode);
                neighbours[e.ToNode].Add(e.FromNode);
                if (e.Width > widest[e.FromNode]) widest[e.FromNode] = e.Width;
                if (e.Width > widest[e.ToNode]) widest[e.ToNode] = e.Width;
                if (e.Width > 0f)                       // ignore unserialised (0-width) edges
                {
                    if (e.Width < wmin) wmin = e.Width;
                    if (e.Width > wmax) wmax = e.Width;
                }
            }
            // Only a usable range when at least one positive width was seen; otherwise leave
            // min == max == 0 so NormalizedWidth falls back to the neutral midpoint.
            _widthMin = wmax > 0f ? wmin : 0f;
            _widthMax = wmax;

            int controlled = 0;
            for (int i = 0; i < n; i++)
            {
                int deg = neighbours[i].Count;
                _degree[i] = deg;
                if (deg <= 2) { _control[i] = IntersectionControl.None; continue; }
                _control[i] = widest[i] >= SignalWidthThreshold
                    ? IntersectionControl.Signal
                    : IntersectionControl.Stop;
                controlled++;
            }
            Debug.Log($"[RoadNetwork] {controlled} controlled junctions (degree ≥ 3) of {n} nodes.");
        }

        void AddPolyline(float[] xz, float width, bool oneWay)
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

            int fwd = _edges.Count;
            if (oneWay)
            {
                // One-way: only the forward edge exists (Reverse = -1). Cars can
                // neither enter from the far end nor U-turn back down it — the
                // point order is already the legal travel direction (#187).
                _edges.Add(new Edge(from, to, pts, length, width, -1));
                _outgoing[from].Add(fwd);
                return;
            }

            var rev = new Vector2[count];
            for (int i = 0; i < count; i++) rev[i] = pts[count - 1 - i];

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

        /// Picks a random outgoing edge from <paramref name="node"/>, avoiding any
        /// U-turn (an exit leading back to the node we just came from) unless that is
        /// the only way out (a dead-end). Returns -1 if the node has no outgoing edges.
        public int NextEdge(int node, int arrivedEdge)
        {
            var outs = _outgoing[node];
            if (outs.Count == 0) return -1;

            // Ban the reverse of the arrived edge (same road, opposite direction) AND
            // any other edge whose destination is the node we just came from — both are
            // U-turns. cameFrom == -1 when there is no arrived edge (fresh spawn).
            int banned   = arrivedEdge >= 0 ? _edges[arrivedEdge].Reverse  : -1;
            int cameFrom = arrivedEdge >= 0 ? _edges[arrivedEdge].FromNode : -1;

            int choices = 0;
            for (int i = 0; i < outs.Count; i++)
            {
                int e = outs[i];
                if (e == banned) continue;
                if (cameFrom >= 0 && _edges[e].ToNode == cameFrom) continue;
                choices++;
            }

            if (choices == 0) return banned >= 0 ? banned : outs[0]; // dead-end: U-turn

            int pick = UnityEngine.Random.Range(0, choices);
            for (int i = 0; i < outs.Count; i++)
            {
                int e = outs[i];
                if (e == banned) continue;
                if (cameFrom >= 0 && _edges[e].ToNode == cameFrom) continue;
                if (pick-- == 0) return e;
            }
            return outs[0]; // unreachable
        }

        // Random probes per spawn query. A small fixed budget keeps the cost trivial while
        // giving the weighted pick a few in-ring candidates to choose between.
        const int SpawnRingProbes = 24;

        /// Returns a random edge whose start node lies within [minDist, maxDist] of
        /// <paramref name="center"/> on the XZ plane, or -1 if none turns up within the
        /// sampling budget. Used to spawn traffic in a ring around the camera.
        ///
        /// <paramref name="classBias"/> (≥ 0) biases the pick toward higher-class (wider)
        /// roads so arterials carry more traffic than residential streets (#249); 0 = the
        /// original uniform-random behaviour. The in-ring probes feed a single-slot weighted
        /// reservoir (weight from <see cref="ClassWeight"/>), so the result is still drawn
        /// from the ring — only the per-edge probability is reweighted. O(probes), no alloc.
        public int RandomEdgeNear(Vector3 center, float minDist, float maxDist, float classBias)
        {
            if (_edges.Count == 0) return -1;
            var c = new Vector2(center.x, center.z);
            float minSq = minDist * minDist, maxSq = maxDist * maxDist;

            int chosen = -1;
            float totalW = 0f;
            for (int a = 0; a < SpawnRingProbes; a++)
            {
                int idx = UnityEngine.Random.Range(0, _edges.Count);
                float d2 = (_nodes[_edges[idx].FromNode] - c).sqrMagnitude;
                if (d2 < minSq || d2 > maxSq) continue;       // outside the spawn ring

                float w = ClassWeight(_edges[idx].Width, classBias);
                totalW += w;
                // Weighted reservoir of size 1: each candidate replaces the incumbent with
                // probability w/totalW, leaving every edge selected ∝ its weight. With
                // classBias == 0 all weights are 1, recovering a uniform in-ring pick.
                if (totalW > 0f && UnityEngine.Random.value <= w / totalW) chosen = idx;
            }
            return chosen;
        }

        // Spawn weight for a road of the given width: 1 + classBias·normalizedWidth, so the
        // widest arterial is (1 + classBias)× as likely to be picked as the narrowest street
        // and every edge keeps a weight ≥ 1 (no road is ever fully starved). Always finite and
        // positive — guards against negative bias, zero width and an all-equal width range.
        float ClassWeight(float width, float classBias)
            => 1f + Mathf.Max(0f, classBias) * NormalizedWidth(width);

        // Maps a road width onto [0,1] across the graph's drivable-width range. Unserialised
        // (≤ 0) widths and a degenerate all-equal range collapse to the neutral midpoint, so
        // pre-width bakes and single-width maps spawn uniformly (no divide-by-zero).
        float NormalizedWidth(float width)
        {
            if (width <= 0f || _widthMax <= _widthMin) return 0.5f;
            return Mathf.Clamp01((width - _widthMin) / (_widthMax - _widthMin));
        }

        public Edge GetEdge(int index) => _edges[index];

        // --- Intersection control (derived; #244 design / #245) ----------------------------

        /// How the junction at <paramref name="node"/> is regulated. <see cref="IntersectionControl.None"/>
        /// for through-nodes (degree ≤ 2) — cars must never pause there — and Stop/Signal for
        /// junctions. Safe before classification (returns None) and for out-of-range indices.
        public IntersectionControl ControlAt(int node) =>
            _control != null && (uint)node < (uint)_control.Length
                ? _control[node] : IntersectionControl.None;

        /// Distinct neighbour count of <paramref name="node"/> (both travel directions).
        public int Degree(int node) =>
            _degree != null && (uint)node < (uint)_degree.Length ? _degree[node] : 0;

        /// Welded XZ position of <paramref name="node"/> — the junction centre cars stop short of.
        public Vector2 GetNode(int node) => _nodes[node];

        // --- Take-turns: per-node FIFO single-occupancy crossing reservation ---------------

        /// Requests permission for <paramref name="car"/> to cross <paramref name="node"/>.
        /// Idempotent: the car joins the FIFO queue on first call and is granted (returns true)
        /// only once it is the queue head AND the crossing is free — giving the all-way-stop
        /// "first to arrive, first to go" cadence. Returns true again for the current occupant
        /// so a car mid-cross keeps moving. The caller MUST <see cref="ReleaseCross"/> once it
        /// has cleared the node, and on cull/destroy, or the junction deadlocks.
        public bool RequestCross(int node, object car)
        {
            if (car == null) return true; // defensive: never block on a null token
            var j = JunctionFor(node);
            if (ReferenceEquals(j.Occupant, car)) return true; // already crossing
            if (!j.Waiting.Contains(car)) j.Waiting.Add(car);  // enqueue once, in arrival order
            if (j.Occupant == null && j.Waiting.Count > 0 && ReferenceEquals(j.Waiting[0], car))
            {
                j.Waiting.RemoveAt(0);
                j.Occupant = car;
                return true;
            }
            return false;
        }

        /// Releases any hold or queue slot <paramref name="car"/> has at <paramref name="node"/>,
        /// freeing the crossing for the next waiter. Safe to call when the car holds nothing.
        public void ReleaseCross(int node, object car)
        {
            if (car == null || !_junctions.TryGetValue(node, out var j)) return;
            if (ReferenceEquals(j.Occupant, car)) j.Occupant = null;
            j.Waiting.Remove(car);
        }

        Junction JunctionFor(int node)
        {
            if (!_junctions.TryGetValue(node, out var j)) { j = new Junction(); _junctions[node] = j; }
            return j;
        }

        // Matches the JSON written by python/sfmap/serialize.py write_road_names().
        [Serializable] class RoadNamesJson { public RoadEntry[] roads; }
        [Serializable] class RoadEntry { public string n; public float[] xz; public float w; public int o; }
    }
}
