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
    /// God Siege mode, server-authoritative. The Commander(s) (PC/Mac/Mobile) share a purse that
    /// fills over time and buy units that march on the God's cube; free auto-waves grow stronger.
    /// The God (VR) spends mana on skills and grabs/throws units.
    /// Commander wins by destroying every Core voxel; God wins by surviving until the timer runs out.
    /// All numbers come from Resources/Balance/siege.json (tuned with Tools/balance_sim.py siege).
    /// </summary>
    public class SiegeDirector : NetworkBehaviour
    {
        public static SiegeDirector Instance { get; private set; }

        /// <summary>Same order as Balance.Siege.units. Each: NetworkObject, NetworkTransform (server auth),
        /// Rigidbody, CapsuleCollider, EnemyUnit, child "Visual" with the unit model.</summary>
        public GameObject[] unitPrefabs;
        /// <summary>3 portals ~22 m from the cube, on walkable ground.</summary>
        public Transform[] spawnPortals;

        public const int MaxLiveUnits = 60;
        /// <summary>Instigator id used for voxel damage dealt by units (not a real client id).</summary>
        public const ulong UnitInstigator = ulong.MaxValue - 1;

        public readonly NetworkVariable<bool> active = new NetworkVariable<bool>();
        public readonly NetworkVariable<float> money = new NetworkVariable<float>();
        public readonly NetworkVariable<float> xp = new NetworkVariable<float>();
        public readonly NetworkVariable<int> era = new NetworkVariable<int>(1);
        public readonly NetworkVariable<float> mana = new NetworkVariable<float>();
        public readonly NetworkVariable<float> timeLeft = new NetworkVariable<float>();
        public readonly NetworkVariable<int> wave = new NetworkVariable<int>();
        public readonly NetworkVariable<float> nextWaveIn = new NetworkVariable<float>();
        public readonly NetworkVariable<float> timeSlowLeft = new NetworkVariable<float>();
        public readonly NetworkVariable<int> shieldsActive = new NetworkVariable<int>();
        public readonly NetworkVariable<int> liveUnits = new NetworkVariable<int>();
        public NetworkList<float> skillCooldowns;

        struct Shield { public Vector3 pos; public float radius; public float until; }
        readonly List<Shield> _shields = new List<Shield>();
        readonly Dictionary<ulong, float> _nextSlap = new Dictionary<ulong, float>();
        VoxelCube _cube;

        public static SiegeFile S => Balance.Siege;
        public float Income => S.commander.baseIncome + S.commander.incomePerEra * (era.Value - 1);
        public float UnitTimeScale => timeSlowLeft.Value > 0 ? 0.4f : 1f;
        public bool IsActive => active.Value;

        /// <summary>XP needed for the next era (or -1 at max era).</summary>
        public float NextEraXp => era.Value < S.commander.eraXp.Length ? S.commander.eraXp[era.Value] : -1f;
        public float CurrentEraXp => S.commander.eraXp[Mathf.Clamp(era.Value - 1, 0, S.commander.eraXp.Length - 1)];

        void Awake()
        {
            Instance = this;
            skillCooldowns = new NetworkList<float>();
        }

        public override void OnNetworkSpawn()
        {
            if (IsServer && skillCooldowns.Count == 0)
                for (int i = 0; i < S.skills.Length; i++) skillCooldowns.Add(0f);
        }

        public SkillDef Skill(string id)
        {
            foreach (var s in S.skills) if (s.id == id) return s;
            return null;
        }

        public float Cooldown(int skillIndex) => skillIndex >= 0 && skillIndex < skillCooldowns.Count ? skillCooldowns[skillIndex] : 0f;

        // ------------------------------------------------------------------ lifecycle (server)

        public void ServerBegin(VoxelCube cube)
        {
            if (!IsServer) return;
            ServerDespawnAllUnits();
            _cube = cube;
            _shields.Clear();
            _nextSlap.Clear();
            money.Value = S.commander.startMoney;
            xp.Value = 0;
            era.Value = 1;
            mana.Value = S.god.maxMana;
            timeLeft.Value = S.matchSeconds;
            wave.Value = 0;
            nextWaveIn.Value = S.commander.autoWaveInterval;
            timeSlowLeft.Value = 0;
            shieldsActive.Value = 0;
            for (int i = 0; i < skillCooldowns.Count; i++) skillCooldowns[i] = 0f;
            cube.DamageFilter = IsShielded;
            active.Value = true;
        }

        public void ServerEnd()
        {
            if (!IsServer) return;
            active.Value = false;
            foreach (var kv in new List<KeyValuePair<ulong, bool>>(_heldPlayers))
                if (PlayerAvatar.ByClient.TryGetValue(kv.Key, out var held)) held.ReleasedRpc(Vector3.zero);
            _heldPlayers.Clear();
            _skillShots.Clear();
            if (_cube != null) _cube.DamageFilter = null;
            _cube = null;
            _shields.Clear();
            shieldsActive.Value = 0;
            ServerDespawnAllUnits();
        }

        void ServerDespawnAllUnits()
        {
            foreach (var u in EnemyUnit.Live.ToArray())
                if (u != null && u.IsSpawned) u.NetworkObject.Despawn(true);
            liveUnits.Value = 0;
        }

        bool IsShielded(int cellIndex)
        {
            if (_shields.Count == 0 || _cube == null) return false;
            var world = _cube.CellToWorld(_cube.Grid.Coord(cellIndex));
            foreach (var s in _shields)
                if ((world - s.pos).sqrMagnitude <= s.radius * s.radius) return true;
            return false;
        }

        void Update()
        {
            if (!IsServer || !active.Value) return;
            float dt = Time.deltaTime;
            var C = S.commander;

            // timers & economy
            timeLeft.Value = Mathf.Max(0, timeLeft.Value - dt);
            money.Value += Income * dt;
            while (era.Value < C.eraXp.Length && xp.Value >= C.eraXp[era.Value]) era.Value++;

            // god mana + cooldowns
            // +15% regen for every Raider on the field (the Minion Caster doesn't count)
            int raiders = 0;
            foreach (var pl in PlayerAvatar.All) if (pl.role.Value == PlayerRole.Raider) raiders++;
            mana.Value = Mathf.Min(S.god.maxMana, mana.Value + S.god.manaRegen * (1f + S.god.manaRegenPerRaider * raiders) * dt);
            for (int i = 0; i < skillCooldowns.Count; i++)
                if (skillCooldowns[i] > 0) skillCooldowns[i] = Mathf.Max(0, skillCooldowns[i] - dt);
            if (timeSlowLeft.Value > 0) timeSlowLeft.Value = Mathf.Max(0, timeSlowLeft.Value - dt);
            ServerTickSkillShots(dt);
            if (_shields.Count > 0)
            {
                _shields.RemoveAll(s => Time.time >= s.until);
                shieldsActive.Value = _shields.Count;
            }

            // automatic waves (free units, growing budget)
            nextWaveIn.Value -= dt;
            if (nextWaveIn.Value <= 0)
            {
                nextWaveIn.Value = C.autoWaveInterval;
                ServerAutoWave();
            }
            liveUnits.Value = EnemyUnit.Live.Count;

            // win conditions
            var mm = MatchManager.Instance;
            if (_cube != null && _cube.IsSpawned && _cube.coreAlive.Value <= 0)
                mm.ServerEndMatch($"The Core shattered at {MatchManager.FormatTime(S.matchSeconds - timeLeft.Value)}! The Commander wins!");
            else if (timeLeft.Value <= 0)
                mm.ServerEndMatch($"The God held the cube through {wave.Value} waves! The God wins!");
        }

        void ServerAutoWave()
        {
            var C = S.commander;
            float budget = C.autoWaveBaseBudget * Mathf.Pow(C.autoWaveGrowth, wave.Value);
            wave.Value++;
            int portal = Random.Range(0, Mathf.Max(1, PortalCount));
            var avail = new List<UnitDef>();
            foreach (var u in S.units) if (u.era <= era.Value) avail.Add(u);
            int guard = 0;
            while (budget > 0 && guard++ < 100)
            {
                var affordable = avail.FindAll(u => u.cost <= budget);
                if (affordable.Count == 0) break;
                var pick = affordable[Random.Range(0, affordable.Count)];
                if (!ServerSpawnUnit(pick, portal)) break;
                budget -= pick.cost;
            }
        }

        int PortalCount => spawnPortals != null && spawnPortals.Length > 0 ? spawnPortals.Length : 3;

        public Vector3 PortalPosition(int portal)
        {
            if (spawnPortals != null && spawnPortals.Length > 0)
                return spawnPortals[Mathf.Clamp(portal, 0, spawnPortals.Length - 1)].position;
            // fallback: three points around the cube
            var c = _cube != null ? _cube.transform.position : (VoxelCube.Instance ? VoxelCube.Instance.transform.position : Vector3.zero);
            float a = portal * Mathf.PI * 2f / 3f + Mathf.PI * 0.5f;
            return c + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 22f + Vector3.up * 0.5f;
        }

        bool ServerSpawnUnit(UnitDef u, int portal)
        {
            if (EnemyUnit.Live.Count >= MaxLiveUnits) return false;
            if (unitPrefabs == null || u.index >= unitPrefabs.Length || unitPrefabs[u.index] == null)
            {
                Debug.LogError($"SiegeDirector: missing unit prefab for {u.id}");
                return false;
            }
            var pos = PortalPosition(portal) + new Vector3(Random.Range(-1.5f, 1.5f), 0.3f, Random.Range(-1.5f, 1.5f));
            var cube = _cube ? _cube.transform.position : Vector3.zero;
            var rot = Quaternion.LookRotation(Vector3.ProjectOnPlane(cube - pos, Vector3.up).normalized + Vector3.forward * 1e-4f);
            var go = Instantiate(unitPrefabs[u.index], pos, rot);
            go.GetComponent<EnemyUnit>().ServerInit(u.index);
            go.GetComponent<NetworkObject>().Spawn(true);
            liveUnits.Value = EnemyUnit.Live.Count;
            return true;
        }

        // ------------------------------------------------------------------ hooks (server)

        public void ServerOnUnitDestroyedVoxels(int count, int money)
        {
            if (!IsServer || !active.Value) return;
            xp.Value += count * S.commander.xpPerVoxel;
        }

        public void ServerOnUnitKilled(EnemyUnit u, bool byGod)
        {
            if (!IsServer || !active.Value || !byGod) return;
            mana.Value = Mathf.Min(S.god.maxMana, mana.Value + S.god.manaPerKill);
        }

        static bool SenderHasRole(RpcParams p, PlayerRole role, out PlayerAvatar pl)
        {
            return PlayerAvatar.ByClient.TryGetValue(p.Receive.SenderClientId, out pl) && pl.role.Value == role;
        }

        // ------------------------------------------------------------------ commander RPCs

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void BuyUnitRpc(int unitIndex, int portal, RpcParams p = default)
        {
            if (!active.Value || !SenderHasRole(p, PlayerRole.Commander, out _)) return;
            if (unitIndex < 0 || unitIndex >= S.units.Length) return;
            var u = S.units[unitIndex];
            if (u.era > era.Value || money.Value < u.cost) return;
            if (!ServerSpawnUnit(u, portal)) return;
            money.Value -= u.cost;
            xp.Value += u.cost * S.commander.xpPerMoneySpent;
        }

        // ------------------------------------------------------------------ god RPCs

        bool TrySpend(SkillDef s, float cost)
        {
            if (s == null || skillCooldowns[s.index] > 0 || mana.Value < cost) return false;
            mana.Value -= cost;
            skillCooldowns[s.index] = s.cooldown;
            return true;
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void CastSkillRpc(int skillIndex, Vector3 point, RpcParams p = default)
        {
            if (!active.Value || !SenderHasRole(p, PlayerRole.God, out _)) return;
            if (skillIndex < 0 || skillIndex >= S.skills.Length) return;
            if (float.IsNaN(point.x) || float.IsInfinity(point.x)) return;
            var s = S.skills[skillIndex];
            if (s.id == "Grab") return;   // grabbing goes through GrabRpc
            if (!TrySpend(s, s.mana)) return;
            var fx = FxHub.Instance;
            switch (s.id)
            {
                case "Smite":
                    EnemyUnit.ServerBlast(point, s.radiusM, s.damage, 6f, byGod: true);
                    if (fx) { fx.TracerRpc(point + Vector3.up * 40f, point, -1, ulong.MaxValue); fx.ExplosionRpc(point, s.radiusM, -1, true); }
                    break;
                case "Shield":
                    _shields.Add(new Shield { pos = point, radius = s.radiusM, until = Time.time + s.duration });
                    shieldsActive.Value = _shields.Count;
                    ShieldFxRpc(point, s.radiusM, s.duration);
                    break;
                case "Regrow":
                    if (_cube) _cube.ServerRegrow(point, s.radiusM, 60);
                    if (fx) fx.ShockwaveRpc(point, s.radiusM);
                    break;
                case "Meteor":
                    MeteorFxRpc(point, s.radiusM, 1.2f);
                    StartCoroutine(MeteorImpact(point, s, 1.2f));
                    break;
                case "TimeSlow":
                    timeSlowLeft.Value = s.duration;
                    break;
            }
        }

        IEnumerator MeteorImpact(Vector3 point, SkillDef s, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (!active.Value) yield break;
            // Comet Drop hurts units only, never your own cube
            EnemyUnit.ServerBlast(point, s.radiusM, s.damage, 16f, byGod: true);
            FxHub.Instance?.ExplosionRpc(point, s.radiusM, -1, false);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void GrabRpc(NetworkObjectReference unitRef, bool left, RpcParams p = default)
        {
            if (!active.Value || !SenderHasRole(p, PlayerRole.God, out var god)) return;
            if (!unitRef.TryGet(out NetworkObject no) || !no.TryGetComponent(out EnemyUnit unit)) return;
            var def = unit.Def;
            if (def == null || unit.state.Value == UnitState.Held) return;
            unit.ServerGrab(god, left);   // grabbing is the God's default move: free, anything goes
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ReleaseRpc(NetworkObjectReference unitRef, Vector3 velocity, RpcParams p = default)
        {
            if (!SenderHasRole(p, PlayerRole.God, out var god)) return;
            if (!unitRef.TryGet(out NetworkObject no) || !no.TryGetComponent(out EnemyUnit unit)) return;
            if (unit.Holder != god) return;
            unit.ServerRelease(Vector3.ClampMagnitude(velocity, 35f));
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void SlapRpc(NetworkObjectReference unitRef, Vector3 handVelocity, RpcParams p = default)
        {
            if (!active.Value || !SenderHasRole(p, PlayerRole.God, out var god)) return;
            if (!unitRef.TryGet(out NetworkObject no) || !no.TryGetComponent(out EnemyUnit unit)) return;
            if (unit.state.Value == UnitState.Held) return;
            ulong id = p.Receive.SenderClientId;
            if (_nextSlap.TryGetValue(id, out var t) && Time.time < t) return;
            _nextSlap[id] = Time.time + S.god.handSlapCooldown;
            // sanity: the hand must actually be near the unit
            float reach = 0.6f * god.Motor.WorldScale + 1.5f;
            if (Vector3.Distance(god.leftHand.position, unit.transform.position) > reach &&
                Vector3.Distance(god.rightHand.position, unit.transform.position) > reach) return;
            unit.ServerHit(S.god.handSlapDamage, Vector3.ClampMagnitude(handVelocity, 20f) * 0.6f + Vector3.up * 3f, byGod: true);
        }

        public static int Mashes, PlayerGrabs, PlayerSwats, SkillThrows, SkillHits, Waves, Claps;   // -autotest

        /// <summary>A hand slammed straight down onto a unit: squashed flat.</summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void MashRpc(NetworkObjectReference unitRef, RpcParams p = default)
        {
            if (!active.Value || !SenderHasRole(p, PlayerRole.God, out var god)) return;
            if (!unitRef.TryGet(out NetworkObject no) || !no.TryGetComponent(out EnemyUnit unit) || unit.state.Value == UnitState.Held) return;
            float reach = 0.6f * god.Motor.WorldScale + 1.5f;
            if (Vector3.Distance(god.leftHand.position, unit.transform.position) > reach &&
                Vector3.Distance(god.rightHand.position, unit.transform.position) > reach) return;
            Mashes++;
            FxHub.Instance?.ShockwaveRpc(unit.transform.position, 1.2f);
            unit.ServerTakeDamage(1e6f, byGod: true);
        }

        // ------------------------------------------------------------------ the God grabs, swats and squashes players

        readonly Dictionary<ulong, bool> _heldPlayers = new Dictionary<ulong, bool>();   // client -> left hand

        static bool Grabbable(PlayerAvatar pl) => pl != null && (pl.role.Value == PlayerRole.Raider || pl.role.Value == PlayerRole.Miner);

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void GrabPlayerRpc(ulong client, bool left, RpcParams p = default)
        {
            if (!active.Value || !SenderHasRole(p, PlayerRole.God, out var god)) return;
            if (!PlayerAvatar.ByClient.TryGetValue(client, out var pl) || !Grabbable(pl) || _heldPlayers.ContainsKey(client)) return;
            var hand = left ? god.leftHand : god.rightHand;
            if (Vector3.Distance(hand.position, pl.Motor.Chest) > 0.5f * god.Motor.WorldScale + 2f) return;
            _heldPlayers[client] = left;
            PlayerGrabs++;
            pl.HeldRpc(god.OwnerClientId, left);
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ReleasePlayerRpc(ulong client, Vector3 velocity, RpcParams p = default)
        {
            if (!SenderHasRole(p, PlayerRole.God, out _) || !_heldPlayers.Remove(client)) return;
            if (PlayerAvatar.ByClient.TryGetValue(client, out var pl)) pl.ReleasedRpc(Vector3.ClampMagnitude(velocity, 40f));
        }

        public bool IsHeld(ulong client) => _heldPlayers.ContainsKey(client);

        /// <summary>An open hand swatted a player (or slammed down on one): ragdoll them.</summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void SwatPlayerRpc(ulong client, Vector3 handVelocity, bool mash, RpcParams p = default)
        {
            if (!active.Value || !SenderHasRole(p, PlayerRole.God, out var god)) return;
            if (!PlayerAvatar.ByClient.TryGetValue(client, out var pl) || !Grabbable(pl) || _heldPlayers.ContainsKey(client)) return;
            ulong id = p.Receive.SenderClientId + 1000;
            if (_nextSlap.TryGetValue(id, out var t) && Time.time < t) return;
            _nextSlap[id] = Time.time + 0.3f;
            float reach = 0.6f * god.Motor.WorldScale + 1.5f;
            if (Vector3.Distance(god.leftHand.position, pl.Motor.Chest) > reach && Vector3.Distance(god.rightHand.position, pl.Motor.Chest) > reach) return;
            var v = Vector3.ClampMagnitude(handVelocity, 30f);
            // a squash pancakes them (straight down, then a bounce); a swat sends them flying
            var knock = mash ? new Vector3(v.x * 0.2f, 14f, v.z * 0.2f) : v * 0.9f + Vector3.up * 5f;
            PlayerSwats++;
            pl.KnockbackRpc(knock.normalized * Mathf.Max(knock.magnitude, 16f));
            FxHub.Instance?.ShockwaveRpc(pl.Motor.Chest, mash ? 2f : 1f);
        }

        // ------------------------------------------------------------------ thrown skills (gestures)

        /// <summary>Thrown skills: Zeus Bolt, Sun Bomb, Hail of Blades, Bubble Ward, Spring Bloom, Comet Drop.</summary>
        public static bool IsThrown(string id) => id == "Smite" || id == "SunBomb" || id == "BladeHail" || id == "Shield" || id == "Regrow" || id == "Meteor";

        class SkillShot { public int id; public SkillDef s; public Vector3 pos, vel; public float age; public int pierce; public Transform vis; }
        readonly List<SkillShot> _skillShots = new List<SkillShot>();
        readonly Dictionary<int, SkillShot> _skillVis = new Dictionary<int, SkillShot>();
        int _skillShotId;

        static float SkillGravity(string id) => id == "Smite" ? 2f : id == "BladeHail" ? 3f : id == "Meteor" ? 14f : 9.81f;

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void ThrowSkillRpc(int skillIndex, Vector3 origin, Vector3 velocity, RpcParams p = default)
        {
            if (!active.Value || !SenderHasRole(p, PlayerRole.God, out var god)) return;
            if (skillIndex < 0 || skillIndex >= S.skills.Length || float.IsNaN(velocity.x) || float.IsNaN(origin.x)) return;
            var s = S.skills[skillIndex];
            if (!IsThrown(s.id) || Vector3.Distance(origin, god.headAnchor.position) > 2f * god.Motor.WorldScale) return;
            if (!TrySpend(s, s.mana)) return;
            ServerThrowSkill(s, origin, velocity);
        }

        /// <summary>Server: launch a thrown skill (also used by -autotest).</summary>
        public void ServerThrowSkill(SkillDef s, Vector3 origin, Vector3 velocity)
        {
            velocity = Vector3.ClampMagnitude(velocity, 70f);
            SkillThrows++;
            if (s.id == "BladeHail")
            {
                // a fan of ten blades around the throw
                var right = Vector3.Cross(Vector3.up, velocity).normalized;
                for (int i = 0; i < 10; i++)
                {
                    float a = (i - 4.5f) * 4.5f;
                    var v = Quaternion.AngleAxis(a, Vector3.up) * Quaternion.AngleAxis(Random.Range(-4f, 4f), right) * velocity;
                    AddSkillShot(s, origin, v, 3);
                }
                return;
            }
            AddSkillShot(s, origin, velocity, 0);
        }

        void AddSkillShot(SkillDef s, Vector3 origin, Vector3 vel, int pierce)
        {
            int id = ++_skillShotId;
            _skillShots.Add(new SkillShot { id = id, s = s, pos = origin, vel = vel, pierce = pierce });
            SkillShotRpc(id, s.index, origin, vel);
        }

        void ServerTickSkillShots(float dt)
        {
            for (int i = _skillShots.Count - 1; i >= 0; i--)
            {
                var sh = _skillShots[i];
                var from = sh.pos;
                sh.vel += Vector3.down * SkillGravity(sh.s.id) * dt;
                sh.pos += sh.vel * dt;
                sh.age += dt;
                bool end = sh.age > 6f || sh.pos.y < -15f;
                Vector3 hitAt = sh.pos;
                // world / cube
                var seg = sh.pos - from;
                if (!end && seg.sqrMagnitude > 1e-6f)
                {
                    var ray = new Ray(from, seg.normalized);
                    if (_cube && _cube.Raycast(ray, seg.magnitude, out _, out var vp)) { end = true; hitAt = vp; }
                    else if (Physics.Raycast(ray, out var rh, seg.magnitude, ~(1 << 5), QueryTriggerInteraction.Ignore)
                             && rh.collider.GetComponentInParent<PlayerAvatar>() == null && rh.collider.GetComponentInParent<EnemyUnit>() == null)
                    { end = true; hitAt = rh.point; }
                }
                // units in the path (blades cut through a few)
                if (!end)
                    foreach (var u in EnemyUnit.Live.ToArray())
                    {
                        if (!u || !u.IsSpawned || u.state.Value == UnitState.Held) continue;
                        if (Vector3.Distance(u.transform.position + Vector3.up * 0.5f, sh.pos) > 0.9f) continue;
                        if (sh.s.id == "BladeHail" && sh.pierce > 0)
                        {
                            sh.pierce--;
                            u.ServerHit(sh.s.damage, sh.vel.normalized * 4f + Vector3.up * 2f, byGod: true);
                            SkillHits++;
                            continue;
                        }
                        end = true; hitAt = sh.pos; break;
                    }
                // Raiders in the path
                if (!end)
                    foreach (var pl in PlayerAvatar.All)
                        if (Grabbable(pl) && Vector3.Distance(pl.Motor.Chest, sh.pos) < 0.9f) { end = true; hitAt = sh.pos; break; }
                if (!end) continue;
                _skillShots.RemoveAt(i);
                SkillImpact(sh, hitAt);
            }
        }

        void SkillImpact(SkillShot sh, Vector3 at)
        {
            var s = sh.s; var fx = FxHub.Instance;
            SkillShotEndRpc(sh.id, at);
            switch (s.id)
            {
                case "Smite":
                    EnemyUnit.ServerBlast(at, s.radiusM, s.damage, 6f, byGod: true);
                    KnockRaiders(at, s.radiusM * 1.4f, 12f);
                    if (fx) { fx.LightningRpc(new[] { at + Vector3.up * 30f + Random.insideUnitSphere * 3f, at + Vector3.up * 12f, at }); fx.ExplosionRpc(at, s.radiusM * 0.6f, -1, true); }
                    break;
                case "SunBomb":
                    EnemyUnit.ServerBlast(at, s.radiusM, s.damage, 12f, byGod: true);
                    KnockRaiders(at, s.radiusM * 1.5f, 18f);
                    if (fx) fx.ExplosionRpc(at, s.radiusM, -1, false);
                    break;
                case "BladeHail":
                    KnockRaiders(at, 1.2f, 8f);
                    if (fx) fx.ImpactRpc(at, -1);
                    break;
                case "Shield":
                    _shields.Add(new Shield { pos = at, radius = s.radiusM, until = Time.time + s.duration });
                    shieldsActive.Value = _shields.Count;
                    ShieldFxRpc(at, s.radiusM, s.duration);
                    break;
                case "Regrow":
                    if (_cube) _cube.ServerRegrow(at, s.radiusM, 60);
                    if (fx) fx.ShockwaveRpc(at, s.radiusM);
                    break;
                case "Meteor":
                    EnemyUnit.ServerBlast(at, s.radiusM, s.damage, 16f, byGod: true);   // never your own cube
                    KnockRaiders(at, s.radiusM * 1.3f, 20f);
                    if (fx) fx.ExplosionRpc(at, s.radiusM, -1, false);
                    break;
            }
            SkillHits++;
        }

        void KnockRaiders(Vector3 at, float radius, float knock)
        {
            foreach (var pl in PlayerAvatar.All)
            {
                if (!Grabbable(pl) || _heldPlayers.ContainsKey(pl.OwnerClientId)) continue;
                var d = pl.Motor.Chest - at;
                if (d.magnitude > radius) continue;
                pl.KnockbackRpc((d.normalized + Vector3.up * 0.7f).normalized * knock * (1f - d.magnitude / radius * 0.5f));
            }
        }

        /// <summary>Sun Wave (kamehameha): a beam from both hands. Charge 0..1 sets width and damage.</summary>
        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void SunWaveRpc(Vector3 origin, Vector3 dir, float charge, RpcParams p = default)
        {
            if (!active.Value || !SenderHasRole(p, PlayerRole.God, out var god) || float.IsNaN(dir.x) || dir.sqrMagnitude < 0.01f) return;
            if (Vector3.Distance(origin, god.headAnchor.position) > 2f * god.Motor.WorldScale) return;
            var s = Skill("SunWave");
            if (!TrySpend(s, s.mana)) return;
            ServerSunWave(origin, dir.normalized, Mathf.Clamp01(charge));
        }

        public void ServerSunWave(Vector3 origin, Vector3 dir, float charge)
        {
            var s = Skill("SunWave");
            float width = s.radiusM * (0.6f + 1.4f * charge), len = 70f, dmg = s.damage * (0.4f + 0.6f * charge);
            Waves++;
            foreach (var u in EnemyUnit.Live.ToArray())
            {
                if (!u || !u.IsSpawned) continue;
                var rel = u.transform.position + Vector3.up * 0.5f - origin;
                float along = Vector3.Dot(rel, dir);
                if (along < 0 || along > len || (rel - dir * along).magnitude > width) continue;
                u.ServerHit(dmg, dir * 10f + Vector3.up * 4f, byGod: true);
                SkillHits++;
            }
            foreach (var pl in PlayerAvatar.All)
            {
                if (!Grabbable(pl) || _heldPlayers.ContainsKey(pl.OwnerClientId)) continue;
                var rel = pl.Motor.Chest - origin;
                float along = Vector3.Dot(rel, dir);
                if (along < 0 || along > len || (rel - dir * along).magnitude > width + 0.5f) continue;
                pl.KnockbackRpc(dir * 20f + Vector3.up * 6f);
            }
            SunWaveFxRpc(origin, dir, width, len);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void SunWaveFxRpc(Vector3 origin, Vector3 dir, float width, float len) => StartCoroutine(SunWaveFx(origin, dir, width, len));

        static IEnumerator SunWaveFx(Vector3 origin, Vector3 dir, float width, float len)
        {
            var beam = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(beam.GetComponent<Collider>());
            beam.name = "SunWave";
            var mr = beam.GetComponent<MeshRenderer>();
            if (FxHub.Instance) mr.sharedMaterial = FxHub.Instance.glowMaterial;
            var block = new MaterialPropertyBlock();
            beam.transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
            for (float e = 0; e < 0.9f && beam; e += Time.deltaTime)
            {
                float grow = Mathf.Min(1f, e * 6f), fade = 1f - Mathf.Clamp01((e - 0.5f) / 0.4f);
                float l = len * grow;
                beam.transform.position = origin + dir * l * 0.5f;
                beam.transform.localScale = new Vector3(width * 2f * fade, l * 0.5f, width * 2f * fade);
                block.SetColor("_BaseColor", new Color(1f, 0.9f, 0.45f, 0.75f * fade));
                mr.SetPropertyBlock(block);
                yield return null;
            }
            if (beam) Destroy(beam);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void SkillShotRpc(int id, int skillIndex, Vector3 pos, Vector3 vel)
        {
            if (skillIndex < 0 || skillIndex >= S.skills.Length) return;
            var s = S.skills[skillIndex];
            var vis = SkillVisual(s.id);
            vis.position = pos;
            _skillVis[id] = new SkillShot { id = id, s = s, pos = pos, vel = vel, vis = vis };
        }

        [Rpc(SendTo.ClientsAndHost)]
        void SkillShotEndRpc(int id, Vector3 at)
        {
            if (_skillVis.TryGetValue(id, out var sh)) { if (sh.vis) Destroy(sh.vis.gameObject); _skillVis.Remove(id); }
        }

        void LateUpdate()
        {
            if (_skillVis.Count == 0) return;
            float dt = Time.deltaTime;
            foreach (var sh in _skillVis.Values)
            {
                if (!sh.vis) continue;
                sh.vel += Vector3.down * SkillGravity(sh.s.id) * dt;
                sh.pos += sh.vel * dt;
                sh.vis.position = sh.pos;
                if (sh.vel.sqrMagnitude > 0.01f) sh.vis.rotation = Quaternion.LookRotation(sh.vel);
                if (sh.s.id == "BladeHail") sh.vis.Rotate(0, 0, 1080f * dt, Space.Self);
            }
        }

        /// <summary>What a skill looks like in the God's hand and in flight.</summary>
        public static Transform SkillVisual(string id, float scale = 2.5f)
        {
            var root = new GameObject("Skill_" + id).transform;
            root.localScale = Vector3.one * scale;
            Material glow = FxHub.Instance ? FxHub.Instance.glowMaterial : null;
            void Piece(PrimitiveType t, Vector3 pos, Vector3 euler, Vector3 scale, Color c)
            {
                var g = GameObject.CreatePrimitive(t);
                Destroy(g.GetComponent<Collider>());
                g.transform.SetParent(root, false);
                g.transform.localPosition = pos; g.transform.localEulerAngles = euler; g.transform.localScale = scale;
                var mr = g.GetComponent<MeshRenderer>();
                if (glow) mr.sharedMaterial = glow;
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var b = new MaterialPropertyBlock(); b.SetColor("_BaseColor", c); mr.SetPropertyBlock(b);
            }
            switch (id)
            {
                case "Smite":   // a zig-zag bolt
                    var y = new Color(1f, 0.95f, 0.45f, 1f);
                    Piece(PrimitiveType.Cube, new Vector3(0, 0, 0.35f), new Vector3(0, 25, 0), new Vector3(0.12f, 0.12f, 0.55f), y);
                    Piece(PrimitiveType.Cube, new Vector3(0.1f, 0, 0), new Vector3(0, -30, 0), new Vector3(0.12f, 0.12f, 0.5f), y);
                    Piece(PrimitiveType.Cube, new Vector3(0, 0, -0.35f), new Vector3(0, 25, 0), new Vector3(0.12f, 0.12f, 0.5f), y);
                    break;
                case "SunBomb":
                    Piece(PrimitiveType.Sphere, Vector3.zero, Vector3.zero, Vector3.one * 0.6f, new Color(1f, 0.6f, 0.2f, 1f));
                    Piece(PrimitiveType.Sphere, Vector3.zero, Vector3.zero, Vector3.one * 0.85f, new Color(1f, 0.85f, 0.3f, 0.35f));
                    break;
                case "BladeHail":
                    Piece(PrimitiveType.Cube, Vector3.zero, Vector3.zero, new Vector3(0.05f, 0.18f, 0.7f), new Color(0.8f, 0.95f, 1f, 1f));
                    break;
                case "Shield":
                    Piece(PrimitiveType.Sphere, Vector3.zero, Vector3.zero, Vector3.one * 0.6f, new Color(0.55f, 0.95f, 1f, 0.45f));
                    break;
                case "Regrow":
                    Piece(PrimitiveType.Sphere, Vector3.zero, Vector3.zero, new Vector3(0.35f, 0.45f, 0.35f), new Color(0.45f, 0.9f, 0.35f, 1f));
                    break;
                case "Meteor":
                    Piece(PrimitiveType.Sphere, Vector3.zero, Vector3.zero, Vector3.one * 1.2f, new Color(1f, 0.55f, 0.25f, 1f));
                    break;
                default:
                    Piece(PrimitiveType.Sphere, Vector3.zero, Vector3.zero, Vector3.one * 0.4f, Color.white);
                    break;
            }
            return root;
        }

        // ------------------------------------------------------------------ client FX

        [Rpc(SendTo.ClientsAndHost)]
        void ShieldFxRpc(Vector3 pos, float radius, float duration)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(s.GetComponent<Collider>());
            s.name = "BubbleWard";
            s.transform.position = pos;
            s.transform.localScale = Vector3.one * radius * 2f;
            var mr = s.GetComponent<MeshRenderer>();
            if (FxHub.Instance) mr.sharedMaterial = FxHub.Instance.glowMaterial;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", new Color(0.55f, 0.95f, 1f, 0.22f));
            mr.SetPropertyBlock(block);
            StartCoroutine(Wobble(s.transform, radius, duration));
        }

        static IEnumerator Wobble(Transform t, float radius, float duration)
        {
            for (float e = 0; e < duration && t; e += Time.deltaTime)
            {
                float pop = Mathf.Min(1f, e * 4f);
                t.localScale = Vector3.one * radius * 2f * pop * (1f + 0.02f * Mathf.Sin(e * 6f));
                yield return null;
            }
            if (t) Destroy(t.gameObject);
        }

        [Rpc(SendTo.ClientsAndHost)]
        void MeteorFxRpc(Vector3 point, float radius, float delay)
        {
            StartCoroutine(MeteorFall(point, radius, delay));
        }

        static IEnumerator MeteorFall(Vector3 point, float radius, float delay)
        {
            var m = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(m.GetComponent<Collider>());
            m.name = "Comet";
            m.transform.localScale = Vector3.one * Mathf.Max(1f, radius * 0.4f);
            var mr = m.GetComponent<MeshRenderer>();
            if (FxHub.Instance) mr.sharedMaterial = FxHub.Instance.glowMaterial;
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", new Color(1f, 0.85f, 0.4f, 0.9f));
            mr.SetPropertyBlock(block);
            var start = point + new Vector3(12f, 60f, 8f);
            for (float e = 0; e < delay && m; e += Time.deltaTime)
            {
                float k = e / delay;
                m.transform.position = Vector3.Lerp(start, point, k * k);
                yield return null;
            }
            if (m) Destroy(m);
        }
    }
}
