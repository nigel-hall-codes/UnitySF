using UnityEngine;

namespace SFMap.Pipeline
{
    /// <summary>
    /// Drives a single car kinematically along the <see cref="RoadNetwork"/> graph: it
    /// walks its current edge's centerline at a fixed speed and, on reaching a node,
    /// picks a random connected edge (a random turn). Each frame it raycasts straight
    /// down onto the "Road" layer to sit on the baked road surface and tilt to its
    /// slope — the centerline JSON carries no height, so the road mesh is the source of
    /// truth for Y.
    ///
    /// On multi-lane roads each car is assigned a random lane and will occasionally
    /// drift into an adjacent one, lerping smoothly over <see cref="TrafficManager.laneChangeDuration"/>
    /// seconds so the move looks intentional rather than a jump.
    ///
    /// No Rigidbody/physics: cars pass through each other and the player. That keeps the
    /// system robust (no pile-ups) and cheap. <see cref="TrafficManager"/> owns spawning
    /// and culling; a car that runs off the streamed area — its downward ray finds no
    /// road for <see cref="LostGrace"/> seconds — sets <see cref="Done"/> so the manager
    /// recycles it.
    /// </summary>
    [DisallowMultipleComponent]
    public class TrafficCar : MonoBehaviour
    {
        const float LostGrace = 1.5f; // give up after this long with no road underneath
        const float RayTop = 1000f;   // raycast origin height above the world
        const float RayLen = 2000f;

        RoadNetwork _net;
        int _edge;          // current edge index
        int _seg;           // current segment within the edge (0 .. Points.Length-2)
        Vector2 _pos;       // current XZ position along the centerline
        float _speed;       // metres/second
        int _mask;          // Road layer mask
        float _yawOffset;   // prefab forward-axis correction, degrees
        float _rideHeight;  // metres above the surface
        float _multiLaneMinWidth; // only use lane offsets on roads at least this wide
        float _laneWidth;         // physical width of one lane, metres
        int _laneIndex;           // current lane (0 = outermost / rightmost)
        int _targetLaneIndex;     // lane we're transitioning toward
        float _laneChangeT;       // 0..1 lerp progress; 1 = settled in current lane
        float _laneChangeDuration;
        float _laneChangeTimer;
        float _laneChangePeriodMin;
        float _laneChangePeriodMax;
        float _lostTime;    // seconds without finding road under us
        bool _ready;
        bool _firstConform;

        /// Set when the car finishes its route (dead-end with nowhere to go) or drives
        /// off the streamed map. The manager destroys and replaces it.
        public bool Done { get; private set; }

        public void Init(RoadNetwork net, int edge, Vector3 startSurface, float speed,
                         int roadMask, float yawOffset, float rideHeight,
                         float multiLaneMinWidth, float laneWidth,
                         float laneChangePeriodMin, float laneChangePeriodMax, float laneChangeDuration)
        {
            _net = net;
            _edge = edge;
            _seg = 0;
            _pos = net.GetEdge(edge).Points[0];
            _speed = speed;
            _mask = roadMask;
            _yawOffset = yawOffset;
            _rideHeight = rideHeight;
            _multiLaneMinWidth = multiLaneMinWidth;
            _laneWidth = laneWidth;
            _laneChangeDuration = laneChangeDuration;
            _laneChangePeriodMin = laneChangePeriodMin;
            _laneChangePeriodMax = laneChangePeriodMax;
            _laneChangeT = 1f; // start settled in the assigned lane
            _lostTime = 0f;
            Done = false;
            _firstConform = true;

            // Pick a random starting lane based on how many fit in this road's half-width.
            var startEdge = net.GetEdge(edge);
            int numLanes = startEdge.Width >= multiLaneMinWidth
                ? Mathf.Max(1, Mathf.FloorToInt(startEdge.Width * 0.5f / laneWidth))
                : 1;
            _laneIndex = Random.Range(0, numLanes);
            _targetLaneIndex = _laneIndex;
            // Stagger first change check so cars don't all decide simultaneously.
            _laneChangeTimer = Random.Range(laneChangePeriodMin, laneChangePeriodMax);

            _ready = true;
            transform.position = startSurface;
        }

        // Metres to shift right of the centerline. On narrow (single-lane) roads this
        // is 0. On multi-lane roads the car's lane index determines its physical offset,
        // lerping smoothly to _targetLaneIndex while a lane change is in progress.
        // Width is 0 on data baked before widths were serialised → no offset (centerline).
        float LaneOffset(in RoadNetwork.Edge e)
        {
            if (e.Width < _multiLaneMinWidth) return 0f;
            float half = e.Width * 0.5f;
            int n = Mathf.Max(1, Mathf.FloorToInt(half / _laneWidth));
            // Lane 0 = outermost right; offset = half - 0.5*laneWidth.
            // Lane n-1 = innermost right; offset = half - (n-0.5)*laneWidth.
            float fromOff = half - (Mathf.Clamp(_laneIndex, 0, n - 1) + 0.5f) * _laneWidth;
            float toOff   = half - (Mathf.Clamp(_targetLaneIndex, 0, n - 1) + 0.5f) * _laneWidth;
            return Mathf.Lerp(fromOff, toOff, _laneChangeT);
        }

