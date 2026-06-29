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
        [Tooltip("Cruise speed on the narrowest drivable road (m/s). Wider roads scale up " +
                 "toward maxSpeed; each car gets mild variation around this.")]
        [Min(0f)] public float minSpeed = 6f;

        [Tooltip("Cruise speed on the widest (arterial) road (m/s).")]
        [Min(0f)] public float maxSpeed = 12f;

        [Tooltip("Height the car body rides above the road surface (metres).")]
        public float rideHeight = 0.05f;

        [Tooltip("Yaw applied to the model so its forward axis points along travel. " +
                 "Set 180 if cars drive backwards, ±90 if sideways.")]
        public float modelYawOffset = 0f;

        [Tooltip("Uniform scale applied to spawned cars relative to the prefab. " +
                 "0.5 = half the prefab's size.")]
        [Min(0.01f)] public float carScale = 0.5f;

        [Header("Lanes")]
        [Tooltip("Physical width of one lane (metres). Sets lane count from road width " +
                 "and centres each car in its assigned lane. Typical SF lane: 3–3.5 m.")]
        [Min(1f)] public float laneWidth = 3.5f;

        [Tooltip("Only use lane offsets on roads at least this wide (metres). Narrower " +
                 "single-track roads stay on the centerline. ~6.5 ≈ two lanes.")]
        [Min(0f)] public float multiLaneMinWidth = 6.5f;

        [Tooltip("Minimum seconds a car waits before considering a lane change.")]
        [Min(1f)] public float laneChangePeriodMin = 5f;

        [Tooltip("Maximum seconds a car waits before considering a lane change.")]
        [Min(1f)] public float laneChangePeriodMax = 15f;

        [Tooltip("Seconds to complete a lane change (smooth lerp between lanes).")]
        [Min(0.5f)] public float laneChangeDuration = 2f;

        [Header("Intersections")]
        [Tooltip("How far ahead (metres) a car starts watching a controlled junction so it " +
                 "can ease to a stop and take its turn. ~25 reads naturally at city speeds.")]
        [Min(1f)] public float intersectionApproach = 25f;

        [Tooltip("How far back from the junction centre (metres) cars hold at the stop line.")]
        [Min(0f)] public float stopSetback = 4f;

        [Header("Speed & Following")]
        [Tooltip("How quickly cars speed up toward their cruise (m/s²).")]
        [Min(0.1f)] public float acceleration = 4f;

        [Tooltip("How quickly cars slow down (m/s²). Usually higher than acceleration — braking " +
                 "is sharper than pulling away.")]
        [Min(0.1f)] public float braking = 8f;

        [Tooltip("Seconds of gap each car keeps to the car ahead (car-following headway). " +
                 "Larger = more cautious, longer following distance.")]
        [Min(0.1f)] public float timeHeadway = 1.2f;

        [Tooltip("Bumper standstill gap to the car ahead (metres) — how close a stopped car " +
                 "sits behind its leader.")]
        [Min(0f)] public float minFollowGap = 5f;

        [Tooltip("Per-car cruise variation (±fraction) so cars don't all drive identically. " +
                 "0.15 = ±15%.")]
        [Range(0f, 0.5f)] public float speedVariation = 0.15f;

        [Header("Pacing")]
        [Tooltip("Seconds between population evaluations.")]
        [Min(0.05f)] public float updateInterval = 0.4f;

        [Tooltip("Max cars to spawn per evaluation (spreads instantiate cost over frames).")]
        [Min(1)] public int maxSpawnsPerTick = 3;

        readonly List<TrafficCar> _cars = new();

        // Per-frame edge → cars index, used by FindLeader so a car looks at only the handful
        // of cars sharing its edge instead of the whole population. Rebuilt at most once per
        // frame (O(cars)); see FindLeader.
        readonly Dictionary<int, List<TrafficCar>> _byEdge = new();
        int _bucketFrame = -1;

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
                    // Backstop: free any junction reservation before the car vanishes, else a
                    // car culled mid-crossing would wedge that junction for everyone (#245).
                    car.ReleaseReservation();
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
            go.transform.localScale *= carScale;

            // Ambient traffic is kinematic and ghost-like by design: drop any physics and
            // colliders the prefab ships with so cars never fight the transform driver or
            // collide with the player.
            foreach (var rb in go.GetComponentsInChildren<Rigidbody>()) rb.isKinematic = true;
            foreach (var col in go.GetComponentsInChildren<Collider>()) col.enabled = false;

            var car = go.GetComponent<TrafficCar>();
            if (car == null) car = go.AddComponent<TrafficCar>();
            car.Init(net, this, edge, surface, _mask, modelYawOffset,
                     rideHeight, multiLaneMinWidth, laneWidth,
                     laneChangePeriodMin, laneChangePeriodMax, laneChangeDuration,
                     intersectionApproach, stopSetback,
                     minSpeed, maxSpeed, acceleration, braking,
                     timeHeadway, minFollowGap, speedVariation);

            _cars.Add(car);
            return true;
        }

        /// The nearest car ahead of <paramref name="self"/> on the same edge and lane, or null
        /// if the road ahead is clear. Cars call this every frame for car-following.
        ///
        /// Cost: the edge→cars buckets are rebuilt at most once per frame (O(cars) total), and
        /// each call scans only the cars sharing this one edge — typically a few. So the whole
        /// system stays close to O(cars) per frame rather than the naive O(cars²) all-pairs scan,
        /// even if targetCount is raised well past the default.
        public TrafficCar FindLeader(TrafficCar self, int edge, int lane, float selfDistToEnd)
        {
            if (_bucketFrame != Time.frameCount) { RebuildBuckets(); _bucketFrame = Time.frameCount; }
            if (!_byEdge.TryGetValue(edge, out var list)) return null;

            TrafficCar best = null;
            float bestGap = float.MaxValue;
            for (int i = 0; i < list.Count; i++)
            {
                var c = list[i];
                if (c == null || c == self || c.CurrentLane != lane) continue;
                // A leader is further along the edge → smaller DistanceToEnd → positive gap.
                float gap = selfDistToEnd - c.DistanceToEnd;
                if (gap > 0f && gap < bestGap) { bestGap = gap; best = c; }
            }
            return best;
        }

        void RebuildBuckets()
        {
            foreach (var list in _byEdge.Values) list.Clear();
            for (int i = 0; i < _cars.Count; i++)
            {
                var c = _cars[i];
                if (c == null) continue;
                int e = c.CurrentEdge;
                if (!_byEdge.TryGetValue(e, out var list)) { list = new List<TrafficCar>(); _byEdge[e] = list; }
                list.Add(c);
            }
        }

        static float SqXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
