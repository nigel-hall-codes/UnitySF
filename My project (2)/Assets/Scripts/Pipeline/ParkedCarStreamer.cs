using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline
{
    /// <summary>
    /// Runtime streamer for parked cars. Loads per-chunk position data from the
    /// <c>_parked.json</c> TextAssets emitted by <see cref="SFMapImporterWindow"/>
    /// and frustum-gates spawning so only cars visible to the camera are alive.
    ///
    /// Cars are managed with a per-prefab object pool — no <c>Instantiate</c>/<c>Destroy</c>
    /// churn per frame. Visibility is tested once per update tick via
    /// <see cref="GeometryUtility"/> so the cost is O(candidate positions), not
    /// O(draw calls).
    ///
    /// Add an <c>IsVisible</c> override for hill occlusion without touching this class.
    /// </summary>
    [AddComponentMenu("SFMap/Parked Car Streamer")]
    [DisallowMultipleComponent]
    public class ParkedCarStreamer : MonoBehaviour
    {
        [Header("Prefabs")]
        [Tooltip("Car prefabs indexed by the m selector in _parked.json. " +
                 "Use the same order as SFMapImporterWindow.CarPrefabPaths.")]
        public GameObject[] carPrefabs;

        [Header("Targeting")]
        [Tooltip("Transform to stream around. Empty = ChunkStreamer.target, then Camera.main.")]
        public Transform target;

        [Tooltip("Chunks within this Chebyshev radius are loaded for parked-car positions.")]
        [Min(0)] public int loadRadius = 1;

        [Header("Spawn budget")]
        [Tooltip("Max cars to activate per update tick (spreads pool-activation cost).")]
        [Min(1)] public int maxSpawnsPerTick = 30;

        [Tooltip("Max distance from the target to keep a parked car active (metres).")]
        [Min(1f)] public float viewRadius = 300f;

        [Tooltip("Uniform scale applied to spawned cars relative to the prefab.")]
        [Min(0.01f)] public float carScale = 0.5f;

        [Header("Pacing")]
        [Tooltip("Seconds between spawn/despawn evaluations.")]
        [Min(0.05f)] public float updateInterval = 0.4f;

        // ---- Manifest / grid ----
        ChunkManifest _manifest;
        float _chunkSize, _originX, _originZ;
        bool _ready;

        // ---- Position data ----
        readonly Dictionary<ChunkCoord, ParkedCarJson[]> _chunkData = new();
        readonly HashSet<ChunkCoord> _loading = new();

        // ---- Active cars, keyed by (chunk, index-within-chunk) ----
        struct CarRecord
        {
            public GameObject go;
            public int        prefabIdx;
            public Vector3    pos;
        }
        readonly Dictionary<(ChunkCoord, int), CarRecord> _active = new();

        // ---- Pool: prefab index → inactive GOs ----
        readonly Dictionary<int, Queue<GameObject>> _pool = new();

        // ---- Frustum planes (pre-allocated, filled via matrix to avoid per-tick GC) ----
        readonly Plane[] _planes = new Plane[6];
        bool _planesValid;

        // ---- Scratch lists (reused to avoid per-tick GC) ----
        readonly List<ChunkCoord>       _removeChunks = new();
        readonly List<(ChunkCoord, int)> _removeCars  = new();

        float _timer;

        // -----------------------------------------------------------------------

        async void OnEnable()
        {
            if (_ready) return;

            var req = Resources.LoadAsync<ChunkManifest>(GeneratedAssets.RuntimeChunkManifest());
            await req;
            if (this == null) return;

            _manifest = req.asset as ChunkManifest;
            if (_manifest?.chunks == null || _manifest.chunks.Length == 0)
            {
                Debug.LogError("[ParkedCarStreamer] No chunk manifest found. " +
                               "Bake the map first (Window › SF Map Importer).", this);
                enabled = false;
                return;
            }

            _chunkSize = _manifest.chunkSizeMeters;
            var a = _manifest.chunks[0];
            _originX = a.worldX - a.col * _chunkSize;
            _originZ = a.worldZ - a.row * _chunkSize;
            _ready = true;
        }

        void OnDisable()
        {
            foreach (var kv in _active)
                if (kv.Value.go != null) Return(kv.Value.go, kv.Value.prefabIdx);
            _active.Clear();
            _chunkData.Clear();
            _loading.Clear();
            _planesValid = false;
        }

        void Update()
        {
            if (!_ready) return;
            _timer += Time.deltaTime;
            if (_timer < updateInterval) return;
            _timer = 0f;

            var t = ResolveTarget();
            if (t == null) return;

            // Update frustum planes once per tick (non-allocating via matrix overload).
            var cam = Camera.main;
            if (cam != null)
            {
                GeometryUtility.CalculateFrustumPlanes(
                    cam.projectionMatrix * cam.worldToCameraMatrix, _planes);
                _planesValid = true;
            }
            else
            {
                _planesValid = false;
            }

            UpdateChunks(WorldToChunk(t.position));
            Despawn(t.position);
            Spawn(t.position);
        }

        // -----------------------------------------------------------------------
        // Chunk data lifecycle
        // -----------------------------------------------------------------------

        void UpdateChunks(ChunkCoord center)
        {
            // Unload data for chunks beyond loadRadius (with one-ring hysteresis).
            _removeChunks.Clear();
            foreach (var c in _chunkData.Keys)
                if (Chebyshev(c, center) > loadRadius + 1)
                    _removeChunks.Add(c);

            foreach (var c in _removeChunks)
            {
                _chunkData.Remove(c);
                _removeCars.Clear();
                foreach (var kv in _active)
                    if (kv.Key.Item1.Equals(c)) _removeCars.Add(kv.Key);
                foreach (var key in _removeCars)
                {
                    Return(_active[key].go, _active[key].prefabIdx);
                    _active.Remove(key);
                }
            }

            // Request data for in-range chunks not yet loaded or loading.
            for (int dc = -loadRadius; dc <= loadRadius; dc++)
            for (int dr = -loadRadius; dr <= loadRadius; dr++)
            {
                var coord = new ChunkCoord(center.Col + dc, center.Row + dr);
                if (!_chunkData.ContainsKey(coord) && !_loading.Contains(coord))
                    _ = LoadAsync(coord);
            }
        }

        async Awaitable LoadAsync(ChunkCoord coord)
        {
            _loading.Add(coord);
            try
            {
                var req = Resources.LoadAsync<TextAsset>(
                    GeneratedAssets.RuntimeChunkParkedCars(coord));
                await req;
                if (this == null) return;

                if (req.asset is TextAsset ta)
                {
                    var data = JsonUtility.FromJson<ParkedCarsJson>(ta.text);
                    if (data?.cars != null)
                        _chunkData[coord] = data.cars;
                }
            }
            finally
            {
                if (this != null) _loading.Remove(coord);
            }
        }

        // -----------------------------------------------------------------------
        // Spawn / despawn
        // -----------------------------------------------------------------------

        void Despawn(Vector3 center)
        {
            float maxSq = viewRadius * viewRadius;
            _removeCars.Clear();

            foreach (var kv in _active)
            {
                var r = kv.Value;
                bool tooFar   = SqXZ(r.pos, center) > maxSq;
                bool notVisible = _planesValid && !IsVisible(r.pos);
                if (tooFar || notVisible)
                    _removeCars.Add(kv.Key);
            }

            foreach (var key in _removeCars)
            {
                Return(_active[key].go, _active[key].prefabIdx);
                _active.Remove(key);
            }
        }

        void Spawn(Vector3 center)
        {
            if (carPrefabs == null || carPrefabs.Length == 0) return;

            float maxSq = viewRadius * viewRadius;
            int spawns = 0;

            foreach (var kv in _chunkData)
            {
                if (spawns >= maxSpawnsPerTick) break;
                var cars = kv.Value;
                for (int i = 0; i < cars.Length; i++)
                {
                    if (spawns >= maxSpawnsPerTick) break;

                    var car = cars[i];
                    if (car.p == null || car.p.Length < 3) continue;

                    var key = (kv.Key, i);
                    if (_active.ContainsKey(key)) continue;

                    var pos = new Vector3(car.p[0], car.p[1], car.p[2]);
                    if (SqXZ(pos, center) > maxSq) continue;
                    if (_planesValid && !IsVisible(pos)) continue;

                    int idx = Mathf.Clamp(
                        Mathf.FloorToInt(car.m * carPrefabs.Length), 0, carPrefabs.Length - 1);
                    if (carPrefabs[idx] == null) continue;

                    var go = Rent(idx);
                    go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, car.r, 0f));
                    go.transform.localScale = carPrefabs[idx].transform.localScale * carScale;

                    _active[key] = new CarRecord { go = go, prefabIdx = idx, pos = pos };
                    spawns++;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Visibility seam — override or extend for hill occlusion (#211)
        // -----------------------------------------------------------------------

        protected virtual bool IsVisible(Vector3 pos)
            => GeometryUtility.TestPlanesAABB(_planes, new Bounds(pos, Vector3.one * 4f));

        // -----------------------------------------------------------------------
        // Pool
        // -----------------------------------------------------------------------

        GameObject Rent(int idx)
        {
            if (_pool.TryGetValue(idx, out var q) && q.Count > 0)
            {
                var go = q.Dequeue();
                go.SetActive(true);
                return go;
            }

            var fresh = Instantiate(carPrefabs[idx], transform);
            // Ambient parked cars are visual only — no physics interactions.
            foreach (var rb  in fresh.GetComponentsInChildren<Rigidbody>()) rb.isKinematic = true;
            foreach (var col in fresh.GetComponentsInChildren<Collider>())  col.enabled    = false;
            return fresh;
        }

        void Return(GameObject go, int idx)
        {
            go.SetActive(false);
            if (!_pool.ContainsKey(idx)) _pool[idx] = new Queue<GameObject>();
            _pool[idx].Enqueue(go);
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        Transform ResolveTarget()
        {
            if (target != null) return target;
            var streamer = FindFirstObjectByType<ChunkStreamer>();
            if (streamer != null && streamer.target != null) return streamer.target;
            return Camera.main != null ? Camera.main.transform : null;
        }

        ChunkCoord WorldToChunk(Vector3 world) => new ChunkCoord(
            Mathf.FloorToInt((world.x - _originX) / _chunkSize),
            Mathf.FloorToInt((world.z - _originZ) / _chunkSize));

        static int Chebyshev(ChunkCoord a, ChunkCoord b) =>
            Mathf.Max(Mathf.Abs(a.Col - b.Col), Mathf.Abs(a.Row - b.Row));

        static float SqXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
