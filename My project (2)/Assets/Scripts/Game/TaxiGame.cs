using SFMap.Pipeline;
using UnityEngine;

namespace SFMap.Game
{
    /// <summary>
    /// A Crazy Taxi–style fare loop layered on top of the existing free-drive scene:
    /// a passenger hails the cab at a roadside <b>pickup</b>, and once aboard a
    /// <b>destination</b> lights up; reaching it pays a fare and buys back time. The run
    /// is a race against a shared countdown — money is the score, zero on the clock ends it.
    ///
    /// The whole thing self-bootstraps after the scene loads (mirroring <see cref="SFMap.UI.CompassHUD"/>
    /// and <see cref="SFMap.UI.StreetHUD"/>), so there is <b>no scene wiring</b>: drop the assembly in
    /// and a run begins as soon as a driveable car and a built <see cref="RoadNetwork"/> exist.
    /// Pickup/destination points are sampled live from the road graph via
    /// <see cref="RoadNetwork.RandomEdgeNear"/> — no baked POI data — and a tall world beacon
    /// marks the current objective. <see cref="TaxiHUD"/> is a pure view over the public state below.
    ///
    /// Deliberately v1: guidance is the beacon + HUD distance (no on-screen arrow), handling is
    /// the stock car controller (no arcade boost/drift), and fares are procedural road points
    /// (no "take me to Coit Tower"). Those are follow-ups (#386 out-of-scope).
    /// </summary>
    [DisallowMultipleComponent]
    public class TaxiGame : MonoBehaviour
    {
        public static TaxiGame Instance { get; private set; }

        // --- Tuning ---------------------------------------------------------------------------
        // Grouped here so the whole feel is one screenful. Distances are metres, time seconds.

        const float StartSeconds = 60f;   // opening clock; a good drop-off nearly refills it
        const float PickupRadius = 11f;   // how close (XZ) the cab must get to grab a fare
        const float DropoffRadius = 13f;  // a touch larger — you arrive faster than you leave

        // Rings the objective is sampled within, measured from the cab. Pickups are close so a
        // run never stalls hunting for one; destinations are farther so each fare is a real trip.
        const float PickupMinDist = 40f, PickupMaxDist = 170f;
        const float DropMinDist = 130f, DropMaxDist = 460f;
        const float ClassBias = 0.5f;     // bias points toward arterials, matching traffic (#249)

        // Fare = base + rate·(straight-line pickup→destination). Time bonus scales with the same
        // distance (assuming you can average ~TargetSpeed) plus a fixed buffer, clamped so neither
        // a hop nor a cross-town haul breaks the economy.
        const float FareBase = 8f, FarePerMetre = 0.12f;
        const float TargetSpeed = 20f;    // m/s the bonus assumes you can sustain
        const float TimeBonusBuffer = 7f;
        const float TimeBonusMin = 12f, TimeBonusMax = 45f;

        const float BeaconHeight = 120f;  // tall enough to peek over most SF blocks
        const float BeaconWidth = 3.2f;

        static readonly Color PickupColor = new Color(0.20f, 0.95f, 0.45f, 0.55f); // green = go get them
        static readonly Color DropColor = new Color(1.0f, 0.78f, 0.16f, 0.55f);    // amber = drop here

        public enum Phase { Warmup, SeekingPickup, Carrying, GameOver }

        // --- Public read-only state (consumed by TaxiHUD) -------------------------------------
        public Phase State { get; private set; } = Phase.Warmup;
        public float TimeLeft { get; private set; }
        public int Earnings { get; private set; }
        public int Fares { get; private set; }
        /// Pending fare value of the passenger currently aboard (0 when seeking).
        public int CurrentFare { get; private set; }
        /// XZ distance from the cab to the active objective, or -1 when there is none.
        public float ObjectiveDistance { get; private set; } = -1f;
        /// Cross-street address of the current objective ("20th St & Kansas St"), or null while
        /// it is still resolving / unnamed. This is the "dispatch" the HUD reads out (#388).
        public string ObjectiveAddress { get; private set; }
        /// Last payout, exposed so the HUD can flash a "+$/+s" toast. Cleared once shown.
        public int LastFarePaid { get; private set; }
        public float LastTimeAdded { get; private set; }
        public float LastPayoutTime { get; private set; } = -999f;

