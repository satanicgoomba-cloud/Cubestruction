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

        public static bool Busy => RaptorView.Active != null || HeliView.Active != null;

        public static void Clear() { TakesOverInput = WantsCursor = HideTouchControls = false; }
    }
}
