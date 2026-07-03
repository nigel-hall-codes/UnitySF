using UnityEngine;
using UnityEngine.UI;

namespace SFMap.Game
{
    /// <summary>
    /// Screen-space HUD for <see cref="TaxiGame"/> — a pure view that reads the game's public
    /// state each frame and never drives it. Pinned top-right so it clears the bottom-left
    /// <see cref="SFMap.UI.StreetHUD"/> and the top-centre <see cref="SFMap.UI.CompassHUD"/>.
    ///
    /// Built entirely in code (same canvas/font idioms as the other HUDs) so it needs no scene
    /// setup; <see cref="TaxiGame"/> spawns it, but it also self-bootstraps if dropped in alone.
    /// Shows the shared clock, running earnings, the current objective + distance, a brief
    /// "+$/+time" payout toast, and a centred game-over card.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("UI/Taxi HUD")]
    public class TaxiHUD : MonoBehaviour
    {
        const float ToastDuration = 1.6f;

        Text _timer, _money, _objective, _toast, _gameOver;
        Image _timerBg;
        Font _font;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            // TaxiGame normally spawns us; this covers the HUD being dropped in on its own.
            if (FindObjectOfType<TaxiHUD>() != null) return;
            if (FindObjectOfType<TaxiGame>() == null) return; // no game → no HUD
            var go = new GameObject(nameof(TaxiHUD));
            go.AddComponent<TaxiHUD>();
            DontDestroyOnLoad(go);
        }

        void Awake() => Build();

        void Update()
        {
            var game = TaxiGame.Instance;
            if (game == null) return;

            bool over = game.State == TaxiGame.Phase.GameOver;
            bool warmup = game.State == TaxiGame.Phase.Warmup;

            // Clock — turns red under 10s to signal the run is nearly up.
            _timer.text = FormatClock(game.TimeLeft);
            bool urgent = !over && game.TimeLeft <= 10f;
            _timer.color = urgent ? new Color(1f, 0.35f, 0.3f) : Color.white;

            _money.text = "$" + game.Earnings;

            // Objective line is the dispatch: phase, cross-street address, and live distance.
            if (over)
                _objective.text = "";
            else if (warmup)
                _objective.text = "Starting run…";
            else if (game.State == TaxiGame.Phase.SeekingPickup)
                _objective.text = ColorTag("● PICK UP", 0.32f, 0.95f, 0.5f)
                                  + AddressSuffix(game.ObjectiveAddress) + DistanceSuffix(game);
            else // Carrying
                _objective.text = ColorTag("▲ DROP OFF  $" + game.CurrentFare, 1f, 0.82f, 0.25f)
                                  + AddressSuffix(game.ObjectiveAddress) + DistanceSuffix(game);

            UpdateToast(game);
            UpdateGameOver(game, over);
        }

        // The cross-street dispatch, e.g. "  ·  20th St & Kansas St". Blank while it resolves.
        static string AddressSuffix(string address)
            => string.IsNullOrEmpty(address) ? "" : "   ·   " + address;

        static string DistanceSuffix(TaxiGame game)
            => game.ObjectiveDistance >= 0f ? $"   {Mathf.RoundToInt(game.ObjectiveDistance)} m" : "";

        void UpdateToast(TaxiGame game)
        {
            float age = Time.time - game.LastPayoutTime;
            if (age > ToastDuration || game.LastFarePaid <= 0)
            {
                if (_toast.color.a != 0f) SetAlpha(_toast, 0f);
                return;
            }
            _toast.text = $"+${game.LastFarePaid}   +{Mathf.RoundToInt(game.LastTimeAdded)}s";
            // Rise and fade over the toast's life.
            float t = age / ToastDuration;
            SetAlpha(_toast, 1f - t);
            var rt = (RectTransform)_toast.transform;
            rt.anchoredPosition = new Vector2(-24f, -150f - 30f * t);
        }

        void UpdateGameOver(TaxiGame game, bool over)
        {
            if (!over)
            {
                if (_gameOver.gameObject.activeSelf) _gameOver.gameObject.SetActive(false);
                return;
            }
            if (!_gameOver.gameObject.activeSelf) _gameOver.gameObject.SetActive(true);
            _gameOver.text = $"TIME'S UP\n\n${game.Earnings} · {game.Fares} fares\n\n<size=28>Press R to drive again</size>";
        }

