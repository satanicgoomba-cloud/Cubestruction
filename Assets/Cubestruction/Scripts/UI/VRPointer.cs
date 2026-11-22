using Cubestruction.Player;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Cubestruction.UI
{
    /// <summary>
    /// Minimal VR laser pointer for world-space canvases (no XR Interaction Toolkit needed).
    /// Every Selectable under the registered canvas gets a thin BoxCollider matching its rect;
    /// we physics-raycast from the right (or left, if left-handed) controller and click on trigger press.
    /// </summary>
    public class VRPointer : MonoBehaviour
    {
        public Canvas canvas;
        /// <summary>Pointing hands. GameMenus sets these (menu-rig hands before a player spawns); falls back to the local player.</summary>
        public Transform leftHand, rightHand;
        public float maxDistance = 6f;
        public int uiLayer = 5;

        LineRenderer _line;
        Transform _dot;
        InputAction _clickR, _clickL;
        Selectable _hover, _pressed;
        static readonly Color LineColor = new Color(0.04f, 0.12f, 0.3f, 1f);   // dark navy: readable on the bright sky
        bool _useLeft;
        float _refreshColliders;

        void OnEnable()
        {
            _clickR = new InputAction(type: InputActionType.Button, binding: "<XRController>{RightHand}/{TriggerButton}"); _clickR.Enable();
            _clickL = new InputAction(type: InputActionType.Button, binding: "<XRController>{LeftHand}/{TriggerButton}"); _clickL.Enable();
            if (!_line)
            {
                _line = gameObject.AddComponent<LineRenderer>();
                _line.widthMultiplier = 0.012f;
                _line.positionCount = 2;
                _line.material = new Material(Shader.Find("Sprites/Default"));
                _line.startColor = LineColor; _line.endColor = new Color(LineColor.r, LineColor.g, LineColor.b, 0.55f);
                _line.numCapVertices = 4;
            }
            if (!_dot)
            {
                var d = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(d.GetComponent<Collider>());
                d.name = "PointerDot";
                d.GetComponent<MeshRenderer>().material = new Material(Shader.Find("Sprites/Default")) { color = LineColor };
                _dot = d.transform;
                _dot.SetParent(transform, false);
                _dot.gameObject.SetActive(false);
            }
        }

        void OnDisable()
        {
            _clickR?.Disable(); _clickL?.Disable();
            if (_line) _line.enabled = false;
            if (_dot) _dot.gameObject.SetActive(false);
            Release(false);
            SetHover(null);
        }

        /// <summary>Adds/resizes BoxColliders for every active Selectable in the canvas. Cheap; run a few times a second.</summary>
        public void RefreshColliders()
        {
            if (!canvas) return;
            foreach (var s in canvas.GetComponentsInChildren<Selectable>(false))
            {
                var rt = (RectTransform)s.transform;
                var bc = s.GetComponent<BoxCollider>();
                if (bc == null) bc = s.gameObject.AddComponent<BoxCollider>();
                bc.isTrigger = true;
                bc.size = new Vector3(rt.rect.width, rt.rect.height, 4f);
                bc.center = new Vector3((0.5f - rt.pivot.x) * rt.rect.width, (0.5f - rt.pivot.y) * rt.rect.height, 0);
                s.gameObject.layer = uiLayer;
            }
        }

        void Update()
        {
            var me = PlayerAvatar.Local;
            var lh = leftHand ? leftHand : me != null ? me.leftHand : null;
            var rh = rightHand ? rightHand : me != null ? me.rightHand : null;
            if (!canvas || !canvas.gameObject.activeInHierarchy || lh == null || rh == null)
            {
                if (_line) _line.enabled = false;
                if (_dot) _dot.gameObject.SetActive(false);
                Release(false);
                SetHover(null);
                return;
            }
            _refreshColliders -= Time.unscaledDeltaTime;
            if (_refreshColliders <= 0) { RefreshColliders(); _refreshColliders = 0.25f; }

            // whichever trigger was pressed last becomes the pointing hand
            if (_clickL.WasPressedThisFrame()) _useLeft = true;
            if (_clickR.WasPressedThisFrame()) _useLeft = false;
            var hand = _useLeft ? lh : rh;
            var ray = new Ray(hand.position, hand.forward);
            float dist = maxDistance * (me != null ? me.transform.localScale.x : 1f);

            Selectable hit = null;
            Vector3 end = ray.origin + ray.direction * dist;
            if (Physics.Raycast(ray, out var h, dist, 1 << uiLayer, QueryTriggerInteraction.Collide))
            {
                hit = h.collider.GetComponent<Selectable>();
                end = h.point;
            }
            _line.enabled = true;
            _line.SetPosition(0, ray.origin); _line.SetPosition(1, end);
            float s = me != null ? me.transform.localScale.x : 1f;
            _line.widthMultiplier = 0.012f * s;
            _dot.gameObject.SetActive(hit != null);
            if (hit != null) { _dot.position = end; _dot.localScale = Vector3.one * 0.025f * s; }
            SetHover(hit);

            var trigger = _useLeft ? _clickL : _clickR;
            if (trigger.WasPressedThisFrame() && hit != null && hit.IsInteractable())
            {
                // show the pressed (dark) state while the trigger is held; click on release
                _pressed = hit;
                ExecuteEvents.Execute(hit.gameObject, Ped(), ExecuteEvents.pointerDownHandler);
            }
            if (trigger.WasReleasedThisFrame()) Release(true);
        }

        static PointerEventData Ped() => new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Left };

        void Release(bool click)
        {
            if (_pressed == null) return;
            var target = _pressed; _pressed = null;
            ExecuteEvents.Execute(target.gameObject, Ped(), ExecuteEvents.pointerUpHandler);
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
            if (!click || target != _hover || !target.IsInteractable()) return;
            if (target is Button b) b.onClick.Invoke();
            else if (target is Toggle t) t.isOn = !t.isOn;
            else if (target is InputField f) { f.ActivateInputField(); TouchScreenKeyboard.Open(f.text); }
            else ExecuteEvents.Execute(target.gameObject, Ped(), ExecuteEvents.pointerClickHandler);
        }

        void SetHover(Selectable s)
        {
            if (s == _hover) return;
            var ped = EventSystem.current != null ? new PointerEventData(EventSystem.current) : null;
            if (_hover && ped != null) ExecuteEvents.Execute(_hover.gameObject, ped, ExecuteEvents.pointerExitHandler);
            _hover = s;
            if (_hover && ped != null) ExecuteEvents.Execute(_hover.gameObject, ped, ExecuteEvents.pointerEnterHandler);
        }
    }
}
