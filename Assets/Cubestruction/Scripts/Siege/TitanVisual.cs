using System.Collections.Generic;
using Cubestruction.Player;
using UnityEngine;

namespace Cubestruction.Siege
{
    /// <summary>
    /// The Titan's body, on every peer (added by AvatarVisual while the player's role is Titan).
    /// Uses the rigged model in Resources/TitanModel (Models/Titan/Titan.fbx) and poses it procedurally:
    ///   legs   walk cycle, blending into a run with speed (longer strides, deeper knees, lean); kicks
    ///   arms   two-bone IK to the player's tracked hands (VR) / view-model hands (desktop)
    ///   head   follows the headset / camera
    /// No Animator: every bone is set from the pose captured at bind time, relative to the body's yaw.
    /// </summary>
    public class TitanVisual : MonoBehaviour
    {
        Transform _root, _headT, _lHand, _rHand;
        bool _owner;
        Transform _model;
        Quaternion _fix = Quaternion.identity;   // turns the model to face +Z if it was authored facing -Z
        float _headBindY, _height, _phase, _run, _move, _kickT = -1;
        bool _kickLeft;
        Vector3 _lastPos;
        Quaternion _yaw = Quaternion.identity;

        class Bone { public Transform t; public Quaternion rel; public Quaternion local; }
        class Leg { public Bone thigh, shin, foot; public float phaseOffset; }
        class Arm { public Bone upper, fore, hand; public float l1, l2; public bool right; }
        Leg _legL, _legR;
        Arm _armL, _armR;
        Bone _spine, _chest, _neck, _head;
        readonly List<Renderer> _buddy = new List<Renderer>();
        public static int Built;   // -autotest

        public bool HasModel => _model != null;

        public void Init(PlayerAvatar avatar) => Init(avatar.transform, avatar.headAnchor, avatar.leftHand, avatar.rightHand, avatar.IsOwner, "Titan_" + avatar.OwnerClientId);

        /// <summary>Generic: a player's Titan or the AI Titan (TitanBot), from its root, head and hands.</summary>
        public void Init(Transform root, Transform head0, Transform lHand, Transform rHand, bool isOwner, string name)
        {
            _root = root; _headT = head0; _lHand = lHand; _rHand = rHand; _owner = isOwner;
            var prefab = Resources.Load<GameObject>("TitanModel");
            if (!prefab) { Debug.LogWarning("TitanVisual: Resources/TitanModel missing"); return; }
            _model = Instantiate(prefab).transform;
            _model.name = name;
            _model.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            _model.localScale = Vector3.one;

            var map = new Dictionary<string, Transform>();
            foreach (var t in _model.GetComponentsInChildren<Transform>(true)) map[t.name] = t;
            Transform B(string id) => map.TryGetValue("jnt " + id, out var t) ? t : (map.TryGetValue("jnt_" + id, out t) ? t : null);

            // which way does it face? the face bones sit in front of the head
            var head = B("005"); var face = B("094");
            if (head && face && face.position.z < head.position.z) { _fix = Quaternion.Euler(0, 180, 0); _model.rotation = _fix; }
            _headBindY = head ? head.position.y : 2.66f;

            Bone Cap(Transform t) => t ? new Bone { t = t, rel = t.rotation, local = t.localRotation } : null;
            // sides from bind positions: +X is the character's right once it faces +Z
            Leg MakeLeg(string th, string sh, string ft) => new Leg { thigh = Cap(B(th)), shin = Cap(B(sh)), foot = Cap(B(ft)) };
            var l1 = MakeLeg("007", "009", "011"); var l2 = MakeLeg("008", "010", "012");
            bool l1Right = l1.thigh != null && l1.thigh.t.position.x > 0;
            _legR = l1Right ? l1 : l2; _legL = l1Right ? l2 : l1;
            _legL.phaseOffset = Mathf.PI;
            Arm MakeArm(string up, string fo, string ha)
            {
                var a = new Arm { upper = Cap(B(up)), fore = Cap(B(fo)), hand = Cap(B(ha)) };
                if (a.upper != null && a.fore != null && a.hand != null)
                {
                    a.l1 = Vector3.Distance(a.upper.t.position, a.fore.t.position);
                    a.l2 = Vector3.Distance(a.fore.t.position, a.hand.t.position);
                    a.right = a.upper.t.position.x > 0;
                }
                return a;
            }
            var a1 = MakeArm("017", "019", "021"); var a2 = MakeArm("018", "020", "022");
            _armR = a1.right ? a1 : a2; _armL = a1.right ? a2 : a1;
            _spine = Cap(B("002")); _chest = Cap(B("003")); _neck = Cap(B("004")); _head = Cap(head);

            foreach (var smr in _model.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                smr.updateWhenOffscreen = true;
                smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                // your own head would fill the view: hide it from yourself
                if (isOwner && (smr.name.Contains("Head") || smr.name.Contains("Hair")))
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            }
            _lastPos = root.position;
            _yaw = Quaternion.Euler(0, head0.eulerAngles.y, 0);
            Built++;
        }

