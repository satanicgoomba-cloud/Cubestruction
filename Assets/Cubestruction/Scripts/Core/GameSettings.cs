using UnityEngine;

namespace Cubestruction
{
    public enum GameMode : byte { Mining = 0, GodSiege = 1 }
    public enum MatchState : byte { Lobby = 0, Playing = 1, Ended = 2 }
    public enum Platform : byte { Desktop = 0, Mobile = 1, VR = 2 }
    public enum PlayerRole : byte { Miner = 0, God = 1, Commander = 2 }

    /// <summary>VR movement options (all of them also allow climbing with grip).</summary>
    public enum VRLocomotion : byte { Smooth = 0, Teleport = 1, ArmSwing = 2 }
    public enum VRTurn : byte { Snap = 0, Smooth = 1 }

    /// <summary>Local, per-device preferences (PlayerPrefs-backed).</summary>
    public static class GameSettings
    {
        public static VRLocomotion Locomotion { get => (VRLocomotion)PlayerPrefs.GetInt("loco", 0); set => PlayerPrefs.SetInt("loco", (int)value); }
        public static VRTurn Turn { get => (VRTurn)PlayerPrefs.GetInt("turn", 0); set => PlayerPrefs.SetInt("turn", (int)value); }
        public static float SnapAngle { get => PlayerPrefs.GetFloat("snap", 45f); set => PlayerPrefs.SetFloat("snap", value); }
        public static bool ComfortVignette { get => PlayerPrefs.GetInt("vignette", 1) == 1; set => PlayerPrefs.SetInt("vignette", value ? 1 : 0); }
        /// <summary>Seated / standing doesn't matter for climbing, but lets small/seated players reach.</summary>
        public static float HeightOffset { get => PlayerPrefs.GetFloat("heightOffset", 0f); set => PlayerPrefs.SetFloat("heightOffset", value); }
        public static bool LeftHanded { get => PlayerPrefs.GetInt("lefty", 0) == 1; set => PlayerPrefs.SetInt("lefty", value ? 1 : 0); }
        public static float LookSensitivity { get => PlayerPrefs.GetFloat("sens", 1f); set => PlayerPrefs.SetFloat("sens", value); }
        public static string PlayerName { get => PlayerPrefs.GetString("name", "Buddy" + Random.Range(100, 999)); set => PlayerPrefs.SetString("name", value); }
        public static int ColorIndex { get => PlayerPrefs.GetInt("color", Random.Range(0, AeroPalette.BuddyColors.Length)); set => PlayerPrefs.SetInt("color", value); }
        /// <summary>Allow explosions to ragdoll you (VR players prone to motion sickness can turn this off; they still get knockback).</summary>
        public static bool AllowRagdoll { get => PlayerPrefs.GetInt("ragdoll", 1) == 1; set => PlayerPrefs.SetInt("ragdoll", value ? 1 : 0); }
    }

    /// <summary>The shared Frutiger Aero palette.</summary>
    public static class AeroPalette
    {
        public static readonly Color Sky = new Color(0.55f, 0.85f, 1f);
        public static readonly Color SkyDeep = new Color(0.12f, 0.52f, 0.92f);
        public static readonly Color Aqua = new Color(0.2f, 0.85f, 0.95f);
        public static readonly Color Lime = new Color(0.55f, 0.9f, 0.25f);
        public static readonly Color Grass = new Color(0.3f, 0.78f, 0.3f);
        public static readonly Color GlassWhite = new Color(1f, 1f, 1f, 0.55f);
        public static readonly Color Ink = new Color(0.07f, 0.22f, 0.4f);
        public static readonly Color Gold = new Color(1f, 0.83f, 0.25f);
        public static readonly Color Danger = new Color(1f, 0.35f, 0.3f);

        public static readonly Color[] BuddyColors =
        {
            new Color(0.45f, 0.85f, 0.25f), // lime
            new Color(0.25f, 0.6f, 1f),     // sky blue
            new Color(0.2f, 0.85f, 0.85f),  // aqua
            new Color(1f, 0.6f, 0.2f),      // orange
            new Color(0.95f, 0.45f, 0.75f), // pink
            new Color(0.65f, 0.5f, 1f),     // violet
            new Color(1f, 0.85f, 0.3f),     // sunshine
            new Color(0.95f, 0.95f, 0.98f), // pearl
        };
    }
}
