using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cubestruction.Super
{
    /// <summary>
    /// The SUPER! arena (VoxelCube shapeMode 2): a 64 m octagonal stone stage floating over the meadow,
    /// 0.5 m cells, 128 × 48 × 128. A ring of pillars around the middle, four towers, broken walls and
    /// boulders to smash fighters through. Fall off the edge and it's a ring-out. The bottom floor row is
    /// anchored and unbreakable, so craters never punch all the way through. Deterministic for every peer.
    /// </summary>
    public static class SuperArena
    {
        public const int SizeX = 128, SizeY = 48, SizeZ = 128;
        public const float CellSize = 0.5f;
        public const int FloorThick = 4;
        const int Half = SizeX / 2;
        const int Octagon = 62;          // |x|+|z| cut-off, in cells from the centre, for the corners

        struct Rock { public int x, y, z, r; }
        static Rock[] _rocks;

        static void Plan()
        {
            if (_rocks != null) return;
            var rng = new System.Random(777);
            var list = new List<Rock>();
            while (list.Count < 12)
            {
                int x = rng.Next(-48, 48), z = rng.Next(-48, 48);
                float d = Mathf.Sqrt(x * x + z * z);
                if (d < 28 || d > 50 || Math.Abs(x) + Math.Abs(z) > Octagon + 20) continue;
                if (Math.Abs(Math.Abs(x) - 40) < 9 && Math.Abs(Math.Abs(z) - 40) < 9) continue;   // keep off the towers
                list.Add(new Rock { x = x, z = z, y = FloorThick, r = rng.Next(3, 6) });
            }
            _rocks = list.ToArray();
        }

        static bool OnStage(int dx, int dz) => Math.Abs(dx) < Half && Math.Abs(dz) < Half && Math.Abs(dx) + Math.Abs(dz) < Half + Octagon - 30;

        /// <summary>Layer index for cell (x, y, z), or -1 for empty.</summary>
        public static Func<int, int, int, int> Shape()
        {
            Plan();
            return (x, y, z) =>
            {
                int dx = x - Half, dz = z - Half;
                if (!OnStage(dx, dz)) return -1;
                if (y < FloorThick) return y == 0 ? 4 : ((dx * dx + dz * dz) < 20 * 20 && ((dx + dz) & 7) == 0 ? 2 : 1);   // a tiled centre circle
                int h = y - FloorThick;

                // pillar ring with lintels
                for (int i = 0; i < 8; i++)
                {
                    float a = i * Mathf.PI / 4f + Mathf.PI / 8f;
                    int px = Mathf.RoundToInt(Mathf.Cos(a) * 24), pz = Mathf.RoundToInt(Mathf.Sin(a) * 24);
                    if (Math.Abs(dx - px) <= 1 && Math.Abs(dz - pz) <= 1 && h < 18) return h < 2 || h >= 16 ? 2 : 1;
                }

                // four corner towers: hollow, with windows and a crown
                foreach (int sx in new[] { -1, 1 })
                foreach (int sz in new[] { -1, 1 })
                {
                    int tx = dx - sx * 38, tz = dz - sz * 38;
                    if (Math.Abs(tx) > 5 || Math.Abs(tz) > 5) continue;
                    bool shell = Math.Abs(tx) == 5 || Math.Abs(tz) == 5;
                    if (h >= 28) return h < 30 && shell && ((tx + tz) & 1) == 0 ? 2 : -1;
                    if (h == 27 || h % 9 == 8) return 2;                                            // floors
                    if (!shell) return -1;
                    if (h % 9 == 4 && (Math.Abs(tx) <= 1 || Math.Abs(tz) <= 1)) return -1;          // windows
                    if (h < 3 && (Math.Abs(tx) <= 1 || Math.Abs(tz) <= 1) && (Math.Abs(tx) == 5 ? tx * sx < 0 : tz * sz < 0)) return -1;   // doors face in
                    return 1;
                }

                // broken walls on the four sides
                if (h < 6)
                {
                    int wx = Math.Abs(dx), wz = Math.Abs(dz);
                    bool alongX = wz >= 50 && wz <= 51 && wx < 14, alongZ = wx >= 50 && wx <= 51 && wz < 14;
                    if (alongX || alongZ)
                    {
                        int run = alongX ? wx : wz;
                        int top = 6 - (run * 7 % 5);        // ragged top
                        if (h < top && !(run >= 3 && run <= 5)) return 0;
                    }
                }

                // boulders
                foreach (var r in _rocks)
                {
                    int ry = r.y + r.r / 2;   // sunk into the floor, so no boulder floats
                    int ex = dx - r.x, ey = y - ry, ez = dz - r.z;
                    if (ex * ex + ey * ey * 2 + ez * ez <= r.r * r.r) return 3;
                }
                return -1;
            };
        }

        /// <summary>Bottom row: never collapses and can't be broken (see SuperDirector's protect filter).</summary>
        public static bool Anchored(int x, int y, int z) => y == 0;

        // ------------------------------------------------------------------ world helpers

        public static float TopY(Vector3 origin) => origin.y + FloorThick * CellSize;
        public static float Radius => (Half - 2) * CellSize;

        /// <summary>Spawn point i of n on a ring around the centre, on the floor.</summary>
        public static Vector3 Spawn(Vector3 origin, int i, int n, float ring = 14f)
        {
            float a = i * Mathf.PI * 2f / Mathf.Max(1, n);
            return new Vector3(origin.x + Mathf.Cos(a) * ring, TopY(origin) + 0.15f, origin.z + Mathf.Sin(a) * ring);
        }

        /// <summary>Below this you've fallen off the stage: ring-out.</summary>
        public static float RingOutY(Vector3 origin) => origin.y - 4f;
    }
}