        Transform _player;
        bool _hasObjective;         // false while a point is still being sampled (chunk streaming)
        Vector3 _objective;         // world position of the current pickup/destination
        Vector3 _pickupPos;         // remembered so the fare can price the pickup→destination leg
        Transform _beacon;
        Material _beaconMat;
        int _roadMask;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindObjectOfType<TaxiGame>() != null) return;
            var go = new GameObject(nameof(TaxiGame));
            go.AddComponent<TaxiGame>();
            DontDestroyOnLoad(go);
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            int roadLayer = LayerMask.NameToLayer("Road");
            _roadMask = roadLayer >= 0 ? 1 << roadLayer : ~0; // fall back to all layers if unnamed
            BuildBeacon();

            // The HUD is a separate self-contained view; spawn it if it isn't already present.
            if (FindObjectOfType<TaxiHUD>() == null)
            {
                var hud = new GameObject(nameof(TaxiHUD));
                hud.AddComponent<TaxiHUD>();
                DontDestroyOnLoad(hud);
            }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        Transform ResolvePlayer()
        {
            if (_player) return _player;

            // Prefer the follow camera's target — the car itself (see StreetHUD for the same
            // resolution order and the PROMETEO CameraFollow fallback).
            var follow = FindObjectOfType<PrometeoFollowCamera>();
            if (follow && follow.target) return _player = follow.target;

            var cf = FindObjectOfType<CameraFollow>();
            if (cf != null && cf.carTransform != null) return _player = cf.carTransform;
            return null;
        }

        void Update()
        {
            if (State == Phase.GameOver)
            {
                if (Input.GetKeyDown(KeyCode.R)) Restart();
                return;
            }

            var player = ResolvePlayer();
            var net = RoadNetwork.Instance;
            if (!player || net == null) return; // still streaming in — hold in Warmup

            if (State == Phase.Warmup)
            {
                StartRun();
                return;
            }

            // Tick the shared clock; a fare buys it back but never pauses it.
            TimeLeft -= Time.deltaTime;
            if (TimeLeft <= 0f)
            {
                TimeLeft = 0f;
                EndRun();
                return;
            }

            // The objective point may still be resolving (its chunk hasn't streamed in). Keep
            // trying to acquire one each frame; only run the arrival check once we have it.
            if (!_hasObjective)
            {
                if (State == Phase.SeekingPickup) AcquirePickup();
                else if (State == Phase.Carrying) AcquireDestination();
                ObjectiveDistance = -1f;
                return;
            }

            AnimateBeacon();

            float distXZ = XZDistance(player.position, _objective);
            ObjectiveDistance = distXZ;

            if (State == Phase.SeekingPickup && distXZ <= PickupRadius)
                OnPickedUp();
            else if (State == Phase.Carrying && distXZ <= DropoffRadius)
                OnDroppedOff(player.position);
        }

        // --- Run lifecycle --------------------------------------------------------------------

        void StartRun()
        {
            TimeLeft = StartSeconds;
            Earnings = 0;
            Fares = 0;
            CurrentFare = 0;
            BeginSeekingPickup();
        }

        void Restart() => State = Phase.Warmup; // Update() re-enters StartRun next frame

        void EndRun()
        {
            State = Phase.GameOver;
            CurrentFare = 0;
            _hasObjective = false;
            ObjectiveDistance = -1f;
            ObjectiveAddress = null;
            if (_beacon) _beacon.gameObject.SetActive(false);
        }

        // Enter the seeking phase; the actual pickup point is acquired (with retry) from Update.
        void BeginSeekingPickup()
        {
            State = Phase.SeekingPickup;
            CurrentFare = 0;
            _hasObjective = false;
            ObjectiveAddress = null;
            if (_beacon) _beacon.gameObject.SetActive(false);
        }

        void AcquirePickup()
        {
            if (!TrySampleObjective(PickupMinDist, PickupMaxDist, out var p, out var addr))
                return; // retry next frame
            _objective = p;
            _hasObjective = true;
            ObjectiveAddress = addr;
            ShowBeacon(p, PickupColor);
        }

        void OnPickedUp()
        {
            _pickupPos = _objective;
            State = Phase.Carrying;
            _hasObjective = false;
            ObjectiveAddress = null;
            if (_beacon) _beacon.gameObject.SetActive(false);
        }

