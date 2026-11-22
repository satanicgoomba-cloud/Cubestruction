using System.Collections.Generic;
using Cubestruction.Player;
using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubestruction.Siege
{
    /// <summary>
    /// The God (owner only; enabled by PlayerAvatar.ApplyRole). You're a giant sun with hands; every
    /// skill is a gesture, nothing is pointed-and-clicked.
    /// VR:
    ///   Grip near anything            grab it (units or Raiders, free); let go to throw it
    ///   Open hand, fast               swat it        slam straight down   squash it (units die, Raiders pancake)
    ///   Trigger                       a skill appears in that hand: Zeus Bolt, Sun Bomb, Hail of Blades,
    ///                                 Bubble Ward, Spring Bloom or Comet. Throw it (let go mid-swing).
    ///   Left stick click / X          next / previous throwable skill
    ///   Hands together, both triggers charge a Sun Wave; push forward (or let go) to fire it
    ///   Clap                          Lazy Clouds (slow every unit)
    /// Desktop fallback (no headset):
    ///   LMB on something   grab; flick the mouse and release to throw    1-6  throw a skill at the crosshair
    ///   K  Sun Wave at the crosshair     C  clap (Lazy Clouds)
    /// </summary>
    [DefaultExecutionOrder(-40)]   // after VRRig (-50) so we can veto its climbing, before PlayerMotor (0)
    public class GodController : MonoBehaviour
    {
        PlayerAvatar _avatar;
        PlayerMotor _motor;
        VRRig _vr;
        FlatRig _flat;
        int[] _throwable;     // skill indices thrown by hand
        int _selected;
        readonly EnemyUnit[] _held = new EnemyUnit[2];   // 0 = left, 1 = right
        readonly ulong[] _heldPlayer = { ulong.MaxValue, ulong.MaxValue };
        readonly float[] _slapReady = new float[2];
        readonly Transform[] _summon = new Transform[2];
        readonly int[] _summonSkill = { -1, -1 };
        InputAction _next, _prev;
        TextMesh _label;
        float _waveCharge = -1, _lastHandGap, _clapReady;
        Transform _waveOrb;
        // desktop grab
        float _grabDist;
        Vector3 _lastHandPos, _handVel, _handHome;

        void OnEnable()
        {
            _avatar = GetComponent<PlayerAvatar>();
            _motor = GetComponent<PlayerMotor>();
            _vr = GetComponent<VRRig>();
            _flat = GetComponent<FlatRig>();

            var list = new List<int>();
            foreach (var s in Balance.Siege.skills) if (SiegeDirector.IsThrown(s.id)) list.Add(s.index);
            _throwable = list.ToArray();
            _selected = 0;

            if (IsVR)
            {
                _next = Bind("<XRController>{LeftHand}/{Primary2DAxisClick}");
                _prev = Bind("<XRController>{LeftHand}/{PrimaryButton}");
            }
            BuildLabel();
        }

        void OnDisable()
        {
            for (int i = 0; i < 2; i++) { if (_held[i] || _heldPlayer[i] != ulong.MaxValue) Release(i, Vector3.zero); Unsummon(i); }
            _next?.Dispose(); _prev?.Dispose();
            _next = _prev = null;
            if (_label) Destroy(_label.gameObject);
            if (_waveOrb) Destroy(_waveOrb.gameObject);
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

        SkillDef Selected => Balance.Siege.skills[_throwable[_selected]];

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
            if (_next.WasPressedThisFrame()) _selected = (_selected + 1) % _throwable.Length;
            if (_prev.WasPressedThisFrame()) _selected = (_selected - 1 + _throwable.Length) % _throwable.Length;

            float scale = _motor.WorldScale;
            var L = _vr.Left; var R = _vr.Right;
            byte pose = 0;
            if (L.grip.IsPressed()) pose |= 1; if (R.grip.IsPressed()) pose |= 2;
            if (L.trigger.IsPressed()) pose |= 4; if (R.trigger.IsPressed()) pose |= 8;
            if (_avatar.handPose.Value != pose) _avatar.handPose.Value = pose;

            // ---- Sun Wave: both hands together, both triggers
            float gap = Vector3.Distance(L.t.position, R.t.position);
            bool together = gap < 0.32f * scale;
            bool bothTriggers = L.trigger.IsPressed() && R.trigger.IsPressed();
            if (_waveCharge < 0 && bothTriggers && together)
            {
                _waveCharge = 0;
                Unsummon(0); Unsummon(1);
            }
            if (_waveCharge >= 0) { TickWave(d, L, R, bothTriggers, scale); _lastHandGap = gap; return; }

            // ---- clap: hands slam together, empty
            float closing = (_lastHandGap - gap) / Mathf.Max(Time.deltaTime, 1e-4f);
            if (gap < 0.14f * scale && _lastHandGap >= 0.14f * scale && closing > 1.5f * scale && pose == 0 && Time.time >= _clapReady)
            {
                _clapReady = Time.time + 1f;
                Clap(d, (L.t.position + R.t.position) * 0.5f);
            }
            _lastHandGap = gap;

            for (int i = 0; i < 2; i++)
            {
                var h = i == 0 ? L : R;
                var handPos = h.t.position;
                bool holding = _held[i] != null || _heldPlayer[i] != ulong.MaxValue;

                // grab anything near the hand
                if (h.grip.WasPressedThisFrame() && !holding && _summon[i] == null) TryGrab(d, i, handPos, scale);
                else if (h.grip.WasReleasedThisFrame() && holding) Release(i, h.velocity * 1.3f);
                if (_held[i] != null && !_held[i].IsSpawned) _held[i] = null;   // died in our hand

                // summon a skill with the trigger, throw it by letting go mid-swing
                if (h.trigger.WasPressedThisFrame() && !holding) Summon(d, i);
                if (h.trigger.WasReleasedThisFrame() && _summon[i] != null) ThrowSummon(d, i, h.t.position, h.velocity, scale);

                // open hand moving fast through things: swat (sideways) or squash (straight down)
                if (!holding && _summon[i] == null && !h.grip.IsPressed() && h.velocity.magnitude > 2f * scale && Time.time >= _slapReady[i])
                    Swat(d, i, handPos, h.velocity, scale);
            }
        }

        void TickWave(SiegeDirector d, VRRig.HandState L, VRRig.HandState R, bool bothTriggers, float scale)
        {
            _waveCharge = Mathf.Min(1f, _waveCharge + Time.deltaTime / 1.5f);
            var mid = (L.t.position + R.t.position) * 0.5f;
            var aim = (L.t.forward + R.t.forward).normalized;
            if (!_waveOrb) _waveOrb = SiegeDirector.SkillVisual("SunBomb", 1f);
            _waveOrb.position = mid + aim * 0.1f * scale;
            _waveOrb.localScale = Vector3.one * (0.06f + 0.18f * _waveCharge) * scale * (1f + 0.06f * Mathf.Sin(Time.time * 30f));
            Haptic(true, 0.15f + 0.4f * _waveCharge, 0.02f); Haptic(false, 0.15f + 0.4f * _waveCharge, 0.02f);
            var push = (L.velocity + R.velocity) * 0.5f;
            bool pushed = Vector3.Dot(push, aim) > 1.3f * scale;
            if (pushed || !bothTriggers)
            {
                var dir = pushed ? (push.normalized + aim).normalized : aim;
                if (_waveCharge >= 0.2f) { d.SunWaveRpc(mid, dir, _waveCharge); Haptic(true, 1f, 0.3f); Haptic(false, 1f, 0.3f); }
                _waveCharge = -1;
                if (_waveOrb) Destroy(_waveOrb.gameObject);
            }
        }

        void TryGrab(SiegeDirector d, int i, Vector3 handPos, float scale)
        {
            float r = 0.25f * scale + 0.6f;
            var u = Nearest(handPos, r);
            var pl = NearestPlayer(handPos, r);
            float du = u ? Vector3.Distance(handPos, u.transform.position) : float.MaxValue;
            float dp = pl ? Vector3.Distance(handPos, pl.Motor.Chest) : float.MaxValue;
            if (pl && dp <= du) { _heldPlayer[i] = pl.OwnerClientId; d.GrabPlayerRpc(pl.OwnerClientId, i == 0); Haptic(i == 0, 0.6f, 0.08f); }
            else if (u) { _held[i] = u; d.GrabRpc(new NetworkObjectReference(u.NetworkObject), i == 0); Haptic(i == 0, 0.5f, 0.06f); }
        }

        void Swat(SiegeDirector d, int i, Vector3 handPos, Vector3 vel, float scale)
        {
            bool down = vel.y < -2f * scale && -vel.y > new Vector2(vel.x, vel.z).magnitude;
            var u = Nearest(handPos, 0.3f * scale + 0.5f);
            if (u && u.state.Value != UnitState.Held)
            {
                if (down) d.MashRpc(new NetworkObjectReference(u.NetworkObject));
                else d.SlapRpc(new NetworkObjectReference(u.NetworkObject), vel);
                _slapReady[i] = Time.time + Balance.Siege.god.handSlapCooldown;
                Haptic(i == 0, down ? 1f : 0.7f, 0.08f);
                return;
            }
            var pl = NearestPlayer(handPos, 0.3f * scale + 0.6f);
            if (pl)
            {
                d.SwatPlayerRpc(pl.OwnerClientId, vel, down);
                _slapReady[i] = Time.time + 0.3f;
                Haptic(i == 0, 0.9f, 0.1f);
            }
        }

        void Summon(SiegeDirector d, int i)
        {
            var s = Selected;
            if (d.Cooldown(s.index) > 0 || d.mana.Value < s.mana) { Haptic(i == 0, 0.15f, 0.05f); return; }
            Unsummon(i);
            var hand = i == 0 ? _avatar.leftHand : _avatar.rightHand;
            _summon[i] = SiegeDirector.SkillVisual(s.id, 0.3f * _motor.WorldScale);
            _summon[i].SetParent(hand, true);
            _summon[i].localPosition = new Vector3(0, 0.02f, 0.08f);
            _summon[i].localRotation = Quaternion.identity;
            _summonSkill[i] = s.index;
            Haptic(i == 0, 0.3f, 0.05f);
        }

        void ThrowSummon(SiegeDirector d, int i, Vector3 from, Vector3 handVel, float scale)
        {
            int skill = _summonSkill[i];
            Unsummon(i);
            if (handVel.magnitude < 1.2f * scale) return;   // a limp let-go: it just fizzles, no mana spent
            d.ThrowSkillRpc(skill, from, handVel * 1.5f);
            Haptic(i == 0, 0.7f, 0.08f);
        }

        void Unsummon(int i)
        {
            if (_summon[i]) Destroy(_summon[i].gameObject);
            _summon[i] = null; _summonSkill[i] = -1;
        }

        void Clap(SiegeDirector d, Vector3 at)
        {
            var s = d.Skill("TimeSlow");
            if (s == null || d.Cooldown(s.index) > 0 || d.mana.Value < s.mana) { Haptic(true, 0.15f, 0.05f); return; }
            d.CastSkillRpc(s.index, at);
            SiegeDirector.Claps++;
            Tools.FxHub.Instance?.LocalBlast(at, 0.45f * _motor.WorldScale, new Color(0.85f, 0.95f, 1f));
            Haptic(true, 1f, 0.15f); Haptic(false, 1f, 0.15f);
        }

        void Release(int i, Vector3 velocity)
        {
            var d = SiegeDirector.Instance;
            var u = _held[i];
            _held[i] = null;
            if (u && u.IsSpawned && d) d.ReleaseRpc(new NetworkObjectReference(u.NetworkObject), velocity);
            if (_heldPlayer[i] != ulong.MaxValue)
            {
                if (d) d.ReleasePlayerRpc(_heldPlayer[i], velocity);
                _heldPlayer[i] = ulong.MaxValue;
            }
        }

        static void Haptic(bool left, float amp, float dur)
        {
            var dev = left ? UnityEngine.InputSystem.XR.XRController.leftHand : UnityEngine.InputSystem.XR.XRController.rightHand;
            if (dev == null) return;
            var cmd = UnityEngine.InputSystem.XR.Haptics.SendHapticImpulseCommand.Create(0, amp, dur);
            dev.ExecuteCommand(ref cmd);
        }

        // ------------------------------------------------------------------ desktop fallback

        void TickDesktop(SiegeDirector d)
        {
            var mouse = Mouse.current; var kb = Keyboard.current;
            var cam = _flat && _flat.Cam ? _flat.Cam : Camera.main;
            if (cam == null) return;
            var ray = new Ray(cam.transform.position, cam.transform.forward);   // crosshair (cursor is locked)
            var hand = _avatar.rightHand;
            bool holding = _held[1] != null || _heldPlayer[1] != ulong.MaxValue;
            byte pose = (byte)(holding ? 2 : 0);
            if (_avatar.handPose.Value != pose) _avatar.handPose.Value = pose;

            if (holding)
            {
                // the held thing dangles under the right hand; put the hand on the crosshair at grab distance
                hand.position = ray.GetPoint(_grabDist);
                _handVel = Vector3.Lerp(_handVel, (hand.position - _lastHandPos) / Mathf.Max(Time.deltaTime, 1e-4f), 0.4f);
                _lastHandPos = hand.position;
                if (mouse != null && mouse.leftButton.wasReleasedThisFrame) { Release(1, _handVel * 1.2f); hand.localPosition = _handHome; }
                else if (_held[1] != null && !_held[1].IsSpawned) { _held[1] = null; hand.localPosition = _handHome; }
            }
            else if (mouse != null && mouse.leftButton.wasPressedThisFrame)
            {
                if (Physics.Raycast(ray, out var hit, 80f * _motor.WorldScale, ~0, QueryTriggerInteraction.Ignore))
                {
                    var u = hit.collider.GetComponentInParent<EnemyUnit>();
                    var pl = hit.collider.GetComponentInParent<PlayerAvatar>();
                    if (u || (pl && pl != _avatar))
                    {
                        _handHome = hand.localPosition;
                        _grabDist = Mathf.Max(2f, hit.distance * 0.8f);
                        hand.position = ray.GetPoint(_grabDist);
                        _lastHandPos = hand.position; _handVel = Vector3.zero;
                        if (u) { _held[1] = u; d.GrabRpc(new NetworkObjectReference(u.NetworkObject), false); }
                        else { _heldPlayer[1] = pl.OwnerClientId; d.GrabPlayerRpc(pl.OwnerClientId, false); }
                    }
                }
            }

            if (kb == null) return;
            for (int k = 0; k < _throwable.Length && k < 9; k++)
                if (kb[Key.Digit1 + k].wasPressedThisFrame)
                {
                    _selected = k;
                    if (Aim(ray, out var p)) DesktopThrow(d, _throwable[k], cam.transform.position + ray.direction * 2f, p);
                }
            if (kb.kKey.wasPressedThisFrame) d.SunWaveRpc(cam.transform.position + ray.direction * 2f, ray.direction, 1f);
            if (kb.cKey.wasPressedThisFrame && Time.time >= _clapReady) { _clapReady = Time.time + 1f; Clap(d, hand.position); }
        }

        /// <summary>Lob a skill so it lands on the crosshair point.</summary>
        void DesktopThrow(SiegeDirector d, int skillIndex, Vector3 from, Vector3 target)
        {
            var s = Balance.Siege.skills[skillIndex];
            if (d.Cooldown(skillIndex) > 0 || d.mana.Value < s.mana) return;
            d.ThrowSkillRpc(skillIndex, from, LobVelocity(s.id, from, target));
        }

        /// <summary>Velocity that lands a thrown skill on <paramref name="target"/> (also used by -autotest).</summary>
        public static Vector3 LobVelocity(string id, Vector3 from, Vector3 target)
        {
            float g = id == "Smite" ? 2f : id == "BladeHail" ? 3f : id == "Meteor" ? 14f : 9.81f;
            float t = Mathf.Clamp(Vector3.Distance(from, target) / 35f, 0.2f, 2f);
            return (target - from) / t + Vector3.up * 0.5f * g * t;
        }

        // ------------------------------------------------------------------ shared

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

        PlayerAvatar NearestPlayer(Vector3 pos, float radius)
        {
            PlayerAvatar best = null; float bestD = radius;
            foreach (var p in PlayerAvatar.All)
            {
                if (p == _avatar || (p.role.Value != PlayerRole.Raider && p.role.Value != PlayerRole.Miner)) continue;
                float dd = Vector3.Distance(pos, p.Motor.Chest);
                if (dd < bestD) { bestD = dd; best = p; }
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
            string hint = IsVR ? "trigger: summon + throw   grip: grab   stick/X: skill\nhands together + both triggers: Sun Wave   clap: Lazy Clouds"
                               : "1-6: throw   K: Sun Wave   C: clap   LMB: grab & throw";
            _label.text = $"{s.name}  ({s.mana:0})  {state}\nMana [{bar}] {d.mana.Value:0}\n{MatchManager.FormatTime(d.timeLeft.Value)} left  wave {d.wave.Value}\n<size=26>{hint}</size>";
            _label.color = cd > 0 || d.mana.Value < s.mana ? new Color(0.75f, 0.8f, 0.9f) : new Color(0.85f, 1f, 0.7f);
        }
    }
}
