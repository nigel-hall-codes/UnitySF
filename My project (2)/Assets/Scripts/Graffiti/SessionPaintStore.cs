using System.Collections.Generic;
using SFMap.Pipeline;
using UnityEngine;

namespace SFMap.Graffiti
{
    /// <summary>
    /// The GO/NO-GO spike for the graffiti mechanic (#390 / #396): a world-space, chunk-keyed
    /// store of spray dabs that survives chunk streaming. Each dab is a soft round textured quad
    /// (the reused <c>SFMap/DecalUnlitTransparent</c> shader) placed at a world-space hit point,
    /// oriented to the surface normal and offset slightly proud — so it lands on <b>anything</b>
    /// the ray hits without needing per-object UVs.
    ///
    /// Dabs are keyed to a chunk via <see cref="ChunkStreamer.ChunkAt"/>. When a chunk loads
    /// (<see cref="ChunkStreamer.ChunkLoaded"/>, #395) its dabs are (re)instantiated under the
    /// chunk GameObject; when it unloads (<see cref="ChunkStreamer.ChunkUnloaded"/>) the GameObjects
    /// are destroyed but <b>the dab data is kept</b>, so walking back re-emits the tag in place.
    /// Never baked, never written to disk — session-only, proving the streaming path first.
    ///
    /// Self-bootstraps after the scene loads (the TaxiGame/HUD idiom) and spawns its
    /// <see cref="SprayCan"/> input alongside it, so there is no scene wiring.
    /// </summary>
    [DisallowMultipleComponent]
    public class SessionPaintStore : MonoBehaviour
    {
        public static SessionPaintStore Instance { get; private set; }

        // MVP: one can, one colour, one soft round nozzle.
        public static readonly Color SprayColor = new Color(0.85f, 0.12f, 0.55f, 1f); // magenta

        const int MaxDabsPerChunk = 4000;   // memory cap; dabs beyond this in a chunk are dropped
        const float SurfaceOffset = 0.03f;  // metres proud of the surface, to avoid z-fighting

        struct Dab
        {
            public Vector3 pos;
            public Quaternion rot;
            public float size;
        }

        // World-space dab data, keyed by chunk. Survives unload — this is the whole point.
        readonly Dictionary<ChunkCoord, List<Dab>> _dabs = new();
        // Live "Paint" root transform per currently-resident chunk (null/absent when unloaded).
        readonly Dictionary<ChunkCoord, Transform> _roots = new();

        ChunkStreamer _streamer;
        bool _subscribed;
        Mesh _quad;
        Material _mat;

        /// True once the streamer is known and its grid origin is resolved, so a world position
        /// can be mapped to a chunk. Spraying is inert until then.
        public bool Ready => _streamer != null && _streamer.IsReady;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindObjectOfType<SessionPaintStore>() != null) return;
            var go = new GameObject(nameof(SessionPaintStore));
            go.AddComponent<SessionPaintStore>();
            DontDestroyOnLoad(go);
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            BuildResources();

