using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Cubestruction.Voxels
{
    /// <summary>An 8x8x8 block of the cube. Builds a face-culled mesh (only faces next to air)
    /// with per-voxel vertex colours and glow (uv2.x) for the AeroGloss shader, plus a MeshCollider.</summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class VoxelChunk : MonoBehaviour
    {
        VoxelCube _cube;
        Vector3Int _origin;
        Mesh _mesh;
        MeshCollider _collider;

        static readonly List<Vector3> V = new List<Vector3>(8192);
        static readonly List<Vector3> Nrm = new List<Vector3>(8192);
        static readonly List<Color32> Col = new List<Color32>(8192);
        static readonly List<Vector2> Uv = new List<Vector2>(8192);
        static readonly List<Vector2> Uv2 = new List<Vector2>(8192);
        static readonly List<int> Tri = new List<int>(12288);

        static readonly Vector3Int[] Dirs =
        {
            Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down, new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1)
        };

        // 4 corners per face, CCW when viewed from outside (Unity uses clockwise front faces -> we wind accordingly)
        static readonly Vector3[][] Faces =
        {
            new[] { new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,.5f,-.5f), new Vector3(.5f,.5f,.5f), new Vector3(.5f,-.5f,.5f) },
            new[] { new Vector3(-.5f,-.5f,.5f), new Vector3(-.5f,.5f,.5f), new Vector3(-.5f,.5f,-.5f), new Vector3(-.5f,-.5f,-.5f) },
            new[] { new Vector3(-.5f,.5f,-.5f), new Vector3(-.5f,.5f,.5f), new Vector3(.5f,.5f,.5f), new Vector3(.5f,.5f,-.5f) },
            new[] { new Vector3(-.5f,-.5f,.5f), new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,-.5f,.5f) },
            new[] { new Vector3(.5f,-.5f,.5f), new Vector3(.5f,.5f,.5f), new Vector3(-.5f,.5f,.5f), new Vector3(-.5f,-.5f,.5f) },
            new[] { new Vector3(-.5f,-.5f,-.5f), new Vector3(-.5f,.5f,-.5f), new Vector3(.5f,.5f,-.5f), new Vector3(.5f,-.5f,-.5f) },
        };

        public void Init(VoxelCube cube, Vector3Int origin, Material mat)
        {
            _cube = cube;
            _origin = origin;
            _mesh = new Mesh { name = name, indexFormat = IndexFormat.UInt32 };
            _mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = GetComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = ShadowCastingMode.Off;   // Quest budget
            _collider = gameObject.AddComponent<MeshCollider>();
        }

        public void Rebuild()
        {
            var g = _cube.Grid;
            float s = _cube.cellSize;
            V.Clear(); Nrm.Clear(); Col.Clear(); Uv.Clear(); Uv2.Clear(); Tri.Clear();
            var layers = Balance.Layers.layers;
            int n = g.N;
            for (int x = _origin.x; x < Mathf.Min(_origin.x + VoxelCube.ChunkSize, n); x++)
            for (int y = _origin.y; y < Mathf.Min(_origin.y + VoxelCube.ChunkSize, n); y++)
            for (int z = _origin.z; z < Mathf.Min(_origin.z + VoxelCube.ChunkSize, n); z++)
            {
                int idx = g.Index(x, y, z);
                if (!g.solid[idx]) continue;
                var L = layers[g.layer[idx]];
                // subtle per-voxel brightness jitter so the surface reads as pixels, not a flat slab
                float j = 0.9f + 0.1f * Hash(x, y, z);
                Color c = L.unityColor * j; c.a = 1;
                float glow = L.glow;
                switch (_cube.MarkOf(idx))
                {
                    case CellMark.Loot: c = Loot.Gold * (0.95f + 0.1f * Hash(x, y, z)); glow = 0.9f; break;               // shines gold
                    case CellMark.Glass: c = new Color(0.78f, 0.95f, 1f) * (0.92f + 0.08f * Hash(x, y, z)); glow = 0.45f; break; // frozen
                    case CellMark.Goo: c = new Color(1f, 0.42f, 0.78f) * (0.95f + 0.05f * Hash(x, y, z)); glow = 0.5f; break;   // bouncy
                }
                c.a = 1;
                Color32 c32 = c;
                // cube local space: origin at bottom centre
                var centre = new Vector3((x - n * 0.5f + 0.5f) * s, (y + 0.5f) * s, (z - n * 0.5f + 0.5f) * s);
                for (int f = 0; f < 6; f++)
                {
                    var d = Dirs[f];
                    if (g.IsSolid(x + d.x, y + d.y, z + d.z)) continue;
                    int vi = V.Count;
                    var face = Faces[f];
                    for (int k = 0; k < 4; k++)
                    {
                        V.Add(centre + face[k] * s);
                        Nrm.Add(d);
                        Col.Add(c32);
                        Uv2.Add(new Vector2(glow, 0));
                    }
                    Uv.Add(new Vector2(0, 0)); Uv.Add(new Vector2(0, 1)); Uv.Add(new Vector2(1, 1)); Uv.Add(new Vector2(1, 0));
                    Tri.Add(vi); Tri.Add(vi + 1); Tri.Add(vi + 2);
                    Tri.Add(vi); Tri.Add(vi + 2); Tri.Add(vi + 3);
                }
            }
            _mesh.Clear();
            if (V.Count > 0)
            {
                _mesh.SetVertices(V); _mesh.SetNormals(Nrm); _mesh.SetColors(Col);
                _mesh.SetUVs(0, Uv); _mesh.SetUVs(1, Uv2); _mesh.SetTriangles(Tri, 0);
                _mesh.RecalculateBounds();
                _collider.sharedMesh = null;
                _collider.sharedMesh = _mesh;
                _collider.enabled = true;
            }
            else _collider.enabled = false;
        }

        static float Hash(int x, int y, int z)
        {
            uint h = (uint)(x * 73856093 ^ y * 19349663 ^ z * 83492791);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h & 0xFFFF) / 65535f;
        }
    }
}
