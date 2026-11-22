using UnityEngine;

namespace Cubestruction
{
    /// <summary>Slow orbit around the arena for the menus; switches itself off once a
    /// player/commander camera exists, and back on when it goes away.</summary>
    [RequireComponent(typeof(Camera))]
    public class LobbyCamera : MonoBehaviour
    {
        public float radius = 24f, height = 9f, speed = 6f;
        Camera _cam;
        void Awake() { _cam = GetComponent<Camera>(); _cam.tag = "MainCamera"; }
        void LateUpdate()
        {
            bool other = false;
            foreach (var c in Camera.allCameras) if (c != _cam && c.enabled && c.targetTexture == null) { other = true; break; }
            _cam.enabled = !other;
            if (!_cam.enabled) return;
            float a = Time.time * speed * Mathf.Deg2Rad;
            transform.position = new Vector3(Mathf.Sin(a) * radius, height, -Mathf.Cos(a) * radius);
            transform.LookAt(new Vector3(0, 4f, 0));
        }
    }
}
