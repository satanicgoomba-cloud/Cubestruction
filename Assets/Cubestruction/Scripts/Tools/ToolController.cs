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
        // super modes (server-written, everyone renders): Echo Clones on, Sun Orb charge 0..1
        public readonly NetworkVariable<bool> clonesOn = new NetworkVariable<bool>(false);
        public readonly NetworkVariable<float> sunOrbSize = new NetworkVariable<float>(0f);

        /// <summary>Where the three Echo Clones stand, relative to you (right, up, forward).</summary>
        public static readonly Vector3[] CloneOffsets = { new Vector3(-1.6f, 0, -0.5f), new Vector3(1.6f, 0, -0.5f), new Vector3(0, 0.4f, -1.6f) };
        public IEnumerable<Vector3> CloneOffsetsWorld() { foreach (var o in CloneOffsets) yield return transform.rotation * (o * _motor.WorldScale); }

        // server: the personal super mode in progress
        string _superKind; float _superUntil, _superTickAt, _sunStart, _clonesUntil; int _sunVox0;
        GameObject _orbVis;

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
            public GameObject visual; public int visualTool = -2; public Vector3 tipLocal;
            public LineRenderer line;
            public Vector3 lastTip; public bool tipInit;
            public bool throwArmed;
            public bool magnetHeld, magnetPending; public GameObject magnetVis;   // Magnet Glove boulder
        }

        bool _magnetHeldServer;

        void Awake()
        {
            _avatar = GetComponent<PlayerAvatar>();
            _motor = GetComponent<PlayerMotor>();
        }

        public override void OnNetworkSpawn()
        {
            leftTool.OnValueChanged += (_, __) => RefreshVisuals();
            rightTool.OnValueChanged += (_, __) => RefreshVisuals();
            clonesOn.OnValueChanged += (_, on) => CloneEcho.Sync(this, on);
            _avatar.role.OnValueChanged += (_, __) => { _left.visualTool = _right.visualTool = -2; RefreshVisuals(); };
            CloneEcho.Sync(this, clonesOn.Value);
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
            var all = Usable();
            if (all.Count <= GameSettings.HotbarMax) return all;
            // too many for two rows: the player's pinned picks first, then the rest, 16 in all
            var pins = new HashSet<string>(GameSettings.HotbarPins.Split(','));
            var list = all.FindAll(t => pins.Contains(t.id));
            if (list.Count > GameSettings.HotbarMax) list.RemoveRange(GameSettings.HotbarMax, list.Count - GameSettings.HotbarMax);
            foreach (var t in all) { if (list.Count >= GameSettings.HotbarMax) break; if (!list.Contains(t)) list.Add(t); }
            list.Sort((a, b) => a.index.CompareTo(b.index));
            return list;
        }

        /// <summary>Every tool you could put in a hand right now (the hotbar shows up to 16 of them).</summary>
        public List<ToolDef> Usable()
        {
            var list = new List<ToolDef>();
            foreach (var t in Balance.Tools)
            {
                if (t.id == "ClimbingGloves" || t.id == "RocketBoots" || t.id == "Jetpack" || t.id == "GrappleHook") continue;   // passives / own button
                if (_avatar.CanUse(t)) list.Add(t);
            }
            return list;
        }

        /// <summary>0..1 of the cooldown left on this tool in whichever hand holds it (for the hotbar).</summary>
        public float CooldownFrac(int toolIndex, out float secondsLeft)
        {
            secondsLeft = 0; float frac = 0;
            foreach (var h in new[] { _left, _right })
            {
                if ((h.isLeft ? leftTool.Value : rightTool.Value) != toolIndex || h.cooldown <= 0) continue;
                var t = Balance.Tool(toolIndex);
                if (t == null || t.cooldown < 0.25f) continue;   // rapid-fire guns: no flicker
                secondsLeft = Mathf.Max(secondsLeft, h.cooldown);
                frac = Mathf.Max(frac, Mathf.Clamp01(h.cooldown / t.cooldown));
            }
            return frac;
        }

        public ToolDef Equipped(bool left) => Balance.Tool(left ? leftTool.Value : rightTool.Value);

        public void CycleHand(bool left, int dir = 1)
        {
            var bar = Hotbar();
            var v = left ? leftTool : rightTool;
            // left hand may be empty (-1) so it's free for climbing
            int pos = bar.FindIndex(t => t.index == v.Value);
            int count = bar.Count + (left ? 1 : 0);
            if (count == 0) return;   // nothing to hold (Titan, God...)
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
            if (r == null || !_avatar.CanUse(r)) rightTool.Value = DefaultTool();
        }

        /// <summary>What the right hand falls back to: the pick if you have it, else the sledgehammer (Titan
        /// Siege defenders), else whatever is first on your hotbar.</summary>
        int DefaultTool()
        {
            foreach (var id in new[] { "Pickaxe", "SledgeHammer" })
            {
                var t = Balance.Tool(id);
                if (t != null && _avatar.CanUse(t)) return t.index;
            }
            var bar = Hotbar();
            return bar.Count > 0 ? bar[0].index : Balance.Tool("Pickaxe").index;
        }

        public void SetTriggers(bool left, bool right) { _left.trigger = left; _right.trigger = right; }

        // ------------------------------------------------------------------ per-frame (owner)

        void Update()
        {
            if (IsSpawned && IsServer) ServerSuperTick();
            if (!IsSpawned || !IsOwner) return;
            if (!Roles.UsesTools(_avatar.role.Value) || MatchManager.Instance == null ||
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

        /// <summary>How long one web-line holds before it snaps (no hanging in the sky forever).</summary>
        public const float WebSeconds = 3f;
        /// <summary>The invisible web ceiling sits this many voxels above the top of the cube.</summary>
        public const float SkyCeilingCells = 20f;
        bool _skyAnchor, _webUsedThisHold;
        float _webAge;
        public static int SkyWebs, WebsSnapped;   // -autotest counters

        /// <summary>Height of the invisible ceiling webs stick to, or NaN when there's no cube.</summary>
        public static float SkyCeilingY
        {
            get
            {
                var cube = VoxelCube.Instance;
                if (!cube || cube.Grid == null) return float.NaN;
                return cube.ContentTopY + SkyCeilingCells * cube.cellSize;
            }
        }

        /// <summary>Spider-Man anchor: a bit ahead of you (the way you're moving, not the way you look)
        /// and high above, on the invisible ceiling, unless something solid is in the way first.</summary>
        bool SkyAnchor(out Vector3 anchor)
        {
            anchor = default;
            float ceil = SkyCeilingY;
            if (float.IsNaN(ceil)) return false;
            float s = _motor.WorldScale;
            var chest = _motor.Chest;
            float h = ceil - chest.y;
            if (h < 2.5f * s) return false;   // already up at the ceiling
            var v = _motor.Velocity; v.y = 0;
            Vector3 fwd;
            if (v.magnitude > 1.5f * s) fwd = v.normalized;
            else if (_motor.moveIntent.sqrMagnitude > 0.04f) fwd = _motor.moveIntent.normalized;
            else fwd = Vector3.ProjectOnPlane(swingAim.direction, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = transform.forward;
            float ahead = Mathf.Clamp(h * 0.35f, 1.5f * s, 7f * s);
            anchor = new Vector3(chest.x, ceil, chest.z) + fwd * ahead;
            var ray = new Ray(chest, (anchor - chest).normalized);
            if (RaycastWorld(ray, Vector3.Distance(chest, anchor), out var p)) { anchor = p; _skyAnchor = false; }
            else _skyAnchor = true;
            return true;
        }

        void TickSwing()
        {
            _swingCooldown -= Time.deltaTime;
            bool canSwing = HasTether && Roles.UsesTools(_avatar.role.Value) && !_motor.Frozen && !Siege.TitanDirector.InputLocked(_avatar) &&
                            MatchManager.Instance != null && MatchManager.Instance.state.Value == MatchState.Playing;
            bool held = swingHeld && canSwing;
            if (!held) _webUsedThisHold = false;
            // one web per press: a web that snapped needs a fresh press, so nobody hangs in the sky
            if (held && !_motor.IsGrappling && !_webUsedThisHold && _swingCooldown <= 0)
            {
                var t = Balance.Tool("GrappleHook");
                bool ok = SkyAnchor(out var p);
                if (!ok) { _skyAnchor = false; ok = RaycastWorld(swingAim, t.rangeM * _motor.WorldScale, out p); }
                if (ok)
                {
                    _motor.StartGrapple(p);
                    _anchorCheck = 0.2f;
                    _webAge = 0;
                    _webUsedThisHold = true;
                    if (_skyAnchor) SkyWebs++;
                    if (_avatar.IsVR) Haptic(swingHand == _avatar.leftHand ? _left : _right, 0.5f);
                    FxHub.Instance?.BulletImpact(p, t, false);
                }
                _swingCooldown = 0.1f;
            }
            if (!held && _motor.IsGrappling) { _motor.StopGrapple(); _swingCooldown = 0.15f; }
            if (_motor.IsGrappling && (_webAge += Time.deltaTime) >= WebSeconds)
            {
                _motor.StopGrapple();   // the web snaps; let go and press again for the next one
                _swingCooldown = 0.1f;
                WebsSnapped++;
            }

            // the voxel we hang from was destroyed: drop (sky webs hang from nothing)
            if (_motor.IsGrappling && !_skyAnchor && (_anchorCheck -= Time.deltaTime) <= 0)
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

        /// <summary>Everyone draws every rope (owner from its live state, others from the synced anchor),
        /// and every charging Sun Orb.</summary>
        void LateUpdate()
        {
            if (!IsSpawned) return;
            float orb = sunOrbSize.Value;
            if (orb > 0)
            {
                if (!_orbVis)
                {
                    _orbVis = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(_orbVis.GetComponent<Collider>());
                    _orbVis.GetComponent<MeshRenderer>().material = new Material(FxHub.Instance.glowMaterial) { color = new Color(1f, 0.85f, 0.3f, 0.85f) };
                }
                float s = _motor.WorldScale;
                _orbVis.transform.position = transform.position + Vector3.up * (2.2f + orb * 1.5f) * s;
                _orbVis.transform.localScale = Vector3.one * (0.4f + 1.6f * orb) * 2f * s * (1f + 0.04f * Mathf.Sin(Time.time * 12f));
            }
            else if (_orbVis) Destroy(_orbVis);
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
                    SetLine(h, on, ray.origin, end, t.id == "PlasmaCutter" ? new Color(1f, 0.5f, 0.9f) : t.freezes ? new Color(0.75f, 0.95f, 1f) : AeroPalette.Aqua);
                    if (on && h.cooldown <= 0) { RayRpc(t.index, ray.origin, ray.direction, 0); h.cooldown = Mathf.Max(t.cooldown, 0.1f); Haptic(h, 0.1f); }
                    break;
                }

                case ToolCategory.Gun when t.id == "ChainWhip":
                    if (pressed && h.cooldown <= 0) Whip(h, t, scale);
                    break;

                case ToolCategory.Gun when t.id == "MagnetGlove":
                    Magnet(h, t, pressed, released, scale);
                    break;

                case ToolCategory.Gun when t.warpOnHit && pressed && FxHub.Instance && FxHub.Instance.TakeKunai(OwnerClientId, out var warpTo):
                    // Warp Kunai: press again while your kunai is out (flying or stuck) to blink to it
                    _motor.Teleport(warpTo, transform.eulerAngles.y);
                    KunaiGoneRpc();
                    FxHub.Instance.LocalBlast(warpTo + Vector3.up * 0.5f * scale, 0.5f * scale, Bullets.BulletColor(t));
                    Bullets.Warps++; Bullets.LastWarp = warpTo;
                    h.cooldown = 0.25f;
                    Haptic(h, 0.8f);
                    break;

                case ToolCategory.Gun when _avatar.IsVR && VrThrowable(t):
                    VrThrowBullet(h, t, pressed, released, scale);
                    break;

                case ToolCategory.Gun:
                {
                    bool supported = !t.twoHanded || TwoHandSupported(h);
                    h.spin = t.id == "Minigun" ? Mathf.MoveTowards(h.spin, h.trigger && supported ? 1 : 0, Time.deltaTime * 4f) : 1;
                    bool auto = t.IsContinuous || t.automatic;
                    bool fire = supported && h.cooldown <= 0 && h.spin >= 1 && (auto ? h.trigger : pressed);
                    if (!fire) break;
                    var ray = Aim(h, t);
                    if (t.id == "RocketLauncher" && _avatar.role.Value == PlayerRole.Defender && _avatar.ammoRockets.Value <= 0)
                    {
                        UI.GameMenus.Instance?.Toast("No rockets! Dig the mine wall behind you with the sledgehammer.", AeroPalette.Danger);
                        h.cooldown = 0.6f;
                        break;
                    }
                    if (t.id == "RocketLauncher") { var m = Muzzle(h, ray, scale, out var rd); ThrowRpc(t.index, m, rd * 32f); }
                    else if (t.IsProjectile)
                    {
                        // physics bullets leave the end of the barrel, aimed at whatever the crosshair is on;
                        // draw ours now (and simulate damage too if we are the host), tell the server
                        var muzzle = Muzzle(h, ray, scale, out var dir);
                        int seed = Random.Range(1, int.MaxValue);
                        FxHub.Instance?.FireBullets(t, muzzle, dir, seed, OwnerClientId, IsServer, scale);
                        FireRpc(t.index, muzzle, dir, seed, scale);
                    }
                    else
                    {
                        RayRpc(t.index, ray.origin, ray.direction, Random.Range(1, int.MaxValue));
                        if (t.chainJumps <= 0)   // the zapper's bolt comes back from the server
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

        /// <summary>The end of the held tool's barrel (from its model bounds), and the direction from there to
        /// what the aim ray is pointing at, so flat players' shots leave the gun and still hit the crosshair.</summary>
        Vector3 Muzzle(Hand h, Ray aim, float scale, out Vector3 dir)
        {
            dir = aim.direction.normalized;
            var tip = h.visual ? h.visual.transform.TransformPoint(h.tipLocal) : aim.origin + dir * 0.35f * scale;
            if (!_avatar.IsVR)
            {
                var target = RaycastWorld(aim, 250f * scale, out var p) ? p : aim.GetPoint(250f * scale);
                var d = target - tip;
                // very close targets (the barrel is past them): keep the camera direction
                if (d.sqrMagnitude > 0.09f * scale * scale && Vector3.Dot(d.normalized, dir) > 0.6f) dir = d.normalized;
            }
            LastMuzzle = tip;
            return tip;
        }
        public static Vector3 LastMuzzle;   // -autotest

        /// <summary>Barrel tip in the visual's local space: the far end of its bounds along +Z (tools point +Z).</summary>
        static Vector3 TipLocal(GameObject v)
        {
            var root = v.transform;
            var min = Vector3.one * float.MaxValue; var max = Vector3.one * float.MinValue;
            bool any = false;
            foreach (var mf in v.GetComponentsInChildren<MeshFilter>())
            {
                if (!mf.sharedMesh) continue;
                var b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = root.InverseTransformPoint(mf.transform.TransformPoint(c));
                    min = Vector3.Min(min, p); max = Vector3.Max(max, p); any = true;
                }
            }
            if (!any) return new Vector3(0, 0, 0.3f);
            return new Vector3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, max.z);
        }

        // ------------------------------------------------------------------ VR throws for kunai / yo-yo / disc

        /// <summary>Things that look throwable get thrown for real in VR: squeeze, swing, let go.</summary>
        static bool VrThrowable(ToolDef t) => t.id == "WarpKunai" || t.id == "SawYoyo" || t.id == "RippleDisc";
        public static int VrThrows;   // -autotest

        void VrThrowBullet(Hand h, ToolDef t, bool pressed, bool released, float scale)
        {
            if (pressed && h.cooldown <= 0) h.throwArmed = true;
            if (!released || !h.throwArmed) return;
            h.throwArmed = false;
            var vr = GetComponent<VRRig>();
            var v = vr ? (h.isLeft ? vr.Left.velocity : vr.Right.velocity) : Vector3.zero;
            if (!VrThrowFrom(h, t, v, scale)) return;
            h.cooldown = t.cooldown;
            Haptic(h, 0.6f);
        }

        /// <summary>Fire a projectile tool as a throw: direction from the hand's velocity, speed from how
        /// hard you threw (35%..125% of the tool's normal speed). A limp release just drops nothing.</summary>
        bool VrThrowFrom(Hand h, ToolDef t, Vector3 handVel, float scale)
        {
            float speed = handVel.magnitude * 1.6f;
            if (speed < 1.5f * scale) return false;
            float mul = Mathf.Clamp(speed / Mathf.Max(1f, t.speed * scale), 0.35f, 1.25f);
            var from = HandT(h).position;
            var dir = handVel.normalized * mul;   // Bullets reads the length as a speed multiplier
            int seed = Random.Range(1, int.MaxValue);
            FxHub.Instance?.FireBullets(t, from, dir, seed, OwnerClientId, IsServer, scale);
            FireRpc(t.index, from, dir, seed, scale);
            VrThrows++;
            return true;
        }

        /// <summary>-autotest: a VR-style throw from the right hand.</summary>
        public bool TestVrThrow(string id, Vector3 handVel) => VrThrowFrom(_right, Balance.Tool(id), handVel, _motor.WorldScale);

        [Rpc(SendTo.NotMe)]
        void KunaiGoneRpc() => FxHub.Instance?.TakeKunai(OwnerClientId, out _);

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
            if (!Roles.UsesTools(_avatar.role.Value)) toolIndex = -1;   // giants and commanders hold no tools
            if (h.visualTool == toolIndex) return;
            h.visualTool = toolIndex;
            if (h.visual) Destroy(h.visual);
            var t = Balance.Tool(toolIndex);
            if (t == null) return;
            var hand = HandT(h);
            var socket = hand.Find("Socket") ?? hand;
            h.visual = ToolVisuals.Create(t.id, socket);
            h.tipLocal = TipLocal(h.visual);
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
            if (t == null || !Roles.UsesTools(_avatar.role.Value)) return false;
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
            if (clonesOn.Value) foreach (var off in CloneOffsetsWorld()) VoxelCube.Instance?.ServerDamage(point + off, t.radiusCells, t.damage, t.tier, false, OwnerClientId);
            if (t.id == "SonicHammer") FxHub.Instance.ShockwaveRpc(point, t.radiusCells * VoxelCube.Instance.cellSize);
            FxHub.Instance.ImpactRpc(point, tool);
        }

        [Rpc(SendTo.Server)]
        void RayRpc(int tool, Vector3 origin, Vector3 dir, int seed)
        {
            var t = Balance.Tool(tool);
            if (!ServerCheck(t, origin, 0) || (t.cat != ToolCategory.Beam && t.cat != ToolCategory.Gun)) return;
            ServerRay(t, origin, dir, seed);
            if (clonesOn.Value) { int k = 1; foreach (var off in CloneOffsetsWorld()) ServerRay(t, origin + off, dir, seed + k++); }
        }

        void ServerRay(ToolDef t, Vector3 origin, Vector3 dir, int seed)
        {
            int tool = t.index;
            var cube = VoxelCube.Instance; if (!cube) return;
            float reachM = t.rangeM * _motor.WorldScale;
            if (t.freezes) { cube.ServerFreezeRay(new Ray(origin, dir), reachM, t.radiusCells, t.tier); return; }
            if (t.chainJumps > 0)
            {
                var path = new List<Vector3> { origin };
                cube.ServerChain(new Ray(origin, dir), reachM, t, OwnerClientId, path);
                if (path.Count > 1) FxHub.Instance.LightningRpc(path.ToArray());
                return;
            }
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
            if (clonesOn.Value)
            {
                // Echo Clones fire the same volley from their spots (everyone sees these, the shooter too)
                int k = 1;
                foreach (var off in CloneOffsetsWorld())
                {
                    fx.FireBullets(t, muzzle + off, dir, seed + k, OwnerClientId, true, scale);
                    fx.BulletsRpc(tool, muzzle + off, dir, seed + k, OwnerClientId, scale, true);
                    k++;
                }
            }
        }

        // ------------------------------------------------------------------ Chain Whip / Magnet Glove (owner)

        /// <summary>Chain Whip: lash a point up to rangeM away; the server rips a chunk out there and you
        /// get yanked toward it (selfRecoil = pull speed).</summary>
        void Whip(Hand h, ToolDef t, float scale)
        {
            h.cooldown = t.cooldown;
            var ray = Aim(h, t);
            var from = HandT(h).position;
            if (!RaycastWorld(ray, t.rangeM * scale, out var p))
            {
                FxHub.Instance?.Whip(from, ray.origin + ray.direction * t.rangeM * scale, false);
                return;
            }
            FxHub.Instance?.Whip(from, p, true);
            WhipRpc(t.index, p, from);
            var pull = (p - _motor.Chest).normalized * t.selfRecoil * scale;
            _motor.AddImpulse(pull + Vector3.up * 2f * scale);
            Haptic(h, 0.8f);
            Swing(h);
        }

        /// <summary>Magnet Glove: press to rip a chunk out (server), hold to float it, release to throw it.</summary>
        void Magnet(Hand h, ToolDef t, bool pressed, bool released, float scale)
        {
            if (pressed && !h.magnetHeld && !h.magnetPending && h.cooldown <= 0)
            {
                var ray = Aim(h, t);
                if (RaycastWorld(ray, t.rangeM * scale, out var p)) { h.magnetPending = true; MagnetGrabRpc(t.index, p, h.isLeft); }
                h.cooldown = 0.3f;
            }
            if (h.magnetHeld)
            {
                if (!h.magnetVis)
                {
                    h.magnetVis = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(h.magnetVis.GetComponent<Collider>());
                    h.magnetVis.GetComponent<MeshRenderer>().material.color = Bullets.BulletColor(Balance.Tool("MagnetBoulder"));
                }
                var ht = HandT(h);
                h.magnetVis.transform.position = ht.position + ht.forward * 0.6f * scale + Vector3.up * Mathf.Sin(Time.time * 5f) * 0.03f;
                h.magnetVis.transform.localScale = Vector3.one * 0.6f * scale;
                h.magnetVis.transform.Rotate(30f * Time.deltaTime, 50f * Time.deltaTime, 0);
                if (released || !h.trigger)
                {
                    var ray = Aim(h, t);
                    var muzzle = h.magnetVis.transform.position;
                    var aimPoint = RaycastWorld(ray, 80f, out var hp) ? hp : ray.origin + ray.direction * 80f;
                    var dir = (aimPoint - muzzle).normalized;
                    if (_avatar.IsVR)
                    {
                        // VR: throw the boulder with your arm (a gentle let-go still lobs it where you point)
                        var vr = GetComponent<VRRig>();
                        var hv = vr ? (h.isLeft ? vr.Left.velocity : vr.Right.velocity) : Vector3.zero;
                        if (hv.magnitude > 2f * scale)
                            dir = hv.normalized * Mathf.Clamp(hv.magnitude * 1.6f / (Balance.Tool("MagnetBoulder").speed * scale), 0.35f, 1.25f);
                    }
                    int seed = Random.Range(1, int.MaxValue);
                    FxHub.Instance?.FireBullets(Balance.Tool("MagnetBoulder"), muzzle, dir, seed, OwnerClientId, IsServer, scale);
                    MagnetThrowRpc(muzzle, dir, seed, scale);
                    Destroy(h.magnetVis);
                    h.magnetHeld = false;
                    h.cooldown = t.cooldown;
                    Haptic(h, 0.9f);
                }
            }
        }

        [Rpc(SendTo.Server)]
        void WhipRpc(int tool, Vector3 point, Vector3 from)
        {
            var t = Balance.Tool(tool);
            if (!ServerCheck(t, point, 1.5f) || t.id != "ChainWhip") return;
            VoxelCube.Instance?.ServerDamage(point, t.radiusCells, t.damage, t.tier, false, OwnerClientId);
            FxHub.Instance?.WhipRpc(from, point, OwnerClientId);
        }

        [Rpc(SendTo.Server)]
        void MagnetGrabRpc(int tool, Vector3 point, bool left)
        {
            var t = Balance.Tool(tool);
            bool ok = false;
            if (ServerCheck(t, point, 1.5f) && t.id == "MagnetGlove" && !_magnetHeldServer)
            {
                var cube = VoxelCube.Instance;
                int before = cube ? cube.alive.Value : 0;
                cube?.ServerDamage(point, t.radiusCells, t.damage, t.tier, false, OwnerClientId);
                ok = cube && cube.alive.Value < before;   // ripped something out
                _magnetHeldServer = ok;
                if (ok) FxHub.Instance?.ShockwaveRpc(point, 0.6f);
            }
            MagnetGrabbedRpc(ok, left);
        }

        [Rpc(SendTo.Owner)]
        void MagnetGrabbedRpc(bool ok, bool left)
        {
            var h = left ? _left : _right;
            h.magnetPending = false;
            h.magnetHeld = ok;
        }

        [Rpc(SendTo.Server)]
        void MagnetThrowRpc(Vector3 muzzle, Vector3 dir, int seed, float scale)
        {
            if (!_magnetHeldServer || float.IsNaN(dir.x)) return;
            _magnetHeldServer = false;
            if (Vector3.Distance(muzzle, _avatar.headAnchor.position) > 4f * Mathf.Max(1f, scale)) return;
            var b = Balance.Tool("MagnetBoulder");
            var fx = FxHub.Instance; if (!fx) return;
            scale = Mathf.Clamp(scale, 0.1f, 10f);
            if (!IsOwner) fx.FireBullets(b, muzzle, dir, seed, OwnerClientId, true, scale);
            fx.BulletsRpc(b.index, muzzle, dir, seed, OwnerClientId, scale);
        }

        // ------------------------------------------------------------------ killstreaks

        [Rpc(SendTo.Server)]
        void UseStreakRpc(int tool)
        {
            var t = Balance.Tool(tool);
            if (t == null || t.cat != ToolCategory.Streak || !Roles.CanShop(_avatar.role.Value)) return;
            if (MatchManager.Instance.state.Value != MatchState.Playing) return;
            if (t.id == "HeliGunner" && HeliGunship.Instance != null) { DenyRpc("A chopper is already in the air. Try again when it leaves."); return; }
            bool personal = t.id == "AquaSurge" || t.id == "MeteorDive" || t.id == "SunOrb" || t.id == "TitanBuddy";
            if (personal && SuperActive) { DenyRpc("Finish your current super first."); return; }
            if (t.id == "EchoClones" && clonesOn.Value) { DenyRpc("Your clones are already out."); return; }
            if (!_avatar.ServerConsumeCharge(tool)) return;
            switch (t.id)
            {
                case "RaptorLaptop":
                    _raptorLeft = Mathf.Max(1, t.pellets);
                    _raptorUntil = Time.time + t.fuse + 3f;
                    BeginRaptorRpc(_raptorLeft, t.fuse);
                    break;
                case "AquaSurge": StartSuper(t.id, 25f); break;
                case "MeteorDive": StartSuper(t.id, 15f); break;
                case "TitanBuddy": StartSuper(t.id, 31f); break;
                case "SunOrb":
                    StartSuper(t.id, 20f);
                    _sunStart = Time.time; _sunVox0 = VoxelCube.TotalDestroyed; sunOrbSize.Value = 0.05f;
                    break;
                case "EchoClones":
                    clonesOn.Value = true; _clonesUntil = Time.time + 20f;
                    break;
                case "HeliGunner":
                    var go = Instantiate(heliPrefab);
                    go.GetComponent<HeliGunship>().ServerInit(OwnerClientId, t.fuse);
                    go.GetComponent<NetworkObject>().Spawn(true);
                    break;
            }
        }

        // ------------------------------------------------------------------ super modes (server)

        bool SuperActive => _superKind != null && Time.time < _superUntil;
        bool Super(string kind) => _superKind == kind && Time.time < _superUntil;

        void StartSuper(string kind, float seconds)
        {
            _superKind = kind; _superUntil = Time.time + seconds;
            BeginSuperRpc(kind);
        }

        void ServerSuperTick()
        {
            if (clonesOn.Value && Time.time > _clonesUntil) clonesOn.Value = false;
            if (_superKind == "SunOrb" && Time.time < _superUntil && Time.time >= _superTickAt)
            {
                // grows with time and with every voxel anyone breaks while it charges (teamwork!)
                _superTickAt = Time.time + 0.2f;
                float time01 = Mathf.Clamp01((Time.time - _sunStart) / 8f);
                float team01 = Mathf.Clamp01((VoxelCube.TotalDestroyed - _sunVox0) / 1500f);
                sunOrbSize.Value = Mathf.Clamp01(0.05f + time01 * 0.6f + team01 * 0.4f);
            }
            if (_superKind != null && Time.time >= _superUntil) { _superKind = null; sunOrbSize.Value = 0; }
        }

        bool NearMe(Vector3 p, float m) => Vector3.Distance(p, _avatar.headAnchor.position) <= m * Mathf.Max(1f, _motor.WorldScale);

        [Rpc(SendTo.Owner)]
        void BeginSuperRpc(string kind) => SuperMode.Begin(this, kind);

        /// <summary>Aqua Surge: one tick of the beam (owner sends ~10/s while firing).</summary>
        [Rpc(SendTo.Server)]
        public void SurgeTickRpc(Vector3 origin, Vector3 dir, float charge)
        {
            if (!Super("AquaSurge") || Time.time < _superTickAt || float.IsNaN(dir.x) || !NearMe(origin, 3f)) return;
            _superTickAt = Time.time + 0.08f;
            charge = Mathf.Clamp01(charge);
            float radiusCells = 1.5f + 2.5f * charge;
            var cube = VoxelCube.Instance;
            cube?.ServerCarveBeam(origin, dir.normalized, 30f, radiusCells, 250f, 5, OwnerClientId);
            FxHub.Instance?.SurgeBeamRpc(origin, dir.normalized, radiusCells * (cube ? cube.cellSize : 0.25f));
        }

        [Rpc(SendTo.Server)]
        public void SuperEndRpc() { _superKind = null; sunOrbSize.Value = 0; }

        /// <summary>Meteor Dive: the owner hit the ground; crater + shockwave (you're immune to your own).</summary>
        [Rpc(SendTo.Server)]
        public void MeteorImpactRpc(Vector3 pos)
        {
            if (!Super("MeteorDive") || !NearMe(pos, 6f)) return;
            _superKind = null;
            var cube = VoxelCube.Instance;
            cube?.ServerDamage(pos, 7f, 500f, 5, true, OwnerClientId);
            ServerBlastOthers(pos, 9f, 14f);
            FxHub.Instance?.ExplosionRpc(pos, 7f * (cube ? cube.cellSize : 0.25f), Balance.Tool("RocketLauncher").index, false);
            FxHub.Instance?.ShockwaveRpc(pos, 4f);
        }

        /// <summary>Sun Orb: throw the charged orb; its size sets the blast.</summary>
        [Rpc(SendTo.Server)]
        public void SunOrbThrowRpc(Vector3 origin, Vector3 dir)
        {
            if (!Super("SunOrb") || float.IsNaN(dir.x) || !NearMe(origin, 6f)) return;
            float size = Mathf.Max(0.05f, sunOrbSize.Value);
            _superKind = null; sunOrbSize.Value = 0;
            var orb = Balance.Tool("SunOrbBlast").Clone();
            orb.radiusCells = 4f + 9f * size;   // full charge: a 13-voxel-radius crater
            float vis = 0.4f + 1.6f * size;
            var fx = FxHub.Instance; if (!fx) return;
            fx.FireBullets(orb, origin, dir.normalized, 1, OwnerClientId, true, 1f, vis);
            fx.BulletsRpc(orb.index, origin, dir.normalized, 1, OwnerClientId, 1f, true, vis);
        }

        /// <summary>Titan Buddy: a giant punch.</summary>
        [Rpc(SendTo.Server)]
        public void TitanPunchRpc(Vector3 point)
        {
            if (!Super("TitanBuddy") || Time.time < _superTickAt || !NearMe(point, 2.2f)) return;
            _superTickAt = Time.time + 0.22f;
            VoxelCube.Instance?.ServerDamage(point, 3.5f, 400f, 5, false, OwnerClientId);
            FxHub.Instance?.ShockwaveRpc(point, 1.6f);
            FxHub.Instance?.ImpactRpc(point, -1);
        }

        /// <summary>Titan Buddy: landing from a jump cracks the ground around you.</summary>
        [Rpc(SendTo.Server)]
        public void TitanStompRpc(Vector3 feet)
        {
            if (!Super("TitanBuddy") || !NearMe(feet, 2f)) return;
            VoxelCube.Instance?.ServerDamage(feet, 4.5f, 300f, 5, true, OwnerClientId);
            ServerBlastOthers(feet, 8f, 10f);
            FxHub.Instance?.ShockwaveRpc(feet, 3f);
        }

        void ServerBlastOthers(Vector3 pos, float radius, float knock)
        {
            foreach (var pl in PlayerAvatar.All)
            {
                if (pl == _avatar || Roles.IsGiant(pl.role.Value)) continue;
                var d = pl.transform.position + Vector3.up * 0.5f - pos;
                if (d.magnitude > radius) continue;
                pl.KnockbackRpc((d.normalized + Vector3.up * 0.6f).normalized * knock * (1f - d.magnitude / radius));
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
            // Titan Siege: the RPG fires rockets you dug out of the mine wall
            if (t.id == "RocketLauncher" && _avatar.role.Value == PlayerRole.Defender)
            {
                if (_avatar.ammoRockets.Value <= 0) return;
                _avatar.ammoRockets.Value--;
            }
            SpawnExplosive(t, pos, vel);
            if (clonesOn.Value && t.cat == ToolCategory.Throwable) foreach (var off in CloneOffsetsWorld()) SpawnExplosive(t, pos + off, vel);
        }

        void SpawnExplosive(ToolDef t, Vector3 pos, Vector3 vel)
        {
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
