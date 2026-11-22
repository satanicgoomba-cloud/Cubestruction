using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cubestruction.Siege
{
    /// <summary>
    /// Titan Siege map, built into a long voxel grid (shapeMode 1, 0.5 m cells, 96 × 32 × 360 cells =
    /// 48 m × 16 m × 180 m). The Titan comes in along +Z. Three thick walls, weakest first, each with a
    /// walkway on top, battlements at the front, and a <b>mine wall</b> along the back edge that defenders
    /// dig for cannonballs, rockets and the odd gun. Between the walls: a dense voxel town (houses, towers,
    /// halls, churches) that the Titan has to smash through. Deterministic, so every peer builds the same map.
    /// </summary>
    public static class TitanMap
    {
        public const int SizeX = 96, SizeY = 32, SizeZ = 360;
        public const float CellSize = 0.5f;
        public const int MineThick = 4, MineHeight = 7;

        public struct Wall
        {
            public int z0, thick, height, layer;
            public int Z1 => z0 + thick;
            /// <summary>Walkway rows on top: behind the battlement row, in front of the mine wall.</summary>
            public int WalkZ0 => z0 + 1;
            public int WalkZ1 => Z1 - MineThick;
        }

        public static readonly Wall[] Walls =
        {
            new Wall { z0 = 20,  thick = 12, height = 16, layer = 1 },   // Stone: weakest
            new Wall { z0 = 170, thick = 16, height = 18, layer = 2 },   // Crystal
            new Wall { z0 = 324, thick = 20, height = 20, layer = 3 },   // Magma: strongest
        };

        /// <summary>Town zones (z cell ranges) between the walls.</summary>
        static readonly (int z0, int z1)[] Towns = { (36, 166), (190, 320) };

        enum Kind { House, Tower, Hall, Church }
        struct Building { public int x0, x1, z0, z1, h; public Kind kind; public int layer; }
        static Building[] _buildings;
        static int[] _at;   // building index per (x, z) column, -1 = street

        static void Plan()
        {
            if (_buildings != null) return;
            var rng = new System.Random(4242);
            var list = new List<Building>();
            foreach (var (tz0, tz1) in Towns)
            {
                // blocks along the street, cross streets between them
                int bi = 0;
                for (int z = tz0; z < tz1 - 8; bi++)
                {
                    int depth = Math.Min(rng.Next(16, 26), tz1 - z);
                    int x = 2;
                    while (x < SizeX - 8)
                    {
                        bool street = x >= 42 && x < 54;
                        if (street)
                        {
                            // the main street: every other block has something in the way
                            if (bi % 2 == 1) list.Add(Make(rng, 44, 52, z + 3, z + depth - 3, rng.Next(3) == 0 ? Kind.Church : Kind.Hall));
                            x = 54; continue;
                        }
                        int w = rng.Next(8, 21);
                        int xEnd = Math.Min(x + w, x < 42 ? 42 : SizeX - 2);
                        if (xEnd - x >= 6)
                        {
                            var k = rng.Next(10) switch { 0 or 1 => Kind.Tower, 2 or 3 => Kind.Hall, 4 => Kind.Church, _ => Kind.House };
                            if (k == Kind.Tower) xEnd = Math.Min(xEnd, x + 8);
                            int dz = rng.Next(0, 3);
                            list.Add(Make(rng, x, xEnd, z + dz, z + depth - rng.Next(0, 3), k));
                        }
                        x = xEnd + rng.Next(2, 4);   // alleys
                    }
                    z += depth + 4;   // cross street
                }
            }
            _buildings = list.ToArray();
            _at = new int[SizeX * SizeZ];
            for (int i = 0; i < _at.Length; i++) _at[i] = -1;
            for (int b = 0; b < _buildings.Length; b++)
            {
                var B = _buildings[b];
                for (int x = Math.Max(0, B.x0); x < Math.Min(SizeX, B.x1); x++)
                for (int z = Math.Max(0, B.z0); z < Math.Min(SizeZ, B.z1); z++) _at[x * SizeZ + z] = b;
            }
        }

        static Building Make(System.Random rng, int x0, int x1, int z0, int z1, Kind k)
        {
            int h = k switch { Kind.Tower => rng.Next(18, 27), Kind.Hall => rng.Next(8, 11), Kind.Church => rng.Next(10, 13), _ => rng.Next(7, 14) };
            int layer = k switch { Kind.Tower => 1, Kind.Hall => rng.Next(2) == 0 ? 2 : 0, Kind.Church => 2, _ => 0 };
            return new Building { x0 = x0, x1 = x1, z0 = z0, z1 = Math.Max(z0 + 5, z1), h = h, kind = k, layer = layer };
        }

        public static int BuildingCount { get { Plan(); return _buildings.Length; } }

        /// <summary>Layer index for cell (x, y, z), or -1 for empty.</summary>
        public static Func<int, int, int, int> Shape()
        {
            Plan();
            return (x, y, z) =>
            {
                for (int i = 0; i < Walls.Length; i++)
                {
                    var w = Walls[i];
                    if (z < w.z0 || z >= w.Z1) continue;
                    if (y < w.height) return w.layer;
                    int above = y - w.height;
                    if (z == w.z0 && above < 2 && (x % 6) < 3) return w.layer;                      // battlements
                    if (z >= w.WalkZ1 && above < MineHeight) return 0;                               // the mine wall
                    return -1;
                }
                if (x < 0 || x >= SizeX || z < 0 || z >= SizeZ) return -1;
                int bi = _at[x * SizeZ + z];
                if (bi < 0) return -1;
                return BuildingCell(_buildings[bi], x, y, z);
            };
        }

        static int BuildingCell(Building b, int x, int y, int z)
        {
            int top = b.h;
            bool edgeX = x == b.x0 || x == b.x1 - 1, edgeZ = z == b.z0 || z == b.z1 - 1, shell = edgeX || edgeZ;
            int cx = (b.x0 + b.x1) / 2, cz = (b.z0 + b.z1) / 2;
            switch (b.kind)
            {
                case Kind.Tower:
                    if (y >= top + 2) return -1;
                    if (y >= top) return shell && ((x + z) % 2 == 0) ? b.layer : -1;                // crenellations
                    if (y == top - 1) return b.layer;
                    if (!shell) return -1;
                    return y % 5 == 3 && (x + z) % 3 == 0 ? -1 : b.layer;                             // slit windows
                case Kind.Church:
                {
                    // a hall with a steeple at the street end
                    bool steeple = Math.Abs(x - cx) <= 1 && z >= b.z0 && z < b.z0 + 3;
                    if (steeple) return y < top + 10 ? (y >= top + 6 && Math.Abs(x - cx) == 1 && z == b.z0 + 1 ? -1 : 2) : -1;
                    break;
                }
            }
            if (b.kind == Kind.House)
            {
                if (y >= top + Math.Min(4, (b.x1 - b.x0) / 2)) return -1;
                if (y >= top)   // pitched roof
                {
                    int inset = y - top + 1;
                    return x >= b.x0 + inset - 1 && x < b.x1 - inset + 1 ? 0 : -1;
                }
            }
            else if (y >= top) return -1;
            if (y == top - 1) return b.layer;                                                        // ceiling / roof slab
            if (!shell) return -1;                                                                    // hollow inside
            bool door = edgeZ && y < 3 && Math.Abs(x - cx) <= 1;
            bool window = y % 4 == 2 && (x + z) % 3 == 0;
            return door || window ? -1 : b.layer;
        }

        /// <summary>Wall voxels are tougher (titan.json wallHpMultiplier, one per wall); the mine wall on top,
        /// the battlements and the town are normal.</summary>
        public static Func<int, int, int, float> HpMultiplier(float[] perWall) => (x, y, z) =>
        {
            if (perWall == null) return 1f;
            for (int i = 0; i < Walls.Length && i < perWall.Length; i++)
                if (z >= Walls[i].z0 && z < Walls[i].Z1 && y < Walls[i].height) return perWall[i];
            return 1f;
        };

        /// <summary>Cells defenders may dig: the mine wall on top of a wall (everything else is the Titan's to break).</summary>
        public static bool IsMine(int x, int y, int z)
        {
            foreach (var w in Walls)
                if (z >= w.WalkZ1 && z < w.Z1 && y >= w.height && y < w.height + MineHeight) return true;
            return false;
        }

        /// <summary>Which wall's mine wall a cell is in (-1 if none).</summary>
        public static int MineWallOf(int z)
        {
            for (int i = 0; i < Walls.Length; i++) if (z >= Walls[i].WalkZ1 && z < Walls[i].Z1) return i;
            return -1;
        }

        // ------------------------------------------------------------------ world helpers

        public static float CellZ(Vector3 origin, float cz) => origin.z + (cz - SizeZ * 0.5f + 0.5f) * CellSize;
        public static float CellX(Vector3 origin, float cx) => origin.x + (cx - SizeX * 0.5f + 0.5f) * CellSize;

        /// <summary>Centre of the walkway on top of wall <paramref name="i"/> (between battlements and mine wall).</summary>
        public static Vector3 WallTop(Vector3 origin, int i, float x = 0f)
        {
            var w = Walls[i];
            float cz = (w.WalkZ0 + w.WalkZ1 - 1) * 0.5f;
            return new Vector3(origin.x + x, origin.y + w.height * CellSize, CellZ(origin, cz));
        }

        /// <summary>Turret spot: on the walkway near the front.</summary>
        public static Vector3 TurretSpot(Vector3 origin, int i, float x) =>
            new Vector3(origin.x + x, origin.y + Walls[i].height * CellSize, CellZ(origin, Walls[i].z0 + 2.5f));

        /// <summary>Just in front of the mine wall (where you stand to dig it).</summary>
        public static Vector3 MineFace(Vector3 origin, int i, float x) =>
            new Vector3(origin.x + x, origin.y + Walls[i].height * CellSize, CellZ(origin, Walls[i].WalkZ1 - 1.2f));

        public static float WallFarZ(Vector3 origin, int i) => CellZ(origin, Walls[i].Z1 - 0.5f);
        public static float WallNearZ(Vector3 origin, int i) => CellZ(origin, Walls[i].z0 - 0.5f);
        public static float HalfWidth => SizeX * CellSize * 0.5f;
        public static float Length => SizeZ * CellSize;

        /// <summary>Where the Titan's little human form stands: on the field at the foot of wall 1.</summary>
        public static Vector3 TitanStart(Vector3 origin) => new Vector3(origin.x, origin.y + 0.2f, CellZ(origin, 6));

        public static bool InWall(int i, int z) => z >= Walls[i].z0 && z < Walls[i].Z1;
    }
}
