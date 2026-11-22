using System.Collections.Generic;
using Cubestruction.Player;
using Cubestruction.Tools;
using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Siege
{
    public enum UnitState : byte { Walking = 0, Attacking = 1, Held = 2, Thrown = 3 }

    /// <summary>
    /// A Commander unit. Server-simulated rigidbody (clients see it through a server-authoritative
    /// NetworkTransform and only animate the Buddy limbs). Walks at the cube, attacks the nearest
    /// voxel in front of it (melee units tunnel inward toward the Core), can be grabbed and thrown
    /// by the God like a ragdoll and takes impact damage when it lands.
    /// </summary>
    [RequireComponent(typeof(Rigidbody), typeof(NetworkObject))]
    public class EnemyUnit : NetworkBehaviour
    {
        public static readonly List<EnemyUnit> Live = new List<EnemyUnit>();

        public readonly NetworkVariable<int> unitIndex = new NetworkVariable<int>(-1);
        public readonly NetworkVariable<float> hp = new NetworkVariable<float>();
        public readonly NetworkVariable<UnitState> state = new NetworkVariable<UnitState>(UnitState.Walking);

        public UnitDef Def => unitIndex.Value >= 0 && unitIndex.Value < Balance.Siege.units.Length ? Balance.Siege.units[unitIndex.Value] : null;
        public PlayerAvatar Holder { get; private set; }

        Rigidbody _rb;
        float _attackCd, _settle, _heldFor, _thrownAt, _blockedFor, _retargetIn;
        bool _holderLeft;
        Vector3 _heldVel, _lastHeldPos;
        Vector3 _targetPoint; bool _hasTarget;
        bool _dead;

        // client visuals
        Transform _visual;
        readonly Transform[] _parts = new Transform[6];
        readonly Quaternion[] _baseRot = new Quaternion[6];
        Vector3 _visualBaseScale = Vector3.one;
        Vector3 _lastPos;
        float _phase, _hitPop;

        const int HEAD = 0, TORSO = 1, ARML = 2, ARMR = 3, LEGL = 4, LEGR = 5;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            _visual = transform.Find("Visual") ?? transform;
            string[] names = { "Head", "Torso", "ArmL", "ArmR", "LegL", "LegR" };
            foreach (var t in _visual.GetComponentsInChildren<Transform>(true))
                for (int i = 0; i < names.Length; i++)
                    if (t.name == names[i] && _parts[i] == null) { _parts[i] = t; _baseRot[i] = t.localRotation; }
            _visualBaseScale = _visual.localScale;
        }

        public void ServerInit(int index)
        {
            unitIndex.Value = index;
            var d = Balance.Siege.units[index];
            hp.Value = d.hp;
            _rb.mass = Mathf.Max(0.5f, d.mass);
            _attackCd = Random.Range(0.2f, 0.8f);
        }

        public override void OnNetworkSpawn()
        {
            Live.Add(this);
            _lastPos = transform.position;
            hp.OnValueChanged += (a, b) => { if (b < a) _hitPop = 1f; };
            if (IsServer)
            {
                _rb.isKinematic = false;
                _rb.freezeRotation = true;
                _rb.interpolation = RigidbodyInterpolation.Interpolate;   // smooth for a host player
                _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            }
            else _rb.isKinematic = true;
        }

        public override void OnNetworkDespawn() { Live.Remove(this); }

        // ------------------------------------------------------------------ server AI

        void FixedUpdate()
        {
            if (!IsServer || _dead || Def == null) return;
            var cube = VoxelCube.Instance;
            var dir = SiegeDirector.Instance;
            float ts = dir ? dir.UnitTimeScale : 1f;
            float dt = Time.fixedDeltaTime;

            // knocked off the edge of the arena (or thrown into the void): gone
            float floor = cube != null ? cube.transform.position.y : 0f;
            if (transform.position.y < floor - 3f) { Die(true); return; }

            switch (state.Value)
            {
                case UnitState.Held: TickHeld(dt); return;
                case UnitState.Thrown: TickThrown(dt); return;
            }
            if (cube == null || cube.Grid == null) return;

            _retargetIn -= dt;
            if (_retargetIn <= 0 || !_hasTarget) { _retargetIn = 0.4f; _hasTarget = FindTarget(cube, out _targetPoint); }

            var d = Def;
            Vector3 flat = Vector3.ProjectOnPlane((_hasTarget ? _targetPoint : cube.transform.position) - transform.position, Vector3.up);
            float dist = _hasTarget ? Vector3.Distance(Chest, _targetPoint) : flat.magnitude;
            bool inRange = _hasTarget && dist <= d.rangeM + 0.35f;

            if (flat.sqrMagnitude > 0.01f)
                _rb.MoveRotation(Quaternion.Slerp(_rb.rotation, Quaternion.LookRotation(flat.normalized), dt * 8f));

            if (inRange)
            {
                if (state.Value != UnitState.Attacking) state.Value = UnitState.Attacking;
                _rb.linearVelocity = new Vector3(0, _rb.linearVelocity.y, 0);
                _attackCd -= dt * ts;
                if (_attackCd <= 0)
                {
                    _attackCd = d.attackCooldown > 0 ? d.attackCooldown : 1f;
                    Attack(cube);
                }
            }
            else
            {
                if (state.Value != UnitState.Walking) state.Value = UnitState.Walking;
                var v = flat.normalized * d.speed * ts;
                _rb.linearVelocity = new Vector3(v.x, _rb.linearVelocity.y, v.z);
                // step-up: if we're pushing but not moving (a voxel ledge), hop up onto it
                float horiz = new Vector3(_rb.linearVelocity.x, 0, _rb.linearVelocity.z).magnitude;
                _blockedFor = horiz < d.speed * 0.25f ? _blockedFor + dt : 0;
                if (_blockedFor > 0.35f && Grounded())
                {
                    _blockedFor = 0;
                    _rb.linearVelocity = new Vector3(v.x, 4.2f, v.z);
                }
            }
        }

        Vector3 Chest => transform.position + Vector3.up * 0.5f * Mathf.Max(1f, transform.localScale.y);

        bool Grounded() => Physics.Raycast(transform.position + Vector3.up * 0.1f, Vector3.down, 0.25f, ~0, QueryTriggerInteraction.Ignore);

        /// <summary>The first solid voxel in front of us: straight at chest height toward the cube's
        /// vertical axis (so melee units tunnel inward), else aimed at the cube centre.</summary>
        bool FindTarget(VoxelCube cube, out Vector3 point)
        {
            var axis = cube.transform.position;
            var chest = Chest;
            var flatDir = Vector3.ProjectOnPlane(axis - chest, Vector3.up);
            if (flatDir.sqrMagnitude > 0.0001f && cube.Raycast(new Ray(chest, flatDir.normalized), 60f, out _, out point)) return true;
            // low: units at the base chew the lowest cells
            var low = new Vector3(chest.x, cube.transform.position.y + cube.cellSize * 0.5f, chest.z);
            if (flatDir.sqrMagnitude > 0.0001f && cube.Raycast(new Ray(low, flatDir.normalized), 60f, out _, out point)) return true;
            var toCentre = cube.Center - chest;
            return cube.Raycast(new Ray(chest, toCentre.normalized), 60f, out _, out point);
        }

        void Attack(VoxelCube cube)
        {
            var d = Def;
            var from = Chest;
            var into = (_targetPoint - from).normalized;
            var hit = _targetPoint + into * cube.cellSize * 0.5f;
            bool explosive = d.attack == "Explode" || d.attack == "Siege";
            cube.ServerDamage(hit, d.attackRadiusCells, d.voxelDamage, d.tier, explosive, SiegeDirector.UnitInstigator);
            var fx = FxHub.Instance;
            switch (d.attack)
            {
                case "Ranged":
                    if (fx) fx.TracerRpc(from, hit, -1, ulong.MaxValue);
                    break;
                case "Siege":
                    if (fx) { fx.TracerRpc(from + Vector3.up * 0.4f, hit, -1, ulong.MaxValue); fx.ExplosionRpc(hit, d.attackRadiusCells * cube.cellSize, -1, true); }
                    break;
                case "Explode":
                    if (fx) fx.ExplosionRpc(hit, d.attackRadiusCells * cube.cellSize, -1, false);
                    Die(false);   // self-destruct doesn't feed the God mana
                    break;
                default:
                    if (fx && Random.value < 0.35f) fx.ImpactRpc(hit, -1);
                    break;
            }
        }

        // ------------------------------------------------------------------ grab / throw (server)

        public void ServerGrab(PlayerAvatar god, bool left)
        {
            Holder = god; _holderLeft = left;
            _heldFor = 0;
            _rb.isKinematic = true;
            _rb.freezeRotation = false;
            _lastHeldPos = HandPos();
            _heldVel = Vector3.zero;
            state.Value = UnitState.Held;
        }

        Vector3 HandPos()
        {
            if (Holder == null) return transform.position;
            var h = _holderLeft ? Holder.leftHand : Holder.rightHand;
            // dangle just below the god's palm
            return h.position - Vector3.up * 0.5f * Mathf.Max(1f, transform.localScale.y);
        }

        void TickHeld(float dt)
        {
            if (Holder == null || !Holder.IsSpawned) { ServerRelease(Vector3.zero); return; }
            var p = HandPos();
            _heldVel = Vector3.Lerp(_heldVel, (p - _lastHeldPos) / Mathf.Max(dt, 1e-4f), 0.5f);
            _lastHeldPos = p;
            _rb.MovePosition(p);
            _rb.MoveRotation(Quaternion.Euler(Mathf.Sin(Time.time * 9f) * 25f, transform.eulerAngles.y, Mathf.Cos(Time.time * 7f) * 25f));
            _heldFor += dt;
            if (_heldFor >= Balance.Siege.god.grabMaxSeconds) ServerRelease(_heldVel);   // they wriggle free
        }

        public void ServerRelease(Vector3 velocity)
        {
            Holder = null;
            Throw(velocity);
        }

        void Throw(Vector3 velocity)
        {
            _rb.isKinematic = false;
            _rb.freezeRotation = false;
            _rb.linearVelocity = velocity;
            _rb.angularVelocity = Random.insideUnitSphere * 10f;
            _thrownAt = Time.time;
            _settle = 0;
            _hasTarget = false;
            state.Value = UnitState.Thrown;
        }

        void TickThrown(float dt)
        {
            _settle = _rb.linearVelocity.magnitude < 0.6f && Time.time - _thrownAt > 0.4f ? _settle + dt : 0;
            if (_settle >= 1f)
            {
                // get back up and keep marching
                _rb.angularVelocity = Vector3.zero;
                _rb.MoveRotation(Quaternion.Euler(0, transform.eulerAngles.y, 0));
                _rb.freezeRotation = true;
                state.Value = UnitState.Walking;
            }
        }

        void OnCollisionEnter(Collision c)
        {
            if (!IsServer || _dead || state.Value != UnitState.Thrown || Time.time - _thrownAt < 0.1f) return;
            float speed = c.relativeVelocity.magnitude;
            if (speed <= 3f) return;
            float dmg = Balance.Siege.god.throwDamagePerMps * (speed - 3f);
            // bowling: a thrown unit hurts whoever it lands on, too
            var other = c.collider.GetComponentInParent<EnemyUnit>();
            if (other != null && other != this && !other._dead) other.ServerHit(dmg * 0.5f, c.relativeVelocity * -0.3f + Vector3.up * 2f, byGod: true);
            ServerTakeDamage(dmg, byGod: true);
        }

        // ------------------------------------------------------------------ damage (server)

        public void ServerTakeDamage(float dmg, bool byGod)
        {
            if (!IsServer || _dead) return;
            hp.Value -= dmg;
            if (hp.Value <= 0) Die(byGod);
        }

        /// <summary>Damage + knock into the thrown/ragdoll state.</summary>
        public void ServerHit(float dmg, Vector3 impulse, bool byGod)
        {
            if (!IsServer || _dead) return;
            ServerTakeDamage(dmg, byGod);
            if (_dead || state.Value == UnitState.Held) return;
            Throw(_rb.isKinematic ? impulse : _rb.linearVelocity + impulse / Mathf.Max(0.5f, Def.mass) * 1.5f);
        }

        void Die(bool byGod)
        {
            if (_dead) return;
            _dead = true;
            SiegeDirector.Instance?.ServerOnUnitKilled(this, byGod);
            FxHub.Instance?.ExplosionRpc(Chest, 0.4f, -1, true);
            NetworkObject.Despawn(true);
        }

        /// <summary>Server: damage and knock back every unit in range. No-op when there are no units
        /// (it's called by every explosion in every mode).</summary>
        public static void ServerBlast(Vector3 pos, float radiusM, float damage, float knockback, bool byGod = false)
        {
            if (Live.Count == 0 || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
            if (radiusM <= 0) return;
            foreach (var u in Live.ToArray())
            {
                if (u == null || u._dead || !u.IsSpawned) continue;
                var d = u.Chest - pos;
                float dist = d.magnitude;
                if (dist > radiusM) continue;
                float k = 1f - dist / radiusM;
                var dir = dist > 0.01f ? d / dist : Vector3.up;
                var impulse = (dir + Vector3.up * 0.7f).normalized * knockback * k;
                if (knockback > 0.5f) u.ServerHit(damage * k, impulse, byGod);
                else u.ServerTakeDamage(damage * k, byGod);
            }
        }

        // ------------------------------------------------------------------ client visuals (everyone)

        void LateUpdate()
        {
            if (_visual == null) return;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            float speed = Vector3.ProjectOnPlane(transform.position - _lastPos, Vector3.up).magnitude / dt;
            _lastPos = transform.position;
            var st = state.Value;
            _phase += dt * (st == UnitState.Held ? 22f : 4f + speed * 6f);
            float s = Mathf.Sin(_phase);

            // squash & stretch + hit pop
            _hitPop = Mathf.MoveTowards(_hitPop, 0, dt * 5f);
            float bounce = st == UnitState.Walking ? Mathf.Abs(s) * 0.08f : st == UnitState.Attacking ? Mathf.Max(0, s) * 0.12f : 0f;
            float pop = _hitPop * 0.25f;
            _visual.localScale = Vector3.Scale(_visualBaseScale, new Vector3(1f + pop - bounce * 0.5f, 1f - pop + bounce, 1f + pop - bounce * 0.5f));

            float limb = st == UnitState.Held || st == UnitState.Thrown ? 70f : st == UnitState.Attacking ? 55f : 30f * Mathf.Clamp01(speed / 1.5f);
            Wobble(ARML, s * limb, st == UnitState.Held ? Mathf.Cos(_phase * 1.3f) * 50f : 0);
            Wobble(ARMR, st == UnitState.Attacking ? -Mathf.Abs(s) * limb * 1.6f : -s * limb, st == UnitState.Held ? -Mathf.Cos(_phase * 1.1f) * 50f : 0);
            Wobble(LEGL, -s * limb, 0);
            Wobble(LEGR, s * limb, 0);
            Wobble(HEAD, st == UnitState.Held ? s * 20f : s * 4f, 0);
        }

        void Wobble(int i, float pitch, float roll)
        {
            if (_parts[i] == null) return;
            _parts[i].localRotation = _baseRot[i] * Quaternion.Euler(pitch, 0, roll);
        }
    }
}
