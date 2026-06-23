using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SFMap.UI
{
    /// <summary>
    /// Top-centre HUD compass rendered as a horizontal dial/tape that scrolls to
    /// reflect the player's heading. The direction the player faces sits in the
    /// centre; cardinal/ordinal labels flank it and shrink toward the edges, with
    /// tick marks filling the gaps.
    ///
    /// The whole UI is built in code, so the component is self-contained: drop it
    /// on any GameObject, or let it auto-spawn at runtime via <see cref="Bootstrap"/>.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("UI/Compass HUD")]
    public class CompassHUD : MonoBehaviour
    {
        [Header("Heading source")]
        [Tooltip("Transform whose Y rotation drives the dial. If empty, the follow " +
                 "camera's target (then the main camera) is used.")]
        [SerializeField] Transform headingSource;

        [Header("Layout")]
        [SerializeField] float width = 560f;
        [SerializeField] float height = 56f;
        [SerializeField] float topMargin = 24f;
        [Tooltip("Horizontal pixels per degree of heading. Larger zooms the dial in.")]
        [SerializeField] float pixelsPerDegree = 4f;

        [Header("Marks")]
        [Tooltip("Degrees between labelled directions (45 = N, NE, E, ...).")]
        [SerializeField] float labelStep = 45f;
        [Tooltip("Degrees between tick marks.")]
        [SerializeField] float tickStep = 15f;

        static readonly string[] EightPoint =
            { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        class Mark
        {
            public float bearing;
            public RectTransform rt;
            public Image image;  // tick
            public Text text;    // label (null for ticks)
        }

        readonly List<Mark> _marks = new List<Mark>();
        Sprite _whiteSprite;
        Font _font;
        bool _built;
        Transform _resolved;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindObjectOfType<CompassHUD>() != null) return;
            var go = new GameObject(nameof(CompassHUD));
            go.AddComponent<CompassHUD>();
        }

        void Awake()
        {
            Build();
        }

        Transform ResolveHeadingSource()
        {
            if (headingSource) return headingSource;
            if (_resolved) return _resolved;
            var follow = FindObjectOfType<PrometeoFollowCamera>();
            if (follow && follow.target) return _resolved = follow.target;
            // CameraFollow lives in Assembly-CSharp (no asmdef), so reference it by name
            // to avoid a cross-assembly compile error from this asmdef assembly.
            var camFollowType = System.Type.GetType("CameraFollow, Assembly-CSharp");
            if (camFollowType != null)
            {
                var comp = FindObjectOfType(camFollowType) as Component;
                if (comp != null)
                {
                    var field = camFollowType.GetField("carTransform");
                    if (field?.GetValue(comp) is Transform t) return _resolved = t;
                }
            }
            var cam = Camera.main != null ? Camera.main : FindObjectOfType<Camera>();
            return _resolved = (cam ? cam.transform : null);
        }

        void Update()
        {
            if (!_built) return;

            var src = ResolveHeadingSource();
            if (!src) return;

            float heading = src.eulerAngles.y;          // 0 = N, 90 = E, 180 = S, 270 = W
            float halfWidth = width * 0.5f;
            float halfDeg = halfWidth / pixelsPerDegree; // degrees visible on each side
            float fadeDeg = halfDeg * 0.35f;             // start fading near the edges

            foreach (var m in _marks)
            {
                float delta = Mathf.DeltaAngle(heading, m.bearing); // [-180, 180]
                float x = delta * pixelsPerDegree;
                bool visible = Mathf.Abs(x) <= halfWidth + 8f;
                if (m.rt.gameObject.activeSelf != visible)
                    m.rt.gameObject.SetActive(visible);
                if (!visible) continue;

                var pos = m.rt.anchoredPosition;
                pos.x = x;
                m.rt.anchoredPosition = pos;

                // Edge fade: full opacity in the centre, fading out at the rim.
                float edge = Mathf.Abs(delta) - (halfDeg - fadeDeg);
                float alpha = edge <= 0f ? 1f : Mathf.Clamp01(1f - edge / fadeDeg);

                if (m.text != null)
                {
                    // Labels also shrink as they move off-centre.
                    float t = Mathf.Clamp01(Mathf.Abs(delta) / halfDeg);
                    m.rt.localScale = Vector3.one * Mathf.Lerp(1f, 0.55f, t);
                    var c = m.text.color; c.a = alpha; m.text.color = c;
                }
                else
                {
                    var c = m.image.color; c.a = m.bearing % labelStep == 0f ? alpha : alpha * 0.6f;
                    m.image.color = c;
                }
            }
        }

        void Build()
        {
            _whiteSprite = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1),
                new Vector2(0.5f, 0.5f), 1f);
            _font = LoadFont();

            // --- Overlay canvas ---------------------------------------------
            var canvasGO = new GameObject("CompassCanvas");
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            // No GraphicRaycaster: the HUD is non-interactive and must not eat clicks.

            // --- Masked viewport pinned top-centre --------------------------
            var viewport = NewRect("Viewport", canvasGO.transform);
            viewport.anchorMin = viewport.anchorMax = new Vector2(0.5f, 1f);
            viewport.pivot = new Vector2(0.5f, 1f);
            viewport.sizeDelta = new Vector2(width, height);
            viewport.anchoredPosition = new Vector2(0f, -topMargin);
            viewport.gameObject.AddComponent<RectMask2D>();

            var bg = NewImage("Background", viewport, new Color(0f, 0f, 0f, 0.35f));
            Stretch(bg.rectTransform);

            // --- Marks (children of the viewport, positioned each frame) ----
            BuildMarks(viewport);

            // --- Centre "you are here" marker, drawn over the viewport ------
            var marker = NewImage("CenterMarker", canvasGO.transform,
                new Color(1f, 0.85f, 0.2f, 0.95f));
            marker.rectTransform.anchorMin = marker.rectTransform.anchorMax = new Vector2(0.5f, 1f);
            marker.rectTransform.pivot = new Vector2(0.5f, 1f);
            marker.rectTransform.sizeDelta = new Vector2(3f, height + 8f);
            marker.rectTransform.anchoredPosition = new Vector2(0f, -topMargin + 4f);

            _built = true;
        }

        void BuildMarks(RectTransform parent)
        {
            float tickTopY = -height * 0.5f + 6f;   // ticks rise from near the bottom
            float labelY = height * 0.16f;          // labels sit in the upper area

            for (float b = 0f; b < 360f; b += tickStep)
            {
                bool major = b % labelStep == 0f;
                var rt = NewRect("Tick", parent);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0f);
                rt.sizeDelta = new Vector2(major ? 2.5f : 1.5f, major ? height * 0.4f : height * 0.24f);
                rt.anchoredPosition = new Vector2(0f, tickTopY);
                var img = rt.gameObject.AddComponent<Image>();
                img.sprite = _whiteSprite;
                img.color = new Color(1f, 1f, 1f, major ? 0.95f : 0.6f);
                img.raycastTarget = false;
                _marks.Add(new Mark { bearing = b, rt = rt, image = img });
            }

            for (float b = 0f; b < 360f; b += labelStep)
            {
                var rt = NewRect("Label", parent);
                rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.sizeDelta = new Vector2(64f, 28f);
                rt.anchoredPosition = new Vector2(0f, labelY);
                var text = rt.gameObject.AddComponent<Text>();
                text.font = _font;
                text.text = LabelFor(b);
                text.alignment = TextAnchor.MiddleCenter;
                text.fontSize = 22;
                text.fontStyle = FontStyle.Bold;
                text.horizontalOverflow = HorizontalWrapMode.Overflow;
                text.verticalOverflow = VerticalWrapMode.Overflow;
                text.color = Color.white;
                text.raycastTarget = false;
                _marks.Add(new Mark { bearing = b, rt = rt, text = text });
            }
        }

        string LabelFor(float bearing)
        {
            if (bearing % 45f == 0f)
                return EightPoint[Mathf.RoundToInt(bearing / 45f) % 8];
            return Mathf.RoundToInt(bearing).ToString();
        }

        static Font LoadFont()
        {
            // Unity 2022.2+/6 renamed the builtin font; fall back for older versions.
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (f == null) f = Font.CreateDynamicFontFromOSFont("Arial", 16);
            return f;
        }

        static RectTransform NewRect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        Image NewImage(string name, Transform parent, Color color)
        {
            var rt = NewRect(name, parent);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = _whiteSprite;
            img.color = color;
            img.raycastTarget = false;
            return img;
        }

        static void Stretch(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
