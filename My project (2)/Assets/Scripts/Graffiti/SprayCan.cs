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
        const float Range = 25f;          // metres the spray reaches
        const float DabSize = 0.6f;       // world-space diameter of a nozzle dab
        const float DabSpacing = 0.12f;   // metres between dabs along a sweep — the continuity knob
        const int MaxDabsPerFrame = 24;   // cap a single fast sweep so it can't spam thousands of dabs

        int _mask;
        Camera _cam;
        bool _wasSpraying;                // were we laying paint last frame? drives stroke continuity
        Vector3 _lastHit;

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
            if (!Physics.Raycast(ray, out var hit, Range, _mask, QueryTriggerInteraction.Ignore))
            {
                _wasSpraying = false;   // aimed at open sky — break the stroke
                return;
            }

            if (_wasSpraying)
                EmitStroke(store, _lastHit, hit.point, hit.normal);
            else
                store.AddDab(hit.point, hit.normal, DabSize);

            _lastHit = hit.point;
            _wasSpraying = true;
        }

        // Lay dabs between the previous and current hit so a fast sweep reads as continuous paint.
        // The current surface normal is reused for the in-between points — fine over a short sweep
        // on one wall, which is all a single frame's movement covers.
        void EmitStroke(SessionPaintStore store, Vector3 from, Vector3 to, Vector3 normal)
        {
            float dist = Vector3.Distance(from, to);
            int steps = Mathf.Clamp(Mathf.CeilToInt(dist / DabSpacing), 1, MaxDabsPerFrame);
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