        Transform _nape;
        float _steamAt;

        /// <summary>The weak spot: a glowing patch on the back of the neck, where shots do triple damage.</summary>
        void EnsureNape()
        {
            if (_nape) return;
            var g = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Destroy(g.GetComponent<Collider>());
            g.name = "Nape";
            var mr = g.GetComponent<MeshRenderer>();
            if (Tools.FxHub.Instance) mr.sharedMaterial = Tools.FxHub.Instance.glowMaterial;
            var b = new MaterialPropertyBlock(); b.SetColor("_BaseColor", new Color(1f, 0.3f, 0.15f, 0.85f)); mr.SetPropertyBlock(b);
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _nape = g.transform;
        }

        void OnDestroy()
        {
            if (_nape) Destroy(_nape.gameObject);
            if (_model) Destroy(_model.gameObject);
        }

        public void Kick(bool left)
        {
            if (_kickT >= 0) return;
            _kickT = 0; _kickLeft = left;
        }

        float _diagAt = -1;
        void LateUpdate()
        {
            if (!_model || !_root) return;
            if (_diagAt < 0) _diagAt = Time.time + 1.5f;
            else if (_diagAt > 0 && Time.time > _diagAt && _head != null && _armR?.hand != null)
            {
                _diagAt = 0;
                Debug.Log($"[TitanVisual] model scale {_model.localScale.x:0.00} (lossy {_model.lossyScale.x:0.00}), headBindY {_headBindY:0.00}, fix {_fix.eulerAngles.y:0}, " +
                          $"root y {_root.position.y:0.0}, head bone y {_head.t.position.y:0.0}, target head y {_headT.position.y:0.0}, " +
                          $"R hand bone {_armR.hand.t.position} target {_rHand.position}, shoulder {_armR.upper.t.position}, l1 {_armR.l1:0.00} l2 {_armR.l2:0.00}, " +
                          $"bones: thighR {_legR.thigh?.t.name} upperR {_armR.upper.t.name} right? {_armR.right}");
            }
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            var root = _root;
            float scale = root.localScale.x;

            // size: tall enough that its head sits at the player's head
            float headH = Mathf.Max(0.5f, _headT.position.y - root.position.y);
            _height = _height <= 0 ? headH : Mathf.Max(headH, Mathf.Lerp(_height, headH, dt * 0.05f));
            float k = _height / Mathf.Max(0.1f, _headBindY);
            _model.localScale = Vector3.one * k;

            // body yaw follows the head, smoothly
            var hf = Vector3.ProjectOnPlane(_headT.forward, Vector3.up);
            if (hf.sqrMagnitude > 0.01f) _yaw = Quaternion.Slerp(_yaw, Quaternion.LookRotation(hf.normalized), dt * 5f);

            // gait from ground speed (in the Titan's own metres)
            var flat = root.position - _lastPos; flat.y = 0;
            _lastPos = root.position;
            float real = flat.magnitude / dt / Mathf.Max(scale, 0.1f);
            _move = Mathf.MoveTowards(_move, Mathf.Clamp01(real / 0.35f), dt * 3f);
            _run = Mathf.MoveTowards(_run, Mathf.Clamp01((real - 0.8f) / 0.5f), dt * 2f);
            _phase += dt * Mathf.PI * 2f * Mathf.Lerp(0.9f, 1.5f, _run) * Mathf.Max(0.15f, _move);

            float bob = _move * Mathf.Lerp(0.015f, 0.04f, _run) * Mathf.Abs(Mathf.Sin(_phase));
            _model.SetPositionAndRotation(root.position + Vector3.up * bob * _height, _yaw * _fix);
            var yawFix = _yaw;   // bind rotations were captured with the model already turned by _fix

            float lean = Mathf.Lerp(3f, 12f, _run) * _move;
            if (_kickT >= 0) lean = -10f * Mathf.Sin(Mathf.Clamp01(_kickT) * Mathf.PI);
            Set(_spine, yawFix, Quaternion.AngleAxis(lean, Vector3.right));
            if (_chest != null) _chest.t.localRotation = _chest.local;

            // legs
            float amp = Mathf.Lerp(26f, 46f, _run) * _move, knee = Mathf.Lerp(38f, 80f, _run) * _move;
            foreach (var leg in new[] { _legL, _legR })
            {
                if (leg.thigh == null || leg.shin == null) continue;
                float ph = _phase + leg.phaseOffset;
                float a = amp * Mathf.Sin(ph);
                float kb = knee * Mathf.Max(0f, Mathf.Cos(ph)) + 4f * _move;
                if (_kickT >= 0 && (leg == _legL) == _kickLeft) KickPose(_kickT, out a, out kb);
                Set(leg.thigh, yawFix, Quaternion.AngleAxis(-a, Vector3.right));
                Set(leg.shin, yawFix, Quaternion.AngleAxis(-(a - kb), Vector3.right));
                if (leg.foot != null) Set(leg.foot, yawFix, Quaternion.AngleAxis(-Mathf.Min(0f, a - kb) * 0.4f, Vector3.right));
            }
            if (_kickT >= 0) { _kickT += dt / 0.9f; if (_kickT >= 1f) _kickT = -1; }

            // arms reach for the hands
            Reach(_armL, _lHand.position, yawFix, -1f);
            Reach(_armR, _rHand.position, yawFix, 1f);

            // head looks where the player looks (within a natural range)
            if (_head != null)
            {
                var look = Quaternion.Inverse(_yaw) * _headT.rotation;
                var e = look.eulerAngles;
                float pitch = Mathf.Clamp(Mathf.DeltaAngle(0, e.x), -50f, 60f), yawD = Mathf.Clamp(Mathf.DeltaAngle(0, e.y), -70f, 70f);
                var headRot = _yaw * Quaternion.Euler(pitch, yawD, 0);
                if (_neck != null) _neck.t.rotation = Quaternion.Slerp(yawFix, headRot, 0.4f) * _neck.rel;
                _head.t.rotation = headRot * _head.rel;
            }

            // nape marker (same spot the server checks), pulsing
            EnsureNape();
            float sc = root.localScale.x;
            var fwd = Vector3.ProjectOnPlane(_headT.forward, Vector3.up);
            fwd = fwd.sqrMagnitude > 0.01f ? fwd.normalized : _yaw * Vector3.forward;
            _nape.position = _headT.position - fwd * 0.11f * sc - Vector3.up * 0.13f * sc;
            _nape.rotation = Quaternion.LookRotation(-fwd);
            _nape.localScale = new Vector3(0.16f, 0.1f, 0.04f) * sc * (1f + 0.12f * Mathf.Sin(Time.time * 6f));

            // rage: steam pours off the body
            var td = TitanDirector.Instance;
            int rage = td ? td.rage.Value : 0;
            if (rage > 0 && Time.time >= _steamAt && Tools.FxHub.Instance)
            {
                _steamAt = Time.time + (rage == 2 ? 0.08f : 0.2f);
                var p = root.position + Vector3.up * Random.Range(0.3f, 0.9f) * _height + Random.insideUnitSphere * 0.15f * _height;
                Tools.FxHub.Instance.LocalBlast(p, Random.Range(0.6f, 1.4f) * sc * 0.25f, new Color(1f, 1f, 1f, 0.35f));
            }
        }

