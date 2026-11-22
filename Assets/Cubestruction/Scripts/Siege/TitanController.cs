using Cubestruction.Player;
using Cubestruction.Voxels;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubestruction.Siege
{
    /// <summary>
    /// The Titan (owner only; enabled by PlayerAvatar.ApplyRole). You're ~6.5 m tall and slow-but-heavy.
    /// VR:
    ///   Hands through voxels      scrape (no cooldown, it just grinds)    a fast punch   big dent, bigger the harder
    ///   Walking into walls        your legs grind them too
    ///   A / X                     kick with the right / left leg (you stop for a moment; huge hit)
    /// Desktop fallback (no headset): LMB punch, hold RMB to scrape, F kick, WASD + mouse as usual.
    /// </summary>
    [DefaultExecutionOrder(-40)]   // after the rigs (-50): we veto climbing and freeze legs mid-kick
    public class TitanController : MonoBehaviour
    {
        static TitanController _local;
        PlayerAvatar _avatar;
        PlayerMotor _motor;
        VRRig _vr;
        FlatRig _flat;
        InputAction _kickR, _kickL;
        TextMesh _label;
        readonly float[] _sendAt = new float[3];
        readonly Vector3[] _last = new Vector3[2];
        float _kickReady, _kickLockUntil, _hurtFlash, _punchAnim = -1, _scrapeAt;
        Vector3 _rHome;

        public const float WalkMul = 0.22f;

        void OnEnable()
        {
            _local = this;
            _avatar = GetComponent<PlayerAvatar>();
            _motor = GetComponent<PlayerMotor>();
            _vr = GetComponent<VRRig>();
            _flat = GetComponent<FlatRig>();
            _motor.speedMul = WalkMul;
            _motor.canJump = false;
            _motor.OnHit += OnBodyHit;
            if (IsVR)
            {
                _kickR = Bind("<XRController>{RightHand}/{PrimaryButton}");
                _kickL = Bind("<XRController>{LeftHand}/{PrimaryButton}");
            }
            else _rHome = _avatar.rightHand.localPosition;
            BuildLabel();
        }

        void OnDisable()
        {
            if (_local == this) _local = null;
            _motor.speedMul = 1f;
            _motor.canJump = true;
            _motor.OnHit -= OnBodyHit;
            _kickR?.Dispose(); _kickL?.Dispose(); _kickR = _kickL = null;
            if (_label) Destroy(_label.gameObject);
            if (!IsVR && _avatar) _avatar.rightHand.localPosition = _rHome;
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
            var go = new GameObject("TitanLabel");
            var parent = IsVR ? _avatar.leftHand : (_flat && _flat.Cam ? _flat.Cam.transform : _avatar.headAnchor);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = IsVR ? new Vector3(0, 0.08f, 0.02f) : new Vector3(0f, 0.26f, 0.8f);
            go.transform.localRotation = IsVR ? Quaternion.Euler(40f, 0, 0) : Quaternion.identity;
            go.transform.localScale = Vector3.one * (IsVR ? 0.006f : 0.004f);   // scales with the giant, so it reads the same
            _label = go.AddComponent<TextMesh>();
            _label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _label.GetComponent<MeshRenderer>().sharedMaterial = _label.font.material;
            _label.fontSize = 48;
            _label.anchor = TextAnchor.LowerCenter;
            _label.alignment = TextAlignment.Center;
        }

        void Update()
        {
            var d = TitanDirector.Instance;
            _motor.climbHeld = false;            // giants don't climb
            _motor.climbDelta = Vector3.zero;
            _motor.reelIn = false;
            bool locked = TitanDirector.InputLocked(_avatar) || Time.time < _kickLockUntil;
            if (locked) { _motor.moveIntent = Vector3.zero; _motor.sprint = false; }
            if (d == null || !d.IsActive) { UpdateLabel(d); return; }

            if (IsVR) TickVR(d, locked);
            else TickDesktop(d, locked);
            UpdateLabel(d);
        }

        // ------------------------------------------------------------------ VR

        void TickVR(TitanDirector d, bool locked)
        {
            if (!locked && (_kickR.WasPressedThisFrame() || _kickL.WasPressedThisFrame())) Kick(d, _kickL.WasPressedThisFrame());
            if (locked && TitanDirector.InputLocked(_avatar)) return;
            for (int i = 0; i < 2; i++)
            {
                var h = i == 0 ? _vr.Left : _vr.Right;
                Limb(d, (byte)i, h.t.position, h.velocity);
            }
        }

        /// <summary>A hand touching voxels: scrape it (12 times a second), the server decides scrape vs punch.</summary>
        void Limb(TitanDirector d, byte limb, Vector3 pos, Vector3 vel)
        {
            var cube = VoxelCube.Instance;
            float s = _motor.WorldScale;
            if (!cube || Time.time < _sendAt[limb]) return;
            if (vel.magnitude < 0.25f * s) return;   // resting a hand on the wall does nothing
            if (!cube.SolidNear(pos, 0.13f * s, out var closest)) return;
            _sendAt[limb] = Time.time + 1f / 12f;
            var point = Vector3.Lerp(pos, closest, 0.5f);
            d.TitanHitRpc(point, vel, limb);
            float real = vel.magnitude / s;
            Haptic(limb == 0, real >= TitanDirector.PunchMinSpeed ? 0.9f : 0.25f, real >= TitanDirector.PunchMinSpeed ? 0.12f : 0.04f);
        }

        /// <summary>Legs: walking into a wall grinds it.</summary>
        void OnBodyHit(ControllerColliderHit hit)
        {
            var d = TitanDirector.Instance;
            if (d == null || !d.IsActive || TitanDirector.InputLocked(_avatar) || Time.time < _sendAt[2]) return;
            if (hit.normal.y > 0.5f || hit.collider.GetComponentInParent<VoxelChunk>() == null) return;
            var v = _motor.Velocity; v.y = 0;
            float s = _motor.WorldScale;
            var push = _motor.moveIntent * _motor.walkSpeed * WalkMul * s;   // into the wall even when blocked
            if (push.magnitude < 0.3f * s) return;
            _sendAt[2] = Time.time + 1f / 8f;
            d.TitanHitRpc(hit.point - hit.normal * 0.1f, push, 2);
        }

        void Kick(TitanDirector d, bool left)
        {
            if (Time.time < _kickReady) return;
            _kickReady = Time.time + 1.0f;
            _kickLockUntil = Time.time + 0.9f;   // plant your foot and swing
            d.KickRpc(left);
            GetComponentInChildren<TitanVisual>()?.Kick(left);
            Haptic(left, 0.6f, 0.2f);
        }

        // ------------------------------------------------------------------ desktop

        void TickDesktop(TitanDirector d, bool locked)
        {
            var kb = Keyboard.current; var mouse = Mouse.current;
            var cam = _flat && _flat.Cam ? _flat.Cam : Camera.main;
            if (cam == null) return;
            float s = _motor.WorldScale;
            if (!locked && kb != null && kb.fKey.wasPressedThisFrame) Kick(d, false);
            if (TitanDirector.InputLocked(_avatar)) return;
            var ray = new Ray(cam.transform.position, cam.transform.forward);
            bool menus = UI.GameMenus.Instance != null && UI.GameMenus.Instance.BlocksGameplayInput;

            // punch: the right fist lunges at the crosshair
            if (!menus && _flat.UseHeld && _punchAnim < 0) _punchAnim = 0;
            if (_punchAnim >= 0)
            {
                _punchAnim += Time.deltaTime / 0.35f;
                float k = Mathf.Sin(Mathf.Clamp01(_punchAnim) * Mathf.PI);
                _avatar.rightHand.localPosition = _rHome + new Vector3(-0.15f, 0.1f, 0.55f) * k;
                if (_punchAnim >= 0.45f && _punchAnim - Time.deltaTime / 0.35f < 0.45f && Reach(ray, out var p))
                    d.TitanHitRpc(p, ray.direction * 3.4f * s, 1);   // a full-strength swing
                if (_punchAnim >= 1f) { _punchAnim = -1; _avatar.rightHand.localPosition = _rHome; }
            }
            // scrape: hold RMB to drag the left hand along whatever you're looking at
            if (!menus && mouse != null && mouse.rightButton.isPressed && Time.time >= _scrapeAt && Reach(ray, out var sp))
            {
                _scrapeAt = Time.time + 1f / 12f;
                d.TitanHitRpc(sp, (ray.direction + cam.transform.right * 0.3f) * 0.6f * s, 0);
            }
        }

        bool Reach(Ray ray, out Vector3 point)
        {
            point = default;
            var cube = VoxelCube.Instance;
            float reach = 1.15f * _motor.WorldScale;
            return cube && cube.Raycast(ray, reach, out _, out point);
        }

        // ------------------------------------------------------------------ feedback

        public static void OnHurt(float dmg)
        {
            if (_local == null) return;
            _local._hurtFlash = 0.35f;
            Haptic(true, 0.5f, 0.1f); Haptic(false, 0.5f, 0.1f);
        }

        public static void OnPunchLanded(byte limb, float radius)
        {
            if (_local == null || limb > 1) return;
            Haptic(limb == 0, 1f, 0.15f + radius * 0.02f);
        }

        static void Haptic(bool left, float amp, float dur)
        {
            var dev = left ? UnityEngine.InputSystem.XR.XRController.leftHand : UnityEngine.InputSystem.XR.XRController.rightHand;
            if (dev == null) return;
            var cmd = UnityEngine.InputSystem.XR.Haptics.SendHapticImpulseCommand.Create(0, amp, dur);
            dev.ExecuteCommand(ref cmd);
        }

        void UpdateLabel(TitanDirector d)
        {
            if (!_label) return;
            _hurtFlash = Mathf.Max(0, _hurtFlash - Time.deltaTime);
            if (d == null || !d.IsActive) { _label.text = "TITAN\nwaiting for the siege..."; return; }
            float hp = d.titanHp.Value, max = Mathf.Max(1f, d.titanMaxHp.Value);
            int filled = Mathf.RoundToInt(10f * hp / max);
            string bar = new string('|', filled) + new string('.', 10 - filled);
            string state = d.prep.Value ? $"Defenders are loading their guns: {Mathf.CeilToInt(d.prepLeft.Value)}s"
                                        : $"Break wall {d.wall.Value + 1} of 3   {MatchManager.FormatTime(d.timeLeft.Value)}";
            string hint = IsVR ? "swing through walls: scrape   fast punch: dent   A/X: kick" : "LMB punch   RMB scrape   F kick";
            _label.text = $"TITAN  [{bar}] {hp:0}\n{state}\n<size=28>{hint}</size>";
            _label.color = _hurtFlash > 0 ? new Color(1f, 0.4f, 0.35f) : new Color(1f, 0.92f, 0.8f);
        }
    }
}
