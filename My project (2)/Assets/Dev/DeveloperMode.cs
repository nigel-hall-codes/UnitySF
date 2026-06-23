using UnityEngine;
using SFMap.Pipeline;

namespace SFMap.Dev
{
    /// <summary>
    /// Runtime developer fly-mode for the car. Lets you lift the car out of physics
    /// and fly it anywhere over the city — or teleport it to exact world coordinates —
    /// without touching the editor. Handy for exercising <see cref="ChunkStreamer"/>
    /// (fly around and watch chunks load/unload) and for parking the car at a specific
    /// spot to test from there.
    ///
    /// It self-bootstraps after every scene load (like the compass HUD), so it's always
    /// available in a play session with no scene wiring. Press <see cref="toggleKey"/>
    /// (F1 by default) to toggle.
    ///
    /// While active it disables the <see cref="PrometeoCarController"/> (so normal driving
    /// input is ignored) and makes the car's Rigidbody kinematic so it can be moved freely.
    /// Both are restored on exit and the car settles back onto its wheels.
    ///
    /// The <see cref="ChunkStreamer"/> follows the car's transform, so flying it around is
    /// exactly the same as driving as far as streaming is concerned — chunks load and unload
    /// around wherever you fly.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("SFMap/Developer Mode")]
    public class DeveloperMode : MonoBehaviour
    {
        [Header("Toggle")]
        [Tooltip("Key that enters/exits developer fly mode.")]
        public KeyCode toggleKey = KeyCode.F1;

        [Header("Flight")]
        [Tooltip("Horizontal fly speed in metres/second (W/S). Mouse wheel adjusts it live.")]
        public float moveSpeed = 80f;
        [Tooltip("Vertical fly speed in metres/second (E up, Q down).")]
        public float verticalSpeed = 50f;
        [Tooltip("Yaw turn rate in degrees/second (A/D).")]
        public float turnSpeed = 90f;
        [Tooltip("Speed multiplier while Left Shift is held.")]
        public float boostMultiplier = 5f;

        [Header("Teleport")]
        [Tooltip("Layer name used when snapping a teleport down onto the road surface.")]
        public string roadLayer = "Road";
        [Tooltip("Clearance above the road a snapped teleport lands at.")]
        public float dropClearance = 1.5f;

        // ---- Runtime state ----
        bool _active;
        Transform _car;
        Rigidbody _rb;
        bool _rbWasKinematic;
        PrometeoCarController _controller;
        bool _controllerWasEnabled;
        ChunkStreamer _streamer;

