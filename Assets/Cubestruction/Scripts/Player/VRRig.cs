using Cubestruction.Voxels;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;

namespace Cubestruction.Player
{
    /// <summary>
    /// Local VR rig (owner only, enabled by PlayerAvatar when an XR device is active).
    /// Controls (Quest Touch via OpenXR):
    ///   Grip            climb: grab ANY voxel (or prop) and pull yourself; release to fling
    ///   Trigger         use the tool in that hand
    ///   A/X             cycle that hand's tool       B/Y   open shop / menu
    ///   Left stick      move (Smooth) / arc teleport (Teleport) ; Right stick turn, click = jump
    ///   ArmSwing mode   hold both grips in the air and swing your arms to run
    /// </summary>
    [DefaultExecutionOrder(-50)]   // feed the motor before it simulates this frame
    public class VRRig : MonoBehaviour
    {
        public float climbGrabRadius = 0.12f;
        public float maxFling = 7f;
        public Material vignetteMaterial;

        public Camera Cam { get; private set; }
        [System.NonSerialized] public HandState Left = new HandState(), Right = new HandState();   // runtime state only

        PlayerAvatar _avatar;
        PlayerMotor _motor;
        InputAction _move, _turn, _jump, _menuL, _menuR;
        float _snapCooldown;
        bool _teleportAiming;
        Vector3 _teleportPoint; bool _teleportValid;
        LineRenderer _arc;
        Transform _vignette;
        float _vignetteAmount;

        public class HandState
        {
            public Transform t;
            public InputAction grip, trigger, primary, secondary, triggerValue;
            public bool climbing;
            public Vector3 anchorWorld;
            public Vector3 lastPos, velocity;
            public bool IsLeft;
        }

        void OnEnable()
        {
            _avatar = GetComponent<PlayerAvatar>();
            _motor = GetComponent<PlayerMotor>();

            // camera on the head anchor
            var camGo = new GameObject("XR Camera");
            camGo.transform.SetParent(_avatar.headAnchor, false);
            Cam = camGo.AddComponent<Camera>();
            Cam.nearClipPlane = 0.02f;
            Cam.tag = "MainCamera";
            camGo.AddComponent<AudioListener>();

            AddPose(_avatar.headAnchor, "<XRHMD>/centerEyePosition", "<XRHMD>/centerEyeRotation");
            AddPose(_avatar.leftHand, "<XRController>{LeftHand}/devicePosition", "<XRController>{LeftHand}/deviceRotation");
            AddPose(_avatar.rightHand, "<XRController>{RightHand}/devicePosition", "<XRController>{RightHand}/deviceRotation");

            Left = MakeHand(_avatar.leftHand, "LeftHand", true);
            Right = MakeHand(_avatar.rightHand, "RightHand", false);

            string moveHand = GameSettings.LeftHanded ? "RightHand" : "LeftHand";
            string turnHand = GameSettings.LeftHanded ? "LeftHand" : "RightHand";
            _move = Bind($"<XRController>{{{moveHand}}}/{{Primary2DAxis}}", InputActionType.Value);
            _turn = Bind($"<XRController>{{{turnHand}}}/{{Primary2DAxis}}", InputActionType.Value);
            _jump = Bind($"<XRController>{{{turnHand}}}/{{Primary2DAxisClick}}", InputActionType.Button);

            _arc = gameObject.AddComponent<LineRenderer>();
            _arc.widthMultiplier = 0.02f; _arc.positionCount = 0;
            _arc.material = vignetteMaterial ? new Material(vignetteMaterial) : null;
            BuildVignette();

            // Tell everyone how tall we are so our Buddy body is scaled to match our real height.
            Invoke(nameof(CalibrateHeight), 2f);

            XRTracking.Recentered += OnRecentered;
            _recenterPending = true;   // face the spawn direction whichever way the player is looking
        }

        bool _recenterPending;
        Vector3? _alignFeet; float _alignYaw;

        void OnRecentered() => _recenterPending = true;

        /// <summary>Make the player's current view the body's forward, keeping them where they stand.</summary>
        public void Recenter() => _recenterPending = true;

