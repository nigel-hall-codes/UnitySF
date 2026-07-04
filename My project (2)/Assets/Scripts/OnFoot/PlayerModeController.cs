using SFMap.Pipeline;
using UnityEngine;

namespace SFMap.OnFoot
{
    /// <summary>
    /// Coordinates the two player modes and the walk-up entry/exit between them (#405):
    /// <b>Driving</b> the Prometeo car and being <b>OnFoot</b> as the <see cref="OnFootPlayer"/>
    /// walker. Self-bootstraps after the scene loads, no scene wiring.
    ///
    /// Press <c>Y</c> (gamepad) or <c>F</c> (keyboard) while stopped in the car to step out beside
    /// it; press it again on foot near a car to get back in. The coordinator owns the shared camera
    /// and the
    /// <see cref="ChunkStreamer.target"/>, handing both to whichever mode is active — which is what
    /// stops the walker and the car's follow camera from fighting over the main camera.
    ///
    /// We only need to toggle the car controller's (<c>PrometeoCarController</c>)
    /// <see cref="Behaviour.enabled"/>, so it's held as a <see cref="Behaviour"/>; the car body is a
    /// plain <see cref="Rigidbody"/>, frozen (kinematic) while you're out so it doesn't roll away.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerModeController : MonoBehaviour
    {
        public enum Mode { Driving, OnFoot }

        const KeyCode ToggleKey = KeyCode.F;
        const float ExitMaxSpeed = 2f;     // m/s — must be near-stopped to step out
        const float DoorOffset = 2.2f;     // metres to the driver side when stepping out
        const float DoorLift = 0.5f;       // start the step-out slightly above the road, then probe down
        const float EntryRadius = 4f;      // how close to the car you must be to get in

        public static PlayerModeController Instance { get; private set; }
        public Mode Current { get; private set; } = Mode.Driving;

        ChunkStreamer _streamer;
        Camera _cam;
        Behaviour _followCam;              // chase cam driving Camera.main — PrometeoFollowCamera or
                                           // the PROMETEO package's CameraFollow
        Transform _car;
        Behaviour _carController;          // PrometeoCarController, toggled via Behaviour.enabled
        Rigidbody _carBody;
        OnFootPlayer _walker;
        bool _nearCar;                     // cached each frame for the "get in" prompt
        GUIStyle _promptStyle;             // lazily built inside OnGUI (GUI.skin is only valid there)

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindObjectOfType<PlayerModeController>() != null) return;
            var go = new GameObject(nameof(PlayerModeController));
            go.AddComponent<PlayerModeController>();
            DontDestroyOnLoad(go);
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        void Start()
        {
            _streamer = FindObjectOfType<ChunkStreamer>();
            ResolveCar();                  // resolves _followCam, _car, _cam, _carController, _carBody

            _walker = OnFootPlayer.EnsureInstance();

            if (_car == null)
            {
                // No drivable car in the scene — go straight on foot (the #394 standalone case).
                EnterOnFoot(_cam != null ? _cam.transform.position : Vector3.zero);
            }
            else
            {
                // Start in the car: walker dormant, the follow camera drives the view, world streams
                // around the car.
                _walker.Deactivate();
                if (_streamer != null) _streamer.target = _car;
                Current = Mode.Driving;
            }
        }

        void Update()
        {
            if (_car == null) return;

            // Keyboard F or gamepad Y (Xbox Y == JoystickButton3) toggles between driving and foot.
            bool toggle = Input.GetKeyDown(ToggleKey) || Input.GetKeyDown(KeyCode.JoystickButton3);

            if (Current == Mode.Driving)
            {
                if (toggle && CarNearlyStopped())
                    ExitToFoot();
            }
            else // OnFoot
            {
                _nearCar = _walker != null &&
                           Vector3.Distance(_walker.transform.position, _car.position) <= EntryRadius;
                if (_nearCar && toggle)
                    EnterCar();
            }
        }

        void ExitToFoot()
        {
            // Freeze and hand off the car so it sits parked while you're out.
            if (_carController != null) _carController.enabled = false;
            if (_carBody != null) _carBody.isKinematic = true;
            if (_followCam != null) _followCam.enabled = false;   // stop the chase cam driving the camera

            var door = _car.position + _car.right * DoorOffset + Vector3.up * DoorLift;
            EnterOnFoot(door);
        }

        void EnterOnFoot(Vector3 at)
        {
            _walker.Activate(at, _cam, _streamer);
            Current = Mode.OnFoot;
        }

        void EnterCar()
        {
            _walker.Deactivate();                               // releases the camera (keeps world pose)

            if (_followCam != null) _followCam.enabled = true;    // chase cam resumes driving the camera
            if (_carBody != null) _carBody.isKinematic = false;
            if (_carController != null) _carController.enabled = true;
            if (_streamer != null) _streamer.target = _car;

            Current = Mode.Driving;
            _nearCar = false;
        }

        bool CarNearlyStopped()
            => _carBody == null || _carBody.linearVelocity.magnitude <= ExitMaxSpeed;

        void ResolveCar()
        {
            // Find the chase camera + the car it follows. Prefer the typed PrometeoFollowCamera
            // (SFMap.Pipeline); fall back to the PROMETEO package's CameraFollow (referenced via
            // its Prometeo asmdef). Mirrors TaxiGame's resolution.
            var prometeo = FindObjectOfType<PrometeoFollowCamera>();
            if (prometeo != null && prometeo.target != null)
            {
                _followCam = prometeo;
                _car = prometeo.target;
            }
            else
            {
                var cf = FindObjectOfType<CameraFollow>();
                if (cf != null)
                {
                    _followCam = cf;
                    _car = cf.carTransform;
                }
            }

            // The chase-cam Behaviour lives on the camera GameObject, so take the camera from it.
            if (_followCam != null) _cam = _followCam.GetComponent<Camera>();
            if (_cam == null) _cam = Camera.main;

            if (_car == null) return;
            _carBody = _car.GetComponent<Rigidbody>();
            _carController = FindCarController(_car);
        }

        // We only need to enable/disable the car controller, so return it through the Behaviour
        // base (PrometeoCarController is now referenceable via the Prometeo asmdef).
        static Behaviour FindCarController(Transform car)
        {
            return car.GetComponent<PrometeoCarController>();
        }

        void OnGUI()
        {
            if (Current != Mode.OnFoot || !_nearCar) return;

            _promptStyle ??= new GUIStyle(GUI.skin.label)
            {
                fontSize = 22,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
            };

            const string prompt = "Press Y / F to get in";
            var rect = new Rect(0f, Screen.height * 0.62f, Screen.width, 40f);

            _promptStyle.normal.textColor = new Color(0f, 0f, 0f, 0.7f);   // drop shadow
            GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), prompt, _promptStyle);
            _promptStyle.normal.textColor = Color.white;
            GUI.Label(rect, prompt, _promptStyle);
        }
    }
}
