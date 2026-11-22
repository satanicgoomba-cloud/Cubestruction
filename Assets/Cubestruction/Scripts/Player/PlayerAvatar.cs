using System.Collections.Generic;
using Cubestruction.Tools;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.XR;

namespace Cubestruction.Player
{
    /// <summary>
    /// Root of the networked player prefab. Holds identity, platform, role, wallet and inventory.
    /// The owner decides its platform (VR / desktop / mobile) and enables the matching local rig.
    ///
    /// Prefab layout (built by Editor/CubestructionProjectBuilder):
    ///   Player [NetworkObject, PlayerAvatar, OwnerNetworkTransform, CharacterController, PlayerMotor,
    ///           ToolController, PlayerRagdoll, VRRig(disabled), FlatRig(disabled)]
    ///     ├─ Visual           (Buddy model: Head, Torso, ArmL, ArmR, LegL, LegR) [AvatarVisual]
    ///     ├─ HeadAnchor       [OwnerNetworkTransform]  (camera is parented here for the owner)
    ///     ├─ LeftHand         [OwnerNetworkTransform]  └─ Socket
    ///     └─ RightHand        [OwnerNetworkTransform]  └─ Socket
    /// </summary>
    public class PlayerAvatar : NetworkBehaviour
    {
        public static readonly List<PlayerAvatar> All = new List<PlayerAvatar>();
        public static readonly Dictionary<ulong, PlayerAvatar> ByClient = new Dictionary<ulong, PlayerAvatar>();
        public static PlayerAvatar Local { get; private set; }

        public Transform headAnchor, leftHand, rightHand;

        // owner-written identity
        public readonly NetworkVariable<Platform> platform = new NetworkVariable<Platform>(Platform.Desktop, writePerm: NetworkVariableWritePermission.Owner);
        public readonly NetworkVariable<FixedString32Bytes> displayName = new NetworkVariable<FixedString32Bytes>("Buddy", writePerm: NetworkVariableWritePermission.Owner);
        public readonly NetworkVariable<byte> colorIndex = new NetworkVariable<byte>(0, writePerm: NetworkVariableWritePermission.Owner);
        /// <summary>God hands: bit 0/1 left/right grip, bit 2/3 left/right trigger (curls the fingers for everyone).</summary>
        public readonly NetworkVariable<byte> handPose = new NetworkVariable<byte>(0, writePerm: NetworkVariableWritePermission.Owner);
        /// <summary>God Siege lobby vote for non-VR players: 0 = Raider (on foot), 1 = Minion Caster.</summary>
        public readonly NetworkVariable<byte> siegeVote = new NetworkVariable<byte>(0, writePerm: NetworkVariableWritePermission.Owner);
        public readonly NetworkVariable<float> avatarScale = new NetworkVariable<float>(1f, writePerm: NetworkVariableWritePermission.Owner);

        // server-written game state
        public readonly NetworkVariable<PlayerRole> role = new NetworkVariable<PlayerRole>(PlayerRole.Miner);
        public readonly NetworkVariable<int> money = new NetworkVariable<int>();
        public readonly NetworkVariable<int> earned = new NetworkVariable<int>();
        public readonly NetworkVariable<int> voxels = new NetworkVariable<int>();
        public readonly NetworkVariable<ulong> ownedTools = new NetworkVariable<ulong>();
        public NetworkList<int> charges;
        /// <summary>Titan Siege: cannonballs and rockets dug out of the mine wall (carried to turrets / fired by the RPG).</summary>
        public readonly NetworkVariable<int> ammoBalls = new NetworkVariable<int>();
        public readonly NetworkVariable<int> ammoRockets = new NetworkVariable<int>();

        public PlayerMotor Motor { get; private set; }
        public ToolController Tools { get; private set; }
        public PlayerRagdoll Ragdoll { get; private set; }
        public bool IsVR => platform.Value == Platform.VR;
        public Color Tint => AeroPalette.BuddyColors[colorIndex.Value % AeroPalette.BuddyColors.Length];

        public System.Action OnInventoryChanged;

        void Awake()
        {
            charges = new NetworkList<int>();
            Motor = GetComponent<PlayerMotor>();
            Tools = GetComponent<ToolController>();
            Ragdoll = GetComponent<PlayerRagdoll>();
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            ByClient[OwnerClientId] = this;
            if (IsServer)
            {
                charges.Clear();
                for (int i = 0; i < Balance.Tools.Length; i++) charges.Add(0);
                ServerResetForMatch(PlayerRole.Miner);
            }
            ownedTools.OnValueChanged += (_, __) => OnInventoryChanged?.Invoke();
            charges.OnListChanged += _ => OnInventoryChanged?.Invoke();
            role.OnValueChanged += (_, r) => ApplyRole(r);

            if (IsOwner)
            {
                Local = this;
                bool vr = XRSettings.isDeviceActive && !System.Environment.GetCommandLineArgs().Contains("-novr");
                platform.Value = vr ? Platform.VR : (Application.isMobilePlatform ? Platform.Mobile : Platform.Desktop);
                displayName.Value = GameSettings.PlayerName;
                colorIndex.Value = (byte)GameSettings.ColorIndex;
                if (vr) GetComponent<VRRig>().enabled = true;
                else GetComponent<FlatRig>().enabled = true;
            }
            ApplyRole(role.Value);
        }

        public override void OnNetworkDespawn()
        {
            All.Remove(this);
            ByClient.Remove(OwnerClientId);
            if (Local == this) Local = null;
        }

