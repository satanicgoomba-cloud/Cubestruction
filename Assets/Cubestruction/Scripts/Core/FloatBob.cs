using UnityEngine;

namespace Cubestruction
{
    /// <summary>Gentle floating motion for decorative Aero bubbles.</summary>
    public class FloatBob : MonoBehaviour
    {
        Vector3 _start; float _phase, _speed;
        void Start() { _start = transform.position; _phase = Random.value * 10f; _speed = Random.Range(0.2f, 0.5f); }
        void Update()
        {
            float t = Time.time * _speed + _phase;
            transform.position = _start + new Vector3(Mathf.Sin(t * 0.7f) * 0.6f, Mathf.Sin(t) * 1.2f, Mathf.Cos(t * 0.5f) * 0.6f);
        }
    }
}
