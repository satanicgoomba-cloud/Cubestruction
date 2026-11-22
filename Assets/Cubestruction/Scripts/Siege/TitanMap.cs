using System;
using UnityEngine;

namespace Cubestruction.Siege
{
    /// <summary>
    /// Titan Siege map, built into the voxel grid (shapeMode 1): three walls across the field, each
    /// stronger than the last, with a little voxel town between them. Deterministic, so every peer
    /// builds the same map from the grid size alone. Cells are 0.5 m; the Titan walks in along +Z.
    ///
    ///   z cells:  12-14 wall 1 (Stone)   18-38 town   42-45 wall 2 (Crystal)   50-70 town   74-78 wall 3 (Magma)
    /// </summary>
    public static class TitanMap
    {
        public const int Size = 96;
        public const float CellSize = 0.5f;

        public struct Wall { public int z0, thick, height, layer; public int Z1 => z0 + thick; }

        public static readonly Wall[] Walls =
        {
            new Wall { z0 = 12, thick = 3, height = 16, layer = 1 },   // Stone: weakest
            new Wall { z0 = 42, thick = 4, height = 18, layer = 2 },   // Crystal
            new Wall { z0 = 74, thick = 5, height = 20, layer = 3 },   // Magma: strongest
        };

        /// <summary>Town zones (z cell ranges) between the walls.</summary>
        static readonly (int z0, int z1)[] Towns = { (18, 38), (50, 70) };

        struct House { public int x0, x1, z0, z1, h; }
        static House[] _houses;

        static House[] Houses()
        {
            if (_houses != null) return _houses;
            var rng = new System.Random(4242);
            var list = new System.Collections.Generic.List<House>();
            foreach (var (tz0, tz1) in Towns)
            {
                // two rows of houses along each side of a main street (x 42..54), plus one in the street
                foreach (var side in new[] { 0, 1 })
                {
                    int x = side == 0 ? 4 : 56;
                    int xEnd = side == 0 ? 40 : 92;
                    while (x < xEnd - 5)
                    {
                        int w = rng.Next(6, 11), d = rng.Next(6, 10), h = rng.Next(6, 15);
                        int z0 = tz0 + rng.Next(0, Math.Max(1, (tz1 - tz0) - d));
                        list.Add(new House { x0 = x, x1 = Math.Min(xEnd, x + w), z0 = z0, z1 = Math.Min(tz1, z0 + d), h = h });
                        x += w + rng.Next(2, 5);
                    }
                }
                int sz = tz0 + 6;
                list.Add(new House { x0 = 45, x1 = 51, z0 = sz, z1 = sz + 6, h = 8 });   // the fountain-house in the street
            }
            return _houses = list.ToArray();
        }

        /// <summary>Layer index for cell (x, y, z), or -1 for empty.</summary>
        public static Func<int, int, int, int> Shape(int n)
        {
            var houses = Houses();
            return (x, y, z) =>
            {
                foreach (var w in Walls)
                {
                    if (z < w.z0 || z >= w.Z1) continue;
                    if (y < w.height) return w.layer;
                    // battlements on the Titan-facing edge
                    if (y < w.height + 2 && z == w.z0 && (x % 6) < 3) return w.layer;
                    return -1;
                }
                foreach (var h in houses)
                {
                    if (x < h.x0 || x >= h.x1 || z < h.z0 || z >= h.z1 || y >= h.h + 2) continue;
                    bool shell = x == h.x0 || x == h.x1 - 1 || z == h.z0 || z == h.z1 - 1;
                    if (y >= h.h)   // pitched roof
                    {
                        int inset = y - h.h + 1;
                        return x >= h.x0 + inset && x < h.x1 - inset ? 0 : -1;
                    }
                    if (y == h.h - 1) return 0;                     // ceiling
                    if (!shell) return -1;                          // hollow inside
                    bool door = z == h.z0 && y < 3 && Math.Abs(x - (h.x0 + h.x1) / 2) <= 0;
                    bool window = y % 4 == 2 && (x + z) % 3 == 0;
                    return door || window ? -1 : 0;
                }
                return -1;
            };
        }

        // ------------------------------------------------------------------ world helpers

        /// <summary>World z of a cell row, for a map whose bottom centre sits at <paramref name="origin"/>.</summary>
        public static float CellZ(Vector3 origin, float cz) => origin.z + (cz - Size * 0.5f + 0.5f) * CellSize;
        public static float CellX(Vector3 origin, float cx) => origin.x + (cx - Size * 0.5f + 0.5f) * CellSize;

        /// <summary>Centre of the top walkway of wall <paramref name="i"/>.</summary>
        public static Vector3 WallTop(Vector3 origin, int i, float x = 0f)
        {
            var w = Walls[i];
            return new Vector3(origin.x + x, origin.y + w.height * CellSize, CellZ(origin, w.z0 + (w.thick - 1) * 0.5f + 0.5f));
        }

        /// <summary>The far (town-side) face of wall <paramref name="i"/> in world z.</summary>
        public static float WallFarZ(Vector3 origin, int i) => CellZ(origin, Walls[i].Z1 - 0.5f);
        public static float WallNearZ(Vector3 origin, int i) => CellZ(origin, Walls[i].z0 - 0.5f);
        public static float HalfWidth => Size * CellSize * 0.5f;

        /// <summary>Where the Titan starts: on the field outside wall 1.</summary>
        public static Vector3 TitanStart(Vector3 origin) => new Vector3(origin.x, origin.y + 0.2f, CellZ(origin, -12));

        public static bool InWall(int i, int z) => z >= Walls[i].z0 && z < Walls[i].Z1;
    }
}
