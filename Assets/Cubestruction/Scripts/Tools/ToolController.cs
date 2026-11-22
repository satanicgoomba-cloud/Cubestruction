using System.Collections.Generic;
using Cubestruction.Player;
using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Tools
{
    /// <summary>
    /// Per-player tool handling. The owner runs input/aiming locally and sends compact,
    /// rate-limited RPCs; the server validates and applies all voxel damage.
    ///
    /// One-handed tools can be held in either VR hand independently (dual-wield a pick and a drill!).
    /// Two-handed tools (guns, jackhammer) only fire in VR while the off-hand supports the tool.
    /// </summary>
    public class ToolController : NetworkBehaviour
    {
        public GameObject explosivePrefab;   // NetworkObject with ExplosiveProjectile
        public GameObject heliPrefab;        // NetworkObject with HeliGunship (Heli-Gunner killstreak)

        // server: an active Raptor Laptop
        int _raptorLeft; float _raptorUntil;

        public readonly NetworkVariable<int> leftTool = new NetworkVariable<int>(-1, writePerm: NetworkVariableWritePermission.Owner);
        public readonly NetworkVariable<int> rightTool = new NetworkVariable<int>(0, writePerm: NetworkVariableWritePermission.Owner);
        // rope swing, synced so everyone sees your web-line
        public readonly NetworkVariable<bool> ropeOn = new NetworkVariable<bool>(false, writePerm: NetworkVariableWritePermission.Owner);
        public readonly NetworkVariable<Vector3> ropeAnchor = new NetworkVariable<Vector3>(default, writePerm: NetworkVariableWritePermission.Owner);

        /// <summary>Swing input, written every frame by the rig (FlatRig / VRRig / mobile) on the owner.</summary>
        [System.NonSerialized] public bool swingHeld;
        [System.NonSerialized] public Ray swingAim;
        [System.NonSerialized] public Transform swingHand;
        LineRenderer _rope;
        float _swingCooldown, _anchorCheck;

        public bool HasTether { get { var g = Balance.Tool("GrappleHook"); return g != null && _avatar.Owns(g.index); } }

        PlayerAvatar _avatar;
        PlayerMotor _motor;
        readonly Hand _left = new Hand { isLeft = true }, _right = new Hand();
        readonly Dictionary<int, float> _serverLastUse = new Dictionary<int, float>();

        class Hand
        {
            public bool isLeft;
            public bool trigger, triggerPrev;
            public float cooldown;
            public float spin;                // minigun spin-up
            public GameObject visual; public int visualTool = -2;
            public LineRenderer line;
            public Vector3 lastTip; public bool tipInit;
            public bool throwArmed;
        }

        void Awake()
        {
            _avatar = GetComponent<PlayerAvatar>();
            _motor = GetComponent<PlayerMotor>();
        }

        public override void OnNetworkSpawn()
        {
            leftTool.OnValueChanged += (_, __) => RefreshVisuals();
            rightTool.OnValueChanged += (_, __) => RefreshVisuals();
            if (IsOwner)
            {
                rightTool.Value = Balance.Tool("Pickaxe").index;
                leftTool.Value = -1;
                _avatar.OnInventoryChanged += ValidateSelection;
            }
            RefreshVisuals();
        }

        // ------------------------------------------------------------------ selection (owner)

        /// <summary>Tools that can be put in a hand: owned actives + consumables with charges. Passives (gloves, boots, jetpack) are always on.</summary>
        public List<ToolDef> Hotbar()
        {
            var list = new List<ToolDef>();
            foreach (var t in Balance.Tools)
            {
                if (t.id == "ClimbingGloves" || t.id == "RocketBoots" || t.id == "Jetpack" || t.id == "GrappleHook") continue;   // passives / own button
                if (_avatar.CanUse(t)) list.Add(t);
            }
            return list;
        }

        public ToolDef Equipped(bool left) => Balance.Tool(left ? leftTool.Value : rightTool.Value);

        public void CycleHand(bool left, int dir = 1)
        {
            var bar = Hotbar();
            var v = left ? leftTool : rightTool;
            // left hand may be empty (-1) so it's free for climbing
            int pos = bar.FindIndex(t => t.index == v.Value);
            int count = bar.Count + (left ? 1 : 0);
            int next = ((pos < 0 ? (left ? bar.Count : 0) : pos) + dir + count) % count;
            v.Value = next < bar.Count ? bar[next].index : -1;
        }

        public void SelectHotbarSlot(int slot)
        {
            var bar = Hotbar();
            if (slot < bar.Count) rightTool.Value = bar[slot].index;
        }

        public void SelectTool(int toolIndex, bool left = false)
        {
            if (left) leftTool.Value = toolIndex; else rightTool.Value = toolIndex;
        }

        void ValidateSelection()
        {
            if (!IsOwner) return;
            var l = Equipped(true); var r = Equipped(false);
            if (l != null && !_avatar.CanUse(l)) leftTool.Value = -1;
            if (r == null || !_avatar.CanUse(r)) rightTool.Value = Balance.Tool("Pickaxe").index;
        }

        public void SetTriggers(bool left, bool right) { _left.trigger = left; _right.trigger = right; }

        // ------------------------------------------------------------------ per-frame (owner)

        void Update()
        {
            if (!IsSpawned || !IsOwner) return;
            if (_avatar.role.Value != PlayerRole.Miner || MatchManager.Instance == null ||
                MatchManager.Instance.state.Value != MatchState.Playing || _motor.Frozen)
            {
                _left.trigger = _right.trigger = false;
            }
            Tick(_left, Equipped(true));
            Tick(_right, Equipped(false));
            TickSwing();
            HeliView.TryBegin(_avatar);
        }

        // ------------------------------------------------------------------ rope swing (owner)

        void TickSwing()
        {
            _swingCooldown -= Time.deltaTime;
            bool canSwing = HasTether && _avatar.role.Value == PlayerRole.Miner && !_motor.Frozen &&
                            MatchManager.Instance != null && MatchManager.Instance.state.Value == MatchState.Playing;
            bool held = swingHeld && canSwing;
            if (held && !_motor.IsGrappling && _swingCooldown <= 0)
            {
                // keep trying while held: swing the aim across a wall and it catches (Spider-Man style)
                var t = Balance.Tool("GrappleHook");
                if (RaycastWorld(swingAim, t.rangeM * _motor.WorldScale, out var p))
                {
                    _motor.StartGrapple(p);
                    _anchorCheck = 0.2f;
                    if (_avatar.IsVR) Haptic(swingHand == _avatar.leftHand ? _left : _right, 0.5f);
                    FxHub.Instance?.BulletImpact(p, t, false);
                }
                _swingCooldown = 0.1f;
            }
            if (!held && _motor.IsGrappling) { _motor.StopGrapple(); _swingCooldown = 0.15f; }

            // the voxel we hang from was destroyed: drop
            if (_motor.IsGrappling && (_anchorCheck -= Time.deltaTime) <= 0)
            {
                _anchorCheck = 0.2f;
                var cube = VoxelCube.Instance;
                var a = _motor.RopeAnchor;
                bool holds = (cube && cube.SolidNear(a, cube.cellSize * 0.75f, out _)) ||
                             Physics.CheckSphere(a, 0.08f, ~(1 << 5), QueryTriggerInteraction.Ignore);
                if (!holds) _motor.StopGrapple();
            }

            if (ropeOn.Value != _motor.IsGrappling) ropeOn.Value = _motor.IsGrappling;
            if (_motor.IsGrappling && (ropeAnchor.Value - _motor.RopeAnchor).sqrMagnitude > 1e-4f) ropeAnchor.Value = _motor.RopeAnchor;
        }

        /// <summary>Everyone draws every rope (owner from its live state, others from the synced anchor).</summary>
        void LateUpdate()
        {
            if (!IsSpawned) return;
            bool on = IsOwner ? _motor.IsGrappling : ropeOn.Value;
            if (!on) { if (_rope) _rope.enabled = false; return; }
            if (!_rope)
            {
                var go = new GameObject("Rope");
                go.transform.SetParent(transform, false);
                _rope = go.AddComponent<LineRenderer>();
                _rope.material = FxHub.Instance ? FxHub.Instance.glowMaterial : null;
                _rope.positionCount = 2;
                _rope.numCapVertices = 2;
                _rope.startColor = new Color(1f, 1f, 1f, 0.95f); _rope.endColor = new Color(0.75f, 1f, 0.45f, 0.95f);
            }
            var hand = IsOwner && swingHand ? swingHand : (_avatar.IsVR ? _avatar.leftHand : _avatar.rightHand);
            _rope.enabled = true;
            _rope.widthMultiplier = 0.03f * _motor.WorldScale;
            _rope.SetPosition(0, hand ? hand.position : _motor.Chest);
            _rope.SetPosition(1, IsOwner ? _motor.RopeAnchor : ropeAnchor.Value);
        }

        Transform HandT(Hand h) => h.isLeft ? _avatar.leftHand : _avatar.rightHand;
        Transform OtherT(Hand h) => h.isLeft ? _avatar.rightHand : _avatar.leftHand;

        Ray Aim(Hand h, ToolDef t)
        {
            if (!_avatar.IsVR)
            {
                var cam = GetComponent<FlatRig>();
                if (cam && cam.Cam) return cam.AimRay;
            }
            var ht = HandT(h);
            if (t != null && t.twoHanded)
            {
                var dir = (OtherT(h).position - ht.position);
                if (dir.sqrMagnitude > 0.01f && Vector3.Dot(dir, ht.forward) > 0) return new Ray(ht.position, dir.normalized);
            }
            return new Ray(ht.position, ht.forward);
        }

        bool TwoHandSupported(Hand h)
        {
            if (!_avatar.IsVR) return true;
            float s = _motor.WorldScale;
            return Vector3.Distance(HandT(h).position, OtherT(h).position) < 0.5f * s;
        }

        void Tick(Hand h, ToolDef t)
        {
            h.cooldown -= Time.deltaTime;
            bool pressed = h.trigger && !h.triggerPrev, released = !h.trigger && h.triggerPrev;
            h.triggerPrev = h.trigger;
            if (t == null) { SetLine(h, false); return; }
            if (t.twoHanded && !h.isLeft && leftTool.Value >= 0 && _avatar.IsVR) leftTool.Value = -1;   // off-hand must be free
            float scale = _motor.WorldScale;

            switch (t.cat)
            {
                case ToolCategory.Melee:
                    if (_avatar.IsVR && !t.IsContinuous) SwingMelee(h, t);
                    else if (h.trigger && h.cooldown <= 0 && (!t.twoHanded || TwoHandSupported(h)))
                    {
                        var ray = Aim(h, t);
                        float reach = (_avatar.IsVR ? t.rangeM : t.rangeM + 1.6f) * scale;
                        if (RaycastWorld(ray, reach, out var p)) { MeleeRpc(t.index, p); Haptic(h, 0.4f); }
                        h.cooldown = t.cooldown;
                        Swing(h);
                    }
                    break;

                case ToolCategory.Beam:
                {
                    var ray = Aim(h, t);
                    bool on = h.trigger;
                    Vector3 end = ray.origin + ray.direction * t.rangeM;
                    if (on && RaycastWorld(ray, t.rangeM, out var p)) end = p;
                    SetLine(h, on, ray.origin, end, t.id == "PlasmaCutter" ? new Color(1f, 0.5f, 0.9f) : AeroPalette.Aqua);
                    if (on && h.cooldown <= 0) { RayRpc(t.index, ray.origin, ray.direction, 0); h.cooldown = Mathf.Max(t.cooldown, 0.1f); Haptic(h, 0.1f); }
                    break;
                }

                case ToolCategory.Gun:
                {
                    bool supported = !t.twoHanded || TwoHandSupported(h);
                    h.spin = t.id == "Minigun" ? Mathf.MoveTowards(h.spin, h.trigger && supported ? 1 : 0, Time.deltaTime * 4f) : 1;
                    bool auto = t.IsContinuous || t.automatic;
                    bool fire = supported && h.cooldown <= 0 && h.spin >= 1 && (auto ? h.trigger : pressed);
                    if (!fire) break;
                    var ray = Aim(h, t);
                    if (t.id == "RocketLauncher") ThrowRpc(t.index, ray.origin + ray.direction * 0.6f * scale, ray.direction * 32f);
                    else if (t.IsProjectile)
                    {
                        // physics bullets: draw ours now (and simulate damage too if we are the host), tell the server
                        var muzzle = ray.origin + ray.direction * 0.35f * scale;
                        int seed = Random.Range(1, int.MaxValue);
                        FxHub.Instance?.FireBullets(t, muzzle, ray.direction, seed, OwnerClientId, IsServer, scale);
                        FireRpc(t.index, muzzle, ray.direction, seed, scale);
                    }
                    else
                    {
                        RayRpc(t.index, ray.origin, ray.direction, Random.Range(1, int.MaxValue));
                        FxHub.Instance?.LocalTracer(ray.origin, RaycastWorld(ray, t.rangeM, out var p) ? p : ray.origin + ray.direction * t.rangeM, t);
                    }
                    if (t.selfRecoil > 0) _motor.AddImpulse(-ray.direction * t.selfRecoil * scale);   // Boost Blaster: fly
                    else if (t.knockback > 0 && !_avatar.IsVR) _motor.AddImpulse(-ray.direction * t.knockback * 0.15f);   // recoil
                    // carry the leftover so rapid fire keeps its real rate instead of snapping to whole frames
                    h.cooldown = Mathf.Max(h.cooldown, -t.cooldown) + t.cooldown;
                    Haptic(h, t.twoHanded ? 0.7f : 0.4f);
                    break;
                }

                case ToolCategory.Throwable:
                    if (_avatar.IsVR)
                    {
                        // squeeze trigger to arm, release to throw with your real hand velocity
                        if (pressed && h.cooldown <= 0) h.throwArmed = true;
                        if (released && h.throwArmed)
                        {
                            h.throwArmed = false;
                            var vr = GetComponent<VRRig>();
                            var v = (h.isLeft ? vr.Left.velocity : vr.Right.velocity) * 1.6f;
                            ThrowRpc(t.index, HandT(h).position, Vector3.ClampMagnitude(v, 25f * scale));
                            h.cooldown = t.cooldown;
                        }
                    }
                    else if (pressed && h.cooldown <= 0)
                    {
                        var ray = Aim(h, t);
                        ThrowRpc(t.index, ray.origin + ray.direction * 0.5f, ray.direction * 13f + Vector3.up * 3f + _motor.Velocity);
                        h.cooldown = t.cooldown;
                    }
                    break;

                case ToolCategory.Placed:
                    if (pressed && h.cooldown <= 0)
                    {
                        var ray = Aim(h, t);
                        if (RaycastWorld(ray, 3.5f * scale, out var p, out var n)) ThrowRpc(t.index, p + n * 0.12f, Vector3.zero);
                        else ThrowRpc(t.index, ray.origin + ray.direction * 0.8f, ray.direction * 3f);
                        h.cooldown = t.cooldown;
                    }
                    break;

                case ToolCategory.Utility:
                    if (pressed && t.id == "Detonator") { DetonateRpc(); Haptic(h, 0.6f); }
                    break;

                case ToolCategory.Streak:
                    if (pressed && h.cooldown <= 0 && !StreakState.Busy) { UseStreakRpc(t.index); h.cooldown = 1f; Haptic(h, 0.6f); }
                    break;

                case ToolCategory.Movement:
                    break;   // passives; the Zip Tether has its own swing button (TickSwing)
            }
            if (t.cat != ToolCategory.Beam) SetLine(h, false);
        }

        /// <summary>VR melee: the tool's tip must move fast through a voxel. Real swings, no button.</summary>
        void SwingMelee(Hand h, ToolDef t)
        {
            var ht = HandT(h);
            float scale = _motor.WorldScale;
            var tip = ht.position + ht.forward * t.rangeM * 0.55f * scale;
            if (!h.tipInit) { h.lastTip = tip; h.tipInit = true; }
            float speed = (tip - h.lastTip).magnitude / Mathf.Max(Time.deltaTime, 1e-4f);
            var cube = VoxelCube.Instance;
            if (cube && h.cooldown <= 0 && speed > 1.8f * scale && cube.SolidNear(tip, 0.08f * scale, out _))
            {
                MeleeRpc(t.index, tip);
                h.cooldown = t.cooldown;
                Haptic(h, Mathf.Clamp01(speed / 6f));
            }
            h.lastTip = tip;
        }

        bool RaycastWorld(Ray ray, float dist, out Vector3 point) => RaycastWorld(ray, dist, out point, out _);
        bool RaycastWorld(Ray ray, float dist, out Vector3 point, out Vector3 normal)
        {
            point = default; normal = Vector3.up;
            if (Physics.Raycast(ray, out var hit, dist, ~0, QueryTriggerInteraction.Ignore) && hit.collider.GetComponentInParent<PlayerAvatar>() != _avatar)
            {
                point = hit.point - ray.direction * 0.02f; normal = hit.normal; return true;
            }
            var cube = VoxelCube.Instance;
            if (cube && cube.Raycast(ray, dist, out _, out point)) { normal = -ray.direction; return true; }
            return false;
        }

        // ------------------------------------------------------------------ visuals

        void RefreshVisuals()
        {
            UpdateVisual(_left, leftTool.Value);
            UpdateVisual(_right, rightTool.Value);
        }

        void UpdateVisual(Hand h, int toolIndex)
        {
            if (h.visualTool == toolIndex) return;
            h.visualTool = toolIndex;
            if (h.visual) Destroy(h.visual);
            var t = Balance.Tool(toolIndex);
            if (t == null) return;
            var hand = HandT(h);
            var socket = hand.Find("Socket") ?? hand;
            h.visual = ToolVisuals.Create(t.id, socket);
        }

        void Swing(Hand h)
        {
            if (h.visual && !_avatar.IsVR) StartCoroutine(SwingAnim(h.visual.transform));
        }

        static System.Collections.IEnumerator SwingAnim(Transform v)
        {
            var start = v.localRotation;
            for (float t = 0; t < 1 && v; t += Time.deltaTime * 7f)
            {
                v.localRotation = start * Quaternion.Euler(Mathf.Sin(t * Mathf.PI) * 70f, 0, 0);
                yield return null;
            }
            if (v) v.localRotation = start;
        }

        void SetLine(Hand h, bool on, Vector3 a = default, Vector3 b = default, Color c = default)
        {
            if (!on) { if (h.line) h.line.enabled = false; return; }
            if (!h.line)
            {
                var go = new GameObject("BeamLine");
                go.transform.SetParent(transform, false);
                h.line = go.AddComponent<LineRenderer>();
                h.line.widthMultiplier = 0.04f;
                h.line.material = FxHub.Instance ? FxHub.Instance.glowMaterial : null;
            }
            h.line.enabled = true;
            h.line.positionCount = 2; h.line.SetPosition(0, a); h.line.SetPosition(1, b);
            h.line.startColor = h.line.endColor = c;
        }

        void Haptic(Hand h, float amp)
        {
            if (!_avatar.IsVR) return;
            var dev = h.isLeft ? UnityEngine.InputSystem.XR.XRController.leftHand : UnityEngine.InputSystem.XR.XRController.rightHand;
            var cmd = UnityEngine.InputSystem.XR.Haptics.SendHapticImpulseCommand.Create(0, amp, 0.05f);
            dev?.ExecuteCommand(ref cmd);
        }

        // ------------------------------------------------------------------ server RPCs (validated)

        bool ServerCheck(ToolDef t, Vector3 point, float slackM)
        {
            if (t == null || _avatar.role.Value != PlayerRole.Miner) return false;
            if (MatchManager.Instance.state.Value != MatchState.Playing) return false;
            if (!_avatar.CanUse(t)) return false;
            float now = Time.time;
            // a little leeway for jitter; two hands may share the same tool so allow 2 uses per cooldown
            if (_serverLastUse.TryGetValue(t.index, out var last) && now - last < t.cooldown * 0.45f) return false;
            _serverLastUse[t.index] = now;
            float reach = (t.rangeM + slackM) * _motor.WorldScale + 2f;
            return Vector3.Distance(point, _avatar.headAnchor.position) <= reach;
        }

        [Rpc(SendTo.Server)]
        void MeleeRpc(int tool, Vector3 point)
        {
            var t = Balance.Tool(tool);
            if (!ServerCheck(t, point, 1.6f) || t.cat != ToolCategory.Melee) return;
            VoxelCube.Instance?.ServerDamage(point, t.radiusCells, t.damage, t.tier, false, OwnerClientId);
            if (t.id == "SonicHammer") FxHub.Instance.ShockwaveRpc(point, t.radiusCells * VoxelCube.Instance.cellSize);
            FxHub.Instance.ImpactRpc(point, tool);
        }

        [Rpc(SendTo.Server)]
        void RayRpc(int tool, Vector3 origin, Vector3 dir, int seed)
        {
            var t = Balance.Tool(tool);
            if (!ServerCheck(t, origin, 0) || (t.cat != ToolCategory.Beam && t.cat != ToolCategory.Gun)) return;
            var cube = VoxelCube.Instance; if (!cube) return;
            var rng = new System.Random(seed);
            int pellets = Mathf.Max(1, t.pellets);
            for (int i = 0; i < pellets; i++)
            {
                var d = dir.normalized;
                if (pellets > 1)
                    d = Quaternion.Euler((float)(rng.NextDouble() - 0.5) * 9f, (float)(rng.NextDouble() - 0.5) * 9f, 0) * d;
                cube.ServerDamageRay(new Ray(origin, d), t.rangeM * _motor.WorldScale, t.radiusCells, t.damage, t.tier, t.pierceCells, OwnerClientId);
            }
            if (t.cat == ToolCategory.Gun) FxHub.Instance.TracerRpc(origin, origin + dir.normalized * Mathf.Min(t.rangeM, 30f), tool, OwnerClientId);
        }

        [Rpc(SendTo.Server)]
        void FireRpc(int tool, Vector3 muzzle, Vector3 dir, int seed, float scale)
        {
            var t = Balance.Tool(tool);
            if (!ServerCheck(t, muzzle, 1f) || !t.IsProjectile || float.IsNaN(dir.x)) return;
            scale = Mathf.Clamp(scale, 0.1f, 10f);
            var fx = FxHub.Instance; if (!fx) return;
            // a remote shooter's bullets: the server's copy is the one that damages (the host's own
            // volley was already simulated with authority when it fired)
            if (!IsOwner) fx.FireBullets(t, muzzle, dir, seed, OwnerClientId, true, scale);
            fx.BulletsRpc(tool, muzzle, dir, seed, OwnerClientId, scale);
        }

        // ------------------------------------------------------------------ killstreaks

        [Rpc(SendTo.Server)]
        void UseStreakRpc(int tool)
        {
            var t = Balance.Tool(tool);
            if (t == null || t.cat != ToolCategory.Streak || _avatar.role.Value != PlayerRole.Miner) return;
            if (MatchManager.Instance.state.Value != MatchState.Playing) return;
            if (t.id == "HeliGunner" && HeliGunship.Instance != null) { DenyRpc("A chopper is already in the air. Try again when it leaves."); return; }
            if (!_avatar.ServerConsumeCharge(tool)) return;
            switch (t.id)
            {
                case "RaptorLaptop":
                    _raptorLeft = Mathf.Max(1, t.pellets);
                    _raptorUntil = Time.time + t.fuse + 3f;
                    BeginRaptorRpc(_raptorLeft, t.fuse);
                    break;
                case "HeliGunner":
                    var go = Instantiate(heliPrefab);
                    go.GetComponent<HeliGunship>().ServerInit(OwnerClientId, t.fuse);
                    go.GetComponent<NetworkObject>().Spawn(true);
                    break;
            }
        }

        [Rpc(SendTo.Owner)]
        void BeginRaptorRpc(int missiles, float seconds)
        {
            if (RaptorView.Active != null) return;
            gameObject.AddComponent<RaptorView>().Begin(this, missiles, seconds);
        }

        [Rpc(SendTo.Owner)]
        void DenyRpc(string msg) => UI.GameMenus.Instance?.Toast(msg, AeroPalette.Danger);

        /// <summary>Raptor: drop a missile from the sky onto a point the laptop picked.</summary>
        [Rpc(SendTo.Server)]
        public void RaptorStrikeRpc(Vector3 target)
        {
            if (_raptorLeft <= 0 || Time.time > _raptorUntil || float.IsNaN(target.x) || float.IsInfinity(target.x)) return;
            var cube = VoxelCube.Instance;
            if (cube && Vector3.Distance(target, cube.Center) > 80f) return;
            _raptorLeft--;
            var m = Balance.Tool("RaptorMissile");
            var start = target + Vector3.up * 40f + new Vector3(Random.Range(-4f, 4f), 0, Random.Range(-4f, 4f));
            var dir = (target - start).normalized;
            var go = Instantiate(explosivePrefab, start, Quaternion.LookRotation(dir));
            go.GetComponent<ExplosiveProjectile>().ServerInit(m, OwnerClientId, dir * 55f);
            go.GetComponent<NetworkObject>().Spawn(true);
        }

        /// <summary>Heli-Gunner: the gunner fired the door gun. Same seeded-volley scheme as FireRpc.</summary>
        [Rpc(SendTo.Server)]
        public void HeliFireRpc(Vector3 muzzle, Vector3 dir, int seed)
        {
            var heli = HeliGunship.Instance;
            if (heli == null || heli.gunner.Value != OwnerClientId || float.IsNaN(dir.x)) return;
            if (Vector3.Distance(muzzle, heli.transform.position) > 8f) return;
            var g = Balance.Tool("HeliGatling");
            float now = Time.time;
            if (_serverLastUse.TryGetValue(g.index, out var last) && now - last < g.cooldown * 0.45f) return;
            _serverLastUse[g.index] = now;
            var fx = FxHub.Instance; if (!fx) return;
            if (!IsOwner) fx.FireBullets(g, muzzle, dir, seed, OwnerClientId, true, 1f);
            fx.BulletsRpc(g.index, muzzle, dir, seed, OwnerClientId, 1f);
        }

        [Rpc(SendTo.Server)]
        void ThrowRpc(int tool, Vector3 pos, Vector3 vel)
        {
            var t = Balance.Tool(tool);
            if (!ServerCheck(t, pos, 4f)) return;
            if (t.consumable && !_avatar.ServerConsumeCharge(tool)) return;
            var go = Instantiate(explosivePrefab, pos, Quaternion.LookRotation(vel.sqrMagnitude > 0.01f ? vel : transform.forward));
            var ex = go.GetComponent<ExplosiveProjectile>();
            ex.ServerInit(t, OwnerClientId, vel);
            go.GetComponent<NetworkObject>().Spawn(true);
        }

        [Rpc(SendTo.Server)]
        void DetonateRpc()
        {
            foreach (var ex in ExplosiveProjectile.Live.ToArray())
                if (ex.Owner == OwnerClientId && ex.Def != null && ex.Def.id == "C4") ex.ServerExplode();
        }
    }
}
