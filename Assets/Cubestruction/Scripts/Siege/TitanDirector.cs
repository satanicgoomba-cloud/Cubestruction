using System.Collections;
using System.Collections.Generic;
using Cubestruction.Player;
using Cubestruction.Tools;
using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Siege
{
    /// <summary>
    /// Titan Siege, server-authoritative. One giant Titan (the VR player) must smash through three walls,
    /// weakest first, running through a voxel town between them. Everyone else defends from the top of the
    /// current wall with cannons, artillery and rocket turrets (standing next to one loads it; Use fires it),
    /// plus their own RPG, web-swing and a short jetpack. A breached wall sends the defenders to the next one.
    /// Defenders win by bringing the Titan down; the Titan wins by breaking the third wall.
    /// </summary>
    public class TitanDirector : NetworkBehaviour
    {
        public static TitanDirector Instance { get; private set; }

        public GameObject explosivePrefab, titanBotPrefab, defenderBotPrefab;
        /// <summary>Lit material for the turret models (tinted per part).</summary>
        public Material partMaterial;
        /// <summary>Titan Siege is always at least this many defenders against one Titan: bots fill the gaps.</summary>
        public const int MinDefenders = 5;
        public const ulong AiTitanClient = ulong.MaxValue - 3;
        readonly List<DefenderBot> _bots = new List<DefenderBot>();
        TitanBot _aiTitan;

        // ---- tuning
        public const float Scale = 7f;                 // the Titan's world scale (~6.5 m tall)
        public const float PrepSeconds = 40f, MatchSeconds = 600f;
        public const float BaseHp = 3000f, HpPerDefender = 1200f;
        public const float RpgDamage = 8f;
        public const float ScrapeRadius = 1.3f, ScrapeDamage = 20f;
        public const float PunchMinSpeed = 2.0f;       // real m/s of the hand (world speed / scale)
        public const float PunchDamage = 500f, KickDamage = 700f;
        /// <summary>Instigator id for the Titan's voxel damage (the only damage walls take in this mode).</summary>
        public const ulong TitanInstigator = ulong.MaxValue - 2;

        public enum TurretKind : byte { Cannon = 0, Artillery = 1, Rocket = 2 }
        public struct TurretDef { public TurretKind kind; public float x; }
        public static readonly TurretDef[] Layout =
        {
            new TurretDef { kind = TurretKind.Cannon, x = -16f }, new TurretDef { kind = TurretKind.Artillery, x = -6f },
            new TurretDef { kind = TurretKind.Rocket, x = 6f },   new TurretDef { kind = TurretKind.Cannon, x = 16f },
        };
        public static int Capacity(TurretKind k) => k == TurretKind.Cannon ? 6 : k == TurretKind.Artillery ? 3 : 4;
        public static float LoadSeconds(TurretKind k) => k == TurretKind.Cannon ? 3f : k == TurretKind.Artillery ? 6f : 4f;
        public static float ShotDamage(TurretKind k) => k == TurretKind.Cannon ? 25f : k == TurretKind.Artillery ? 80f : 40f;
        public static float FireCooldown(TurretKind k) => k == TurretKind.Cannon ? 0.7f : k == TurretKind.Artillery ? 1.5f : 1.0f;
        public static string Name(TurretKind k) => k == TurretKind.Cannon ? "Cannon" : k == TurretKind.Artillery ? "Artillery" : "Rocket Pod";
        public const float TurretReach = 1.9f;

        // ---- synced state
        public readonly NetworkVariable<bool> active = new NetworkVariable<bool>();
        public readonly NetworkVariable<bool> prep = new NetworkVariable<bool>();
        public readonly NetworkVariable<float> prepLeft = new NetworkVariable<float>();
        public readonly NetworkVariable<float> timeLeft = new NetworkVariable<float>();
        public readonly NetworkVariable<float> titanHp = new NetworkVariable<float>();
        public readonly NetworkVariable<float> titanMaxHp = new NetworkVariable<float>(1f);
        public readonly NetworkVariable<int> wall = new NetworkVariable<int>();          // the wall being attacked (0..2)
        public readonly NetworkVariable<ulong> titanClient = new NetworkVariable<ulong>(ulong.MaxValue);
        /// <summary>Ammo per turret (wall * 4 + slot); -1 = destroyed.</summary>
        public NetworkList<int> ammo;

        public bool IsActive => active.Value;
        public int TurretCount => TitanMap.Walls.Length * Layout.Length;
        public PlayerAvatar Titan => PlayerAvatar.ByClient.TryGetValue(titanClient.Value, out var t) ? t : null;
        TitanBody _playerBody;
        /// <summary>The Titan's body, whether a player or the AI Titan (TitanBot).</summary>
        public TitanBody Body
        {
            get
            {
                if (TitanBot.Instance && TitanBot.Instance.IsSpawned) return TitanBot.Instance.Body;
                var p = Titan;
                if (!p) return null;
                if (_playerBody == null || _playerBody.player != p) _playerBody = new TitanBody(p);
                return _playerBody;
            }
        }

        VoxelCube _cube;
        readonly float[] _load = new float[12], _fireReady = new float[12];
        float _supportCheck;
        readonly float[] _limbReady = new float[3], _punchReady = new float[3];
        readonly Vector3[] _lastHand = new Vector3[2];
        Vector3 _handVelL, _handVelR;
        float _kickReady;
        GameObject _blockers;
        readonly List<GameObject> _turretVis = new List<GameObject>();

        // -autotest counters
        public static int Punches, Scrapes, Kicks, ShotsFired, ShotsHit, Deflects, Breaches;

        void Awake() { Instance = this; ammo = new NetworkList<int>(); }

        public override void OnNetworkSpawn()
        {
            if (IsServer && ammo.Count == 0) for (int i = 0; i < 12; i++) ammo.Add(0);
            active.OnValueChanged += (_, on) => SyncWorld(on);
            ammo.OnListChanged += _ => RefreshTurretVisuals();
            SyncWorld(active.Value);
        }

        /// <summary>True while the Titan must wait out the defenders' prep time.</summary>
        public static bool InputLocked(PlayerAvatar who) =>
            Instance != null && Instance.active.Value && Instance.prep.Value && who != null && who.role.Value == PlayerRole.Titan;

        public Vector3 Origin => _cube ? _cube.transform.position : (VoxelCube.Instance ? VoxelCube.Instance.transform.position : Vector3.zero);

        public Vector3 TurretPos(int i)
        {
            int w = i / Layout.Length, s = i % Layout.Length;
            return TitanMap.WallTop(Origin, w, Layout[s].x);
        }
        public TurretKind KindOf(int i) => Layout[i % Layout.Length].kind;

        /// <summary>The live turret nearest <paramref name="pos"/> within reach on the current wall, or -1.</summary>
        public int TurretNear(Vector3 pos)
        {
            int best = -1; float bd = TurretReach;
            for (int i = 0; i < TurretCount && i < ammo.Count; i++)
            {
                if (ammo[i] < 0) continue;
                var p = TurretPos(i);
                var d = pos - p;
                if (Mathf.Abs(d.y) > 2.2f) continue;
                float h = new Vector2(d.x, d.z).magnitude;
                if (h < bd) { bd = h; best = i; }
            }
            return best;
        }

        // ------------------------------------------------------------------ lifecycle (server)

        public void ServerBegin(VoxelCube cube, List<PlayerAvatar> players, PlayerAvatar titan)
        {
            if (!IsServer) return;
            _cube = cube;
            cube.InstigatorFilter = id => id == TitanInstigator;   // only the Titan breaks walls
            int defenders = 0;
            foreach (var p in players) if (p != titan) defenders++;
            int botCount = Mathf.Max(0, MinDefenders - defenders);
            titanClient.Value = titan ? titan.OwnerClientId : AiTitanClient;
            // bots are a bit weaker than people, so they add a bit less Titan health
            titanMaxHp.Value = BaseHp + HpPerDefender * (defenders + botCount * 0.6f);
            titanHp.Value = titanMaxHp.Value;
            wall.Value = 0;
            prep.Value = true;
            prepLeft.Value = PrepSeconds;
            timeLeft.Value = MatchSeconds;
            for (int i = 0; i < 12; i++) { ammo[i] = 0; _load[i] = 0; _fireReady[i] = 0; }
            _shots.Clear();
            PlayerMotor.RespawnOverride = DefenderRespawn;
            active.Value = true;

            ServerDespawnBots();
            if (!titan && titanBotPrefab)
            {
                // no VR player: an AI Titan comes for you
                var tb = Instantiate(titanBotPrefab, TitanMap.TitanStart(Origin), Quaternion.identity);
                tb.GetComponent<NetworkObject>().Spawn(true);
                _aiTitan = tb.GetComponent<TitanBot>();
            }
            for (int b = 0; b < botCount && defenderBotPrefab; b++)
            {
                var go = Instantiate(defenderBotPrefab, TitanMap.WallTop(Origin, 0, Mathf.Lerp(-14f, 14f, (b + 0.5f) / botCount)) + Vector3.up * 0.1f, Quaternion.Euler(0, 180, 0));
                var bot = go.GetComponent<DefenderBot>();
                bot.ServerInit(b);
                go.GetComponent<NetworkObject>().Spawn(true);
                _bots.Add(bot);
            }

            // loadouts + starting spots
            var g = Balance.Tool("GrappleHook"); var j = Balance.Tool("Jetpack"); var r = Balance.Tool("RocketLauncher");
            int k = 0;
            foreach (var p in players)
            {
                if (p == titan)
                {
                    p.role.Value = PlayerRole.Titan;
                    p.ownedTools.Value = 0;
                    p.TeleportRpc(TitanMap.TitanStart(Origin), 0f);
                    continue;
                }
                p.role.Value = PlayerRole.Defender;
                p.ownedTools.Value = (1UL << g.index) | (1UL << j.index) | (1UL << r.index);
                float x = defenders <= 1 ? 0f : Mathf.Lerp(-12f, 12f, k / (float)(defenders - 1));
                p.TeleportRpc(TitanMap.WallTop(Origin, 0, x) + Vector3.up * 0.3f, 180f);
                k++;
            }
        }

        public void ServerEnd()
        {
            if (!IsServer) return;
            active.Value = false;
            prep.Value = false;
            if (_cube) _cube.InstigatorFilter = null;
            _cube = null;
            _shots.Clear();
            PlayerMotor.RespawnOverride = null;
            ServerDespawnBots();
        }

        void ServerDespawnBots()
        {
            foreach (var b in _bots) if (b && b.IsSpawned) b.NetworkObject.Despawn(true);
            _bots.Clear();
            if (_aiTitan && _aiTitan.IsSpawned) _aiTitan.NetworkObject.Despawn(true);
            _aiTitan = null;
        }

        public int BotCount => _bots.Count;
        public bool HasAiTitan => _aiTitan != null;

        Vector3? DefenderRespawn(PlayerMotor m)
        {
            if (!active.Value) return null;
            var av = m.GetComponent<PlayerAvatar>();
            if (av && av.role.Value == PlayerRole.Titan) return TitanMap.TitanStart(Origin) + Vector3.forward * (TitanMap.WallNearZ(Origin, wall.Value) - TitanMap.TitanStart(Origin).z - 6f);
            return TitanMap.WallTop(Origin, Mathf.Min(wall.Value, 2)) + Vector3.up * 0.5f;
        }

        void Update()
        {
            if (IsServer && active.Value) ServerTick(Time.deltaTime);
            TickShotVisuals(Time.deltaTime);
        }

        void ServerTick(float dt)
        {
            var mm = MatchManager.Instance;
            if (prep.Value)
            {
                prepLeft.Value = Mathf.Max(0, prepLeft.Value - dt);
                if (prepLeft.Value <= 0) { prep.Value = false; BannerRpc("The Titan is coming!"); }
            }
            else timeLeft.Value = Mathf.Max(0, timeLeft.Value - dt);

            var titan = Body;
            TrackHands(titan, dt);
            ServerLoadTurrets(dt);
            ServerTickShots(dt, titan);

            // supports: a turret whose wall-top under it is gone falls off
            if ((_supportCheck -= dt) <= 0 && _cube)
            {
                _supportCheck = 0.5f;
                for (int i = 0; i < 12; i++)
                    if (ammo[i] >= 0 && !_cube.SolidNear(TurretPos(i) - Vector3.up * 0.3f, 0.5f, out _))
                    {
                        ammo[i] = -1;
                        FxHub.Instance?.ExplosionRpc(TurretPos(i), 1f, -1, true);
                    }
            }

            // breach: the Titan made it through the current wall
            if (titan && !prep.Value && titan.transform.position.z > TitanMap.WallFarZ(Origin, wall.Value) + 1.5f)
            {
                Breaches++;
                int broken = wall.Value;
                for (int s = 0; s < Layout.Length; s++) ammo[broken * Layout.Length + s] = -1;
                if (broken >= TitanMap.Walls.Length - 1)
                {
                    mm.ServerEndMatch($"The Titan broke through all three walls at {MatchManager.FormatTime(MatchSeconds - timeLeft.Value)}! The Titan wins!");
                    return;
                }
                wall.Value = broken + 1;
                BannerRpc($"Wall {broken + 1} has fallen! Fall back to wall {broken + 2}!");
                foreach (var p in PlayerAvatar.All)
                    if (p.role.Value == PlayerRole.Defender)
                        p.TeleportRpc(TitanMap.WallTop(Origin, wall.Value, Random.Range(-10f, 10f)) + Vector3.up * 0.3f, 180f);
                foreach (var b in _bots) if (b) b.ServerTeleport(TitanMap.WallTop(Origin, wall.Value, Random.Range(-12f, 12f)) + Vector3.up * 0.1f);
            }

            if (titanHp.Value <= 0)
                mm.ServerEndMatch($"The Titan fell at wall {wall.Value + 1}! The defenders win!");
            else if (timeLeft.Value <= 0)
                mm.ServerEndMatch($"The walls held for {MatchManager.FormatTime(MatchSeconds)}! The defenders win!");
        }

        void TrackHands(TitanBody titan, float dt)
        {
            if (!titan) return;
            dt = Mathf.Max(dt, 1e-4f);
            _handVelL = Vector3.Lerp(_handVelL, (titan.leftHand.position - _lastHand[0]) / dt, 0.5f);
            _handVelR = Vector3.Lerp(_handVelR, (titan.rightHand.position - _lastHand[1]) / dt, 0.5f);
            _lastHand[0] = titan.leftHand.position; _lastHand[1] = titan.rightHand.position;
        }

        void ServerLoadTurrets(float dt)
        {
            for (int i = 0; i < 12; i++)
            {
                if (ammo[i] < 0 || i / Layout.Length != wall.Value) continue;
                var k = KindOf(i);
                if (ammo[i] >= Capacity(k)) { _load[i] = 0; continue; }
                bool manned = false;
                foreach (var p in PlayerAvatar.All)
                    if (p.role.Value == PlayerRole.Defender && TurretNearIs(p.transform.position, i)) { manned = true; break; }
                if (!manned) foreach (var b in _bots) if (b && TurretNearIs(b.transform.position, i)) { manned = true; break; }
                if (!manned) continue;
                _load[i] += dt;
                if (_load[i] >= LoadSeconds(k)) { _load[i] = 0; ammo[i]++; }
            }
        }

        bool TurretNearIs(Vector3 pos, int i)
        {
            var d = pos - TurretPos(i);
            return Mathf.Abs(d.y) < 2.2f && new Vector2(d.x, d.z).magnitude < TurretReach;
        }

        // ------------------------------------------------------------------ turret fire (defenders)

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void FireTurretRpc(int i, RpcParams p = default)
        {
            if (!active.Value || i < 0 || i >= 12) return;
            if (!PlayerAvatar.ByClient.TryGetValue(p.Receive.SenderClientId, out var pl) || pl.role.Value != PlayerRole.Defender) return;
            if (!TurretNearIs(pl.transform.position, i)) return;
            ServerFireTurret(i);
        }

        /// <summary>Server: fire turret i at the Titan (also used by -autotest).</summary>
        public bool ServerFireTurret(int i)
        {
            if (ammo[i] <= 0 || Time.time < _fireReady[i]) return false;
            var titan = Body;
            if (!titan) return false;
            var k = KindOf(i);
            ammo[i]--;
            _fireReady[i] = Time.time + FireCooldown(k);
            var origin = TurretPos(i) + Vector3.up * 2.2f + Vector3.back * 0.6f;   // above the battlements
            var target = TitanChest(titan) + TitanVelocity(titan) * 0.5f;
            Vector3 vel;
            if (k == TurretKind.Artillery)
            {
                float t = 2.2f;   // high lob
                vel = (target - origin) / t + Vector3.up * (0.5f * ShotGravity(k) * t);
            }
            else if (k == TurretKind.Cannon)
            {
                float t = Vector3.Distance(origin, target) / 45f;
                vel = (target - origin) / Mathf.Max(t, 0.05f) + Vector3.up * (0.5f * ShotGravity(k) * t);
            }
            else vel = ((target - origin).normalized + Vector3.up * 0.8f).normalized * 22f;
            int id = ++_shotId;
            _shots.Add(new Shot { id = id, kind = k, pos = origin, vel = vel });
            ShotsFired++;
            ShotRpc(id, (byte)k, origin, vel);
            return true;
        }

        public static Vector3 TitanChest(TitanBody titan) => titan.transform.position + Vector3.up * 0.6f * titan.WorldScale;
        static Vector3 TitanVelocity(TitanBody titan) => titan.Velocity;
        static float ShotGravity(TurretKind k) => k == TurretKind.Artillery ? 9.81f : k == TurretKind.Cannon ? 4f : 0f;

        // ------------------------------------------------------------------ shots (server sim + client visuals)

        class Shot { public int id; public TurretKind kind; public Vector3 pos, vel; public float age; public bool deflected; public Transform vis; }
        readonly List<Shot> _shots = new List<Shot>();
        readonly Dictionary<int, Shot> _visShots = new Dictionary<int, Shot>();
        int _shotId;

        void ServerTickShots(float dt, TitanBody titan)
        {
            for (int s = _shots.Count - 1; s >= 0; s--)
            {
                var sh = _shots[s];
                Step(sh, dt, titan);
                sh.age += dt;
                bool end = false, hit = false;
                if (titan && !sh.deflected)
                {
                    // the Titan can swat shots away with a fast hand
                    foreach (var (hand, hv) in new[] { (titan.leftHand.position, _handVelL), (titan.rightHand.position, _handVelR) })
                        if (Vector3.Distance(hand, sh.pos) < 0.3f * Scale && hv.magnitude > 2f * Scale)
                        {
                            sh.deflected = true;
                            sh.vel = hv.normalized * 30f + Vector3.up * 6f;
                            Deflects++;
                            DeflectRpc(sh.id, sh.pos, sh.vel);
                            break;
                        }
                    if (!sh.deflected && CapsuleDistance(titan, sh.pos) < 0.35f * Scale)
                    {
                        hit = end = true;
                        ServerDamageTitan(ShotDamage(sh.kind));
                        ShotsHit++;
                    }
                }
                if (!end && (sh.age > 7f || sh.pos.y < Origin.y - 0.2f || (sh.age > 0.25f && _cube && _cube.Grid != null && _cube.SolidNear(sh.pos, 0.15f, out _)))) end = true;
                if (end)
                {
                    _shots.RemoveAt(s);
                    float r = sh.kind == TurretKind.Artillery ? 3f : sh.kind == TurretKind.Rocket ? 2f : 1.4f;
                    if (!hit && titan && !sh.deflected && CapsuleDistance(titan, sh.pos) < r + 0.3f * Scale)
                    {
                        ServerDamageTitan(ShotDamage(sh.kind) * 0.5f);   // splash
                        ShotsHit++;
                    }
                    ShotEndRpc(sh.id, sh.pos, r);
                }
            }
        }

        static float CapsuleDistance(TitanBody titan, Vector3 p)
        {
            var a = titan.transform.position + Vector3.up * 0.3f * titan.WorldScale;
            var b = titan.headAnchor.position;
            var ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-4f));
            return Vector3.Distance(p, a + ab * t);
        }

        void Step(Shot sh, float dt, TitanBody titan)
        {
            if (sh.kind == TurretKind.Rocket && !sh.deflected && titan)
            {
                // homing rocket pod: turns toward the Titan's chest and speeds up
                var want = (TitanChest(titan) - sh.pos).normalized;
                float speed = Mathf.Min(45f, sh.vel.magnitude + 25f * dt);
                sh.vel = Vector3.RotateTowards(sh.vel.normalized, want, 2.6f * dt, 0f) * speed;
            }
            else sh.vel += Vector3.down * ShotGravity(sh.kind) * dt;
            sh.pos += sh.vel * dt;
        }

        [Rpc(SendTo.ClientsAndHost)]
        void ShotRpc(int id, byte kind, Vector3 pos, Vector3 vel)
        {
            var k = (TurretKind)kind;
            var go = GameObject.CreatePrimitive(k == TurretKind.Rocket ? PrimitiveType.Capsule : PrimitiveType.Sphere);
            Destroy(go.GetComponent<Collider>());
            go.name = "TurretShot";
            go.transform.localScale = k == TurretKind.Artillery ? Vector3.one * 0.9f : k == TurretKind.Cannon ? Vector3.one * 0.55f : new Vector3(0.3f, 0.45f, 0.3f);
            var mr = go.GetComponent<MeshRenderer>();
            if (FxHub.Instance) mr.sharedMaterial = FxHub.Instance.glowMaterial;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", k == TurretKind.Rocket ? new Color(1f, 0.5f, 0.25f) : new Color(0.25f, 0.25f, 0.3f));
            mr.SetPropertyBlock(block);
            var tr = go.AddComponent<TrailRenderer>();
            if (FxHub.Instance) tr.sharedMaterial = FxHub.Instance.glowMaterial;
            tr.time = 0.3f; tr.widthMultiplier = 0.3f;
            tr.startColor = new Color(1f, 0.8f, 0.5f, 0.7f); tr.endColor = new Color(1f, 1f, 1f, 0f);
            go.transform.position = pos;
            _visShots[id] = new Shot { id = id, kind = k, pos = pos, vel = vel, vis = go.transform };
            FxHub.Instance?.LocalBlast(pos, 0.5f, new Color(1f, 0.85f, 0.5f));
        }

        [Rpc(SendTo.ClientsAndHost)]
        void DeflectRpc(int id, Vector3 pos, Vector3 vel)
        {
            if (_visShots.TryGetValue(id, out var s)) { s.pos = pos; s.vel = vel; s.deflected = true; }
            FxHub.Instance?.LocalBlast(pos, 1.2f, new Color(0.7f, 0.9f, 1f));
        }

        [Rpc(SendTo.ClientsAndHost)]
        void ShotEndRpc(int id, Vector3 pos, float radius)
        {
            if (_visShots.TryGetValue(id, out var s)) { if (s.vis) Destroy(s.vis.gameObject); _visShots.Remove(id); }
            FxHub.Instance?.LocalBlast(pos, radius, new Color(1f, 0.6f, 0.25f));
        }

        void TickShotVisuals(float dt)
        {
            if (_visShots.Count == 0) return;
            var titan = Body;
            foreach (var s in _visShots.Values)
            {
                if (!s.vis) continue;
                Step(s, dt, titan);
                s.vis.position = s.pos;
                if (s.vel.sqrMagnitude > 0.01f) s.vis.rotation = Quaternion.LookRotation(s.vel) * Quaternion.Euler(90, 0, 0);
            }
        }

        // ------------------------------------------------------------------ Titan damage

        public void ServerDamageTitan(float dmg)
        {
            if (!IsServer || !active.Value || prep.Value) return;
            titanHp.Value = Mathf.Max(0, titanHp.Value - dmg);
            var t = Titan;
            if (t) TitanHurtRpc(dmg);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void TitanHurtRpc(float dmg)
        {
            var t = Titan;
            if (t && t.IsOwner) TitanController.OnHurt(dmg);
        }

        /// <summary>Server: an explosion went off (RPGs, grenades...). Hurts the Titan if it's close.</summary>
        public static void ServerExplosion(Vector3 pos, float radiusM, ToolDef t, ulong owner)
        {
            var d = Instance;
            if (d == null || !d.IsServer || !d.active.Value || owner == d.titanClient.Value) return;
            var titan = d.Body;
            if (!titan) return;
            if (CapsuleDistance(titan, pos) > Mathf.Max(radiusM * 1.5f, 1.2f) + 0.3f * Scale) return;
            d.ServerDamageTitan(t.id == "RocketLauncher" ? RpgDamage : Mathf.Max(2f, t.damage * 0.05f));
        }

        // ------------------------------------------------------------------ Titan attacks

        /// <summary>The Titan's hand or foot moved through voxels. limb 0 = left hand, 1 = right hand, 2 = foot.
        /// Slow contact scrapes (no cooldown, just a tick rate); a fast punch with momentum digs a big dent.</summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void TitanHitRpc(Vector3 point, Vector3 velocity, byte limb, RpcParams p = default)
        {
            if (!active.Value || prep.Value || p.Receive.SenderClientId != titanClient.Value) return;
            ServerTitanHit(point, velocity, limb);
        }

        public void ServerTitanHit(Vector3 point, Vector3 velocity, byte limb)
        {
            var titan = Body;
            if (!titan || !_cube || limb > 2 || float.IsNaN(point.x) || float.IsNaN(velocity.x)) return;
            float s = titan.WorldScale;
            if (Vector3.Distance(point, titan.transform.position + Vector3.up * 0.5f * s) > 1.4f * s + 2f) return;   // must be within reach
            if (Time.time < _limbReady[limb]) return;
            _limbReady[limb] = Time.time + 0.05f;   // a tick rate, not a cooldown
            float real = velocity.magnitude / Mathf.Max(s, 0.1f);
            if (limb < 2 && real >= PunchMinSpeed && Time.time >= _punchReady[limb])
            {
                // a real punch: the harder you swing, the bigger the dent (pushed in along the swing)
                _punchReady[limb] = Time.time + 0.25f;
                float r = Mathf.Clamp(2.2f + (real - PunchMinSpeed) * 1.3f, 2.2f, 5.5f);
                var into = velocity.normalized * r * 0.5f * _cube.cellSize;
                _cube.ServerDamage(point + into, r, PunchDamage, 5, false, TitanInstigator);
                FxHub.Instance?.ShockwaveRpc(point, r * _cube.cellSize * 1.4f);
                FxHub.Instance?.ImpactRpc(point, -1);
                KnockDefenders(point, r * _cube.cellSize * 1.6f + 1f, 9f + real * 2f);
                Punches++;
                PunchFeelRpc(limb, r);
            }
            else
            {
                _cube.ServerDamage(point, ScrapeRadius, ScrapeDamage, 5, false, TitanInstigator);
                if (Random.value < 0.3f) FxHub.Instance?.ImpactRpc(point, -1);
                KnockDefenders(point, 1.2f, 4f);
                Scrapes++;
            }
        }

        [Rpc(SendTo.ClientsAndHost)]
        void PunchFeelRpc(byte limb, float radius)
        {
            var t = Titan;
            if (t && t.IsOwner) TitanController.OnPunchLanded(limb, radius);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void KickRpc(bool left, RpcParams p = default)
        {
            if (!active.Value || prep.Value || p.Receive.SenderClientId != titanClient.Value) return;
            ServerKick(left);
        }

        public void ServerKick(bool left)
        {
            if (Time.time < _kickReady) return;
            _kickReady = Time.time + 1.0f;
            KickAnimRpc(left);
            StartCoroutine(KickStrike());
        }

        IEnumerator KickStrike()
        {
            yield return new WaitForSeconds(0.42f);
            var titan = Body;
            if (!titan || !_cube || !active.Value) yield break;
            float s = titan.WorldScale;
            var fwd = Vector3.ProjectOnPlane(titan.headAnchor.forward, Vector3.up).normalized;
            if (fwd.sqrMagnitude < 0.01f) fwd = titan.transform.forward;
            var basePos = titan.transform.position;
            foreach (var (ahead, up) in new[] { (0.35f, 0.22f), (0.6f, 0.3f) })
            {
                var p = basePos + fwd * ahead * s + Vector3.up * up * s;
                _cube.ServerDamage(p, 4.5f, KickDamage, 5, true, TitanInstigator);
            }
            var hit = basePos + fwd * 0.5f * s + Vector3.up * 0.25f * s;
            FxHub.Instance?.ShockwaveRpc(hit, 3f);
            FxHub.Instance?.ImpactRpc(hit, -1);
            KnockDefenders(hit, 5f, 16f);
            Kicks++;
        }

        [Rpc(SendTo.ClientsAndHost)]
        void KickAnimRpc(bool left)
        {
            var b = Body;
            if (b) b.Visual?.Kick(left);
        }

        void KnockDefenders(Vector3 pos, float radius, float knock)
        {
            foreach (var pl in PlayerAvatar.All)
            {
                if (pl.role.Value != PlayerRole.Defender) continue;
                var d = pl.transform.position + Vector3.up * 0.5f - pos;
                if (d.magnitude > radius) continue;
                pl.KnockbackRpc((d.normalized + Vector3.up * 0.6f).normalized * knock * (1f - d.magnitude / radius * 0.5f));
            }
            foreach (var b in _bots)
            {
                if (!b) continue;
                var d = b.transform.position + Vector3.up * 0.5f - pos;
                if (d.magnitude > radius) continue;
                b.ServerKnock((d.normalized + Vector3.up * 0.6f).normalized * knock * (1f - d.magnitude / radius * 0.5f));
            }
        }

        [Rpc(SendTo.ClientsAndHost)]
        void BannerRpc(string text) => UI.GameMenus.Instance?.Toast(text, AeroPalette.Danger);

        // ------------------------------------------------------------------ local world: side walls + turret models (every peer)

        void SyncWorld(bool on)
        {
            if (_blockers) Destroy(_blockers);
            foreach (var t in _turretVis) if (t) Destroy(t);
            _turretVis.Clear();
            if (!on) return;
            StartCoroutine(BuildWorldWhenReady());
        }

        IEnumerator BuildWorldWhenReady()
        {
            while (active.Value && (VoxelCube.Instance == null || VoxelCube.Instance.Grid == null)) yield return null;
            if (!active.Value) yield break;
            var o = Origin;
            // invisible side walls: the Titan has to go through, not around
            _blockers = new GameObject("TitanSideWalls");
            float hw = TitanMap.HalfWidth;
            foreach (var side in new[] { -1f, 1f })
            {
                var b = new GameObject("Side").AddComponent<BoxCollider>();
                b.transform.SetParent(_blockers.transform, false);
                b.size = new Vector3(1f, 60f, 70f);
                b.center = new Vector3(o.x + side * (hw + 0.5f), o.y + 30f, o.z - 11f);
            }
            for (int i = 0; i < TurretCount; i++) _turretVis.Add(BuildTurret(i));
            RefreshTurretVisuals();
        }

        GameObject BuildTurret(int i)
        {
            var k = KindOf(i);
            var root = new GameObject($"Turret{i}_{k}");
            root.transform.position = TurretPos(i);
            root.transform.rotation = Quaternion.LookRotation(Vector3.back);   // facing the Titan's field
            Color body = k == TurretKind.Cannon ? new Color(0.35f, 0.38f, 0.45f) : k == TurretKind.Artillery ? new Color(0.45f, 0.5f, 0.3f) : new Color(0.75f, 0.35f, 0.25f);
            Part(root.transform, PrimitiveType.Cylinder, new Vector3(0, 0.15f, 0), Vector3.zero, new Vector3(1.1f, 0.15f, 1.1f), body * 0.8f);
            switch (k)
            {
                case TurretKind.Cannon:
                    Part(root.transform, PrimitiveType.Cube, new Vector3(0, 0.45f, 0), Vector3.zero, new Vector3(0.8f, 0.4f, 0.8f), body);
                    Part(root.transform, PrimitiveType.Cylinder, new Vector3(0, 0.7f, 0.55f), new Vector3(80, 0, 0), new Vector3(0.32f, 0.6f, 0.32f), new Color(0.15f, 0.15f, 0.18f));
                    break;
                case TurretKind.Artillery:
                    Part(root.transform, PrimitiveType.Cube, new Vector3(0, 0.45f, 0), Vector3.zero, new Vector3(1f, 0.45f, 1f), body);
                    Part(root.transform, PrimitiveType.Cylinder, new Vector3(0, 1.1f, 0.35f), new Vector3(40, 0, 0), new Vector3(0.45f, 0.75f, 0.45f), new Color(0.2f, 0.22f, 0.15f));
                    break;
                default:
                    Part(root.transform, PrimitiveType.Cube, new Vector3(0, 0.75f, 0.1f), new Vector3(-20, 0, 0), new Vector3(0.9f, 0.7f, 0.9f), body);
                    for (int t = 0; t < 4; t++)
                        Part(root.transform, PrimitiveType.Cylinder, new Vector3(-0.2f + 0.4f * (t % 2), 0.65f + 0.35f * (t / 2), 0.5f), new Vector3(70, 0, 0), new Vector3(0.22f, 0.12f, 0.22f), new Color(0.1f, 0.1f, 0.1f));
                    break;
            }
            // ammo pips
            var label = new GameObject("Ammo").AddComponent<TextMesh>();
            label.transform.SetParent(root.transform, false);
            label.transform.localPosition = new Vector3(0, 1.9f, 0);
            label.transform.localScale = Vector3.one * 0.05f;
            label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            label.GetComponent<MeshRenderer>().sharedMaterial = label.font.material;
            label.fontSize = 48; label.anchor = TextAnchor.MiddleCenter; label.alignment = TextAlignment.Center;
            label.gameObject.AddComponent<FaceCamera>();
            return root;
        }

        static void Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 euler, Vector3 scale, Color c)
        {
            var g = GameObject.CreatePrimitive(type);
            Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = pos; g.transform.localEulerAngles = euler; g.transform.localScale = scale;
            var mr = g.GetComponent<MeshRenderer>();
            if (Instance && Instance.partMaterial) mr.sharedMaterial = Instance.partMaterial;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var block = new MaterialPropertyBlock(); block.SetColor("_BaseColor", c); block.SetColor("_Color", c);
            mr.SetPropertyBlock(block);
        }

        void RefreshTurretVisuals()
        {
            for (int i = 0; i < _turretVis.Count && i < ammo.Count; i++)
            {
                var t = _turretVis[i];
                if (!t) continue;
                bool alive = ammo[i] >= 0;
                t.SetActive(alive);
                if (!alive) continue;
                var k = KindOf(i);
                var tm = t.GetComponentInChildren<TextMesh>();
                if (tm)
                {
                    tm.text = $"{Name(k)}\n{new string('●', ammo[i])}{new string('○', Capacity(k) - ammo[i])}";
                    tm.color = ammo[i] > 0 ? new Color(1f, 0.9f, 0.4f) : new Color(0.8f, 0.85f, 0.95f);
                }
            }
        }
    }

    /// <summary>The Titan's body for the director: a player (VR or desktop) or the AI Titan.</summary>
    public sealed class TitanBody
    {
        public Transform transform, headAnchor, leftHand, rightHand;
        public PlayerAvatar player;
        public TitanBot bot;
        public TitanBody(PlayerAvatar p) { player = p; transform = p.transform; headAnchor = p.headAnchor; leftHand = p.leftHand; rightHand = p.rightHand; }
        public TitanBody(TitanBot b, Transform head, Transform l, Transform r) { bot = b; transform = b.transform; headAnchor = head; leftHand = l; rightHand = r; }
        public float WorldScale => transform.localScale.x;
        public Vector3 Velocity => bot ? bot.Velocity : (player && player.IsOwner ? player.Motor.Velocity : Vector3.zero);
        public TitanVisual Visual => bot ? bot.GetComponent<TitanVisual>() : player ? player.GetComponentInChildren<TitanVisual>() : null;
        public static implicit operator bool(TitanBody b) => b != null && b.transform != null;
    }
}
