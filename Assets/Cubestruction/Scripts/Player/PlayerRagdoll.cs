using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Cubestruction.Player
{
    /// <summary>
    /// Explosion ragdoll. The owner triggers it (from KnockbackRpc); every client builds a jointed
    /// ragdoll from the Buddy parts locally and throws it with the same velocity.
    /// Owner: movement is frozen and the rig is dragged along with the torso
    /// (VR gets a heavy comfort vignette; flat players get a third-person chase cam).
    /// Recovers once the body settles (or after a timeout) and stands back up where it landed.
    /// </summary>
    public class PlayerRagdoll : NetworkBehaviour
    {
        public float minDuration = 1.2f, maxDuration = 4f;
        public AvatarVisual visual;
        public bool Active { get; private set; }

        PlayerAvatar _avatar;
        PlayerMotor _motor;
        readonly List<Rigidbody> _bodies = new List<Rigidbody>();
        readonly Dictionary<Transform, (Transform parent, Vector3 pos, Quaternion rot)> _home = new Dictionary<Transform, (Transform, Vector3, Quaternion)>();
        Transform _ragRoot;
        Rigidbody _torso;
        float _t, _settled;
        Vector3 _headOffset;

        void Awake()
        {
            _avatar = GetComponent<PlayerAvatar>();
            _motor = GetComponent<PlayerMotor>();
            if (!visual) visual = GetComponentInChildren<AvatarVisual>();
        }

        /// <summary>Owner only.</summary>
        public void Begin(Vector3 velocity)
        {
            if (!IsOwner || Active) return;
            RagdollRpc(true, velocity, transform.position);
        }

        /// <summary>Owner only. instant = called from a teleport.</summary>
        public void Recover(bool instant = false)
        {
            if (!IsOwner || !Active) return;
            var p = _torso ? _torso.position : transform.position;
            RagdollRpc(false, Vector3.zero, p);
        }

        [Rpc(SendTo.Everyone)]
        void RagdollRpc(bool on, Vector3 velocity, Vector3 position)
        {
            if (on) Build(velocity);
            else Teardown(position);
        }

        void Build(Vector3 velocity)
        {
            if (visual == null || visual.torso == null) return;
            Active = true; _t = 0; _settled = 0;
            visual.Posing = false;
            _ragRoot = new GameObject($"{name}_Ragdoll").transform;
            float s = visual.transform.lossyScale.x;
            var parts = new[] { visual.torso, visual.head, visual.armL, visual.armR, visual.legL, visual.legR };
            _bodies.Clear(); _home.Clear();
            foreach (var p in parts)
            {
                if (!p) continue;
                _home[p] = (p.parent, p.localPosition, p.localRotation);
                p.SetParent(_ragRoot, true);
                var rb = p.gameObject.AddComponent<Rigidbody>();
                rb.mass = p == visual.torso ? 3f : 0.6f;
                rb.linearDamping = 0.1f; rb.angularDamping = 0.6f;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
                var col = p.gameObject.AddComponent<SphereCollider>();
                float worldRadius = (p == visual.torso ? 0.17f : p == visual.head ? 0.13f : 0.06f) * s;
                col.radius = worldRadius / Mathf.Max(p.lossyScale.x, 0.01f);
                rb.linearVelocity = velocity + Random.insideUnitSphere * 1.5f;
                rb.angularVelocity = Random.insideUnitSphere * 8f;
                _bodies.Add(rb);
                if (p == visual.torso) _torso = rb;
            }
            foreach (var rb in _bodies)
            {
                if (rb == _torso) continue;
                var j = rb.gameObject.AddComponent<CharacterJoint>();
                j.connectedBody = _torso;
                j.enableProjection = true;
                var limit = j.swing1Limit; limit.limit = 70; j.swing1Limit = limit; j.swing2Limit = limit;
            }
            // ignore our own character controller
            foreach (var rb in _bodies) Physics.IgnoreCollision(rb.GetComponent<Collider>(), _motor.Controller);

            if (IsOwner)
            {
                _motor.Frozen = true;
                _motor.Controller.enabled = false;
                _headOffset = _avatar.headAnchor.position - transform.position;
                UI.GameMenus.Instance?.SetRagdollView(true, _torso.transform);
            }
        }

        void Update()
        {
            if (!Active || _torso == null) return;
            _t += Time.deltaTime;
            if (IsOwner)
            {
                // drag the rig with the body: head stays roughly above the torso
                var target = _torso.position + Vector3.up * 0.35f - _headOffset;
                transform.position = Vector3.Lerp(transform.position, target, Time.deltaTime * 10f);
                _settled = _torso.linearVelocity.magnitude < 0.6f ? _settled + Time.deltaTime : 0;
                if ((_t > minDuration && _settled > 0.5f) || _t > maxDuration) Recover();
            }
        }

        void Teardown(Vector3 standAt)
        {
            if (!Active) return;
            Active = false;
            foreach (var rb in _bodies)
            {
                if (!rb) continue;
                Destroy(rb.GetComponent<CharacterJoint>());
                Destroy(rb.GetComponent<Collider>());
                Destroy(rb);
            }
            foreach (var kv in _home)
            {
                kv.Key.SetParent(kv.Value.parent, false);
                kv.Key.localPosition = kv.Value.pos; kv.Key.localRotation = kv.Value.rot;
            }
            _bodies.Clear(); _home.Clear();
            if (_ragRoot) Destroy(_ragRoot.gameObject);
            visual.Posing = true;
            if (IsOwner)
            {
                // stand up where we landed (nudged up so we don't spawn inside voxels)
                _motor.Frozen = false;
                _motor.Teleport(new Vector3(standAt.x, standAt.y + 0.3f, standAt.z), transform.eulerAngles.y);
                UI.GameMenus.Instance?.SetRagdollView(false, null);
            }
        }
    }
}
