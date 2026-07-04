using UnityEngine;
using UnityEngine.UI;

namespace SFMap.Graffiti
{
    /// <summary>
    /// Minimal screen-space HUD for the graffiti mechanic (#390 / #397): a centre reticle to aim
    /// the spray and a small "can" swatch showing the active colour. A pure view — it reads only
    /// <see cref="SessionPaintStore.SprayColor"/> and adds no state of its own.
    ///
    /// Built entirely in code (the TaxiHUD canvas/font idioms) and self-bootstraps after the scene
    /// loads, so there is no scene wiring — it ships alongside the graffiti assembly.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("UI/Spray HUD")]
    public class SprayHUD : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (FindObjectOfType<SprayHUD>() != null) return;
            var go = new GameObject(nameof(SprayHUD));
            go.AddComponent<SprayHUD>();
            DontDestroyOnLoad(go);
        }

        void Awake() => Build();

        void Build()
        {
            var white = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1),
                new Vector2(0.5f, 0.5f), 1f);

            var canvasGO = new GameObject("SprayHUDCanvas");
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 90;                    // under the taxi HUD (100) if both are up
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            // No GraphicRaycaster: non-interactive, must not eat spray clicks.

            var col = SessionPaintStore.SprayColor;

            // --- Centre reticle: a small dot on a dark rim, so it reads on any background. ---
            var rim = MakeImage("ReticleRim", canvasGO.transform, white,
                new Color(0f, 0f, 0f, 0.55f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(12f, 12f));
            MakeImage("ReticleDot", rim.transform, white,
                new Color(col.r, col.g, col.b, 1f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(6f, 6f));

            // --- "Can" swatch, bottom-centre: a colour chip on a dark plate + a SPRAY label. ---
            var plate = MakeImage("CanPlate", canvasGO.transform, white,
                new Color(0f, 0f, 0f, 0.5f), new Vector2(0.5f, 0f), new Vector2(0f, 28f), new Vector2(96f, 64f));
            MakeImage("CanSwatch", plate.transform, white,
                new Color(col.r, col.g, col.b, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(72f, 30f));

            var label = MakeLabel("CanLabel", plate.transform, 18, FontStyle.Bold, TextAnchor.LowerCenter);
            label.text = "SPRAY";
            label.color = new Color(1f, 1f, 1f, 0.85f);
            var lRt = (RectTransform)label.transform;
            lRt.anchorMin = Vector2.zero; lRt.anchorMax = new Vector2(1f, 0.4f);
            lRt.offsetMin = lRt.offsetMax = Vector2.zero;
        }

        // Anchored, pivot-centred image helper. `anchor` is the shared anchor+pivot, `pos` the
        // anchored offset from it, `size` the rect size.
        static Image MakeImage(string name, Transform parent, Sprite sprite, Color color,
            Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var img = go.AddComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            var rt = (RectTransform)go.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return img;
        }

        static Text MakeLabel(string name, Transform parent, int size, FontStyle style, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = LoadFont();
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = anchor;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            return t;
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
