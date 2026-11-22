using UnityEngine;

namespace Cubestruction.Player
{
    /// <summary>
    /// Poses the little Buddy body procedurally from head + hands (no animator needed):
    /// head floats at the head anchor, torso hangs below it, arms reach toward the hands,
    /// legs waddle with movement speed. Tints the body with the player's colour.
    /// Expects child parts named Head, Torso, ArmL, ArmR, LegL, LegR (see Blender/buddy.py).
    /// </summary>
    public class AvatarVisual : MonoBehaviour
    {
        public Transform head, torso, armL, armR, legL, legR;
        public bool hideHeadForOwner = true;

        PlayerAvatar _avatar;
        Vector3 _lastPos;
        float _walkPhase;
        public bool Posing = true;   // false while ragdolled

        public static readonly string[] PartNames = { "Head", "Torso", "ArmL", "ArmR", "LegL", "LegR" };

        void Awake()
        {
            _avatar = GetComponentInParent<PlayerAvatar>();
            head = head ? head : Find("Head"); torso = torso ? torso : Find("Torso");
            armL = armL ? armL : Find("ArmL"); armR = armR ? armR : Find("ArmR");
            legL = legL ? legL : Find("LegL"); legR = legR ? legR : Find("LegR");
        }

        Transform Find(string n)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true)) if (t.name == n) return t;
            return null;
        }

        void Start()
        {
            if (_avatar == null) return;
            _avatar.colorIndex.OnValueChanged += (_, __) => Tint();
            Tint();
            if (_avatar.IsOwner && hideHeadForOwner && head)
                foreach (var r in head.GetComponentsInChildren<Renderer>()) r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        }

        public void Tint()
        {
            var block = new MaterialPropertyBlock();
            block.SetColor("_BaseColor", _avatar.Tint);
            foreach (var r in GetComponentsInChildren<Renderer>()) r.SetPropertyBlock(block);
        }

        void LateUpdate()
        {
            if (_avatar == null || !Posing || torso == null) return;
            var root = _avatar.transform;
            float s = _avatar.avatarScale.Value * root.localScale.x;
            transform.localScale = Vector3.one * _avatar.avatarScale.Value;

            var headPos = _avatar.headAnchor.position;
            var bodyFwd = Vector3.ProjectOnPlane(_avatar.headAnchor.forward, Vector3.up);
            if (bodyFwd.sqrMagnitude < 0.01f) bodyFwd = root.forward;
            var bodyRot = Quaternion.LookRotation(bodyFwd.normalized, Vector3.up);

            // Buddy proportions (metres at scale 1): neck 0.627, torso centre 0.4, shoulders 0.5, hips 0.24
            if (head) head.SetPositionAndRotation(headPos - Vector3.up * 0.06f * s, _avatar.headAnchor.rotation);
            var torsoPos = headPos - Vector3.up * (0.33f * s);
            torso.SetPositionAndRotation(torsoPos, bodyRot);
            Reach(armL, torsoPos + bodyRot * new Vector3(-0.17f, 0.1f, 0) * s, _avatar.leftHand.position, bodyRot);
            Reach(armR, torsoPos + bodyRot * new Vector3(0.17f, 0.1f, 0) * s, _avatar.rightHand.position, bodyRot);

            // legs waddle with ground speed
            float speed = (root.position - _lastPos).WithYZero().magnitude / Mathf.Max(Time.deltaTime, 1e-4f);
            _lastPos = root.position;
            _walkPhase += speed * Time.deltaTime * 9f;
            float swing = Mathf.Sin(_walkPhase) * Mathf.Clamp01(speed / 3f) * 35f;
            var hip = torsoPos - Vector3.up * 0.16f * s;
            if (legL) legL.SetPositionAndRotation(hip + bodyRot * new Vector3(-0.08f, 0, 0) * s, bodyRot * Quaternion.Euler(swing, 0, 0));
            if (legR) legR.SetPositionAndRotation(hip + bodyRot * new Vector3(0.08f, 0, 0) * s, bodyRot * Quaternion.Euler(-swing, 0, 0));
        }

        static void Reach(Transform arm, Vector3 shoulder, Vector3 hand, Quaternion bodyRot)
        {
            if (!arm) return;
            var dir = hand - shoulder;
            // arms are modelled hanging down (-Y); rotate -Y onto the shoulder->hand direction
            var rot = dir.sqrMagnitude > 1e-4f ? Quaternion.FromToRotation(Vector3.down, dir.normalized) * bodyRot : bodyRot;
            arm.SetPositionAndRotation(shoulder, rot);
        }
    }

    static class V3Ext { public static Vector3 WithYZero(this Vector3 v) => new Vector3(v.x, 0, v.z); }
}
