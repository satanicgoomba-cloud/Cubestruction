using System;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Cubestruction.UI
{
    /// <summary>
    /// Frutiger Aero look, generated entirely at runtime (no image assets): glossy pill buttons,
    /// glass orbs, frosted panels, sky gradients and progress bars, plus small factory helpers
    /// for building uGUI hierarchies from code. Uses legacy UI.Text (no TMP Essentials import needed).
    /// </summary>
    public static class AeroTheme
    {
        static Sprite _pill, _orb, _panel, _sky, _barBg, _barFill, _circle;
        static Font _font;

        public static Font Font => _font ? _font : (_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        public static Sprite Pill => _pill ? _pill : (_pill = MakeRounded(64, 64, 30, Color.white, true, 0.35f));
        public static Sprite PanelSprite => _panel ? _panel : (_panel = MakeRounded(64, 64, 22, new Color(0.92f, 0.97f, 1f, 0.82f), false, 0.18f));
        public static Sprite BarBg => _barBg ? _barBg : (_barBg = MakeRounded(32, 32, 15, new Color(0.1f, 0.25f, 0.4f, 0.55f), false, 0f));
        public static Sprite BarFill => _barFill ? _barFill : (_barFill = MakeRounded(32, 32, 15, Color.white, true, 0.25f));
        public static Sprite Orb => _orb ? _orb : (_orb = MakeOrb(128));
        public static Sprite Circle => _circle ? _circle : (_circle = MakeOrb(64, flat: true));
        public static Sprite Sky => _sky ? _sky : (_sky = MakeSky());

        // ------------------------------------------------------------------ sprite generation

        /// <summary>Rounded rect with vertical gradient, glossy top-half highlight and a soft rim. 9-sliced.</summary>
        static Sprite MakeRounded(int w, int h, int radius, Color tint, bool gloss, float rimDark)
        {
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float d = RoundedSdf(x + 0.5f, y + 0.5f, w, h, radius);   // <0 inside
                float a = Mathf.Clamp01(0.5f - d);
                float v = y / (float)(h - 1);                              // 0 bottom .. 1 top
                Color c = tint * Mathf.Lerp(0.78f, 1.05f, v);
                // rim: darker near the edge
                float rim = Mathf.Clamp01(1f + d / 3f);
                c = Color.Lerp(c, c * (1f - rimDark), rim);
                if (gloss && v > 0.52f)
                {
                    // bright glassy highlight band on the top half, inset from the edge
                    float inset = Mathf.Clamp01((-d - 2f) / 4f);
                    float k = Mathf.SmoothStep(0, 1, (v - 0.52f) / 0.48f) * 0.55f * inset;
                    c = Color.Lerp(c, Color.white, k);
                }
                // subtle inner glow at the bottom
                if (gloss && v < 0.25f) c = Color.Lerp(c, Color.white, (0.25f - v) * 0.6f * Mathf.Clamp01(-d / 4f));
                c.a = a * tint.a;
                px[y * w + x] = c;
            }
            tex.SetPixels(px); tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect,
                new Vector4(radius, radius, radius, radius));
        }

        static float RoundedSdf(float x, float y, int w, int h, int r)
        {
            float cx = Mathf.Clamp(x, r, w - r), cy = Mathf.Clamp(y, r, h - r);
            return new Vector2(x - cx, y - cy).magnitude - r;
        }

        /// <summary>Aqua glass orb: radial gradient, darker rim, white specular cap and a bottom bounce light.</summary>
        static Sprite MakeOrb(int s, bool flat = false)
        {
            var tex = new Texture2D(s, s, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[s * s];
            float r = s * 0.5f - 1;
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                var p = new Vector2(x + 0.5f - s * 0.5f, y + 0.5f - s * 0.5f) / r;   // -1..1
                float d = p.magnitude;
                float a = Mathf.Clamp01((1f - d) * r);
                Color c;
                if (flat) c = Color.white;
                else
                {
                    c = Color.Lerp(new Color(0.75f, 0.97f, 1f), new Color(0.1f, 0.6f, 0.9f), d * d);
                    // specular cap (upper area)
                    var cap = (p - new Vector2(0, 0.42f)) / new Vector2(0.72f, 0.42f);
                    float capK = Mathf.Clamp01(1f - cap.magnitude);
                    c = Color.Lerp(c, Color.white, Mathf.SmoothStep(0, 1, capK) * 0.85f);
                    // bounce light at the bottom
                    float b = Mathf.Clamp01(1f - ((p - new Vector2(0, -0.7f)) / new Vector2(0.6f, 0.25f)).magnitude);
                    c = Color.Lerp(c, new Color(0.8f, 1f, 1f), b * 0.6f);
                }
                c.a = a * (flat ? 1f : 0.95f);
                px[y * s + x] = c;
            }
            tex.SetPixels(px); tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, s, s), new Vector2(0.5f, 0.5f), 100);
        }

        static Sprite MakeSky()
        {
            const int H = 128;
            var tex = new Texture2D(2, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < H; y++)
            {
                float v = y / (float)(H - 1);
                Color c = v < 0.35f ? Color.Lerp(new Color(0.75f, 0.95f, 0.75f), AeroPalette.Sky, v / 0.35f)
                                    : Color.Lerp(AeroPalette.Sky, AeroPalette.SkyDeep, (v - 0.35f) / 0.65f);
                tex.SetPixel(0, y, c); tex.SetPixel(1, y, c);
            }
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, 2, H), new Vector2(0.5f, 0.5f), 100);
        }

        // ------------------------------------------------------------------ factories

        public static RectTransform Rect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent ? parent.gameObject.layer : 5;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            return rt;
        }

        public static RectTransform Place(this RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2 pos, Vector2 size, Vector2? pivot = null)
        {
            rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
            rt.pivot = pivot ?? new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = pos; rt.sizeDelta = size;
            return rt;
        }

        public static RectTransform Fill(this RectTransform rt, float inset = 0)
        {
            rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(inset, inset); rt.offsetMax = new Vector2(-inset, -inset);
            return rt;
        }

        public static Image Image(Transform parent, string name, Sprite sprite, Color color)
        {
            var img = Rect(parent, name).gameObject.AddComponent<Image>();
            img.sprite = sprite; img.color = color;
            img.type = sprite != null && sprite.border != Vector4.zero ? UnityEngine.UI.Image.Type.Sliced : UnityEngine.UI.Image.Type.Simple;
            return img;
        }

        public static Image Panel(Transform parent, string name) => Image(parent, name, PanelSprite, Color.white);

        public static Text Label(Transform parent, string text, int size = 24, TextAnchor align = TextAnchor.MiddleCenter, Color? color = null, bool shadow = true)
        {
            var t = Rect(parent, "Label").gameObject.AddComponent<Text>();
            t.font = Font; t.text = text; t.fontSize = size; t.alignment = align;
            t.color = color ?? AeroPalette.Ink;
            t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Wrap; t.verticalOverflow = VerticalWrapMode.Overflow;
            if (shadow)
            {
                var sh = t.gameObject.AddComponent<Shadow>();
                sh.effectColor = new Color(1, 1, 1, 0.6f); sh.effectDistance = new Vector2(0, -1);
            }
            return t;
        }

        /// <summary>Glossy pill button. tint defaults to aqua.</summary>
        public static Button Button(Transform parent, string label, UnityAction onClick, Color? tint = null, int fontSize = 24)
        {
            var img = Image(parent, "Btn_" + label, Pill, tint ?? AeroPalette.Aqua);
            var b = img.gameObject.AddComponent<Button>();
            PressFeedback(b);
            if (onClick != null) b.onClick.AddListener(onClick);
            var t = Label(img.transform, label, fontSize, TextAnchor.MiddleCenter, Color.white, false);
            t.rectTransform.Fill(4);
            var ol = t.gameObject.AddComponent<Outline>();
            ol.effectColor = new Color(0.05f, 0.3f, 0.5f, 0.55f); ol.effectDistance = new Vector2(1, -1);
            return b;
        }

        /// <summary>Clear hover/press feedback: darker when pointed at, darker still while pressed.</summary>
        public static void PressFeedback(Selectable s)
        {
            var cb = s.colors;
            cb.normalColor = Color.white;
            cb.highlightedColor = new Color(0.72f, 0.76f, 0.82f);
            cb.pressedColor = new Color(0.45f, 0.5f, 0.58f);
            cb.selectedColor = Color.white;   // don't stay dark after a click
            cb.disabledColor = new Color(0.6f, 0.6f, 0.6f, 0.6f);
            cb.colorMultiplier = 1f;
            cb.fadeDuration = 0.06f;
            s.colors = cb;
        }

        public static void SetLabel(this Button b, string text)
        {
            var t = b.GetComponentInChildren<Text>(); if (t) t.text = text;
        }

        public static InputField InputField(Transform parent, string initial, string placeholder)
        {
            var img = Image(parent, "Input", Pill, new Color(1, 1, 1, 0.95f));
            var f = img.gameObject.AddComponent<InputField>();
            var txt = Label(img.transform, initial, 24, TextAnchor.MiddleCenter, AeroPalette.Ink, false);
            txt.rectTransform.Fill(10); txt.supportRichText = false;
            var ph = Label(img.transform, placeholder, 22, TextAnchor.MiddleCenter, new Color(0.3f, 0.45f, 0.6f, 0.6f), false);
            ph.rectTransform.Fill(10); ph.fontStyle = FontStyle.Italic;
            f.textComponent = txt; f.placeholder = ph; f.text = initial;
            return f;
        }

        /// <summary>A horizontal progress bar. Returns the fill Image (set fillAmount via SetBar).</summary>
        public static Image Bar(Transform parent, string name, Color fill)
        {
            var bg = Image(parent, name, BarBg, Color.white);
            var f = Image(bg.transform, "Fill", BarFill, fill);
            f.rectTransform.anchorMin = Vector2.zero; f.rectTransform.anchorMax = new Vector2(1, 1);
            f.rectTransform.offsetMin = new Vector2(3, 3); f.rectTransform.offsetMax = new Vector2(-3, -3);
            f.rectTransform.pivot = new Vector2(0, 0.5f);
            return f;
        }

        public static void SetBar(Image fill, float v)
        {
            v = Mathf.Clamp01(v);
            var rt = fill.rectTransform;
            rt.anchorMax = new Vector2(Mathf.Max(v, 0.001f), 1);
            fill.enabled = v > 0.002f;
        }

        public static HorizontalLayoutGroup HRow(Transform parent, string name, float spacing = 10)
        {
            var g = Rect(parent, name).gameObject.AddComponent<HorizontalLayoutGroup>();
            g.spacing = spacing; g.childAlignment = TextAnchor.MiddleCenter;
            g.childControlWidth = true; g.childControlHeight = true; g.childForceExpandWidth = true; g.childForceExpandHeight = true;
            return g;
        }

        public static VerticalLayoutGroup VCol(Transform parent, string name, float spacing = 10)
        {
            var g = Rect(parent, name).gameObject.AddComponent<VerticalLayoutGroup>();
            g.spacing = spacing; g.childAlignment = TextAnchor.UpperCenter;
            g.childControlWidth = true; g.childControlHeight = true; g.childForceExpandWidth = true; g.childForceExpandHeight = false;
            return g;
        }

        public static LayoutElement Size(this Component c, float prefH = -1, float prefW = -1, float flexW = -1)
        {
            var le = c.GetComponent<LayoutElement>();
            if (le == null) le = c.gameObject.AddComponent<LayoutElement>();   // no ?? on UnityEngine.Object (fake-null)
            if (prefH >= 0) { le.preferredHeight = prefH; le.minHeight = prefH; }
            if (prefW >= 0) { le.preferredWidth = prefW; le.minWidth = prefW; }
            if (flexW >= 0) le.flexibleWidth = flexW;
            return le;
        }

        /// <summary>Cycling option button ("Locomotion: Smooth"). Calls get/set with the option index.</summary>
        public static Button Cycle(Transform parent, string label, string[] options, Func<int> get, Action<int> set)
        {
            Button b = null;
            b = Button(parent, "", () =>
            {
                set((get() + 1) % options.Length);
                b.SetLabel($"{label}: {options[get()]}");
            }, AeroPalette.SkyDeep, 22);
            b.SetLabel($"{label}: {options[Mathf.Clamp(get(), 0, options.Length - 1)]}");
            return b;
        }

        public static string Money(int v) => v >= 100000 ? $"${v / 1000}k" : $"${v:N0}";
    }
}
