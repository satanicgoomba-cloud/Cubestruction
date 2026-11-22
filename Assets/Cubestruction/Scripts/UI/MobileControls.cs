using Cubestruction.Player;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Cubestruction.UI
{
    /// <summary>
    /// Touch controls for phones/tablets (also forced on with the "-mobileui" command-line arg).
    /// Left half: floating virtual joystick. Right half: drag to look. Aero bubble buttons for
    /// Jump, Use (hold), Climb (hold), Sprint (toggle) and Shop. Writes into Player.MobileInput.
    /// </summary>
    public class MobileControls : MonoBehaviour
    {
        RectTransform _stickBase, _stickKnob;
        bool _sprint;
        Image _sprintImg;

        public static MobileControls Create(Transform canvas)
        {
            var root = AeroTheme.Rect(canvas, "MobileControls").Fill();
            var mc = root.gameObject.AddComponent<MobileControls>();
            mc.Build(root);
            return mc;
        }

        void Build(RectTransform root)
        {
            // ---- left: joystick zone
            var left = AeroTheme.Image(root, "MoveZone", null, new Color(1, 1, 1, 0.001f));
            left.rectTransform.Place(new Vector2(0, 0), new Vector2(0.45f, 0.75f), Vector2.zero, Vector2.zero);
            left.rectTransform.offsetMin = Vector2.zero; left.rectTransform.offsetMax = Vector2.zero;
            _stickBase = AeroTheme.Image(left.transform, "StickBase", AeroTheme.Circle, new Color(1, 1, 1, 0.25f)).rectTransform;
            _stickBase.Place(Vector2.zero, Vector2.zero, new Vector2(200, 200), new Vector2(220, 220));
            _stickKnob = AeroTheme.Image(_stickBase, "Knob", AeroTheme.Orb, Color.white).rectTransform;
            _stickKnob.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(110, 110));
            var stick = left.gameObject.AddComponent<TouchZone>();
            stick.onDown = p => { _stickBase.position = p; };
            stick.onDrag = (p, _) =>
            {
                Vector2 d = (Vector2)p - (Vector2)_stickBase.position;
                float max = 90f * _stickBase.lossyScale.x;
                d = Vector2.ClampMagnitude(d, max);
                _stickKnob.position = (Vector2)_stickBase.position + d;
                MobileInput.Move = d / max;
            };
            stick.onUp = () => { _stickKnob.anchoredPosition = Vector2.zero; MobileInput.Move = Vector2.zero; };

            // ---- right: look zone
            var right = AeroTheme.Image(root, "LookZone", null, new Color(1, 1, 1, 0.001f));
            right.rectTransform.anchorMin = new Vector2(0.45f, 0); right.rectTransform.anchorMax = new Vector2(1, 0.8f);
            right.rectTransform.offsetMin = Vector2.zero; right.rectTransform.offsetMax = Vector2.zero;
            var look = right.gameObject.AddComponent<TouchZone>();
            look.onDrag = (_, delta) => MobileInput.LookDelta += delta;

            // ---- buttons (bottom-right cluster), above the look zone so they receive touches first
            HoldButton(root, "Use", new Vector2(-150, 260), 150, v => MobileInput.UseHeld = v, AeroPalette.Danger);
            HoldButton(root, "Jump", new Vector2(-330, 150), 120, v => { if (v) MobileInput.Jump = true; }, AeroPalette.Lime);
            HoldButton(root, "Climb", new Vector2(-120, 90), 120, v => MobileInput.ClimbHeld = v, AeroPalette.Aqua);
            _sprintImg = HoldButton(root, "Sprint", new Vector2(-330, 320), 100, v =>
            {
                if (!v) return;
                _sprint = !_sprint; MobileInput.Sprint = _sprint;
                _sprintImg.color = _sprint ? AeroPalette.Gold : AeroPalette.SkyDeep;
            }, AeroPalette.SkyDeep);
            var shop = AeroTheme.Button(root, "Shop", () => GameMenus.Instance?.ToggleShop(), AeroPalette.Gold, 26);
            ((RectTransform)shop.transform).Place(new Vector2(1, 1), new Vector2(1, 1), new Vector2(-110, -150), new Vector2(170, 70));
        }

        static Image HoldButton(Transform parent, string label, Vector2 pos, float size, System.Action<bool> onState, Color tint)
        {
            var img = AeroTheme.Image(parent, label, AeroTheme.Orb, tint);
            img.rectTransform.Place(new Vector2(1, 0), new Vector2(1, 0), pos, new Vector2(size, size));
            var t = AeroTheme.Label(img.transform, label, (int)(size * 0.2f), TextAnchor.MiddleCenter, Color.white, false);
            t.rectTransform.Fill();
            t.gameObject.AddComponent<Outline>().effectColor = new Color(0, 0.2f, 0.4f, 0.6f);
            var z = img.gameObject.AddComponent<TouchZone>();
            z.onDown = _ => onState(true);
            z.onUp = () => onState(false);
            return img;
        }

        void OnDisable()
        {
            MobileInput.Move = Vector2.zero; MobileInput.UseHeld = false; MobileInput.ClimbHeld = false;
        }
    }

    /// <summary>Tiny pointer handler used for joystick / look / hold buttons (works with touch and mouse).</summary>
    public class TouchZone : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public System.Action<Vector3> onDown;
        public System.Action<Vector3, Vector2> onDrag;
        public System.Action onUp;
        int _pointer = int.MinValue;

        public void OnPointerDown(PointerEventData e)
        {
            if (_pointer != int.MinValue) return;   // already tracking a finger
            _pointer = e.pointerId;
            onDown?.Invoke(e.position);
            onDrag?.Invoke(e.position, Vector2.zero);
        }

        public void OnDrag(PointerEventData e)
        {
            if (e.pointerId != _pointer) return;
            onDrag?.Invoke(e.position, e.delta);
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (e.pointerId != _pointer) return;
            _pointer = int.MinValue;
            onUp?.Invoke();
        }

        void OnDisable() { if (_pointer != int.MinValue) { _pointer = int.MinValue; onUp?.Invoke(); } }
    }
}
