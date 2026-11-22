using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Cubestruction.Siege
{
    /// <summary>
    /// Self-contained Frutiger Aero uGUI helpers for the Commander HUD: procedurally generated
    /// glossy rounded-rect sprites (white base so Image.color tints them), labels and buttons.
    /// No asset dependencies.
    /// </summary>
    public static class SiegeUiKit
    {
        static Sprite _gloss, _flat;
        static Font _font;

        public static Font Font => _font ? _font : (_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

        /// <summary>Rounded rect with a vertical gradient, a bright glossy upper half and a soft rim.</summary>
        public static Sprite Gloss => _gloss ? _gloss : (_gloss = MakeRounded(64, 18, true));
        public static Sprite Flat => _flat ? _flat : (_flat = MakeRounded(64, 18, false));

        static Sprite MakeRounded(int size, int radius, bool gloss)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                // signed distance to the rounded rect (inside negative)
                float px = Mathf.Abs(x + 0.5f - size / 2f) - (size / 2f - radius);
                float py = Mathf.Abs(y + 0.5f - size / 2f) - (size / 2f - radius);
                float outside = new Vector2(Mathf.Max(px, 0), Mathf.Max(py, 0)).magnitude + Mathf.Min(Mathf.Max(px, py), 0) - radius;
                float a = Mathf.Clamp01(0.5f - outside);
                float v = y / (float)(size - 1);   // 0 bottom .. 1 top
                Color c;
                if (gloss)
                {
                    // darker bottom, lighter top, then a strong white sheen on the top half
                    float shade = Mathf.Lerp(0.78f, 1f, v);
                    float sheen = v > 0.52f ? Mathf.Lerp(0.25f, 0.6f, (v - 0.52f) / 0.48f) : 0f;
                    float rim = Mathf.Clamp01(1f - Mathf.Abs(outside + 2f) / 2f) * 0.35f;
                    float l = Mathf.Clamp01(shade + sheen + rim);
                    c = new Color(l, l, l, a);
                }
                else c = new Color(1, 1, 1, a);
                tex.SetPixel(x, y, c);
            }
            tex.Apply();
            float b = radius + 2;
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        }

        public static Canvas CreateCanvas(string name, int sortOrder = 10)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;
            var scaler = go.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            go.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();
            return canvas;
        }

        public static void EnsureEventSystem()
        {
            if (EventSystem.current != null || Object.FindAnyObjectByType<EventSystem>() != null) return;
            var es = new GameObject("EventSystem", typeof(EventSystem));
            es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }

        public static RectTransform MakeRect(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.offsetMin = offsetMin; rt.offsetMax = offsetMax;
            return rt;
        }

        public static Image Panel(Transform parent, string name, Color color, bool gloss = true)
        {
            var rt = MakeRect(parent, name, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = gloss ? Gloss : Flat;
            img.type = Image.Type.Sliced;
            img.color = color;
            // soft drop shadow for the bubbly look
            var sh = rt.gameObject.AddComponent<Shadow>();
            sh.effectColor = new Color(0.05f, 0.2f, 0.4f, 0.35f);
            sh.effectDistance = new Vector2(0, -3);
            return img;
        }

        public static Text Label(Transform parent, string name, string text, int size, Color color, TextAnchor anchor = TextAnchor.MiddleCenter)
        {
            var rt = MakeRect(parent, name, Vector2.zero, Vector2.one, new Vector2(8, 2), new Vector2(-8, -2));
            var t = rt.gameObject.AddComponent<Text>();
            t.font = Font;
            t.text = text;
            t.fontSize = size;
            t.color = color;
            t.alignment = anchor;
            t.fontStyle = FontStyle.Bold;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            var o = rt.gameObject.AddComponent<Outline>();
            o.effectColor = new Color(1, 1, 1, 0.55f);
            o.effectDistance = new Vector2(1, -1);
            return t;
        }

        public static Button MakeButton(Transform parent, string name, string text, Color color, UnityEngine.Events.UnityAction onClick, int fontSize = 24)
        {
            var img = Panel(parent, name, color);
            var b = img.gameObject.AddComponent<Button>();
            var cb = b.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(1.1f, 1.1f, 1.15f);
            cb.pressedColor = new Color(0.8f, 0.9f, 1f);
            cb.disabledColor = new Color(0.65f, 0.65f, 0.7f, 0.75f);
            b.colors = cb;
            b.targetGraphic = img;
            if (onClick != null) b.onClick.AddListener(onClick);
            Label(img.transform, "Label", text, fontSize, AeroInk);
            return b;
        }

        /// <summary>A glossy track with a fill image (Filled/Horizontal). Returns the fill.</summary>
        public static Image Bar(Transform parent, string name, Color track, Color fill)
        {
            var bg = Panel(parent, name, track);
            var rt = MakeRect(bg.transform, "Fill", Vector2.zero, Vector2.one, new Vector2(4, 4), new Vector2(-4, -4));
            var f = rt.gameObject.AddComponent<Image>();
            f.sprite = Gloss;
            f.type = Image.Type.Filled;
            f.fillMethod = Image.FillMethod.Horizontal;
            f.fillOrigin = (int)Image.OriginHorizontal.Left;
            f.fillAmount = 0.5f;
            f.color = fill;
            f.raycastTarget = false;
            return f;
        }

        public static readonly Color AeroInk = new Color(0.07f, 0.22f, 0.4f);
        public static readonly Color AeroGlass = new Color(0.85f, 0.95f, 1f, 0.85f);
        public static readonly Color AeroSky = new Color(0.55f, 0.85f, 1f, 0.95f);
        public static readonly Color AeroAqua = new Color(0.45f, 0.95f, 0.95f, 0.95f);
        public static readonly Color AeroLime = new Color(0.65f, 0.95f, 0.4f, 0.95f);
        public static readonly Color AeroGold = new Color(1f, 0.85f, 0.35f, 0.95f);
    }
}
