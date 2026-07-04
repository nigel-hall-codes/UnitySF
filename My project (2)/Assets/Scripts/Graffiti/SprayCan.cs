using UnityEngine;

namespace SFMap.Graffiti
{
    /// <summary>
    /// Hold-to-spray input for the graffiti spike (#396). While the trigger is held it raycasts
    /// from the first-person camera and drops soft round dabs at the hit point via
    /// <see cref="SessionPaintStore"/>. To make a sweep read as continuous paint rather than
    /// spaced stickers, it fills in dabs between the previous and current hit each frame.
    ///
    /// Self-managed alongside the store (no scene wiring); uses the legacy <see cref="Input"/>.
    /// </summary>
    [DisallowMultipleComponent]
    public class SprayCan : MonoBehaviour
    {
        // Feel knobs — serialized so the #398 by-feel pass can tune them live in the Hierarchy
        // during Play (this GameObject is runtime-created, so select it while playing to adjust).
        [Header("Reach")]
        [SerializeField, Tooltip("Metres the spray ray reaches.")]
        float _range = 25f;

        [Header("Nozzle")]
        [SerializeField, Range(0.1f, 2f), Tooltip("World-space diameter of one nozzle dab.")]
        float _dabSize = 0.6f;

        [SerializeField, Range(0.02f, 0.5f),
         Tooltip("Metres between dabs along a sweep — the continuity knob. Keep well below the dab " +
                 "size so consecutive dabs overlap into a stroke rather than reading as spaced dots.")]
        float _dabSpacing = 0.1f;

        [Header("Rate")]
        [SerializeField, Range(4f, 60f),
         Tooltip("Dabs per second laid while aiming at one spot. Caps overdraw so a held can builds " +
                 "paint at a steady, framerate-independent rate instead of one dab every frame.")]
        float _stationaryRate = 20f;

        [SerializeField, Range(4, 64),
         Tooltip("Max dabs emitted in a single frame's sweep, so one fast flick can't spawn thousands.")]
        int _maxDabsPerFrame = 24;

        int _mask;
        Camera _cam;
        bool _wasSpraying;                // were we laying paint last frame? drives stroke continuity
        Vector3 _lastHit;                 // where the last dab landed (not just last frame's hit)
        float _sprayClock;                // time accumulated toward the next stationary dab

        void Awake()
        {
            // Buildings sit on Default (#393), roads on Road, car bodies on Detachable Part.
            _mask = BuildMask("Default", "Road", "Detachable Part");
        }

        void Update()
        {
            var store = SessionPaintStore.Instance;
            if (store == null || !store.Ready) { _wasSpraying = false; return; }

            var cam = ResolveCamera();
            if (cam == null) { _wasSpraying = false; return; }

            // Only while actually playing (cursor captured) and holding the trigger. When the
            // cursor is free the same button re-captures it (OnFootPlayer), so we stay off then.
            bool spraying = Cursor.lockState == CursorLockMode.Locked && Input.GetMouseButton(0);
            if (!spraying) { _wasSpraying = false; return; }

            var ray = new Ray(cam.transform.position, cam.transform.forward);
            if (!Physics.Raycast(ray, out var hit, _range, _mask, QueryTriggerInteraction.Ignore))
            {
                _wasSpraying = false;   // aimed at open sky — break the stroke
                return;
            }

            if (!_wasSpraying)
            {
                // Stroke start: lay one dab and anchor the stroke here.
                store.AddDab(hit.point, hit.normal, _dabSize);
                _lastHit = hit.point;
                _sprayClock = 0f;
                _wasSpraying = true;
                return;
            }

            // Distance since the last dab decides spacing; measuring from the last *dab* (not last
            // frame) means even a slow drift accumulates until it earns an evenly-spaced dab.
            if (Vector3.Distance(_lastHit, hit.point) >= _dabSpacing)
            {
                EmitStroke(store, _lastHit, hit.point, hit.normal);
                _lastHit = hit.point;
                _sprayClock = 0f;
            }
            else
            {
                // Aiming at ~one spot: deposit at a fixed rate rather than one dab per frame, so
                // paint builds steadily and overdraw stays bounded regardless of framerate.
                _sprayClock += Time.deltaTime;
                float interval = 1f / Mathf.Max(_stationaryRate, 0.01f);
                if (_sprayClock >= interval)
                {
                    store.AddDab(hit.point, hit.normal, _dabSize);
                    _lastHit = hit.point;
                    _sprayClock -= interval;
                }
            }
        }

        // Lay dabs between the previous and current hit so a fast sweep reads as continuous paint.
        // The current surface normal is reused for the in-between points — fine over a short sweep
        // on one wall, which is all a single frame's movement covers.
        void EmitStroke(SessionPaintStore store, Vector3 from, Vector3 to, Vector3 normal)
        {
            float dist = Vector3.Distance(from, to);
            int steps = Mathf.Clamp(Mathf.CeilToInt(dist / _dabSpacing), 1, _maxDabsPerFrame);
            for (int i = 1; i <= steps; i++)
            {
                var p = Vector3.Lerp(from, to, (float)i / steps);
                store.AddDab(p, normal, DabSize);
            }
        }

        Camera ResolveCamera()
        {
            if (_cam != null) return _cam;
            return _cam = Camera.main;   // the FP camera OnFootPlayer parents under the walker
        }

        static int BuildMask(params string[] layers)
        {
            int mask = 0;
            foreach (var n in layers)
            {
                int l = LayerMask.NameToLayer(n);
                if (l >= 0) mask |= 1 << l;
            }
            return mask != 0 ? mask : ~0;   // fall back to everything if none of the layers exist
        }
    }
}