            // Spray input is its own component, spawned here (the TaxiGame -> TaxiHUD idiom).
            if (FindObjectOfType<SprayCan>() == null)
            {
                var can = new GameObject(nameof(SprayCan));
                can.AddComponent<SprayCan>();
                DontDestroyOnLoad(can);
            }
        }

        void OnDestroy()
        {
            if (_subscribed && _streamer != null)
            {
                _streamer.ChunkLoaded -= OnChunkLoaded;
                _streamer.ChunkUnloaded -= OnChunkUnloaded;
            }
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            // The streamer may be created after us; subscribe as soon as it appears. Any chunks it
            // already loaded before we subscribed still get their dabs when they next (re)load.
            if (_subscribed) return;
            _streamer = FindObjectOfType<ChunkStreamer>();
            if (_streamer == null) return;
            _streamer.ChunkLoaded += OnChunkLoaded;
            _streamer.ChunkUnloaded += OnChunkUnloaded;
            _subscribed = true;
            // A chunk resident from before we subscribed is adopted lazily on first spray
            // (see TryGetResidentRoot), so we don't need to reconcile the existing set here.
        }

        /// Record a dab at a world hit and, if its chunk is resident, show it immediately.
        public void AddDab(Vector3 worldPos, Vector3 normal, float size)
        {
            if (!Ready) return;

            var coord = _streamer.ChunkAt(worldPos);
            if (!_dabs.TryGetValue(coord, out var list))
                _dabs[coord] = list = new List<Dab>();
            if (list.Count >= MaxDabsPerChunk) return;

            var dab = new Dab
            {
                pos = worldPos + normal * SurfaceOffset,
                // Face the surface normal, with a random roll so overlapping dabs don't visibly tile.
                rot = Quaternion.LookRotation(normal) * Quaternion.Euler(0f, 0f, Random.value * 360f),
                size = size,
            };
            list.Add(dab);

            if (TryGetResidentRoot(coord, out var root))
                Spawn(dab, root);
        }

        void OnChunkLoaded(ChunkCoord coord, GameObject chunkGo)
        {
            if (chunkGo == null) return;
            var root = EnsureRoot(coord, chunkGo.transform);
            if (_dabs.TryGetValue(coord, out var list))
                foreach (var d in list)
                    Spawn(d, root);
        }

        void OnChunkUnloaded(ChunkCoord coord)
        {
            if (_roots.TryGetValue(coord, out var root))
            {
                if (root != null) Destroy(root.gameObject);
                _roots.Remove(coord);
            }
            // _dabs[coord] is intentionally kept, so the tag re-emits when the chunk returns.
        }

        // Resolve the live paint root for a resident chunk. If the chunk loaded before we subscribed
        // (so we never saw its ChunkLoaded), adopt its GameObject — ChunkStreamer parents each chunk
        // under itself named coord.ToString() — so the first spray there still shows immediately.
        bool TryGetResidentRoot(ChunkCoord coord, out Transform root)
        {
            if (_roots.TryGetValue(coord, out root) && root != null) return true;
            var chunkGo = _streamer != null ? _streamer.transform.Find(coord.ToString()) : null;
            if (chunkGo != null) { root = EnsureRoot(coord, chunkGo); return true; }
            root = null;
            return false;
        }

        Transform EnsureRoot(ChunkCoord coord, Transform chunk)
        {
            if (_roots.TryGetValue(coord, out var root) && root != null) return root;
            // Chunk geometry is baked in world space and spawned at the origin, so a dab's world
            // position maps straight onto a child under the chunk with no local re-basing needed.
            root = new GameObject("Paint").transform;
            root.SetParent(chunk, false);
            _roots[coord] = root;
            return root;
        }

        void Spawn(Dab d, Transform parent)
        {
            var go = new GameObject("dab");
            go.transform.SetParent(parent, false);
            go.transform.SetPositionAndRotation(d.pos, d.rot);
            go.transform.localScale = new Vector3(d.size, d.size, 1f);

            go.AddComponent<MeshFilter>().sharedMesh = _quad;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;                       // shared -> GPU instancing batches them
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        void BuildResources()
        {
            _quad = BuildQuad();

            var shader = Shader.Find("SFMap/DecalUnlitTransparent");
            if (shader == null)
                Debug.LogError("[SessionPaintStore] Shader 'SFMap/DecalUnlitTransparent' not found.", this);
            _mat = new Material(shader) { name = "SprayDab", enableInstancing = true };
            _mat.mainTexture = BuildNozzle();
            _mat.SetColor("_Color", SprayColor);
        }

        // A unit quad in the XY plane facing +Z; LookRotation(normal) then lays it flat on a surface.
        static Mesh BuildQuad()
        {
            var m = new Mesh { name = "SprayDabQuad" };
            m.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f),
                new Vector3( 0.5f, -0.5f, 0f),
                new Vector3(-0.5f,  0.5f, 0f),
                new Vector3( 0.5f,  0.5f, 0f),
            };
            m.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
            m.triangles = new[] { 0, 2, 1, 2, 3, 1 };   // shader is two-sided (Cull Off), so winding is moot
            m.RecalculateBounds();
            return m;
        }

        // A soft round nozzle: white RGB with a radial alpha falloff, so dabs blend into strokes.
        static Texture2D BuildNozzle()
        {
            const int N = 64;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[N * N];
            float c = (N - 1) * 0.5f;
            for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                float dx = (x - c) / c, dy = (y - c) / c;
                float r = Mathf.Sqrt(dx * dx + dy * dy);                     // 0 centre -> 1 edge
                float a = 1f - Mathf.SmoothStep(0.35f, 1f, r);              // solid core, soft rim
                px[y * N + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a) * 255f));
            }
            tex.SetPixels32(px);
            tex.Apply();
            return tex;
        }
    }
}
