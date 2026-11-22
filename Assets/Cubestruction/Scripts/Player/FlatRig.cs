using Cubestruction.Voxels;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubestruction.Player
{
    /// <summary>Written by on-screen touch controls (UI/MobileControls); read by FlatRig.</summary>
    public static class MobileInput
    {
        public static Vector2 Move;
        public static Vector2 LookDelta;
        public static bool Jump, UseHeld, ClimbHeld, Sprint;
        public static void ConsumeFrame() { LookDelta = Vector2.zero; Jump = false; }
    }

    /// <summary>
    /// First-person rig for PC / Mac / Mobile (owner only).
    ///   WASD / left stick / touch pad    move          Mouse / right stick / touch drag  look
    ///   Space / A / Jump button          jump (double jump with Hop Boots)
    ///   Hold E / LB / Climb button       spider-climb any surface you're facing
    ///   LMB / RT / Use button            use tool       RMB / LT   jetpack (if owned)
    ///   1-9 / scroll / D-pad             hotbar         Tab / B / Y  shop      Shift / L3  sprint
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public class FlatRig : MonoBehaviour
    {
        public float eyeHeight = 0.85f;
        public float mouseSensitivity = 0.12f;
        public float stickSensitivity = 160f;
        public Camera Cam { get; private set; }
        public bool CursorFree { get; set; }

        PlayerAvatar _avatar;
        PlayerMotor _motor;
        float _pitch;
        InputAction _move, _look, _jump, _sprint, _use, _climb, _jet, _shop, _scroll, _next, _prev;

        void OnEnable()
        {
            _avatar = GetComponent<PlayerAvatar>();
            _motor = GetComponent<PlayerMotor>();

            _avatar.headAnchor.localPosition = new Vector3(0, eyeHeight, 0);
            _avatar.rightHand.localPosition = new Vector3(0.28f, eyeHeight - 0.25f, 0.45f);
            _avatar.leftHand.localPosition = new Vector3(-0.28f, eyeHeight - 0.25f, 0.45f);
            _avatar.rightHand.SetParent(_avatar.headAnchor, true);  // view-model: hands follow the camera pitch
            _avatar.leftHand.SetParent(_avatar.headAnchor, true);

            var camGo = new GameObject("Flat Camera");
            camGo.transform.SetParent(_avatar.headAnchor, false);
            Cam = camGo.AddComponent<Camera>();
            Cam.nearClipPlane = 0.03f;
            Cam.fieldOfView = 75f;
            Cam.tag = "MainCamera";
            camGo.AddComponent<AudioListener>();

            _move = Composite();
            _look = new InputAction(type: InputActionType.Value);
            _look.AddBinding("<Mouse>/delta"); _look.AddBinding("<Gamepad>/rightStick").WithProcessor("scaleVector2(x=12,y=12)");
            _jump = Btn("<Keyboard>/space", "<Gamepad>/buttonSouth");
            _sprint = Btn("<Keyboard>/leftShift", "<Gamepad>/leftStickPress");
            _use = Btn("<Mouse>/leftButton", "<Gamepad>/rightTrigger");
            _climb = Btn("<Keyboard>/e", "<Gamepad>/leftShoulder");
            _jet = Btn("<Mouse>/rightButton", "<Gamepad>/leftTrigger");
            _shop = Btn("<Keyboard>/tab", "<Gamepad>/buttonNorth");
            _next = Btn("<Gamepad>/dpad/right", "<Gamepad>/rightShoulder");
            _prev = Btn("<Gamepad>/dpad/left", null);
            _scroll = new InputAction(binding: "<Mouse>/scroll/y"); _scroll.Enable();
            foreach (var a in new[] { _move, _look }) a.Enable();

            if (!Application.isMobilePlatform) LockCursor(true);
        }

        InputAction Composite()
        {
            var a = new InputAction(type: InputActionType.Value);
            a.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w").With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            a.AddBinding("<Gamepad>/leftStick");
            return a;
        }

        static InputAction Btn(string a, string b)
        {
            var act = new InputAction(type: InputActionType.Button);
            act.AddBinding(a); if (b != null) act.AddBinding(b);
            act.Enable();
            return act;
        }

        public void LockCursor(bool locked)
        {
            CursorFree = !locked;
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        void OnDisable() { if (Cam) Destroy(Cam.gameObject); }

        void Update()
        {
            bool menus = UI.GameMenus.Instance != null && UI.GameMenus.Instance.BlocksGameplayInput;
            if (_shop.WasPressedThisFrame()) UI.GameMenus.Instance?.ToggleShop();
            if (!Application.isMobilePlatform && menus != CursorFree) LockCursor(!menus);
            if (_motor.Frozen || menus)
            {
                _motor.moveIntent = Vector3.zero; _motor.climbHeld = false;
                _avatar.Tools.SetTriggers(false, false);
                MobileInput.ConsumeFrame();
                return;
            }

            // ---- look
            Vector2 look = _look.ReadValue<Vector2>() * mouseSensitivity + MobileInput.LookDelta * 0.15f;
            look *= GameSettings.LookSensitivity;
            transform.Rotate(0, look.x, 0, Space.World);
            _pitch = Mathf.Clamp(_pitch - look.y, -85f, 85f);
            _avatar.headAnchor.localRotation = Quaternion.Euler(_pitch, 0, 0);

            // ---- move
            Vector2 mv = Vector2.ClampMagnitude(_move.ReadValue<Vector2>() + MobileInput.Move, 1f);
            Vector3 fwd = transform.forward, right = transform.right;
            bool climbKey = _climb.IsPressed() || MobileInput.ClimbHeld;
            if (climbKey && WallAhead(out var normal))
            {
                // spider-climb: forward = up the wall, strafe = along it
                var up = Vector3.ProjectOnPlane(Vector3.up, normal).normalized;
                if (up.sqrMagnitude < 0.01f) up = Vector3.ProjectOnPlane(transform.forward, normal).normalized; // ceilings
                var side = Vector3.Cross(normal, up);
                float sp = 2.2f * _motor.ClimbSpeedMul * _motor.WorldScale;
                _motor.climbHeld = true;
                _motor.climbDelta = (up * mv.y - side * mv.x) * sp * Time.deltaTime - normal * 0.02f; // hug the wall
                _motor.moveIntent = Vector3.zero;
            }
            else
            {
                _motor.climbHeld = false;
                _motor.moveIntent = fwd * mv.y + right * mv.x;
            }
            _motor.sprint = _sprint.IsPressed() || MobileInput.Sprint;
            _motor.jetHeld = _jet.IsPressed() || (MobileInput.Jump && !_motor.Grounded);
            if (_jump.WasPressedThisFrame() || MobileInput.Jump) _motor.Jump();

            // ---- tools
            _avatar.Tools.SetTriggers(false, _use.IsPressed() || MobileInput.UseHeld);
            float sc = _scroll.ReadValue<float>();
            if (sc > 0.1f || _next.WasPressedThisFrame()) _avatar.Tools.CycleHand(false, +1);
            if (sc < -0.1f || _prev.WasPressedThisFrame()) _avatar.Tools.CycleHand(false, -1);
            var kb = Keyboard.current;
            if (kb != null)
                for (int i = 0; i < 9; i++)
                    if (kb[Key.Digit1 + i].wasPressedThisFrame) _avatar.Tools.SelectHotbarSlot(i);

            MobileInput.ConsumeFrame();
        }

        bool WallAhead(out Vector3 normal)
        {
            normal = Vector3.zero;
            float s = _motor.WorldScale;
            var origin = transform.position + Vector3.up * 0.5f * s;
            var dir = Vector3.ProjectOnPlane(_avatar.headAnchor.forward, Vector3.up).normalized;
            if (dir.sqrMagnitude < 0.01f) dir = transform.forward;
            if (Physics.SphereCast(origin, 0.2f * s, dir, out var hit, 0.6f * s, ~0, QueryTriggerInteraction.Ignore) ||
                Physics.SphereCast(origin, 0.2f * s, _avatar.headAnchor.forward, out hit, 0.7f * s, ~0, QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.GetComponentInParent<PlayerAvatar>() != null) return false;
                normal = hit.normal;
                return true;
            }
            return false;
        }

        /// <summary>Ray from the centre of the screen.</summary>
        public Ray AimRay => new Ray(Cam.transform.position, Cam.transform.forward);
    }
}
