using System;
using System.Collections.Generic;
using UnityEngine;

namespace Cubestruction
{
    // ---------------------------------------------------------------------------------------------
    // Balance data. Loaded from Resources/Balance/*.json — the SAME files Tools/balance_sim.py reads,
    // so a tuning change in JSON is reflected in both the game and the simulator.
    // ---------------------------------------------------------------------------------------------

    public enum ToolCategory { Melee, Beam, Gun, Throwable, Placed, Movement, Utility }

    [Serializable]
    public class LayerDef
    {
        public string id;
        public float maxDepthFraction;
        public int tier;
        public float hp;
        public int value;
        public string color;
        public float glow;
        [NonSerialized] public Color unityColor;
    }

    [Serializable]
    public class LayerFile
    {
        public float tierPenalty = 0.35f;
        public float coopHpExponent = 0.5f;
        public float coopValueExponent = 0.7f;
        public LayerDef[] layers;
    }

    [Serializable]
    public class ToolDef
    {
        public string id;
        public string name;
        public string category;
        public int price;
        public bool consumable;
        public int stack;
        public float damage;
        public float radiusCells;
        public float cooldown;
        public int tier;
        public bool twoHanded;
        public float rangeM;
        public float fuse;
        public int pellets;
        public int pierceCells;
        public float knockback;
        public string desc;
        // physics projectiles (guns with speed > 0; 0 keeps the instant hitscan ray)
        public float speed;        // m/s at the muzzle
        public float gravity;      // multiple of normal gravity
        public int bounces;        // ricochets before the bullet is spent (every impact damages)
        public float restitution;  // speed kept per bounce
        public float spreadDeg;    // cone half-angle for pellets / inaccuracy
        public float bulletSize;   // visual radius, m
        public float lifeS;        // seconds before it fizzles
        public bool automatic;     // hold to keep firing even with a cooldown above 0.12 s
        public string bulletShape; // "" = ball, "disc" = flat spinning disc that re-arms its pierce after each ricochet
        public float selfRecoil;   // m/s pushed back along the aim when fired (Boost Blaster: rocket-jump without rockets)
        public int splitCount;     // on first impact the shell bursts into this many bouncing bomblets
        public float splitDamage, splitRadiusCells; public int splitBounces;
        [NonSerialized] public bool popOnEnd;   // bomblets: bounce harmlessly, explode when spent
        [NonSerialized] ToolDef _split;

        /// <summary>The bomblet a splitting shell turns into (derived once from the shell's split* fields).</summary>
        public ToolDef SplitDef
        {
            get
            {
                if (splitCount <= 0) return null;
                if (_split == null)
                {
                    _split = (ToolDef)MemberwiseClone();
                    _split.damage = splitDamage; _split.radiusCells = splitRadiusCells; _split.bounces = splitBounces;
                    _split.splitCount = 0; _split.pellets = 1; _split.pierceCells = 0; _split.gravity = 1f;
                    _split.restitution = 0.55f; _split.bulletSize = 0.06f; _split.lifeS = 2.5f; _split.popOnEnd = true;
                    _split._split = null;
                }
                return _split;
            }
        }

        [NonSerialized] public int index;
        [NonSerialized] public ToolCategory cat;
        public bool IsExplosive => (cat == ToolCategory.Throwable || cat == ToolCategory.Placed || id == "RocketLauncher") && id != "BoingPad";
        public bool IsContinuous => cooldown <= 0.12f;
        public bool IsProjectile => cat == ToolCategory.Gun && speed > 0;
    }

    [Serializable]
    public class ToolFile
    {
        public float ragdollThreshold = 9f;
        public ToolDef[] tools;
    }

    [Serializable] public class CommanderDef
    {
        public int startMoney; public float baseIncome; public float incomePerEra; public float xpPerMoneySpent;
        public float xpPerVoxel; public float[] eraXp; public float autoWaveInterval; public float autoWaveBaseBudget; public float autoWaveGrowth;
    }

    [Serializable] public class GodDef
    {
        public float maxMana; public float manaRegen; public float manaPerKill; public float handSlapDamage;
        public float handSlapCooldown; public float throwDamagePerMps; public float grabMaxSeconds; public float scale;
    }

    [Serializable] public class SkillDef
    {
        public string id; public string name; public float mana; public float cooldown; public float damage;
        public float radiusM; public float duration; public string desc;
        [NonSerialized] public int index;
    }

    [Serializable] public class UnitDef
    {
        public string id; public string name; public int era; public int cost; public float hp; public float speed;
        public float mass; public string attack; public float voxelDamage; public float attackRadiusCells;
        public float attackCooldown; public float rangeM; public int tier; public bool grabbable; public string desc;
        [NonSerialized] public int index;
    }

    [Serializable] public class SiegeFile
    {
        public int cubeSize = 24;
        public float matchSeconds = 900;
        public CommanderDef commander;
        public GodDef god;
        public SkillDef[] skills;
        public UnitDef[] units;
    }

    public static class Balance
    {
        static LayerFile _layers; static ToolFile _tools; static SiegeFile _siege;
        static Dictionary<string, ToolDef> _toolById;

        public static LayerFile Layers { get { Load(); return _layers; } }
        public static ToolFile ToolsFile { get { Load(); return _tools; } }
        public static ToolDef[] Tools { get { Load(); return _tools.tools; } }
        public static SiegeFile Siege { get { Load(); return _siege; } }

        public static ToolDef Tool(string id) { Load(); return _toolById.TryGetValue(id, out var t) ? t : null; }
        public static ToolDef Tool(int index) { Load(); return index >= 0 && index < _tools.tools.Length ? _tools.tools[index] : null; }

        static void Load()
        {
            if (_layers != null) return;
            _layers = Parse<LayerFile>("Balance/layers");
            _tools = Parse<ToolFile>("Balance/tools");
            _siege = Parse<SiegeFile>("Balance/siege");
            foreach (var l in _layers.layers)
                l.unityColor = ColorUtility.TryParseHtmlString(l.color, out var c) ? c : Color.magenta;
            _toolById = new Dictionary<string, ToolDef>();
            for (int i = 0; i < _tools.tools.Length; i++)
            {
                var t = _tools.tools[i];
                t.index = i;
                t.cat = Enum.TryParse<ToolCategory>(t.category, out var c) ? c : ToolCategory.Utility;
                _toolById[t.id] = t;
            }
            for (int i = 0; i < _siege.skills.Length; i++) _siege.skills[i].index = i;
            for (int i = 0; i < _siege.units.Length; i++) _siege.units[i].index = i;
            if (_tools.tools.Length > 64) Debug.LogError("Inventory bitmask supports at most 64 tools.");
        }

        static T Parse<T>(string path)
        {
            var ta = Resources.Load<TextAsset>(path);
            if (ta == null) throw new Exception($"Missing balance file Resources/{path}.json");
            return JsonUtility.FromJson<T>(ta.text);
        }

        /// <summary>Damage multiplier when a tool's tier is below the voxel layer's tier.</summary>
        public static float TierMultiplier(int toolTier, int layerTier)
        {
            return toolTier >= layerTier ? 1f : Mathf.Pow(Layers.tierPenalty, layerTier - toolTier);
        }
    }
}