        /// <summary>Place the player's head over <paramref name="feet"/>, looking along <paramref name="yaw"/>
        /// (used for spawns and teleports, so VR players face the way the game intends).</summary>
        public void AlignTo(Vector3 feet, float yaw)
        {
            _alignFeet = feet; _alignYaw = yaw;
            _motor.Teleport(feet, yaw);   // immediate; refined once the head pose is known
        }

        void ApplyPendingAlignment()
        {
            if (!XRTracking.HasPose(_avatar.headAnchor)) return;
            Vector3 feet; float yaw;
            if (_alignFeet.HasValue) { feet = _alignFeet.Value; yaw = _alignYaw; }
            else if (_recenterPending)
            {
                var h = _avatar.headAnchor.position;
                feet = new Vector3(h.x, transform.position.y, h.z);
                yaw = transform.eulerAngles.y;
            }
            else return;
            XRTracking.AlignPose(transform, _avatar.headAnchor, feet, yaw, out var pos, out var rootYaw);
            _motor.Teleport(pos, rootYaw);
            _alignFeet = null; _recenterPending = false;
        }

        void CalibrateHeight()
        {
            float h = _avatar.headAnchor.localPosition.y + GameSettings.HeightOffset;
            if (h > 0.5f) _avatar.avatarScale.Value = Mathf.Clamp(h / 0.9f, 0.6f, 2.2f);
        }

        static void AddPose(Transform target, string pos, string rot)
        {
            var tpd = target.gameObject.AddComponent<TrackedPoseDriver>();
            tpd.positionInput = new InputActionProperty(new InputAction(binding: pos));
            tpd.rotationInput = new InputActionProperty(new InputAction(binding: rot));
            tpd.positionInput.action.Enable(); tpd.rotationInput.action.Enable();
        }

        static InputAction Bind(string path, InputActionType type)
        {
            var a = new InputAction(type: type, binding: path);
            a.Enable();
            return a;
        }

        HandState MakeHand(Transform t, string hand, bool isLeft) => new HandState
        {
            t = t, IsLeft = isLeft,
            grip = Bind($"<XRController>{{{hand}}}/{{GripButton}}", InputActionType.Button),
            trigger = Bind($"<XRController>{{{hand}}}/{{TriggerButton}}", InputActionType.Button),
            triggerValue = Bind($"<XRController>{{{hand}}}/{{Trigger}}", InputActionType.Value),
            primary = Bind($"<XRController>{{{hand}}}/{{PrimaryButton}}", InputActionType.Button),
            secondary = Bind($"<XRController>{{{hand}}}/{{SecondaryButton}}", InputActionType.Button),
            lastPos = t.position,
        };

        void OnDisable()
        {
            XRTracking.Recentered -= OnRecentered;
            if (Cam) Destroy(Cam.gameObject);
        }

        void Update()
        {
            XRTracking.Tick();
            ApplyPendingAlignment();
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            foreach (var h in new[] { Left, Right })
            {
                h.velocity = Vector3.Lerp(h.velocity, (h.t.position - h.lastPos) / dt, 0.5f);
                h.lastPos = h.t.position;
            }
            // keep the character capsule under the head
            var headLocal = _avatar.headAnchor.localPosition;
            _motor.Controller.center = new Vector3(headLocal.x, _motor.Controller.height * 0.5f, headLocal.z);

            if (_motor.Frozen) { UpdateVignette(Tools.HeliView.Active ? 0.3f : 1f); return; }   // ragdoll: dark; heli seat: light

            // tool use is ToolController's job; the rig only routes A/X (cycle) and B/Y (menu)
            if (Left.primary.WasPressedThisFrame()) _avatar.Tools.CycleHand(true);
            if (Right.primary.WasPressedThisFrame()) _avatar.Tools.CycleHand(false);
            if (Left.secondary.WasPressedThisFrame() || Right.secondary.WasPressedThisFrame()) UI.GameMenus.Instance?.ToggleShop();
            _avatar.Tools.SetTriggers(Left.trigger.IsPressed(), Right.trigger.IsPressed());

            // swing (Zip Tether): the off-hand's trigger shoots a web-line when that hand is empty;
            // jump (right stick click) reels in, release to fling
            var swingHand = GameSettings.LeftHanded ? Right : Left;
            bool handFree = _avatar.Tools.Equipped(swingHand.IsLeft) == null;
            _avatar.Tools.swingHeld = handFree && swingHand.trigger.IsPressed();
            _avatar.Tools.swingAim = new Ray(swingHand.t.position, swingHand.t.forward);
            _avatar.Tools.swingHand = swingHand.t;
            _motor.reelIn = _motor.IsGrappling && _jump.IsPressed();

            Climb();
            Locomote(dt);
            Turn(dt);
            if (_jump.WasPressedThisFrame()) _motor.Jump();

            float speed = new Vector3(_motor.Velocity.x, 0, _motor.Velocity.z).magnitude;
            UpdateVignette(GameSettings.ComfortVignette ? Mathf.InverseLerp(1.5f, 6f, speed) * 0.8f : 0f);
        }