        void ApplyRole(PlayerRole r)
        {
            // God players are giants; commanders hide their body and use an RTS camera.
            if (!IsOwner) return;
            var god = GetComponent<Siege.GodController>();
            var cmd = GetComponent<Siege.CommanderController>();
            var titan = GetComponent<Siege.TitanController>();
            var def = GetComponent<Siege.DefenderController>();
            if (god) god.enabled = r == PlayerRole.God;
            if (cmd) cmd.enabled = r == PlayerRole.Commander;
            if (titan) titan.enabled = r == PlayerRole.Titan;
            if (def) def.enabled = r == PlayerRole.Defender;
            // (the Titan starts Buddy-sized; TitanController grows it when it bites its hand)
            Motor.WorldScale = r == PlayerRole.God ? Balance.Siege.god.scale : 1f;
        }

        // ------------------------------------------------------------------ inventory helpers

        public bool Owns(int toolIndex) => (ownedTools.Value & (1UL << toolIndex)) != 0;
        public int Charges(int toolIndex) => toolIndex < charges.Count ? charges[toolIndex] : 0;
        public bool CanUse(ToolDef t) => t.consumable ? Charges(t.index) > 0 : Owns(t.index);

        public void ServerResetForMatch(PlayerRole r)
        {
            if (!IsServer) return;
            role.Value = r;
            money.Value = 0; earned.Value = 0; voxels.Value = 0;
            ownedTools.Value = 1UL << Balance.Tool("Pickaxe").index;
            for (int i = 0; i < charges.Count; i++) charges[i] = 0;
        }

        public void ServerAddMoney(int amount)
        {
            money.Value += amount;
            earned.Value += amount;
        }

        public bool ServerConsumeCharge(int toolIndex)
        {
            if (charges[toolIndex] <= 0) return false;
            charges[toolIndex]--;
            return true;
        }

        [Rpc(SendTo.Server)]
        public void BuyRpc(int toolIndex)
        {
            var t = Balance.Tool(toolIndex);
            if (t == null || !Roles.CanShop(role.Value)) return;
            if (!t.consumable && Owns(toolIndex)) return;
            if (money.Value < t.price) return;
            money.Value -= t.price;
            if (t.consumable) charges[toolIndex] += t.stack;
            else ownedTools.Value |= 1UL << toolIndex;
            if (t.id == "C4") ownedTools.Value |= 1UL << Balance.Tool("Detonator").index;
        }

        // ------------------------------------------------------------------ owner-targeted RPCs

        [Rpc(SendTo.Owner)]
        public void TeleportRpc(Vector3 position, float yaw)
        {
            Ragdoll.Recover(true);
            var vr = GetComponent<VRRig>();
            if (vr != null && vr.enabled) vr.AlignTo(position, yaw);
            else Motor.Teleport(position, yaw);
        }

        /// <summary>Server -> owner: you broke a loot voxel.</summary>
        [Rpc(SendTo.Owner)]
        public void LootRpc(string prize, string layer)
        {
            UI.GameMenus.Instance?.Toast($"LOOT ({layer})!  {prize}", AeroPalette.Gold);
        }

        /// <summary>Server -> owner: Boing Pad. Sets the upward speed (keeps horizontal momentum), never ragdolls.</summary>
        [Rpc(SendTo.Owner)]
        public void LaunchRpc(Vector3 velocity)
        {
            if (Motor.IsGrappling) return;
            var v = Motor.Velocity;
            Motor.Velocity = new Vector3(v.x, Mathf.Max(v.y, 0f), v.z);
            Motor.AddImpulse(velocity);
        }

        [Rpc(SendTo.Owner)]
        public void ToastRpc(string msg) => UI.GameMenus.Instance?.Toast(msg, AeroPalette.Gold);

        /// <summary>Server -> owner: the God picked you up. You dangle from its hand until it lets go.</summary>
        [Rpc(SendTo.Owner)]
        public void HeldRpc(ulong godClient, bool left)
        {
            if (!ByClient.TryGetValue(godClient, out var god)) return;
            Ragdoll.Recover(true);
            Motor.StopGrapple();
            Motor.heldBy = left ? god.leftHand : god.rightHand;
            Motor.heldOffset = Vector3.down * (0.08f * god.Motor.WorldScale + 0.6f);
            UI.GameMenus.Instance?.Toast("The God grabbed you!", AeroPalette.Danger);
        }

        /// <summary>Server -> owner: dropped or thrown. A real throw ragdolls you.</summary>
        [Rpc(SendTo.Owner)]
        public void ReleasedRpc(Vector3 velocity)
        {
            if (Motor.heldBy == null) return;
            Motor.heldBy = null;
            Motor.Teleport(transform.position, transform.eulerAngles.y);
            if (velocity.magnitude > 4f && GameSettings.AllowRagdoll) Ragdoll.Begin(velocity);
            else Motor.AddImpulse(velocity);
        }

        /// <summary>Server -> owner: explosion knockback. Big hits ragdoll the player.</summary>
        [Rpc(SendTo.Owner)]
        public void KnockbackRpc(Vector3 velocity)
        {
            if (Motor.heldBy != null) return;   // in the God's fist: nothing else moves you
            float threshold = Balance.ToolsFile.ragdollThreshold;
            if (velocity.magnitude >= threshold && GameSettings.AllowRagdoll) Ragdoll.Begin(velocity);
            else Motor.AddImpulse(velocity);
        }
    }

    static class ArgsExt
    {
        public static bool Contains(this string[] arr, string s) => System.Array.IndexOf(arr, s) >= 0;
    }
}
