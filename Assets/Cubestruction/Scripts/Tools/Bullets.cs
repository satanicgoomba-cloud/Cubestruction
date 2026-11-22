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
            public float life, scale, age;
            public float burrowLeft, digAt; public bool burrowing;
            public System.Random rng;     // per-bullet, seeded from the volley: splits match on every peer
            public ulong owner;
            public bool authority;
            public bool stuck; public Vector3 warpTo;   // Warp Kunai sitting where it struck
            public Transform visual;
            public TrailRenderer trail;
            public LineRenderer str;      // Saw Yo-yo string back to the thrower's hand
        }

        public Material material;   // glow material from FxHub

        readonly List<Bullet> _live = new List<Bullet>();
        readonly Stack<Transform> _pool = new Stack<Transform>();
        const float MaxStep = 1f / 90f;
        const int MaxLive = 600;

        /// <summary>Counters for the -autotest smoke test.</summary>
        public static int Fired, Impacts, Bounces, Returns, Warps, Rides;
        public static Vector3 LastWarp;

        public int LiveCount => _live.Count;

        public void Fire(ToolDef t, Vector3 origin, Vector3 dir, int seed, ulong owner, bool authority, float scale)
        {
            var rng = new System.Random(seed);
            int pellets = Mathf.Max(1, t.pellets);
            // the direction's length is a speed multiplier (VR throws: how hard you threw); 1 = normal
            float mul = dir.magnitude;
            dir = dir.normalized;
            mul = mul > 0.98f && mul < 1.02f ? 1f : Mathf.Clamp(mul, 0.2f, 2f);
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
                    rng = new System.Random(seed * 31 + i),
                    def = t, pos = origin, vel = d * t.speed * scale * mul,
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
                b.age += dt;
                if (alive && b.def.splitAfterS > 0 && b.age >= b.def.splitAfterS && !b.burrowing)
                {
                    Split(b, b.pos, Vector3.zero);   // fireworks: burst in the air
                    alive = false;
                }
                if (!alive || b.life <= 0 || b.pos.y < -20f)
                {
                    if (b.def.popOnEnd && b.pos.y > -20f) Pop(b);
                    Release(b); _live.RemoveAt(i); continue;
                }
                b.visual.position = b.pos;
                if (b.def.carve && b.def.returnS > 0) DrawString(b);
                if (b.def.bulletShape == "disc" && b.vel.sqrMagnitude > 0.01f)
                    b.visual.rotation = Quaternion.LookRotation(b.vel) * Quaternion.Euler(0, Time.time * 1440f % 360f, 0);
                else if (b.def.bulletShape == "torpedo" && b.vel.sqrMagnitude > 0.01f)
                    b.visual.rotation = Quaternion.LookRotation(b.vel) * Quaternion.Euler(0, 0, Time.time * 900f % 360f);
            }
            if (_spawn.Count > 0) { _live.AddRange(_spawn); _spawn.Clear(); }
        }

        readonly List<Bullet> _spawn = new List<Bullet>();

        /// <summary>A shell bursts into bouncing bomblets (same seeded directions on every peer).</summary>
        void Split(Bullet b, Vector3 point, Vector3 normal)
        {
            var child = b.def.SplitDef;
            for (int i = 0; i < b.def.splitCount && _live.Count + _spawn.Count < MaxLive; i++)
            {
                var r = new Vector3((float)b.rng.NextDouble() * 2 - 1, (float)b.rng.NextDouble() * 2 - 1, (float)b.rng.NextDouble() * 2 - 1);
                var dir = (normal * 1.3f + r).normalized;
                if (normal == Vector3.zero && r.sqrMagnitude > 1f) r = r.normalized;   // mid-air burst: all directions
                if (normal == Vector3.zero) dir = r.sqrMagnitude > 0.01f ? r.normalized : Vector3.up;
                float speed = (b.def.splitSpeed > 0 ? b.def.splitSpeed * (0.8f + 0.4f * (float)b.rng.NextDouble())
                                                     : 5f + (float)b.rng.NextDouble() * 4f) * b.scale;
                var c = new Bullet
                {
                    def = child, pos = point + normal * 0.05f, vel = dir * speed, bouncesLeft = child.bounces,
                    life = child.lifeS + (float)b.rng.NextDouble() * 0.6f, scale = b.scale, owner = b.owner,
                    authority = b.authority, rng = new System.Random(b.rng.Next()),
                };
                c.visual = Get(child, b.scale); c.visual.position = c.pos;
                c.trail = c.visual.GetComponent<TrailRenderer>(); c.trail.Clear();
                _spawn.Add(c);
            }
        }

        /// <summary>A bomblet pops: explosive damage (falloff to zero) on the server copy, a blast for everyone.</summary>
        void Pop(Bullet b)
        {
            var cube = VoxelCube.Instance;
            if (b.authority && cube && cube.IsSpawned)
                cube.ServerDamage(b.pos, b.def.radiusCells, b.def.damage, b.def.tier, true, b.owner);
            FxHub.Instance?.LocalBlast(b.pos, b.def.radiusCells * (cube ? cube.cellSize : 0.25f) * b.scale, BulletColor(b.def));
        }

        /// <summary>Advances one bullet by dt. Returns false when it is spent.</summary>
        bool Step(Bullet b, float dt, VoxelCube cube)
        {
            var t = b.def;
            if (b.stuck) return true;
            if (b.burrowing) return Burrow(b, dt, cube);
            if (t.carve) return Carve(b, dt, cube);
            if (t.floatS > 0 && b.age > 0.4f && TouchesLocalPlayer(b))
            {
                // Bubble Wand: whoever touches a bubble rides it up (each peer handles its own player)
                PlayerAvatar.Local.Motor.Float(t.floatS);
                Rides++;
                FxHub.Instance?.LocalBlast(b.pos, 0.3f * b.scale, BulletColor(t));
                return false;
            }
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
            if (isVoxel && t.burrowS > 0)
            {
                // Drill Torpedo: start boring straight ahead
                b.burrowing = true; b.burrowLeft = t.burrowS; b.digAt = 0;
                b.vel = dir * t.burrowSpeed * b.scale;
                b.pos = point;
                return true;
            }
            if (t.warpOnHit)
            {
                // Warp Kunai: it sticks where it struck; its thrower presses again to blink there
                float s = b.scale;
                var to = point + normal * 0.4f * s;
                if (normal.y < 0.5f) to -= Vector3.up * 0.6f * s;   // on a wall: hang with your head at the hit
                FxHub.Instance?.LocalBlast(point, 0.35f * b.scale, BulletColor(t));
                if (isVoxel && b.authority && cube.IsSpawned && t.damage > 0)
                    cube.ServerDamage(cube.CellToWorld(cell), t.radiusCells, t.damage, t.tier, false, b.owner);
                b.pos = point; b.vel = Vector3.zero;
                b.stuck = true; b.warpTo = to;
                b.life = Mathf.Max(b.life, 6f);
                return true;
            }
            if (t.explodeOnImpact)
            {
                if (b.authority && cube && cube.IsSpawned) cube.ServerDamage(point, t.radiusCells, t.damage, t.tier, true, b.owner);
                FxHub.Instance?.LocalBlast(point, t.radiusCells * (cube ? cube.cellSize : 0.25f) * b.scale, BulletColor(t));
                b.pos = point;
                return false;
            }
            if (t.gooRadiusCells > 0)
            {
                if (isVoxel && b.authority && cube.IsSpawned) cube.ServerGoo(cell, t.gooRadiusCells);
                FxHub.Instance?.LocalBlast(point, 0.25f * b.scale, BulletColor(t));
                b.pos = point;
                return false;
            }
            if (!t.popOnEnd && t.damage > 0)   // bomblets only bounce; they damage when they pop
            {
                if (isVoxel && b.authority && cube.IsSpawned)
                    cube.ServerDamage(cube.CellToWorld(cell), t.radiusCells, t.damage, t.tier, false, b.owner);
                FxHub.Instance?.BulletImpact(point, t, b.authority);
            }
            if (t.splitCount > 0) { Split(b, point, normal); return false; }

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
                if (t.bulletShape == "disc") b.pierceLeft = t.pierceCells;   // discs slice again after each ricochet
                return true;
            }
            b.pos = point;
            return false;
        }

        /// <summary>Drill Torpedo burrowing: straight through the cube, digging a tunnel you can climb into.</summary>
        bool Burrow(Bullet b, float dt, VoxelCube cube)
        {
            b.pos += b.vel * dt;
            b.burrowLeft -= dt;
            b.digAt -= dt;
            if (b.digAt <= 0)
            {
                b.digAt = 0.08f;
                if (b.authority && cube && cube.IsSpawned) cube.ServerDamage(b.pos, b.def.radiusCells, b.def.damage, b.def.tier, false, b.owner);
                FxHub.Instance?.BulletImpact(b.pos, b.def, b.authority);
                Impacts++;
            }
            bool outside = true;
            if (cube && cube.Grid != null)
            {
                var c = cube.WorldToCell(b.pos); int n = cube.Grid.N;
                outside = c.x < -3 || c.y < -3 || c.z < -3 || c.x > n + 2 || c.y > n + 2 || c.z > n + 2;
            }
            if (b.burrowLeft > 0 && !outside) return true;
            FxHub.Instance?.LocalBlast(b.pos, 0.8f * b.scale, BulletColor(b.def));
            return false;
        }

        bool TouchesLocalPlayer(Bullet b)
        {
            var me = PlayerAvatar.Local;
            if (me == null || !me.IsSpawned) return false;
            return Vector3.Distance(me.Motor.Chest, b.pos) < (b.def.bulletSize + 0.55f) * b.scale;
        }

        /// <summary>Saw Yo-yo: passes through the cube carving, flies out, then homes back to the thrower's hand.</summary>
        bool Carve(Bullet b, float dt, VoxelCube cube)
        {
            var t = b.def;
            if (t.returnS > 0 && b.age >= t.returnS * 0.5f)
            {
                if (!PlayerAvatar.ByClient.TryGetValue(b.owner, out var pl)) return false;
                var hand = pl.rightHand ? pl.rightHand.position : pl.Motor.Chest;
                if (pl.IsVR && pl.leftHand && Vector3.Distance(pl.leftHand.position, b.pos) < Vector3.Distance(hand, b.pos)) hand = pl.leftHand.position;
                var to = hand - b.pos;
                if (to.magnitude < 0.6f * b.scale) { Returns++; return false; }   // caught
                b.vel = Vector3.Lerp(b.vel, to.normalized * t.speed * b.scale * 1.2f, dt * 8f);
            }
            b.pos += b.vel * dt;
            b.digAt -= dt;
            if (b.digAt <= 0)
            {
                b.digAt = 0.05f;
                if (cube && cube.SolidNear(b.pos, t.radiusCells * cube.cellSize * b.scale, out _))
                {
                    if (b.authority && cube.IsSpawned) cube.ServerDamage(b.pos, t.radiusCells, t.damage, t.tier, false, b.owner);
                    FxHub.Instance?.BulletImpact(b.pos, t, b.authority);
                    Impacts++;
                }
            }
            return true;
        }

        /// <summary>Remove <paramref name="owner"/>'s newest Warp Kunai (flying or stuck); where to blink to.</summary>
        public bool TakeKunai(ulong owner, out Vector3 to)
        {
            to = default;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var b = _live[i];
                if (!b.def.warpOnHit || b.owner != owner) continue;
                if (b.stuck) to = b.warpTo;
                else
                {
                    // mid-flight: a step back along its path so you don't land inside a wall
                    var back = b.vel.sqrMagnitude > 0.01f ? -b.vel.normalized : Vector3.up;
                    to = b.pos + back * 0.5f * b.scale - Vector3.up * 0.6f * b.scale;
                }
                Release(b); _live.RemoveAt(i);
                return true;
            }
            return false;
        }

        Transform Get(ToolDef t, float scale)
        {
            var tr = _pool.Count > 0 ? _pool.Pop() : Create();
            tr.gameObject.SetActive(true);
            tr.localScale = (t.bulletShape == "disc" ? new Vector3(2f, 0.35f, 2f) : t.bulletShape == "torpedo" ? new Vector3(1.2f, 1.2f, 4f) : Vector3.one * 2f) * t.bulletSize * scale;
            tr.rotation = Quaternion.identity;
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

        /// <summary>The yo-yo's string: from the thrower's hand to the yo-yo, with a little sag.</summary>
        void DrawString(Bullet b)
        {
            if (!PlayerAvatar.ByClient.TryGetValue(b.owner, out var pl)) return;
            var hand = pl.rightHand ? pl.rightHand.position : pl.Motor.Chest;
            if (pl.IsVR && pl.leftHand && Vector3.Distance(pl.leftHand.position, b.pos) < Vector3.Distance(hand, b.pos)) hand = pl.leftHand.position;
            if (!b.str)
            {
                b.str = b.visual.GetComponent<LineRenderer>();
                if (!b.str)
                {
                    b.str = b.visual.gameObject.AddComponent<LineRenderer>();
                    b.str.sharedMaterial = material;
                    b.str.positionCount = 8;
                    b.str.numCapVertices = 1;
                    b.str.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                b.str.useWorldSpace = true;
                b.str.widthMultiplier = 0.012f * b.scale;
                b.str.startColor = new Color(1f, 1f, 1f, 0.95f); b.str.endColor = new Color(0.8f, 0.9f, 1f, 0.95f);
                b.str.enabled = true;
            }
            float len = Vector3.Distance(hand, b.pos);
            for (int i = 0; i < 8; i++)
            {
                float k = i / 7f;
                // slack while it flies out, taut as it comes back
                float sag = Mathf.Sin(k * Mathf.PI) * Mathf.Min(0.6f, len * 0.08f) * (b.age < b.def.returnS * 0.5f ? 1f : 0.3f);
                b.str.SetPosition(i, Vector3.Lerp(hand, b.pos, k) - Vector3.up * sag);
            }
            Strings++;
        }
        public static int Strings;   // -autotest

        void Release(Bullet b)
        {
            if (b.str) { b.str.enabled = false; b.str = null; }
            else { var lr = b.visual ? b.visual.GetComponent<LineRenderer>() : null; if (lr) lr.enabled = false; }
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
                case "FireworkLauncher":
                    if (t.splitCount == 0) { var fc = Color.HSVToRGB(Random.value, 0.75f, 1f); fc.a = 1f; return fc; }   // stars
                    return new Color(1f, 0.95f, 0.6f, 1f);
                case "DrillTorpedo": return new Color(1f, 0.62f, 0.2f, 1f);
                case "GooGun": return new Color(1f, 0.42f, 0.78f, 0.95f);
                case "SunOrbBlast": return new Color(1f, 0.82f, 0.3f, 0.9f);
                case "WarpKunai": return new Color(0.55f, 0.35f, 1f, 1f);
                case "SawYoyo": return new Color(0.9f, 0.95f, 1f, 1f);
                case "MagnetBoulder": return new Color(0.55f, 0.6f, 0.75f, 1f);
                case "BubbleWand": return new Color(0.75f, 0.95f, 1f, 0.45f);
                case "HeliGatling": return new Color(1f, 0.7f, 0.25f, 1f);
                case "RippleDisc": return new Color(0.75f, 0.55f, 1f, 0.95f);
                case "BoostBlaster": return new Color(1f, 0.55f, 0.3f, 0.95f);
                case "BloomMortar": return t.popOnEnd ? new Color(1f, 0.45f, 0.75f, 0.95f) : new Color(1f, 0.9f, 0.4f, 1f);
                case "PinballPopper":
                    // each ball a different candy colour
                    float h = Random.value;
                    var c = Color.HSVToRGB(h, 0.6f, 1f); c.a = 0.95f; return c;
                default: return new Color(1f, 1f, 1f, 0.9f);
            }
        }
    }
}
