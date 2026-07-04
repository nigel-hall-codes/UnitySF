using SFMap.Pipeline;
using UnityEngine;

namespace SFMap.OnFoot
{
    /// <summary>
    /// A minimal first-person on-foot player for walking the streamed SF city — the movement
    /// half of the graffiti mechanic (#390). A CharacterController walks/strafes over the streamed
    /// colliders (roads, sidewalks, and building facades from #393) under gravity, while mouse-look
    /// drives a first-person camera.
    ///
    /// Lifecycle is owned by <see cref="PlayerModeController"/> (#405): it is spawned dormant via
    /// <see cref="EnsureInstance"/> and switched on/off with <see cref="Activate"/> /
    /// <see cref="Deactivate"/> as the player steps out of / into a car. It no longer self-bootstraps,
    /// so it never contends with the car's follow camera — the coordinator hands the camera across.
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

        // After (re)placing us — a scene spawn or stepping out of a car — the chunk under us may not
        // be streamed yet. We probe straight down and drop in once a collider exists.
        const float SpawnProbeHeight = 500f;
        const float SpawnProbeMax = 2000f;

        CharacterController _cc;
        Transform _cam;
        float _pitch;                         // accumulated look pitch (degrees)
        float _vy;                            // vertical velocity carried across frames for gravity
        bool _spawned;                        // false until we've dropped onto real ground

        /// Create the (single) walker, dormant. The coordinator calls this once and then drives it
        /// with <see cref="Activate"/> / <see cref="Deactivate"/>. RequireComponent adds the
        /// CharacterController; Awake sets <see cref="Instance"/>.
        public static OnFootPlayer EnsureInstance()
        {
            if (Instance != null) return Instance;
            var go = new GameObject(nameof(OnFootPlayer));
            go.AddComponent<OnFootPlayer>();
            DontDestroyOnLoad(go);
            return Instance;
        }

        void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;

            _cc = GetComponent<CharacterController>();
            _cc.height = EyeHeight;
            _cc.center = new Vector3(0f, EyeHeight * 0.5f, 0f);
            _cc.radius = BodyRadius;
            _cc.enabled = false;              // enabled once placed on ground (see TrySpawn)
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// Switch the walker on at <paramref name="position"/>, taking ownership of the camera and
        /// the streaming target. Re-probes onto the ground under the new spot, so it works both for
        /// an initial spawn and for stepping out of a car onto a road.
        public void Activate(Vector3 position, Camera cam, ChunkStreamer streamer)
        {
            gameObject.SetActive(true);

            _cc.enabled = false;             // disable while we teleport + re-probe (probe skips self)
            transform.position = position;
            transform.rotation = Quaternion.identity;

            if (cam != null)
            {
                _cam = cam.transform;
                _cam.SetParent(transform, false);
                _cam.localPosition = new Vector3(0f, EyeHeight, 0f);
                _cam.localRotation = Quaternion.identity;
                _pitch = 0f;
            }

            if (streamer != null) streamer.target = transform;

            _spawned = false;                // drop onto ground at the new position
            _vy = 0f;
            LockCursor(true);
        }

        /// Switch the walker off, releasing the camera (keeping its world pose) so the car's follow
        /// camera can drive it again. The coordinator re-enables that follow camera afterwards.
        public void Deactivate()
        {
            if (_cam != null) { _cam.SetParent(null, true); _cam = null; }
            _cc.enabled = false;
            LockCursor(false);
            gameObject.SetActive(false);
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
        // is kept disabled (Awake/Activate) until now, so this probe can't hit our own capsule and we
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