        void AcquireDestination()
        {
            if (!TrySampleObjective(DropMinDist, DropMaxDist, out var dest, out var addr))
                return; // retry next frame
            _objective = dest;
            _hasObjective = true;
            ObjectiveAddress = addr;
            CurrentFare = Mathf.RoundToInt(FareBase + FarePerMetre * XZDistance(_pickupPos, dest));
            ShowBeacon(dest, DropColor);
        }

        void OnDroppedOff(Vector3 playerPos)
        {
            float legDist = XZDistance(_pickupPos, playerPos);
            float bonus = Mathf.Clamp(legDist / TargetSpeed + TimeBonusBuffer, TimeBonusMin, TimeBonusMax);

            Earnings += CurrentFare;
            TimeLeft += bonus;
            Fares++;

            LastFarePaid = CurrentFare;
            LastTimeAdded = bonus;
            LastPayoutTime = Time.time;

            BeginSeekingPickup();
        }

        // --- Objective sampling ---------------------------------------------------------------

        /// Samples a dispatchable <b>intersection</b> in the [min,max] ring around the cab: a road
        /// edge is drawn (biased toward arterials), and a junction endpoint of it (degree ≥ 3, so
        /// it has genuine cross streets) becomes the objective. Ground height comes from raycasting
        /// the Road layer — the same trick <see cref="TrafficManager"/> uses, which also skips
        /// corners whose chunk hasn't streamed in yet. The corner's cross-street address is read
        /// from <see cref="RoadNameIndex"/>. Retries a handful of edges before giving up this frame.
        bool TrySampleObjective(float minDist, float maxDist, out Vector3 world, out string address)
        {
            world = default;
            address = null;
            var net = RoadNetwork.Instance;
            if (net == null || !_player) return false;

            for (int attempt = 0; attempt < 16; attempt++)
            {
                int edge = net.RandomEdgeNear(_player.position, minDist, maxDist, ClassBias);
                if (edge < 0) continue;

                // Prefer a real junction endpoint so the cross-street address is meaningful; the
                // From endpoint is the one guaranteed to sit inside the sampling ring.
                var e = net.GetEdge(edge);
                int node = net.Degree(e.FromNode) >= 3 ? e.FromNode
                         : net.Degree(e.ToNode) >= 3 ? e.ToNode : -1;
                if (node < 0) continue; // a bend or dead-end, not a dispatchable corner

                Vector2 xz = net.GetNode(node);
                var origin = new Vector3(xz.x, 1000f, xz.y);
                if (!Physics.Raycast(origin, Vector3.down, out var hit, 2000f, _roadMask,
                                     QueryTriggerInteraction.Ignore))
                    continue; // that corner's chunk hasn't streamed in — try elsewhere

                world = hit.point;
                var names = RoadNameIndex.Instance;
                address = names != null ? names.CrossStreetsNear(hit.point) : null;
                return true;
            }
            return false;
        }

        static float XZDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // --- World beacon ---------------------------------------------------------------------

        void BuildBeacon()
        {
            // A translucent unlit column. Sprites/Default is always-included, double-sided and
            // honours the material's alpha, so the beam reads as light and is occluded by
            // buildings in front of it (depth-tested) — no URP/lighting assumptions.
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "TaxiObjectiveBeacon";
            Destroy(go.GetComponent<Collider>()); // never block the car or raycasts
            go.transform.SetParent(transform, false);
            go.transform.localScale = new Vector3(BeaconWidth, BeaconHeight, BeaconWidth);

            var shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            _beaconMat = new Material(shader);
            go.GetComponent<MeshRenderer>().sharedMaterial = _beaconMat;

            _beacon = go.transform;
            _beacon.gameObject.SetActive(false);
        }

        void ShowBeacon(Vector3 groundPos, Color color)
        {
            if (!_beacon) return;
            _beacon.gameObject.SetActive(true);
            _beacon.position = groundPos + Vector3.up * (BeaconHeight * 0.5f);
            if (_beaconMat) _beaconMat.color = color;
        }

        void AnimateBeacon()
        {
            if (!_beacon || !_beacon.gameObject.activeSelf) return;
            // Slow spin + gentle breathing so the marker draws the eye without being noisy.
            _beacon.Rotate(0f, 60f * Time.deltaTime, 0f, Space.World);
            float pulse = 1f + 0.06f * Mathf.Sin(Time.time * 3f);
            _beacon.localScale = new Vector3(BeaconWidth * pulse, BeaconHeight, BeaconWidth * pulse);
        }
    }
}
