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
        /// <summary>Walk/sprint speed multiplier (the Titan is huge but shouldn't cross the map in 2 s).</summary>
        [System.NonSerialized] public float speedMul = 1f;
        [System.NonSerialized] public bool canJump = true;
        /// <summary>Owner: every surface the capsule touches while moving (the Titan grinds walls with it).</summary>
        public System.Action<ControllerColliderHit> OnHit;
        /// <summary>Set while something (the God's hand) holds this player: the body follows it, no simulation.</summary>
        [System.NonSerialized] public Transform heldBy;
        [System.NonSerialized] public Vector3 heldOffset;
        /// <summary>A super mode (Meteor Dive) drives the body directly: gravity and input are ignored.</summary>
        [System.NonSerialized] public Vector3? forcedVelocity;
        public CollisionFlags LastFlags { get; private set; }

        // intent set by rigs every frame
        [HideInInspector] public Vector3 moveIntent;      // world-space, horizontal, magnitude 0..1
        [HideInInspector] public bool sprint;
        [HideInInspector] public Vector3 climbDelta;      // world-space translation requested by climbing hands this frame
        [HideInInspector] public bool climbHeld;          // any hand gripping something climbable
        [HideInInspector] public bool jetHeld;
        [HideInInspector] public bool reelIn;             // swinging: shorten the rope (jump held)

        // rope swing (Zip Tether)
        public const float MinRope = 1.5f, ReelSpeed = 10f, RopeSpring = 25f, SwingPump = 9f;
        public float RopeLength { get; private set; }
        public Vector3 RopeAnchor => _grappleTarget;
        /// <summary>Where the rope attaches on the body.</summary>
        public Vector3 Chest => transform.position + Vector3.up * WorldScale;

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

        // ---- Bubble Wand ride: gravity off, gentle lift, full steering
        float _floatLeft;
        public bool Floating => _floatLeft > 0;
        public void Float(float seconds)
        {
            _floatLeft = Mathf.Max(_floatLeft, seconds);
            Grounded = false;
            if (_grappling) StopGrapple();
            // pop off the ground first: standing pins vertical speed at -1, which the gentle lift alone can't beat
            Velocity.y = Mathf.Max(Velocity.y, 2f * WorldScale);
        }

        public void AddImpulse(Vector3 v) { Velocity += v; Grounded = false; }   // ropes hold: rocket-jump mid-swing

        public void Jump()
        {
            if (Frozen || _grappling || !canJump) return;   // while swinging, jump reels in instead
            if (Grounded || Climbing) { Velocity.y = jumpSpeed; Grounded = false; Climbing = false; return; }
            if (!_usedDoubleJump && HasTool("RocketBoots"))
            {
                _usedDoubleJump = true;
                var fwd = moveIntent.sqrMagnitude > 0.01f ? moveIntent.normalized : Vector3.zero;
                Velocity = new Vector3(Velocity.x, 0, Velocity.z) + Vector3.up * jumpSpeed * 1.15f + fwd * 4f;
            }
        }

        /// <summary>Attach a rope. It starts shorter than the distance and yanks, so it lifts you off the ground.</summary>
        public void StartGrapple(Vector3 target)
        {
            _grappleTarget = target;
            _grappling = true;
            // start well short of the distance and yank toward the anchor, so attaching lifts you
            RopeLength = Mathf.Max(MinRope * WorldScale, Vector3.Distance(Chest, target) * 0.7f);
            Velocity += (target - Chest).normalized * 6f * WorldScale;
            Grounded = false;
        }

        /// <summary>Let go: keep the swing's momentum plus a little hop, Spider-Man style.</summary>
        public void StopGrapple()
        {
            if (!_grappling) return;
            _grappling = false;
            Velocity = Velocity * 1.08f + Vector3.up * 2.5f * WorldScale;
            _usedDoubleJump = false;
        }
        public bool IsGrappling => _grappling;

        void Update()
        {
            if (heldBy != null)
            {
                // dangling from a giant hand: follow it exactly; physics resumes on release (Teleport re-enables)
                if (Controller.enabled) Controller.enabled = false;
                transform.position = heldBy.position + heldOffset;
                Velocity = Vector3.zero;
                return;
            }
            if (Frozen || !Controller.enabled) return;
            float dt = Time.deltaTime;
            float scale = WorldScale;
            if (forcedVelocity.HasValue)
            {
                Velocity = forcedVelocity.Value;
                LastFlags = Controller.Move(Velocity * dt);
                Grounded = (LastFlags & CollisionFlags.Below) != 0;
                FallCheck();
                return;
            }

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

            // ---- rope swing: pendulum on an inextensible rope
            if (_grappling) { Swing(dt, scale); FallCheck(); return; }

            // ---- walking
            Vector3 targetH = moveIntent * (sprint ? sprintSpeed : walkSpeed) * scale * speedMul;
            Vector3 h = new Vector3(Velocity.x, 0, Velocity.z);
            float control = Grounded || _floatLeft > 0 ? 12f : 12f * airControl;
            h = Vector3.MoveTowards(h, targetH, control * scale * dt);
            Velocity.x = h.x; Velocity.z = h.z;

            // ---- jetpack
            if (jetHeld && JetFuel > 0 && HasTool("Jetpack"))
            {
                JetFuel -= dt;
                Velocity.y = Mathf.MoveTowards(Velocity.y, 4.5f * scale, 25f * dt);
            }
            else if (_floatLeft > 0) { _floatLeft -= dt; Velocity.y = Mathf.MoveTowards(Velocity.y, 3.5f * scale, 20f * dt); }
            else Velocity.y += gravity * scale * dt;

            var flags = Controller.Move(Velocity * dt);
            LastFlags = flags;
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

            FallCheck();
        }

        void Swing(float dt, float scale)
        {
            if (reelIn) RopeLength = Mathf.Max(MinRope * scale, RopeLength - ReelSpeed * scale * dt);
            Velocity += moveIntent * SwingPump * scale * dt;          // pump the swing with the stick/WASD
            Velocity.y += gravity * scale * dt;
            var offset = Chest - _grappleTarget;
            float dist = offset.magnitude;
            if (dist > 0.01f && dist >= RopeLength)
            {
                var n = offset / dist;
                float outward = Vector3.Dot(Velocity, n);
                if (outward > 0) Velocity -= n * outward;                 // a taut rope doesn't stretch
                // and pulls you back to its length, but never faster than 8 m/s (no slingshot over the anchor)
                if (-Vector3.Dot(Velocity, n) < 8f * scale) Velocity -= n * (dist - RopeLength) * RopeSpring * dt;
            }
            Velocity = Vector3.ClampMagnitude(Velocity, 32f * scale);
            var flags = Controller.Move(Velocity * dt);
            Grounded = (flags & CollisionFlags.Below) != 0;
            if (Grounded && Velocity.y < 0) Velocity.y = 0;
            if ((flags & CollisionFlags.Above) != 0 && Velocity.y > 0) Velocity.y = 0;
            JetFuel = Mathf.Min(MaxJetFuel, JetFuel + dt);
        }

        // ---- Boing Goo: goo-coated voxels are trampolines
        float _gooReady;
        public static int GooBounces;   // -autotest counter

        void OnControllerColliderHit(ControllerColliderHit hit)
        {
            OnHit?.Invoke(hit);
            if (Time.time < _gooReady) return;
            var cube = VoxelCube.Instance;
            if (!cube || cube.GooCount == 0 || hit.collider.GetComponentInParent<VoxelChunk>() == null) return;
            var c = cube.WorldToCellInt(hit.point - hit.normal * cube.cellSize * 0.5f);
            if (!cube.Grid.InBounds(c.x, c.y, c.z) || cube.MarkOf(cube.Grid.Index(c.x, c.y, c.z)) != CellMark.Goo) return;
            var n = hit.normal;
            float into = Vector3.Dot(Velocity, n);
            var v = into < 0 ? Velocity - 2f * into * n : Velocity;   // reflect off the goo...
            float outSpeed = Vector3.Dot(v, n);
            float want = 11f * WorldScale;
            if (outSpeed < want) v += n * (want - outSpeed);       // ...with at least a trampoline's kick
            Velocity = v;
            Grounded = false;
            _gooReady = Time.time + 0.15f;
            GooBounces++;
        }

        /// <summary>A mode can pick where fallen players come back (Titan Siege: the current wall).</summary>
        public static System.Func<PlayerMotor, Vector3?> RespawnOverride;

        void FallCheck()
        {
            // fell off the arena (the meadow is 12 m down): respawn on the pedestal edge
            if (transform.position.y < -8f)
            {
                var over = RespawnOverride?.Invoke(this);
                if (over.HasValue) { Teleport(over.Value, transform.eulerAngles.y); return; }
                var cube = VoxelCube.Instance;
                var p = cube ? cube.transform.position + Vector3.back * (cube.HalfExtent + 2.5f) : Vector3.up;
                Teleport(p + Vector3.up, 0);
            }
        }
    }
}
