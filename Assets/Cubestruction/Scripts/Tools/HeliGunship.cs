using Cubestruction.Player;
using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Tools
{
    /// <summary>
    /// Heli-Gunner killstreak: a gunship that circles the cube for a minute with its open left door
    /// facing in. The server flies it (NetworkTransform); the gunner's client rides the "Seat" and
    /// works the door gun through <see cref="HeliView"/>. One in the air at a time.
    /// </summary>
    public class HeliGunship : NetworkBehaviour
    {
        public static HeliGunship Instance { get; private set; }

        public readonly NetworkVariable<ulong> gunner = new NetworkVariable<ulong>(ulong.MaxValue);
        public readonly NetworkVariable<float> timeLeft = new NetworkVariable<float>();

        public const float Speed = 8f;      // m/s around the cube
        public Transform Seat { get; private set; }
        Transform _rotor, _tail;
        float _angle;

        ulong _initGunner; float _initSeconds;

        /// <summary>Server, before Spawn (the values are written once spawned).</summary>
        public void ServerInit(ulong owner, float seconds)
        {
            _initGunner = owner;
            _initSeconds = seconds;
            _angle = Random.value * 360f;
            Place(0);
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            FindParts();
            if (IsServer) { gunner.Value = _initGunner; timeLeft.Value = _initSeconds; }
        }

        public override void OnNetworkDespawn() { if (Instance == this) Instance = null; }

        void FindParts()
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Seat") Seat = t;
                else if (t.name == "Rotor") _rotor = t;
                else if (t.name == "TailRotor") _tail = t;
            }
            if (!Seat)
            {
                Seat = new GameObject("Seat").transform;
                Seat.SetParent(transform, false);
                Seat.localPosition = new Vector3(-0.75f, -0.35f, 0.15f);
            }
        }

        void Update()
        {
            if (_rotor) _rotor.Rotate(0, 1400f * Time.deltaTime, 0, Space.Self);
            if (_tail) _tail.Rotate(2000f * Time.deltaTime, 0, 0, Space.Self);
            if (!IsServer || !IsSpawned) return;
            Place(Time.deltaTime);
            timeLeft.Value -= Time.deltaTime;
            var mm = MatchManager.Instance;
            if (timeLeft.Value <= 0 || !PlayerAvatar.ByClient.ContainsKey(gunner.Value) ||
                mm == null || mm.state.Value != MatchState.Playing)
                ServerEnd();
        }

        /// <summary>Circle the cube, nose along the path, left door (Unity -X) facing in, banked a little.</summary>
        void Place(float dt)
        {
            var cube = VoxelCube.Instance;
            Vector3 c = cube ? cube.Center : Vector3.up * 8f;
            float h = cube ? cube.HalfExtent : 8f;
            float r = h + 13f;
            _angle += dt * Speed / r * Mathf.Rad2Deg;
            float a = _angle * Mathf.Deg2Rad;
            var pos = new Vector3(c.x + Mathf.Cos(a) * r, c.y + h + 4f + Mathf.Sin(Time.time * 0.6f) * 1.2f, c.z + Mathf.Sin(a) * r);
            var tangent = new Vector3(-Mathf.Sin(a), 0, Mathf.Cos(a));
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(tangent) * Quaternion.Euler(0, 0, 8f));
        }

        /// <summary>Server: put the gunner back on the platform and leave.</summary>
        public void ServerEnd()
        {
            if (!IsServer || !IsSpawned) return;
            if (PlayerAvatar.ByClient.TryGetValue(gunner.Value, out var pl))
            {
                var cube = VoxelCube.Instance;
                var p = cube ? cube.transform.position + Vector3.back * (cube.HalfExtent + 2.5f) + Vector3.up * 0.2f : Vector3.up;
                pl.TeleportRpc(p, 0f);
            }
            NetworkObject.Despawn(true);
        }
    }
}
