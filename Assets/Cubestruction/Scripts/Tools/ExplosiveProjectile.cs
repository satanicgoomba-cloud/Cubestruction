using System.Collections.Generic;
using Cubestruction.Player;
using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Tools
{
    /// <summary>
    /// Grenades, goo bombs, pop sticks, vortex orbs, rockets, fizzy barrels (TNT), pixel charges (C4)
    /// and the sunburst crate, plus the Boing Pad (no blast: launches players who step on it).
    /// Server-simulated rigidbody; clients see it through NetworkTransform.
    /// </summary>
    [RequireComponent(typeof(Rigidbody), typeof(NetworkObject))]
    public class ExplosiveProjectile : NetworkBehaviour
    {
        public static readonly List<ExplosiveProjectile> Live = new List<ExplosiveProjectile>();

        public readonly NetworkVariable<int> toolIndex = new NetworkVariable<int>(-1);
        public readonly NetworkVariable<float> fuseLeft = new NetworkVariable<float>();

        public ToolDef Def => Balance.Tool(toolIndex.Value);
        public ulong Owner { get; private set; }

        Rigidbody _rb;
        bool _stuck, _exploded;
        int _pulsesLeft;
        float _pulseTimer;
        GameObject _visual;
        readonly Dictionary<ulong, float> _padReady = new Dictionary<ulong, float>();
        public bool IsPad => Def != null && Def.id == "BoingPad";
        static bool IsRocket(ToolDef t) => t.id == "RocketLauncher" || t.id == "RaptorMissile";

        void Awake() { _rb = GetComponent<Rigidbody>(); }

        ToolDef _init; Vector3 _initVel;

        /// <summary>Server, before Spawn. The values are applied in OnNetworkSpawn: NetworkVariables can't be
        /// written before the object spawns, and NetworkRigidbody holds the body kinematic until then (a
        /// velocity set earlier is dropped, which used to leave grenades at your feet and rockets hanging).</summary>
        public void ServerInit(ToolDef t, ulong owner, Vector3 velocity)
        {
            _init = t;
            _initVel = velocity;
            Owner = owner;
        }

        void ServerApplyInit()
        {
            var t = _init;
            if (t == null) return;
            toolIndex.Value = t.index;
            bool rocket = IsRocket(t);
            fuseLeft.Value = t.id == "C4" ? float.PositiveInfinity : (rocket ? 6f : t.fuse);
            bool placed = t.cat == ToolCategory.Placed;
            _rb.isKinematic = placed && _initVel.sqrMagnitude < 0.01f;   // placed on a surface
            _rb.useGravity = !rocket;
            _rb.mass = placed ? 6f : 0.4f;
            if (!_rb.isKinematic) _rb.linearVelocity = _initVel;
            if (t.id == "BoingPad")
            {
                // gyro: the spring always points up, it never tumbles or rolls away
                _rb.freezeRotation = true;
                _rb.angularVelocity = Vector3.zero;
                transform.rotation = Quaternion.Euler(0, transform.eulerAngles.y, 0);
                _rb.linearDamping = 1.5f;
            }
            _pulsesLeft = t.id == "BlackHoleGrenade" ? Mathf.Max(1, t.pellets) : 0;
        }

        public override void OnNetworkSpawn()
        {
            Live.Add(this);
            if (IsServer) ServerApplyInit();
            else _rb.isKinematic = true;
            toolIndex.OnValueChanged += (_, __) => BuildVisual();
            BuildVisual();
        }

        public override void OnNetworkDespawn() { Live.Remove(this); }

        void BuildVisual()
        {
            var t = Def;
            if (t == null || _visual) return;
            // rockets look like missiles (not like the launcher that fired them)
            _visual = ToolVisuals.Create(IsRocket(t) ? "RaptorMissile" : t.id, transform);
        }

        void Update()
        {
            // blinking warning light speeds up as the fuse burns (everyone sees it)
            if (_visual && !float.IsInfinity(fuseLeft.Value) && !IsPad)
            {
                float f = fuseLeft.Value;
                float rate = f < 2 ? 14 : f < 5 ? 6 : 2;
                float pulse = 1f + 0.08f * Mathf.Max(0, Mathf.Sin(Time.time * rate));
                _visual.transform.localScale = Vector3.one * pulse;
            }
            if (!IsServer || _exploded) return;

            var t = Def;
            if (t == null) return;
            if (_pulsesLeft > 0 && fuseLeft.Value <= 0)
            {
                // Vortex Orb: hover, pull players in, crush in pulses
                _rb.isKinematic = true;
                _pulseTimer -= Time.deltaTime;
                if (_pulseTimer <= 0)
                {
                    _pulseTimer = 0.45f;
                    _pulsesLeft--;
                    Explosion.ServerExplode(transform.position, t, Owner, pulse: true);
                    if (_pulsesLeft == 0) Finish();
                }
                return;
            }
            fuseLeft.Value -= Time.deltaTime;
            if (t.id == "BoingPad") { TickPad(t); if (fuseLeft.Value <= 0) Finish(); return; }   // expires quietly
            if (fuseLeft.Value <= 0 && _pulsesLeft == 0) ServerExplode();
        }

        void OnCollisionEnter(Collision c)
        {
            if (!IsServer || _exploded) return;
            var t = Def; if (t == null) return;
            if (IsRocket(t)) { ServerExplode(); return; }
            if ((t.id == "StickyBomb" || t.id == "C4") && !_stuck && c.collider.GetComponentInParent<ExplosiveProjectile>() == null)
            {
                _stuck = true;
                _rb.isKinematic = true;
                // stick to players too: ride along with them
                var pl = c.collider.GetComponentInParent<PlayerAvatar>();
                if (pl) transform.SetParent(null, true);
            }
        }

        /// <summary>Boing Pad: anyone standing on it gets launched straight up (knockback = launch speed).</summary>
        float _padCheck;
        public static int PadsUpright, PadsTilted;   // -autotest

        void TickPad(ToolDef t)
        {
            // stay upright; once it has landed, park it (no sliding off), and drop again if the floor goes
            transform.rotation = Quaternion.Euler(0, transform.eulerAngles.y, 0);
            if ((_padCheck -= Time.deltaTime) <= 0)
            {
                _padCheck = 0.25f;
                bool floor = Physics.Raycast(transform.position + Vector3.up * 0.1f, Vector3.down, 0.3f, ~(1 << 5), QueryTriggerInteraction.Ignore)
                             || (VoxelCube.Instance && VoxelCube.Instance.SolidNear(transform.position - Vector3.up * 0.1f, 0.12f, out _));
                if (!_rb.isKinematic && floor && _rb.linearVelocity.magnitude < 0.4f) { _rb.linearVelocity = Vector3.zero; _rb.isKinematic = true; }
                else if (_rb.isKinematic && !floor) _rb.isKinematic = false;
                if (Vector3.Angle(transform.up, Vector3.up) < 2f) PadsUpright++; else PadsTilted++;
            }
            var top = transform.position;
            foreach (var pl in PlayerAvatar.All)
            {
                if (Roles.IsGiant(pl.role.Value)) continue;
                var d = pl.transform.position - top;
                float s = pl.Motor.WorldScale;
                if (new Vector2(d.x, d.z).magnitude > 0.85f || d.y < -0.4f || d.y > 0.9f * s) continue;
                if (_padReady.TryGetValue(pl.OwnerClientId, out var at) && Time.time < at) continue;
                _padReady[pl.OwnerClientId] = Time.time + 0.6f;
                pl.LaunchRpc(Vector3.up * t.knockback * s);
                FxHub.Instance?.ShockwaveRpc(top, 0.8f);
            }
        }

        public void ServerExplode()
        {
            if (_exploded) return;
            Explosion.ServerExplode(transform.position, Def, Owner, pulse: false);
            Finish();
        }

        void Finish()
        {
            _exploded = true;
            NetworkObject.Despawn(true);
        }
    }

    public static class Explosion
    {
        /// <summary>Server: damage voxels, knock back (and maybe ragdoll) every player nearby, including the thrower.</summary>
        public static void ServerExplode(Vector3 pos, ToolDef t, ulong owner, bool pulse)
        {
            var cube = VoxelCube.Instance;
            float cell = cube ? cube.cellSize : 0.25f;
            float radiusM = t.radiusCells * cell;
            if (cube) cube.ServerDamage(pos, t.radiusCells, t.damage, t.tier, true, owner);

            // players get blasted from further away than voxels break (that's the fun part)
            float blastR = Mathf.Max(radiusM * 2.2f, 2.5f);
            foreach (var pl in PlayerAvatar.All)
            {
                if (Roles.IsGiant(pl.role.Value)) continue;
                var center = pl.transform.position + Vector3.up * 0.5f;
                var d = center - pos;
                float dist = d.magnitude;
                if (dist > blastR) continue;
                float k = 1f - dist / blastR;
                var dir = dist > 0.01f ? d / dist : Vector3.up;
                Vector3 v;
                if (t.knockback < 0) v = -dir * Mathf.Abs(t.knockback) * k;                // vortex pull
                else v = (dir + Vector3.up * 0.6f).normalized * t.knockback * k * (pulse ? 0.3f : 1f);
                if (v.sqrMagnitude > 0.25f) pl.KnockbackRpc(v);
            }
            // siege units get thrown too
            Siege.EnemyUnit.ServerBlast(pos, blastR, t.damage * 0.5f, Mathf.Abs(t.knockback));
            Siege.TitanDirector.ServerExplosion(pos, radiusM, t, owner);
            FxHub.Instance?.ExplosionRpc(pos, radiusM, t.index, pulse);
        }
    }
}