        /// <summary>Wind up (thigh back, knee bent), snap through (straight leg, high), recover.</summary>
        static void KickPose(float t, out float thigh, out float knee)
        {
            if (t < 0.3f) { float u = t / 0.3f; thigh = -35f * u; knee = 75f * u; }
            else if (t < 0.5f) { float u = (t - 0.3f) / 0.2f; thigh = Mathf.Lerp(-35f, 95f, u); knee = Mathf.Lerp(75f, 0f, u); }
            else { float u = (t - 0.5f) / 0.5f; thigh = Mathf.Lerp(95f, 0f, u); knee = Mathf.Lerp(0f, 10f, Mathf.Sin(u * Mathf.PI)); }
        }

        static void Set(Bone b, Quaternion yawFix, Quaternion extra)
        {
            if (b == null) return;
            b.t.rotation = yawFix * extra * b.rel;
        }

        /// <summary>Two-bone IK: shoulder -> elbow -> wrist at the target, elbow pointing down, back and out.</summary>
        void Reach(Arm a, Vector3 target, Quaternion yawFix, float side)
        {
            if (a == null || a.upper == null || a.fore == null || a.hand == null) return;
            float k = _model.lossyScale.x;
            float l1 = a.l1 * k, l2 = a.l2 * k;
            a.upper.t.rotation = yawFix * a.upper.rel;
            a.fore.t.localRotation = a.fore.local;
            a.hand.t.localRotation = a.hand.local;
            var shoulder = a.upper.t.position;
            var to = target - shoulder;
            float d = Mathf.Clamp(to.magnitude, Mathf.Abs(l1 - l2) + 1e-3f, (l1 + l2) * 0.999f);
            var dir = to.sqrMagnitude > 1e-6f ? to.normalized : _yaw * Vector3.forward;
            var pole = _yaw * new Vector3(side * 0.5f, -1f, -0.6f);
            pole = Vector3.ProjectOnPlane(pole, dir).normalized;
            float along = (l1 * l1 - l2 * l2 + d * d) / (2f * d);
            float h = Mathf.Sqrt(Mathf.Max(0f, l1 * l1 - along * along));
            var elbow = shoulder + dir * along + pole * h;
            var wrist = shoulder + dir * d;

            var cur = a.fore.t.position - shoulder;
            a.upper.t.rotation = Quaternion.FromToRotation(cur, elbow - shoulder) * a.upper.t.rotation;
            cur = a.hand.t.position - a.fore.t.position;
            a.fore.t.rotation = Quaternion.FromToRotation(cur, wrist - elbow) * a.fore.t.rotation;
        }
    }
}
