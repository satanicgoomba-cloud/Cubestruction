using System;
using System.IO;
using UnityEngine;

namespace Cubestruction
{
    /// <summary>
    /// One save slot on this device (Application.persistentDataPath/cubestruction_save.json).
    /// Holds the mode, every voxel that's still standing, the match clock, and the host's own state:
    /// where you were, your fighter and tier (SUPER! / Sandbox) or your money and tools (Mining).
    /// God Siege and Titan Siege can't be saved (too much in flight: waves, turrets, the Titan).
    /// </summary>
    [Serializable]
    public class SaveData
    {
        public int version = 1;
        public string savedAt;
        public int mode;
        public int rules;             // SUPER!: 0 = break the cube, 1/2 = brawl
        public int cubeSize;
        public string solid;          // base64 of VoxelGrid.PackSolid()
        public int alive, total;
        public float elapsed, timeLeft;
        public float x, y, z, yaw;
        public int fighter, tier;
        public float power, hp;
        public int kos, falls;
        public int money, earned, voxels;
        public ulong ownedTools;
        public int[] charges;

        public GameMode Mode => (GameMode)mode;
        public string Summary
        {
            get
            {
                string m = Mode == GameMode.Super ? (rules == 0 ? "SUPER!" : "SUPER! Brawl") : Mode == GameMode.Sandbox ? "Sandbox" : Mode.ToString();
                float pct = total > 0 ? alive * 100f / total : 0f;
                return $"{m} · {savedAt} · {pct:0}% of the map standing";
            }
        }
    }

    public static class SaveGame
    {
        public static string FilePath => Path.Combine(Application.persistentDataPath, "cubestruction_save.json");
        public static bool CanSave(GameMode m) => m == GameMode.Mining || m == GameMode.Super || m == GameMode.Sandbox;

        public static bool Exists => File.Exists(FilePath);

        public static void Write(SaveData d)
        {
            d.savedAt = DateTime.Now.ToString("MMM d, h:mm tt");
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(d));
            if (File.Exists(FilePath)) File.Delete(FilePath);
            File.Move(tmp, FilePath);
        }

        public static SaveData Read()
        {
            try
            {
                if (!Exists) return null;
                var d = JsonUtility.FromJson<SaveData>(File.ReadAllText(FilePath));
                return d != null && !string.IsNullOrEmpty(d.solid) ? d : null;
            }
            catch (Exception e) { Debug.LogWarning("[SaveGame] couldn't read the save: " + e.Message); return null; }
        }

        public static void Delete() { if (Exists) File.Delete(FilePath); }
    }
}
