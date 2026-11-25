using System.Linq;
using Cubestruction.Player;
using Cubestruction.Siege;
using Cubestruction.Voxels;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction
{
    /// <summary>
    /// Scene-placed, server-authoritative match flow. Mining mode: everyone climbs and mines;
    /// the match ends the moment the last voxel is cleared. God Siege: see SiegeDirector.
    /// </summary>
    public class MatchManager : NetworkBehaviour
    {
        public static MatchManager Instance { get; private set; }

        public NetworkObject cubePrefab;
        public Transform pedestal;
        /// <summary>Mining cube edge in voxels: 68 = ~314k voxels, ~17 m tall (5x the original 40 = 64k).</summary>
        public const int DefaultMiningCubeSize = 68;
        [Tooltip("Cube size for Mining mode. 68 = ~314k voxels, 17 m.")] public int miningCubeSize = DefaultMiningCubeSize;

        public readonly NetworkVariable<GameMode> mode = new NetworkVariable<GameMode>(GameMode.Mining);
        public readonly NetworkVariable<MatchState> state = new NetworkVariable<MatchState>(MatchState.Lobby);
        public readonly NetworkVariable<float> elapsed = new NetworkVariable<float>();
        public readonly NetworkVariable<FixedString128Bytes> resultText = new NetworkVariable<FixedString128Bytes>();

        public System.Action<MatchState> OnStateChanged;
        /// <summary>-autotest: make the first player the Titan even without a headset.</summary>
        public static bool ForceHumanTitan;
        NetworkObject _cube;

        void Awake() { Instance = this; }

        public override void OnNetworkSpawn()
        {
            state.OnValueChanged += (_, s) => OnStateChanged?.Invoke(s);
        }

        void Update()
        {
            if (!IsServer || state.Value != MatchState.Playing) return;
            elapsed.Value += Time.deltaTime;
            var cube = VoxelCube.Instance;
            bool superCube = Roles.IsFighterMode(mode.Value) && Super.SuperDirector.Instance != null && !Super.SuperDirector.Instance.IsBrawl;
            if (superCube && cube != null && cube.IsSpawned && cube.alive.Value <= 0) { ServerEndMatch(Super.SuperDirector.Instance.ResultText()); return; }
            if (mode.Value == GameMode.Mining && cube != null && cube.IsSpawned && cube.alive.Value <= 0)
            {
                var best = PlayerAvatar.All.OrderByDescending(p => p.voxels.Value).FirstOrDefault();
                ServerEndMatch(best != null
                    ? $"Cube cleared in {FormatTime(elapsed.Value)}! MVP: {best.displayName.Value} ({best.voxels.Value} voxels)"
                    : "Cube cleared!");
            }
        }

        public static string FormatTime(float s) => $"{(int)s / 60}:{(int)s % 60:00}";

        // ------------------------------------------------------------------ server API

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestStartRpc(GameMode m, RpcParams p = default)
        {
            // only the host may start
            if (p.Receive.SenderClientId != NetworkManager.ServerClientId) return;
            ServerStartMatch(m);
        }

        public void ServerStartMatch(GameMode m) => ServerStartMatch(m, null);

        /// <summary>Start a match; with <paramref name="load"/>, rebuild the saved map and put the host back where they were.</summary>
        public void ServerStartMatch(GameMode m, SaveData load)
        {
            if (!IsServer || state.Value == MatchState.Playing) return;
            if (load != null) { m = load.Mode; if (Roles.IsFighterMode(m)) Super.SuperDirector.Rules = load.rules; }
            if (_cube != null && _cube.IsSpawned) _cube.Despawn();
            mode.Value = m;
            elapsed.Value = 0;
            resultText.Value = "";
            var players = PlayerAvatar.All.ToList();

            bool titan = m == GameMode.TitanSiege;
            bool fight = Roles.IsFighterMode(m);
            bool arena = fight && Super.SuperDirector.WantBrawl;   // SUPER! brawl: the voxel arena; otherwise break the Mining cube
            int n = m == GameMode.GodSiege ? Balance.Siege.cubeSize : titan ? Siege.TitanMap.SizeX : arena ? Super.SuperArena.SizeX : load != null && load.cubeSize > 0 ? load.cubeSize : miningCubeSize;
            _cube = Instantiate(cubePrefab, pedestal ? pedestal.position : Vector3.zero, Quaternion.identity);
            var vc = _cube.GetComponent<VoxelCube>();
            // In siege the cube isn't mined by players, so no co-op scaling.
            if (titan) vc.ServerConfigure(n, 1, 1, Siege.TitanMap.CellSize);
            else if (arena) vc.ServerConfigure(n, 1, 2, Super.SuperArena.CellSize);
            else vc.ServerConfigure(n, m == GameMode.Mining || fight ? players.Count : 1);
            vc.OnCellsDestroyedServer += OnCellsDestroyed;
            if (m == GameMode.Mining && load == null) vc.ServerEnableLoot();
            _cube.Spawn(true);
            if (load != null)
            {
                try { vc.ServerRestoreSolid(System.Convert.FromBase64String(load.solid)); }
                catch (System.Exception e) { Debug.LogWarning("[MatchManager] save map unreadable: " + e.Message); }
                elapsed.Value = load.elapsed;
            }

            // assign roles + reset economy
            foreach (var pl in players) pl.ServerResetForMatch(PlayerRole.Miner);
            var giant = players.FirstOrDefault(p => p.platform.Value == Platform.VR) ?? players.FirstOrDefault();
            if (titan)
            {
                // the Titan is the VR player; with no headset in the lobby it's the AI Titan (-autotest can force a person)
                var vr = players.FirstOrDefault(p => p.platform.Value == Platform.VR);
                giant = vr ?? (ForceHumanTitan ? players.FirstOrDefault() : null);
                Siege.TitanDirector.Instance.ServerBegin(vc, players, giant);
                state.Value = MatchState.Playing;
                return;
            }
            if (fight)
            {
                Super.SuperDirector.Instance.ServerBegin(vc, players, m == GameMode.Sandbox);
                if (arena)
                {
                    state.Value = MatchState.Playing;
                    if (load != null) ServerApplyHostSave(load);
                    return;
                }
            }
            if (m == GameMode.GodSiege)
            {
                // everyone else voted in the lobby: Raider (on foot) or Minion Caster; one caster, picked at random
                var others = players.Where(p => p != giant).ToList();
                var volunteers = others.Where(p => p.siegeVote.Value == 1).ToList();
                var caster = volunteers.Count > 0 ? volunteers[Random.Range(0, volunteers.Count)] : null;
                if (giant) giant.role.Value = PlayerRole.God;
                foreach (var pl in others)
                {
                    pl.role.Value = pl == caster ? PlayerRole.Commander : PlayerRole.Raider;
                    if (pl != caster) pl.ServerAddMoney(Balance.Siege.raiderStartMoney);
                }
                SiegeDirector.Instance.ServerBegin(vc);
            }

            // spread players around the cube
            float ring = vc.HalfExtent + 2.5f;
            for (int i = 0; i < players.Count; i++)
            {
                float a = i * Mathf.PI * 2f / Mathf.Max(1, players.Count);
                var pos = vc.transform.position + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * ring + Vector3.up * 0.2f;
                if (players[i].role.Value == PlayerRole.God) pos = vc.transform.position + new Vector3(0, 0, -(vc.HalfExtent + 10f));
                var look = Quaternion.LookRotation(Vector3.ProjectOnPlane(vc.transform.position - pos, Vector3.up));
                players[i].TeleportRpc(pos, look.eulerAngles.y);
            }
            state.Value = MatchState.Playing;
            if (load != null) ServerApplyHostSave(load);
        }

        // ------------------------------------------------------------------ save / load (host)

        /// <summary>The host's own state goes back in after a load (other players start fresh).</summary>
        void ServerApplyHostSave(SaveData d)
        {
            if (!PlayerAvatar.ByClient.TryGetValue(NetworkManager.ServerClientId, out var host)) return;
            if (d.Mode == GameMode.Mining)
            {
                host.money.Value = d.money; host.earned.Value = d.earned; host.voxels.Value = d.voxels;
                host.ownedTools.Value = d.ownedTools | (1UL << Balance.Tool("Pickaxe").index);
                if (d.charges != null) for (int i = 0; i < d.charges.Length && i < host.charges.Count; i++) host.charges[i] = d.charges[i];
            }
            var fs = host.GetComponent<Super.FighterState>();
            if (fs != null && Roles.IsFighterMode(d.Mode))
            {
                if (fs.IsOwner) fs.character.Value = (byte)Mathf.Clamp(d.fighter, 0, Super.Roster.All.Length - 1);
                if (d.Mode == GameMode.Super)
                {
                    fs.tier.Value = (byte)Mathf.Clamp(d.tier, 1, Super.Roster.MaxTier);
                    fs.power.Value = d.power;
                    fs.kos.Value = d.kos; fs.falls.Value = d.falls;
                    if (d.hp > 0) fs.hp.Value = Mathf.Min(fs.maxHp.Value, d.hp);
                    if (d.timeLeft > 0) Super.SuperDirector.Instance.timeLeft.Value = d.timeLeft;
                }
            }
            host.TeleportRpc(new Vector3(d.x, d.y, d.z) + Vector3.up * 0.1f, d.yaw);
        }

        /// <summary>Host: write the save slot from the match in progress. Returns an error message, or null.</summary>
        public string ServerSave()
        {
            if (!IsServer) return "Only the host can save.";
            if (state.Value != MatchState.Playing) return "Nothing to save yet.";
            if (!SaveGame.CanSave(mode.Value)) return "God Siege and Titan Siege can't be saved.";
            var cube = VoxelCube.Instance;
            if (cube == null || cube.Grid == null) return "Nothing to save yet.";
            var d = new SaveData
            {
                mode = (int)mode.Value, cubeSize = cube.size.Value, rules = Super.SuperDirector.Rules, solid = System.Convert.ToBase64String(cube.Grid.PackSolid()),
                alive = cube.alive.Value, total = cube.Total, elapsed = elapsed.Value,
            };
            if (PlayerAvatar.ByClient.TryGetValue(NetworkManager.ServerClientId, out var host))
            {
                var p = host.transform.position;
                d.x = p.x; d.y = p.y; d.z = p.z; d.yaw = host.transform.eulerAngles.y;
                d.money = host.money.Value; d.earned = host.earned.Value; d.voxels = host.voxels.Value; d.ownedTools = host.ownedTools.Value;
                d.charges = new int[host.charges.Count];
                for (int i = 0; i < d.charges.Length; i++) d.charges[i] = host.charges[i];
                var fs = host.GetComponent<Super.FighterState>();
                if (fs != null)
                {
                    d.fighter = fs.character.Value; d.tier = fs.tier.Value; d.power = fs.power.Value; d.hp = fs.hp.Value;
                    d.kos = fs.kos.Value; d.falls = fs.falls.Value;
                }
                if (Super.SuperDirector.Instance != null) d.timeLeft = Super.SuperDirector.Instance.timeLeft.Value;
            }
            try { SaveGame.Write(d); }
            catch (System.Exception e) { return "Couldn't save: " + e.Message; }
            return null;
        }

        void OnCellsDestroyed(int count, ulong instigator, int money)
        {
            if (PlayerAvatar.ByClient.TryGetValue(instigator, out var pl))
            {
                pl.ServerAddMoney(money);
                pl.voxels.Value += count;
            }
            else if (mode.Value == GameMode.GodSiege)
            {
                SiegeDirector.Instance.ServerOnUnitDestroyedVoxels(count, money);
            }
        }

        public void ServerEndMatch(string message)
        {
            if (!IsServer || state.Value != MatchState.Playing) return;
            resultText.Value = message;
            state.Value = MatchState.Ended;
            if (mode.Value == GameMode.GodSiege) SiegeDirector.Instance.ServerEnd();
            if (mode.Value == GameMode.TitanSiege) Siege.TitanDirector.Instance?.ServerEnd();
            if (Roles.IsFighterMode(mode.Value)) Super.SuperDirector.Instance?.ServerEnd();
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestLobbyRpc(RpcParams p = default)
        {
            if (p.Receive.SenderClientId != NetworkManager.ServerClientId) return;
            if (state.Value == MatchState.Playing) { SiegeDirector.Instance?.ServerEnd(); Siege.TitanDirector.Instance?.ServerEnd(); Super.SuperDirector.Instance?.ServerEnd(); }
            if (_cube != null && _cube.IsSpawned) _cube.Despawn();
            state.Value = MatchState.Lobby;
            foreach (var pl in PlayerAvatar.All) pl.ServerResetForMatch(PlayerRole.Miner);
        }
    }
}
