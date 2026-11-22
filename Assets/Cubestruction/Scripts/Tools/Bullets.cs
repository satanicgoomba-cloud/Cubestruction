using System.Collections.Generic;
using Cubestruction.Player;
using Cubestruction.Voxels;
using UnityEngine;

namespace Cubestruction.Tools
{
    /// <summary>
    /// Physics bullets for projectile guns (tools with speed &gt; 0): gravity, ricochets off voxels
    /// and the arena, piercing, and damage on every impact. No NetworkObject per bullet: every peer
    /// simulates the same volley from (tool, origin, direction, seed); only the server's copy
    /// (<c>authority</c>) damages voxels, everyone else's is just for looks.
    /// Lives on the FxHub object; FxHub.FireBullets is the entry point.
    /// </summary>
    public class Bullets : MonoBehaviour
    {
        class Bullet
        {
            public ToolDef def;
            public Vector3 pos, vel;
            public int bouncesLeft, pierceLeft;
            public float life, scale;
            public ulong owner;
            public bool authority;
            public Transform visual;
            public TrailRenderer trail;
        }

        public Material material;   // glow material from FxHub

        readonly List<Bullet> _live = new List<Bullet>();
        readonly Stack<Transform> _pool = new Stack<Transform>();
        const float MaxStep = 1f / 90f;
        const int MaxLive = 600;

        /// <summary>Counters for the -autotest smoke test.</summary>
        public static int Fired, Impacts, Bounces;

        public int LiveCount => _live.Count;

        public void Fire(ToolDef t, Vector3 origin, Vector3 dir, int seed, ulong owner, bool authority, float scale)
        {
            var rng = new System.Random(seed);
            int pellets = Mathf.Max(1, t.pellets);
            dir = dir.normalized;
            for (int i = 0; i < pellets && _live.Count < MaxLive; i++)
            {
                var d = dir;
                if (t.spreadDeg > 0)
                {
                    // uniform in a cone, identical on every peer for the same seed
                    float a = (float)rng.NextDouble() * Mathf.PI * 2f, r = Mathf.Sqrt((float)rng.NextDouble()) * t.spreadDeg;
                    d = Quaternion.AngleAxis(r, Quaternion.AngleAxis(a * Mathf.Rad2Deg, dir) * Perp(dir)) * dir;
                }
                var b = new Bullet
                {
                    def = t, pos = origin, vel = d * t.speed * scale,
                    bouncesLeft = t.bounces, pierceLeft = t.pierceCells,
                    life = t.lifeS > 0 ? t.lifeS : 3f, scale = scale, owner = owner, authority = authority,
                };
                b.visual = Get(t, scale);
                b.visual.position = origin;
                b.trail = b.visual.GetComponent<TrailRenderer>();
                b.trail.Clear();
                _live.Add(b);
                Fired++;
            }
        }

        static Vector3 Perp(Vector3 v) => Vector3.Cross(v, Mathf.Abs(v.y) < 0.9f ? Vector3.up : Vector3.right).normalized;

        void Update()
        {
            float dt = Time.deltaTime;
            var cube = VoxelCube.Instance;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var b = _live[i];
                float left = dt;
                bool alive = true;
                while (left > 0 && alive)
                {
                    float step = Mathf.Min(left, MaxStep);
                    left -= step;
                    alive = Step(b, step, cube);
                }
                b.life -= dt;
                if (!alive || b.life <= 0 || b.pos.y < -20f) { Release(b); _live.RemoveAt(i); continue; }
                b.visual.position = b.pos;
            }
        }

