using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Cubestruction.Player;
using Cubestruction.Tools;
using Cubestruction.Voxels;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Super
{
    /// <summary>
    /// SUPER! and Sandbox (scene-placed, server-authoritative).
    /// Default rules, <b>Break the cube</b>: the Mining cube, but everything you do is an anime move. Breaking
    /// voxels fills your Power bar; each Power Up unlocks bigger moves, and the cube's tougher inner layers
    /// need the higher tiers. Clear the last voxel to win. The optional <b>Brawl</b> rules fight each other in
    /// a voxel arena instead (below).
    /// Brawl: everyone picks a fighter, starts at Tier 1
    /// (Brawler) and climbs to Tier 5 (ULTIMATE) by filling the Power bar: landing hits, taking hits and
    /// parrying all charge it. Each tier unlocks the next special; Tier 4 drains Power while you hold it;
    /// using your ultimate drops you back to Tier 4; a KO drops you a tier. Most KOs when time runs out
    /// wins. Sandbox: every move unlocked, infinite Power, health regenerates, training dummies.
    ///
    /// Owners send what they did (melee, punches, specials, beam aim); the server finds who got hit,
    /// applies blocks and parries, moves fighters (owners move themselves via FighterState RPCs) and
    /// breaks the arena. Projectiles and beams are simulated here and mirrored to every peer.
    /// </summary>
    public class SuperDirector : NetworkBehaviour
    {
        public static SuperDirector Instance { get; private set; }
        public GameObject botPrefab;

        public readonly NetworkVariable<bool> active = new NetworkVariable<bool>();
        public readonly NetworkVariable<bool> sandbox = new NetworkVariable<bool>();
        public readonly NetworkVariable<bool> teams = new NetworkVariable<bool>();
        /// <summary>False = break the cube together; true = fight each other in the arena.</summary>
        public readonly NetworkVariable<bool> brawl = new NetworkVariable<bool>();
        public readonly NetworkVariable<float> timeLeft = new NetworkVariable<float>();
        public readonly NetworkVariable<float> timeStopUntil = new NetworkVariable<float>();
        public readonly NetworkVariable<ulong> timeStopper = new NetworkVariable<ulong>(ulong.MaxValue);
        public readonly NetworkVariable<FixedString4096Bytes> scoreboard = new NetworkVariable<FixedString4096Bytes>();

        /// <summary>Lobby choices (host): 0 = break the cube, 1 = free-for-all brawl, 2 = team brawl;
        /// and how many bots in a brawl (-1 = fill to 4 fighters).</summary>
        public static int Rules;
        public static bool WantTeams => Rules == 2;
        public static bool WantBrawl => Rules != 0;
        public bool IsBrawl => brawl.Value;
        public static int BotsOverride = -1;
        public static readonly Color[] TeamColors = { new Color(1f, 0.35f, 0.3f), new Color(0.3f, 0.6f, 1f) };
        public static readonly string[] TeamNames = { "Red", "Blue" };

        // -autotest counters
        public static int Hits, Blocks, Parries, Specials, Shots, BeamTicks, Clashes, Ults, Kos, Transforms, Punches, Freezes, Denied;

        VoxelCube _cube;
        readonly List<FighterBot> _bots = new List<FighterBot>();
        public bool IsActive => active.Value;
        public bool IsSandbox => sandbox.Value;
        public Vector3 Origin => _cube ? _cube.transform.position : VoxelCube.Instance ? VoxelCube.Instance.transform.position : Vector3.zero;
        public double Now => NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;
        public IReadOnlyList<FighterBot> Bots => _bots;

        void Awake() { Instance = this; }

        public override void OnNetworkSpawn()
        {
            active.OnValueChanged += (_, on) => SyncWorld(on);
            SyncWorld(active.Value);
        }

        // ================================================================== match flow (server)

        public void ServerBegin(VoxelCube cube, List<PlayerAvatar> players, bool isSandbox)
        {
            if (!IsServer) return;
            _cube = cube;
            brawl.Value = WantBrawl;
            if (brawl.Value) cube.ProtectFilter = (idx, who) => cube.Grid.Coord(idx).y == 0;   // the stage floor never goes all the way through
            cube.OnCellsDestroyedServer -= OnVoxels;
            cube.OnCellsDestroyedServer += OnVoxels;
            sandbox.Value = isSandbox;
            teams.Value = WantTeams && !isSandbox && brawl.Value;
            timeLeft.Value = Roster.MatchSeconds;
            timeStopUntil.Value = 0;
            _shots.Clear(); _beams.Clear(); _hazards.Clear(); _meteors.Clear();
            scoreboard.Value = "";
            active.Value = true;

            int slot = 0;
            foreach (var p in players)
            {
                p.role.Value = PlayerRole.Fighter;
                var fs = p.GetComponent<FighterState>();
                if (fs) ServerReset(fs, slot++);
            }
            if (!brawl.Value) return;   // breaking the cube: no bots; MatchManager spreads everyone around the cube
            int bots = BotsOverride >= 0 ? BotsOverride : isSandbox ? 2 : Mathf.Max(0, 4 - players.Count);
            if (teams.Value && (players.Count + bots) % 2 == 1) bots++;
            var taken = players.Select(p => (int)(p.GetComponent<FighterState>()?.character.Value ?? 0)).ToList();
            for (int i = 0; i < bots && botPrefab; i++)
            {
                int ch = Enumerable.Range(0, Roster.All.Length).Where(c => !taken.Contains(c)).DefaultIfEmpty(Random.Range(0, Roster.All.Length)).OrderBy(_ => Random.value).First();
                taken.Add(ch);
                var go = Instantiate(botPrefab, SuperArena.Spawn(Origin, slot, players.Count + bots), Quaternion.identity);
                var bot = go.GetComponent<FighterBot>();
                bot.ServerInit(i, ch, isSandbox);
                go.GetComponent<NetworkObject>().Spawn(true);
                _bots.Add(bot);
                ServerReset(bot.State, slot++);
            }
            int n = slot, k = 0;
            foreach (var f in FighterState.All.Where(f => f.inMatch.Value).ToList())
            {
                var pos = SuperArena.Spawn(Origin, k++, n);
                f.RespawnRpc(pos, YawToCentre(pos));
            }
            PlayerMotor.RespawnOverride = m =>
            {
                var o = Origin;
                return SuperArena.Spawn(o, Random.Range(0, 8), 8);
            };
        }

        float YawToCentre(Vector3 pos)
        {
            var d = Origin - pos; d.y = 0;
            return d.sqrMagnitude > 0.01f ? Quaternion.LookRotation(d).eulerAngles.y : 0f;
        }

        void ServerReset(FighterState f, int slot)
        {
            var def = f.Def;
            f.inMatch.Value = true;
            f.tier.Value = (byte)(sandbox.Value ? Roster.MaxTier : 1);
            f.power.Value = sandbox.Value ? Roster.PowerMax : 0f;
            f.maxHp.Value = Roster.BaseHp * def.hpMul;
            f.hp.Value = f.maxHp.Value;
            f.down.Value = false;
            f.kos.Value = 0; f.falls.Value = 0; f.combo.Value = 0;
            f.team.Value = (byte)(teams.Value ? slot % 2 : slot);
            f.frozenUntil.Value = 0;
            f.damageDealt = 0; f.bestCombo = 0; f.comboStep = 0;
            f.lastAttacker = ulong.MaxValue; f.stunUntil = 0;
            for (int i = 0; i < f.cooldownEnd.Length; i++) f.cooldownEnd[i] = 0;
        }

        public void ServerEnd()
        {
            if (!IsServer) return;
            if (active.Value) scoreboard.Value = BuildScoreboard();
            active.Value = false;
            if (_cube) { _cube.ProtectFilter = null; _cube.OnCellsDestroyedServer -= OnVoxels; }
            _cube = null;
            foreach (var sh in _shots) ShotEndRpc(sh.id, sh.pos, 0f, sh.color);
            _shots.Clear(); _beams.Clear(); _hazards.Clear(); _meteors.Clear();
            PlayerMotor.RespawnOverride = null;
            foreach (var f in FighterState.All) { f.inMatch.Value = false; f.down.Value = false; f.frozenUntil.Value = 0; }
            foreach (var b in _bots) if (b && b.IsSpawned) b.NetworkObject.Despawn();
            _bots.Clear();
        }

        /// <summary>"Kenji wins with 7 KOs!" (or the winning team).</summary>
        /// <summary>Break the cube: breaking voxels fills Power (less per voxel at higher tiers).</summary>
        void OnVoxels(int count, ulong instigator, int money)
        {
            if (brawl.Value || sandbox.Value || count <= 0) return;
            var f = FighterState.Of(instigator);
            if (f == null || !f.inMatch.Value) return;
            f.power.Value = Mathf.Min(Roster.PowerMax, f.power.Value + count * Roster.PowerPerVoxel / f.tier.Value);
            f.damageDealt += count;
        }

        public string ResultText()
        {
            var list = FighterState.All.Where(f => f.inMatch.Value).ToList();
            if (list.Count == 0) return "Match over!";
            if (!brawl.Value)
            {
                var mvp = list.OrderByDescending(f => f.damageDealt).First();
                return $"Cube cleared in {MatchManager.FormatTime(MatchManager.Instance ? MatchManager.Instance.elapsed.Value : 0)}! MVP: {mvp.DisplayName} ({mvp.Def.name}, {mvp.damageDealt:N0} voxels)";
            }
            if (teams.Value)
            {
                int red = list.Where(f => f.team.Value == 0).Sum(f => f.kos.Value), blue = list.Where(f => f.team.Value == 1).Sum(f => f.kos.Value);
                return red == blue ? $"Draw! {red} KOs each" : $"Team {(red > blue ? "Red" : "Blue")} wins, {Mathf.Max(red, blue)} KOs to {Mathf.Min(red, blue)}!";
            }
            var best = list.OrderByDescending(f => f.kos.Value).ThenBy(f => f.falls.Value).ThenByDescending(f => f.damageDealt).First();
            return $"{best.DisplayName} ({best.Def.name}) wins with {best.kos.Value} KO{(best.kos.Value == 1 ? "" : "s")}!";
        }

        string BuildScoreboard()
        {
            var sb = new System.Text.StringBuilder();
            int rank = 1;
            if (!brawl.Value)
            {
                foreach (var f in FighterState.All.Where(f => f.inMatch.Value).OrderByDescending(f => f.damageDealt))
                {
                    string c = ColorUtility.ToHtmlStringRGB(f.Def.color * 0.85f);
                    sb.Append($"{rank++}.  <color=#{c}><b>{f.DisplayName}</b></color> · {f.Def.name} \"{f.Def.title}\"   {f.damageDealt:N0} voxels · best combo {f.bestCombo} · Tier {f.tier.Value}\n");
                }
                return sb.ToString();
            }
            foreach (var f in FighterState.All.Where(f => f.inMatch.Value).OrderByDescending(f => f.kos.Value).ThenBy(f => f.falls.Value).ThenByDescending(f => f.damageDealt))
            {
                string col = ColorUtility.ToHtmlStringRGB(teams.Value ? TeamColors[f.team.Value % 2] : f.Def.color * 0.85f);
                sb.Append($"{rank++}.  <color=#{col}><b>{f.DisplayName}</b></color> · {f.Def.name} \"{f.Def.title}\"   {f.kos.Value} KOs · {f.falls.Value} falls · {f.damageDealt:N0} dmg · best combo {f.bestCombo} · Tier {f.tier.Value}\n");
                if (sb.Length > 3600) break;
            }
            return sb.ToString();
        }

        // ================================================================== tick

        float _beamAcc, _hazAcc;

        void Update()
        {
            TickShotVisuals(Time.deltaTime);
            if (!IsServer || !active.Value) return;
            float dt = Time.deltaTime;
            bool timeStop = timeStopUntil.Value > Now;
            if (!sandbox.Value && !timeStop && brawl.Value)
            {
                timeLeft.Value = Mathf.Max(0f, timeLeft.Value - dt);
                if (timeLeft.Value <= 0f) { MatchManager.Instance?.ServerEndMatch(ResultText()); return; }
            }
            if (!timeStop && timeStopper.Value != ulong.MaxValue) timeStopper.Value = ulong.MaxValue;

            var o = Origin;
            foreach (var f in FighterState.All)
            {
                if (!f.inMatch.Value) continue;
                if (f.down.Value)
                {
                    if (Time.time >= f.respawnAt) ServerRespawn(f);
                    continue;
                }
                if (brawl.Value && f.transform.position.y < SuperArena.RingOutY(o)) { ServerKo(f, Time.time - f.lastHurt < 8f ? f.lastAttacker : ulong.MaxValue, Vector3.zero, true); continue; }
                if (sandbox.Value)
                {
                    f.power.Value = Roster.PowerMax;
                    if (f.Player) f.tier.Value = Roster.MaxTier;
                    if (Time.time - f.lastHurt > 2f && f.hp.Value < f.maxHp.Value) f.hp.Value = Mathf.Min(f.maxHp.Value, f.hp.Value + 150f * dt);
                }
                else if (!timeStop)
                {
                    float p = f.power.Value + Roster.PowerPerSecond * dt;
                    if (!brawl.Value) p -= Roster.PowerPerSecond * dt;   // the cube: Power comes from breaking it
                    if (f.tier.Value == 4 && brawl.Value) p -= Roster.AscendedUpkeep * dt;
                    if (p <= 0f && f.tier.Value == 4) { f.tier.Value = 3; p = 0f; Callout($"{f.DisplayName} drops back to Tier 3", f.Def.color); }
                    f.power.Value = Mathf.Clamp(p, 0f, Roster.PowerMax);
                }
                if (f.combo.Value > 0 && Time.time - f.lastLight > 1.3f && Time.time - f.lastHeavy > 1.3f && Time.time - _lastComboHit.GetValueOrDefault(f) > 1.3f) f.combo.Value = 0;
            }

            if (!timeStop) StepShots(dt);
            _beamAcc += dt;
            if (_beamAcc >= 0.1f) { _beamAcc = 0; TickBeams(); }
            _hazAcc += dt;
            if (_hazAcc >= 0.25f && !timeStop) { _hazAcc = 0; TickHazards(); }
        }

        readonly Dictionary<FighterState, float> _lastComboHit = new Dictionary<FighterState, float>();

        // ================================================================== rules

        public bool Enemies(FighterState a, FighterState b) =>
            brawl.Value && a != null && b != null && a != b && b.Alive && (!teams.Value || a.team.Value != b.team.Value);

        /// <summary>Can this fighter act right now (alive, not frozen, not stunned, not stopped in time)?</summary>
        public bool CanAct(FighterState f) =>
            f != null && f.Alive && active.Value && !f.Frozen(Now) && Time.time >= f.stunUntil;

        public bool Unlocked(FighterState f, int slot) => sandbox.Value || f.tier.Value >= f.Def.moves[slot].tier;

        public float CooldownLeft(FighterState f, int slot) => Mathf.Max(0f, f.cooldownEnd[slot] - Time.time);

        IEnumerable<FighterState> EnemiesOf(FighterState a) => FighterState.All.Where(f => f.inMatch.Value && Enemies(a, f));

        /// <summary>Best target in front: inside the cone, nearest first (angle counts a little).</summary>
        public FighterState TargetInFront(FighterState a, Vector3 origin, Vector3 dir, float range, float coneDeg)
        {
            FighterState best = null; float bestScore = float.MaxValue;
            foreach (var f in EnemiesOf(a))
            {
                var to = f.Chest - origin;
                float d = to.magnitude;
                if (d > range + 0.4f * f.Scale) continue;
                float ang = d < 0.6f ? 0f : Vector3.Angle(dir, to);
                if (ang > coneDeg) continue;
                float score = d + ang * 0.03f;
                if (score < bestScore) { bestScore = score; best = f; }
            }
            return best;
        }

        /// <summary>Server: one hit. Returns true if it landed (not parried, target alive).</summary>
        public bool ServerHit(FighterState a, FighterState t, float damage, Vector3 knock, float stun = 0f, bool unblockable = false, bool beam = false, bool raw = false)
        {
            if (!Enemies(a, t)) return false;
            if (timeStopUntil.Value > Now && timeStopper.Value != a.Id) return false;   // stopped in time: nothing lands
            bool blocked = false;
            var fromAttacker = a.transform.position - t.transform.position; fromAttacker.y = 0;
            if (!unblockable && t.blocking.Value && !t.Frozen(Now) && Vector3.Dot(t.Facing, fromAttacker.normalized) > 0.1f)
            {
                if (!beam && Time.time - t.blockSince < 0.22f)
                {
                    // PARRY: no damage, the attacker is stunned, the defender gets Power
                    Parries++;
                    a.stunUntil = Time.time + 0.9f;
                    if (!sandbox.Value) t.power.Value = Mathf.Min(Roster.PowerMax, t.power.Value + Roster.PowerParry);
                    a.KnockRpc(-fromAttacker.normalized * -4f + Vector3.up * 2f);
                    ParryFxRpc(t.Chest);
                    return false;
                }
                blocked = true;
                Blocks++;
                damage *= 0.2f; knock *= 0.3f; stun = 0;
            }
            if (!raw) damage *= Roster.TierDamage(a.tier.Value) * a.Def.damageMul;
            if (timeStopUntil.Value > Now && timeStopper.Value == a.Id) knock *= 0.15f;    // the world is frozen: they just take it
            t.hp.Value = Mathf.Max(0f, t.hp.Value - damage);
            t.lastHurt = Time.time;
            t.lastAttacker = a.Id;
            a.damageDealt += damage;
            if (!sandbox.Value)
            {
                a.power.Value = Mathf.Min(Roster.PowerMax, a.power.Value + damage * Roster.PowerPerDealt);
                t.power.Value = Mathf.Min(Roster.PowerMax, t.power.Value + damage * Roster.PowerPerTaken);
            }
            float last = _lastComboHit.TryGetValue(a, out var lc) ? lc : -99f;
            a.combo.Value = Time.time - last < 1.3f ? a.combo.Value + 1 : 1;
            _lastComboHit[a] = Time.time;
            a.bestCombo = Mathf.Max(a.bestCombo, a.combo.Value);
            if (stun > 0) t.stunUntil = Mathf.Max(t.stunUntil, Time.time + stun);
            if (knock.sqrMagnitude > 0.01f) t.KnockRpc(knock);
            HitFxRpc(t.Chest, a.Def.aura, damage, blocked);
            Hits++;
            if (t.hp.Value <= 0f) ServerKo(t, a.Id, knock, false);
            return true;
        }

        void ServerKo(FighterState t, ulong by, Vector3 fling, bool ringOut)
        {
            if (t.down.Value) return;
            Kos++;
            t.down.Value = true;
            t.hp.Value = 0;
            t.falls.Value++;
            t.respawnAt = Time.time + Roster.RespawnSeconds;
            var killer = by != t.Id ? FighterState.Of(by) : null;
            if (killer != null && killer.inMatch.Value) killer.kos.Value++;
            if (!sandbox.Value)
            {
                t.tier.Value = (byte)Mathf.Max(1, t.tier.Value - 1);
                t.power.Value = 0;
            }
            foreach (var bm in _beams.Where(b => b.owner == t).ToList()) EndBeam(bm);
            t.KoRpc(fling.sqrMagnitude > 1f ? fling : Vector3.up * 3f);
            string how = ringOut ? "RING OUT!" : "K.O.!";
            Callout(killer != null ? $"{how}  {killer.DisplayName} took out {t.DisplayName}" : $"{how}  {t.DisplayName}", killer != null ? killer.Def.color : AeroPalette.Danger);
            KoFxRpc(t.Chest);
        }

        void ServerRespawn(FighterState f)
        {
            f.maxHp.Value = Roster.BaseHp * f.Def.hpMul;
            f.hp.Value = f.maxHp.Value;
            f.down.Value = false;
            f.frozenUntil.Value = 0;
            f.stunUntil = 0;
            var pos = SuperArena.Spawn(Origin, Random.Range(0, 8), 8);
            f.RespawnRpc(pos, YawToCentre(pos));
        }

        public void ServerFreeze(FighterState t, float seconds)
        {
            Freezes++;
            t.frozenUntil.Value = (float)(Now + seconds);
            t.stunUntil = Mathf.Max(t.stunUntil, Time.time + seconds);
        }

        void Callout(string text, Color c) => CalloutRpc(text, c);

        // ================================================================== basic attacks

        /// <summary>kind: 0 light (jab, cross, hook chain), 1 kick, 2 launcher, 3 axe kick.</summary>
        public void ServerMelee(FighterState a, byte kind, Vector3 origin, Vector3 dir)
        {
            if (!CanAct(a)) return;
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : a.Facing;
            float s = a.Scale, dmg; Vector3 knock; float stun;
            var flat = new Vector3(dir.x, 0, dir.z).normalized;
            if (kind == 0)
            {
                if (Time.time - a.lastLight < Roster.LightCooldown * 0.8f) return;
                a.comboStep = Time.time - a.lastLight < 0.9f ? (a.comboStep + 1) % 3 : 0;
                a.lastLight = Time.time;
                dmg = a.comboStep == 0 ? Roster.JabDamage : a.comboStep == 1 ? Roster.CrossDamage : Roster.HookDamage;
                knock = a.comboStep == 2 ? flat * 7f + Vector3.up * 3f : flat * 2.2f + Vector3.up * 0.8f;
                stun = a.comboStep == 2 ? 0.15f : 0.3f;
            }
            else
            {
                if (Time.time - a.lastHeavy < Roster.HeavyCooldown * 0.85f) return;
                a.lastHeavy = Time.time;
                switch (kind)
                {
                    case 2: dmg = Roster.LauncherDamage; knock = Vector3.up * 13f + flat * 1.5f; stun = 0.6f; break;
                    case 3: dmg = Roster.AxeDamage; knock = Vector3.down * 16f + flat * 3f; stun = 0.4f; break;
                    default: dmg = Roster.KickDamage; knock = flat * 10f + Vector3.up * 3.5f; stun = 0.25f; break;
                }
            }
            var t = TargetInFront(a, origin, dir, Roster.MeleeRange * s * (kind == 0 ? 1f : 1.15f), 70f);
            if (t != null) ServerHit(a, t, dmg, knock * Mathf.Sqrt(Roster.TierDamage(a.tier.Value)), stun);
            else PunchScenery(a, new Ray(origin, dir), Roster.MeleeRange * s * 1.1f, kind == 0 ? 1f : 1.4f);
        }

        /// <summary>A real punch (VR): a fast fist next to a foe or the scenery.</summary>
        public void ServerPunch(FighterState a, Vector3 fist, Vector3 vel)
        {
            if (!CanAct(a)) return;
            float speed = vel.magnitude / Mathf.Max(0.5f, a.Scale);
            if (speed < 2f || Time.time - a.lastLight < 0.16f) return;
            a.lastLight = Time.time;
            Punches++;
            FighterState best = null; float bd = float.MaxValue;
            foreach (var f in EnemiesOf(a))
            {
                // anywhere on the body: a capsule from the feet to the head
                var p0 = f.transform.position + Vector3.up * 0.15f * f.Scale; var p1 = f.transform.position + Vector3.up * 0.95f * f.Scale;
                float d = DistToSegment(fist, p0, p1);
                if (d < 0.5f * f.Scale + 0.25f && d < bd) { bd = d; best = f; }
            }
            float k = Mathf.Clamp01((speed - 2f) / 4.5f);
            if (best != null) ServerHit(a, best, Mathf.Lerp(Roster.JabDamage, Roster.KickDamage * 1.2f, k), vel.normalized * Mathf.Lerp(2.5f, 11f, k) + Vector3.up * (1f + 3f * k), 0.25f);
            else if (_cube && _cube.SolidNear(fist, 0.3f * a.Scale, out var at)) _cube.ServerDamage(at, 1.2f + 0.4f * a.tier.Value, 18f * Roster.TierDamage(a.tier.Value) * (0.5f + k), Roster.VoxelTier(a.tier.Value), false, a.Id);
        }

        void PunchScenery(FighterState a, Ray ray, float range, float mul)
        {
            if (!_cube || !_cube.Raycast(ray, range, out _, out var p)) return;
            float r = (1.3f + 0.35f * a.tier.Value) * mul * (brawl.Value ? 1f : 1.25f);
            _cube.ServerDamage(p + ray.direction * 0.1f, r, (brawl.Value ? 16f : 22f) * Roster.TierDamage(a.tier.Value) * mul, Roster.VoxelTier(a.tier.Value), false, a.Id);
            FxHub.Instance?.ImpactRpc(p, -1);
        }

        static float DistToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            var ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(1e-5f, ab.sqrMagnitude));
            return Vector3.Distance(p, a + ab * t);
        }

        // ================================================================== Power Up

        public void ServerTransform(FighterState a)
        {
            if (!CanAct(a) || sandbox.Value || a.tier.Value >= Roster.MaxTier || a.power.Value < Roster.PowerMax - 0.01f) return;
            Transforms++;
            a.tier.Value++;
            a.power.Value = a.tier.Value == 4 ? 35f : 0f;
            a.hp.Value = Mathf.Min(a.maxHp.Value, a.hp.Value + a.maxHp.Value * Roster.TransformHeal);
            foreach (var f in EnemiesOf(a).Where(f => Vector3.Distance(f.transform.position, a.transform.position) < 4f * a.Scale).ToList())
                ServerHit(a, f, 20f, (f.transform.position - a.transform.position).normalized * 9f + Vector3.up * 4f, 0.2f, true, raw: true);
            TransformFxRpc(a.NetworkObjectId, a.tier.Value);
            Callout($"{a.DisplayName} powers up!  TIER {a.tier.Value}: {Roster.TierNames[a.tier.Value].ToUpper()}", a.Def.aura);
        }

        // ================================================================== specials

        /// <summary>Slot 0-2 = specials A-C, 3 = ultimate. charge 0..1 for beams and the sun.</summary>
        public bool ServerSpecial(FighterState a, int slot, Vector3 origin, Vector3 dir, float charge = 1f)
        {
            if (!CanAct(a) || slot < 0 || slot > 3) return false;
            var m = a.Def.moves[slot];
            dir = dir.sqrMagnitude > 1e-4f ? dir.normalized : a.Facing;
            bool ult = slot == 3;
            if (!Unlocked(a, slot) || (ult && !sandbox.Value && a.tier.Value < Roster.MaxTier) || Time.time < a.cooldownEnd[slot]
                || (!sandbox.Value && a.power.Value < m.cost)) { Denied++; return false; }
            if (!sandbox.Value) a.power.Value -= m.cost;
            a.cooldownEnd[slot] = Time.time + m.cooldown * (ult && sandbox.Value ? 0.6f : 1f);
            Specials++;
            if (ult)
            {
                Ults++;
                if (!sandbox.Value) { a.tier.Value = 4; a.power.Value = 35f; }
                Callout($"{a.DisplayName}:  {m.name.ToUpper()}!!", a.Def.aura);
            }
            else SpecialCalloutRpc(a.NetworkObjectId, m.name);
            var flat = new Vector3(dir.x, 0, dir.z).normalized;
            if (flat.sqrMagnitude < 0.01f) flat = a.Facing;
            float s = a.Scale;
            if (!brawl.Value && CubeSpecial(a, m, origin, dir, flat)) return true;
            switch (m.kind)
            {
                case MoveKind.FlashStep: StartCoroutine(FlashStep(a, m, origin, dir, flat)); break;
                case MoveKind.Rush: StartCoroutine(Rush(a, m, origin, dir, flat)); break;
                case MoveKind.RisingStrike:
                {
                    var t = TargetInFront(a, origin, dir, m.radius * s, 75f);
                    a.KnockRpc(Vector3.up * 11f + flat * 2f);
                    if (t != null) ServerHit(a, t, m.damage, Vector3.up * 14f + flat * 2.5f, 0.7f);
                    _cube?.ServerDamage(origin + flat * 1f + Vector3.up * 0.5f, m.voxels, 40f, Roster.VoxelTier(a.tier.Value), false, a.Id);
                    FlameFxRpc(a.transform.position, Vector3.up, a.Def.aura);
                    break;
                }
                case MoveKind.Slam: StartCoroutine(Slam(a, m, flat)); break;
                case MoveKind.Grab: StartCoroutine(Grab(a, m, origin, dir, flat)); break;
                case MoveKind.Blast:
                    SpawnShot(a, m, origin + dir * 0.6f * s, dir * m.speed, m.radius * s, m.damage, explodeRadius: m.radius * 3.2f * s, look: m.speed < 20f ? (byte)3 : (byte)0);
                    break;
                case MoveKind.Volley:
                    for (int i = 0; i < m.count; i++)
                    {
                        float yaw = (i - (m.count - 1) * 0.5f) * (m.count > 3 ? 8f : 12f);
                        var d = Quaternion.AngleAxis(yaw, Vector3.up) * dir;
                        SpawnShot(a, m, origin + d * 0.5f * s, d * m.speed, m.radius * s, m.damage, explodeRadius: 0.6f * s, look: (byte)2);
                    }
                    break;
                case MoveKind.Freeze:
                    foreach (var f in EnemiesOf(a).Where(f => Vector3.Distance(f.transform.position, a.transform.position) < m.radius * s).ToList())
                        if (ServerHit(a, f, m.damage, (f.transform.position - a.transform.position).normalized * 2f, 0f)) ServerFreeze(f, 1.8f);
                    DomeFxRpc(a.transform.position, m.radius * s, a.Def.aura, 0.7f);
                    break;
                case MoveKind.Slash:
                    SpawnShot(a, m, origin + flat * 0.8f * s, flat * m.speed, m.radius * s, m.damage, pierce: true, carve: m.voxels, range: 32f, look: (byte)1);
                    break;
                case MoveKind.Beam: StartBeam(a, m, origin, dir, charge); break;
                case MoveKind.Tornado:
                {
                    var p = a.transform.position + flat * 4f * s;
                    _hazards.Add(new Hazard { owner = a, m = m, pos = p, vel = flat * m.speed, until = Time.time + 5f });
                    break;
                }
                // ---- ultimates
                case MoveKind.Sphere:
                {
                    var from = a.transform.position + Vector3.up * 5.5f * s;
                    var aim = (origin + dir * 40f - from).normalized;
                    SpawnShot(a, m, from, aim * m.speed, 3.4f * s, m.damage, explodeRadius: m.radius * s, look: (byte)4, unblockable: true);
                    break;
                }
                case MoveKind.MeteorDrop:
                    if (a.Bot) StartCoroutine(BotMeteor(a, m, origin, dir));
                    else _meteors[a] = Time.time + 6f;   // the owner flies up and dives, then reports the impact
                    break;
                case MoveKind.Storm: StartCoroutine(Storm(a, m)); break;
                case MoveKind.AbsoluteZero: StartCoroutine(AbsoluteZero(a, m)); break;
                case MoveKind.Eclipse:
                    SpawnShot(a, m, a.transform.position + Vector3.up * 1.5f * s + flat * 1f, flat * m.speed, m.radius, m.damage, pierce: true, carve: m.voxels, range: 80f, look: (byte)5, unblockable: true);
                    break;
                case MoveKind.TimeStop:
                {
                    const float secs = 4f;
                    timeStopper.Value = a.Id;
                    timeStopUntil.Value = (float)(Now + secs);
                    foreach (var f in EnemiesOf(a).ToList()) ServerFreeze(f, secs);
                    if (teams.Value) foreach (var f in FighterState.All.Where(f => f.inMatch.Value && f != a && !Enemies(a, f)).ToList()) ServerFreeze(f, secs);
                    TimeStopFxRpc(a.transform.position, secs);
                    break;
                }
            }
            return true;
        }

        // ================================================================== break the cube: moves aimed at people hit the cube instead

        void CubeBlast(FighterState a, Vector3 at, float radiusCells, float damage, int tierBonus = 1)
        {
            if (!_cube) return;
            _cube.ServerDamage(at, radiusCells, damage, Mathf.Min(5, Roster.VoxelTier(a.tier.Value) + tierBonus), true, a.Id);
        }

        /// <summary>The cube versions of moves that normally need a foe. Returns false to run the normal move.</summary>
        bool CubeSpecial(FighterState a, MoveDef m, Vector3 origin, Vector3 dir, Vector3 flat)
        {
            float s = a.Scale;
            switch (m.kind)
            {
                case MoveKind.FlashStep:
                {
                    // blink to the cube face in front and strike it
                    float dist = m.radius * s;
                    Vector3 hit = default;
                    bool wall = _cube != null && _cube.Raycast(new Ray(a.Chest, dir), dist, out _, out hit);
                    var to = wall ? hit - dir * 0.8f * s : a.transform.position + flat * 8f * s;
                    if (wall) to.y = Mathf.Max(to.y - 0.6f * s, a.transform.position.y - 20f);
                    var from = a.transform.position;
                    a.PlaceRpc(to, Quaternion.LookRotation(flat).eulerAngles.y);
                    StreakFxRpc(from + Vector3.up * 0.6f, to + Vector3.up * 0.6f, a.Def.aura);
                    if (wall) { CubeBlast(a, hit + dir * 0.3f, 3.5f, m.damage * 0.8f); FxHub.Instance?.ImpactRpc(hit, -1); }
                    return true;
                }
                case MoveKind.Rush:
                    StartCoroutine(CubeRush(a, m, dir));
                    return true;
                case MoveKind.Grab:
                {
                    // rip a chunk out of the cube
                    if (_cube && _cube.Raycast(new Ray(a.Chest, dir), m.radius * s + 1f, out _, out var hit))
                    {
                        CubeBlast(a, hit + dir * 0.5f, m.voxels, m.damage * 0.6f, 2);
                        DomeFxRpc(hit, 1.5f, a.Def.aura, 0.4f);
                    }
                    return true;
                }
                case MoveKind.Freeze:
                    CubeBlast(a, a.Chest, 7f, m.damage * 1.2f);
                    DomeFxRpc(a.transform.position, m.radius * s, a.Def.aura, 0.7f);
                    return true;
                case MoveKind.Storm:
                    StartCoroutine(CubeStorm(a, m));
                    return true;
                case MoveKind.AbsoluteZero:
                    StartCoroutine(CubeZero(a, m));
                    return true;
                case MoveKind.TimeStop:
                    // nobody to stop: a huge cyclone instead
                    _hazards.Add(new Hazard { owner = a, m = new MoveDef(m.name, MoveKind.Tornado, 5, 0, 0, 40, 8f, 3, 6, ""), pos = a.transform.position + flat * 5f * s, vel = flat * 2.5f, until = Time.time + 7f });
                    TimeStopFxRpc(a.transform.position, 0.6f);
                    return true;
            }
            return false;
        }

        IEnumerator CubeRush(FighterState a, MoveDef m, Vector3 dir)
        {
            if (!_cube || !_cube.Raycast(new Ray(a.Chest, dir), m.radius * a.Scale, out _, out var hit)) yield break;
            var from = a.transform.position;
            var flat = new Vector3(dir.x, 0, dir.z).normalized;
            var spot = hit - dir * 0.9f * a.Scale; spot.y = Mathf.Max(spot.y - 0.6f * a.Scale, from.y - 20f);
            a.PlaceRpc(spot, Quaternion.LookRotation(flat.sqrMagnitude > 0.01f ? flat : a.Facing).eulerAngles.y);
            StreakFxRpc(from + Vector3.up * 0.6f, spot + Vector3.up * 0.6f, a.Def.aura);
            for (int i = 0; i < m.count; i++)
            {
                yield return new WaitForSeconds(0.07f);
                if (!a || !a.Alive) yield break;
                var p = hit + dir * (0.2f + i * 0.12f) + Random.insideUnitSphere * 0.5f;
                CubeBlast(a, p, m.voxels + 1f, m.damage * 2.5f);
                FxHub.Instance?.ImpactRpc(p, -1);
                if (a.Def.name == "Rin" && i % 3 == 0) FxHub.Instance?.LightningRpc(new[] { p + Random.insideUnitSphere, p });
            }
        }

        IEnumerator CubeStorm(FighterState a, MoveDef m)
        {
            for (int i = 0; i < m.count; i++)
            {
                for (int k = 0; k < 5; k++)
                {
                    var r = Random.insideUnitCircle * 9f;
                    var top = a.transform.position + new Vector3(r.x, 22f, r.y);
                    if (!_cube || !_cube.Raycast(new Ray(top, Vector3.down), 60f, out _, out var p)) continue;
                    FxHub.Instance?.LightningRpc(new[] { top, Vector3.Lerp(top, p, 0.5f) + Random.insideUnitSphere, p });
                    CubeBlast(a, p, m.voxels + 2.5f, 90f, 2);
                }
                yield return new WaitForSeconds(0.28f);
                if (!a || !a.Alive) yield break;
            }
        }

        IEnumerator CubeZero(FighterState a, MoveDef m)
        {
            var c = a.Chest;
            DomeFxRpc(c, 6f, a.Def.aura, 2f);
            yield return new WaitForSeconds(1.8f);
            if (!a) yield break;
            CubeBlast(a, c, 13f, 160f, 3);
            FxHub.Instance?.ExplosionRpc(c, 3f, -1, false);
        }

        IEnumerator FlashStep(FighterState a, MoveDef m, Vector3 origin, Vector3 dir, Vector3 flat)
        {
            var t = TargetInFront(a, origin, dir, m.radius * a.Scale, 55f);
            var from = a.transform.position;
            if (t == null)
            {
                // nobody there: a straight blink forward (stopped by walls)
                float dist = 8f * a.Scale;
                if (_cube && _cube.Raycast(new Ray(a.Chest, flat), dist, out _, out var wall)) dist = Mathf.Max(0f, Vector3.Distance(a.Chest, wall) - 0.6f);
                var to = from + flat * dist;
                a.PlaceRpc(to, Quaternion.LookRotation(flat).eulerAngles.y);
                StreakFxRpc(from + Vector3.up * 0.6f, to + Vector3.up * 0.6f, a.Def.aura);
                yield break;
            }
            var behind = t.transform.position - t.Facing * 1.1f * t.Scale + Vector3.up * 0.1f;
            var face = t.transform.position - behind; face.y = 0;
            a.PlaceRpc(behind, Quaternion.LookRotation(face.sqrMagnitude > 0.01f ? face : flat).eulerAngles.y);
            StreakFxRpc(from + Vector3.up * 0.6f, behind + Vector3.up * 0.6f, a.Def.aura);
            yield return new WaitForSeconds(0.1f);
            if (t) ServerHit(a, t, m.damage, face.normalized * 8f + Vector3.up * 4f, 0.5f);
        }

        IEnumerator Rush(FighterState a, MoveDef m, Vector3 origin, Vector3 dir, Vector3 flat)
        {
            var t = TargetInFront(a, origin, dir, m.radius * a.Scale, 50f);
            var from = a.transform.position;
            if (t == null)
            {
                var to = from + flat * 6f * a.Scale;
                a.PlaceRpc(to, Quaternion.LookRotation(flat).eulerAngles.y);
                StreakFxRpc(from + Vector3.up * 0.6f, to + Vector3.up * 0.6f, a.Def.aura);
                yield break;
            }
            var toT = t.transform.position - from; toT.y = 0;
            var spot = t.transform.position - toT.normalized * 1.1f * t.Scale;
            a.PlaceRpc(spot, Quaternion.LookRotation(toT.sqrMagnitude > 0.01f ? toT : flat).eulerAngles.y);
            StreakFxRpc(from + Vector3.up * 0.6f, spot + Vector3.up * 0.6f, a.Def.aura);
            for (int i = 0; i < m.count; i++)
            {
                yield return new WaitForSeconds(0.07f);
                if (!t || !a || !a.Alive) yield break;
                bool last = i == m.count - 1;
                ServerHit(a, t, m.damage, last ? toT.normalized * 13f + Vector3.up * 5f : Vector3.up * 0.6f, last ? 0.2f : 0.25f);
                if (a.Def.name == "Rin" && i % 3 == 0) FxHub.Instance?.LightningRpc(new[] { t.Chest + Random.insideUnitSphere * 0.8f, t.Chest });
            }
        }

        IEnumerator Slam(FighterState a, MoveDef m, Vector3 flat)
        {
            float floor = SuperArena.TopY(Origin);
            if (brawl.Value && a.transform.position.y > floor + 1.8f)
            {
                a.KnockRpc(Vector3.down * 30f);
                yield return new WaitForSeconds(0.3f);
            }
            if (!a || !a.Alive) yield break;
            var c = a.transform.position;
            _cube?.ServerDamage(c + Vector3.down * 0.3f, m.voxels, 70f, Roster.VoxelTier(a.tier.Value) + 1, true, a.Id);
            foreach (var f in EnemiesOf(a).Where(f => Vector3.Distance(f.transform.position, c) < m.radius * a.Scale).ToList())
            {
                var away = f.transform.position - c; away.y = 0;
                float k = 1f - Mathf.Clamp01(away.magnitude / (m.radius * a.Scale));
                ServerHit(a, f, m.damage * (0.5f + 0.5f * k), away.normalized * (6f + 8f * k) + Vector3.up * (6f + 5f * k), 0.4f);
            }
            DomeFxRpc(c, m.radius * a.Scale, a.Def.aura, 0.4f);
        }

        IEnumerator Grab(FighterState a, MoveDef m, Vector3 origin, Vector3 dir, Vector3 flat)
        {
            var t = TargetInFront(a, origin, dir, m.radius * a.Scale, 80f);
            if (t == null) yield break;   // whiffed
            t.stunUntil = Time.time + 1.2f;
            // up we go together...
            a.KnockRpc(Vector3.up * 11f);
            t.PlaceRpc(a.transform.position + flat * 0.9f + Vector3.up * 0.4f, t.transform.eulerAngles.y);
            yield return null;
            t.KnockRpc(Vector3.up * 11.5f);
            yield return new WaitForSeconds(0.5f);
            if (!t || !a) yield break;
            // ...and down they go
            t.KnockRpc(Vector3.down * 28f + flat * 2f);
            a.KnockRpc(Vector3.down * 20f);
            yield return new WaitForSeconds(0.3f);
            if (!t || !a) yield break;
            ServerHit(a, t, m.damage, Vector3.up * 6f + flat * 4f, 0.6f, unblockable: true);
            _cube?.ServerDamage(t.transform.position + Vector3.down * 0.3f, m.voxels, 70f, Roster.VoxelTier(a.tier.Value) + 1, true, a.Id);
            DomeFxRpc(t.transform.position, 3f, a.Def.aura, 0.4f);
        }

        IEnumerator BotMeteor(FighterState a, MoveDef m, Vector3 origin, Vector3 dir)
        {
            var target = TargetInFront(a, origin, dir, 40f, 180f);
            var land = target ? target.transform.position : a.transform.position + a.Facing * 6f;
            a.PlaceRpc(a.transform.position + Vector3.up * 18f, a.transform.eulerAngles.y);
            yield return new WaitForSeconds(0.9f);
            if (!a || !a.Alive) yield break;
            a.PlaceRpc(land + Vector3.up * 0.3f, a.transform.eulerAngles.y);
            ServerMeteorImpact(a, land, m);
        }

        public void ServerMeteorImpact(FighterState a, Vector3 at, MoveDef m)
        {
            _cube?.ServerDamage(at + Vector3.down * 0.5f, m.voxels, 120f, 5, true, a.Id);
            foreach (var f in EnemiesOf(a).Where(f => Vector3.Distance(f.transform.position, at) < m.radius).ToList())
            {
                var away = f.transform.position - at; away.y = 0;
                float k = 1f - Mathf.Clamp01(away.magnitude / m.radius);
                ServerHit(a, f, m.damage * (0.4f + 0.6f * k), away.normalized * 12f + Vector3.up * 10f, 0.5f, unblockable: true);
            }
            DomeFxRpc(at, m.radius, a.Def.aura, 0.6f);
            FxHub.Instance?.ExplosionRpc(at, m.radius * 0.5f, -1, false);
        }

        IEnumerator Storm(FighterState a, MoveDef m)
        {
            for (int i = 0; i < m.count; i++)
            {
                foreach (var f in EnemiesOf(a).Where(f => Vector3.Distance(f.transform.position, a.transform.position) < m.radius).ToList())
                {
                    var top = f.Chest + Vector3.up * 14f + Random.insideUnitSphere * 2f;
                    FxHub.Instance?.LightningRpc(new[] { top, Vector3.Lerp(top, f.Chest, 0.5f) + Random.insideUnitSphere, f.Chest });
                    ServerHit(a, f, m.damage, Vector3.up * 2f, 0.35f, unblockable: true);
                    _cube?.ServerDamage(f.transform.position + Vector3.down * 0.2f, m.voxels, 40f, 4, true, a.Id);
                }
                yield return new WaitForSeconds(0.28f);
                if (!a || !a.Alive) yield break;
            }
        }

        IEnumerator AbsoluteZero(FighterState a, MoveDef m)
        {
            var c = a.transform.position;
            var hit = EnemiesOf(a).Where(f => Vector3.Distance(f.transform.position, c) < m.radius).ToList();
            foreach (var f in hit) ServerFreeze(f, 2.3f);
            DomeFxRpc(c, m.radius, a.Def.aura, 2f);
            yield return new WaitForSeconds(2.1f);
            if (!a) yield break;
            foreach (var f in hit)
            {
                if (!f || !f.Alive) continue;
                var away = f.transform.position - c; away.y = 0;
                f.frozenUntil.Value = 0; f.stunUntil = 0;
                ServerHit(a, f, m.damage, away.normalized * 7f + Vector3.up * 10f, 0.3f, unblockable: true);
                _cube?.ServerDamage(f.transform.position, m.voxels, 60f, 4, true, a.Id);
                FxHub.Instance?.ExplosionRpc(f.Chest, 1.5f, -1, true);
            }
        }

        // ================================================================== projectiles

        class Shot
        {
            public int id; public FighterState owner; public MoveDef m;
            public Vector3 pos, vel; public float radius, damage, explode, carve, range, travelled, carveAcc;
            public bool pierce, unblockable; public byte look; public Color color;
            public readonly HashSet<FighterState> hit = new HashSet<FighterState>();
        }
        readonly List<Shot> _shots = new List<Shot>();
        int _shotId;

        void SpawnShot(FighterState a, MoveDef m, Vector3 pos, Vector3 vel, float radius, float damage, float explodeRadius = 0f,
                       bool pierce = false, float carve = 0f, float range = 45f, byte look = 0, bool unblockable = false)
        {
            Shots++;
            var sh = new Shot
            {
                id = ++_shotId, owner = a, m = m, pos = pos, vel = vel, radius = radius, damage = damage, explode = explodeRadius,
                pierce = pierce, carve = carve, range = range, look = look, color = a.Def.aura, unblockable = unblockable,
            };
            _shots.Add(sh);
            ShotRpc(sh.id, look, pos, vel, radius, sh.color);
        }

        void StepShots(float dt)
        {
            for (int i = _shots.Count - 1; i >= 0; i--)
            {
                var sh = _shots[i];
                if (sh.owner == null) { _shots.RemoveAt(i); ShotEndRpc(sh.id, sh.pos, 0, sh.color); continue; }
                float step = sh.vel.magnitude * dt;
                var dir = sh.vel.normalized;
                bool done = false;
                Vector3 endAt = sh.pos;
                // fighters along the way
                foreach (var f in EnemiesOf(sh.owner).ToList())
                {
                    if (sh.hit.Contains(f)) continue;
                    float d = DistToSegment(f.Chest, sh.pos, sh.pos + dir * step);
                    if (d > sh.radius + 0.45f * f.Scale) continue;
                    if (sh.pierce)
                    {
                        sh.hit.Add(f);
                        ServerHit(sh.owner, f, sh.damage, dir * 10f + Vector3.up * 5f, 0.4f, sh.unblockable);
                    }
                    else { done = true; endAt = f.Chest; break; }
                }
                // the arena
                if (!done && _cube)
                {
                    if (sh.carve > 0)
                    {
                        sh.carveAcc += step;
                        if (sh.carveAcc >= 0.7f)
                        {
                            sh.carveAcc = 0;
                            _cube.ServerCarveBeam(sh.pos, dir, step + 0.7f, sh.carve, 60f, Roster.VoxelTier(sh.owner.tier.Value) + 1, sh.owner.Id);
                            if (sh.look == 5) _cube.ServerCarveBeam(sh.pos + Vector3.up * 2.5f, dir, step + 0.7f, sh.carve, 60f, 5, sh.owner.Id);
                        }
                    }
                    else if (_cube.Raycast(new Ray(sh.pos, dir), step + sh.radius * 0.5f, out _, out var p)) { done = true; endAt = p; }
                }
                sh.pos += dir * step;
                sh.travelled += step;
                if (!done && sh.travelled >= sh.range) { done = true; endAt = sh.pos; }
                if (!done) continue;
                _shots.RemoveAt(i);
                Explode(sh, endAt);
            }
        }

        void Explode(Shot sh, Vector3 at)
        {
            float r = sh.explode > 0 ? sh.explode : sh.radius;
            if (_cube && sh.carve <= 0) _cube.ServerDamage(at, sh.m.voxels, 70f, Roster.VoxelTier(sh.owner.tier.Value) + 1, true, sh.owner.Id);
            if (!sh.pierce)
                foreach (var f in EnemiesOf(sh.owner).Where(f => Vector3.Distance(f.Chest, at) < r + 0.45f * f.Scale).ToList())
                {
                    var away = f.Chest - at; float k = 1f - Mathf.Clamp01(away.magnitude / (r + 0.5f));
                    ServerHit(sh.owner, f, sh.damage * (0.55f + 0.45f * k), away.normalized * (5f + 7f * k) + Vector3.up * (3f + 3f * k), 0.3f, sh.unblockable);
                }
            ShotEndRpc(sh.id, at, r, sh.color);
        }

        // ================================================================== beams + clashes

        class Beam
        {
            public FighterState owner; public MoveDef m; public Vector3 origin, dir;
            public float until, charge, lastAim; public Beam rival; public float meter, clashStart;
        }
        readonly List<Beam> _beams = new List<Beam>();
        /// <summary>Client ids currently in a beam clash (for the MASH prompt).</summary>
        public readonly HashSet<ulong> Clashing = new HashSet<ulong>();

        void StartBeam(FighterState a, MoveDef m, Vector3 origin, Vector3 dir, float charge)
        {
            foreach (var old in _beams.Where(b => b.owner == a).ToList()) EndBeam(old);
            _beams.Add(new Beam { owner = a, m = m, origin = origin, dir = dir, charge = Mathf.Clamp01(charge), until = Time.time + Roster.BeamFire, lastAim = Time.time });
        }

        public void ServerBeamAim(FighterState a, Vector3 origin, Vector3 dir)
        {
            var b = _beams.FirstOrDefault(x => x.owner == a);
            if (b == null || dir.sqrMagnitude < 1e-4f) return;
            if (Vector3.Distance(origin, a.Chest) > 3f * a.Scale) origin = a.Chest;
            b.origin = origin; b.dir = dir.normalized; b.lastAim = Time.time;
        }

        public void ServerBeamEnd(FighterState a) { foreach (var b in _beams.Where(x => x.owner == a).ToList()) EndBeam(b); }

        public bool HasBeam(FighterState a) => _beams.Any(b => b.owner == a);

        void EndBeam(Beam b)
        {
            _beams.Remove(b);
            if (b.rival != null) { b.rival.rival = null; EndClash(b, b.rival); }
            BeamEndRpc(b.owner ? b.owner.NetworkObjectId : 0);
        }

        void TickBeams()
        {
            bool timeStop = timeStopUntil.Value > Now;
            foreach (var b in _beams.ToList())
            {
                if (!_beams.Contains(b)) continue;
                if (b.owner == null || !b.owner.Alive || Time.time > b.until || (b.owner.Frozen(Now) && !(timeStop && timeStopper.Value == b.owner.Id))) { EndBeam(b); continue; }
                if (b.owner.Bot == null && Time.time - b.lastAim > 0.6f) { EndBeam(b); continue; }   // the owner stopped sending
                BeamTicks++;
                float len = (b.m.length > 0 ? b.m.length : Roster.BeamLength) * b.owner.Scale;
                var a = b.owner;

                // ---- clash: two beams meeting head on
                if (b.rival == null)
                    foreach (var o in _beams)
                    {
                        if (o == b || o.rival != null || !Enemies(a, o.owner)) continue;
                        if (Vector3.Dot(b.dir, o.dir) > -0.55f) continue;
                        float gap = SegmentGap(b.origin, b.origin + b.dir * len, o.origin, o.origin + o.dir * len);
                        if (gap > 1.8f) continue;
                        b.rival = o; o.rival = b; b.meter = o.meter = 0; b.clashStart = o.clashStart = Time.time;
                        b.until = o.until = Time.time + 6f;
                        Clashes++;
                        ClashRpc(a.Id, o.owner.Id, true);
                        Callout("BEAM CLASH!  MASH ATTACK!", AeroPalette.Gold);
                        break;
                    }
                if (b.rival != null)
                {
                    var o = b.rival;
                    if (!ReferenceEquals(o.rival, b)) { b.rival = null; continue; }
                    if (a.NetworkObjectId > o.owner.NetworkObjectId) continue;   // the pair resolves once per tick
                    // bots mash too
                    if (a.Bot) b.meter += Random.Range(0.3f, 0.75f) * a.Bot.Skill;
                    if (o.owner.Bot) b.meter -= Random.Range(0.3f, 0.75f) * o.owner.Bot.Skill;
                    float k = Mathf.Clamp(0.5f + b.meter * 0.02f, 0.1f, 0.9f);
                    var point = Vector3.Lerp(b.origin, o.origin, k);
                    BeamFxRpc(b.origin, (point - b.origin).normalized, Vector3.Distance(b.origin, point), b.m.radius * a.Scale, a.Def.aura);
                    BeamFxRpc(o.origin, (point - o.origin).normalized, Vector3.Distance(o.origin, point), o.m.radius * o.owner.Scale, o.owner.Def.aura);
                    ClashFxRpc(point, a.Def.aura, o.owner.Def.aura);
                    _cube?.ServerDamage(point, 2.5f, 40f, 5, true, a.Id);
                    bool timeout = Time.time - b.clashStart > 4.5f;
                    if (Mathf.Abs(b.meter) >= 25f || timeout)
                    {
                        var win = b.meter >= 0 ? b : o; var lose = win == b ? o : b;
                        win.rival = null; lose.rival = null;
                        EndClash(b, o);
                        _beams.Remove(lose); BeamEndRpc(lose.owner.NetworkObjectId);
                        win.until = Time.time + 1.2f;
                        ServerHit(win.owner, lose.owner, 300f, win.dir * 14f + Vector3.up * 6f, 0.6f, unblockable: true);
                        Callout($"{win.owner.DisplayName} wins the beam clash!", win.owner.Def.aura);
                    }
                    continue;
                }

                // ---- a normal beam: everything along it gets hit, the arena gets carved
                BeamFxRpc(b.origin, b.dir, len, b.m.radius * a.Scale, a.Def.aura);
                float dmg = b.m.damage * (0.55f + 0.45f * b.charge);
                foreach (var f in EnemiesOf(a).ToList())
                {
                    float d = DistToSegment(f.Chest, b.origin, b.origin + b.dir * len);
                    if (d > b.m.radius * a.Scale + 0.5f * f.Scale) continue;
                    if (ServerHit(a, f, dmg, b.dir * 1.6f + Vector3.up * 0.4f, 0.12f, beam: true) && a.Def.name == "Yuki")
                        f.stunUntil = Mathf.Max(f.stunUntil, Time.time + 0.3f);
                }
                _cube?.ServerCarveBeam(b.origin + b.dir * 0.6f, b.dir, len, b.m.voxels * (0.7f + 0.3f * b.charge), 45f, Roster.VoxelTier(a.tier.Value) + 1, a.Id);
                if (a.Bot == null) a.KnockRpc(-b.dir * 0.35f);   // recoil
            }
        }

        void EndClash(Beam a, Beam b)
        {
            if (a?.owner) Clashing.Remove(a.owner.Id);
            if (b?.owner) Clashing.Remove(b.owner.Id);
            ClashRpc(a?.owner ? a.owner.Id : ulong.MaxValue, b?.owner ? b.owner.Id : ulong.MaxValue, false);
        }

        public void ServerMash(FighterState a)
        {
            var b = _beams.FirstOrDefault(x => x.owner == a && x.rival != null);
            if (b == null) return;
            float push = 1f + 0.15f * (a.tier.Value - b.rival.owner.tier.Value);
            // the meter lives on the pair's "first" beam
            if (a.NetworkObjectId <= b.rival.owner.NetworkObjectId) b.meter += push; else b.rival.meter -= push;
        }

        static float SegmentGap(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            // closest distance between two segments (sampled; plenty for beams)
            float best = float.MaxValue;
            for (int i = 0; i <= 24; i++)
            {
                var p = Vector3.Lerp(p1, q1, i / 24f);
                best = Mathf.Min(best, DistToSegment(p, p2, q2));
            }
            return best;
        }

        // ================================================================== hazards (Hurricane) + meteors

        class Hazard { public FighterState owner; public MoveDef m; public Vector3 pos, vel; public float until; }
        readonly List<Hazard> _hazards = new List<Hazard>();
        readonly Dictionary<FighterState, float> _meteors = new Dictionary<FighterState, float>();

        void TickHazards()
        {
            for (int i = _hazards.Count - 1; i >= 0; i--)
            {
                var h = _hazards[i];
                if (h.owner == null || Time.time > h.until) { _hazards.RemoveAt(i); continue; }
                h.pos += h.vel * 0.25f;
                if (brawl.Value) h.pos.y = SuperArena.TopY(Origin);
                foreach (var f in EnemiesOf(h.owner).Where(f => Vector3.Distance(f.transform.position.WithY(0), h.pos.WithY(0)) < h.m.radius).ToList())
                {
                    var pull = h.pos - f.transform.position; pull.y = 0;
                    ServerHit(h.owner, f, h.m.damage, pull.normalized * 4f + Vector3.up * 6f, 0.3f, beam: true);
                }
                for (int k = 0; k < 3; k++) _cube?.ServerDamage(h.pos + Vector3.up * (0.5f + k * 2f), h.m.voxels, 30f, 3, false, h.owner.Id);
                TornadoFxRpc(h.pos, h.m.radius, h.owner.Def.aura);
            }
        }

        // ================================================================== owner RPCs

        FighterState Caller(RpcParams p) =>
            PlayerAvatar.ByClient.TryGetValue(p.Receive.SenderClientId, out var pl) ? pl.GetComponent<FighterState>() : null;

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void MeleeRpc(byte kind, Vector3 origin, Vector3 dir, RpcParams p = default) { var f = Caller(p); if (f) ServerMelee(f, kind, origin, dir); }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void PunchRpc(Vector3 fist, Vector3 vel, RpcParams p = default) { var f = Caller(p); if (f) ServerPunch(f, fist, vel); }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void SpecialRpc(byte slot, Vector3 origin, Vector3 dir, float charge, RpcParams p = default)
        {
            var f = Caller(p);
            if (f && !ServerSpecial(f, slot, origin, dir, charge)) DeniedRpc(slot, RpcTarget.Single(p.Receive.SenderClientId, RpcTargetUse.Temp));
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void BeamAimRpc(Vector3 origin, Vector3 dir, RpcParams p = default) { var f = Caller(p); if (f) ServerBeamAim(f, origin, dir); }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void BeamStopRpc(RpcParams p = default) { var f = Caller(p); if (f) ServerBeamEnd(f); }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void MashRpc(RpcParams p = default) { var f = Caller(p); if (f) ServerMash(f); }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void TransformRpc(RpcParams p = default) { var f = Caller(p); if (f) ServerTransform(f); }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void MeteorImpactRpc(Vector3 at, RpcParams p = default)
        {
            var f = Caller(p);
            if (f == null || !_meteors.TryGetValue(f, out var until) || Time.time > until) return;
            _meteors.Remove(f);
            if (Vector3.Distance(at, f.transform.position) > 6f) at = f.transform.position;
            ServerMeteorImpact(f, at, f.Def.Ult);
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void DeniedRpc(byte slot, RpcParams p = default) => FighterController.Local?.OnDenied(slot);

        // ================================================================== effects (every peer)

        [Rpc(SendTo.ClientsAndHost)]
        void CalloutRpc(string text, Color c) => UI.GameMenus.Instance?.Toast(text, c);

        [Rpc(SendTo.ClientsAndHost)]
        void SpecialCalloutRpc(ulong netId, string move)
        {
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(netId, out var no)) return;
            no.GetComponent<FighterVisual>()?.Shout(move);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void HitFxRpc(Vector3 pos, Color c, float damage, bool blocked)
        {
            var fx = FxHub.Instance; if (!fx) return;
            fx.Burst(pos, blocked ? 4 : Mathf.Clamp((int)(damage / 8f), 5, 30), blocked ? Color.white : c);
            if (damage > 90f && !blocked) fx.BubbleLocal(pos, 0.4f + damage / 400f, c, 0.2f);
            FxHub.Play(fx.hitClip, pos, blocked ? 0.35f : Mathf.Clamp(0.35f + damage / 200f, 0.4f, 1f), blocked ? 1.7f : Random.Range(0.75f, 1.05f));
            var me = PlayerAvatar.Local;
            if (me && Vector3.Distance(me.transform.position, pos) < 4f) UI.GameMenus.Instance?.Shake(Mathf.Clamp(damage / 400f, 0.05f, 0.5f));
            HitSparks.Spawn(pos, damage, blocked, c);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void ParryFxRpc(Vector3 pos)
        {
            var fx = FxHub.Instance; if (!fx) return;
            fx.BubbleLocal(pos, 0.9f, Color.white, 0.25f);
            fx.Burst(pos, 20, new Color(1f, 1f, 0.6f));
            FxHub.Play(fx.zapClip, pos, 0.9f, 2f);
            HitSparks.Text(pos, "PARRY!", AeroPalette.Gold);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void KoFxRpc(Vector3 pos)
        {
            var fx = FxHub.Instance; if (!fx) return;
            fx.BubbleLocal(pos, 2.5f, Color.white, 0.5f);
            fx.Burst(pos, 40, AeroPalette.Gold);
            FxHub.Play(fx.boomClip, pos, 1f, 0.7f);
            HitSparks.Text(pos + Vector3.up * 0.6f, "K.O.!", AeroPalette.Danger, 2f);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void TransformFxRpc(ulong netId, byte tier)
        {
            if (!NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(netId, out var no)) return;
            no.GetComponent<FighterVisual>()?.PowerUpBurst(tier);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void StreakFxRpc(Vector3 from, Vector3 to, Color c)
        {
            var fx = FxHub.Instance; if (!fx) return;
            var go = new GameObject("Streak");
            var lr = go.AddComponent<LineRenderer>();
            lr.material = fx.glowMaterial; lr.positionCount = 2; lr.SetPosition(0, from); lr.SetPosition(1, to);
            lr.widthMultiplier = 0.35f; lr.startColor = new Color(c.r, c.g, c.b, 0.1f); lr.endColor = new Color(c.r, c.g, c.b, 0.8f);
            Destroy(go, 0.18f);
            fx.Burst(to, 12, c);
            FxHub.Play(fx.zapClip, to, 0.6f, 2.2f);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void FlameFxRpc(Vector3 pos, Vector3 dir, Color c)
        {
            var fx = FxHub.Instance; if (!fx) return;
            for (int i = 0; i < 5; i++) fx.BubbleLocal(pos + dir * i * 0.8f, 0.5f + i * 0.1f, c, 0.35f);
            fx.Burst(pos, 25, c);
            FxHub.Play(fx.boomClip, pos, 0.5f, 1.6f);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void DomeFxRpc(Vector3 pos, float radius, Color c, float time)
        {
            var fx = FxHub.Instance; if (!fx) return;
            fx.BubbleLocal(pos, radius, c, time);
            fx.Burst(pos, 40, c);
            FxHub.Play(fx.boomClip, pos, 0.8f, 0.8f);
            var me = PlayerAvatar.Local;
            if (me) UI.GameMenus.Instance?.Shake(Mathf.Clamp01(1f - Vector3.Distance(me.transform.position, pos) / (radius * 3f)) * 0.6f);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void TornadoFxRpc(Vector3 pos, float radius, Color c)
        {
            var fx = FxHub.Instance; if (!fx) return;
            for (int i = 0; i < 4; i++) fx.BubbleLocal(pos + Vector3.up * (i * 1.6f), radius * (0.35f + i * 0.15f), new Color(c.r, c.g, c.b, 0.5f), 0.3f);
            fx.Burst(pos + Vector3.up * 2f, 8, c);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void TimeStopFxRpc(Vector3 pos, float secs)
        {
            var fx = FxHub.Instance; if (!fx) return;
            fx.BubbleLocal(pos, 40f, new Color(0.7f, 0.7f, 0.75f), 0.8f);
            FxHub.Play(fx.zapClip, pos, 1f, 0.3f);
            UI.GameMenus.Instance?.TimeStopTint(secs);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void ClashRpc(ulong a, ulong b, bool on)
        {
            if (on) { Clashing.Add(a); Clashing.Add(b); }
            else { Clashing.Remove(a); Clashing.Remove(b); }
        }

        [Rpc(SendTo.ClientsAndHost)]
        void ClashFxRpc(Vector3 point, Color a, Color b)
        {
            var fx = FxHub.Instance; if (!fx) return;
            fx.BubbleLocal(point, Random.Range(1.2f, 2f), Color.Lerp(a, b, Random.value), 0.14f);
            fx.Burst(point, 10, Random.value < 0.5f ? a : b);
            var me = PlayerAvatar.Local;
            if (me && Vector3.Distance(me.transform.position, point) < 30f) UI.GameMenus.Instance?.Shake(0.15f);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void BeamFxRpc(Vector3 origin, Vector3 dir, float len, float radius, Color c)
        {
            var fx = FxHub.Instance; if (!fx || len < 0.05f) return;
            var core = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(core.GetComponent<Collider>());
            core.GetComponent<MeshRenderer>().material = new Material(fx.glowMaterial) { color = new Color(1f, 1f, 1f, 0.9f) };
            core.transform.SetPositionAndRotation(origin + dir * len * 0.5f, Quaternion.FromToRotation(Vector3.up, dir));
            core.transform.localScale = new Vector3(radius * 1.1f, len * 0.5f, radius * 1.1f);
            var glow = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(glow.GetComponent<Collider>());
            glow.GetComponent<MeshRenderer>().material = new Material(fx.glowMaterial) { color = new Color(c.r, c.g, c.b, 0.55f) };
            glow.transform.SetPositionAndRotation(core.transform.position, core.transform.rotation);
            glow.transform.localScale = new Vector3(radius * 2.4f, len * 0.5f, radius * 2.4f);
            Destroy(core, 0.13f); Destroy(glow, 0.13f);
            fx.Burst(origin + dir * Mathf.Min(len, 2f), 4, c);
            if (Time.frameCount % 3 == 0) FxHub.Play(fx.zapClip, origin, 0.5f, 0.55f);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void BeamEndRpc(ulong netId)
        {
            if (NetworkManager.SpawnManager.SpawnedObjects.TryGetValue(netId, out var no)) no.GetComponent<FighterController>()?.OnBeamEnded();
        }

        // ---- shot visuals: every peer moves its own copy in a straight line

        class ShotVis { public Transform t; public Vector3 vel; public float spin; }
        readonly Dictionary<int, ShotVis> _vis = new Dictionary<int, ShotVis>();

        [Rpc(SendTo.ClientsAndHost)]
        void ShotRpc(int id, byte look, Vector3 pos, Vector3 vel, float radius, Color c)
        {
            var fx = FxHub.Instance; if (!fx) return;
            var root = new GameObject("SuperShot").transform;
            root.position = pos;
            root.rotation = Quaternion.LookRotation(vel.sqrMagnitude > 0.01f ? vel : Vector3.forward);
            PrimitiveType shape = look == 1 || look == 5 ? PrimitiveType.Cube : look == 2 ? PrimitiveType.Capsule : PrimitiveType.Sphere;
            var core = GameObject.CreatePrimitive(shape);
            Destroy(core.GetComponent<Collider>());
            core.transform.SetParent(root, false);
            var mat = look == 3 ? null : new Material(fx.glowMaterial) { color = new Color(c.r, c.g, c.b, 0.85f) };
            if (mat) core.GetComponent<MeshRenderer>().material = mat;
            else core.GetComponent<MeshRenderer>().material.color = new Color(0.5f, 0.42f, 0.32f);   // a boulder
            switch (look)
            {
                case 1: core.transform.localScale = new Vector3(radius * 2.2f, 0.25f, 0.6f); core.transform.localRotation = Quaternion.Euler(0, 0, 10); break;  // crescent
                case 5: core.transform.localScale = new Vector3(radius * 2f, 0.4f, 1.2f); break;                                                               // eclipse cut
                case 2: core.transform.localScale = new Vector3(radius * 1.2f, radius * 2.4f, radius * 1.2f); core.transform.localRotation = Quaternion.Euler(90, 0, 0); break;
                default: core.transform.localScale = Vector3.one * radius * 2f; break;
            }
            if (look == 0 || look == 4)
            {
                var halo = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(halo.GetComponent<Collider>());
                halo.transform.SetParent(root, false);
                halo.transform.localScale = Vector3.one * radius * 3.2f;
                halo.GetComponent<MeshRenderer>().material = new Material(fx.glowMaterial) { color = new Color(c.r, c.g, c.b, 0.3f) };
            }
            _vis[id] = new ShotVis { t = root, vel = vel, spin = look == 2 || look == 3 ? 720f : 0f };
            FxHub.Play(look == 4 ? fx.boomClip : fx.zapClip, pos, look == 4 ? 0.9f : 0.5f, look == 4 ? 0.4f : 1.3f);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void ShotEndRpc(int id, Vector3 pos, float radius, Color c)
        {
            if (_vis.TryGetValue(id, out var v)) { if (v.t) Destroy(v.t.gameObject); _vis.Remove(id); }
            var fx = FxHub.Instance; if (!fx || radius <= 0) return;
            fx.BubbleLocal(pos, radius, c, 0.35f + radius * 0.03f);
            fx.Burst(pos, Mathf.Clamp((int)(radius * 12f), 8, 60), c);
            FxHub.Play(fx.boomClip, pos, Mathf.Clamp01(0.4f + radius * 0.1f), Mathf.Lerp(1.4f, 0.6f, Mathf.Clamp01(radius / 9f)));
            var me = PlayerAvatar.Local;
            if (me) UI.GameMenus.Instance?.Shake(Mathf.Clamp01(1f - Vector3.Distance(me.transform.position, pos) / (radius * 5f + 4f)) * Mathf.Clamp01(radius / 3f));
        }

        void TickShotVisuals(float dt)
        {
            if (_vis.Count == 0) return;
            bool stopped = timeStopUntil.Value > Now;
            List<int> dead = null;
            foreach (var kv in _vis)
            {
                var v = kv.Value;
                if (!v.t) { (dead ??= new List<int>()).Add(kv.Key); continue; }
                if (stopped) continue;
                v.t.position += v.vel * dt;
                if (v.spin > 0) v.t.Rotate(0, 0, v.spin * dt, Space.Self);
                if ((v.t.position - Origin).sqrMagnitude > 200f * 200f) { Destroy(v.t.gameObject); (dead ??= new List<int>()).Add(kv.Key); }
            }
            if (dead != null) foreach (var d in dead) _vis.Remove(d);
        }

        // ================================================================== local world

        GameObject _kiosk;

        void SyncWorld(bool on)
        {
            if (_kiosk == null) _kiosk = GameObject.Find("ShopKiosk");
            if (_kiosk && (Siege.TitanDirector.Instance == null || !Siege.TitanDirector.Instance.IsActive)) _kiosk.SetActive(!on);
            if (!on)
            {
                foreach (var v in _vis.Values) if (v.t) Destroy(v.t.gameObject);
                _vis.Clear();
                Clashing.Clear();
            }
        }
    }
}
