using Cubestruction.Player;
using Cubestruction.UI;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubestruction.Tools
{
    /// <summary>
    /// Heli-Gunner seat, on the gunner's client. You ride the open door of the circling gunship
    /// (your body follows the seat, so everyone sees you up there) and hold fire on the door gun:
    /// mouse/stick/drag to aim on PC/mobile, your dominant hand in VR. Ends when the chopper leaves.
    /// Runs before FlatRig so it reads the mobile look-drag before that is consumed.
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public class HeliView : MonoBehaviour
    {
        public static HeliView Active;

        PlayerAvatar _av;
        ToolController _tc;
        ToolDef _gun;
        bool _vr;
        Camera _cam, _hidden;
        float _yaw, _pitch = 25f, _cooldown, _bannerAt;
        InputAction _vrFire;
        Vector2 _lookAccum;
        bool _fireHeld;

        /// <summary>-autotest hooks: fire continuously at a point.</summary>
        [System.NonSerialized] public bool autoFire;
        [System.NonSerialized] public Vector3? aimAt;

        /// <summary>Called every frame by the owner's ToolController: take the seat when our chopper arrives.</summary>
        public static void TryBegin(PlayerAvatar me)
        {
            var heli = HeliGunship.Instance;
            if (Active != null || heli == null || !heli.IsSpawned || heli.gunner.Value != me.OwnerClientId || heli.Seat == null) return;
            me.gameObject.AddComponent<HeliView>().Begin(me);
        }

        void Begin(PlayerAvatar me)
        {
            _av = me;
            _tc = me.Tools;
            _vr = me.IsVR;
            _gun = Balance.Tool("HeliGatling");
            Active = this;
            StreakState.TakesOverInput = true;
            StreakState.WantsCursor = false;
            StreakState.HideTouchControls = false;   // mobile: look-drag + Use to fire
            _av.Motor.Frozen = true;
            if (_av.Motor.IsGrappling) _av.Motor.StopGrapple();
            if (_vr)
            {
                string hand = GameSettings.LeftHanded ? "LeftHand" : "RightHand";
                _vrFire = new InputAction(type: InputActionType.Button, binding: $"<XRController>{{{hand}}}/{{TriggerButton}}");
                _vrFire.Enable();
            }
            else
            {
                var rig = GetComponent<FlatRig>();
                _hidden = rig ? rig.Cam : null;
                if (_hidden) _hidden.enabled = false;
                _cam = new GameObject("HeliCam").AddComponent<Camera>();
                _cam.fieldOfView = 70f; _cam.nearClipPlane = 0.05f; _cam.farClipPlane = 500f;
                _cam.tag = "MainCamera";
            }
        }

        /// <summary>Read input in Update (before FlatRig consumes the mobile look-drag); act in LateUpdate,
        /// after the chopper's NetworkTransform has moved this frame.</summary>
        void Update()
        {
            if (_vr) { _fireHeld = _vrFire.IsPressed(); return; }
            Vector2 look = Vector2.zero;
            if (Mouse.current != null) look += Mouse.current.delta.ReadValue() * 0.36f;
            if (Gamepad.current != null) look += Gamepad.current.rightStick.ReadValue() * 160f * Time.deltaTime;
            look += MobileInput.LookDelta * 0.15f;
            _lookAccum += look * GameSettings.LookSensitivity;
            _fireHeld = (Mouse.current != null && Mouse.current.leftButton.isPressed) ||
                        (Gamepad.current != null && Gamepad.current.rightTrigger.isPressed) || MobileInput.UseHeld;
        }

        void LateUpdate()
        {
            var heli = HeliGunship.Instance;
            if (heli == null || !heli.IsSpawned || heli.gunner.Value != _av.OwnerClientId) { End(); return; }

            // ride the door seat, facing out of the left door (toward the cube)
            var seat = heli.Seat;
            float baseYaw = heli.transform.eulerAngles.y - 90f;
            transform.SetPositionAndRotation(seat.position, Quaternion.Euler(0, baseYaw, 0));

            Ray aim;
            bool fire;
            if (_vr)
            {
                var hand = GameSettings.LeftHanded ? _av.leftHand : _av.rightHand;
                aim = new Ray(hand.position + hand.forward * 0.3f, hand.forward);
                fire = _fireHeld;
            }
            else
            {
                var look = _lookAccum; _lookAccum = Vector2.zero;
                _yaw = Mathf.Clamp(_yaw + look.x, -110f, 110f);
                _pitch = Mathf.Clamp(_pitch - look.y, -30f, 80f);
                var eye = seat.position + Vector3.up * 0.85f;
                _cam.transform.SetPositionAndRotation(eye, Quaternion.Euler(_pitch, baseYaw + _yaw, 0));
                aim = new Ray(eye + _cam.transform.forward * 1.1f, _cam.transform.forward);
                fire = _fireHeld;
            }
            if (aimAt.HasValue) aim = new Ray(aim.origin, (aimAt.Value - aim.origin).normalized);
            fire |= autoFire;

            _cooldown -= Time.deltaTime;
            if (fire && _gun != null && _cooldown <= 0)
            {
                _cooldown = Mathf.Max(_cooldown, -_gun.cooldown) + _gun.cooldown;
                int seed = Random.Range(1, int.MaxValue);
                bool host = Unity.Netcode.NetworkManager.Singleton != null && Unity.Netcode.NetworkManager.Singleton.IsServer;
                FxHub.Instance?.FireBullets(_gun, aim.origin, aim.direction, seed, _av.OwnerClientId, host, 1f);
                _tc.HeliFireRpc(aim.origin, aim.direction, seed);
            }

            if (Time.time >= _bannerAt)
            {
                _bannerAt = Time.time + 0.5f;
                GameMenus.Instance?.SetBanner($"HELI-GUNNER  ·  {Mathf.CeilToInt(heli.timeLeft.Value)} s  ·  hold fire");
            }
        }

        void End()
        {
            if (Active != this) return;
            Active = null;
            StreakState.Clear();
            GameMenus.Instance?.SetBanner(null);
            if (_av) _av.Motor.Frozen = false;
            if (_cam) Destroy(_cam.gameObject);
            if (_hidden) _hidden.enabled = true;
            _vrFire?.Disable();
            Destroy(this);
        }

        void OnDestroy() { if (Active == this) { Active = null; StreakState.Clear(); } }
    }
}