        // ------------------------------------------------------------------ climbing (grab anywhere)

        void Climb()
        {
            var cube = VoxelCube.Instance;
            float scale = _motor.WorldScale;
            int holding = 0;
            Vector3 delta = Vector3.zero;
            foreach (var h in new[] { Left, Right })
            {
                bool grip = h.grip.IsPressed();
                if (grip && !h.climbing)
                {
                    bool near = (cube && cube.SolidNear(h.t.position, climbGrabRadius * scale, out _)) ||
                                Physics.CheckSphere(h.t.position, climbGrabRadius * scale, ~0, QueryTriggerInteraction.Ignore);
                    if (near && h.grip.WasPressedThisFrame()) { h.climbing = true; h.anchorWorld = h.t.position; Haptic(h, 0.3f); }
                }
                else if (!grip && h.climbing) h.climbing = false;

                // the voxel under a hand was destroyed -> you slip!
                if (h.climbing && cube && !cube.SolidNear(h.anchorWorld, climbGrabRadius * scale * 1.5f, out _) &&
                    !Physics.CheckSphere(h.anchorWorld, climbGrabRadius * scale, ~0, QueryTriggerInteraction.Ignore))
                {
                    h.climbing = false; Haptic(h, 0.8f);
                }

                if (h.climbing)
                {
                    holding++;
                    delta += (h.anchorWorld - h.t.position) * _motor.ClimbSpeedMul;
                }
            }
            bool wasClimbing = _motor.climbHeld;
            _motor.climbHeld = holding > 0;
            if (holding > 0)
            {
                delta /= holding;
                _motor.climbDelta = delta;
                // anchor follows: after we move the rig the hand is back at the anchor
            }
            else if (wasClimbing)
            {
                // fling: opposite of hand motion on release
                var v = -(Left.velocity + Right.velocity) * 0.5f;
                if (Left.velocity.sqrMagnitude > Right.velocity.sqrMagnitude * 4) v = -Left.velocity;
                else if (Right.velocity.sqrMagnitude > Left.velocity.sqrMagnitude * 4) v = -Right.velocity;
                _motor.Velocity = Vector3.ClampMagnitude(v * _motor.ClimbSpeedMul, maxFling * scale);
            }
            if (_motor.Stamina <= 0 && holding > 0) { Left.climbing = Right.climbing = false; Haptic(Left, 1); Haptic(Right, 1); }
        }

        static void Haptic(HandState h, float amp)
        {
            var dev = h.IsLeft ? UnityEngine.InputSystem.XR.XRController.leftHand : UnityEngine.InputSystem.XR.XRController.rightHand;
            var cmd = UnityEngine.InputSystem.XR.Haptics.SendHapticImpulseCommand.Create(0, amp, 0.08f);
            dev?.ExecuteCommand(ref cmd);
        }

        // ------------------------------------------------------------------ locomotion

        void Locomote(float dt)
        {
            Vector2 stick = _move.ReadValue<Vector2>();
            var head = _avatar.headAnchor;
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            Vector3 right = Vector3.Cross(Vector3.up, fwd);
            _motor.moveIntent = Vector3.zero;

            switch (GameSettings.Locomotion)
            {
                case VRLocomotion.Smooth:
                    _motor.moveIntent = Vector3.ClampMagnitude(fwd * stick.y + right * stick.x, 1f);
                    _motor.sprint = stick.magnitude > 0.95f;
                    break;

                case VRLocomotion.Teleport:
                    if (stick.y > 0.6f) { _teleportAiming = true; DrawArc(); }
                    else if (_teleportAiming)
                    {
                        _teleportAiming = false; _arc.positionCount = 0;
                        if (_teleportValid) _motor.Teleport(_teleportPoint - (head.position - transform.position).WithY(0), transform.eulerAngles.y);
                    }
                    break;

                case VRLocomotion.ArmSwing:
                    if (Left.grip.IsPressed() && Right.grip.IsPressed() && !Left.climbing && !Right.climbing)
                    {
                        float swing = (Mathf.Abs(Left.velocity.y) + Mathf.Abs(Right.velocity.y)) * 0.5f;
                        var dir = Vector3.ProjectOnPlane(Left.t.forward + Right.t.forward, Vector3.up).normalized;
                        _motor.moveIntent = dir * Mathf.Clamp01(swing / 2f);
                        _motor.sprint = swing > 2.5f;
                    }
                    break;
            }
        }

