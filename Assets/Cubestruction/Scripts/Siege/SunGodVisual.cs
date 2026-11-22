using System.Collections.Generic;
using Cubestruction.Player;
using Cubestruction.Tools;
using UnityEngine;

namespace Cubestruction.Siege
{
    /// <summary>
    /// The God's body on every peer (added by AvatarVisual while the role is God): a big smiling sun with
    /// flat black cool-guy shades where the head is, rays slowly turning around it, and a pair of human
    /// hands at the tracked hands whose fingers curl with grip (fist) and trigger (index finger).
    /// The owner doesn't see their own sun (it surrounds the camera), only their hands.
    /// </summary>
    public class SunGodVisual : MonoBehaviour
    {
        PlayerAvatar _avatar;
        Transform _sun, _rays;
        readonly List<GameObject> _parts = new List<GameObject>();
        readonly List<Renderer> _hidden = new List<Renderer>();
        Hand _left, _right;
        Material _lit, _glow;
        public static int Built;   // -autotest

        class Hand { public Transform root; public Transform[][] fingers; public float curl, index; public bool isLeft; }

        static readonly Color Skin = new Color(1f, 0.8f, 0.6f);
        static readonly Color Shade = new Color(0.03f, 0.03f, 0.05f);

        public void Init(PlayerAvatar avatar)
        {
            _avatar = avatar;
            _glow = FxHub.Instance ? FxHub.Instance.glowMaterial : null;
            foreach (var r in GetComponentsInChildren<Renderer>(true)) if (!_lit && r.sharedMaterial) _lit = r.sharedMaterial;

            BuildSun();
            _left = BuildHand(avatar.leftHand, true);
            _right = BuildHand(avatar.rightHand, false);
            Built++;
        }

        static bool InMitten(Transform t)
        {
            for (; t != null; t = t.parent) if (t.name.StartsWith("Mitten")) return true;
            return false;
        }

        GameObject Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 euler, Vector3 scale, Color c, bool glow)
        {
            var g = GameObject.CreatePrimitive(type);
            Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            g.transform.localPosition = pos; g.transform.localEulerAngles = euler; g.transform.localScale = scale;
            var mr = g.GetComponent<MeshRenderer>();
            var m = glow ? _glow : _lit;
            if (m) mr.sharedMaterial = m;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var b = new MaterialPropertyBlock(); b.SetColor("_BaseColor", c); b.SetColor("_Color", c);
            mr.SetPropertyBlock(b);
            _parts.Add(g);
            return g;
        }

