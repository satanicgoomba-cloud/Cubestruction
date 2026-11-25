using System.Linq;
using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Super
{
    /// <summary>
    /// A SUPER! bot (server-driven). It closes in on the nearest foe, throws punch chains and kicks,
    /// blocks (and sometimes parries) when you swing, uses its specials as it unlocks them, powers up as
    /// soon as the bar is full, flies after you once it can, and fires its ultimate at Tier 5.
    /// In Sandbox the bots are training dummies: they stand there, get back up, and never hit back.
    /// </summary>
    [RequireComponent(typeof(NetworkObject), typeof(FighterState))]
    public class FighterBot : NetworkBehaviour
    {
        public readonly NetworkVariable<int> slot = new NetworkVariable<int>();
        public readonly NetworkVariable<bool> dummy = new NetworkVariable<bool>();

        public FighterState State { get; private set; }
        public ulong BotId => BotIdFor(slot.Value);
        public static ulong BotIdFor(int s) => ulong.MaxValue - 200 - (ulong)s;
        public string Name => dummy.Value ? $"Dummy {slot.Value + 1}" : $"{Roster.Get(State ? State.character.Value : 0).name} (bot)";
        /// <summary>0.6 .. 1.2: how sharp this bot is (mash speed, reactions).</summary>
        public float Skill { get; private set; } = 1f;

        Vector3 _vel;
        bool _grounded;
        float _nextAct, _nextSpecial, _blockUntil, _strafe, _beamUntil;
        int _initSlot, _initChar; bool _initDummy;
        Transform _visual;
        public static int Actions;   // -autotest

        public void ServerInit(int s, int character, bool isDummy) { _initSlot = s; _initChar = character; _initDummy = isDummy; }

        void Awake() { State = GetComponent<FighterState>(); }

        public override void OnNetworkSpawn()
        {
            if (IsServer)
            {
                slot.Value = _initSlot; dummy.Value = _initDummy;
                State.character.Value = (byte)_initChar;
                Skill = Random.Range(0.7f, 1.15f);
                _strafe = Random.value < 0.5f ? -1f : 1f;
            }
            _visual = transform.Find("Visual");
        }

        // ------------------------------------------------------------------ server physics

        public void Knock(Vector3 v) { _vel = new Vector3(_vel.x * 0.3f, Mathf.Max(0, _vel.y), _vel.z * 0.3f) + v; _grounded = false; }
        public void Place(Vector3 p, float yaw) { transform.SetPositionAndRotation(p, Quaternion.Euler(0, yaw, 0)); _vel = Vector3.zero; }
        public void Ko(Vector3 fling) { _vel = fling; _grounded = false; }

        float GroundBelow(Vector3 p)
        {
            var cube = VoxelCube.Instance;
            if (cube && cube.Raycast(new Ray(p + Vector3.up * 0.9f, Vector3.down), 40f, out _, out var hit)) return hit.y;
            return float.NegativeInfinity;
        }

        void Move(float dt, Vector3 want)
        {
            bool fly = State.Tier >= State.Def.flyTier + 1 && Mathf.Abs(want.y) > 0.1f;
            if (fly) _vel = Vector3.MoveTowards(_vel, want, 30f * dt);
            else
            {
                var h = Vector3.MoveTowards(new Vector3(_vel.x, 0, _vel.z), new Vector3(want.x, 0, want.z), (_grounded ? 30f : 8f) * dt);
                _vel = new Vector3(h.x, _vel.y + UnityEngine.Physics.gravity.y * 1.4f * dt, h.z);
            }
            var p = transform.position + _vel * dt;
            // walls: don't walk through solid voxels (hits break through them anyway)
            var cube = VoxelCube.Instance;
            var flat = new Vector3(_vel.x, 0, _vel.z);
            if (cube && flat.sqrMagnitude > 0.01f && cube.Raycast(new Ray(transform.position + Vector3.up * 0.5f, flat.normalized), flat.magnitude * dt + 0.35f, out _, out _))
            {
                p.x = transform.position.x; p.z = transform.position.z;
                if (_grounded && !fly) { _vel.y = 7f; _grounded = false; }   // hop over
            }
            float g = GroundBelow(p);
            if (p.y <= g && _vel.y <= 0.5f) { p.y = g; if (_vel.y < 0) _vel.y = 0; _grounded = true; _vel.x *= 1f - Mathf.Clamp01(dt * 5f); _vel.z *= 1f - Mathf.Clamp01(dt * 5f); }
            else _grounded = p.y - g < 0.05f;
            transform.position = p;
        }

        // ------------------------------------------------------------------ AI

        void Update()
        {
            if (_visual) _visual.localPosition = Vector3.up * Mathf.Abs(Mathf.Sin(Time.time * 10f)) * 0.04f * Mathf.Clamp01(new Vector3(_vel.x, 0, _vel.z).magnitude);
            if (!IsServer) return;
            var d = SuperDirector.Instance;
            if (d == null || !d.IsActive || !State.inMatch.Value) return;
            float dt = Time.deltaTime;
            bool stopped = State.Frozen(d.Now);
            if (State.down.Value || stopped) { if (!stopped) Move(dt, Vector3.zero); return; }

            var target = FighterState.All.Where(f => d.Enemies(State, f)).OrderBy(f => (f.transform.position - transform.position).sqrMagnitude).FirstOrDefault();
            if (dummy.Value || target == null)
            {
                Move(dt, Vector3.zero);
                if (target) Face(target.transform.position - transform.position, dt);
                return;
            }

            var to = target.transform.position - transform.position;
            var flat = new Vector3(to.x, 0, to.z);
            float dist = flat.magnitude;
            Face(flat, dt);
            var fwd = flat.sqrMagnitude > 0.01f ? flat.normalized : transform.forward;
            float speed = 5.2f * State.Def.speedMul * Roster.TierSpeed(State.Tier);
            bool canAct = d.CanAct(State);

            // keep away from the edge
            var fromCentre = transform.position - d.Origin; fromCentre.y = 0;
            Vector3 want;
            if (fromCentre.magnitude > SuperArena.Radius - 4f) want = -fromCentre.normalized * speed;
            else if (dist > 1.6f) want = fwd * speed + Vector3.Cross(Vector3.up, fwd) * _strafe * (dist < 6f ? 1.2f : 0f);
            else want = Vector3.Cross(Vector3.up, fwd) * _strafe * 0.8f;
            // flyers chase you up
            if (State.Tier >= State.Def.flyTier + 1 && Mathf.Abs(to.y) > 1.2f) want.y = Mathf.Clamp(to.y * 2f, -speed, speed);
            else if (_grounded && to.y > 1.5f && dist < 4f && Random.value < dt * 2f) { _vel.y = 8f; _grounded = false; }

            // block when the foe is close and swinging (sometimes right on time = parry)
            bool foeSwinging = dist < 2.6f && (target.anim.Value & 15) != 0 && Time.time > _blockUntil + 0.6f && Random.value < dt * 2.2f * Skill;
            if (foeSwinging) _blockUntil = Time.time + Random.Range(0.25f, 0.6f);
            bool block = Time.time < _blockUntil;
            if (State.blocking.Value != block) State.blocking.Value = block;
            if (block) want *= 0.2f;
            if (Time.time < _beamUntil) want = Vector3.zero;
            Move(dt, want);
            if (!canAct || block) return;

            var origin = State.Chest;
            var aim = (target.Chest - origin).normalized;

            // keep a beam on target while it fires
            if (Time.time < _beamUntil) { d.ServerBeamAim(State, origin, aim); return; }

            // power up as soon as possible
            if (State.power.Value >= Roster.PowerMax && State.Tier < Roster.MaxTier && !d.IsSandbox) { d.ServerTransform(State); State.Cue(FighterState.Anim.PowerUp); _nextAct = Time.time + 0.6f; return; }

            // specials
            if (Time.time >= _nextSpecial)
            {
                _nextSpecial = Time.time + Random.Range(1.4f, 3.2f) / Skill;
                for (int s = 3; s >= 0; s--)
                {
                    var m = State.Def.moves[s];
                    if (!d.Unlocked(State, s) || d.CooldownLeft(State, s) > 0 || (s == 3 && State.Tier < Roster.MaxTier)) continue;
                    if (!Useful(m, dist)) continue;
                    if (d.ServerSpecial(State, s, origin, aim, 1f))
                    {
                        Actions++;
                        State.Cue(s == 3 ? FighterState.Anim.Ultimate : m.kind == MoveKind.Beam ? FighterState.Anim.Beam : FighterState.Anim.Special);
                        if (m.kind == MoveKind.Beam) _beamUntil = Time.time + Roster.BeamFire;
                        _nextAct = Time.time + 0.5f;
                        return;
                    }
                }
            }

            // basics
            if (Time.time < _nextAct || dist > Roster.MeleeRange + 0.3f) return;
            Actions++;
            float r = Random.value;
            if (r < 0.72f)
            {
                d.ServerMelee(State, 0, origin, aim);
                State.Cue(State.comboStep == 0 ? FighterState.Anim.Jab : State.comboStep == 1 ? FighterState.Anim.Cross : FighterState.Anim.Hook);
                _nextAct = Time.time + Roster.LightCooldown * Random.Range(1.05f, 1.6f) / Skill;
            }
            else
            {
                byte kind = r < 0.85f ? (byte)1 : (byte)2;
                d.ServerMelee(State, kind, origin, aim);
                State.Cue(kind == 2 ? FighterState.Anim.Launcher : FighterState.Anim.Kick);
                _nextAct = Time.time + Roster.HeavyCooldown * Random.Range(1.1f, 1.5f) / Skill;
                if (Random.value < 0.3f) _strafe = -_strafe;
            }
        }

        bool Useful(MoveDef m, float dist)
        {
            switch (m.kind)
            {
                case MoveKind.FlashStep: case MoveKind.Rush: return dist > 3f && dist < m.radius;
                case MoveKind.RisingStrike: case MoveKind.Grab: return dist < m.radius;
                case MoveKind.Slam: case MoveKind.Freeze: return dist < m.radius * 0.8f;
                case MoveKind.Storm: case MoveKind.AbsoluteZero: return dist < m.radius * 0.8f;
                case MoveKind.TimeStop: return dist < 8f;
                case MoveKind.MeteorDrop: return dist < 30f;
                default: return dist > 2.5f && dist < 30f;   // projectiles, beams, slashes
            }
        }

        void Face(Vector3 dir, float dt)
        {
            dir.y = 0;
            if (dir.sqrMagnitude > 0.01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(dir), dt * 10f);
        }
    }
}
