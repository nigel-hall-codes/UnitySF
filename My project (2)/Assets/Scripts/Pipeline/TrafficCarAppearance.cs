using UnityEngine;
using UnityEngine.Rendering;

namespace SFMap.Pipeline
{
    /// <summary>
    /// Per-car visual variety for ambient traffic, layered onto the low-poly vehicle prefab
    /// at spawn without touching the (shared, palette-atlas) materials:
    ///
    ///  • <b>Body colour</b> — a realistic, weighted-random tint (heavy on white/black/grey/
    ///    silver, the occasional colour) applied to the body renderer's first submesh via a
    ///    <see cref="MaterialPropertyBlock"/>. No material is instantiated, so nothing leaks
    ///    and SRP batching/instancing is preserved; tyres and emissive trim keep their own
    ///    swatch (only submesh 0 — the paint — is tinted).
    ///
    ///  • <b>Brake &amp; indicator lights</b> — the prefabs ship no light objects, so a handful
    ///    of tiny emissive quads are created at runtime at the rear corners, sharing one red
    ///    and one amber <see cref="Shader.Find">Unlit/Color</see> material across every car.
    ///    They are toggled by <see cref="Renderer.enabled"/> — no per-frame allocation. The
    ///    brake pair lights while the car is braking; an indicator blinks the side it is about
    ///    to turn. <see cref="TrafficCar"/> drives both each frame via <see cref="SetState"/>.
    ///
    /// Everything here is runtime-created (the vehicle art is third-party and not authored
    /// with lights); the prefab assets are never modified.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class TrafficCarAppearance : MonoBehaviour
    {
        const float BlinkHz = 1.5f; // turn-signal flashes ≈ 1.5 Hz, like a real relay

        static readonly int ColorId = Shader.PropertyToID("_Color");

        // Realistic body-colour distribution (weights sum to 1). Tints multiply the prefab's
        // palette atlas, so white = "as authored". Heavily weighted to the neutral fleet
        // colours real streets are full of, per the #243 brainstorm.
        static readonly (Color color, float weight)[] BodyPalette =
        {
            (new Color(1.00f, 1.00f, 1.00f), 0.30f), // white
            (new Color(0.06f, 0.06f, 0.07f), 0.18f), // black
            (new Color(0.78f, 0.80f, 0.82f), 0.13f), // silver
            (new Color(0.45f, 0.46f, 0.48f), 0.12f), // grey
            (new Color(0.22f, 0.23f, 0.25f), 0.07f), // graphite
            (new Color(0.62f, 0.10f, 0.10f), 0.07f), // red
            (new Color(0.13f, 0.20f, 0.42f), 0.06f), // dark blue
            (new Color(0.20f, 0.38f, 0.62f), 0.03f), // mid blue
            (new Color(0.13f, 0.30f, 0.18f), 0.02f), // dark green
            (new Color(0.72f, 0.66f, 0.52f), 0.015f),// beige
            (new Color(0.35f, 0.08f, 0.12f), 0.01f), // maroon
            (new Color(0.80f, 0.62f, 0.10f), 0.005f),// gold
        };

        // Shared across every car — built once, never destroyed. Materials use the always-
        // present built-in Unlit/Color shader so the lights read as a solid glow regardless
        // of scene lighting.
        static Mesh _quad;
        static Material _brakeMat;
        static Material _indicatorMat;

        MaterialPropertyBlock _mpb;
        Renderer _brakeL, _brakeR, _indL, _indR;

        /// Applies a weighted-random body colour and creates the rear light quads. Called once
        /// at spawn by <see cref="TrafficManager"/>, before the car starts driving.
        public void Configure()
        {
            var body = FindBodyRenderer();
            if (body == null) return;

            _mpb ??= new MaterialPropertyBlock();
            body.GetPropertyBlock(_mpb, 0);
            _mpb.SetColor(ColorId, RandomBodyColor());
            body.SetPropertyBlock(_mpb, 0); // submesh 0 = the paint; leave trim/glass untouched

            CreateLights(body);
        }

        /// Drives the lights from the car's real motion state. <paramref name="braking"/> lights
        /// the brake pair; <paramref name="indicator"/> blinks the matching side. Cheap enough to
        /// call every frame: it only flips <see cref="Renderer.enabled"/> and allocates nothing.
        public void SetState(bool braking, TrafficCar.Indicator indicator)
        {
            if (_brakeL == null) return; // body renderer was missing → no lights created

            if (_brakeL.enabled != braking) { _brakeL.enabled = braking; _brakeR.enabled = braking; }

            bool blink = Mathf.Repeat(Time.time * BlinkHz, 1f) < 0.5f;
            bool left = indicator == TrafficCar.Indicator.Left && blink;
            bool right = indicator == TrafficCar.Indicator.Right && blink;
            if (_indL.enabled != left) _indL.enabled = left;
            if (_indR.enabled != right) _indR.enabled = right;
        }

        static Color RandomBodyColor()
        {
            float r = Random.value; // [0,1)
            float acc = 0f;
            for (int i = 0; i < BodyPalette.Length; i++)
            {
                acc += BodyPalette[i].weight;
                if (r < acc) return BodyPalette[i].color;
            }
            return BodyPalette[0].color; // rounding guard
        }

        // The body is the largest mesh on the vehicle; tyres and trim are smaller renderers.
        // Picking by bounds volume is robust across the different prefab hierarchies.
        Renderer FindBodyRenderer()
        {
            var rends = GetComponentsInChildren<MeshRenderer>();
            Renderer best = null;
            float bestVol = -1f;
            foreach (var r in rends)
            {
                Vector3 s = r.localBounds.size;
                float vol = s.x * s.y * s.z;
                if (vol > bestVol) { bestVol = vol; best = r; }
            }
            return best;
        }

        void CreateLights(Renderer body)
        {
            EnsureSharedAssets();

            // Body bounds expressed in this car's local space, so the quads land on the rear
            // face regardless of how the prefab nests its body mesh.
            Bounds b = RootLocalBounds(body, transform);
            float rearZ = b.center.z - b.extents.z; // -Z is the rear (front wheels sit at +Z)
            float sideX = b.extents.x * 0.7f;
            float y = b.min.y + b.size.y * 0.42f;
            float behind = rearZ - 0.02f; // nudge just clear of the bodywork to avoid z-fight
            float bw = b.extents.x * 0.32f, bh = b.size.y * 0.16f; // brake quad half-extents
            float iw = bw * 0.7f, ih = bh * 0.7f;                  // indicator quad half-extents

            _brakeR = MakeQuad("BrakeR", new Vector3(sideX, y, behind), bw, bh, _brakeMat);
            _brakeL = MakeQuad("BrakeL", new Vector3(-sideX, y, behind), bw, bh, _brakeMat);
            _indR = MakeQuad("IndicatorR", new Vector3(sideX * 1.04f, y - bh * 1.2f, behind), iw, ih, _indicatorMat);
            _indL = MakeQuad("IndicatorL", new Vector3(-sideX * 1.04f, y - bh * 1.2f, behind), iw, ih, _indicatorMat);

            _brakeR.enabled = _brakeL.enabled = _indR.enabled = _indL.enabled = false;
        }

        Renderer MakeQuad(string name, Vector3 localPos, float halfW, float halfH, Material mat)
        {
            var go = new GameObject(name);
            var t = go.transform;
            t.SetParent(transform, false);
            t.localPosition = localPos;
            t.localRotation = Quaternion.Euler(0f, 180f, 0f);   // face the quad rearward (-Z)
            t.localScale = new Vector3(halfW * 2f, halfH * 2f, 1f);

            go.AddComponent<MeshFilter>().sharedMesh = _quad;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = LightProbeUsage.Off;
            mr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            return mr;
        }

        // Axis-aligned bounds of <paramref name="r"/> expressed in <paramref name="root"/>'s
        // local space — independent of root rotation/scale, so light placement is correct even
        // before the car's first orientation update.
        static Bounds RootLocalBounds(Renderer r, Transform root)
        {
            Bounds lb = r.localBounds;
            Matrix4x4 m = root.worldToLocalMatrix * r.transform.localToWorldMatrix;
            Vector3 c = lb.center, e = lb.extents;
            var result = new Bounds(m.MultiplyPoint3x4(c), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                var corner = c + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                result.Encapsulate(m.MultiplyPoint3x4(corner));
            }
            return result;
        }

        static void EnsureSharedAssets()
        {
            if (_quad == null)
            {
                _quad = new Mesh { name = "TrafficLightQuad" };
                _quad.SetVertices(new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                    new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
                });
                _quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
                _quad.RecalculateNormals();
                _quad.RecalculateBounds();
            }

            var unlit = Shader.Find("Unlit/Color");
            if (_brakeMat == null)
                _brakeMat = new Material(unlit) { name = "TrafficBrakeLight", color = new Color(0.95f, 0.05f, 0.05f) };
            if (_indicatorMat == null)
                _indicatorMat = new Material(unlit) { name = "TrafficIndicator", color = new Color(1f, 0.55f, 0.05f) };
        }
    }
}
