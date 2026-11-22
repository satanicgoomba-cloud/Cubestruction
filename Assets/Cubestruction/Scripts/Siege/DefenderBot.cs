using Cubestruction.Tools;
using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Siege
{
    /// <summary>
    /// An AI defender for Titan Siege (bots top the defenders up to five). Server-driven, it plays like a
    /// person: digs the mine wall on top of its wall for cannonballs and rockets, carries them to its turret
    /// (standing there loads them), fires the turret at the Titan, and fires its RPG when it has rockets.
    /// The Titan can grab and throw it; knocked off the wall, it webs back up a moment later.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class DefenderBot : NetworkBehaviour
    {
        public readonly NetworkVariable<int> slot = new NetworkVariable<int>();
        public readonly NetworkVariable<byte> colorIndex = new NetworkVariable<byte>();

        /// <summary>Owner id used for the bot's rockets and digging (never a real client).</summary>
        public ulong BotId => BotIdFor(slot.Value);
        public static ulong BotIdFor(int s) => ulong.MaxValue - 100 - (ulong)s;

        int _balls, _rockets;
        Vector3 _vel;
        bool _airborne;
        Transform _heldBy;
        float _downSince = -1, _rpgAt, _digAt, _mineX;
        enum Job { Mine, Haul }
        Job _job;
        Transform _visual;
        public static int RocketsFired, TurretShots, Digs;   // -autotest

        public void ServerInit(int s) { _initSlot = s; }
        int _initSlot;

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                slot.Value = _initSlot; colorIndex.Value = (byte)(_initSlot + 3);
                _rpgAt = Time.time + Random.Range(2f, 4f);
                _mineX = Random.Range(-18f, 18f);
            }
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

        // ------------------------------------------------------------------ ammo (server)

        /// <summary>Mined ammo, capped like a player's. Returns how many it actually kept.</summary>
        public int ServerAddAmmo(int balls, int rockets)
        {
            int b0 = _balls, r0 = _rockets;
            _balls = Mathf.Min(TitanDirector.MaxBalls, _balls + balls);
            _rockets = Mathf.Min(TitanDirector.MaxRockets, _rockets + rockets);
            return _balls - b0 + _rockets - r0;
        }

        /// <summary>A turret takes one round from us (cannonball, or a rocket for rocket pods).</summary>
        public bool ServerTakeAmmo(bool rocket)
        {
            if (rocket) { if (_rockets <= 0) return false; _rockets--; return true; }
            if (_balls <= 0) return false; _balls--; return true;
        }

        public int Balls => _balls;
        public int Rockets => _rockets;

        // ------------------------------------------------------------------ AI (server)

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

            if (_heldBy != null) { transform.position = _heldBy.position - Vector3.up * 0.9f; return; }   // dangling in the Titan's fist
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
                if (Time.time - _downSince > 2.5f) { transform.position = top + Vector3.right * _mineX * 0.5f + Vector3.up * 0.1f; _downSince = -1; FxHub.Instance?.ShockwaveRpc(transform.position, 0.6f); }
                return;
            }
            _downSince = -1;

            int s = slot.Value;
            int turret = s < TitanDirector.Layout.Length ? w * TitanDirector.Layout.Length + s : -1;
            bool turretAlive = turret >= 0 && d.ammo[turret] >= 0;
            bool rocketTurret = turretAlive && d.KindOf(turret) == TitanDirector.TurretKind.Rocket;
            int carryForTurret = rocketTurret ? _rockets : _balls;
            int cap = turretAlive ? TitanDirector.Capacity(d.KindOf(turret)) : 0;

            // dig until there's a load worth carrying (or the turret is full), then haul it over
            if (_job == Job.Mine && (carryForTurret >= 3 || _balls + _rockets >= 8) && turretAlive && d.ammo[turret] < cap) _job = Job.Haul;
            if (_job == Job.Haul && (carryForTurret == 0 || !turretAlive || d.ammo[turret] >= cap)) _job = Job.Mine;

            Vector3 spot = _job == Job.Haul ? d.TurretPos(turret) + Vector3.right * 0.9f : TitanMap.MineFace(o, w, _mineX);
            spot.y = top.y;
            var flat = spot - transform.position; flat.y = 0;
            var step = Vector3.ClampMagnitude(flat, 3.5f * dt);
            _vel = step / Mathf.Max(dt, 1e-4f);
            transform.position = new Vector3(transform.position.x + step.x, top.y, transform.position.z + step.z);
            bool there = flat.magnitude < 0.4f;

            if (_job == Job.Mine && there)
            {
                Face(Vector3.forward);
                if (Time.time >= _digAt)
                {
                    _digAt = Time.time + 0.55f;
                    var cube = VoxelCube.Instance;
                    var chest = transform.position + Vector3.up * 0.6f;
                    var ray = new Ray(chest + Vector3.up * Random.Range(-0.3f, 1.4f), Vector3.forward);
                    if (cube && cube.Raycast(ray, 3f, out _, out var p))
                    {
                        cube.ServerDamage(p + Vector3.forward * 0.1f, 1.3f, 60f, 3, false, BotId);
                        FxHub.Instance?.ImpactRpc(p, -1);
                        Digs++;
                    }
                    else _mineX = Mathf.Clamp(_mineX + Random.Range(-6f, 6f), -20f, 20f);   // dug through here: move along
                }
            }

            var titan = d.Body;
            if (!titan || d.prep.Value) { if (_job == Job.Haul) Face(Vector3.back); return; }
            var chestT = TitanDirector.TitanChest(titan);
            if (_job == Job.Haul) Face(chestT - transform.position);
            float dist = Vector3.Distance(transform.position, chestT);

            // fire the turret whenever it has a round and the Titan is in range
            if (turretAlive && _job == Job.Haul && there && d.ammo[turret] > 0 && dist < 70f)
                if (d.ServerFireTurret(turret, BotId)) TurretShots++;

            // RPG when it has rockets to spare (rocket-pod bots keep theirs for the pod)
            if (Time.time >= _rpgAt && dist < 70f && _rockets > (rocketTurret ? 2 : 0) && d.explosivePrefab)
            {
                _rpgAt = Time.time + Random.Range(4f, 6f);
                _rockets--;
                var from = transform.position + Vector3.up * 0.7f;
                var aim = (chestT + Random.insideUnitSphere * 3f - from).normalized;   // bots aren't snipers
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

        /// <summary>Server: a Titan blow (or a throw) sends the bot flying.</summary>
        public void ServerKnock(Vector3 v) { _heldBy = null; _airborne = true; _vel = v; transform.position += Vector3.up * 0.1f; }

        /// <summary>Server: the Titan picked the bot up.</summary>
        public void ServerHold(Transform hand) { _heldBy = hand; _airborne = false; }

        public void ServerTeleport(Vector3 p) { _heldBy = null; _airborne = false; _vel = Vector3.zero; _downSince = -1; transform.position = p; }
    }
}
