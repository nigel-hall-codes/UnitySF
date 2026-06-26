using System.Collections.Generic;
using UnityEngine;

namespace SFMap.Pipeline
{
    /// <summary>
    /// Keeps a roughly constant number of <see cref="TrafficCar"/>s alive in a ring
    /// around the camera by spawning them on the <see cref="RoadNetwork"/> graph and
    /// recycling ones that drive off the streamed area or out of range.
    ///
    /// Cars only spawn where a downward ray currently hits the "Road" layer, so the
    /// manager naturally waits for <see cref="ChunkStreamer"/> to bring a chunk in
    /// before populating it — no car is ever dropped onto ground that isn't there.
    ///
    /// Add via <b>SFMap ▸ Add Traffic System</b> (auto-wires the car prefabs), or drop
    /// this component on an empty GameObject and assign <see cref="carPrefabs"/> by hand.
    /// </summary>
    [AddComponentMenu("SFMap/Traffic Manager")]
    [DisallowMultipleComponent]
    public class TrafficManager : MonoBehaviour
    {
        [Header("Population")]
        [Tooltip("How many cars to keep alive around the target at once.")]
        [Min(0)] public int targetCount = 25;

        [Tooltip("Low-poly car prefabs to spawn at random. Wire these in the Inspector " +
                 "or use SFMap ▸ Add Traffic System.")]
        public GameObject[] carPrefabs;

        [Header("Targeting")]
        [Tooltip("Transform the traffic ring follows. Empty = the ChunkStreamer's target, " +
                 "else Camera.main.")]
        public Transform target;

        [Tooltip("Cars spawn no closer than this to the target (metres) — keeps them from " +
                 "popping in under the player's nose.")]
        [Min(0f)] public float spawnInner = 40f;

        [Tooltip("Cars spawn no further than this from the target (metres).")]
        [Min(1f)] public float spawnOuter = 180f;

        [Tooltip("Cars beyond this distance from the target are recycled (metres). Keep it " +
                 "above spawnOuter so freshly spawned cars aren't culled immediately.")]
        [Min(1f)] public float despawnRadius = 260f;

        [Header("Motion")]
        [Min(0f)] public float minSpeed = 6f;
        [Min(0f)] public float maxSpeed = 12f;

        [Tooltip("Height the car body rides above the road surface (metres).")]
        public float rideHeight = 0.05f;

        [Tooltip("Yaw applied to the model so its forward axis points along travel. " +
                 "Set 180 if cars drive backwards, ±90 if sideways.")]
        public float modelYawOffset = 0f;

        [Header("Pacing")]
        [Tooltip("Seconds between population evaluations.")]
        [Min(0.05f)] public float updateInterval = 0.4f;

        [Tooltip("Max cars to spawn per evaluation (spreads instantiate cost over frames).")]
        [Min(1)] public int maxSpawnsPerTick = 3;

        readonly List<TrafficCar> _cars = new();
        int _mask;
        float _timer;
        bool _warnedNoPrefabs;

        void Start()
        {
            int roadLayer = LayerMask.NameToLayer("Road");
            if (roadLayer < 0)
            {
                Debug.LogWarning("[TrafficManager] No 'Road' layer in the project — bake/import a " +
                                 "map so it exists. Disabling.", this);
                enabled = false;
                return;
            }
            _mask = 1 << roadLayer;
        }

        void Update()
        {
            _timer += Time.deltaTime;
            if (_timer < updateInterval) return;
            _timer = 0f;

            var t = ResolveTarget();
            if (t == null) return;

            var net = RoadNetwork.Instance;
            if (net == null || !net.IsReady) return;

            Cull(t.position);
            Refill(net, t.position);
        }

        Transform ResolveTarget()
        {
            if (target != null) return target;
            var streamer = FindFirstObjectByType<ChunkStreamer>();
            if (streamer != null && streamer.target != null) return streamer.target;
            return Camera.main != null ? Camera.main.transform : null;
        }

        void Cull(Vector3 center)
        {
            float maxSq = despawnRadius * despawnRadius;
            for (int i = _cars.Count - 1; i >= 0; i--)
            {
                var car = _cars[i];
                if (car == null) { _cars.RemoveAt(i); continue; }

                if (car.Done || SqXZ(car.transform.position, center) > maxSq)
                {
                    Destroy(car.gameObject);
                    _cars.RemoveAt(i);
                }
            }
        }

        void Refill(RoadNetwork net, Vector3 center)
        {
            if (carPrefabs == null || carPrefabs.Length == 0)
            {
                if (!_warnedNoPrefabs)
                {
                    Debug.LogWarning("[TrafficManager] No car prefabs assigned — nothing to spawn.", this);
                    _warnedNoPrefabs = true;
                }
                return;
            }

            int spawns = 0;
            while (_cars.Count < targetCount && spawns < maxSpawnsPerTick)
            {
                if (!TrySpawn(net, center)) break; // no valid spot streamed in this tick
                spawns++;
            }
        }

        bool TrySpawn(RoadNetwork net, Vector3 center)
        {
            int edge = net.RandomEdgeNear(center, spawnInner, spawnOuter);
            if (edge < 0) return false;

            Vector2 start = net.GetEdge(edge).Points[0];
            var origin = new Vector3(start.x, 1000f, start.y);
            if (!Physics.Raycast(origin, Vector3.down, out var hit, 2000f, _mask, QueryTriggerInteraction.Ignore))
                return false; // the chunk holding this road hasn't streamed in yet

            var prefab = carPrefabs[Random.Range(0, carPrefabs.Length)];
            if (prefab == null) return false;

            Vector3 surface = hit.point + Vector3.up * rideHeight;
            var go = Instantiate(prefab, surface, Quaternion.identity, transform);
            go.name = $"TrafficCar_{prefab.name}";

            // Ambient traffic is kinematic and ghost-like by design: drop any physics and
            // colliders the prefab ships with so cars never fight the transform driver or
            // collide with the player.
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>()) rb.isKinematic = true;
            foreach (var col in go.GetComponentsInChildren<Collider>()) col.enabled = false;

            var car = go.GetComponent<TrafficCar>();
            if (car == null) car = go.AddComponent<TrafficCar>();
            car.Init(net, edge, surface, Random.Range(minSpeed, maxSpeed), _mask, modelYawOffset, rideHeight);

            _cars.Add(car);
            return true;
        }

        static float SqXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
