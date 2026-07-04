using SFMap.Pipeline;
using UnityEngine;

namespace SFMap.OnFoot
{
    /// <summary>
    /// A minimal first-person on-foot player for walking the streamed SF city — the movement
    /// half of the graffiti mechanic (#390) before any spraying exists. A CharacterController
    /// walks/strafes over the streamed colliders (roads, sidewalks, and building facades from
    /// #393) under gravity, while mouse-look drives a first-person camera.
    ///
    /// Self-bootstraps after the scene loads (the <see cref="SFMap.Game.TaxiGame"/> /HUD idiom),
    /// so there is <b>no scene wiring</b>: the assembly spawns the walker and registers it as
    /// <see cref="ChunkStreamer.target"/> so the world streams around it. Spawn-on-foot only —
    /// getting out of a car is out of MVP scope (#390 / C2).
    ///
    /// Uses the legacy <see cref="UnityEngine.Input"/> (matching TaxiGame), not the new Input System.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CharacterController))]
    public class OnFootPlayer : MonoBehaviour
    {
        public static OnFootPlayer Instance { get; private set; }

        // --- Tuning (metres, seconds, m/s) ---------------------------------------------------
        const float WalkSpeed = 4.5f;         // brisk walk
        const float RunSpeed = 8f;            // while holding Left Shift
        const float Gravity = -20f;           // snappier than real g so drops feel game-y
        const float MouseSensitivity = 2.2f;
        const float PitchLimit = 85f;         // clamp look up/down (degrees)
        const float EyeHeight = 1.7f;
        const float BodyRadius = 0.3f;
        const float GroundStick = -2f;        // small downward bias so isGrounded stays true on slopes

        // Chunks stream in asynchronously, so at scene start there may be no ground beneath us.
        // We probe straight down from high above our XZ and drop in once a collider exists.
        const float SpawnProbeHeight = 500f;
        const float SpawnProbeMax = 2000f;

        CharacterController _cc;
        Transform _cam;
        float _pitch;                         // accumulated look pitch (degrees)
        float _vy;                            // vertical velocity carried across frames for gravity
        bool _spawned;                        // false until we've dropped onto real streamed ground

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindObjectOfType<OnFootPlayer>() != null) return;
            var go = new GameObject(nameof(OnFootPlayer));
            go.AddComponent<OnFootPlayer>();          // RequireComponent adds the CharacterController
            DontDestroyOnLoad(go);
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            _cc = GetComponent<CharacterController>();
            _cc.height = EyeHeight;
            _cc.center = new Vector3(0f, EyeHeight * 0.5f, 0f);
            _cc.radius = BodyRadius;

            // Seed our XZ from wherever the scene camera already is (the car's follow camera, if
            // any), so we spawn over the city rather than at a possibly-empty world origin. Capture
            // its world position *before* reparenting the camera under us below.
            var cam = Camera.main;
            var seed = cam != null ? cam.transform.position : Vector3.zero;
            transform.position = new Vector3(seed.x, seed.y, seed.z);

            // First-person camera: reuse Camera.main if present, otherwise create one. Parent it to
            // the body at eye height so mouse-look pitches the camera and yaw turns the body.
            if (cam == null)
            {
                var camGo = new GameObject("OnFootCamera") { tag = "MainCamera" };
                cam = camGo.AddComponent<Camera>();
            }
            _cam = cam.transform;
            _cam.SetParent(transform, false);
            _cam.localPosition = new Vector3(0f, EyeHeight, 0f);
            _cam.localRotation = Quaternion.identity;

            // Stream the world around the walker instead of whatever it was following before.
            var streamer = FindObjectOfType<ChunkStreamer>();
            if (streamer != null) streamer.target = transform;

            // Stay disabled until we've found ground: a CharacterController is itself a collider,
            // so an enabled one would be hit by our own downward spawn probe (giving a bogus "ground"
            // hit on our capsule) and would also fall under gravity before real ground streams in.
            _cc.enabled = false;

            LockCursor(true);
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void Update()
        {
            // Esc frees the mouse (so you can leave play mode); click re-locks it.
            if (Input.GetKeyDown(KeyCode.Escape)) LockCursor(false);
            else if (Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0)) LockCursor(true);

            if (!_spawned)
            {
                TrySpawn();
                return;
            }

            if (Cursor.lockState == CursorLockMode.Locked) Look();
            Move();
        }

        // Wait for the chunk under us to stream in, then drop the controller onto it. The controller
        // is kept disabled (see Awake) until now, so this probe can't hit our own capsule and we
        // don't fall before there's ground. Enabling it after placing avoids it fighting the move.
        void TrySpawn()
        {
            var from = transform.position + Vector3.up * SpawnProbeHeight;
            if (!Physics.Raycast(from, Vector3.down, out var hit, SpawnProbeMax + SpawnProbeHeight))
                return;

            transform.position = hit.point + Vector3.up * (EyeHeight * 0.5f + 0.1f);
            _cc.enabled = true;
            _vy = 0f;
            _spawned = true;
        }

        void Look()
        {
            float mx = Input.GetAxisRaw("Mouse X") * MouseSensitivity;
            float my = Input.GetAxisRaw("Mouse Y") * MouseSensitivity;
            transform.Rotate(0f, mx, 0f, Space.Self);                       // yaw the body
            _pitch = Mathf.Clamp(_pitch - my, -PitchLimit, PitchLimit);     // pitch the camera
            _cam.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
        }

        void Move()
        {
            float fwd = Input.GetAxisRaw("Vertical");     // W / S
            float str = Input.GetAxisRaw("Horizontal");   // A / D
            // Body only ever yaws, so forward/right are horizontal — no need to flatten them.
            var wish = Vector3.ClampMagnitude(transform.forward * fwd + transform.right * str, 1f);
            float speed = Input.GetKey(KeyCode.LeftShift) ? RunSpeed : WalkSpeed;

            if (_cc.isGrounded && _vy < 0f) _vy = GroundStick;
            _vy += Gravity * Time.deltaTime;

            _cc.Move((wish * speed + Vector3.up * _vy) * Time.deltaTime);
        }

        static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