        void BuildSun()
        {
            _sun = new GameObject("SunGod").transform;
            _parts.Add(_sun.gameObject);
            var sunCol = new Color(1f, 0.82f, 0.25f, 1f);
            Part(_sun, PrimitiveType.Sphere, Vector3.zero, Vector3.zero, Vector3.one * 0.56f, sunCol, false);
            Part(_sun, PrimitiveType.Sphere, Vector3.zero, Vector3.zero, Vector3.one * 0.72f, new Color(1f, 0.75f, 0.2f, 0.25f), true);   // halo
            _rays = new GameObject("Rays").transform;
            _rays.SetParent(_sun, false);
            for (int i = 0; i < 12; i++)
            {
                float a = i * 30f;
                var ray = new GameObject("Ray").transform;
                ray.SetParent(_rays, false);
                ray.localRotation = Quaternion.Euler(0, 0, a);
                bool longRay = i % 2 == 0;
                Part(ray, PrimitiveType.Cube, new Vector3(0, longRay ? 0.43f : 0.39f, -0.02f), new Vector3(0, 0, 45), new Vector3(0.07f, 0.07f, 0.02f), new Color(1f, 0.65f, 0.15f, 1f), false);
                Part(ray, PrimitiveType.Cube, new Vector3(0, longRay ? 0.38f : 0.35f, -0.02f), Vector3.zero, new Vector3(0.045f, longRay ? 0.12f : 0.07f, 0.02f), new Color(1f, 0.72f, 0.2f, 1f), false);
            }
            // the shades: a flat black bar with two square lenses, and a wide grin
            float z = 0.268f;
            Part(_sun, PrimitiveType.Cube, new Vector3(0, 0.07f, z), Vector3.zero, new Vector3(0.36f, 0.025f, 0.02f), Shade, false);
            Part(_sun, PrimitiveType.Cube, new Vector3(-0.085f, 0.035f, z), Vector3.zero, new Vector3(0.13f, 0.075f, 0.02f), Shade, false);
            Part(_sun, PrimitiveType.Cube, new Vector3(0.085f, 0.035f, z), Vector3.zero, new Vector3(0.13f, 0.075f, 0.02f), Shade, false);
            Part(_sun, PrimitiveType.Cube, new Vector3(-0.11f, 0.05f, z + 0.011f), Vector3.zero, new Vector3(0.03f, 0.015f, 0.005f), new Color(1, 1, 1, 1), false);   // glint
            Part(_sun, PrimitiveType.Cube, new Vector3(0.06f, 0.05f, z + 0.011f), Vector3.zero, new Vector3(0.03f, 0.015f, 0.005f), new Color(1, 1, 1, 1), false);
            for (int i = 0; i < 9; i++)
            {
                float t = (i - 4) / 4f;
                Part(_sun, PrimitiveType.Cube, new Vector3(t * 0.12f, -0.09f + t * t * 0.045f, z - 0.01f + (1 - t * t) * 0.012f), new Vector3(0, 0, t * 25f), new Vector3(0.035f, 0.022f, 0.02f), Shade, false);
            }
            if (_avatar.IsOwner)
                foreach (var r in _sun.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        }

        /// <summary>Palm, four two-jointed fingers and a thumb. Fingers point along +Z (the controller's
        /// forward); the palm faces the body's centre line.</summary>
        Hand BuildHand(Transform anchor, bool left)
        {
            var h = new Hand { isLeft = left, root = new GameObject(left ? "GodHandL" : "GodHandR").transform };
            h.root.SetParent(anchor, false);
            h.root.localPosition = new Vector3(0, -0.01f, -0.03f);
            _parts.Add(h.root.gameObject);
            float side = left ? 1f : -1f;   // towards the palm
            Part(h.root, PrimitiveType.Cube, new Vector3(0, 0, 0.02f), Vector3.zero, new Vector3(0.035f, 0.085f, 0.095f), Skin, false);
            Part(h.root, PrimitiveType.Sphere, new Vector3(0, 0, -0.035f), Vector3.zero, new Vector3(0.04f, 0.07f, 0.05f), Skin, false);   // wrist
            h.fingers = new Transform[5][];
            float[] ys = { 0.03f, 0.01f, -0.01f, -0.03f };
            float[] lens = { 0.045f, 0.05f, 0.047f, 0.038f };
            for (int f = 0; f < 4; f++)
            {
                var j1 = new GameObject("F" + f).transform; j1.SetParent(h.root, false); j1.localPosition = new Vector3(0, ys[f], 0.067f);
                Part(j1, PrimitiveType.Capsule, new Vector3(0, 0, lens[f] * 0.5f), new Vector3(90, 0, 0), new Vector3(0.019f, lens[f] * 0.5f, 0.019f), Skin, false);
                var j2 = new GameObject("F" + f + "b").transform; j2.SetParent(j1, false); j2.localPosition = new Vector3(0, 0, lens[f]);
                Part(j2, PrimitiveType.Capsule, new Vector3(0, 0, lens[f] * 0.4f), new Vector3(90, 0, 0), new Vector3(0.017f, lens[f] * 0.4f, 0.017f), Skin, false);
                h.fingers[f] = new[] { j1, j2 };
            }
            var t1 = new GameObject("Thumb").transform; t1.SetParent(h.root, false);
            t1.localPosition = new Vector3(side * 0.02f, 0.04f, 0.0f);
            t1.localRotation = Quaternion.Euler(-35f, side * -20f, 0);
            Part(t1, PrimitiveType.Capsule, new Vector3(0, 0, 0.025f), new Vector3(90, 0, 0), new Vector3(0.022f, 0.025f, 0.022f), Skin, false);
            var t2 = new GameObject("ThumbB").transform; t2.SetParent(t1, false); t2.localPosition = new Vector3(0, 0, 0.05f);
            Part(t2, PrimitiveType.Capsule, new Vector3(0, 0, 0.018f), new Vector3(90, 0, 0), new Vector3(0.02f, 0.02f, 0.02f), Skin, false);
            h.fingers[4] = new[] { t1, t2 };
            return h;
        }

        void OnDestroy()
        {
            foreach (var p in _parts) if (p) Destroy(p);
        }

        float _diagAt = -1;
        void LateUpdate()
        {
            if (!_avatar) return;
            if (_diagAt < 0) _diagAt = Time.time + 1f;
            else if (_diagAt > 0 && Time.time > _diagAt)
            {
                _diagAt = 0;
                var cam = Camera.main;
                var r = _right.root.GetComponentInChildren<MeshRenderer>();
                Debug.Log($"[SunGodVisual] right hand at {_right.root.position} (lossy {_right.root.lossyScale.x:0.0}), cam {(cam ? cam.transform.position.ToString() : "none")}, " +
                          $"renderer {(r ? r.enabled + " " + r.sharedMaterial?.shader?.name + " bounds " + r.bounds.size : "none")}, lit {(_lit ? _lit.shader.name : "null")}, " +
                          $"viewport {(cam ? cam.WorldToViewportPoint(_right.root.position).ToString() : "")}");
            }
            float s = _avatar.transform.localScale.x;
            var head = _avatar.headAnchor;
            _sun.SetPositionAndRotation(head.position, Quaternion.Euler(0, head.eulerAngles.y, 0) * Quaternion.Euler(Mathf.Clamp(Mathf.DeltaAngle(0, head.eulerAngles.x), -30f, 30f), 0, 0));
            _sun.localScale = Vector3.one * s;
            _rays.localRotation = Quaternion.Euler(0, 0, Time.time * 12f);
            float pulse = 1f + 0.03f * Mathf.Sin(Time.time * 2.5f);
            _rays.localScale = Vector3.one * pulse;

            byte pose = _avatar.handPose.Value;
            Pose(_left, (pose & 1) != 0, (pose & 4) != 0);
            Pose(_right, (pose & 2) != 0, (pose & 8) != 0);
        }

        void Pose(Hand h, bool grip, bool trigger)
        {
            float dt = Time.deltaTime;
            h.curl = Mathf.MoveTowards(h.curl, grip ? 1f : 0.12f, dt * 8f);
            h.index = Mathf.MoveTowards(h.index, trigger || grip ? 1f : 0.1f, dt * 10f);
            float side = h.isLeft ? 1f : -1f;
            for (int f = 0; f < 4; f++)
            {
                float c = f == 0 ? h.index : h.curl;
                h.fingers[f][0].localRotation = Quaternion.AngleAxis(side * -75f * c, Vector3.up);
                h.fingers[f][1].localRotation = Quaternion.AngleAxis(side * -95f * c, Vector3.up);
            }
            h.fingers[4][1].localRotation = Quaternion.AngleAxis(side * -50f * h.curl, Vector3.up);
        }
    }
}
