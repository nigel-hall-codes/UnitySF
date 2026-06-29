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
        const float BrakeDistance = 10f; // start easing the throttle within this far of the stop line

        // Road-width band that maps to the [minSpeed, maxSpeed] cruise range. A narrow
        // residential street drives at minSpeed, an arterial at maxSpeed; widths between
        // interpolate. Width 0 (data baked before widths were serialised) reads as mid-band.
        const float NarrowWidth = 6f;  // single-lane-ish carriageway
        const float WideWidth = 14f;   // multi-lane arterial

        RoadNetwork _net;
        TrafficManager _manager;  // owner; queried for the car ahead (car-following)
        int _edge;          // current edge index
        int _seg;           // current segment within the edge (0 .. Points.Length-2)
        Vector2 _pos;       // current XZ position along the centerline
        float _speed;       // metres/second — current speed, accel/decel-limited toward a target
        float _minSpeed;    // cruise on the narrowest road, metres/second
        float _maxSpeed;    // cruise on the widest road, metres/second
        float _accel;       // how fast the car speeds up, metres/second²
        float _decel;       // how fast the car slows down, metres/second² (usually > accel)
        float _timeHeadway; // seconds of gap the car keeps to the leader (car-following)
        float _minFollowGap; // bumper standstill gap to the leader, metres
        float _speedFactor; // per-car cruise multiplier (mild variation so cars differ)
        float _distToEnd;   // cached XZ distance from _pos to this edge's end node (this frame)
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
        float _approachWindow; // start watching a controlled node this far ahead, metres
        float _stopSetback;    // stop this far back from the node centre (the stop line), metres
        int _crossNode;        // node we've requested/hold a crossing reservation for, or -1
        bool _holdsCrossing;   // true once granted occupancy of _crossNode (driving through)

        /// Set when the car finishes its route (dead-end with nowhere to go) or drives
        /// off the streamed map. The manager destroys and replaces it.
        public bool Done { get; private set; }

        /// Current edge index — the manager buckets cars by this for car-following lookups.
        public int CurrentEdge => _edge;

        /// Current lane index — a follower only yields to a leader sharing its lane.
        public int CurrentLane => _laneIndex;

        /// Cached XZ distance from this car to its edge's end node, refreshed each frame.
        /// Smaller means further along the edge, so the car ahead has the smaller value.
        public float DistanceToEnd => _distToEnd;

        public void Init(RoadNetwork net, TrafficManager manager, int edge, Vector3 startSurface,
                         int roadMask, float yawOffset, float rideHeight,
                         float multiLaneMinWidth, float laneWidth,
                         float laneChangePeriodMin, float laneChangePeriodMax, float laneChangeDuration,
                         float approachWindow, float stopSetback,
                         float minSpeed, float maxSpeed, float accel, float decel,
                         float timeHeadway, float minFollowGap, float speedVariation)
        {
            _net = net;
            _manager = manager;
            _edge = edge;
            _seg = 0;
            _pos = net.GetEdge(edge).Points[0];
            _mask = roadMask;
            _yawOffset = yawOffset;
            _rideHeight = rideHeight;
            _multiLaneMinWidth = multiLaneMinWidth;
            _laneWidth = laneWidth;
            _laneChangeDuration = laneChangeDuration;
            _laneChangePeriodMin = laneChangePeriodMin;
            _laneChangePeriodMax = laneChangePeriodMax;
            _approachWindow = approachWindow;
            _stopSetback = stopSetback;
            _crossNode = -1;
            _holdsCrossing = false;
            _laneChangeT = 1f; // start settled in the assigned lane
            _lostTime = 0f;
            Done = false;
            _firstConform = true;

            _minSpeed = minSpeed;
            _maxSpeed = maxSpeed;
            _accel = Mathf.Max(0.1f, accel);
            _decel = Mathf.Max(0.1f, decel);
            _timeHeadway = Mathf.Max(0.1f, timeHeadway); // guard div-by-zero in the gap controller
            _minFollowGap = Mathf.Max(0f, minFollowGap);
            _speedFactor = 1f + Random.Range(-speedVariation, speedVariation);

            // Pick a random starting lane based on how many fit in this road's half-width.
            var startEdge = net.GetEdge(edge);
            int numLanes = startEdge.Width >= multiLaneMinWidth
                ? Mathf.Max(1, Mathf.FloorToInt(startEdge.Width * 0.5f / laneWidth))
                : 1;
            _laneIndex = Random.Range(0, numLanes);
            _targetLaneIndex = _laneIndex;
            // Stagger first change check so cars don't all decide simultaneously.
            _laneChangeTimer = Random.Range(laneChangePeriodMin, laneChangePeriodMax);

            // Start already rolling at the starting road's cruise so cars don't crawl up from 0.
            _speed = CruiseSpeed(startEdge);

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
            // Distribute the n lanes evenly across the carriageway rather than packing
            // them at the fixed _laneWidth against the outer edge — otherwise any slack
            // (half not a clean multiple of _laneWidth) pools on the centerline side and
            // cars hug the kerb (#216). With an effective lane width of half/n, lane 0
            // centres a single-lane carriageway and multi-lane roads spread evenly.
            float lane = half / n;
            // Lane 0 = outermost right; offset = half - 0.5*lane.
            // Lane n-1 = innermost right; offset = half - (n-0.5)*lane.
            float fromOff = half - (Mathf.Clamp(_laneIndex, 0, n - 1) + 0.5f) * lane;
            float toOff   = half - (Mathf.Clamp(_targetLaneIndex, 0, n - 1) + 0.5f) * lane;
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

            // Speed pipeline, all composed as a single min():
            //   1. accel/decel-limit _speed toward the free-road cruise capped by car-following,
            //   2. let the intersection governor cap THIS frame's step at the stop line,
            //   3. reflect any intersection braking back into _speed so the resume is smooth.
            // The net per-frame speed is therefore min(cruise, car-following, intersection).
            _distToEnd = DistanceToEdgeEnd();
            UpdateSpeed();

            float dt = Time.deltaTime;
            float governed = IntersectionGovernedStep(_speed * dt);
            if (dt > 1e-5f && governed < _speed * dt) _speed = governed / dt; // braked at the line
            Advance(governed);
            if (Done) return; // reached a dead-end; manager will cull
            Conform();
        }

        // Free-road cruise on this edge: wider roads drive faster, scaled by this car's mild
        // per-car variation. Width 0 (unserialised) reads as mid-band so old data still moves.
        float CruiseSpeed(in RoadNetwork.Edge e)
        {
            float t = e.Width <= 0f
                ? 0.5f
                : Mathf.Clamp01((e.Width - NarrowWidth) / (WideWidth - NarrowWidth));
            return Mathf.Lerp(_minSpeed, _maxSpeed, t) * _speedFactor;
        }

        // Eases _speed toward the target this frame under bounded accel/decel. The target is the
        // free-road cruise, lowered to a car-following cap whenever there is a slower car ahead on
        // the same edge and lane — a constant-time-headway controller that holds a safe gap and
        // smoothly reaches 0 as the gap closes, so cars never drive through the leader.
        void UpdateSpeed()
        {
            float target = CruiseSpeed(_net.GetEdge(_edge));

            var leader = _manager != null ? _manager.FindLeader(this, _edge, _laneIndex, _distToEnd) : null;
            if (leader != null)
            {
                float gap = _distToEnd - leader.DistanceToEnd;             // centerline gap to the car ahead
                float follow = (gap - _minFollowGap) / _timeHeadway;       // _timeHeadway guarded > 0 in Init
                if (follow < target) target = follow;
            }
            if (target < 0f) target = 0f;                                  // never reverse

            float rate = target > _speed ? _accel : _decel;
            _speed = Mathf.MoveTowards(_speed, target, rate * Time.deltaTime);
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

        // Caps this frame's travel so the car obeys the controlled node ahead: it eases to a
        // stop at the stop line and only rolls through once it has been granted the crossing.
        // For an uncontrolled upcoming node it returns the desired step unchanged (a true
        // no-op, so cars never stutter at mid-block welds or chunk-crop joints).
        float IntersectionGovernedStep(float desired)
        {
            var e = _net.GetEdge(_edge);
            int node = e.ToNode;
            if (_net.ControlAt(node) == RoadNetwork.IntersectionControl.None) return desired;

            float remaining = _distToEnd; // metres from _pos to the node centre (cached this frame)
            if (remaining > _approachWindow) return desired; // not close enough to care yet

            // Already cleared to cross this node (we are its occupant) — drive straight through.
            if (_holdsCrossing && _crossNode == node) return desired;

            // Join the junction's FIFO queue (idempotent) and take our turn when granted.
            bool granted = _net.RequestCross(node, this);
            _crossNode = node;
            if (granted) { _holdsCrossing = true; return desired; }

            // Waiting our turn: ease to a halt at the stop line, set back from the node centre.
            float toStopLine = remaining - _stopSetback;
            if (toStopLine <= 0f) return 0f; // at/over the line — hold
            float throttle = Mathf.Clamp01(toStopLine / BrakeDistance); // linear slow-down
            return Mathf.Min(desired * throttle, toStopLine);            // never overshoot the line
        }

        // Remaining XZ distance from the current position to the end (ToNode) of this edge.
        float DistanceToEdgeEnd()
        {
            var pts = _net.GetEdge(_edge).Points;
            float d = Vector2.Distance(_pos, pts[_seg + 1]);
            for (int i = _seg + 1; i + 1 < pts.Length; i++)
                d += Vector2.Distance(pts[i], pts[i + 1]);
            return d;
        }

        /// Releases any junction reservation this car holds or is queued for. Called by the car
        /// itself once it clears a node and by <see cref="TrafficManager"/> before culling — the
        /// mandatory deadlock backstop, since a car destroyed mid-crossing would otherwise wedge
        /// the junction for every other car forever.
        public void ReleaseReservation()
        {
            if (_crossNode < 0 || _net == null) { _crossNode = -1; _holdsCrossing = false; return; }
            _net.ReleaseCross(_crossNode, this);
            _crossNode = -1;
            _holdsCrossing = false;
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

            // We've reached the end node of this edge. If we were crossing it under a
            // reservation, free the junction now so the next waiting car can take its turn.
            int crossed = e.ToNode;
            if (_crossNode == crossed) ReleaseReservation();

            int next = _net.NextEdge(crossed, _edge);
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
