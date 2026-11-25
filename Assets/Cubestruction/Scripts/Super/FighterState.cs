using System.Collections.Generic;
using Cubestruction.Player;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Super
{
    /// <summary>
    /// SUPER! state for one fighter: a player (on the player prefab) or a FighterBot. The owner writes its
    /// character pick, block and animation cue; the server owns health, Power, tier and the score.
    /// </summary>
    public class FighterState : NetworkBehaviour
    {
        public static readonly List<FighterState> All = new List<FighterState>();

        public readonly NetworkVariable<byte> character = new NetworkVariable<byte>(0, writePerm: NetworkVariableWritePermission.Owner);
        public readonly NetworkVariable<bool> blocking = new NetworkVariable<bool>(false, writePerm: NetworkVariableWritePermission.Owner);
        /// <summary>Animation cue: low 4 bits = Anim, high 4 bits = a counter so repeats replay.</summary>
        public readonly NetworkVariable<byte> anim = new NetworkVariable<byte>(0, writePerm: NetworkVariableWritePermission.Owner);

        public readonly NetworkVariable<bool> inMatch = new NetworkVariable<bool>();
        public readonly NetworkVariable<float> hp = new NetworkVariable<float>(Roster.BaseHp);
        public readonly NetworkVariable<float> maxHp = new NetworkVariable<float>(Roster.BaseHp);
        public readonly NetworkVariable<float> power = new NetworkVariable<float>();
        public readonly NetworkVariable<byte> tier = new NetworkVariable<byte>(1);
        public readonly NetworkVariable<byte> team = new NetworkVariable<byte>();
        public readonly NetworkVariable<bool> down = new NetworkVariable<bool>();
        public readonly NetworkVariable<int> kos = new NetworkVariable<int>();
        public readonly NetworkVariable<int> falls = new NetworkVariable<int>();
        public readonly NetworkVariable<int> combo = new NetworkVariable<int>();
        public readonly NetworkVariable<float> frozenUntil = new NetworkVariable<float>();   // server time

        public enum Anim : byte { None, Jab, Cross, Hook, Kick, Launcher, Axe, Special, Charge, Beam, Ultimate, PowerUp }

        public PlayerAvatar Player { get; private set; }
        public FighterBot Bot { get; private set; }
        public FighterDef Def => Roster.Get(character.Value);
        public int Tier => tier.Value;
        public bool Alive => inMatch.Value && !down.Value;
        public string DisplayName => Player ? Player.displayName.Value.ToString() : Bot ? Bot.Name : "?";
        /// <summary>Owner id used for damage credit and voxel instigators.</summary>
        public ulong Id => Player ? Player.OwnerClientId : Bot ? Bot.BotId : ulong.MaxValue;
        public float Scale => transform.localScale.x;
        public Vector3 Chest => transform.position + Vector3.up * 0.6f * Scale;
        public Vector3 Facing
        {
            get
            {
                var f = Player ? Player.headAnchor.forward : transform.forward;
                f.y = 0;
                return f.sqrMagnitude > 1e-4f ? f.normalized : Vector3.forward;
            }
        }
        public bool Frozen(double serverTime) => frozenUntil.Value > serverTime;

        // ---- server bookkeeping (not synced)
        [System.NonSerialized] public float lastLight, lastHeavy, lastHurt = -99f, blockSince = -99f, respawnAt, stunUntil;
        [System.NonSerialized] public int comboStep;
        [System.NonSerialized] public float[] cooldownEnd = new float[4];
        [System.NonSerialized] public ulong lastAttacker = ulong.MaxValue;
        [System.NonSerialized] public float damageDealt;
        [System.NonSerialized] public int bestCombo;

        byte _animSeq;

        void Awake()
        {
            Player = GetComponent<PlayerAvatar>();
            Bot = GetComponent<FighterBot>();
        }

        public override void OnNetworkSpawn()
        {
            All.Add(this);
            if (IsOwner && Player) character.Value = (byte)Mathf.Clamp(GameSettings.Fighter, 0, Roster.All.Length - 1);
            blocking.OnValueChanged += (was, now) => { if (now && !was) blockSince = Time.time; };
        }

        public override void OnNetworkDespawn() { All.Remove(this); }

        public static FighterState Of(ulong id)
        {
            foreach (var f in All) if (f.Id == id) return f;
            return null;
        }

        /// <summary>Owner (or the server for bots): play an animation cue for everyone.</summary>
        public void Cue(Anim a)
        {
            if (!IsOwner) return;
            _animSeq = (byte)((_animSeq + 1) & 15);
            anim.Value = (byte)((_animSeq << 4) | (byte)a);
        }

        // ------------------------------------------------------------------ server -> owner

        /// <summary>A hit sent you flying (bots: the server is the owner).</summary>
        [Rpc(SendTo.Owner)]
        public void KnockRpc(Vector3 velocity)
        {
            if (Bot) { Bot.Knock(velocity); return; }
            var m = Player.Motor;
            if (m.heldBy != null) return;
            m.Velocity = new Vector3(m.Velocity.x * 0.3f, Mathf.Max(0f, m.Velocity.y), m.Velocity.z * 0.3f);
            m.AddImpulse(velocity);
            FighterController.Local?.OnKnocked(velocity);
        }

        /// <summary>Move the fighter (flash steps, rushes, grabs). Owners move themselves.</summary>
        [Rpc(SendTo.Owner)]
        public void PlaceRpc(Vector3 position, float yaw)
        {
            if (Bot) { Bot.Place(position, yaw); return; }
            Player.Motor.Velocity = Vector3.zero;
            Player.Motor.Teleport(position, yaw);
        }

        /// <summary>Knocked out: the body goes limp for a moment, then you're back on the stage.</summary>
        [Rpc(SendTo.Owner)]
        public void KoRpc(Vector3 fling)
        {
            if (Bot) { Bot.Ko(fling); return; }
            if (GameSettings.AllowRagdoll) Player.Ragdoll.Begin(fling);
            FighterController.Local?.OnKo();
        }

        [Rpc(SendTo.Owner)]
        public void RespawnRpc(Vector3 position, float yaw)
        {
            if (Bot) { Bot.Place(position, yaw); return; }
            Player.TeleportRpc(position, yaw);
        }
    }
}
