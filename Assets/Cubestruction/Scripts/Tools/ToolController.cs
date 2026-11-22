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

        public readonly NetworkVariable<int> leftTool = new NetworkVariable<int>(-1, writePerm: NetworkVariableWritePermission.Owner);
        public readonly NetworkVariable<int> rightTool = new NetworkVariable<int>(0, writePerm: NetworkVariableWritePermission.Owner);

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
                if (t.id == "ClimbingGloves" || t.id == "RocketBoots" || t.id == "Jetpack") continue;
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
                    h.spin = t.id == "Minigun" ? Mathf.MoveTowards(h.spin, h.trigger && supported ? 1 : 0, Time.deltaTime * 2.5f) : 1;
                    bool auto = t.IsContinuous;
                    bool fire = supported && h.cooldown <= 0 && h.spin >= 1 && (auto ? h.trigger : pressed);
                    if (!fire) break;
                    var ray = Aim(h, t);
                    if (t.id == "RocketLauncher") ThrowRpc(t.index, ray.origin + ray.direction * 0.6f * scale, ray.direction * 32f);
                    else
                    {
                        RayRpc(t.index, ray.origin, ray.direction, Random.Range(1, int.MaxValue));
                        FxHub.Instance?.LocalTracer(ray.origin, RaycastWorld(ray, t.rangeM, out var p) ? p : ray.origin + ray.direction * t.rangeM, t);
                    }
                    if (t.knockback > 0 && !_avatar.IsVR) _motor.AddImpulse(-ray.direction * t.knockback * 0.15f);   // recoil
                    h.cooldown = t.cooldown;
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

                case ToolCategory.Movement:
                    if (t.id == "GrappleHook")
                    {
                        if (pressed && h.cooldown <= 0)
                        {
                            var ray = Aim(h, t);
                            if (RaycastWorld(ray, t.rangeM * scale, out var p)) { _motor.StartGrapple(p); h.lastTip = p; }
                            h.cooldown = t.cooldown;
                        }
                        if (released) _motor.StopGrapple();
                        SetLine(h, _motor.IsGrappling, HandT(h).position, h.lastTip, AeroPalette.Lime);
                    }
                    break;
            }
            if (t.cat != ToolCategory.Beam && !(t.id == "GrappleHook")) SetLine(h, false);
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
            dev?.ExecuteCommand(UnityEngine.InputSystem.XR.Haptics.SendHapticImpulseCommand.Create(0, amp, 0.05f));
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
