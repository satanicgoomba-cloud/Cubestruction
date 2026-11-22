using System.Collections;
using System.Collections.Generic;
using Cubestruction.Player;
using Cubestruction.Tools;
using Cubestruction.Voxels;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Siege
{
    /// <summary>
    /// Titan Siege, server-authoritative. The Titan player starts as a little Buddy at the foot of wall 1;
    /// when the prep time ends they bite their hand, lightning strikes, and they become the Titan, who must
    /// smash through three thick walls and the voxel town between them. Defenders dig the mine wall on top
    /// of their wall for cannonballs, rockets and the odd gun, carry the ammo to cannons, artillery and
    /// rocket pods (standing next to one loads what you carry; Use fires it) and fire their RPG. A breached
    /// wall sends the defenders to the next one. The Titan's nape takes triple damage; it can swat shots,
    /// block or catch RPGs (and throw them back), grab defenders, and gets faster and harder as it weakens.
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
        /// <summary>-titanbench: force this many bots (-1 = normal).</summary>
        public static int BotsOverride = -1;
        public const ulong AiTitanClient = ulong.MaxValue - 3;
        readonly List<DefenderBot> _bots = new List<DefenderBot>();
        TitanBot _aiTitan;

        // ---- tuning
        public const float Scale = 7f;                 // the Titan's world scale (~6.5 m tall)
        static TitanFile T => Balance.Titan;
        public static float PrepSeconds => T.prepSeconds;
        public static float MatchSeconds => T.matchSeconds;
        public static float BaseHp => T.baseHp;
        public static float HpPerDefender => T.hpPerDefender;
        public static float RpgDamage => T.rpgDamage;
        public const float ScrapeRadius = 1.3f;
        public static float ScrapeDamage => T.scrapeDamage;
        public const float PunchMinSpeed = 2.0f;       // real m/s of the hand (world speed / scale)
        public static float PunchDamage => T.punchDamage;
        public static float KickDamage => T.kickDamage;
        /// <summary>Instigator id for the Titan's voxel damage (the only damage walls take in this mode).</summary>
        public const ulong TitanInstigator = ulong.MaxValue - 2;
        public static int MaxBalls => T.maxBalls;
        public static int MaxRockets => T.maxRockets;
        public static float BallChance => T.ballChance;          // per mined voxel
        public static float RocketChance => T.rocketChance;
        public static float NapeMultiplier => T.napeMultiplier;
        public static float BlockMultiplier => T.blockMultiplier;
        public static int GunsPerMineWall => T.gunsPerMineWall;
        static readonly string[] WallGuns = { "Shotgun", "Minigun", "PinballPopper", "BoostBlaster", "RailGun", "FireworkLauncher", "RippleDisc", "BloomMortar" };

        public enum TurretKind : byte { Cannon = 0, Artillery = 1, Rocket = 2 }
        public struct TurretDef { public TurretKind kind; public float x; }
        public static readonly TurretDef[] Layout =
        {
            new TurretDef { kind = TurretKind.Cannon, x = -16f }, new TurretDef { kind = TurretKind.Artillery, x = -6f },
            new TurretDef { kind = TurretKind.Rocket, x = 6f },   new TurretDef { kind = TurretKind.Cannon, x = 16f },
        };
        static TurretTuning Tune(TurretKind k) => k == TurretKind.Cannon ? T.cannon : k == TurretKind.Artillery ? T.artillery : T.rocketPod;
        public static int Capacity(TurretKind k) => Tune(k).capacity;
        public static float LoadSeconds(TurretKind k) => k == TurretKind.Cannon ? 3f : k == TurretKind.Artillery ? 6f : 4f;
        public static float ShotDamage(TurretKind k) => Tune(k).damage;
        public static float FireCooldown(TurretKind k) => Tune(k).cooldown;
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
        /// <summary>False while the Titan is still a little Buddy (before the hand-bite).</summary>
        public readonly NetworkVariable<bool> transformed = new NetworkVariable<bool>();
        /// <summary>0 calm, 1 enraged (below 50% HP), 2 berserk (below 25%).</summary>
        public readonly NetworkVariable<byte> rage = new NetworkVariable<byte>();
        public readonly NetworkVariable<FixedString4096Bytes> scoreboard = new NetworkVariable<FixedString4096Bytes>();
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
                if (!transformed.Value) return null;   // still a little Buddy: nothing to shoot at yet
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
        public static int Punches, Scrapes, Kicks, ShotsFired, ShotsHit, Deflects, Breaches, Blocks, Catches, NapeHits, BulletHits, Grabs, Transforms, AmmoMined, AmmoLoaded, GunsFound;

        // ---- per-participant stats for the end scoreboard (client id, or a bot id)
        class Stat { public string name; public float damage; public int nape, mined, loaded, guns; }
        readonly Dictionary<ulong, Stat> _stats = new Dictionary<ulong, Stat>();
        int _titanCatches, _titanThrows, _titanBlocks;
        float _biteDeadline, _lastHurt;
        Stat StatOf(ulong id)
        {
            if (_stats.TryGetValue(id, out var st)) return st;
            st = new Stat { name = PlayerAvatar.ByClient.TryGetValue(id, out var pl) ? pl.displayName.Value.ToString() : BotName(id) };
            _stats[id] = st;
            return st;
        }
        static string BotName(ulong id) => id >= ulong.MaxValue - 200 ? $"Bot {ulong.MaxValue - 100 - id + 1}" : "?";

        void Awake() { Instance = this; ammo = new NetworkList<int>(); }

        public override void OnNetworkSpawn()
        {
            if (IsServer && ammo.Count == 0) for (int i = 0; i < 12; i++) ammo.Add(0);
            active.OnValueChanged += (_, on) => SyncWorld(on);
            ammo.OnListChanged += _ => RefreshTurretVisuals();
            SyncWorld(active.Value);
        }

        /// <summary>True while the Titan is still a little Buddy (it can walk about, but not smash).</summary>
        public static bool InputLocked(PlayerAvatar who) =>
            Instance != null && Instance.active.Value && !Instance.transformed.Value && who != null && who.role.Value == PlayerRole.Titan;

        public Vector3 Origin => _cube ? _cube.transform.position : (VoxelCube.Instance ? VoxelCube.Instance.transform.position : Vector3.zero);

        public Vector3 TurretPos(int i)
        {
            int w = i / Layout.Length, s = i % Layout.Length;
            return TitanMap.TurretSpot(Origin, w, Layout[s].x);
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
            // only the Titan breaks walls and town; defenders (people and bots) can only dig the mine walls
            cube.ProtectFilter = (idx, who) =>
            {
                var c = cube.Grid.Coord(idx);
                if (who == TitanInstigator) return TitanMap.IsTopProtected(c.x, c.y, c.z);   // the Titan can't take the walkway away
                return !TitanMap.IsMine(c.x, c.y, c.z);
            };
            cube.OnCellsDestroyedServer -= OnCellsDestroyed;
            cube.OnCellsDestroyedServer += OnCellsDestroyed;
            int defenders = 0;
            foreach (var p in players) if (p != titan) defenders++;
            int botCount = BotsOverride >= 0 ? BotsOverride : Mathf.Max(0, MinDefenders - defenders);
            titanClient.Value = titan ? titan.OwnerClientId : AiTitanClient;
            // bots are a bit weaker than people, so they add a bit less Titan health
            titanMaxHp.Value = (BaseHp + HpPerDefender * (defenders + botCount * T.botHpFactor)) * (titan ? 1f : T.aiHpMultiplier);
            titanHp.Value = titanMaxHp.Value;
            wall.Value = 0;
            rage.Value = 0;
            transformed.Value = false;
            scoreboard.Value = "";
            prep.Value = true;
            prepLeft.Value = PrepSeconds;
            timeLeft.Value = MatchSeconds;
            _biteDeadline = -1;
            for (int i = 0; i < 12; i++) { ammo[i] = 0; _load[i] = 0; _fireReady[i] = 0; }
            _shots.Clear();
            _stats.Clear();
            _titanCatches = _titanThrows = _titanBlocks = 0;
            PlayerMotor.RespawnOverride = DefenderRespawn;
            active.Value = true;

            ServerDespawnBots();
            for (int b = 0; b < botCount && defenderBotPrefab; b++)
            {
                var go = Instantiate(defenderBotPrefab, TitanMap.WallTop(Origin, 0, Mathf.Lerp(-14f, 14f, (b + 0.5f) / botCount)) + Vector3.up * 0.1f, Quaternion.Euler(0, 180, 0));
                var bot = go.GetComponent<DefenderBot>();
                bot.ServerInit(b);
                go.GetComponent<NetworkObject>().Spawn(true);
                _bots.Add(bot);
                StatOf(DefenderBot.BotIdFor(b));
            }
            ServerPlaceWallGuns();

            // loadouts + starting spots: the swing, an empty RPG and a sledgehammer (plus a little jetpack)
            var g = Balance.Tool("GrappleHook"); var j = Balance.Tool("Jetpack"); var r = Balance.Tool("RocketLauncher"); var sl = Balance.Tool("SledgeHammer");
            int k = 0;
            foreach (var p in players)
            {
                p.ammoBalls.Value = 0; p.ammoRockets.Value = 0;
                StatOf(p.OwnerClientId);
                if (p == titan)
                {
                    // the Titan starts as a little Buddy at the foot of wall 1
                    p.role.Value = PlayerRole.Titan;
                    p.ownedTools.Value = 0;
                    p.TeleportRpc(TitanMap.TitanStart(Origin), 0f);
                    continue;
                }
                p.role.Value = PlayerRole.Defender;
                p.ownedTools.Value = (1UL << g.index) | (1UL << j.index) | (1UL << r.index) | (1UL << sl.index);
                float x = defenders <= 1 ? 0f : Mathf.Lerp(-12f, 12f, k / (float)(defenders - 1));
                p.TeleportRpc(TitanMap.WallTop(Origin, 0, x) + Vector3.up * 0.3f, 180f);
                k++;
            }
        }

        public void ServerEnd()
        {
            if (!IsServer) return;
            if (active.Value) scoreboard.Value = BuildScoreboard();
            active.Value = false;
            prep.Value = false;
            transformed.Value = false;
            if (_cube) { _cube.ProtectFilter = null; _cube.OnCellsDestroyedServer -= OnCellsDestroyed; }
            _cube = null;
            _shots.Clear();
            PlayerMotor.RespawnOverride = null;
            foreach (var p in PlayerAvatar.All) if (p.Motor.heldBy != null) p.ReleasedRpc(Vector3.zero);
            ServerDespawnBots();
        }

        // ------------------------------------------------------------------ the hand-bite (human -> Titan)

        /// <summary>The Titan player bit their hand (VR: hand to mouth; desktop: B).</summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void BiteRpc(RpcParams p = default)
        {
            if (!active.Value || prep.Value || transformed.Value || p.Receive.SenderClientId != titanClient.Value) return;
            ServerTransform();
        }

        /// <summary>Server: lightning from a clear sky, and the Titan stands where the Buddy was.</summary>
        public void ServerTransform()
        {
            if (transformed.Value) return;
            Transforms++;
            Vector3 at = TitanMap.TitanStart(Origin);
            var human = Titan;
            if (human) at = human.transform.position;
            else if (titanBotPrefab)
            {
                var tb = Instantiate(titanBotPrefab, at, Quaternion.identity);
                tb.GetComponent<NetworkObject>().Spawn(true);
                _aiTitan = tb.GetComponent<TitanBot>();
            }
            transformed.Value = true;
            TransformFxRpc(at);
            KnockDefenders(at + Vector3.up, 9f, 14f);
            FxHub.Instance?.ExplosionRpc(at + Vector3.up * 2f, 4f, -1, false);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void TransformFxRpc(Vector3 at) => StartCoroutine(TransformFx(at));

        IEnumerator TransformFx(Vector3 at)
        {
            var fx = FxHub.Instance;
            UI.GameMenus.Instance?.Toast("Lightning strikes! The Titan is here!", AeroPalette.Danger);
            for (int i = 0; i < 4 && fx; i++)
            {
                var sky = at + new Vector3(Random.Range(-4f, 4f), 60f, Random.Range(-4f, 4f));
                var pts = new Vector3[6];
                for (int k = 0; k < 6; k++) pts[k] = Vector3.Lerp(sky, at, k / 5f) + (k > 0 && k < 5 ? Random.insideUnitSphere * 3f : Vector3.zero);
                fx.LightningLocal(pts);
                fx.LocalBlast(at + Vector3.up * (1f + i), 2f + i * 1.5f, new Color(1f, 0.95f, 0.6f));
                yield return new WaitForSeconds(0.12f);
            }
            for (float t = 0; t < 2.5f; t += 0.25f)
            {
                fx?.LocalBlast(at + Vector3.up * (2f + t * 2f) + Random.insideUnitSphere * 2f, 2.5f, new Color(1f, 1f, 1f, 0.5f));   // steam
                yield return new WaitForSeconds(0.25f);
            }
        }

        // ------------------------------------------------------------------ mining: ammo and guns

        void OnCellsDestroyed(int count, ulong instigator, int money)
        {
            if (!active.Value || instigator == TitanInstigator || count <= 0) return;
            int balls = 0, rockets = 0;
            for (int i = 0; i < count; i++)
            {
                float r = Random.value;
                if (r < BallChance) balls++;
                else if (r < BallChance + RocketChance) rockets++;
            }
            if (balls + rockets == 0) return;
            if (PlayerAvatar.ByClient.TryGetValue(instigator, out var pl))
            {
                int b0 = pl.ammoBalls.Value, r0 = pl.ammoRockets.Value;
                pl.ammoBalls.Value = Mathf.Min(MaxBalls, b0 + balls);
                pl.ammoRockets.Value = Mathf.Min(MaxRockets, r0 + rockets);
                int got = pl.ammoBalls.Value - b0 + pl.ammoRockets.Value - r0;
                StatOf(instigator).mined += got; AmmoMined += got;
            }
            else
                foreach (var b in _bots)
                    if (b && b.BotId == instigator) { int got = b.ServerAddAmmo(balls, rockets); StatOf(instigator).mined += got; AmmoMined += got; }
        }

        /// <summary>Hide a few guns (gold voxels) in each mine wall.</summary>
        void ServerPlaceWallGuns()
        {
            var g = _cube.Grid;
            var rng = new System.Random(System.Environment.TickCount);
            for (int w = 0; w < TitanMap.Walls.Length; w++)
            {
                var W = TitanMap.Walls[w];
                for (int n = 0, tries = 0; n < GunsPerMineWall && tries < 500; tries++)
                {
                    int x = rng.Next(2, TitanMap.SizeX - 2), y = W.height + rng.Next(0, TitanMap.MineHeight - 1), z = rng.Next(W.WalkZ1, W.Z1);
                    int idx = g.Index(x, y, z);
                    if (!g.solid[idx] || _cube.MarkOf(idx) != CellMark.None) continue;
                    _cube.ServerMark(idx, CellMark.Loot);
                    n++;
                }
            }
        }

        /// <summary>Server (from VoxelCube): a gold voxel in a mine wall broke: hand its digger a gun.</summary>
        public void ServerLoot(ulong instigator, Vector3 where)
        {
            if (!PlayerAvatar.ByClient.TryGetValue(instigator, out var pl)) return;
            var choices = new List<ToolDef>();
            foreach (var id in WallGuns) { var t = Balance.Tool(id); if (t != null && !pl.Owns(t.index)) choices.Add(t); }
            if (choices.Count == 0) { pl.ammoRockets.Value = MaxRockets; pl.LootRpc("Rockets x10", "Mine wall"); return; }
            var gun = choices[Random.Range(0, choices.Count)];
            pl.ownedTools.Value |= 1UL << gun.index;
            pl.LootRpc(gun.name + " (shoot the Titan!)", "Mine wall");
            StatOf(instigator).guns++; GunsFound++;
            FxHub.Instance?.ShockwaveRpc(where, 1.2f);
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
            if (av && av.role.Value == PlayerRole.Titan)
                return new Vector3(Origin.x, Origin.y + 0.5f, TitanMap.WallNearZ(Origin, wall.Value) - (transformed.Value ? 6f : 4f));
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
                if (prepLeft.Value <= 0)
                {
                    prep.Value = false;
                    if (Titan) { BannerRpc("Time's up! The Titan is about to transform..."); BiteHintRpc(); _biteDeadline = Time.time + 15f; }
                    else { BannerRpc("Lightning in the distance... the Titan is coming!"); _biteDeadline = Time.time + 1.5f; }
                }
            }
            else timeLeft.Value = Mathf.Max(0, timeLeft.Value - dt);

            // the bite: a human Titan has 15 s to bite their hand before the lightning finds them anyway
            if (!prep.Value && !transformed.Value && _biteDeadline > 0 && Time.time >= _biteDeadline) ServerTransform();
            // rage as health drops
            float frac = titanHp.Value / Mathf.Max(1f, titanMaxHp.Value);
            byte want = (byte)(frac <= 0.25f ? 2 : frac <= 0.5f ? 1 : 0);
            if (transformed.Value && want > rage.Value)
            {
                rage.Value = want;
                BannerRpc(want == 2 ? "The Titan goes berserk!" : "The Titan is enraged! It's faster now...");
            }

            // slow regeneration once the Titan hasn't been hurt for a few seconds
            if (transformed.Value && titanHp.Value > 0 && titanHp.Value < titanMaxHp.Value && Time.time - _lastHurt > T.regenDelay)
                titanHp.Value = Mathf.Min(titanMaxHp.Value, titanHp.Value + T.regenPerSecond * dt);

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

        /// <summary>Standing next to a turret loads what you're carrying into it: cannonballs into cannons and
        /// artillery, rockets into rocket pods, one every 0.3 s. Turrets never fill on their own.</summary>
        void ServerLoadTurrets(float dt)
        {
            for (int i = 0; i < 12; i++)
            {
                if (ammo[i] < 0 || i / Layout.Length != wall.Value) continue;
                var k = KindOf(i);
                if (ammo[i] >= Capacity(k)) { _load[i] = 0; continue; }
                _load[i] += dt;
                if (_load[i] < 0.3f) continue;
                bool rocket = k == TurretKind.Rocket;
                bool loaded = false;
                foreach (var p in PlayerAvatar.All)
                {
                    if (p.role.Value != PlayerRole.Defender || !TurretNearIs(p.transform.position, i)) continue;
                    var carry = rocket ? p.ammoRockets : p.ammoBalls;
                    if (carry.Value <= 0) continue;
                    carry.Value--; loaded = true; StatOf(p.OwnerClientId).loaded++;
                    break;
                }
                if (!loaded) foreach (var b in _bots) if (b && TurretNearIs(b.transform.position, i) && b.ServerTakeAmmo(rocket)) { loaded = true; StatOf(b.BotId).loaded++; break; }
                if (!loaded) continue;
                _load[i] = 0; ammo[i]++; AmmoLoaded++;
            }
        }

        [Rpc(SendTo.ClientsAndHost)]
        void BiteHintRpc()
        {
            var t = Titan;
            if (t && t.IsOwner) TitanController.ShowBiteHint();
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
            ServerFireTurret(i, pl.OwnerClientId);
        }

        /// <summary>Server: fire turret i at the Titan (also used by -autotest).</summary>
        public bool ServerFireTurret(int i) => ServerFireTurret(i, ulong.MaxValue);

        public bool ServerFireTurret(int i, ulong by)
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
            _shots.Add(new Shot { id = id, kind = k, pos = origin, vel = vel, firedBy = by });
            ShotsFired++;
            ShotRpc(id, (byte)k, origin, vel);
            return true;
        }

        public static Vector3 TitanChest(TitanBody titan) => titan.transform.position + Vector3.up * 0.6f * titan.WorldScale;
        static Vector3 TitanVelocity(TitanBody titan) => titan.Velocity;
        static float ShotGravity(TurretKind k) => k == TurretKind.Artillery ? 9.81f : k == TurretKind.Cannon ? 4f : 0f;

        // ------------------------------------------------------------------ shots (server sim + client visuals)

        class Shot { public int id; public TurretKind kind; public Vector3 pos, vel; public float age; public bool deflected; public Transform vis; public ulong firedBy = ulong.MaxValue; }
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
                        ServerDamageTitan(ShotDamage(sh.kind), sh.firedBy, IsNape(titan, sh.pos));
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
                        ServerDamageTitan(ShotDamage(sh.kind) * 0.5f, sh.firedBy, false);   // splash
                        ShotsHit++;
                    }
                    ShotEndRpc(sh.id, sh.pos, r);
                }
            }
        }

        /// <summary>The nape: back of the neck, the Titan's weak spot (×3 damage).</summary>
        public static Vector3 NapePos(TitanBody titan)
        {
            float s = titan.WorldScale;
            var fwd = Vector3.ProjectOnPlane(titan.headAnchor.forward, Vector3.up);
            fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : titan.transform.forward;
            return titan.headAnchor.position - fwd * 0.11f * s - Vector3.up * 0.13f * s;
        }
        public static bool IsNape(TitanBody titan, Vector3 p) => titan && Vector3.Distance(p, NapePos(titan)) < 0.13f * titan.WorldScale;

        /// <summary>Server, from the bullet sim: did this bullet hit the Titan? (guns found in the mine wall)</summary>
        public static bool ServerBulletHit(Vector3 pos, ToolDef t, ulong owner, float bulletScale)
        {
            var d = Instance;
            if (d == null || !d.active.Value || owner == d.titanClient.Value) return false;
            var titan = d.Body;
            if (!titan || CapsuleDistance(titan, pos) > 0.32f * titan.WorldScale + t.bulletSize * bulletScale) return false;
            BulletHits++;
            d.ServerDamageTitan(T.WeaponHit(t), owner, IsNape(titan, pos));
            return true;
        }

        /// <summary>Server: a bullet burst (mortar bomblet, firework, explosive round) near the Titan: splash damage.</summary>
        public static void ServerBulletBlast(Vector3 pos, float radiusM, ToolDef t, ulong owner)
        {
            var d = Instance;
            if (d == null || !d.active.Value || owner == d.titanClient.Value) return;
            var titan = d.Body;
            if (!titan || CapsuleDistance(titan, pos) > radiusM + 0.32f * titan.WorldScale) return;
            BulletHits++;
            d.ServerDamageTitan(T.WeaponHit(t), owner, IsNape(titan, pos));
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

        public void ServerDamageTitan(float dmg) => ServerDamageTitan(dmg, ulong.MaxValue, false);

        public void ServerDamageTitan(float dmg, ulong source, bool nape)
        {
            if (!IsServer || !active.Value || prep.Value || !transformed.Value) return;
            if (nape) { dmg *= NapeMultiplier; NapeHits++; }
            if (source != ulong.MaxValue) { var st = StatOf(source); st.damage += dmg; if (nape) st.nape++; }
            if (nape && PlayerAvatar.ByClient.TryGetValue(source, out var shooter)) shooter.ToastRpc("NAPE HIT! x3 damage");
            titanHp.Value = Mathf.Max(0, titanHp.Value - dmg);
            _lastHurt = Time.time;
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
            float body = CapsuleDistance(titan, pos);
            if (body > Mathf.Max(radiusM * 1.5f, 1.2f) + 0.3f * Scale) return;
            float dmg = t.id == "RocketLauncher" ? RpgDamage : Mathf.Max(2f, T.WeaponHit(t));
            // a rocket that bursts on the Titan's hand is blocked: a quarter of the damage
            float hand = Mathf.Min(Vector3.Distance(pos, titan.leftHand.position), Vector3.Distance(pos, titan.rightHand.position));
            if (hand < 0.14f * titan.WorldScale && hand < body)
            {
                dmg *= BlockMultiplier; Blocks++; d._titanBlocks++;
                FxHub.Instance?.ShockwaveRpc(pos, 1.5f);
            }
            d.ServerDamageTitan(dmg, owner, IsNape(titan, pos));
        }

        // ------------------------------------------------------------------ Titan attacks

        /// <summary>The Titan's hand or foot moved through voxels. limb 0 = left hand, 1 = right hand, 2 = foot.
        /// Slow contact scrapes (no cooldown, just a tick rate); a fast punch with momentum digs a big dent.</summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void TitanHitRpc(Vector3 point, Vector3 velocity, byte limb, RpcParams p = default)
        {
            if (!active.Value || prep.Value || !transformed.Value || p.Receive.SenderClientId != titanClient.Value) return;
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
                float r = Mathf.Clamp(T.punchRadiusMin + (real - PunchMinSpeed) * T.punchRadiusPerMps, T.punchRadiusMin, T.punchRadiusMax) * (1f + 0.15f * rage.Value);
                var into = velocity.normalized * r * 0.5f * _cube.cellSize;
                _cube.ServerDamage(point + into, r, PunchDamage * (1f + 0.3f * rage.Value), 5, false, TitanInstigator);
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
            if (!active.Value || prep.Value || !transformed.Value || p.Receive.SenderClientId != titanClient.Value) return;
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
                _cube.ServerDamage(p, 4.5f * (1f + 0.15f * rage.Value), KickDamage * (1f + 0.3f * rage.Value), 5, true, TitanInstigator);
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
                if (pl.role.Value != PlayerRole.Defender || pl.Motor.heldBy != null || _heldDefenders.ContainsKey(pl.OwnerClientId)) continue;
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

        // ------------------------------------------------------------------ the Titan grabs defenders

        readonly Dictionary<ulong, bool> _heldDefenders = new Dictionary<ulong, bool>();   // client or bot id -> left hand

        /// <summary>The Titan's grip closed near a defender (a player's client id, or a bot's id).</summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void GrabDefenderRpc(ulong id, bool left, RpcParams p = default)
        {
            if (!active.Value || !transformed.Value || p.Receive.SenderClientId != titanClient.Value) return;
            ServerGrabDefender(id, left);
        }

        public bool ServerGrabDefender(ulong id, bool left)
        {
            var titan = Body;
            if (!titan || _heldDefenders.ContainsKey(id)) return false;
            var hand = left ? titan.leftHand : titan.rightHand;
            float reach = 0.25f * titan.WorldScale + 1f;
            if (PlayerAvatar.ByClient.TryGetValue(id, out var pl))
            {
                if (pl.role.Value != PlayerRole.Defender || Vector3.Distance(hand.position, pl.Motor.Chest) > reach) return false;
                _heldDefenders[id] = left;
                pl.HeldRpc(titanClient.Value, left);
            }
            else
            {
                var bot = _bots.Find(b => b && b.BotId == id);
                if (!bot || Vector3.Distance(hand.position, bot.transform.position + Vector3.up * 0.5f) > reach) return false;
                _heldDefenders[id] = left;
                bot.ServerHold(hand);
            }
            Grabs++;
            return true;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ReleaseDefenderRpc(ulong id, Vector3 velocity, RpcParams p = default)
        {
            if (p.Receive.SenderClientId != titanClient.Value) return;
            ServerReleaseDefender(id, velocity);
        }

        public void ServerReleaseDefender(ulong id, Vector3 velocity)
        {
            if (!_heldDefenders.Remove(id)) return;
            velocity = Vector3.ClampMagnitude(velocity, 40f);
            _titanThrows++;
            if (PlayerAvatar.ByClient.TryGetValue(id, out var pl)) pl.ReleasedRpc(velocity);
            else _bots.Find(b => b && b.BotId == id)?.ServerKnock(velocity);
        }

        // ------------------------------------------------------------------ RPGs vs the Titan's hands

        /// <summary>Server, every frame for each flying rocket: an open hand in the way blocks it (it bursts there,
        /// at a quarter damage); a closed hand catches it, and letting go throws it back.</summary>
        public static bool ServerRocketNearTitan(ExplosiveProjectile rocket)
        {
            var d = Instance;
            if (d == null || !d.active.Value || rocket.Owner == d.titanClient.Value) return false;
            var titan = d.Body;
            if (!titan) return false;
            // the AI Titan only reacts to about a third of the rockets (the same ones its hands go up for)
            if (titan.bot && rocket.NetworkObjectId % 100 >= 35) return false;
            float r = 0.12f * titan.WorldScale;   // about a metre around the hand
            for (int h = 0; h < 2; h++)
            {
                var hand = h == 0 ? titan.leftHand : titan.rightHand;
                if (Vector3.Distance(hand.position, rocket.transform.position) > r) continue;
                if (d.Gripping(titan, h == 0))
                {
                    rocket.ServerCatch(hand, d.titanClient.Value);
                    Catches++; d._titanCatches++;
                    d.CatchRpc(h == 0);
                    return true;
                }
                rocket.ServerExplode();   // blocked: ServerExplosion sees it next to the hand
                return true;
            }
            return false;
        }

        bool Gripping(TitanBody titan, bool left)
        {
            if (titan.bot) return titan.bot.WantsCatch;
            if (!titan.player) return false;
            return (titan.player.handPose.Value & (left ? 1 : 2)) != 0;
        }

        public static int RocketsThrownBack;   // -autotest

        /// <summary>Where the nearest defender (player or bot) is, for the AI Titan's throws.</summary>
        public Vector3? NearestDefender(Vector3 from)
        {
            Vector3? best = null; float bd = float.MaxValue;
            foreach (var p in PlayerAvatar.All)
                if (p.role.Value == PlayerRole.Defender) { float dd = Vector3.Distance(from, p.Motor.Chest); if (dd < bd) { bd = dd; best = p.Motor.Chest; } }
            foreach (var b in _bots)
                if (b) { float dd = Vector3.Distance(from, b.transform.position); if (dd < bd) { bd = dd; best = b.transform.position + Vector3.up * 0.5f; } }
            return best;
        }

        /// <summary>Velocity of a Titan hand (for throws), tracked on the server.</summary>
        public Vector3 HandVelocity(bool left) => left ? _handVelL : _handVelR;

        [Rpc(SendTo.ClientsAndHost)]
        void CatchRpc(bool left)
        {
            var t = Titan;
            if (t && t.IsOwner) TitanController.OnCaught(left);
        }

        // ------------------------------------------------------------------ end scoreboard

        string BuildScoreboard()
        {
            var sb = new System.Text.StringBuilder();
            Stat mvp = null;
            foreach (var kv in _stats)
            {
                if (kv.Key == titanClient.Value || kv.Key == AiTitanClient) continue;
                var st = kv.Value;
                if (mvp == null || st.damage > mvp.damage) mvp = st;
            }
            sb.Append("<b>Defenders</b>   damage · nape hits · ammo mined · loaded · guns found\n");
            foreach (var kv in _stats)
            {
                if (kv.Key == titanClient.Value || kv.Key == AiTitanClient) continue;
                var st = kv.Value;
                sb.Append($"{(st == mvp ? "★ " : "")}{st.name}   {st.damage:0} · {st.nape} · {st.mined} · {st.loaded} · {st.guns}\n");
            }
            string tname = Titan ? Titan.displayName.Value.ToString() : "AI Titan";
            sb.Append($"\n<b>Titan ({tname})</b>   reached wall {wall.Value + 1} · {titanHp.Value:0}/{titanMaxHp.Value:0} HP left · " +
                      $"{_titanBlocks} blocks · {_titanCatches} catches · {_titanThrows} throws");
            if (mvp != null) sb.Append($"\nMVP: {mvp.name} ({mvp.damage:0} damage)");
            var text = sb.ToString();
            return text.Length > 4000 ? text.Substring(0, 4000) : text;
        }

        [Rpc(SendTo.ClientsAndHost)]
        void BannerRpc(string text) => UI.GameMenus.Instance?.Toast(text, AeroPalette.Danger);

        // ------------------------------------------------------------------ local world: side walls + turret models (every peer)

        GameObject _kiosk;

        void SyncWorld(bool on)
        {
            // the Mining arena's shop kiosk stands where the first town is: hide it during Titan Siege
            if (_kiosk == null) _kiosk = GameObject.Find("ShopKiosk");
            if (_kiosk) _kiosk.SetActive(!on);
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
            // the field: a long meadow under the whole map, and invisible side walls so the Titan
            // has to go through the walls, not around them
            _blockers = new GameObject("TitanField");
            float hw = TitanMap.HalfWidth, len = TitanMap.Length;
            float z0 = TitanMap.CellZ(o, -40), z1 = TitanMap.CellZ(o, TitanMap.SizeZ + 16);
            var ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "Meadow";
            ground.transform.SetParent(_blockers.transform, false);
            ground.transform.position = new Vector3(o.x, o.y - 0.5f, (z0 + z1) * 0.5f);
            ground.transform.localScale = new Vector3(hw * 2f + 24f, 1f, z1 - z0);
            var gr = ground.GetComponent<MeshRenderer>();
            if (partMaterial) gr.sharedMaterial = partMaterial;
            var gb = new MaterialPropertyBlock(); gb.SetColor("_BaseColor", new Color(0.45f, 0.82f, 0.4f)); gr.SetPropertyBlock(gb);
            foreach (var side in new[] { -1f, 1f })
            {
                var b = new GameObject("Side").AddComponent<BoxCollider>();
                b.transform.SetParent(_blockers.transform, false);
                b.size = new Vector3(1f, 60f, z1 - z0);
                b.center = new Vector3(o.x + side * (hw + 0.5f), o.y + 30f, (z0 + z1) * 0.5f);
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
