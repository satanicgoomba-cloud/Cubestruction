using Cubestruction.Player;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Cubestruction.UI
{
    /// <summary>Tiny pointer handler used for joystick / look / hold buttons (works with touch and mouse).</summary>
    public class TouchZone : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
    {
        public System.Action<Vector3> onDown;
        public System.Action<Vector3, Vector2> onDrag;
        public System.Action onUp;
        int _pointer = int.MinValue;

        public void OnPointerDown(PointerEventData e)
        {
            if (_pointer != int.MinValue) return;   // already tracking a finger
            _pointer = e.pointerId;
            onDown?.Invoke(e.position);
            onDrag?.Invoke(e.position, Vector2.zero);
        }

        public void OnDrag(PointerEventData e)
        {
            if (e.pointerId != _pointer) return;
            onDrag?.Invoke(e.position, e.delta);
        }

        public void OnPointerUp(PointerEventData e)
        {
            if (e.pointerId != _pointer) return;
            _pointer = int.MinValue;
            onUp?.Invoke();
        }

        void OnDisable() { if (_pointer != int.MinValue) { _pointer = int.MinValue; onUp?.Invoke(); } }
    }
}