        static string FormatClock(float seconds)
        {
            int s = Mathf.Max(0, Mathf.CeilToInt(seconds));
            return $"{s / 60:0}:{s % 60:00}";
        }

        static string ColorTag(string s, float r, float g, float b)
            => $"<color=#{ToHex(r)}{ToHex(g)}{ToHex(b)}>{s}</color>";

        static string ToHex(float c) => Mathf.RoundToInt(Mathf.Clamp01(c) * 255f).ToString("X2");

        static void SetAlpha(Graphic g, float a)
        {
            var c = g.color; c.a = a; g.color = c;
        }

        // --- UI construction ------------------------------------------------------------------

        void Build()
        {
            _font = LoadFont();
            var white = Sprite.Create(Texture2D.whiteTexture, new Rect(0, 0, 1, 1),
                new Vector2(0.5f, 0.5f), 1f);

            var canvasGO = new GameObject("TaxiHUDCanvas");
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            // No GraphicRaycaster: the HUD is non-interactive and must not eat clicks.

            // Top-right pill holding the clock (top) and earnings (below it).
            var bg = new GameObject("TaxiBG", typeof(RectTransform));
            bg.transform.SetParent(canvasGO.transform, false);
            _timerBg = bg.AddComponent<Image>();
            _timerBg.sprite = white;
            _timerBg.color = new Color(0f, 0f, 0f, 0.45f);
            _timerBg.raycastTarget = false;
            var bgRt = (RectTransform)bg.transform;
            bgRt.anchorMin = bgRt.anchorMax = bgRt.pivot = new Vector2(1f, 1f);
            bgRt.anchoredPosition = new Vector2(-24f, -24f);
            bgRt.sizeDelta = new Vector2(260f, 110f);

            _timer = MakeLabel("Timer", bg.transform, 52, FontStyle.Bold, TextAnchor.MiddleCenter);
            var tRt = (RectTransform)_timer.transform;
            tRt.anchorMin = new Vector2(0f, 0.42f); tRt.anchorMax = Vector2.one;
            tRt.offsetMin = tRt.offsetMax = Vector2.zero;

            _money = MakeLabel("Money", bg.transform, 30, FontStyle.Bold, TextAnchor.MiddleCenter);
            _money.color = new Color(0.55f, 1f, 0.6f);
            var mRt = (RectTransform)_money.transform;
            mRt.anchorMin = Vector2.zero; mRt.anchorMax = new Vector2(1f, 0.42f);
            mRt.offsetMin = mRt.offsetMax = Vector2.zero;

            // Objective line under the pill (top-right, rich-text coloured per phase).
            _objective = MakeLabel("Objective", canvasGO.transform, 26, FontStyle.Bold, TextAnchor.UpperRight);
            _objective.supportRichText = true;
            var oRt = (RectTransform)_objective.transform;
            oRt.anchorMin = oRt.anchorMax = oRt.pivot = new Vector2(1f, 1f);
            oRt.anchoredPosition = new Vector2(-24f, -140f);
            oRt.sizeDelta = new Vector2(760f, 34f); // wide enough for a full cross-street address

            // Payout toast, just below the objective; fades itself out.
            _toast = MakeLabel("Toast", canvasGO.transform, 30, FontStyle.Bold, TextAnchor.UpperRight);
            _toast.color = new Color(1f, 0.95f, 0.5f, 0f);
            var toRt = (RectTransform)_toast.transform;
            toRt.anchorMin = toRt.anchorMax = toRt.pivot = new Vector2(1f, 1f);
            toRt.anchoredPosition = new Vector2(-24f, -150f);
            toRt.sizeDelta = new Vector2(300f, 40f);

            // Centred game-over card (hidden until the clock hits zero).
            _gameOver = MakeLabel("GameOver", canvasGO.transform, 54, FontStyle.Bold, TextAnchor.MiddleCenter);
            _gameOver.supportRichText = true;
            var goRt = (RectTransform)_gameOver.transform;
            goRt.anchorMin = goRt.anchorMax = goRt.pivot = new Vector2(0.5f, 0.5f);
            goRt.sizeDelta = new Vector2(900f, 400f);
            _gameOver.gameObject.SetActive(false);
        }

        Text MakeLabel(string name, Transform parent, int size, FontStyle style, TextAnchor anchor)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = _font;
            t.fontSize = size;
            t.fontStyle = style;
            t.alignment = anchor;
            t.color = Color.white;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
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
