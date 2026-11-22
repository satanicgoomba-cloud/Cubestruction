using Cubestruction.Tools;
using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Siege
{
    /// <summary>
    /// An AI defender for Titan Siege (bots top the defenders up to five). Server-driven: it walks to its
    /// turret on the current wall, stays to load it, fires it at the Titan, and fires its RPG in between.
    /// Knocked off the wall, it webs back up a moment later. Everyone sees a Buddy with a launcher.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class DefenderBot : NetworkBehaviour
    {
        public readonly NetworkVariable<int> slot = new NetworkVariable<int>();
        public readonly NetworkVariable<byte> colorIndex = new NetworkVariable<byte>();

        /// <summary>Owner id used for the bot's rockets (never a real client).</summary>
        public ulong BotId => ulong.MaxValue - 100 - (ulong)slot.Value;

        Vector3 _vel;
        bool _airborne;
        float _downSince = -1, _rpgAt, _wanderX;
        Transform _visual;
        public static int RocketsFired, TurretShots;   // -autotest

        public void ServerInit(int s) { _initSlot = s; }
        int _initSlot;

        public override void OnNetworkSpawn()
        {
            if (IsServer) { slot.Value = _initSlot; colorIndex.Value = (byte)(_initSlot + 3); _rpgAt = Time.time + Random.Range(2f, 4f); _wanderX = Random.Range(-14f, 14f); }
            _visual = transform.Find("Visual");
            if (_visual)
            {
                var c = AeroPalette.BuddyColors[colorIndex.Value % AeroPalette.BuddyColors.Length];
                var block = new MaterialPropertyBlock(); block.SetColor("_BaseColor", c);
                foreach (var r in _visual.GetComponentsInChildren<Renderer>()) r.SetPropertyBlock(block);
                var launcher = ToolVisuals.Create("RocketLauncher", _visual);
                launcher.transform.localPosition = new Vector3(0.2f, 0.62f, 0.15f);
            }
        }

        void Update()
        {
            if (_visual) _visual.localPosition = Vector3.up * Mathf.Abs(Mathf.Sin(Time.time * 9f)) * 0.04f * Mathf.Clamp01(_vel.magnitude);
            if (!IsServer) return;
            var d = TitanDirector.Instance;
            if (d == null || !d.IsActive) return;
            float dt = Time.deltaTime;
            var o = d.Origin;
            int w = Mathf.Min(d.wall.Value, TitanMap.Walls.Length - 1);
            var top = TitanMap.WallTop(o, w);

            if (_airborne)
            {
                // knocked flying: fall, then web back up onto the wall
                _vel += Physics.gravity * dt;
                transform.position += _vel * dt;
                var cube = VoxelCube.Instance;
                bool landed = transform.position.y <= o.y + 0.05f || (cube && _vel.y < 0 && cube.SolidNear(transform.position - Vector3.up * 0.05f, 0.1f, out _));
                if (landed) { _airborne = false; _vel = Vector3.zero; transform.position = new Vector3(transform.position.x, Mathf.Max(transform.position.y, o.y), transform.position.z); _downSince = Time.time; }
                return;
            }
            if (_downSince >= 0 && transform.position.y < top.y - 1f)
            {
                if (Time.time - _downSince > 2.5f) { transform.position = top + Vector3.right * _wanderX + Vector3.up * 0.1f; _downSince = -1; FxHub.Instance?.ShockwaveRpc(transform.position, 0.6f); }
                return;
            }
            _downSince = -1;

            // go to my turret on this wall (bots past the fourth roam the wall with their RPG)
            int s = slot.Value;
            int turret = s < TitanDirector.Layout.Length ? w * TitanDirector.Layout.Length + s : -1;
            bool turretAlive = turret >= 0 && d.ammo[turret] >= 0;
            var spot = turretAlive ? d.TurretPos(turret) + Vector3.right * 0.9f : top + Vector3.right * _wanderX;
            spot.y = top.y;
            var flat = spot - transform.position; flat.y = 0;
            var step = Vector3.ClampMagnitude(flat, 3.5f * dt);
            _vel = step / Mathf.Max(dt, 1e-4f);
            transform.position = new Vector3(transform.position.x + step.x, top.y, transform.position.z + step.z);

            var titan = d.Body;
            if (!titan || d.prep.Value) { Face(Vector3.back); return; }
            var chest = TitanDirector.TitanChest(titan);
            Face(chest - transform.position);
            float dist = Vector3.Distance(transform.position, chest);

            // fire the turret when it's full, or whenever the Titan is close
            if (turretAlive && flat.magnitude < 0.5f && d.ammo[turret] > 0 &&
                (d.ammo[turret] >= TitanDirector.Capacity(d.KindOf(turret)) || dist < 30f))
                if (d.ServerFireTurret(turret)) TurretShots++;

            // RPG between turret shots
            if (Time.time >= _rpgAt && dist < 70f && d.explosivePrefab)
            {
                _rpgAt = Time.time + Random.Range(4f, 6f);
                var from = transform.position + Vector3.up * 0.7f;
                var aim = (chest + Random.insideUnitSphere * 3f - from).normalized;   // bots aren't snipers
                var go = Instantiate(d.explosivePrefab, from + aim * 0.6f, Quaternion.LookRotation(aim));
                go.GetComponent<ExplosiveProjectile>().ServerInit(Balance.Tool("RocketLauncher"), BotId, aim * 32f);
                go.GetComponent<NetworkObject>().Spawn(true);
                RocketsFired++;
            }
        }

        void Face(Vector3 dir)
        {
            dir.y = 0;
            if (dir.sqrMagnitude > 0.01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), Time.deltaTime * 6f);
        }

        /// <summary>Server: a Titan blow sends the bot flying.</summary>
        public void ServerKnock(Vector3 v) { _airborne = true; _vel = v; transform.position += Vector3.up * 0.1f; }

        public void ServerTeleport(Vector3 p) { _airborne = false; _vel = Vector3.zero; _downSince = -1; transform.position = p; }
    }
}
