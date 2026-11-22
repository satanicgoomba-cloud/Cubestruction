using Cubestruction.Voxels;
using UnityEngine;

namespace Cubestruction.Player
{
    /// <summary>
    /// Shared movement for VR and flat players (owner-only simulation; position is synced by the
    /// owner-authoritative NetworkTransform). Rigs feed it intent; it handles gravity, climbing,
    /// stamina, jetpack/boots/grapple movement tools and explosion impulses.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMotor : MonoBehaviour
    {
        public float walkSpeed = 3.2f;
        public float sprintSpeed = 5.5f;
        public float gravity = -14f;
        public float jumpSpeed = 5.2f;
        public float airControl = 0.35f;
        public float baseClimbStamina = 8f;

        public CharacterController Controller { get; private set; }
        public Vector3 Velocity;
        public bool Grounded { get; private set; }
        public bool Climbing { get; private set; }
        public float Stamina { get; private set; }
        public float MaxStamina => baseClimbStamina * (HasTool("ClimbingGloves") ? 2f : 1f);
        public float ClimbSpeedMul => HasTool("ClimbingGloves") ? 1.4f : 1f;
        public float JetFuel { get; private set; } = 6f;
        public const float MaxJetFuel = 6f;
        public float WorldScale { get => transform.localScale.x; set => transform.localScale = Vector3.one * value; }
        public bool Frozen;    // ragdoll / menus

        // intent set by rigs every frame
        [HideInInspector] public Vector3 moveIntent;      // world-space, horizontal, magnitude 0..1
        [HideInInspector] public bool sprint;
        [HideInInspector] public Vector3 climbDelta;      // world-space translation requested by climbing hands this frame
        [HideInInspector] public bool climbHeld;          // any hand gripping something climbable
        [HideInInspector] public bool jetHeld;

        PlayerAvatar _avatar;
        bool _usedDoubleJump;
        Vector3 _grappleTarget; bool _grappling;

        void Awake()
        {
            Controller = GetComponent<CharacterController>();
            _avatar = GetComponent<PlayerAvatar>();
            Stamina = baseClimbStamina;
        }

        public bool HasTool(string id)
        {
            var t = Balance.Tool(id);
            return t != null && _avatar != null && _avatar.IsSpawned && _avatar.Owns(t.index);
        }

        public void Teleport(Vector3 pos, float yaw)
        {
            Controller.enabled = false;
            transform.SetPositionAndRotation(pos, Quaternion.Euler(0, yaw, 0));
            Controller.enabled = true;
            Velocity = Vector3.zero;
            _grappling = false;
        }

        public void AddImpulse(Vector3 v) { Velocity += v; Grounded = false; _grappling = false; }

        public void Jump()
        {
            if (Frozen) return;
            if (Grounded || Climbing) { Velocity.y = jumpSpeed; Grounded = false; Climbing = false; return; }
            if (!_usedDoubleJump && HasTool("RocketBoots"))
            {
                _usedDoubleJump = true;
                var fwd = moveIntent.sqrMagnitude > 0.01f ? moveIntent.normalized : Vector3.zero;
                Velocity = new Vector3(Velocity.x, 0, Velocity.z) + Vector3.up * jumpSpeed * 1.15f + fwd * 4f;
            }
        }

        public void StartGrapple(Vector3 target) { _grappleTarget = target; _grappling = true; }
        public void StopGrapple() { _grappling = false; }
        public bool IsGrappling => _grappling;

        void Update()
        {
            if (Frozen || !Controller.enabled) return;
            float dt = Time.deltaTime;
            float scale = WorldScale;

            // ---- climbing: hands (VR) or spider-climb (flat) move us directly, gravity off
            bool canClimb = Stamina > 0f;
            if (climbHeld && canClimb)
            {
                Climbing = true;
                Stamina -= dt;
                Velocity = Vector3.zero;
                Controller.Move(climbDelta);
                climbDelta = Vector3.zero;
                _usedDoubleJump = false;
                JetFuel = Mathf.Min(MaxJetFuel, JetFuel + dt * 2f);
                return;
            }
            if (Climbing) Climbing = false; // released: keep whatever fling velocity the rig set
            climbDelta = Vector3.zero;

            // ---- grapple: spring toward the anchor
            if (_grappling)
            {
                var to = _grappleTarget - transform.position;
                if (to.magnitude < 1.2f * scale) _grappling = false;
                else Velocity = Vector3.Lerp(Velocity, to.normalized * 14f * scale, dt * 4f);
            }

            // ---- walking
            Vector3 targetH = moveIntent * (sprint ? sprintSpeed : walkSpeed) * scale;
            Vector3 h = new Vector3(Velocity.x, 0, Velocity.z);
            float control = Grounded ? 12f : 12f * airControl;
            if (!_grappling) h = Vector3.MoveTowards(h, targetH, control * scale * dt);
            Velocity.x = h.x; Velocity.z = h.z;

            // ---- jetpack
            if (jetHeld && JetFuel > 0 && HasTool("Jetpack"))
            {
                JetFuel -= dt;
                Velocity.y = Mathf.MoveTowards(Velocity.y, 4.5f * scale, 25f * dt);
            }
            else if (!_grappling) Velocity.y += gravity * scale * dt;

            var flags = Controller.Move(Velocity * dt);
            Grounded = (flags & CollisionFlags.Below) != 0;
            if (Grounded)
            {
                if (Velocity.y < 0) Velocity.y = -1f;
                _usedDoubleJump = false;
                Stamina = Mathf.Min(MaxStamina, Stamina + dt * 3f);
                JetFuel = Mathf.Min(MaxJetFuel, JetFuel + dt * 1.5f);
                // friction bleeds off explosion impulses
                Velocity.x *= 1f - Mathf.Clamp01(dt * 6f); Velocity.z *= 1f - Mathf.Clamp01(dt * 6f);
            }
            if ((flags & CollisionFlags.Above) != 0 && Velocity.y > 0) Velocity.y = 0;

            // fell off the arena (the meadow is 12 m down): respawn on the pedestal edge
            if (transform.position.y < -8f)
            {
                var cube = VoxelCube.Instance;
                var p = cube ? cube.transform.position + Vector3.back * (cube.HalfExtent + 2.5f) : Vector3.up;
                Teleport(p + Vector3.up, 0);
            }
        }
    }
}
