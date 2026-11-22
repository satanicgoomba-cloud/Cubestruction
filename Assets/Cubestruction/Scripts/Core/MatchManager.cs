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

        public void ServerStartMatch(GameMode m)
        {
            if (!IsServer || state.Value == MatchState.Playing) return;
            if (_cube != null && _cube.IsSpawned) _cube.Despawn();
            mode.Value = m;
            elapsed.Value = 0;
            resultText.Value = "";
            var players = PlayerAvatar.All.ToList();

            bool titan = m == GameMode.TitanSiege;
            int n = m == GameMode.GodSiege ? Balance.Siege.cubeSize : titan ? Siege.TitanMap.Size : miningCubeSize;
            _cube = Instantiate(cubePrefab, pedestal ? pedestal.position : Vector3.zero, Quaternion.identity);
            var vc = _cube.GetComponent<VoxelCube>();
            // In siege the cube isn't mined by players, so no co-op scaling.
            if (titan) vc.ServerConfigure(n, 1, 1, Siege.TitanMap.CellSize);
            else vc.ServerConfigure(n, m == GameMode.Mining ? players.Count : 1);
            vc.OnCellsDestroyedServer += OnCellsDestroyed;
            if (m == GameMode.Mining) vc.ServerEnableLoot();
            _cube.Spawn(true);

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
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        public void RequestLobbyRpc(RpcParams p = default)
        {
            if (p.Receive.SenderClientId != NetworkManager.ServerClientId) return;
            if (state.Value == MatchState.Playing) { SiegeDirector.Instance?.ServerEnd(); Siege.TitanDirector.Instance?.ServerEnd(); }
            if (_cube != null && _cube.IsSpawned) _cube.Despawn();
            state.Value = MatchState.Lobby;
            foreach (var pl in PlayerAvatar.All) pl.ServerResetForMatch(PlayerRole.Miner);
        }
    }
}
