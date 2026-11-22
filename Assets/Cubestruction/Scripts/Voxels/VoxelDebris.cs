using System.Collections.Generic;
using UnityEngine;

namespace Cubestruction.Voxels
{
    /// <summary>
    /// The "sand" look: destroyed voxels burst into tiny cubes that fall, bounce off the cube
    /// and pedestal (world collision), pile briefly and shrink away. One pooled ParticleSystem
    /// with mesh particles keeps it cheap enough for Quest (hard cap on live particles).
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    public class VoxelDebris : MonoBehaviour
    {
        public static VoxelDebris Instance { get; private set; }
        public int maxParticles = 2500;
        [Range(1, 4)] public int particlesPerVoxel = 2;
        ParticleSystem _ps;

        void Awake()
        {
            Instance = this;
            _ps = GetComponent<ParticleSystem>();
            var main = _ps.main;
            main.maxParticles = maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = 1.6f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.0f, 3.5f);
            main.playOnAwake = false;
            var emission = _ps.emission; emission.enabled = false;
            var shape = _ps.shape; shape.enabled = false;
            var col = _ps.collision;
            col.enabled = true;
            col.type = ParticleSystemCollisionType.World;
            col.mode = ParticleSystemCollisionMode.Collision3D;
            col.quality = ParticleSystemCollisionQuality.Low;
            col.dampen = 0.45f;
            col.bounce = 0.25f;
            col.lifetimeLoss = 0.05f;
            var sol = _ps.sizeOverLifetime;
            sol.enabled = true;
            sol.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(new Keyframe(0, 1), new Keyframe(0.7f, 0.9f), new Keyframe(1, 0)));
            var rol = _ps.rotationOverLifetime;
            rol.enabled = true;
            rol.z = new ParticleSystem.MinMaxCurve(-3f, 3f);
            var r = GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Mesh;
            if (r.mesh == null) r.mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        public void Spawn(VoxelCube cube, List<int> cells)
        {
            if (_ps == null || cells.Count == 0) return;
            var g = cube.Grid;
            // budget: never emit more than we have room for; sample cells evenly if huge (nukes)
            int room = maxParticles - _ps.particleCount;
            if (room <= 0) return;
            int perVoxel = cells.Count * particlesPerVoxel > room ? 1 : particlesPerVoxel;
            int stride = Mathf.Max(1, Mathf.CeilToInt(cells.Count * perVoxel / (float)room));
            var centre = cube.Center;
            var ep = new ParticleSystem.EmitParams();
            for (int i = 0; i < cells.Count; i += stride)
            {
                int idx = cells[i];
                var pos = cube.CellToWorld(g.Coord(idx));
                var L = g.LayerOf(idx);
                for (int k = 0; k < perVoxel; k++)
                {
                    ep.position = pos + Random.insideUnitSphere * cube.cellSize * 0.4f;
                    // pop outward from the cube a little, then gravity takes over (sand pours down the faces)
                    var outward = (pos - centre).normalized;
                    ep.velocity = outward * Random.Range(0.3f, 1.6f) + Random.insideUnitSphere * 0.6f + Vector3.up * Random.Range(0f, 1.2f);
                    ep.startSize = cube.cellSize * Random.Range(0.25f, 0.5f);
                    ep.startColor = Color.Lerp(L.unityColor, Color.white, Random.Range(0f, 0.25f));
                    ep.rotation3D = Random.insideUnitSphere * 180f;
                    _ps.Emit(ep, 1);
                }
            }
        }
    }
}
