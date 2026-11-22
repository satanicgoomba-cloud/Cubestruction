using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cubestruction.Voxels
{
    /// <summary>
    /// Pure voxel data + destruction rules (no networking, no rendering). Mirrors
    /// Tools/balance_sim.py exactly: layer-by-depth, tier penalty, linear falloff
    /// (melee to 50%, explosives to 0%), "sand" crumble of loose voxels and
    /// collapse of floating islands.
    /// </summary>
    public class VoxelGrid
    {
        /// <summary>Size along X (and the cube's size for full cubes). Long maps use NX × NY × NZ.</summary>
        public readonly int N;
        public readonly int NX, NY, NZ;
        public int Count => NX * NY * NZ;
        public readonly byte[] layer;   // layer index per cell
        public readonly float[] hp;     // remaining hit points (server authoritative)
        public readonly bool[] solid;
        public int Alive { get; private set; }
        public int CoreAlive { get; private set; }
        public readonly int CoreTotal;
        public readonly int CoreLayer;
        readonly float hpScale;

        /// <summary>Highest solid row + 1 at build time (cells). A full cube is N; shaped maps are lower.</summary>
        public readonly int ContentTop;

        /// <param name="shape">Optional map: returns the layer index of cell (x, y, z), or -1 for empty.
        /// Without it the grid is a full cube layered by depth (Mining / God Siege).</param>
        /// <summary>Optional per-cell HP multiplier by position (Titan Siege walls); kept so Restore/MaxHp agree.</summary>
        readonly Func<int, int, int, float> hpMul;

        /// <summary>Cells that count as resting on the floor (Titan Siege wall tops: they never collapse).</summary>
        readonly bool[] anchor;

        public VoxelGrid(int n, float hpScale = 1f, Func<int, int, int, int> shape = null, int ny = -1, int nz = -1, Func<int, int, int, float> hpMul = null, Func<int, int, int, bool> anchored = null)
        {
            this.hpMul = hpMul;
            N = NX = n; NY = ny > 0 ? ny : n; NZ = nz > 0 ? nz : n;
            this.hpScale = hpScale;
            int count = NX * NY * NZ;
            layer = new byte[count];
            hp = new float[count];
            solid = new bool[count];
            var L = Balance.Layers.layers;
            CoreLayer = L.Length - 1;
            float half = n / 2f;
            int alive = 0, top = 0;
            for (int x = 0; x < NX; x++)
            for (int y = 0; y < NY; y++)
            for (int z = 0; z < NZ; z++)
            {
                int li;
                if (shape != null)
                {
                    li = shape(x, y, z);
                    if (li < 0) { hp[Index(x, y, z)] = -1f; continue; }
                    li = Mathf.Min(li, L.Length - 1);
                }
                else
                {
                    int d = Mathf.Min(Mathf.Min(D(x), D(y)), D(z));
                    float frac = d / half;
                    li = L.Length - 1;
                    for (int i = 0; i < L.Length; i++) if (frac < L[i].maxDepthFraction) { li = i; break; }
                }
                int idx = Index(x, y, z);
                layer[idx] = (byte)li;
                hp[idx] = L[li].hp * hpScale * (hpMul != null ? hpMul(x, y, z) : 1f);
                if (anchored != null && anchored(x, y, z)) { if (anchor == null) anchor = new bool[count]; anchor[idx] = true; }
                solid[idx] = true;
                alive++;
                if (y + 1 > top) top = y + 1;
                if (li == CoreLayer) CoreTotal++;
            }
            Alive = alive;
            CoreAlive = CoreTotal;
            ContentTop = top;
        }

        int D(int i) => Mathf.Min(i, N - 1 - i);

        public int Index(int x, int y, int z) => (x * NY + y) * NZ + z;
        public Vector3Int Coord(int idx) { int z = idx % NZ; int y = (idx / NZ) % NY; int x = idx / (NY * NZ); return new Vector3Int(x, y, z); }
        public bool InBounds(int x, int y, int z) => x >= 0 && y >= 0 && z >= 0 && x < NX && y < NY && z < NZ;
        public bool IsSolid(int x, int y, int z) => InBounds(x, y, z) && solid[Index(x, y, z)];
        public bool IsSolid(Vector3Int c) => IsSolid(c.x, c.y, c.z);
        public LayerDef LayerOf(int idx) => Balance.Layers.layers[layer[idx]];
        public float MaxHp(int idx) { var c = Coord(idx); return LayerOf(idx).hp * hpScale * (hpMul != null ? hpMul(c.x, c.y, c.z) : 1f); }

        public bool IsExposed(int x, int y, int z)
        {
            if (!IsSolid(x, y, z)) return false;
            return !IsSolid(x + 1, y, z) || !IsSolid(x - 1, y, z) || !IsSolid(x, y + 1, z) ||
                   !IsSolid(x, y - 1, z) || !IsSolid(x, y, z + 1) || !IsSolid(x, y, z - 1);
        }

        void Kill(int idx, List<int> killed)
        {
            solid[idx] = false;
            hp[idx] = 0;
            Alive--;
            if (layer[idx] == CoreLayer) CoreAlive--;
            killed.Add(idx);
        }

        /// <summary>Apply spherical damage. centre is in cell space (cell centres at integer coords).
        /// Returns killed cell indices appended to <paramref name="killed"/>.</summary>
        public void ApplySphere(Vector3 centre, float radius, float damage, int tier, bool explosive,
                                List<int> killed, Func<int, bool> protectedCell = null)
        {
            float r = Mathf.Max(radius, 0.5f);
            int x0 = Mathf.Max(0, Mathf.FloorToInt(centre.x - r)), x1 = Mathf.Min(NX - 1, Mathf.CeilToInt(centre.x + r));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(centre.y - r)), y1 = Mathf.Min(NY - 1, Mathf.CeilToInt(centre.y + r));
            int z0 = Mathf.Max(0, Mathf.FloorToInt(centre.z - r)), z1 = Mathf.Min(NZ - 1, Mathf.CeilToInt(centre.z + r));
            var L = Balance.Layers.layers;
            int startCount = killed.Count;
            for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            for (int z = z0; z <= z1; z++)
            {
                int idx = Index(x, y, z);
                if (!solid[idx]) continue;
                float dist = Vector3.Distance(centre, new Vector3(x, y, z));
                if (dist > r) continue;
                if (protectedCell != null && protectedCell(idx)) continue;
                float t = Mathf.Clamp01(dist / r);
                float fall = explosive ? 1f - t : 1f - 0.5f * t;
                float dmg = damage * fall * Balance.TierMultiplier(tier, L[layer[idx]].tier);
                hp[idx] -= dmg;
                if (hp[idx] <= 0f) Kill(idx, killed);
            }
            if (killed.Count > startCount)
                Crumble(new Vector3Int(x0 - 1, y0 - 1, z0 - 1), new Vector3Int(x1 + 1, y1 + 1, z1 + 1), killed);
        }

        /// <summary>"Sand" rule: any voxel left with <= 1 solid face-neighbour crumbles away.</summary>
        public void Crumble(Vector3Int min, Vector3Int max, List<int> killed)
        {
            for (int x = Mathf.Max(0, min.x); x <= Mathf.Min(NX - 1, max.x); x++)
            for (int y = Mathf.Max(0, min.y); y <= Mathf.Min(NY - 1, max.y); y++)
            for (int z = Mathf.Max(0, min.z); z <= Mathf.Min(NZ - 1, max.z); z++)
            {
                int idx = Index(x, y, z);
                if (!solid[idx]) continue;
                int n = (IsSolid(x + 1, y, z) ? 1 : 0) + (IsSolid(x - 1, y, z) ? 1 : 0) + (IsSolid(x, y + 1, z) ? 1 : 0) +
                        (IsSolid(x, y - 1, z) ? 1 : 0) + (IsSolid(x, y, z + 1) ? 1 : 0) + (IsSolid(x, y, z - 1) ? 1 : 0);
                if (n <= 1) Kill(idx, killed);
            }
        }

        readonly Queue<int> _bfs = new Queue<int>();
        bool[] _visited;

        /// <summary>Flood fill from the bottom layer (resting on the pedestal). Anything not connected
        /// is a floating island and collapses into sand. O(N^3) — call debounced, server only.</summary>
        public void CollapseFloatingIslands(List<int> killed)
        {
            int count = Count;
            if (_visited == null) _visited = new bool[count];
            Array.Clear(_visited, 0, count);
            _bfs.Clear();
            for (int x = 0; x < NX; x++)
            for (int z = 0; z < NZ; z++)
            {
                int idx = Index(x, 0, z);
                if (solid[idx]) { _visited[idx] = true; _bfs.Enqueue(idx); }
            }
            if (anchor != null)
                for (int i = 0; i < count; i++)
                    if (anchor[i] && solid[i] && !_visited[i]) { _visited[i] = true; _bfs.Enqueue(i); }
            int nn = NY * NZ;
            while (_bfs.Count > 0)
            {
                int idx = _bfs.Dequeue();
                int z = idx % NZ, y = (idx / NZ) % NY, x = idx / nn;
                if (x > 0) Visit(idx - nn); if (x < NX - 1) Visit(idx + nn);
                if (y > 0) Visit(idx - NZ);  if (y < NY - 1) Visit(idx + NZ);
                if (z > 0) Visit(idx - 1);  if (z < NZ - 1) Visit(idx + 1);
            }
            for (int i = 0; i < count; i++)
                if (solid[i] && !_visited[i]) Kill(i, killed);
        }

        // ---- incremental island check: only around cells destroyed since the last check
        int[] _stamp;      // cells reached by a search that found the floor in generation _gen
        int _gen;
        readonly Stack<int> _dfs = new Stack<int>();
        readonly List<int> _region = new List<int>();

        /// <summary>
        /// Same rule as <see cref="CollapseFloatingIslands"/> but only searches from the solid
        /// neighbours of <paramref name="removed"/> (cells destroyed since the last check). Each
        /// search heads downward first and stops at the floor, so in a mostly solid cube it costs a
        /// few dozen steps instead of a flood over the whole grid. A search that can't reach the
        /// floor has found a floating island, which collapses.
        /// </summary>
        public void CollapseIslandsNear(List<int> removed, List<int> killed)
        {
            int count = Count, nn = NY * NZ;
            if (_stamp == null) _stamp = new int[count];
            if (++_gen == int.MaxValue) { Array.Clear(_stamp, 0, count); _gen = 1; }
            foreach (int r in removed)
            {
                int rz = r % NZ, ry = (r / NZ) % NY, rx = r / nn;
                for (int k = 0; k < 6; k++)
                {
                    int x = rx, y = ry, z = rz;
                    switch (k) { case 0: x--; break; case 1: x++; break; case 2: y--; break; case 3: y++; break; case 4: z--; break; default: z++; break; }
                    if (!InBounds(x, y, z)) continue;
                    int seed = Index(x, y, z);
                    if (!solid[seed] || _stamp[seed] == _gen) continue;   // gone, or already known to rest on the floor
                    if (!ReachesFloor(seed)) foreach (int i in _region) Kill(i, killed);
                }
            }
        }

        /// <summary>DFS from seed preferring down; true if it touches y = 0 or a cell already known grounded.
        /// Leaves the visited cells in _region and stamps them grounded on success.</summary>
        bool ReachesFloor(int seed)
        {
            int nn = NY * NZ;
            _region.Clear(); _dfs.Clear();
            int visit = -_gen;   // temporary mark for this search
            _stamp[seed] = visit; _dfs.Push(seed); _region.Add(seed);
            bool grounded = false;
            while (_dfs.Count > 0 && !grounded)
            {
                int idx = _dfs.Pop();
                int z = idx % NZ, y = (idx / NZ) % NY, x = idx / nn;
                if (y == 0 || (anchor != null && anchor[idx])) { grounded = true; break; }
                // push the downward step last so it is explored first
                grounded |= Push(idx + 1, z < NZ - 1, visit); grounded |= Push(idx - 1, z > 0, visit);
                grounded |= Push(idx + nn, x < NX - 1, visit); grounded |= Push(idx - nn, x > 0, visit);
                grounded |= Push(idx + NZ, y < NY - 1, visit); grounded |= Push(idx - NZ, y > 0, visit);
            }
            if (grounded) foreach (int i in _region) _stamp[i] = _gen;
            return grounded;
        }

        bool Push(int idx, bool inBounds, int visit)
        {
            if (!inBounds || !solid[idx]) return false;
            int s = _stamp[idx];
            if (s == _gen) return true;          // joins a region already proven grounded
            if (s == visit) return false;
            _stamp[idx] = visit; _dfs.Push(idx); _region.Add(idx);
            return false;
        }

        void Visit(int idx)
        {
            if (!solid[idx] || _visited[idx]) return;
            _visited[idx] = true;
            _bfs.Enqueue(idx);
        }

        /// <summary>Client-side: mark a cell destroyed as instructed by the server.</summary>
        public void ForceRemove(int idx)
        {
            if (idx < 0 || idx >= solid.Length || !solid[idx]) return;
            solid[idx] = false;
            Alive--;
            if (layer[idx] == CoreLayer) CoreAlive--;
        }

        /// <summary>Restore a cell (God "Regrow" skill). Never restores Core.</summary>
        public bool Restore(int idx)
        {
            if (solid[idx] || layer[idx] == CoreLayer || hp[idx] < 0f) return false;   // hp < 0: never part of the map
            solid[idx] = true;
            hp[idx] = MaxHp(idx);
            Alive++;
            return true;
        }

        /// <summary>Amanatides-Woo voxel traversal. Returns the first solid cell along the ray (cell space).</summary>
        public bool Raycast(Vector3 origin, Vector3 dir, float maxDist, out Vector3Int hit, out Vector3 hitPoint)
        {
            hit = default; hitPoint = default;
            dir.Normalize();
            // Shift so that cell (i) spans [i-0.5, i+0.5]
            Vector3 o = origin + new Vector3(0.5f, 0.5f, 0.5f);
            // Clip to grid bounds first
            if (!ClipRay(o, dir, maxDist, out float tEnter, out float tExit)) return false;
            Vector3 p = o + dir * (tEnter + 1e-4f);
            int x = Mathf.FloorToInt(p.x), y = Mathf.FloorToInt(p.y), z = Mathf.FloorToInt(p.z);
            int sx = dir.x > 0 ? 1 : -1, sy = dir.y > 0 ? 1 : -1, sz = dir.z > 0 ? 1 : -1;
            float tdx = Mathf.Abs(1f / (Mathf.Abs(dir.x) < 1e-6f ? 1e-6f : dir.x));
            float tdy = Mathf.Abs(1f / (Mathf.Abs(dir.y) < 1e-6f ? 1e-6f : dir.y));
            float tdz = Mathf.Abs(1f / (Mathf.Abs(dir.z) < 1e-6f ? 1e-6f : dir.z));
            float tmx = tEnter + ((sx > 0 ? (x + 1 - p.x) : (p.x - x)) * tdx);
            float tmy = tEnter + ((sy > 0 ? (y + 1 - p.y) : (p.y - y)) * tdy);
            float tmz = tEnter + ((sz > 0 ? (z + 1 - p.z) : (p.z - z)) * tdz);
            float t = tEnter;
            while (t <= tExit)
            {
                if (IsSolid(x, y, z))
                {
                    hit = new Vector3Int(x, y, z);
                    hitPoint = origin + dir * t;
                    return true;
                }
                if (tmx < tmy && tmx < tmz) { x += sx; t = tmx; tmx += tdx; }
                else if (tmy < tmz) { y += sy; t = tmy; tmy += tdy; }
                else { z += sz; t = tmz; tmz += tdz; }
            }
            return false;
        }

        bool ClipRay(Vector3 o, Vector3 d, float maxDist, out float tEnter, out float tExit)
        {
            tEnter = 0; tExit = maxDist;
            for (int a = 0; a < 3; a++)
            {
                float oa = o[a], da = d[a], size = a == 0 ? NX : a == 1 ? NY : NZ;
                if (Mathf.Abs(da) < 1e-8f) { if (oa < 0 || oa > size) return false; continue; }
                float t0 = (0 - oa) / da, t1 = (size - oa) / da;
                if (t0 > t1) (t0, t1) = (t1, t0);
                tEnter = Mathf.Max(tEnter, t0); tExit = Mathf.Min(tExit, t1);
                if (tEnter > tExit) return false;
            }
            return true;
        }

        /// <summary>Packs solid flags as bits (for late-join sync).</summary>
        public byte[] PackSolid()
        {
            var bytes = new byte[(solid.Length + 7) / 8];
            for (int i = 0; i < solid.Length; i++) if (solid[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
            return bytes;
        }

        public void UnpackSolid(byte[] bytes, int byteOffset)
        {
            for (int b = 0; b < bytes.Length; b++)
            {
                int baseIdx = (byteOffset + b) * 8;
                for (int bit = 0; bit < 8; bit++)
                {
                    int i = baseIdx + bit;
                    if (i >= solid.Length) break;
                    bool s = (bytes[b] & (1 << bit)) != 0;
                    if (solid[i] && !s) ForceRemove(i);
                    else if (!solid[i] && s) { solid[i] = true; Alive++; if (layer[i] == CoreLayer) CoreAlive++; }
                }
            }
        }
    }
}