        /// <summary>Advances one bullet by dt. Returns false when it is spent.</summary>
        bool Step(Bullet b, float dt, VoxelCube cube)
        {
            var t = b.def;
            b.vel += Physics.gravity * t.gravity * b.scale * dt;
            var move = b.vel * dt;
            float dist = move.magnitude;
            if (dist < 1e-5f) return true;
            var dir = move / dist;
            var ray = new Ray(b.pos, dir);

            // nearest of: a solid voxel, or other world geometry (pedestal, arena, kiosk)
            bool hit = false; float hitDist = float.MaxValue; Vector3 point = default, normal = Vector3.up; Vector3Int cell = default; bool isVoxel = false;
            if (cube && cube.Raycast(ray, dist, out var c, out var p))
            {
                hit = true; isVoxel = true; cell = c; point = p; hitDist = Vector3.Distance(b.pos, p);
                normal = cube.FaceNormal(c, p, dir);
            }
            if (Physics.Raycast(ray, out var ph, dist, ~(1 << 5), QueryTriggerInteraction.Ignore) && ph.distance < hitDist
                && ph.collider.GetComponentInParent<VoxelChunk>() == null && ph.collider.GetComponentInParent<PlayerAvatar>() == null)
            {
                hit = true; isVoxel = false; point = ph.point; normal = ph.normal; hitDist = ph.distance;
            }
            if (!hit) { b.pos += move; return true; }

            Impacts++;
            if (isVoxel && b.authority && cube.IsSpawned)
                cube.ServerDamage(cube.CellToWorld(cell), t.radiusCells, t.damage, t.tier, false, b.owner);
            FxHub.Instance?.BulletImpact(point, t, b.authority);

            if (isVoxel && b.pierceLeft > 0)
            {
                // punch through: keep flying, one pierce per cell
                b.pierceLeft--;
                b.pos = point + dir * (cube.cellSize * 1.01f);
                return true;
            }
            if (b.bouncesLeft > 0 && b.vel.magnitude > 2f)
            {
                b.bouncesLeft--;
                Bounces++;
                float keep = t.restitution > 0 ? t.restitution : 0.6f;
                b.vel = Vector3.Reflect(b.vel, normal) * keep;
                b.pos = point + normal * 0.02f;
                return true;
            }
            return false;
        }

        Transform Get(ToolDef t, float scale)
        {
            var tr = _pool.Count > 0 ? _pool.Pop() : Create();
            tr.gameObject.SetActive(true);
            tr.localScale = Vector3.one * t.bulletSize * 2f * scale;
            var col = BulletColor(t);
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", col);
            tr.GetComponent<MeshRenderer>().SetPropertyBlock(block);
            var trail = tr.GetComponent<TrailRenderer>();
            trail.time = t.speed > 120f ? 0.08f : 0.18f;
            trail.widthMultiplier = t.bulletSize * 1.6f * scale;
            trail.startColor = new Color(col.r, col.g, col.b, 0.7f);
            trail.endColor = new Color(col.r, col.g, col.b, 0f);
            return tr;
        }

        Transform Create()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = "Bullet";
            Destroy(go.GetComponent<Collider>());
            go.GetComponent<MeshRenderer>().sharedMaterial = material;
            go.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var trail = go.AddComponent<TrailRenderer>();
            trail.sharedMaterial = material;
            trail.minVertexDistance = 0.05f;
            trail.numCapVertices = 2;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            go.transform.SetParent(transform, false);
            return go.transform;
        }

        void Release(Bullet b)
        {
            if (!b.visual) return;
            b.trail.Clear();
            b.visual.gameObject.SetActive(false);
            _pool.Push(b.visual);
        }

        public static Color BulletColor(ToolDef t)
        {
            switch (t.id)
            {
                case "Minigun": return new Color(0.55f, 0.95f, 1f, 0.9f);
                case "Shotgun": return new Color(1f, 0.82f, 0.35f, 0.95f);
                case "RailGun": return new Color(0.8f, 1f, 1f, 1f);
                case "PinballPopper":
                    // each ball a different candy colour
                    float h = Random.value;
                    var c = Color.HSVToRGB(h, 0.6f, 1f); c.a = 0.95f; return c;
                default: return new Color(1f, 1f, 1f, 0.9f);
            }
        }
    }
}
