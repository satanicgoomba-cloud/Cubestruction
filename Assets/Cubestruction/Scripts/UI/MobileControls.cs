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
        GameObject _swingBtn, _toolGroup, _fightGroup, _landBtn;

        void Update()
        {
            var me = PlayerAvatar.Local;
            // SUPER!: fighter buttons instead of the tool ones
            bool fighter = me != null && me.role.Value == PlayerRole.Fighter;
            if (_fightGroup.activeSelf != fighter) { _fightGroup.SetActive(fighter); _toolGroup.SetActive(!fighter); ClearFight(); }
            if (fighter)
            {
                var fc = Super.FighterController.Local;
                bool flying = fc != null && fc.Flying;
                if (_landBtn.activeSelf != flying) _landBtn.SetActive(flying);
                return;
            }
            // the Swing button only appears once you own the Zip Tether
            bool show = me != null && me.Tools != null && me.Tools.HasTether;
            if (_swingBtn && _swingBtn.activeSelf != show) { _swingBtn.SetActive(show); if (!show) MobileInput.SwingHeld = false; }
        }

        static void ClearFight()
        {
            Super.FightInput.Block = false; Super.FightInput.Land = false;
            for (int i = 0; i < 4; i++) Super.FightInput.Special[i] = false;
        }
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
            var tools = AeroTheme.Rect(root, "ToolButtons").Fill(); _toolGroup = tools.gameObject;
            HoldButton(tools, "Use", new Vector2(-150, 260), 150, v => MobileInput.UseHeld = v, AeroPalette.Danger);
            HoldButton(root, "Jump", new Vector2(-330, 150), 120, v => { if (v) MobileInput.Jump = true; MobileInput.JumpHeld = v; }, AeroPalette.Lime);
            _swingBtn = HoldButton(tools, "Swing", new Vector2(-500, 230), 120, v => MobileInput.SwingHeld = v, new Color(0.72f, 0.52f, 1f)).gameObject;
            HoldButton(tools, "Climb", new Vector2(-120, 90), 120, v => MobileInput.ClimbHeld = v, AeroPalette.Aqua);

            // ---- SUPER! fighter buttons
            var fight = AeroTheme.Rect(root, "FightButtons").Fill(); _fightGroup = fight.gameObject;
            HoldButton(fight, "Punch", new Vector2(-150, 260), 150, v => { if (v) Super.FightInput.Light = true; }, AeroPalette.Danger);
            HoldButton(fight, "Kick", new Vector2(-120, 90), 120, v => { if (v) Super.FightInput.Heavy = true; }, new Color(1f, 0.55f, 0.2f));
            HoldButton(fight, "Block", new Vector2(-500, 200), 120, v => Super.FightInput.Block = v, AeroPalette.SkyDeep);
            var gold = AeroPalette.Gold;
            HoldButton(fight, "A", new Vector2(-120, 440), 100, v => Super.FightInput.Special[0] = v, new Color(0.45f, 0.8f, 1f));
            HoldButton(fight, "B", new Vector2(-240, 470), 100, v => Super.FightInput.Special[1] = v, new Color(0.55f, 0.6f, 1f));
            HoldButton(fight, "C", new Vector2(-360, 500), 100, v => Super.FightInput.Special[2] = v, new Color(0.75f, 0.5f, 1f));
            HoldButton(fight, "ULT", new Vector2(-480, 530), 110, v => Super.FightInput.Special[3] = v, gold);
            HoldButton(fight, "POWER", new Vector2(-120, 600), 120, v => { if (v) Super.FightInput.PowerUp = true; }, new Color(1f, 0.85f, 0.35f));
            _landBtn = HoldButton(fight, "Down", new Vector2(-500, 70), 100, v => Super.FightInput.Land = v, AeroPalette.Aqua).gameObject;
            _landBtn.SetActive(false);
            // pause menu (save, lobby, main menu): top centre for fighters, beside the money pill for miners
            var pf = AeroTheme.Button(fight, "Menu", () => GameMenus.Instance?.TogglePause(), AeroPalette.SkyDeep, 24);
            ((RectTransform)pf.transform).Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, -24), new Vector2(150, 64), new Vector2(0.5f, 1));
            var pt = AeroTheme.Button(tools, "Menu", () => GameMenus.Instance?.TogglePause(), AeroPalette.SkyDeep, 24);
            ((RectTransform)pt.transform).Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(345, -30), new Vector2(130, 72), new Vector2(0, 1));
            _fightGroup.SetActive(false);
            _sprintImg = HoldButton(root, "Sprint", new Vector2(-330, 320), 100, v =>
            {
                if (!v) return;
                _sprint = !_sprint; MobileInput.Sprint = _sprint;
                _sprintImg.color = _sprint ? AeroPalette.Gold : AeroPalette.SkyDeep;
            }, AeroPalette.SkyDeep);
            var shop = AeroTheme.Button(tools, "Shop", () => GameMenus.Instance?.ToggleShop(), AeroPalette.Gold, 26);
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
            ClearFight();
        }
    }
}
