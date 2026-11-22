using Cubestruction.Player;
using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubestruction.Siege
{
    /// <summary>
    /// The God (owner only; enabled by PlayerAvatar.ApplyRole). PlayerMotor.WorldScale makes you a giant.
    /// VR:
    ///   Grip near a unit           grab it (Divine Grip, costs mana x unit mass); release to THROW it
    ///   Swat a unit fast           slap it
    ///   Left stick click / X       next / previous skill        Right trigger   cast at the right-hand pointer
    /// Desktop fallback (no headset):
    ///   Left mouse on a unit       grab; flick the mouse and release to throw
    ///   1-5                        cast Sky Zap / Bubble Ward / Spring Bloom / Comet Drop / Lazy Clouds at the crosshair
    /// </summary>
    [DefaultExecutionOrder(-40)]   // after VRRig (-50) so we can veto its climbing, before PlayerMotor (0)
    public class GodController : MonoBehaviour
    {
        PlayerAvatar _avatar;
        PlayerMotor _motor;
        VRRig _vr;
        FlatRig _flat;
        int[] _castable;      // skill indices excluding Grab
        int _selected;
        readonly EnemyUnit[] _held = new EnemyUnit[2];   // 0 = left, 1 = right
        readonly float[] _slapReady = new float[2];
        InputAction _next, _prev, _cast;
        TextMesh _label;
        LineRenderer _aim;
        // desktop grab
        float _grabDist;
        Vector3 _lastHandPos, _handVel, _handHome;

        void OnEnable()
        {
            _avatar = GetComponent<PlayerAvatar>();
            _motor = GetComponent<PlayerMotor>();
            _vr = GetComponent<VRRig>();
            _flat = GetComponent<FlatRig>();

            var skills = Balance.Siege.skills;
            var list = new System.Collections.Generic.List<int>();
            foreach (var s in skills) if (s.id != "Grab") list.Add(s.index);
            _castable = list.ToArray();
            _selected = 0;

            if (IsVR)
            {
                _next = Bind("<XRController>{LeftHand}/{Primary2DAxisClick}");
                _prev = Bind("<XRController>{LeftHand}/{PrimaryButton}");
                _cast = Bind("<XRController>{RightHand}/{TriggerButton}");
            }
            BuildLabel();
            var aimGo = new GameObject("GodAim");
            aimGo.transform.SetParent(transform, false);
            _aim = aimGo.AddComponent<LineRenderer>();
            _aim.positionCount = 2;
            _aim.widthMultiplier = 0.06f;
            if (Tools.FxHub.Instance) _aim.material = Tools.FxHub.Instance.glowMaterial;
            _aim.startColor = _aim.endColor = new Color(1f, 0.95f, 0.6f, 0.7f);
        }

        void OnDisable()
        {
            for (int i = 0; i < 2; i++) if (_held[i]) Release(i, Vector3.zero);
            _next?.Dispose(); _prev?.Dispose(); _cast?.Dispose();
            _next = _prev = _cast = null;
            if (_label) Destroy(_label.gameObject);
            if (_aim) Destroy(_aim.gameObject);
        }

        bool IsVR => _avatar != null && _avatar.IsVR && _vr != null && _vr.enabled;

        static InputAction Bind(string path)
        {
            var a = new InputAction(type: InputActionType.Button, binding: path);
            a.Enable();
            return a;
        }

        void BuildLabel()
        {
            var go = new GameObject("GodSkillLabel");
            var parent = IsVR ? _avatar.leftHand : (_flat && _flat.Cam ? _flat.Cam.transform : _avatar.headAnchor);
            go.transform.SetParent(parent, false);
            // VR: floats above the left wrist; desktop: bottom-left of the view
            go.transform.localPosition = IsVR ? new Vector3(0, 0.08f, 0.02f) : new Vector3(-0.32f, -0.2f, 0.6f);
            go.transform.localRotation = IsVR ? Quaternion.Euler(40f, 0, 0) : Quaternion.identity;
            go.transform.localScale = Vector3.one * (IsVR ? 0.006f : 0.004f);
            _label = go.AddComponent<TextMesh>();
            _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _label.GetComponent<MeshRenderer>().sharedMaterial = _label.font.material;
            _label.fontSize = 48;
            _label.anchor = TextAnchor.LowerCenter;
            _label.alignment = TextAlignment.Center;
            _label.color = new Color(0.85f, 0.97f, 1f);
        }

        SkillDef Selected => Balance.Siege.skills[_castable[_selected]];

        void Update()
        {
            var d = SiegeDirector.Instance;
            // the God never climbs: veto VRRig's grip-climb before the motor simulates
            _motor.climbHeld = false;
            _motor.climbDelta = Vector3.zero;
            if (d == null || !d.IsActive) { UpdateLabel(d); return; }

            if (IsVR) TickVR(d);
            else TickDesktop(d);
            UpdateLabel(d);
        }

        // ------------------------------------------------------------------ VR

        void TickVR(SiegeDirector d)
        {
            if (_next.WasPressedThisFrame()) _selected = (_selected + 1) % _castable.Length;
            if (_prev.WasPressedThisFrame()) _selected = (_selected - 1 + _castable.Length) % _castable.Length;

            float scale = _motor.WorldScale;
            for (int i = 0; i < 2; i++)
            {
                var h = i == 0 ? _vr.Left : _vr.Right;
                var handPos = h.t.position;
                if (h.grip.WasPressedThisFrame() && _held[i] == null)
                {
                    var u = Nearest(handPos, 0.25f * scale + 0.6f);
                    if (u) { _held[i] = u; d.GrabRpc(new NetworkObjectReference(u.NetworkObject), i == 0); }
                }
                else if (h.grip.WasReleasedThisFrame() && _held[i] != null) Release(i, h.velocity);

                // slap: a fast open hand through a unit
                if (_held[i] == null && h.velocity.magnitude > 2f * scale && Time.time >= _slapReady[i])
                {
                    var u = Nearest(handPos, 0.3f * scale + 0.5f);
                    if (u && u.state.Value != UnitState.Held)
                    {
                        d.SlapRpc(new NetworkObjectReference(u.NetworkObject), h.velocity);
                        _slapReady[i] = Time.time + Balance.Siege.god.handSlapCooldown;
                        Haptic(i == 0, 0.7f);
                    }
                }
                // grab failed / expired on the server -> forget it
                if (_held[i] != null && !_held[i].IsSpawned) _held[i] = null;   // died in our hand
            }

            // aim + cast with the right hand
            var ray = new Ray(_vr.Right.t.position, _vr.Right.t.forward);
            bool hit = Aim(ray, out var point);
            _aim.enabled = true;
            _aim.SetPosition(0, ray.origin);
            _aim.SetPosition(1, hit ? point : ray.origin + ray.direction * 30f * scale);
            if (_cast.WasPressedThisFrame() && hit) Cast(d, _castable[_selected], point);
        }

        void Release(int i, Vector3 velocity)
        {
            var u = _held[i];
            _held[i] = null;
            if (u && u.IsSpawned && SiegeDirector.Instance)
                SiegeDirector.Instance.ReleaseRpc(new NetworkObjectReference(u.NetworkObject), velocity);
        }

        static void Haptic(bool left, float amp)
        {
            var dev = left ? UnityEngine.InputSystem.XR.XRController.leftHand : UnityEngine.InputSystem.XR.XRController.rightHand;
            dev?.ExecuteCommand(UnityEngine.InputSystem.XR.Haptics.SendHapticImpulseCommand.Create(0, amp, 0.06f));
        }

        // ------------------------------------------------------------------ desktop fallback

        void TickDesktop(SiegeDirector d)
        {
            var mouse = Mouse.current; var kb = Keyboard.current;
            var cam = _flat && _flat.Cam ? _flat.Cam : Camera.main;
            if (cam == null) return;
            var ray = new Ray(cam.transform.position, cam.transform.forward);   // crosshair (cursor is locked)
            var hand = _avatar.rightHand;

            if (_held[1] != null)
            {
                // the held unit dangles under the right hand; put the hand on the crosshair at grab distance
                hand.position = ray.GetPoint(_grabDist);
                _handVel = Vector3.Lerp(_handVel, (hand.position - _lastHandPos) / Mathf.Max(Time.deltaTime, 1e-4f), 0.4f);
                _lastHandPos = hand.position;
                if (mouse != null && mouse.leftButton.wasReleasedThisFrame) { Release(1, _handVel * 1.2f); hand.localPosition = _handHome; }
                else if (!_held[1].IsSpawned) { _held[1] = null; hand.localPosition = _handHome; }
            }
            else if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                if (Physics.Raycast(ray, out var hit, 80f * _motor.WorldScale, ~0, QueryTriggerInteraction.Ignore))
                {
                    var u = hit.collider.GetComponentInParent<EnemyUnit>();
                    if (u)
                    {
                        _held[1] = u;
                        _handHome = hand.localPosition;
                        _grabDist = Mathf.Max(2f, hit.distance * 0.8f);
                        hand.position = ray.GetPoint(_grabDist);
                        _lastHandPos = hand.position; _handVel = Vector3.zero;
                        d.GrabRpc(new NetworkObjectReference(u.NetworkObject), false);
                    }
                }
            }

            if (kb != null)
                for (int k = 0; k < _castable.Length && k < 9; k++)
                    if (kb[Key.Digit1 + k].wasPressedThisFrame)
                    {
                        _selected = k;
                        if (Aim(ray, out var p)) Cast(d, _castable[k], p);
                    }
            _aim.enabled = false;
        }

        // ------------------------------------------------------------------ shared

        void Cast(SiegeDirector d, int skillIndex, Vector3 point)
        {
            var s = Balance.Siege.skills[skillIndex];
            if (d.Cooldown(skillIndex) > 0 || d.mana.Value < s.mana) { Haptic(false, 0.15f); return; }
            d.CastSkillRpc(skillIndex, point);
            Haptic(false, 0.5f);
        }

        bool Aim(Ray ray, out Vector3 point)
        {
            point = default;
            float maxDist = 120f * _motor.WorldScale;
            var hits = Physics.RaycastAll(ray, maxDist, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.collider.GetComponentInParent<PlayerAvatar>() == _avatar) continue;
                if (h.distance < best) { best = h.distance; point = h.point; }
            }
            var cube = VoxelCube.Instance;
            if (cube && cube.Raycast(ray, maxDist, out _, out var vp) && Vector3.Distance(ray.origin, vp) < best)
            {
                best = Vector3.Distance(ray.origin, vp); point = vp;
            }
            return best < float.MaxValue;
        }

        EnemyUnit Nearest(Vector3 pos, float radius)
        {
            EnemyUnit best = null; float bestD = radius;
            foreach (var u in EnemyUnit.Live)
            {
                if (!u || !u.IsSpawned) continue;
                float dd = Vector3.Distance(pos, u.transform.position + Vector3.up * 0.4f * u.transform.localScale.y);
                if (dd < bestD) { bestD = dd; best = u; }
            }
            return best;
        }

        void UpdateLabel(SiegeDirector d)
        {
            if (!_label) return;
            if (d == null || !d.IsActive) { _label.text = "God of the Cube\nwaiting for the siege..."; return; }
            var s = Selected;
            float cd = d.Cooldown(s.index);
            float maxMana = Mathf.Max(1f, Balance.Siege.god.maxMana);
            int filled = Mathf.RoundToInt(10f * d.mana.Value / maxMana);
            string bar = new string('|', filled) + new string('.', 10 - filled);
            string state = cd > 0 ? $"{cd:0.0}s" : d.mana.Value < s.mana ? "need mana" : "READY";
            string hint = IsVR ? "stick-click/X: skill   trigger: cast   grip: grab" : "1-5: cast   LMB: grab & throw";
            _label.text = $"{s.name}  ({s.mana:0})  {state}\nMana [{bar}] {d.mana.Value:0}\n{MatchManager.FormatTime(d.timeLeft.Value)} left  wave {d.wave.Value}\n<size=28>{hint}</size>";
            _label.color = cd > 0 || d.mana.Value < s.mana ? new Color(0.75f, 0.8f, 0.9f) : new Color(0.85f, 1f, 0.7f);
        }
    }
}
