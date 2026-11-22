namespace Cubestruction.Tools
{
    /// <summary>
    /// What an active killstreak view (Raptor laptop, Heli-Gunner seat) is doing to the local
    /// player's normal controls. Read by FlatRig and GameMenus.
    /// </summary>
    public static class StreakState
    {
        /// <summary>The streak view drives the camera and input; the normal flat rig stands still.</summary>
        public static bool TakesOverInput;
        /// <summary>Free the mouse cursor (Raptor: point and click on the map).</summary>
        public static bool WantsCursor;
        /// <summary>Hide the mobile joystick/buttons (Raptor: taps go to the map instead).</summary>
        public static bool HideTouchControls;
        /// <summary>A super mode owns the Use button (charge/throw/punch): tools don't fire, but you can still move and look.</summary>
        public static bool BlocksTools;

        public static bool Busy => RaptorView.Active != null || HeliView.Active != null || SuperMode.Active != null;

        public static void Clear() { TakesOverInput = WantsCursor = HideTouchControls = BlocksTools = false; }
    }
}
