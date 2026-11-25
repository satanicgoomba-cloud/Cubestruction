using System.Linq;
using Cubestruction.Player;
using Cubestruction.Tools;
using Cubestruction.UI;
using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubestruction.Super
{
    /// <summary>Written by the SUPER! touch buttons (UI/MobileControls); read by FighterController.</summary>
    public static class FightInput
    {
        public static bool Light, Heavy, Block, PowerUp, Land;
        public static readonly bool[] Special = new bool[4];   // A, B, C, Ultimate (held)
        public static void ConsumeFrame() { Light = Heavy = PowerUp = false; }
    }

    /// <summary>
    /// SUPER! controls (owner only; enabled for the Fighter role).
    ///   PC / Mac      LMB punch chain · RMB kick (look up: launcher · in the air looking down: axe kick)
    ///                 C block (tap just before a hit: parry) · Q / E / R specials A / B / C · F Power Up
    ///                 G ultimate · Space in the air: fly (once your tier allows) · Ctrl: fly down
    ///   Gamepad       RT punch · LT kick · RB block · X / Y / B specials · D-pad up Power Up · D-pad down ultimate · LB down
    ///   Mobile        Punch / Kick / Block / A / B / C / POWER / ULT buttons; hold Jump in the air to fly
    ///   VR            real punches · both fists up by your face: block · A / X / B specials (hold B to charge a
    ///                 beam between your hands) · hold both grips + both triggers: Power Up · fist above your head
    ///                 + its trigger: ultimate · both triggers with your hands pointing down: fly
    /// Charged moves (beams, the sun) charge while held and fire on release.
    /// </summary>
    public class FighterController : MonoBehaviour
    {
        public static FighterController Local { get; private set; }

        PlayerAvatar _av; FighterState _fs; PlayerMotor _m; FlatRig _flat; VRRig _vrRig;
        bool _vr;
        InputAction _light, _heavy, _block, _a, _b, _c, _power, _ult, _down, _fly;
        float _baseJump, _flyFuel = 2.5f, _punchL, _punchR, _transformHold, _charge, _beamUntil, _beamAimAt, _localAnimT, _ultHand;
        bool _flying, _beamFiring, _frozenHeld, _prevSpecial0, _prevSpecial1, _prevSpecial2, _prevSpecial3, _vrUltHeld;
        int _chargingSlot = -1, _punchSide, _meteorPhase;
        float _meteorT; Vector3 _meteorTarget;
        readonly float[] _cd = new float[4];
        GameObject _orb, _marker;
        Vector3 _rBase, _lBase;
        public static int SentMelee, SentSpecials, SentPunches;   // -autotest

        public FighterState State => _fs;
        public bool Flying => _flying;
        public bool BeamFiring => _beamFiring;
        public int ChargingSlot => _chargingSlot;
        public float Charge => _charge;
        public bool InClash => _fs && SuperDirector.Instance && SuperDirector.Instance.Clashing.Contains(_fs.Id);
        public float CooldownLeft(int slot) => Mathf.Max(0f, _cd[slot] - Time.time);

        /// <summary>-autotest hooks.</summary>
        [System.NonSerialized] public Vector3? autoAim;

        void OnEnable()
        {
            Local = this;
            _av = GetComponent<PlayerAvatar>(); _fs = GetComponent<FighterState>(); _m = GetComponent<PlayerMotor>();
            _flat = GetComponent<FlatRig>(); _vrRig = GetComponent<VRRig>();
            _vr = _av.IsVR;
            _baseJump = _m.jumpSpeed;
            if (_m.IsGrappling) _m.StopGrapple();   // a web line left over from another mode
            _m.canJump = true; _m.heldBy = null;
            _light = Btn("<Mouse>/leftButton", "<Gamepad>/rightTrigger");
            _heavy = Btn("<Mouse>/rightButton", "<Gamepad>/leftTrigger");
            _block = Btn("<Keyboard>/c", "<Gamepad>/rightShoulder");
            _a = Btn("<Keyboard>/q", "<Gamepad>/buttonWest");
            _b = Btn("<Keyboard>/e", "<Gamepad>/buttonNorth");
            _c = Btn("<Keyboard>/r", "<Gamepad>/buttonEast");
            _power = Btn("<Keyboard>/f", "<Gamepad>/dpad/up");
            _ult = Btn("<Keyboard>/g", "<Gamepad>/dpad/down");
            _down = Btn("<Keyboard>/leftCtrl", "<Gamepad>/leftShoulder");
            _fly = Btn("<Keyboard>/space", "<Gamepad>/buttonSouth");
            _rBase = _av.rightHand.localPosition; _lBase = _av.leftHand.localPosition;
            for (int i = 0; i < 4; i++) _cd[i] = 0;
        }

        static InputAction Btn(string a, string b)
        {
            var act = new InputAction(type: InputActionType.Button);
            act.AddBinding(a); if (b != null) act.AddBinding(b);
            act.Enable();
            return act;
        }

        void OnDisable()
        {
            if (Local == this) Local = null;
            foreach (var a in new[] { _light, _heavy, _block, _a, _b, _c, _power, _ult, _down, _fly }) a?.Disable();
            StopFlying();
            CancelCharge();
            if (_m) { _m.jumpSpeed = _baseJump > 0 ? _baseJump : 5.2f; _m.speedMul = 1f; _m.forcedVelocity = null; }
            if (_fs && _fs.IsSpawned && _fs.IsOwner) _fs.blocking.Value = false;
            if (_marker) Destroy(_marker);
            GameMenus.Instance?.SetBanner(null);
        }

        // ------------------------------------------------------------------ helpers

        SuperDirector D => SuperDirector.Instance;
        bool Sandbox => D && D.IsSandbox;
        FighterDef Def => _fs.Def;

        Ray Aim
        {
            get
            {
                Ray r;
                if (_vr) r = new Ray(_av.headAnchor.position, _av.headAnchor.forward);
                else if (_flat != null && _flat.Cam != null) r = _flat.AimRay;
                else r = new Ray(_av.headAnchor.position, _av.headAnchor.forward);
                if (autoAim.HasValue) r = new Ray(r.origin, (autoAim.Value - r.origin).normalized);
                return r;
            }
        }

        Vector3 Origin => _vr ? (_av.leftHand.position + _av.rightHand.position) * 0.5f : _fs.Chest + Aim.direction * 0.3f;

        public bool Unlocked(int slot) => _fs && (Sandbox || _fs.Tier >= Def.moves[slot].tier) && (slot < 3 || Sandbox || _fs.Tier >= Roster.MaxTier);
        public bool Affordable(int slot) => Sandbox || _fs.power.Value >= Def.moves[slot].cost;
        public int FlyLevel => !_fs ? 0 : Sandbox || _fs.Tier > Def.flyTier ? 2 : _fs.Tier == Def.flyTier ? 1 : 0;   // 1 = bursts, 2 = full flight

        bool Pressed(InputAction a, bool mobile) => a.WasPressedThisFrame() || mobile;

        // ------------------------------------------------------------------ main loop

        void Update()
        {
            var d = D;
            if (d == null || !d.IsActive || _fs == null || !_fs.inMatch.Value) { FightInput.ConsumeFrame(); return; }
            float s = _m.WorldScale;
            _m.speedMul = Def.speedMul * Roster.TierSpeed(_fs.Tier);
            _m.jumpSpeed = _baseJump * (1f + 0.08f * (_fs.Tier - 1));

            // frozen (Frost Dome, Absolute Zero, someone stopped time) or knocked out: hold still
            bool frozen = _fs.Frozen(d.Now);
            if (frozen || _fs.down.Value)
            {
                if (frozen && !_fs.down.Value) { _m.forcedVelocity = Vector3.zero; _frozenHeld = true; }
                StopFlying(); CancelCharge();
                if (_fs.blocking.Value) _fs.blocking.Value = false;
                FightInput.ConsumeFrame();
                GameMenus.Instance?.SetBanner(frozen ? (d.timeStopUntil.Value > d.Now ? "TIME HAS STOPPED" : "FROZEN!") : null);
                return;
            }
            if (_frozenHeld) { _frozenHeld = false; _m.forcedVelocity = null; GameMenus.Instance?.SetBanner(null); }
            bool menus = GameMenus.Instance != null && GameMenus.Instance.BlocksGameplayInput;
            if (menus) { FightInput.ConsumeFrame(); return; }

            if (_meteorPhase > 0) { Meteor(); FightInput.ConsumeFrame(); return; }
            if (_vr) UpdateVR(s); else UpdateFlat(s);
            UpdateFlight(s);
            UpdateCharge();
            UpdateBeam();
            FightInput.ConsumeFrame();
        }

        void LateUpdate()
        {
            if (_vr || _av == null) return;
            // flat view-model: jab the hands forward for a moment
            float k = Mathf.Clamp01(1f - (Time.time - _localAnimT) / 0.14f);
            var hand = _punchSide == 0 ? _av.rightHand : _av.leftHand;
            var other = _punchSide == 0 ? _av.leftHand : _av.rightHand;
            var baseH = _punchSide == 0 ? _rBase : _lBase;
            hand.localPosition = baseH + new Vector3(_punchSide == 0 ? -0.12f : 0.12f, 0.08f, 0.32f) * Mathf.Sin(k * Mathf.PI * 0.5f);
            other.localPosition = _punchSide == 0 ? _lBase : _rBase;
            if (_fs && _fs.blocking.Value)
            {
                _av.rightHand.localPosition = _rBase + new Vector3(-0.1f, 0.15f, -0.08f);
                _av.leftHand.localPosition = _lBase + new Vector3(0.1f, 0.15f, -0.08f);
            }
        }

        void UpdateFlat(float s)
        {
            bool clash = InClash;
            bool block = _block.IsPressed() || FightInput.Block;
            if (_fs.blocking.Value != (block && !clash && _chargingSlot < 0)) _fs.blocking.Value = block && !clash && _chargingSlot < 0;
            if (clash)
            {
                GameMenus.Instance?.SetBanner("BEAM CLASH!  MASH PUNCH / USE!");
                if (Pressed(_light, FightInput.Light) || Pressed(_heavy, FightInput.Heavy)) D.MashRpc();
                return;
            }
            if (block) return;
            var aim = Aim;

            if (Pressed(_light, FightInput.Light) && _chargingSlot < 0 && !_beamFiring)
            {
                SentMelee++;
                D.MeleeRpc(0, _fs.Chest, aim.direction);
                _fs.Cue(new[] { FighterState.Anim.Jab, FighterState.Anim.Cross, FighterState.Anim.Hook }[Random.Range(0, 3)]);
                _punchSide = 1 - _punchSide; _localAnimT = Time.time;
            }
            if (Pressed(_heavy, FightInput.Heavy) && _chargingSlot < 0 && !_beamFiring)
            {
                SentMelee++;
                byte kind = aim.direction.y > 0.35f ? (byte)2 : !_m.Grounded && aim.direction.y < -0.3f ? (byte)3 : (byte)1;
                D.MeleeRpc(kind, _fs.Chest, aim.direction);
                _fs.Cue(kind == 2 ? FighterState.Anim.Launcher : kind == 3 ? FighterState.Anim.Axe : FighterState.Anim.Kick);
            }
            if (Pressed(_power, FightInput.PowerUp)) TryPowerUp();

            bool a = _a.IsPressed() || FightInput.Special[0], b = _b.IsPressed() || FightInput.Special[1], c = _c.IsPressed() || FightInput.Special[2], u = _ult.IsPressed() || FightInput.Special[3];
            HandleSpecialButton(0, a, ref _prevSpecial0);
            HandleSpecialButton(1, b, ref _prevSpecial1);
            HandleSpecialButton(2, c, ref _prevSpecial2);
            HandleSpecialButton(3, u, ref _prevSpecial3);
        }

        /// <summary>Instant specials fire on press; charged ones (beams, the sun) charge while held.</summary>
        void HandleSpecialButton(int slot, bool held, ref bool prev)
        {
            bool pressed = held && !prev, released = !held && prev;
            prev = held;
            if (pressed && _chargingSlot < 0 && !_beamFiring)
            {
                if (!CheckReady(slot)) return;
                var m = Def.moves[slot];
                if (m.kind == MoveKind.Beam || m.kind == MoveKind.Sphere) { _chargingSlot = slot; _charge = 0; _fs.Cue(FighterState.Anim.Charge); return; }
                if (m.kind == MoveKind.MeteorDrop) { Fire(slot, 1f); StartMeteor(); return; }
                Fire(slot, 1f);
            }
            if (released && _chargingSlot == slot) ReleaseCharge();
        }

        bool CheckReady(int slot)
        {
            var m = Def.moves[slot];
            if (!Unlocked(slot)) { GameMenus.Instance?.Toast($"{m.name} unlocks at Tier {(slot == 3 ? Roster.MaxTier : m.tier)}", AeroPalette.Danger); return false; }
            if (CooldownLeft(slot) > 0) return false;
            if (!Affordable(slot)) { GameMenus.Instance?.Toast($"{m.name} needs {m.cost:0} Power", AeroPalette.Danger); return false; }
            return true;
        }

        void Fire(int slot, float charge)
        {
            var m = Def.moves[slot];
            var aim = Aim;
            SentSpecials++;
            D.SpecialRpc((byte)slot, m.kind == MoveKind.Beam ? Origin : _fs.Chest, aim.direction, charge);
            _cd[slot] = Time.time + m.cooldown * (slot == 3 && Sandbox ? 0.6f : 1f);
            _fs.Cue(slot == 3 ? FighterState.Anim.Ultimate : m.kind == MoveKind.Beam ? FighterState.Anim.Beam : FighterState.Anim.Special);
            if (m.kind == MoveKind.Beam) { _beamFiring = true; _beamUntil = Time.time + Roster.BeamFire + 0.3f; _beamAimAt = 0; }
            if (m.kind == MoveKind.RisingStrike || m.kind == MoveKind.Grab) StopFlying();
        }

        void TryPowerUp()
        {
            if (Sandbox) { GameMenus.Instance?.Toast("Sandbox: you're already at full power!", AeroPalette.Gold); return; }
            if (_fs.Tier >= Roster.MaxTier) return;
            if (_fs.power.Value < Roster.PowerMax - 0.01f) { GameMenus.Instance?.Toast("Fill your Power bar to power up!", AeroPalette.SkyDeep); return; }
            D.TransformRpc();
            _fs.Cue(FighterState.Anim.PowerUp);
        }

        // ------------------------------------------------------------------ VR

        void UpdateVR(float s)
        {
            if (_vrRig == null) return;
            var L = _vrRig.Left; var R = _vrRig.Right;
            var head = _av.headAnchor;
            bool clash = InClash;

            // block: both fists up in front of your face
            bool guard = Vector3.Distance(L.t.position, head.position) < 0.32f * s && Vector3.Distance(R.t.position, head.position) < 0.32f * s &&
                         Vector3.Dot(head.forward, (L.t.position + R.t.position) * 0.5f - head.position) > 0.02f * s;
            if (_fs.blocking.Value != (guard && !clash)) _fs.blocking.Value = guard && !clash;

            if (clash)
            {
                GameMenus.Instance?.SetBanner("BEAM CLASH!  PUSH!  MASH THE TRIGGERS!");
                if (L.trigger.WasPressedThisFrame() || R.trigger.WasPressedThisFrame()) D.MashRpc();
                // shoving both hands forward counts too
                if (Vector3.Dot((L.velocity + R.velocity) * 0.5f, head.forward) > 2.5f * s && Time.time > _punchL) { _punchL = Time.time + 0.15f; D.MashRpc(); }
                return;
            }

            // real punches: a fast fist near a foe or the scenery
            if (!guard)
            {
                VrPunch(L, ref _punchL, s);
                VrPunch(R, ref _punchR, s);
            }

            // Power Up: hold both grips + both triggers (flex!)
            bool flex = L.grip.IsPressed() && R.grip.IsPressed() && L.trigger.IsPressed() && R.trigger.IsPressed();
            _transformHold = flex ? _transformHold + Time.deltaTime : 0f;
            if (_transformHold > 0.8f) { _transformHold = -99f; TryPowerUp(); }

            // specials: A = special A, X = special B, B (hold) = special C
            bool a = R.primary.IsPressed(), x = L.primary.IsPressed(), b = R.secondary.IsPressed();
            HandleSpecialButton(0, a, ref _prevSpecial0);
            HandleSpecialButton(1, x, ref _prevSpecial1);
            HandleSpecialButton(2, b, ref _prevSpecial2);

            // ultimate: a fist raised above your head + its trigger (hold to charge the sun, throw to release)
            var hand = R.t.position.y > head.position.y + 0.12f * s ? R : L.t.position.y > head.position.y + 0.12f * s ? L : null;
            bool ultHeld = hand != null && hand.trigger.IsPressed() && !hand.grip.IsPressed();
            if (_chargingSlot == 3 && !ultHeld && _prevSpecial3)
            {
                // released: throw the sun the way the hand is moving (if it's moving)
                var hv = (R.velocity.magnitude > L.velocity.magnitude ? R : L).velocity;
                if (hv.magnitude > 1.5f * s) autoAimOnce = _fs.Chest + hv.normalized * 30f;
            }
            HandleSpecialButton(3, ultHeld || (_chargingSlot == 3 && (R.trigger.IsPressed() || L.trigger.IsPressed())), ref _prevSpecial3);
        }

        Vector3? autoAimOnce;

        void VrPunch(VRRig.HandState h, ref float next, float s)
        {
            if (Time.time < next || h.grip.IsPressed() || h.velocity.magnitude < 2.2f * s) return;
            var fist = h.t.position;
            bool near = FighterState.All.Any(f => f != _fs && f.Alive && Vector3.Distance(f.Chest, fist) < 0.9f * f.Scale);
            var cube = VoxelCube.Instance;
            if (!near && !(cube && cube.SolidNear(fist, 0.22f * s, out _))) return;
            next = Time.time + 0.16f;
            SentPunches++;
            D.PunchRpc(fist, h.velocity);
            _fs.Cue(FighterState.Anim.Jab);
            Haptic(h, 0.5f);
        }

        static void Haptic(VRRig.HandState h, float amp)
        {
            var dev = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(h.IsLeft ? UnityEngine.XR.XRNode.LeftHand : UnityEngine.XR.XRNode.RightHand);
            if (dev.isValid) dev.SendHapticImpulse(0, amp, 0.08f);
        }

        // ------------------------------------------------------------------ flight

        void UpdateFlight(float s)
        {
            int level = FlyLevel;
            if (level == 0) { StopFlying(); return; }
            if (_m.Grounded && !_flying) _flyFuel = Mathf.Min(2.5f, _flyFuel + Time.deltaTime);

            Vector3 thrust;
            bool want;
            if (_vr && _vrRig != null)
            {
                var L = _vrRig.Left; var R = _vrRig.Right;
                bool both = L.trigger.IsPressed() && R.trigger.IsPressed() && !L.grip.IsPressed() && !R.grip.IsPressed() && _chargingSlot < 0;
                var palms = (L.t.forward + R.t.forward) * 0.5f;
                want = both && palms.y < -0.35f || (_flying && both);
                thrust = -palms.normalized;
            }
            else
            {
                bool up = _fly.IsPressed() || MobileInput.JumpHeld;
                // (MobileInput.Jump is used up by FlatRig earlier in the frame, so watch the held flag's edge)
                bool mobilePress = MobileInput.JumpHeld && !_prevJumpHeld;
                if (!_flying && !_m.Grounded && (_fly.WasPressedThisFrame() || mobilePress)) want = true;
                else want = _flying;
                float vert = up ? 1f : (_down.IsPressed() || FightInput.Land) ? -1f : 0f;
                thrust = Vector3.up * vert;
            }
            _prevJumpHeld = MobileInput.JumpHeld;
            if (!_flying && want) { _flying = true; _flyStart = Time.time; }
            if (!_flying) return;

            if (level == 1) { _flyFuel -= Time.deltaTime; if (_flyFuel <= 0) { StopFlying(); return; } }
            float speed = 9f * s * Roster.TierSpeed(_fs.Tier) * Def.speedMul * (_m.sprint ? 1.6f : 1f);
            var h = _m.moveIntent * speed;
            var v = _vr ? thrust * speed * (want ? 1f : 0f) : thrust * speed * 0.8f;
            if (!_vr && !_fly.IsPressed() && !MobileInput.JumpHeld && !_down.IsPressed() && !FightInput.Land) v = Vector3.zero;   // hover
            var vel = h + v;
            if (_vr && !want) vel.y -= 2f * s;                                  // VR: let go of the triggers to drift down
            _m.forcedVelocity = vel;
            // landed
            if (_m.Grounded && vel.y <= 0.01f && Time.time > _flyStart + 0.3f) StopFlying();
            if (_fs.transform.position.y > D.Origin.y + 40f) _m.forcedVelocity = new Vector3(vel.x, Mathf.Min(vel.y, 0f), vel.z);   // ceiling
        }
        float _flyStart; bool _prevJumpHeld;

        void StopFlying()
        {
            if (!_flying) return;
            _flying = false;
            if (_m && _meteorPhase == 0 && !_frozenHeld) _m.forcedVelocity = null;
        }

        public void OnKnocked(Vector3 v) { if (v.magnitude > 4f) StopFlying(); }
        public void OnKo() { StopFlying(); CancelCharge(); _beamFiring = false; _meteorPhase = 0; if (_m) _m.forcedVelocity = null; if (_marker) _marker.SetActive(false); }
        public void OnDenied(int slot)
        {
            _cd[slot] = 0;
            if (Def.moves[slot].kind == MoveKind.Beam) _beamFiring = false;
            if (Def.moves[slot].kind == MoveKind.MeteorDrop && _meteorPhase > 0) { _meteorPhase = 0; _m.forcedVelocity = null; }
        }
        public void OnBeamEnded() { _beamFiring = false; }

        // ------------------------------------------------------------------ charged moves

        void UpdateCharge()
        {
            if (_chargingSlot < 0) { if (_orb) _orb.SetActive(false); return; }
            var m = Def.moves[_chargingSlot];
            bool sun = m.kind == MoveKind.Sphere;
            _charge = Mathf.Min(1f, _charge + Time.deltaTime / (sun ? 1.2f : Roster.BeamCharge));
            if (!_orb)
            {
                _orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(_orb.GetComponent<Collider>());
                _orb.GetComponent<MeshRenderer>().material = new Material(FxHub.Instance.glowMaterial);
            }
            _orb.SetActive(true);
            _orb.GetComponent<MeshRenderer>().material.color = new Color(Def.aura.r, Def.aura.g, Def.aura.b, 0.85f);
            float s = _m.WorldScale;
            _orb.transform.position = sun ? transform.position + Vector3.up * 5.5f * s : Origin + (_vr ? Vector3.zero : Aim.direction * 0.4f + Vector3.down * 0.1f);
            _orb.transform.localScale = Vector3.one * (sun ? 0.6f + 6.2f * _charge : 0.1f + 0.35f * _charge) * s * (1f + 0.07f * Mathf.Sin(Time.time * 35f));
            if (Time.frameCount % 4 == 0) FxHub.Instance?.Burst(_orb.transform.position, 2, Def.aura);
            GameMenus.Instance?.SetBanner($"{m.name.ToUpper()}  ·  charging {Mathf.RoundToInt(_charge * 100)}%  ·  release to fire!");
        }

        void ReleaseCharge()
        {
            int slot = _chargingSlot;
            float charge = _charge;
            CancelCharge();
            if (charge < 0.2f) { GameMenus.Instance?.Toast("Hold it a bit longer to charge!", AeroPalette.SkyDeep); return; }
            if (autoAimOnce.HasValue) { var keep = autoAim; autoAim = autoAimOnce; autoAimOnce = null; Fire(slot, charge); autoAim = keep; }
            else Fire(slot, charge);
        }

        void CancelCharge()
        {
            if (_chargingSlot >= 0) GameMenus.Instance?.SetBanner(null);
            _chargingSlot = -1; _charge = 0;
            if (_orb) _orb.SetActive(false);
        }

        void UpdateBeam()
        {
            if (!_beamFiring) return;
            if (Time.time > _beamUntil) { _beamFiring = false; GameMenus.Instance?.SetBanner(null); return; }
            if (Time.time >= _beamAimAt)
            {
                _beamAimAt = Time.time + 0.09f;
                var dir = Aim.direction;
                if (_vr && _vrRig != null) { var palms = (_vrRig.Left.t.forward + _vrRig.Right.t.forward) * 0.5f; if (palms.sqrMagnitude > 0.1f) dir = palms.normalized; }
                D.BeamAimRpc(Origin, dir);
            }
            if (!InClash) GameMenus.Instance?.SetBanner($"{Def.moves[2].name.ToUpper()}  ·  HAAAAAA!");
        }

        // ------------------------------------------------------------------ Meteor Drop (owner flies it)

        void StartMeteor() { _meteorPhase = 1; _meteorT = 0; StopFlying(); }

        void Meteor()
        {
            float s = _m.WorldScale;
            _meteorT += Time.deltaTime;
            if (_meteorPhase == 1)
            {
                _m.forcedVelocity = Vector3.up * 24f * s;
                GameMenus.Instance?.SetBanner("METEOR DROP  ·  up you go!");
                if (_meteorT > 0.9f) { _meteorPhase = 2; _meteorT = 0; }
                return;
            }
            if (_meteorPhase == 2)
            {
                _m.forcedVelocity = Vector3.zero;
                var cube = VoxelCube.Instance;
                Vector3 p = default;
                bool hit = cube != null && cube.Raycast(Aim, 120f, out _, out p);
                if (!_marker)
                {
                    _marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    Destroy(_marker.GetComponent<Collider>());
                    _marker.GetComponent<MeshRenderer>().material = new Material(FxHub.Instance.glowMaterial) { color = new Color(Def.aura.r, Def.aura.g, Def.aura.b, 0.7f) };
                    _marker.transform.localScale = new Vector3(Def.Ult.radius * 2f, 0.02f, Def.Ult.radius * 2f);
                }
                _marker.SetActive(hit);
                if (hit) { _marker.transform.position = p + Vector3.up * 0.05f; _meteorTarget = p; }
                GameMenus.Instance?.SetBanner($"METEOR DROP  ·  aim the ring  ·  punch to dive  ·  {Mathf.Max(0, 2f - _meteorT):0.0}s");
                bool go = (_light.WasPressedThisFrame() || FightInput.Light || (_vr && _vrRig != null && (_vrRig.Right.trigger.WasPressedThisFrame() || _vrRig.Left.trigger.WasPressedThisFrame()))) && _meteorT > 0.15f;
                if (go || _meteorT > 2f) { _meteorPhase = 3; _meteorT = 0; if (!hit) _meteorTarget = transform.position + Vector3.down * 25f; }
                return;
            }
            var to = _meteorTarget - transform.position;
            _m.forcedVelocity = to.normalized * 46f * s;
            GameMenus.Instance?.SetBanner("METEOR DROP!!");
            bool blocked = _meteorT > 0.1f && _m.LastFlags != CollisionFlags.None;
            if (blocked || to.magnitude < 1.2f * s || _meteorT > 2.5f)
            {
                D.MeteorImpactRpc(transform.position);
                _meteorPhase = 0;
                _m.forcedVelocity = null;
                _m.Velocity = Vector3.up * 5f * s;
                if (_marker) _marker.SetActive(false);
                GameMenus.Instance?.SetBanner(null);
            }
        }

        void OnDestroy() { if (_orb) Destroy(_orb); if (_marker) Destroy(_marker); }
    }
}
