using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Voxels
{
    /// <summary>
    /// The networked cube. The server owns HP and decides what is destroyed; clients only
    /// receive lists of destroyed cell indices and rebuild their chunk meshes.
    /// World layout: the cube's local origin is the centre of its bottom face, resting on the pedestal.
    /// </summary>
    [RequireComponent(typeof(NetworkObject))]
    public class VoxelCube : NetworkBehaviour
    {
        public static VoxelCube Instance { get; private set; }

        [Tooltip("Size of one voxel in metres.")] public float cellSize = 0.25f;
        public Material voxelMaterial;
        public const int ChunkSize = 8;

        public readonly NetworkVariable<int> size = new NetworkVariable<int>(40);
        public readonly NetworkVariable<float> hpScale = new NetworkVariable<float>(1f);
        public readonly NetworkVariable<float> valueScale = new NetworkVariable<float>(1f);
        public readonly NetworkVariable<int> alive = new NetworkVariable<int>();
        public readonly NetworkVariable<int> coreAlive = new NetworkVariable<int>();

        public VoxelGrid Grid { get; private set; }
        public int Total => Grid == null ? 1 : Grid.N * Grid.N * Grid.N;

        /// <summary>Server: called with (killed cells, instigator client id, money awarded).</summary>
        public event Action<int, ulong, int> OnCellsDestroyedServer;
        /// <summary>Server hook used by God Siege shields: return true to protect a cell.</summary>
        public Func<int, bool> DamageFilter;
        /// <summary>All machines: fired when cells disappear (for VFX/audio).</summary>
        public event Action<List<int>> OnCellsRemoved;

        VoxelChunk[,,] _chunks;
        int _chunkCount;
        readonly List<int> _killed = new List<int>(4096);
        readonly HashSet<VoxelChunk> _dirty = new HashSet<VoxelChunk>();
        float _islandCheckAt = -1;
        ulong _lastInstigator;

        /// <summary>Loot voxels (cell indices). Server writes; everyone renders them gold.</summary>
        public NetworkList<int> loot;
        readonly HashSet<int> _lootSet = new HashSet<int>();
        bool _wantLoot;
        public bool IsLoot(int idx) => _lootSet.Contains(idx);
        public int LootCount => _lootSet.Count;
        /// <summary>Server, before Spawn: hide loot in this cube (Mining).</summary>
        public void ServerEnableLoot() => _wantLoot = true;

        void Awake() { Instance = this; loot = new NetworkList<int>(); }
        public override void OnDestroy() { if (Instance == this) Instance = null; base.OnDestroy(); }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Server: configure before spawning.</summary>
        public void ServerConfigure(int n, int players)
        {
            size.Value = n;
            players = Mathf.Max(1, players);
            hpScale.Value = Mathf.Pow(players, Balance.Layers.coopHpExponent);
            valueScale.Value = Mathf.Pow(players, Balance.Layers.coopValueExponent);
        }

        public override void OnNetworkSpawn()
        {
            Build();
            loot.OnListChanged += OnLootChanged;
            foreach (var i in loot) _lootSet.Add(i);
            foreach (var i in _lootSet) MarkCellDirty(i);
            if (IsServer)
            {
                alive.Value = Grid.Alive;
                coreAlive.Value = Grid.CoreAlive;
                if (_wantLoot) ServerPlaceLoot();
            }
            else
            {
                RequestFullStateRpc();
            }
        }

        void Build()
        {
            int n = size.Value;
            Grid = new VoxelGrid(n, hpScale.Value);
            float extent = n * cellSize;
            // local origin = bottom centre
            _chunkCount = Mathf.CeilToInt(n / (float)ChunkSize);
            _chunks = new VoxelChunk[_chunkCount, _chunkCount, _chunkCount];
            for (int x = 0; x < _chunkCount; x++)
            for (int y = 0; y < _chunkCount; y++)
            for (int z = 0; z < _chunkCount; z++)
            {
                var go = new GameObject($"Chunk_{x}_{y}_{z}");
                go.layer = gameObject.layer;
                go.transform.SetParent(transform, false);
                var c = go.AddComponent<VoxelChunk>();
                c.Init(this, new Vector3Int(x, y, z) * ChunkSize, voxelMaterial);
                _chunks[x, y, z] = c;
                c.Rebuild();
            }
        }

        // ------------------------------------------------------------------ coordinate helpers

        public Vector3 CellToWorld(Vector3Int c) => CellToWorld(new Vector3(c.x, c.y, c.z));
        public Vector3 CellToWorld(Vector3 c)
        {
            float half = Grid.N * 0.5f;
            var local = new Vector3((c.x - half + 0.5f) * cellSize, (c.y + 0.5f) * cellSize, (c.z - half + 0.5f) * cellSize);
            return transform.TransformPoint(local);
        }

        public Vector3 WorldToCell(Vector3 world)
        {
            var l = transform.InverseTransformPoint(world);
            float half = Grid.N * 0.5f;
            return new Vector3(l.x / cellSize + half - 0.5f, l.y / cellSize - 0.5f, l.z / cellSize + half - 0.5f);
        }

        public Vector3Int WorldToCellInt(Vector3 world)
        {
            var c = WorldToCell(world);
            return new Vector3Int(Mathf.RoundToInt(c.x), Mathf.RoundToInt(c.y), Mathf.RoundToInt(c.z));
        }

        public Vector3 Center => transform.TransformPoint(new Vector3(0, Grid.N * cellSize * 0.5f, 0));
        public float HalfExtent => Grid.N * cellSize * 0.5f;
        /// <summary>Server: duration of the last floating-island flood fill (perf diagnostics).</summary>
        public static float LastIslandCheckMs;
        /// <summary>Debug (-autotest): cross-check the local island search against a full flood fill.</summary>
        public static bool VerifyIslands;
        public static int MissedIslandCells;

        /// <summary>Is there solid voxel within `radius` metres of a world point? Used for climbing.</summary>
        public bool SolidNear(Vector3 world, float radius, out Vector3 closestWorld)
        {
            closestWorld = default;
            if (Grid == null) return false;
            var c = WorldToCell(world);
            float rc = radius / cellSize + 0.5f;
            int r = Mathf.CeilToInt(rc);
            float best = float.MaxValue;
            var ci = new Vector3Int(Mathf.RoundToInt(c.x), Mathf.RoundToInt(c.y), Mathf.RoundToInt(c.z));
            for (int x = -r; x <= r; x++)
            for (int y = -r; y <= r; y++)
            for (int z = -r; z <= r; z++)
            {
                var p = ci + new Vector3Int(x, y, z);
                if (!Grid.IsSolid(p)) continue;
                // distance from point to the voxel's box
                var d = new Vector3(Mathf.Max(0, Mathf.Abs(c.x - p.x) - 0.5f), Mathf.Max(0, Mathf.Abs(c.y - p.y) - 0.5f), Mathf.Max(0, Mathf.Abs(c.z - p.z) - 0.5f));
                float dist = d.magnitude * cellSize;
                if (dist < best) { best = dist; closestWorld = CellToWorld(p); }
            }
            return best <= radius;
        }

        public bool Raycast(Ray worldRay, float maxDist, out Vector3Int cell, out Vector3 worldPoint)
        {
            cell = default; worldPoint = default;
            if (Grid == null) return false;
            var o = WorldToCell(worldRay.origin);
            var d = transform.InverseTransformDirection(worldRay.direction);
            if (!Grid.Raycast(o, d, maxDist / cellSize, out cell, out var hp)) return false;
            worldPoint = CellToWorld(hp);
            return true;
        }

        /// <summary>World-space normal of the face of <paramref name="cell"/> that <paramref name="worldPoint"/>
        /// (a raycast hit) lies on. Falls back to facing back along <paramref name="dir"/>.</summary>
        public Vector3 FaceNormal(Vector3Int cell, Vector3 worldPoint, Vector3 dir)
        {
            var l = WorldToCell(worldPoint) - new Vector3(cell.x, cell.y, cell.z);   // each axis in [-0.5, 0.5]
            float ax = Mathf.Abs(l.x), ay = Mathf.Abs(l.y), az = Mathf.Abs(l.z);
            Vector3 n;
            if (ax >= ay && ax >= az) n = new Vector3(Mathf.Sign(l.x), 0, 0);
            else if (ay >= az) n = new Vector3(0, Mathf.Sign(l.y), 0);
            else n = new Vector3(0, 0, Mathf.Sign(l.z));
            n = transform.TransformDirection(n);
            return Vector3.Dot(n, dir) < 0 ? n : -dir.normalized;
        }

        // ------------------------------------------------------------------ server damage API

        /// <summary>Server only. Damage voxels in a sphere around a world point. Returns money awarded.</summary>
        public int ServerDamage(Vector3 worldPos, float radiusCells, float damage, int tier, bool explosive, ulong instigator)
        {
            if (!IsServer || Grid == null) return 0;
            _killed.Clear();
            Grid.ApplySphere(WorldToCell(worldPos), radiusCells, damage, tier, explosive, _killed, DamageFilter);
            return FinishKills(instigator);
        }

        /// <summary>Server only. Ray through the grid, damaging up to `pierce` cells (railgun) or the first hit.</summary>
        public int ServerDamageRay(Ray worldRay, float range, float radiusCells, float damage, int tier, int pierce, ulong instigator)
        {
            if (!IsServer || !Raycast(worldRay, range, out var cell, out var point)) return 0;
            _killed.Clear();
            var dir = transform.InverseTransformDirection(worldRay.direction).normalized;
            var start = new Vector3(cell.x, cell.y, cell.z);
            int steps = Mathf.Max(1, pierce);
            for (int i = 0; i < steps; i++)
                Grid.ApplySphere(start + dir * i, radiusCells, damage, tier, false, _killed, DamageFilter);
            return FinishKills(instigator);
        }

        readonly List<int> _removedSinceCheck = new List<int>(4096);

        int FinishKills(ulong instigator)
        {
            if (_killed.Count == 0) return 0;
            _removedSinceCheck.AddRange(_killed);
            _lastInstigator = instigator;
            _islandCheckAt = Time.time + 0.75f;   // debounce the island flood-fill
            return Broadcast(instigator);
        }

        void ServerPlaceLoot()
        {
            var counts = Loot.CountsFor(Grid.N);
            var rng = new System.Random(System.Environment.TickCount);
            int total = Grid.N * Grid.N * Grid.N;
            var want = (int[])counts.Clone();
            for (int tries = 0; tries < 400000; tries++)
            {
                int idx = rng.Next(total);
                int li = Grid.layer[idx];
                if (li >= want.Length || want[li] <= 0 || _lootSet.Contains(idx)) continue;
                want[li]--;
                loot.Add(idx);
                bool done = true; foreach (var w in want) if (w > 0) { done = false; break; }
                if (done) break;
            }
        }

        void OnLootChanged(NetworkListEvent<int> e)
        {
            switch (e.Type)
            {
                case NetworkListEvent<int>.EventType.Add: _lootSet.Add(e.Value); MarkCellDirty(e.Value); break;
                case NetworkListEvent<int>.EventType.Remove:
                case NetworkListEvent<int>.EventType.RemoveAt: _lootSet.Remove(e.Value); break;
                default:
                    _lootSet.Clear(); foreach (var i in loot) _lootSet.Add(i);
                    break;
            }
        }

        void MarkCellDirty(int idx)
        {
            var c = Grid.Coord(idx);
            if (_chunks == null) return;
            _dirty.Add(_chunks[c.x / ChunkSize, c.y / ChunkSize, c.z / ChunkSize]);
        }

        int Broadcast(ulong instigator)
        {
            if (_lootSet.Count > 0)
                foreach (var idx in _killed)
                    if (_lootSet.Remove(idx))
                    {
                        loot.Remove(idx);
                        Loot.ServerGrant(instigator, Grid.layer[idx], CellToWorld(Grid.Coord(idx)));
                    }
            int money = 0;
            foreach (var idx in _killed) money += Grid.LayerOf(idx).value;
            money = Mathf.RoundToInt(money * valueScale.Value);
            alive.Value = Grid.Alive;
            coreAlive.Value = Grid.CoreAlive;
            var arr = _killed.ToArray();
            // Chunked to stay well under transport payload limits.
            const int max = 1500;
            for (int i = 0; i < arr.Length; i += max)
            {
                int len = Mathf.Min(max, arr.Length - i);
                var part = new int[len];
                Array.Copy(arr, i, part, 0, len);
                CellsDestroyedRpc(part);
            }
            OnCellsDestroyedServer?.Invoke(arr.Length, instigator, money);
            return money;
        }

        void Update()
        {
            if (IsServer && _islandCheckAt > 0 && Time.time >= _islandCheckAt)
            {
                _islandCheckAt = -1;
                _killed.Clear();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Grid.CollapseIslandsNear(_removedSinceCheck, _killed);
                LastIslandCheckMs = (float)sw.Elapsed.TotalMilliseconds;
                _removedSinceCheck.Clear();
                if (VerifyIslands)
                {
                    // debug: the full flood fill must find nothing the local check missed
                    int before = _killed.Count;
                    Grid.CollapseFloatingIslands(_killed);
                    MissedIslandCells += _killed.Count - before;
                    if (_killed.Count > before) Debug.LogWarning($"[VoxelCube] local island check missed {_killed.Count - before} cells");
                }
                if (LastIslandCheckMs > 8f) Debug.Log($"[VoxelCube] island check took {LastIslandCheckMs:0.0} ms ({Grid.N}^3)");
                if (_killed.Count > 0) Broadcast(_lastInstigator);
            }
            if (_dirty.Count > 0)
            {
                foreach (var c in _dirty) c.Rebuild();
                _dirty.Clear();
            }
        }

        /// <summary>Server only (God Siege "Regrow"). Restores up to `max` missing non-core cells near a point.</summary>
        public int ServerRegrow(Vector3 worldPos, float radiusM, int max)
        {
            if (!IsServer) return 0;
            var c = WorldToCell(worldPos);
            int r = Mathf.CeilToInt(radiusM / cellSize);
            var restored = new List<int>();
            for (int x = -r; x <= r && restored.Count < max; x++)
            for (int y = -r; y <= r && restored.Count < max; y++)
            for (int z = -r; z <= r && restored.Count < max; z++)
            {
                var p = new Vector3Int(Mathf.RoundToInt(c.x) + x, Mathf.RoundToInt(c.y) + y, Mathf.RoundToInt(c.z) + z);
                if (!Grid.InBounds(p.x, p.y, p.z) || new Vector3(x, y, z).magnitude > r) continue;
                int idx = Grid.Index(p.x, p.y, p.z);
                if (Grid.Restore(idx)) restored.Add(idx);
            }
            if (restored.Count > 0)
            {
                alive.Value = Grid.Alive;
                CellsRestoredRpc(restored.ToArray());
            }
            return restored.Count;
        }

        // ------------------------------------------------------------------ RPCs

        [Rpc(SendTo.ClientsAndHost)]
        void CellsDestroyedRpc(int[] cells)
        {
            var list = new List<int>(cells.Length);
            foreach (var idx in cells)
            {
                if (!IsServer) Grid.ForceRemove(idx);
                list.Add(idx);
                MarkDirty(idx);
            }
            OnCellsRemoved?.Invoke(list);
            VoxelDebris.Instance?.Spawn(this, list);
        }

        [Rpc(SendTo.NotServer)]
        void CellsRestoredRpc(int[] cells)
        {
            foreach (var idx in cells) { Grid.Restore(idx); MarkDirty(idx); }
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        void RequestFullStateRpc(RpcParams p = default)
        {
            var packed = Grid.PackSolid();
            const int chunk = 2048;
            for (int i = 0; i < packed.Length; i += chunk)
            {
                int len = Mathf.Min(chunk, packed.Length - i);
                var part = new byte[len];
                Array.Copy(packed, i, part, 0, len);
                FullStateChunkRpc(i, part, RpcTarget.Single(p.Receive.SenderClientId, RpcTargetUse.Temp));
            }
        }

        [Rpc(SendTo.SpecifiedInParams)]
        void FullStateChunkRpc(int byteOffset, byte[] data, RpcParams p = default)
        {
            Grid.UnpackSolid(data, byteOffset);
            // mark every chunk covered by this byte range dirty
            int first = byteOffset * 8, last = Mathf.Min((byteOffset + data.Length) * 8, Total) - 1;
            for (int i = first; i <= last; i += 64) MarkDirty(i);
            MarkDirty(last);
        }

        void MarkDirty(int idx)
        {
            var c = Grid.Coord(idx);
            MarkChunk(c);
            // neighbours on chunk borders need rebuilding too (faces become visible)
            if (c.x % ChunkSize == 0) MarkChunk(c + Vector3Int.left);
            if (c.x % ChunkSize == ChunkSize - 1) MarkChunk(c + Vector3Int.right);
            if (c.y % ChunkSize == 0) MarkChunk(c + Vector3Int.down);
            if (c.y % ChunkSize == ChunkSize - 1) MarkChunk(c + Vector3Int.up);
            if (c.z % ChunkSize == 0) MarkChunk(c + new Vector3Int(0, 0, -1));
            if (c.z % ChunkSize == ChunkSize - 1) MarkChunk(c + new Vector3Int(0, 0, 1));
        }

        void MarkChunk(Vector3Int cell)
        {
            if (!Grid.InBounds(cell.x, cell.y, cell.z)) return;
            _dirty.Add(_chunks[cell.x / ChunkSize, cell.y / ChunkSize, cell.z / ChunkSize]);
        }
    }
}
