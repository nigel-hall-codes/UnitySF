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
        float _lostTime;    // seconds without finding road under us
        bool _ready;
        bool _firstConform;

        /// Set when the car finishes its route (dead-end with nowhere to go) or drives
        /// off the streamed map. The manager destroys and replaces it.
        public bool Done { get; private set; }

        public void Init(RoadNetwork net, int edge, Vector3 startSurface,
                         float speed, int roadMask, float yawOffset, float rideHeight)
        {
            _net = net;
            _edge = edge;
            _seg = 0;
            _pos = net.GetEdge(edge).Points[0];
            _speed = speed;
            _mask = roadMask;
            _yawOffset = yawOffset;
            _rideHeight = rideHeight;
            _lostTime = 0f;
            Done = false;
            _firstConform = true;
            _ready = true;
            transform.position = startSurface;
        }

        void Update()
        {
            if (!_ready || _net == null) return;

            Advance(_speed * Time.deltaTime);
            if (Done) return; // reached a dead-end; manager will cull
            Conform();
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

        // Snaps the car onto the road surface (Y + slope) and faces it along travel.
        void Conform()
        {
            var origin = new Vector3(_pos.x, RayTop, _pos.y);
            bool grounded = Physics.Raycast(origin, Vector3.down, out var hit, RayLen, _mask,
                                            QueryTriggerInteraction.Ignore);

            Vector3 up = grounded ? hit.normal : Vector3.up;
            float y = grounded ? hit.point.y + _rideHeight : transform.position.y;

            // Heading: direction toward the end of the current segment.
            var e = _net.GetEdge(_edge);
            Vector2 d = e.Points[_seg + 1] - _pos;
            if (d.sqrMagnitude < 1e-6f) d = e.Points[_seg + 1] - e.Points[_seg];
            Vector3 fwd = new Vector3(d.x, 0f, d.y);
            if (fwd.sqrMagnitude < 1e-6f) fwd = transform.forward;

            // Build an orientation whose forward follows travel and whose up matches the
            // road normal, then apply the model's yaw correction.
            Vector3 right = Vector3.Cross(up, fwd).normalized;
            fwd = Vector3.Cross(right, up).normalized;
            Quaternion look = Quaternion.LookRotation(fwd, up) * Quaternion.Euler(0f, _yawOffset, 0f);

            transform.position = new Vector3(_pos.x, y, _pos.y);
            transform.rotation = _firstConform
                ? look
                : Quaternion.Slerp(transform.rotation, look, 1f - Mathf.Exp(-10f * Time.deltaTime));
            _firstConform = false;

            if (grounded) _lostTime = 0f;
            else if ((_lostTime += Time.deltaTime) > LostGrace) Done = true;
        }
    }
}
