using SFMap.Pipeline;
using UnityEngine;
using UnityEngine.UI;

namespace SFMap.UI
{
    // Displays the nearest named street at the bottom-left of the screen.
    // Self-bootstraps at runtime; no scene setup required.
    [DisallowMultipleComponent]
    [AddComponentMenu("UI/Street HUD")]
    public class StreetHUD : MonoBehaviour
    {
        const float UpdateInterval = 0.5f;

        Text _label;
        float _nextUpdate;
        Transform _resolved;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindObjectOfType<StreetHUD>() != null) return;
            var go = new GameObject(nameof(StreetHUD));
            go.AddComponent<StreetHUD>();
        }

        void Awake() => Build();

        Transform ResolveTarget()
        {
            if (_resolved) return _resolved;
            var follow = FindObjectOfType<PrometeoFollowCamera>();
            if (follow && follow.target) return _resolved = follow.target;
            return _resolved = (Camera.main ? Camera.main.transform : null);
        }

        void Update()
        {
            if (_label == null) return;
            if (Time.time < _nextUpdate) return;
            _nextUpdate = Time.time + UpdateInterval;

            var idx = RoadNameIndex.Instance;
            if (idx == null) return;

            var target = ResolveTarget();
            if (!target) return;

            string street = idx.FindNearest(target.position);
            _label.text = street ?? "";
        }

        void Build()
        {
            var font = LoadFont();
            var white = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1),
                new Vector2(0.5f, 0.5f), 1f);

            var canvasGO = new GameObject("StreetHUDCanvas");
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            // Background pill — bottom-left, 24px from each edge.
            var bg = new GameObject("StreetBG", typeof(RectTransform));
            bg.transform.SetParent(canvasGO.transform, false);
            var bgRt = (RectTransform)bg.transform;
            bgRt.anchorMin = bgRt.anchorMax = bgRt.pivot = new Vector2(0f, 0f);
            bgRt.anchoredPosition = new Vector2(24f, 24f);
            bgRt.sizeDelta = new Vector2(340f, 48f);
            var bgImg = bg.AddComponent<Image>();
            bgImg.sprite = white;
            bgImg.color = new Color(0f, 0f, 0f, 0.45f);
            bgImg.raycastTarget = false;

            // Street name label, centred inside the background.
            var labelGO = new GameObject("StreetLabel", typeof(RectTransform));
            labelGO.transform.SetParent(bg.transform, false);
            var labelRt = (RectTransform)labelGO.transform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(10f, 0f);
            labelRt.offsetMax = new Vector2(-10f, 0f);
            _label = labelGO.AddComponent<Text>();
            _label.font = font;
            _label.fontSize = 22;
            _label.fontStyle = FontStyle.Bold;
            _label.alignment = TextAnchor.MiddleCenter;
            _label.color = Color.white;
            _label.horizontalOverflow = HorizontalWrapMode.Overflow;
            _label.verticalOverflow = VerticalWrapMode.Overflow;
            _label.raycastTarget = false;
        }

        static Font LoadFont()
        {
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (f == null) f = Font.CreateDynamicFontFromOSFont("Arial", 16);
            return f;
        }
    }
}
