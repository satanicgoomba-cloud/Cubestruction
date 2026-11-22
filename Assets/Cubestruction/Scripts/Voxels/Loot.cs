using Cubestruction.Player;
using UnityEngine;

namespace Cubestruction.Voxels
{
    /// <summary>
    /// Loot hidden in the Mining cube: glowing gold voxels that pay out to whoever breaks them.
    /// Deeper layers roll better prizes; killstreaks and supers only show up from crystal down.
    /// </summary>
    public static class Loot
    {
        public static readonly Color Gold = new Color(1f, 0.82f, 0.2f);

        struct Prize { public string tool; public int count; public int money; public float weight; }
        static Prize Cash(int m, float w) => new Prize { money = m, weight = w };
        static Prize Item(string id, int n, float w) => new Prize { tool = id, count = n, weight = w };

        // per layer: Crust, Stone, Crystal, Magma, Core
        static readonly Prize[][] Table =
        {
            new[] { Cash(150, 4), Item("Grenade", 5, 2), Item("BoingPad", 3, 2) },
            new[] { Cash(500, 3), Item("StickyBomb", 5, 2), Item("Dynamite", 4, 2), Item("BoingPad", 3, 1) },
            new[] { Cash(1500, 3), Item("TNT", 3, 2), Item("RaptorLaptop", 1, 2), Item("MeteorDive", 1, 1.5f) },
            new[] { Cash(4000, 2), Item("C4", 3, 2), Item("RaptorLaptop", 1, 3), Item("HeliGunner", 1, 1.5f),
                    Item("AquaSurge", 1, 1.5f), Item("EchoClones", 1, 1.5f), Item("MeteorDive", 1, 1f) },
            new[] { Item("RaptorLaptop", 1, 2), Item("HeliGunner", 1, 2.5f), Item("NukeCrate", 1, 1f),
                    Item("SunOrb", 1, 2f), Item("TitanBuddy", 1, 2f), Item("AquaSurge", 1, 1.5f), Item("EchoClones", 1, 1.5f) },
        };

        /// <summary>How many loot voxels each layer gets (38 in the 68^3 cube; scales with volume).</summary>
        public static int[] CountsFor(int n)
        {
            float k = Mathf.Pow(n / 68f, 3f);
            int[] baseCounts = { 12, 10, 8, 5, 3 };
            var r = new int[baseCounts.Length];
            for (int i = 0; i < r.Length; i++) r[i] = Mathf.Max(1, Mathf.RoundToInt(baseCounts[i] * k));
            return r;
        }

        /// <summary>Server: pay out a loot voxel from <paramref name="layer"/> to the player who broke it.</summary>
        public static void ServerGrant(ulong instigator, int layer, Vector3 where)
        {
            if (!PlayerAvatar.ByClient.TryGetValue(instigator, out var pl)) return;
            var table = Table[Mathf.Clamp(layer, 0, Table.Length - 1)];
            float total = 0; foreach (var p in table) total += p.weight;
            float roll = Random.value * total;
            var prize = table[table.Length - 1];
            foreach (var p in table) { if (roll < p.weight) { prize = p; break; } roll -= p.weight; }

            string text;
            if (prize.tool == null) { pl.ServerAddMoney(prize.money); text = $"${prize.money:N0}"; }
            else
            {
                var t = Balance.Tool(prize.tool);
                if (t == null) return;
                if (t.consumable) pl.charges[t.index] += prize.count;
                else pl.ownedTools.Value |= 1UL << t.index;
                if (t.id == "C4") pl.ownedTools.Value |= 1UL << Balance.Tool("Detonator").index;
                text = prize.count > 1 ? $"{t.name} x{prize.count}" : t.name;
            }
            pl.LootRpc(text, Balance.Layers.layers[Mathf.Clamp(layer, 0, Balance.Layers.layers.Length - 1)].id);
            Tools.FxHub.Instance?.ShockwaveRpc(where, 1.2f);
        }
    }
}
