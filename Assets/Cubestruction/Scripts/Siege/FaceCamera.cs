using UnityEngine;

namespace Cubestruction.Siege
{
    /// <summary>Keeps a world label turned toward the camera.</summary>
    public class FaceCamera : MonoBehaviour
    {
        void LateUpdate()
        {
            var c = Camera.main;
            if (c) transform.rotation = Quaternion.LookRotation(transform.position - c.transform.position);
        }
    }
}