        void DrawArc()
        {
            var hand = GameSettings.LeftHanded ? Right.t : Left.t;
            Vector3 p = hand.position, v = hand.forward * 9f * _motor.WorldScale;
            _arc.positionCount = 0; _teleportValid = false;
            var pts = new System.Collections.Generic.List<Vector3> { p };
            for (int i = 0; i < 40; i++)
            {
                var next = p + v * 0.05f; v += Physics.gravity * 0.05f;
                if (Physics.Linecast(p, next, out var hit, ~0, QueryTriggerInteraction.Ignore))
                {
                    pts.Add(hit.point);
                    _teleportValid = Vector3.Dot(hit.normal, Vector3.up) > 0.7f;
                    _teleportPoint = hit.point;
                    break;
                }
                pts.Add(next); p = next;
            }
            _arc.positionCount = pts.Count; _arc.SetPositions(pts.ToArray());
            _arc.startColor = _arc.endColor = _teleportValid ? AeroPalette.Aqua : AeroPalette.Danger;
        }

        void Turn(float dt)
        {
            float x = _turn.ReadValue<Vector2>().x;
            if (GameSettings.Turn == VRTurn.Smooth)
            {
                if (Mathf.Abs(x) > 0.15f) RotateAroundHead(x * 120f * dt);
            }
            else
            {
                _snapCooldown -= dt;
                if (Mathf.Abs(x) > 0.7f && _snapCooldown <= 0) { RotateAroundHead(Mathf.Sign(x) * GameSettings.SnapAngle); _snapCooldown = 0.3f; }
                if (Mathf.Abs(x) < 0.3f) _snapCooldown = 0;
            }
        }

        void RotateAroundHead(float degrees)
        {
            var pivot = _avatar.headAnchor.position;
            _motor.Controller.enabled = false;
            transform.RotateAround(pivot, Vector3.up, degrees);
            _motor.Controller.enabled = true;
        }

        // ------------------------------------------------------------------ comfort vignette

        void BuildVignette()
        {
            if (!vignetteMaterial) return;
            var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
            Destroy(q.GetComponent<Collider>());
            q.name = "Vignette";
            q.transform.SetParent(Cam.transform, false);
            q.transform.localPosition = new Vector3(0, 0, 0.05f);
            q.transform.localScale = Vector3.one * 0.6f;   // always covers the full view at this distance
            var mat = new Material(vignetteMaterial) { mainTexture = MakeVignetteTexture() };
            q.GetComponent<MeshRenderer>().sharedMaterial = mat;
            _vignette = q.transform;
        }

        static Texture2D MakeVignetteTexture()
        {
            const int S = 128;
            var tex = new Texture2D(S, S, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float d = new Vector2(x - S / 2f, y - S / 2f).magnitude / (S / 2f);
                float a = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(0.45f, 0.95f, d));
                tex.SetPixel(x, y, new Color(0.05f, 0.2f, 0.35f, a));
            }
            tex.Apply();
            return tex;
        }

        void UpdateVignette(float target)
        {
            if (!_vignette) return;
            _vignetteAmount = Mathf.MoveTowards(_vignetteAmount, target, Time.deltaTime * 3f);
            _vignette.gameObject.SetActive(_vignetteAmount > 0.01f);
            var mr = _vignette.GetComponent<MeshRenderer>();
            mr.sharedMaterial.color = new Color(1, 1, 1, _vignetteAmount);
        }
    }

    static class VecExt
    {
        public static Vector3 WithY(this Vector3 v, float y) => new Vector3(v.x, y, v.z);
    }
}
