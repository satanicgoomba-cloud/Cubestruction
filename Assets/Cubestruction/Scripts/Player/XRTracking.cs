using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace Cubestruction.Player
{
    /// <summary>
    /// VR tracking space helpers: asks for floor-level tracking, forwards the headset's recenter
    /// event (holding the Meta/Oculus button), and computes how to turn a rig so the player's
    /// current view becomes a chosen direction.
    /// </summary>
    public static class XRTracking
    {
        /// <summary>Raised when the headset recenters, or when the player presses "Recenter view".</summary>
        public static event Action Recentered;

        static readonly List<XRInputSubsystem> _subsystems = new List<XRInputSubsystem>();
        static bool _hooked;

        /// <summary>Call every frame in VR. Hooks the XR input subsystem once it is running.</summary>
        public static void Tick()
        {
            if (_hooked) return;
            SubsystemManager.GetSubsystems(_subsystems);
            foreach (var s in _subsystems)
            {
                if (!s.running) continue;
                if ((s.GetSupportedTrackingOriginModes() & TrackingOriginModeFlags.Floor) != 0)
                    s.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                s.trackingOriginUpdated += _ => Recentered?.Invoke();
                _hooked = true;
                Debug.Log($"[XRTracking] origin mode {s.GetTrackingOriginMode()}");
            }
        }

        public static void RequestRecenter() => Recentered?.Invoke();

        /// <summary>True once the tracked head has reported a pose.</summary>
        public static bool HasPose(Transform head) => head != null && head.localPosition.sqrMagnitude > 1e-6f;

        /// <summary>
        /// Root pose that puts the tracked <paramref name="head"/> (a child of <paramref name="root"/>)
        /// above <paramref name="feet"/> and looking along <paramref name="yaw"/> degrees.
        /// </summary>
        public static void AlignPose(Transform root, Transform head, Vector3 feet, float yaw, out Vector3 position, out float rootYaw)
        {
            var f = head.localRotation * Vector3.forward;
            float headYaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            rootYaw = yaw - headYaw;
            var local = head.localPosition;
            var offset = Quaternion.Euler(0, rootYaw, 0) * new Vector3(local.x, 0, local.z) * root.localScale.x;
            position = new Vector3(feet.x - offset.x, feet.y, feet.z - offset.z);
        }
    }
}