        // Teleport panel fields (editable strings so they survive partial typing).
        string _x = "0", _y = "150", _z = "0";
        string _status = "";
        GUIStyle _panel, _label, _hint;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindObjectOfType<DeveloperMode>() != null) return;
            var go = new GameObject(nameof(DeveloperMode));
            go.AddComponent<DeveloperMode>();
            DontDestroyOnLoad(go);
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                if (_active) Exit();
                else Enter();
            }

            if (_active)
                Fly();
        }

        void Enter()
        {
            _controller = FindObjectOfType<PrometeoCarController>();
            if (_controller == null)
            {
                _status = "No car (PrometeoCarController) in the scene.";
                Debug.LogWarning("[DeveloperMode] No PrometeoCarController found — nothing to fly.", this);
                return;
            }

            _car = _controller.transform;
            _streamer = FindObjectOfType<ChunkStreamer>();

            // Stop the car driving itself while we fly it.
            _controllerWasEnabled = _controller.enabled;
            _controller.enabled = false;

            _rb = _car.GetComponent<Rigidbody>();
            if (_rb != null)
            {
                _rbWasKinematic = _rb.isKinematic;
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
                _rb.isKinematic = true;
            }

            // Level the car (keep heading) so it flies flat and the compass stays sane.
            var e = _car.eulerAngles;
            _car.rotation = Quaternion.Euler(0f, e.y, 0f);

            SyncTeleportFields();
            _status = "";
            _active = true;
        }

        void Exit()
        {
            _active = false;
            if (_controller != null)
                _controller.enabled = _controllerWasEnabled;
            if (_rb != null)
            {
                _rb.isKinematic = _rbWasKinematic;
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }
        }

        void Fly()
        {
            if (_car == null) // car was destroyed/reloaded under us
            {
                Exit();
                return;
            }

            float dt = Time.deltaTime;
            bool boost = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            // Mouse wheel trims the base speed so you can slow down for fine positioning.
            float scroll = Input.mouseScrollDelta.y;
            if (scroll != 0f)
                moveSpeed = Mathf.Clamp(moveSpeed * (1f + scroll * 0.1f), 5f, 2000f);

            // Yaw with A/D, around world up so the car stays level.
            float yaw = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
            if (yaw != 0f)
                _car.Rotate(0f, yaw * turnSpeed * dt, 0f, Space.World);

            // Horizontal move along the (flattened) heading.
            Vector3 fwd = _car.forward; fwd.y = 0f; fwd.Normalize();
            float drive = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
            float lift  = (Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f);

            float speed = moveSpeed * (boost ? boostMultiplier : 1f);
            Vector3 delta = fwd * (drive * speed)
                          + Vector3.up * (lift * verticalSpeed * (boost ? boostMultiplier : 1f));
            _car.position += delta * dt;

            // Deliberately *don't* re-seed the teleport fields here: doing it every
            // frame would overwrite whatever the user is typing into them. The HUD's
            // live "Pos" readout already shows the current position; the editable
            // fields are seeded on Enter() and after a teleport.
        }

        void SyncTeleportFields()
        {
            if (_car == null) return;
            var p = _car.position;
            _x = p.x.ToString("0.0");
            _y = p.y.ToString("0.0");
            _z = p.z.ToString("0.0");
        }

        /// Move the car to an explicit world position. When <paramref name="snapToRoad"/>
        /// is set, raycast down onto the road layer and land the car there with clearance.
        void TeleportTo(Vector3 target, bool snapToRoad)
        {
            if (_car == null) return;

            if (snapToRoad)
            {
                int layer = LayerMask.NameToLayer(roadLayer);
                if (layer < 0)
                {
                    _status = $"No '{roadLayer}' layer — placed without snapping.";
                }
                else
                {
                    var origin = new Vector3(target.x, target.y + 1000f, target.z);
                    if (Physics.Raycast(origin, Vector3.down, out var hit, 4000f,
                                        1 << layer, QueryTriggerInteraction.Ignore))
                    {
                        target = hit.point + Vector3.up * dropClearance;
                        _status = "Snapped to road.";
                    }
                    else
                    {
                        _status = "No road under that point — placed at the given height.";
                    }
                }
            }
            else
            {
                _status = "Teleported.";
            }

            _car.position = target;
            if (_rb != null)
            {
                _rb.linearVelocity = Vector3.zero;
                _rb.angularVelocity = Vector3.zero;
            }
            SyncTeleportFields();
        }

        void OnGUI()
        {
            EnsureStyles();

            if (!_active)
            {
                GUI.Label(new Rect(10, Screen.height - 26, 300, 20),
                    $"[{toggleKey}] Developer mode", _hint);
                return;
            }

            const float w = 290f;
            float h = 250f;
            GUILayout.BeginArea(new Rect(10, 10, w, h), _panel);
            GUILayout.Label("<b>DEVELOPER MODE</b>", _label);
            GUILayout.Label($"{toggleKey}: exit   WASD: fly/turn   E/Q: up/down   Shift: boost", _label);
            GUILayout.Label($"Wheel: speed ({moveSpeed:0} m/s)", _label);
            GUILayout.Space(6);

            if (_car != null)
            {
                var p = _car.position;
                GUILayout.Label($"Pos   X {p.x:0.0}   Y {p.y:0.0}   Z {p.z:0.0}", _label);
            }
            if (_streamer != null)
            {
                if (_streamer.IsReady && _car != null)
                {
                    var c = _streamer.ChunkAt(_car.position);
                    GUILayout.Label($"Chunk col {c.Col}, row {c.Row}", _label);
                }
                GUILayout.Label($"Loaded chunks: {_streamer.LoadedChunkCount}", _label);
            }
            else
            {
                GUILayout.Label("No ChunkStreamer in scene.", _label);
            }

            GUILayout.Space(6);
            GUILayout.Label("Teleport to X / Y / Z:", _label);
            GUILayout.BeginHorizontal();
            _x = GUILayout.TextField(_x, GUILayout.Width(86));
            _y = GUILayout.TextField(_y, GUILayout.Width(86));
            _z = GUILayout.TextField(_z, GUILayout.Width(86));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Teleport") && TryParseTarget(out var t1))
                TeleportTo(t1, snapToRoad: false);
            if (GUILayout.Button("Snap to road") && TryParseTarget(out var t2))
                TeleportTo(t2, snapToRoad: true);
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_status))
                GUILayout.Label(_status, _label);

            GUILayout.EndArea();
        }

        bool TryParseTarget(out Vector3 target)
        {
            target = default;
            if (float.TryParse(_x, out var x) && float.TryParse(_y, out var y) && float.TryParse(_z, out var z))
            {
                target = new Vector3(x, y, z);
                return true;
            }
            _status = "Couldn't parse X/Y/Z.";
            return false;
        }

        void EnsureStyles()
        {
            if (_panel != null) return;

            var bg = new Texture2D(1, 1);
            bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.7f));
            bg.Apply();

            _panel = new GUIStyle(GUI.skin.box) { padding = new RectOffset(10, 10, 10, 10) };
            _panel.normal.background = bg;

            _label = new GUIStyle(GUI.skin.label) { richText = true, fontSize = 12 };
            _label.normal.textColor = Color.white;

            _hint = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            _hint.normal.textColor = new Color(1f, 1f, 1f, 0.5f);
        }
    }
}
