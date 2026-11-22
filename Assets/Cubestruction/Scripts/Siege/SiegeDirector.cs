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
            mana.Value = Mathf.Min(S.god.maxMana, mana.Value + S.god.manaRegen * dt);
            for (int i = 0; i < skillCooldowns.Count; i++)
                if (skillCooldowns[i] > 0) skillCooldowns[i] = Mathf.Max(0, skillCooldowns[i] - dt);
            if (timeSlowLeft.Value > 0) timeSlowLeft.Value = Mathf.Max(0, timeSlowLeft.Value - dt);
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
            if (!def.grabbable && unit.hp.Value > def.hp * 0.3f) return;
            var grab = Skill("Grab");
            if (!TrySpend(grab, grab.mana * Mathf.Max(1f, def.mass))) return;
            unit.ServerGrab(god, left);
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
