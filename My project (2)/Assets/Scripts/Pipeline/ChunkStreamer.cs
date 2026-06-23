using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace SFMap.Pipeline
{
    /// <summary>
    /// Runtime streamer for baked map chunks. Loads the chunk prefabs that sit
    /// within <see cref="loadRadius"/> chunks of a target (the main camera by
    /// default) and destroys them once the target moves past
    /// <see cref="unloadRadius"/>.
    ///
    /// All loading uses Unity 6's async APIs so streaming never blocks the main
    /// thread: <see cref="Resources.LoadAsync(string,Type)"/> for the prefab and
    /// <see cref="UnityEngine.Object.InstantiateAsync{T}(T,int,Vector3,Quaternion)"/>
    /// (an <c>AsyncInstantiateOperation</c>) to spawn it, both awaited via
    /// <see cref="Awaitable"/>.
    ///
    /// Chunk prefabs are baked with their geometry already in world space (the
    /// terrain is internally offset by the chunk's worldX/worldZ), so instances
    /// are spawned at the origin; the manifest's worldX/worldZ is used only to
    /// map the target's position onto the chunk grid.
    /// </summary>
    [AddComponentMenu("SFMap/Chunk Streamer")]
    public class ChunkStreamer : MonoBehaviour
    {
        [Tooltip("Preset whose baked chunks to stream. Must match the baked output under Resources/Generated/<preset>.")]
        public string preset = "default";

        [Tooltip("Transform the streaming radius follows. Leave empty to follow Camera.main.")]
        public Transform target;

        [Tooltip("Chunks within this Chebyshev radius (in chunks) of the target are loaded.")]
        [Min(0)] public int loadRadius = 1;

        [Tooltip("Loaded chunks beyond this radius are unloaded. Forced above loadRadius to avoid thrashing at the boundary.")]
        [Min(1)] public int unloadRadius = 2;

        [Tooltip("Maximum number of chunks instantiating concurrently.")]
        [Min(1)] public int maxConcurrentLoads = 2;

        [Tooltip("Seconds between streaming evaluations.")]
        [Min(0f)] public float updateInterval = 0.25f;

        // ---- Manifest-derived state ----
        ChunkManifest _manifest;
        readonly Dictionary<ChunkCoord, ChunkManifestEntry> _entries = new();
        float _chunkSize;
        float _originX, _originZ; // world position of the (col 0, row 0) chunk corner

        // ---- Runtime state ----
        readonly Dictionary<ChunkCoord, GameObject> _loaded = new();
        readonly HashSet<ChunkCoord> _loading = new();
        readonly HashSet<ChunkCoord> _missing = new(); // prefab absent on disk — don't retry every tick
        readonly List<ChunkCoord> _unloadScratch = new();
        readonly List<ChunkCoord> _loadScratch = new();
        int _activeLoads;
        bool _ready;
        CancellationTokenSource _cts;

        // ---- Read-only surface for tooling (e.g. the developer fly mode) ----

        /// True once the manifest has loaded and the grid origin is known.
        public bool IsReady => _ready;

        /// Number of chunk instances currently resident in the scene.
        public int LoadedChunkCount => _loaded.Count;

        /// Map a world position onto the chunk grid. Returns <c>default</c> until
        /// <see cref="IsReady"/>, since the grid origin isn't known before then.
        public ChunkCoord ChunkAt(Vector3 world) => _ready ? WorldToChunk(world) : default;

        async void OnEnable()
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(destroyCancellationToken);
            var token = _cts.Token;
            try
            {
                if (!_ready && !await InitializeAsync(token))
                    return;
                await StreamLoopAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Disabled or destroyed mid-flight — expected, nothing to clean up here.
            }
        }

        void OnDisable()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }

        async Awaitable<bool> InitializeAsync(CancellationToken token)
        {
            GeneratedAssets.ActivePreset = preset;

            var req = Resources.LoadAsync<ChunkManifest>(GeneratedAssets.RuntimeChunkManifest());
            await req;
            token.ThrowIfCancellationRequested();

            _manifest = req.asset as ChunkManifest;
            if (_manifest == null || _manifest.chunks == null || _manifest.chunks.Length == 0)
            {
                Debug.LogError($"[ChunkStreamer] No chunk manifest at Resources/{GeneratedAssets.RuntimeChunkManifest()} " +
                               $"for preset '{preset}'. Bake the map first.", this);
                return false;
            }

            _chunkSize = _manifest.chunkSizeMeters;
            if (_chunkSize <= 0f)
            {
                Debug.LogError($"[ChunkStreamer] Manifest chunkSizeMeters is {_chunkSize}; cannot stream.", this);
                return false;
            }

            if (unloadRadius <= loadRadius)
                unloadRadius = loadRadius + 1;

            _entries.Clear();
            foreach (var e in _manifest.chunks)
                _entries[new ChunkCoord(e.col, e.row)] = e;

            // Derive the grid origin from any entry: worldX/worldZ is that chunk's corner.
            var anchor = _manifest.chunks[0];
            _originX = anchor.worldX - anchor.col * _chunkSize;
            _originZ = anchor.worldZ - anchor.row * _chunkSize;

            ClearStaticChunks();

            _ready = true;
            return true;
        }

        /// Remove the static "SF Map" hierarchy the importer instantiates into the
        /// scene, so the streamed chunks don't double up with it.
        static void ClearStaticChunks()
        {
            var stale = GameObject.Find("SF Map");
            if (stale != null)
            {
                Debug.Log("[ChunkStreamer] Removing static 'SF Map' hierarchy; chunks will be streamed instead.");
                Destroy(stale);
            }
        }

        async Awaitable StreamLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Tick(token);
                await Awaitable.WaitForSecondsAsync(updateInterval, token);
            }
        }

        void Tick(CancellationToken token)
        {
            var t = target != null ? target
                  : Camera.main != null ? Camera.main.transform
                  : null;
            if (t == null)
                return;

            var center = WorldToChunk(t.position);

            // Unload chunks that have drifted past the unload radius.
            _unloadScratch.Clear();
            foreach (var kv in _loaded)
                if (Chebyshev(kv.Key, center) > unloadRadius)
                    _unloadScratch.Add(kv.Key);
            foreach (var c in _unloadScratch)
            {
                var go = _loaded[c];
                _loaded.Remove(c);
                if (go != null)
                    Destroy(go);
            }

            // Queue missing chunks within the load radius, nearest first, up to the concurrency cap.
            int budget = maxConcurrentLoads - _activeLoads;
            if (budget <= 0)
                return;

            _loadScratch.Clear();
            for (int dc = -loadRadius; dc <= loadRadius; dc++)
            for (int dr = -loadRadius; dr <= loadRadius; dr++)
            {
                var c = new ChunkCoord(center.Col + dc, center.Row + dr);
                if (_loaded.ContainsKey(c) || _loading.Contains(c) || _missing.Contains(c) || !_entries.ContainsKey(c))
                    continue;
                _loadScratch.Add(c);
            }
            _loadScratch.Sort((a, b) => Chebyshev(a, center).CompareTo(Chebyshev(b, center)));

            for (int i = 0; i < _loadScratch.Count && budget > 0; i++, budget--)
                _ = LoadChunkAsync(_loadScratch[i], token);
        }

        async Awaitable LoadChunkAsync(ChunkCoord coord, CancellationToken token)
        {
            _loading.Add(coord);
            _activeLoads++;
            try
            {
                var req = Resources.LoadAsync<GameObject>(GeneratedAssets.RuntimeChunkPrefab(coord));
                await req;
                token.ThrowIfCancellationRequested();

                if (req.asset is not GameObject prefab)
                {
                    Debug.LogWarning($"[ChunkStreamer] Missing chunk prefab at " +
                                     $"Resources/{GeneratedAssets.RuntimeChunkPrefab(coord)}.", this);
                    _missing.Add(coord); // don't re-issue the load (and warning) every tick
                    return;
                }

                // Chunk geometry is baked in world space, so spawn at the origin.
                var op = InstantiateAsync(prefab, 1, Vector3.zero, Quaternion.identity);
                await op;

                // The instance already exists once the op completes, so if we were
                // cancelled mid-flight we must destroy it — it isn't parented or tracked
                // yet, so it would otherwise outlive this streamer.
                var go = op.Result[0];
                if (token.IsCancellationRequested)
                {
                    Destroy(go);
                    return;
                }

                go.transform.SetParent(transform, false);
                go.name = coord.ToString();
                _loaded[coord] = go; // a now-stale chunk is reclaimed by the next Tick's unload pass
            }
            catch (OperationCanceledException)
            {
                // Cancelled before the instance was committed to _loaded; nothing to undo.
            }
            finally
            {
                _loading.Remove(coord);
                _activeLoads--;
            }
        }

        ChunkCoord WorldToChunk(Vector3 world)
        {
            int col = Mathf.FloorToInt((world.x - _originX) / _chunkSize);
            int row = Mathf.FloorToInt((world.z - _originZ) / _chunkSize);
            return new ChunkCoord(col, row);
        }

        static int Chebyshev(ChunkCoord a, ChunkCoord b)
            => Mathf.Max(Mathf.Abs(a.Col - b.Col), Mathf.Abs(a.Row - b.Row));
    }
}
