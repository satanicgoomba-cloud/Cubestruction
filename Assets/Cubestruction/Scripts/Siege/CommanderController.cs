using System.Collections.Generic;
using Cubestruction.Player;
using Cubestruction.Voxels;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Cubestruction.Siege
{
    /// <summary>
    /// The Commander's RTS view (PC / Mac / Mobile, owner only; enabled by PlayerAvatar.ApplyRole).
    /// Replaces the first-person rig with an orbit camera over the arena and an Aero HUD:
    ///   WASD / arrows / right-drag / one-finger drag   pan        Wheel / pinch       zoom
    ///   Q / E / two-finger twist                       rotate     1-6 / unit buttons  send unit
    ///   Click a portal or the portal buttons           choose which portal units march from
    /// </summary>
    public class CommanderController : MonoBehaviour
    {
        public float minDistance = 10f, maxDistance = 60f;

        PlayerAvatar _avatar;
        PlayerMotor _motor;
        FlatRig _flat;
        Camera _cam;
        Canvas _canvas;
        Vector3 _pivot;
        float _yaw = 0f, _pitch = 50f, _distance = 32f;
        int _portal;
        GameObject _portalMarker;
        float _dragTwist; float _pinchDist;

        // HUD refs
        Text _money, _era, _wave, _timer, _mana, _core, _status;
        Image _xpFill, _manaFill;
        readonly List<Button> _unitButtons = new List<Button>();
        readonly List<Text> _unitLabels = new List<Text>();
        readonly List<Image> _portalButtons = new List<Image>();
        RectTransform _topBar, _bottomBar, _portalBar;

        void OnEnable()
        {
            _avatar = GetComponent<PlayerAvatar>();
            _motor = GetComponent<PlayerMotor>();
            _flat = GetComponent<FlatRig>();
            if (_flat && _flat.enabled) _flat.enabled = false;   // destroys its first-person camera
            if (_motor) { _motor.Frozen = true; _motor.moveIntent = Vector3.zero; }
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            var camGo = new GameObject("Commander Camera");
            _cam = camGo.AddComponent<Camera>();
            _cam.tag = "MainCamera";
            _cam.fieldOfView = 55f;
            _cam.farClipPlane = 400f;
            camGo.AddComponent<AudioListener>();

            var cube = VoxelCube.Instance;
            _pivot = cube ? cube.transform.position : Vector3.zero;
            // start looking at the cube from behind the selected portal
            var p = SiegeDirector.Instance ? SiegeDirector.Instance.PortalPosition(_portal) : _pivot + Vector3.back * 20f;
            var away = Vector3.ProjectOnPlane(p - _pivot, Vector3.up);
            if (away.sqrMagnitude > 0.01f) _yaw = Quaternion.LookRotation(-away).eulerAngles.y;

            BuildHud();
            SelectPortal(0);
        }

        void OnDisable()
        {
            if (_cam) Destroy(_cam.gameObject);
            if (_canvas) Destroy(_canvas.gameObject);
            if (_portalMarker) Destroy(_portalMarker);
            _unitButtons.Clear(); _unitLabels.Clear(); _portalButtons.Clear();
            if (_motor) _motor.Frozen = false;
            // hand control back to the first-person rig (non-VR owners only)
            if (_flat && _avatar && _avatar.IsSpawned && _avatar.IsOwner && !_avatar.IsVR) _flat.enabled = true;
        }

        // ------------------------------------------------------------------ HUD

        void BuildHud()
        {
            _canvas = SiegeUiKit.CreateCanvas("CommanderHUD", 20);
            var root = _canvas.transform;
            var ink = SiegeUiKit.AeroInk;

            // top bar
            _topBar = SiegeUiKit.MakeRect(root, "TopBar", new Vector2(0.02f, 1f), new Vector2(0.98f, 1f), new Vector2(0, -120), new Vector2(0, -12));
            SiegeUiKit.Panel(_topBar, "Bg", SiegeUiKit.AeroGlass);
            var cells = new[] { "Money", "Era", "Wave", "Timer", "Mana", "Core" };
            var texts = new Text[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                var c = SiegeUiKit.MakeRect(_topBar, cells[i], new Vector2(i / (float)cells.Length, 0), new Vector2((i + 1) / (float)cells.Length, 1), new Vector2(6, 8), new Vector2(-6, -8));
                SiegeUiKit.Panel(c, "Bubble", i == 0 ? SiegeUiKit.AeroGold : i == 4 ? new Color(0.75f, 0.65f, 1f, 0.95f) : SiegeUiKit.AeroSky);
                texts[i] = SiegeUiKit.Label(c, "Text", "", 24, ink);
            }
            _money = texts[0]; _era = texts[1]; _wave = texts[2]; _timer = texts[3]; _mana = texts[4]; _core = texts[5];
            // XP bar under the era bubble, mana bar under the mana bubble
            var xpRt = SiegeUiKit.MakeRect(_topBar.Find("Era"), "XP", new Vector2(0.08f, 0f), new Vector2(0.92f, 0f), new Vector2(0, 2), new Vector2(0, 18));
            _xpFill = SiegeUiKit.Bar(xpRt, "XPBar", new Color(1, 1, 1, 0.7f), SiegeUiKit.AeroLime);
            var manaRt = SiegeUiKit.MakeRect(_topBar.Find("Mana"), "ManaBarRt", new Vector2(0.08f, 0f), new Vector2(0.92f, 0f), new Vector2(0, 2), new Vector2(0, 18));
            _manaFill = SiegeUiKit.Bar(manaRt, "ManaBar", new Color(1, 1, 1, 0.7f), new Color(0.6f, 0.5f, 1f, 1f));

            // status line (result / warnings)
            var statusRt = SiegeUiKit.MakeRect(root, "Status", new Vector2(0.25f, 1f), new Vector2(0.75f, 1f), new Vector2(0, -175), new Vector2(0, -125));
            _status = SiegeUiKit.Label(statusRt, "Text", "", 26, Color.white);
            _status.GetComponent<Outline>().effectColor = new Color(0.05f, 0.2f, 0.4f, 0.8f);

            // portal selector
            _portalBar = SiegeUiKit.MakeRect(root, "Portals", new Vector2(0.3f, 0f), new Vector2(0.7f, 0f), new Vector2(0, 200), new Vector2(0, 256));
            string[] names = { "Portal A", "Portal B", "Portal C" };
            for (int i = 0; i < 3; i++)
            {
                int idx = i;
                var cell = SiegeUiKit.MakeRect(_portalBar, names[i], new Vector2(i / 3f, 0), new Vector2((i + 1) / 3f, 1), new Vector2(6, 0), new Vector2(-6, 0));
                var b = SiegeUiKit.MakeButton(cell, "Button", names[i], SiegeUiKit.AeroAqua, () => SelectPortal(idx), 22);
                _portalButtons.Add(b.GetComponent<Image>());
            }

            // unit buttons
            var units = Balance.Siege.units;
            _bottomBar = SiegeUiKit.MakeRect(root, "Units", new Vector2(0.02f, 0f), new Vector2(0.98f, 0f), new Vector2(0, 12), new Vector2(0, 190));
            SiegeUiKit.Panel(_bottomBar, "Bg", SiegeUiKit.AeroGlass);
            for (int i = 0; i < units.Length; i++)
            {
                int idx = i;
                var cell = SiegeUiKit.MakeRect(_bottomBar, units[i].id, new Vector2(i / (float)units.Length, 0), new Vector2((i + 1) / (float)units.Length, 1), new Vector2(8, 10), new Vector2(-8, -10));
                var b = SiegeUiKit.MakeButton(cell, "Button", "", SiegeUiKit.AeroLime, () => Buy(idx), 20);
                _unitButtons.Add(b);
                _unitLabels.Add(b.GetComponentInChildren<Text>());
            }
        }

        void SelectPortal(int i)
        {
            _portal = Mathf.Clamp(i, 0, 2);
            for (int k = 0; k < _portalButtons.Count; k++)
                _portalButtons[k].color = k == _portal ? SiegeUiKit.AeroGold : SiegeUiKit.AeroAqua;
            var d = SiegeDirector.Instance;
            if (d == null) return;
            if (!_portalMarker)
            {
                _portalMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(_portalMarker.GetComponent<Collider>());
                _portalMarker.name = "PortalMarker";
                _portalMarker.transform.localScale = new Vector3(4f, 0.05f, 4f);
                var mr = _portalMarker.GetComponent<MeshRenderer>();
                if (Tools.FxHub.Instance) mr.sharedMaterial = Tools.FxHub.Instance.glowMaterial;
                var block = new MaterialPropertyBlock();
                block.SetColor("_BaseColor", new Color(1f, 0.85f, 0.3f, 0.45f));
                mr.SetPropertyBlock(block);
            }
            _portalMarker.transform.position = d.PortalPosition(_portal) + Vector3.up * 0.05f;
        }

        void Buy(int unitIndex)
        {
            var d = SiegeDirector.Instance;
            if (d == null || !d.IsActive) return;
            d.BuyUnitRpc(unitIndex, _portal);
        }

        // ------------------------------------------------------------------ per frame

        void Update()
        {
            var d = SiegeDirector.Instance;
            if (_motor) { _motor.Frozen = true; _motor.moveIntent = Vector3.zero; }
            HandleCamera();
            HandleHotkeys();
            if (d != null) RefreshHud(d);
            if (_portalMarker) _portalMarker.transform.Rotate(0, 30f * Time.deltaTime, 0);
        }

        void HandleHotkeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            int n = Mathf.Min(6, Balance.Siege.units.Length);
            for (int i = 0; i < n; i++)
                if (kb[Key.Digit1 + i].wasPressedThisFrame) Buy(i);
            if (kb.zKey.wasPressedThisFrame) SelectPortal(0);
            if (kb.xKey.wasPressedThisFrame) SelectPortal(1);
            if (kb.cKey.wasPressedThisFrame) SelectPortal(2);
        }

        void RefreshHud(SiegeDirector d)
        {
            var S = Balance.Siege;
            _money.text = $"${Mathf.FloorToInt(d.money.Value)}\n<size=18>+{d.Income:0}/s</size>";
            float next = d.NextEraXp, cur = d.CurrentEraXp;
            _era.text = next > 0 ? $"Era {d.era.Value}\n<size=18>{d.xp.Value:0}/{next:0} XP</size>" : $"Era {d.era.Value}\n<size=18>MAX</size>";
            _xpFill.fillAmount = next > 0 ? Mathf.InverseLerp(cur, next, d.xp.Value) : 1f;
            _wave.text = $"Wave {d.wave.Value}\n<size=18>next in {Mathf.CeilToInt(Mathf.Max(0, d.nextWaveIn.Value))}s</size>";
            _timer.text = $"{MatchManager.FormatTime(d.timeLeft.Value)}\n<size=18>God survives at 0:00</size>";
            _mana.text = $"God mana {d.mana.Value:0}\n<size=18>{(d.timeSlowLeft.Value > 0 ? "TIME SLOWED" : d.shieldsActive.Value > 0 ? "Bubble Ward up" : "intel")}</size>";
            _manaFill.fillAmount = d.mana.Value / Mathf.Max(1f, S.god.maxMana);
            var cube = VoxelCube.Instance;
            int coreTotal = cube && cube.Grid != null ? Mathf.Max(1, cube.Grid.CoreTotal) : 1;
            int coreLeft = cube ? cube.coreAlive.Value : 0;
            _core.text = $"Core {coreLeft}/{coreTotal}\n<size=18>units {d.liveUnits.Value}/{SiegeDirector.MaxLiveUnits}</size>";

            var mm = MatchManager.Instance;
            _status.text = mm && mm.state.Value == MatchState.Ended ? mm.resultText.Value.ToString()
                         : !d.IsActive ? "Waiting for the match to start..." : "";

            for (int i = 0; i < _unitButtons.Count; i++)
            {
                var u = S.units[i];
                bool locked = u.era > d.era.Value;
                bool afford = d.money.Value >= u.cost;
                _unitButtons[i].interactable = d.IsActive && !locked && afford && d.liveUnits.Value < SiegeDirector.MaxLiveUnits;
                _unitLabels[i].text = locked
                    ? $"{u.name}\n<size=16>Era {u.era}</size>"
                    : $"[{i + 1}] {u.name}\n<size=18>${u.cost}  HP {u.hp:0}</size>";
            }
        }

        bool OverHud(Vector2 screenPos)
        {
            foreach (var rt in new[] { _topBar, _bottomBar, _portalBar })
                if (rt && RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, null)) return true;
            return false;
        }

        void HandleCamera()
        {
            if (!_cam) return;
            float dt = Time.unscaledDeltaTime;
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            Vector2 pan = Vector2.zero;
            float zoom = 0f, rot = 0f;

            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) pan.y += 1;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) pan.y -= 1;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) pan.x -= 1;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) pan.x += 1;
                if (kb.qKey.isPressed) rot -= 1;
                if (kb.eKey.isPressed) rot += 1;
                pan *= 18f * dt; rot *= 90f * dt;
            }
            if (mouse != null)
            {
                Vector2 mp = mouse.position.ReadValue();
                if (!OverHud(mp))
                {
                    zoom -= mouse.scroll.ReadValue().y * 0.01f * _distance * 0.25f;
                    if (mouse.rightButton.isPressed || mouse.middleButton.isPressed)
                        pan -= mouse.delta.ReadValue() * 0.0016f * _distance;
                    if (mouse.leftButton.wasPressedThisFrame) ClickWorld(mp);
                }
            }

            var ts = Touchscreen.current;
            if (ts != null)
            {
                var active = new List<UnityEngine.InputSystem.Controls.TouchControl>();
                foreach (var t in ts.touches)
                    if (t.isInProgress && !OverHud(t.startPosition.ReadValue())) active.Add(t);
                if (active.Count == 1)
                {
                    var t = active[0];
                    pan -= t.delta.ReadValue() * 0.0022f * _distance;
                    if (t.tapCount.ReadValue() > 0 && t.phase.ReadValue() == UnityEngine.InputSystem.TouchPhase.Began) ClickWorld(t.position.ReadValue());
                    _pinchDist = 0;
                }
                else if (active.Count >= 2)
                {
                    Vector2 a = active[0].position.ReadValue(), b = active[1].position.ReadValue();
                    float dist = Vector2.Distance(a, b);
                    float ang = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
                    if (_pinchDist > 0)
                    {
                        zoom -= (dist - _pinchDist) * 0.05f;
                        rot -= Mathf.DeltaAngle(_dragTwist, ang);
                    }
                    _pinchDist = dist; _dragTwist = ang;
                }
                else _pinchDist = 0;
            }

            _yaw += rot;
            _distance = Mathf.Clamp(_distance + zoom, minDistance, maxDistance);
            var yawRot = Quaternion.Euler(0, _yaw, 0);
            _pivot += yawRot * new Vector3(pan.x, 0, pan.y);
            // keep the pivot inside the arena
            var cube = VoxelCube.Instance;
            var centre = cube ? cube.transform.position : Vector3.zero;
            var off = Vector3.ProjectOnPlane(_pivot - centre, Vector3.up);
            if (off.magnitude > 30f) _pivot = centre + off.normalized * 30f + Vector3.up * (_pivot.y - centre.y);

            var camRot = Quaternion.Euler(_pitch, _yaw, 0);
            _cam.transform.SetPositionAndRotation(_pivot - camRot * Vector3.forward * _distance, camRot);
        }

        void ClickWorld(Vector2 screenPos)
        {
            var d = SiegeDirector.Instance;
            if (d == null || !_cam) return;
            var ray = _cam.ScreenPointToRay(screenPos);
            // pick the portal closest to the clicked ground point (or the ray, if it hit nothing)
            Vector3 p;
            if (Physics.Raycast(ray, out var hit, 500f, ~0, QueryTriggerInteraction.Collide)) p = hit.point;
            else
            {
                var plane = new Plane(Vector3.up, _pivot);
                if (!plane.Raycast(ray, out float t)) return;
                p = ray.GetPoint(t);
            }
            int best = -1; float bestD = 7f;
            for (int i = 0; i < 3; i++)
            {
                float dd = Vector3.Distance(Vector3.ProjectOnPlane(d.PortalPosition(i) - p, Vector3.up), Vector3.zero);
                if (dd < bestD) { bestD = dd; best = i; }
            }
            if (best >= 0) SelectPortal(best);
        }
    }
}
