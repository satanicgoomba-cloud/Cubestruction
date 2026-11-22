using UnityEngine;

namespace Cubestruction.Tools
{
    /// <summary>
    /// Echo Clones, drawn on every peer: three glassy copies of a player standing at
    /// <see cref="ToolController.CloneOffsets"/>, following them while <c>clonesOn</c> is set.
    /// (The server is what makes them fire: it repeats the player's actions from these spots.)
    /// </summary>
    public class CloneEcho : MonoBehaviour
    {
        ToolController _tc;
        readonly Transform[] _ghosts = new Transform[3];

        public static void Sync(ToolController tc, bool on)
        {
            var e = tc.GetComponent<CloneEcho>();
            if (on && e == null) { e = tc.gameObject.AddComponent<CloneEcho>(); e._tc = tc; e.Build(); }
            else if (!on && e != null) e.Remove();
        }

        void Build()
        {
            var glow = FxHub.Instance ? FxHub.Instance.glowMaterial : null;
            for (int i = 0; i < _ghosts.Length; i++)
            {
                var root = new GameObject("EchoClone" + i).transform;
                var mat = new Material(glow) { color = Color.HSVToRGB(0.5f + i * 0.08f, 0.45f, 1f) * new Color(1, 1, 1, 0.45f) };
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Destroy(body.GetComponent<Collider>());
                body.transform.SetParent(root, false);
                body.transform.localPosition = new Vector3(0, 0.45f, 0);
                body.transform.localScale = new Vector3(0.45f, 0.45f, 0.45f);
                body.GetComponent<MeshRenderer>().material = mat;
                var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(head.GetComponent<Collider>());
                head.transform.SetParent(root, false);
                head.transform.localPosition = new Vector3(0, 0.95f, 0);
                head.transform.localScale = Vector3.one * 0.42f;
                head.GetComponent<MeshRenderer>().material = mat;
                _ghosts[i] = root;
            }
            FxHub.Instance?.LocalBlast(transform.position + Vector3.up, 1.2f, new Color(0.6f, 0.95f, 1f));
        }

        void LateUpdate()
        {
            if (_tc == null) { Remove(); return; }
            int i = 0;
            float s = transform.localScale.x;
            foreach (var off in _tc.CloneOffsetsWorld())
            {
                var g = _ghosts[i];
                if (g)
                {
                    g.SetPositionAndRotation(transform.position + off + Vector3.up * Mathf.Sin(Time.time * 3f + i) * 0.05f * s,
                                             Quaternion.Euler(0, transform.eulerAngles.y, 0));
                    g.localScale = Vector3.one * s;
                }
                i++;
            }
        }

        void Remove()
        {
            foreach (var g in _ghosts) if (g) Destroy(g.gameObject);
            Destroy(this);
        }

        void OnDestroy() { foreach (var g in _ghosts) if (g) Destroy(g.gameObject); }
    }
}
