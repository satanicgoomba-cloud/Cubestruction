using Cubestruction.Player;
using Cubestruction.UI;
using Cubestruction.Voxels;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Cubestruction.Tools
{
    /// <summary>
    /// Raptor Laptop killstreak, on the owner's client. PC/mobile: the view jumps to a drone camera
    /// circling high over the cube; click/tap anywhere to call a missile there (A/D or the arrows
    /// turn the camera, Esc or "Done" closes the laptop). VR keeps your own view (no camera jumps)
    /// and gives your dominant hand a targeting laser: pull the trigger to call a strike.
    /// The server checks each strike and drops the missile from the sky.
    /// </summary>
    public class RaptorView : MonoBehaviour
    {
        public static RaptorView Active;

        ToolController _tc;
        PlayerAvatar _av;
        int _left;
        float _until, _cooldown, _orbit;
        bool _vr, _ending;
        Camera _cam, _hidden;
        Transform _marker;
        LineRenderer _laser;
        InputAction _vrTrigger;

        public int MissilesLeft => _left;

        public void Begin(ToolController tc, int missiles, float seconds)
        {
            _tc = tc;
            _av = GetComponent<PlayerAvatar>();
            _vr = _av.IsVR;
            _left = missiles;
            _until = Time.time + seconds;
            Active = this;

            StreakState.TakesOverInput = !_vr;
            StreakState.WantsCursor = true;
            StreakState.HideTouchControls = true;

            var glow = FxHub.Instance ? FxHub.Instance.glowMaterial : null;
            var m = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            Destroy(m.GetComponent<Collider>());
            m.name = "RaptorMarker";
            m.GetComponent<MeshRenderer>().material = new Material(glow) { color = new Color(1f, 0.25f, 0.2f, 0.75f) };
            _marker = m.transform;
            _marker.localScale = new Vector3(2.2f, 0.02f, 2.2f);

            if (_vr)
            {
                string hand = GameSettings.LeftHanded ? "LeftHand" : "RightHand";
                _vrTrigger = new InputAction(type: InputActionType.Button, binding: $"<XRController>{{{hand}}}/{{TriggerButton}}");
                _vrTrigger.Enable();
                _laser = gameObject.AddComponent<LineRenderer>();
                _laser.material = glow; _laser.positionCount = 2; _laser.widthMultiplier = 0.01f;
                _laser.startColor = _laser.endColor = new Color(1f, 0.2f, 0.2f, 0.9f);
            }
            else
            {
                var rig = GetComponent<FlatRig>();
                _hidden = rig ? rig.Cam : null;
                if (_hidden) _hidden.enabled = false;
                _cam = new GameObject("RaptorCam").AddComponent<Camera>();
                _cam.fieldOfView = 55f; _cam.nearClipPlane = 0.3f; _cam.farClipPlane = 500f;
                _cam.tag = "MainCamera";
                _orbit = transform.eulerAngles.y;
            }
            Banner();
        }

        void Banner()
        {
            string how = _vr ? "point and pull the trigger" : Application.isMobilePlatform ? "tap to strike" : "click to strike · A/D to turn · Esc to close";
            GameMenus.Instance?.SetBanner($"RAPTOR ONLINE  ·  {_left} missile{(_left == 1 ? "" : "s")}  ·  {how}", _vr ? null : "Done", End);
        }

        /// <summary>Call a strike at a world point (also used by -autotest).</summary>
        public void Strike(Vector3 point)
        {
            if (_left <= 0 || _cooldown > 0 || _ending) return;
            _left--;
            _cooldown = 0.35f;
            _tc.RaptorStrikeRpc(point);
            Banner();
            if (_left == 0) { _ending = true; Invoke(nameof(End), 1.5f); }
        }

        void Update()
        {
            if (Time.time > _until) { End(); return; }
            _cooldown -= Time.deltaTime;
            var cube = VoxelCube.Instance;
            if (!cube || _av == null || !_av.IsSpawned) { End(); return; }

            Ray ray; bool click;
            if (_vr)
            {
                var hand = GameSettings.LeftHanded ? _av.leftHand : _av.rightHand;
                ray = new Ray(hand.position, hand.forward);
                click = _vrTrigger.WasPressedThisFrame();
            }
            else
            {
                var kb = Keyboard.current;
                float turn = 0;
                if (kb != null)
                {
                    if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) turn -= 1;
                    if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) turn += 1;
                    if (kb.escapeKey.wasPressedThisFrame) { End(); return; }
                }
                _orbit += (turn * 70f + 4f) * Time.deltaTime;
                float h = cube.HalfExtent;
                var c = cube.Center;
                _cam.transform.position = c + Quaternion.Euler(0, _orbit, 0) * new Vector3(0, h * 2f + 22f, -(h + 18f));
                _cam.transform.LookAt(c);

                Vector2 pos; int pointerId = -1;
                var touch = Touchscreen.current;
                if (touch != null && touch.primaryTouch.press.isPressed)
                {
                    pos = touch.primaryTouch.position.ReadValue();
                    click = touch.primaryTouch.press.wasPressedThisFrame;
                    pointerId = touch.primaryTouch.touchId.ReadValue();
                }
                else if (Mouse.current != null)
                {
                    pos = Mouse.current.position.ReadValue();
                    click = Mouse.current.leftButton.wasPressedThisFrame;
                }
                else return;
                // the banner's Done button is UI: don't strike through it
                if (click && EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(pointerId)) click = false;
                ray = _cam.ScreenPointToRay(pos);
            }

            bool hit = Aim(ray, cube, out var p);
            _marker.gameObject.SetActive(hit);
            if (hit) _marker.position = p + Vector3.up * 0.05f;
            if (_laser)
            {
                _laser.SetPosition(0, ray.origin);
                _laser.SetPosition(1, hit ? p : ray.origin + ray.direction * 60f);
            }
            if (click && hit) Strike(p);
        }

        static bool Aim(Ray ray, VoxelCube cube, out Vector3 point)
        {
            point = default;
            float best = float.MaxValue;
            if (cube.Raycast(ray, 400f, out _, out var vp)) { point = vp; best = Vector3.Distance(ray.origin, vp); }
            if (Physics.Raycast(ray, out var h, 400f, ~(1 << 5), QueryTriggerInteraction.Ignore) && h.distance < best &&
                h.collider.GetComponentInParent<VoxelChunk>() == null && h.collider.GetComponentInParent<PlayerAvatar>() == null)
            { point = h.point; best = h.distance; }
            return best < float.MaxValue;
        }

        public void End()
        {
            if (Active != this) return;
            Active = null;
            StreakState.Clear();
            GameMenus.Instance?.SetBanner(null);
            if (_cam) Destroy(_cam.gameObject);
            if (_hidden) _hidden.enabled = true;
            if (_marker) Destroy(_marker.gameObject);
            if (_laser) Destroy(_laser);
            _vrTrigger?.Disable();
            Destroy(this);
        }

        void OnDestroy() { if (Active == this) { Active = null; StreakState.Clear(); } }
    }
}
