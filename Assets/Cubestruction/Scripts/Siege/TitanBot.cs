using Cubestruction.Voxels;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Siege
{
    /// <summary>
    /// The AI Titan (Titan Siege without a VR player): server-driven. Picks a spot on the current wall,
    /// walks (runs when far) through the town, punches whatever blocks it, kicks the wall every few
    /// seconds and keeps going until it's through. Everyone sees the same rigged Titan (TitanVisual) posed
    /// from its root, a head and two hands that every peer animates from the synced action counter.
    /// </summary>
    [RequireComponent(typeof(NetworkObject), typeof(CharacterController))]
    public class TitanBot : NetworkBehaviour
    {
        public static TitanBot Instance { get; private set; }

        /// <summary>Changes every punch/kick: 1 = left punch, 2 = right punch, 3 = kick (low 2 bits), counter above.</summary>
        public readonly NetworkVariable<int> action = new NetworkVariable<int>();

        public TitanBody Body { get; private set; }
        /// <summary>When a rocket reaches its hand: catch it about a third of the time (else it blocks).</summary>
        public bool WantsCatch => Random.value < 0.35f;
        public Vector3 Velocity { get; private set; }

        CharacterController _cc;
        Transform _head, _left, _right;
        float _laneX, _vy, _attackAt, _kickAt, _blocked, _punchT = -1;
        int _lastAction, _punchHand;
        Vector3 _lastPos;
        TitanVisual _visual;
        public static int Punches, Steps;   // -autotest

        void Awake()
        {
            _cc = GetComponent<CharacterController>();
            _head = Child("Head", new Vector3(0, 0.86f, 0));
            _left = Child("LeftHand", new Vector3(-0.3f, 0.45f, 0.05f));
            _right = Child("RightHand", new Vector3(0.3f, 0.45f, 0.05f));
            Body = new TitanBody(this, _head, _left, _right);
        }

        Transform Child(string n, Vector3 local)
        {
            var t = transform.Find(n);
            if (!t) { t = new GameObject(n).transform; t.SetParent(transform, false); }
            t.localPosition = local;
            return t;
        }

        public override void OnNetworkSpawn()
        {
            Instance = this;
            transform.localScale = Vector3.one * TitanDirector.Scale;
            _visual = gameObject.AddComponent<TitanVisual>();
            _visual.Init(transform, _head, _left, _right, false, "AITitan");
            _lastPos = transform.position;
            if (IsServer) { _laneX = Random.Range(-6f, 6f); _kickAt = Time.time + 4f; }
            else _cc.enabled = false;
        }

        public override void OnNetworkDespawn() { if (Instance == this) Instance = null; }

        void Update()
        {
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            if (IsServer) Think(dt);
            Velocity = (transform.position - _lastPos) / dt;
            _lastPos = transform.position;
            AnimateHands(dt);
        }

        // ------------------------------------------------------------------ AI (server)

        void Think(float dt)
        {
            var d = TitanDirector.Instance;
            var cube = VoxelCube.Instance;
            if (d == null || !d.IsActive || cube == null || cube.Grid == null) return;
            float s = transform.localScale.x;
            var o = d.Origin;
            _vy = _cc.isGrounded ? -1f : _vy - 30f * dt;
            if (transform.position.y < o.y - 8f) { Teleport(TitanMap.TitanStart(o) + Vector3.forward * 2f); return; }
            if (d.prep.Value) { _cc.Move(Vector3.up * _vy * dt); return; }

            // head for a spot just in front of the current wall; once there (or stuck on a house) start smashing
            int w = Mathf.Min(d.wall.Value, TitanMap.Walls.Length - 1);
            float standZ = TitanMap.WallNearZ(o, w) - 0.42f * s;
            var goal = new Vector3(o.x + _laneX, transform.position.y, standZ);
            var to = goal - transform.position; to.y = 0;
            // at the wall once it's (nearly) up to its punching spot; it never turns back to fix a small overshoot
            bool atWall = transform.position.z >= standZ - 0.8f;
            var fwd = atWall ? Vector3.forward : (to + Vector3.forward * 2f).normalized;
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(fwd), dt * 3f);

            if (!atWall)
            {
                float speed = (to.magnitude > 14f ? 8f : 5f) * (1f + 0.25f * d.rage.Value);   // rage: faster
                var before = transform.position;
                _cc.Move((fwd * speed + Vector3.up * _vy) * dt);
                var moved = transform.position - before; moved.y = 0;
                _blocked = moved.magnitude < speed * dt * 0.3f ? _blocked + dt : 0;
                Steps++;
            }
            else
            {
                _cc.Move(Vector3.up * _vy * dt);
                // through the wall: push on once the hole is big enough
                if (!cube.Raycast(new Ray(transform.position + Vector3.up * 0.5f * s, Vector3.forward), 0.9f * s, out _, out _) &&
                    !cube.Raycast(new Ray(transform.position + Vector3.up * 0.15f * s, Vector3.forward), 0.9f * s, out _, out _))
                    _cc.Move(Vector3.forward * 5f * dt);
            }

            if ((atWall || _blocked > 0.5f) && Time.time >= _attackAt)
            {
                _attackAt = Time.time + 0.55f / (1f + 0.3f * d.rage.Value);
                _punchHand ^= 1;
                // aim high, middle and low in turn so the hole grows to the Titan's size
                float h = new[] { 0.62f, 0.4f, 0.2f }[Random.Range(0, 3)];
                float side = (_punchHand == 0 ? -0.12f : 0.12f) * s;
                var origin = transform.position + Vector3.up * h * s + transform.right * side;
                var dir = transform.forward;
                if (cube.Raycast(new Ray(origin, dir), 0.95f * s, out _, out var p))
                {
                    d.ServerTitanHit(p, dir * 3.2f * s, (byte)_punchHand);
                    Punches++;
                }
                action.Value = ((action.Value >> 2) + 1 << 2) | (_punchHand + 1);
            }
            if ((atWall || _blocked > 0.5f) && Time.time >= _kickAt)
            {
                _kickAt = Time.time + 5f;
                d.ServerKick(Random.value < 0.5f);
                action.Value = ((action.Value >> 2) + 1 << 2) | 3;
            }
        }

        public void Teleport(Vector3 p)
        {
            _cc.enabled = false;
            transform.position = p;
            _cc.enabled = IsServer;
            _vy = 0;
        }

        // ------------------------------------------------------------------ hands (every peer)

        void AnimateHands(float dt)
        {
            int a = action.Value;
            if (a != _lastAction) { _lastAction = a; if ((a & 3) == 1 || (a & 3) == 2) { _punchT = 0; _punchHand = (a & 3) - 1; } }
            float speed = new Vector2(Velocity.x, Velocity.z).magnitude / transform.localScale.x;
            float swing = Mathf.Sin(Time.time * 6f) * Mathf.Clamp01(speed / 0.5f) * 0.12f;
            var l = new Vector3(-0.3f, 0.45f, 0.05f + swing);
            var r = new Vector3(0.3f, 0.45f, 0.05f - swing);
            if (_punchT >= 0)
            {
                _punchT += dt / 0.45f;
                float k = Mathf.Sin(Mathf.Clamp01(_punchT) * Mathf.PI);
                var hit = new Vector3(_punchHand == 0 ? -0.12f : 0.12f, 0.6f, 0.62f);
                if (_punchHand == 0) l = Vector3.Lerp(l, hit, k); else r = Vector3.Lerp(r, hit, k);
                if (_punchT >= 1f) _punchT = -1;
            }
            // a hand comes up to meet an incoming rocket (block it, or catch it)
            float s = transform.localScale.x;
            Tools.ExplosiveProjectile threat = null; float best = 14f;
            foreach (var e in Tools.ExplosiveProjectile.Live)
            {
                if (e == null || e.IsHeld || e.Def == null || e.Def.id != "RocketLauncher") continue;
                float dd = Vector3.Distance(e.transform.position, transform.position + Vector3.up * 0.6f * s);
                if (dd < best && Vector3.Dot(e.transform.position - transform.position, transform.forward) > 0) { best = dd; threat = e; }
            }
            if (threat != null && _punchT < 0)
            {
                var local = transform.InverseTransformPoint(threat.transform.position);
                var reach = Vector3.ClampMagnitude(local - new Vector3(0, 0.62f, 0), 0.32f) + new Vector3(0, 0.62f, 0);
                if (local.x >= 0) r = Vector3.Lerp(r, reach, 0.85f); else l = Vector3.Lerp(l, reach, 0.85f);
            }
            _left.localPosition = l; _right.localPosition = r;
            _head.localRotation = Quaternion.identity;
        }
    }
}
