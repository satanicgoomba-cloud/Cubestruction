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
        public float maxDistance = 6f;
        public int uiLayer = 5;

        LineRenderer _line;
        InputAction _clickR, _clickL;
        Selectable _hover;
        bool _useLeft;
        float _refreshColliders;

        void OnEnable()
        {
            _clickR = new InputAction(type: InputActionType.Button, binding: "<XRController>{RightHand}/{TriggerButton}"); _clickR.Enable();
            _clickL = new InputAction(type: InputActionType.Button, binding: "<XRController>{LeftHand}/{TriggerButton}"); _clickL.Enable();
            if (!_line)
            {
                _line = gameObject.AddComponent<LineRenderer>();
                _line.widthMultiplier = 0.006f;
                _line.positionCount = 2;
                _line.material = new Material(Shader.Find("Sprites/Default"));
                _line.startColor = new Color(0.6f, 0.95f, 1f, 0.9f); _line.endColor = new Color(0.6f, 0.95f, 1f, 0.1f);
            }
        }

        void OnDisable()
        {
            _clickR?.Disable(); _clickL?.Disable();
            if (_line) _line.enabled = false;
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
            if (!canvas || !canvas.gameObject.activeInHierarchy || me == null)
            {
                if (_line) _line.enabled = false;
                SetHover(null);
                return;
            }
            _refreshColliders -= Time.unscaledDeltaTime;
            if (_refreshColliders <= 0) { RefreshColliders(); _refreshColliders = 0.25f; }

            // whichever trigger was pressed last becomes the pointing hand
            if (_clickL.WasPressedThisFrame()) _useLeft = true;
            if (_clickR.WasPressedThisFrame()) _useLeft = false;
            var hand = _useLeft ? me.leftHand : me.rightHand;
            var ray = new Ray(hand.position, hand.forward);
            float dist = maxDistance * me.transform.localScale.x;

            Selectable hit = null;
            Vector3 end = ray.origin + ray.direction * dist;
            if (Physics.Raycast(ray, out var h, dist, 1 << uiLayer, QueryTriggerInteraction.Collide))
            {
                hit = h.collider.GetComponent<Selectable>();
                end = h.point;
            }
            _line.enabled = true;
            _line.SetPosition(0, ray.origin); _line.SetPosition(1, end);
            SetHover(hit);

            bool click = _useLeft ? _clickL.WasPressedThisFrame() : _clickR.WasPressedThisFrame();
            if (click && hit != null && hit.IsInteractable())
            {
                var ped = new PointerEventData(EventSystem.current);
                if (hit is Button b) b.onClick.Invoke();
                else if (hit is Toggle t) t.isOn = !t.isOn;
                else if (hit is InputField f) { f.ActivateInputField(); TouchScreenKeyboard.Open(f.text); }
                else ExecuteEvents.Execute(hit.gameObject, ped, ExecuteEvents.pointerClickHandler);
            }
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