        void Update()
        {
            if (!_ready || _net == null) return;

            // Advance lane-change lerp; once settled, count down to the next check.
            if (_laneChangeT < 1f)
            {
                _laneChangeT = Mathf.Min(1f, _laneChangeT + Time.deltaTime / _laneChangeDuration);
                if (_laneChangeT >= 1f) _laneIndex = _targetLaneIndex;
            }
            else
            {
                _laneChangeTimer -= Time.deltaTime;
                if (_laneChangeTimer <= 0f)
                {
                    TryChangeLane();
                    _laneChangeTimer = Random.Range(_laneChangePeriodMin, _laneChangePeriodMax);
                }
            }

            Advance(_speed * Time.deltaTime);
            if (Done) return; // reached a dead-end; manager will cull
            Conform();
        }

        // Shifts into an adjacent lane on multi-lane roads. No-ops if the road is too
        // narrow for multiple lanes or if the car is already at the road edge.
        void TryChangeLane()
        {
            var e = _net.GetEdge(_edge);
            if (e.Width < _multiLaneMinWidth) return;
            int n = Mathf.Max(1, Mathf.FloorToInt(e.Width * 0.5f / _laneWidth));
            if (n < 2) return;

            int next = Mathf.Clamp(_laneIndex + (Random.value < 0.5f ? -1 : 1), 0, n - 1);
            if (next == _laneIndex) return; // already at road edge in that direction

            _targetLaneIndex = next;
            _laneChangeT = 0f;
        }

        // Walks `distance` metres along the centerline, hopping to the next segment —
        // and to a random onward edge at a node — as it crosses each one.
        void Advance(float distance)
        {
            var e = _net.GetEdge(_edge);
            var pts = e.Points;

            int guard = 0;
            while (distance > 1e-4f && guard++ < 512)
            {
                Vector2 next = pts[_seg + 1];
                float segLen = Vector2.Distance(_pos, next);

                if (segLen <= 1e-4f) // degenerate segment — step over it
                {
                    _pos = next;
                    if (!StepSegment(ref e, ref pts)) return;
                    continue;
                }

                if (distance < segLen)
                {
                    _pos = Vector2.MoveTowards(_pos, next, distance);
                    return;
                }

                distance -= segLen;
                _pos = next;
                if (!StepSegment(ref e, ref pts)) return;
            }
        }

        // Advances to the next segment, transitioning to a new edge at the end node.
        // Returns false (and sets Done) when there's no onward edge.
        bool StepSegment(ref RoadNetwork.Edge e, ref Vector2[] pts)
        {
            _seg++;
            if (_seg < pts.Length - 1) return true;

            int next = _net.NextEdge(e.ToNode, _edge);
            if (next < 0) { Done = true; return false; }

            _edge = next;
            _seg = 0;
            e = _net.GetEdge(_edge);
            pts = e.Points;
            _pos = pts[0];
            return true;
        }

        // Snaps the car onto the road surface (Y + slope) and faces it along travel,
        // shifted into its current (or transitioning) lane on multi-lane roads.
        void Conform()
        {
            // Heading: direction toward the end of the current segment.
            var e = _net.GetEdge(_edge);
            Vector2 d = e.Points[_seg + 1] - _pos;
            if (d.sqrMagnitude < 1e-6f) d = e.Points[_seg + 1] - e.Points[_seg];
            Vector3 fwd = new Vector3(d.x, 0f, d.y);
            if (fwd.sqrMagnitude < 1e-6f) fwd = transform.forward;
            fwd.Normalize();

            // Shift the drive point right of the centerline into the car's lane.
            // The road mesh is wide enough that the offset point still lands on it, so
            // the raycast below grounds the car at its actual (offset) position.
            Vector3 flatRight = Vector3.Cross(Vector3.up, fwd); // unit: fwd is normalised
            Vector2 drive = _pos + new Vector2(flatRight.x, flatRight.z) * LaneOffset(e);

            var origin = new Vector3(drive.x, RayTop, drive.y);
            bool grounded = Physics.Raycast(origin, Vector3.down, out var hit, RayLen, _mask,
                                            QueryTriggerInteraction.Ignore);

            Vector3 up = grounded ? hit.normal : Vector3.up;
            float y = grounded ? hit.point.y + _rideHeight : transform.position.y;

            // Build an orientation whose forward follows travel and whose up matches the
            // road normal, then apply the model's yaw correction.
            Vector3 right = Vector3.Cross(up, fwd).normalized;
            fwd = Vector3.Cross(right, up).normalized;
            Quaternion look = Quaternion.LookRotation(fwd, up) * Quaternion.Euler(0f, _yawOffset, 0f);

            transform.position = new Vector3(drive.x, y, drive.y);
            transform.rotation = _firstConform
                ? look
                : Quaternion.Slerp(transform.rotation, look, 1f - Mathf.Exp(-10f * Time.deltaTime));
            _firstConform = false;

            if (grounded) _lostTime = 0f;
            else if ((_lostTime += Time.deltaTime) > LostGrace) Done = true;
        }
    }
}
