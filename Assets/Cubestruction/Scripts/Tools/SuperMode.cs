using Cubestruction.Player;
using Cubestruction.UI;
using Cubestruction.Voxels;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Cubestruction.Tools
{
    /// <summary>
    /// The owner's side of the personal super modes (the server validates every hit):
    ///   Aqua Surge   hold Use (VR: both triggers, hands together) to charge, release to fire a
    ///                steerable beam for 2.5 s that tunnels through the cube and pushes you back.
    ///   Meteor Dive  shoots you ~28 m up, hover and aim (up to 3 s), Use dives; crater + shockwave.
    ///   Sun Orb      a sun grows over your head for 8 s (faster when anyone breaks voxels); Use
    ///                (or a real throw in VR) sends it; bigger orb, bigger crater.
    ///   Titan Buddy  5x tall for 30 s: Use punches (VR: real punches), landing from a jump stomps.
    /// Echo Clones need no owner view: the server repeats your actions (see ToolController).
    /// </summary>
    public class SuperMode : MonoBehaviour
    {
        public static SuperMode Active;
        public string Kind { get; private set; }

        ToolController _tc;
        PlayerAvatar _av;
        PlayerMotor _m;
        FlatRig _flat;
        VRRig _vrRig;
        bool _vr, _prevUse;
        int _phase;
        float _t, _charge, _tickAt, _cdL, _cdR, _scale0, _fallSpeed;
        Vector3 _target;
        GameObject _orb, _marker;
        InputAction _trigL, _trigR;

        /// <summary>-autotest hooks.</summary>
        [System.NonSerialized] public bool autoUse;
        [System.NonSerialized] public Vector3? autoAim;
        [System.NonSerialized] public float sunChargeS = 8f;

        public static void Begin(ToolController tc, string kind)
        {
            if (Active != null) return;
            tc.gameObject.AddComponent<SuperMode>().Init(tc, kind);
        }

        void Init(ToolController tc, string kind)
        {
            Active = this;
            Kind = kind;
            _tc = tc;
            _av = GetComponent<PlayerAvatar>();
            _m = GetComponent<PlayerMotor>();
            _flat = GetComponent<FlatRig>();
            _vrRig = GetComponent<VRRig>();
            _vr = _av.IsVR;
            _trigL = new InputAction(type: InputActionType.Button, binding: "<XRController>{LeftHand}/{TriggerButton}"); _trigL.Enable();
            _trigR = new InputAction(type: InputActionType.Button, binding: "<XRController>{RightHand}/{TriggerButton}"); _trigR.Enable();
            StreakState.BlocksTools = true;
            if (_m.IsGrappling) _m.StopGrapple();
            if (Kind == "TitanBuddy")
            {
                _scale0 = _m.WorldScale;
                _m.Teleport(transform.position + Vector3.up * 0.5f, transform.eulerAngles.y);
            }
            if (Kind == "MeteorDive") _target = transform.position;
        }

        // ------------------------------------------------------------------ input helpers

        bool UseHeld
        {
            get
            {
                if (autoUse) return true;
                if (_vr) return _trigR.IsPressed() || _trigL.IsPressed();
                return _flat != null && _flat.UseHeld;
            }
        }

        Ray Aim
        {
            get
            {
                Ray r;
                if (_vr)
                {
                    var h = GameSettings.LeftHanded ? _av.leftHand : _av.rightHand;
                    r = new Ray(h.position, h.forward);
                }
                else if (_flat != null && _flat.Cam != null) r = _flat.AimRay;
                else r = new Ray(_av.headAnchor.position, _av.headAnchor.forward);
                if (autoAim.HasValue) r = new Ray(r.origin, (autoAim.Value - r.origin).normalized);
                return r;
            }
        }

        static bool WorldHit(Ray ray, float max, out Vector3 point)
        {
            point = default;
            float best = float.MaxValue;
            var cube = VoxelCube.Instance;
            if (cube && cube.Raycast(ray, max, out _, out var vp)) { point = vp; best = Vector3.Distance(ray.origin, vp); }
            if (Physics.Raycast(ray, out var h, max, ~(1 << 5), QueryTriggerInteraction.Ignore) && h.distance < best &&
                h.collider.GetComponentInParent<VoxelChunk>() == null && h.collider.GetComponentInParent<PlayerAvatar>() == null)
            { point = h.point; best = h.distance; }
            return best < float.MaxValue;
        }

        Transform Marker(Color c)
        {
            if (!_marker)
            {
                _marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                Destroy(_marker.GetComponent<Collider>());
                _marker.GetComponent<MeshRenderer>().material = new Material(FxHub.Instance.glowMaterial) { color = c };
                _marker.transform.localScale = new Vector3(3f, 0.02f, 3f);
            }
            return _marker.transform;
        }

        void Banner(string s) => GameMenus.Instance?.SetBanner(s);

        // ------------------------------------------------------------------ per-mode

        void Update()
        {
            if (_av == null || !_av.IsSpawned) { End(); return; }
            _t += Time.deltaTime;
            bool use = UseHeld, pressed = use && !_prevUse, released = !use && _prevUse;
            _prevUse = use;
            switch (Kind)
            {
                case "AquaSurge": Surge(use, released); break;
                case "MeteorDive": Meteor(pressed); break;
                case "SunOrb": Sun(pressed, released); break;
                case "TitanBuddy": Titan(use); break;
                default: End(); break;
            }
        }

        Vector3 ChargePoint()
        {
            if (_vr) return (_av.leftHand.position + _av.rightHand.position) * 0.5f;
            var a = Aim;
            return a.origin + a.direction * 0.7f + Vector3.down * 0.15f;
        }

        void Surge(bool use, bool released)
        {
            if (_phase == 0)
            {
                bool charging = use && (!_vr || autoUse || Vector3.Distance(_av.leftHand.position, _av.rightHand.position) < 0.45f * _m.WorldScale);
                if (charging) _charge = Mathf.Min(1f, _charge + Time.deltaTime / 2f);
                if (!_orb)
                {
                    _orb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    Destroy(_orb.GetComponent<Collider>());
                    _orb.GetComponent<MeshRenderer>().material = new Material(FxHub.Instance.glowMaterial) { color = new Color(0.45f, 0.9f, 1f, 0.85f) };
                }
                _orb.transform.position = ChargePoint();
                _orb.transform.localScale = Vector3.one * (0.08f + 0.3f * _charge) * _m.WorldScale * (1f + 0.08f * Mathf.Sin(Time.time * 30f));
                Banner(_charge <= 0 ? (_vr ? "AQUA SURGE  ·  bring your hands together and hold both triggers" : "AQUA SURGE  ·  hold Use to charge")
                                    : $"AQUA SURGE  ·  charging {Mathf.RoundToInt(_charge * 100)}%  ·  release to fire");
                if ((released || (!use && _charge >= 1f)) && _charge >= 0.15f) { _phase = 1; _t = 0; }
                if (_t > 24f) End();
                return;
            }
            // firing: steerable for 2.5 s
            var aim = Aim;
            var origin = ChargePoint();
            var dir = aim.direction;
            if (_orb) _orb.transform.position = origin;
            if (Time.time >= _tickAt)
            {
                _tickAt = Time.time + 0.1f;
                _tc.SurgeTickRpc(origin, dir, _charge);
                _m.AddImpulse(-dir * 0.8f * (1f + _charge) * _m.WorldScale);
            }
            Banner("AQUA SURGE  ·  HAAAAA!");
            if (_t > 2.5f) { _tc.SuperEndRpc(); End(); }
        }

        void Meteor(bool pressed)
        {
            float s = _m.WorldScale;
            if (_phase == 0)
            {
                // blast off
                _m.forcedVelocity = Vector3.up * 26f * s;
                Banner("METEOR DIVE  ·  up you go!");
                if (_t > 1.1f) { _phase = 1; _t = 0; }
                return;
            }
            if (_phase == 1)
            {
                // hover and aim
                _m.forcedVelocity = Vector3.zero;
                var m = Marker(new Color(1f, 0.35f, 0.2f, 0.8f));
                bool hit = WorldHit(Aim, 150f, out var p);
                m.gameObject.SetActive(hit);
                if (hit) { m.position = p + Vector3.up * 0.05f; _target = p; }
                Banner($"METEOR DIVE  ·  aim the marker  ·  {(_vr ? "trigger" : "Use")} to dive  ·  {Mathf.Max(0, 3f - _t):0.0}s");
                if ((pressed && _t > 0.2f) || _t > 3f) { _phase = 2; _t = 0; if (!hit) _target = transform.position + Vector3.down * 30f; }
                return;
            }
            // dive
            var to = _target - transform.position;
            _m.forcedVelocity = to.normalized * 45f * s;
            Banner("METEOR DIVE");
            bool blocked = _t > 0.1f && _m.LastFlags != CollisionFlags.None;
            if (blocked || to.magnitude < 1.2f * s || _t > 3f)
            {
                _tc.MeteorImpactRpc(transform.position);
                _m.forcedVelocity = null;
                _m.Velocity = Vector3.up * 5f * s;
                End();
            }
        }

        void Sun(bool pressed, bool released)
        {
            float size = _tc.sunOrbSize.Value;
            Banner($"SUN ORB  ·  gathering energy {Mathf.RoundToInt(size * 100)}%  ·  {(_t >= 2f ? (_vr ? "throw it!" : "Use to throw") : "everyone, break voxels!")}");
            bool throwNow = _t >= sunChargeS || (_t >= 2f && (_vr ? released : pressed));
            if (!throwNow) { if (_t > 19f) End(); return; }
            var origin = transform.position + Vector3.up * (2.2f + size * 1.5f) * _m.WorldScale;
            Vector3 dir;
            var handVel = _vrRig != null ? (GameSettings.LeftHanded ? _vrRig.Left.velocity : _vrRig.Right.velocity) : Vector3.zero;
            if (_vr && handVel.magnitude > 1.5f && !autoAim.HasValue) dir = handVel.normalized;     // a real throw
            else dir = WorldHit(Aim, 150f, out var p) ? (p - origin).normalized : Aim.direction;
            _tc.SunOrbThrowRpc(origin, dir);
            End();
        }

        void Titan(bool use)
        {
            const float Big = 5f;
            float grow = Mathf.Clamp01(_t / 0.6f), shrink = Mathf.Clamp01((_t - 30f) / 0.6f);
            _m.WorldScale = Mathf.Lerp(_scale0, Big, grow * (1f - shrink));
            if (_t > 30.6f) { _m.WorldScale = _scale0; _tc.SuperEndRpc(); End(); return; }
            Banner($"TITAN BUDDY  ·  {Mathf.CeilToInt(30f - _t)}s  ·  {(_vr ? "punch things!" : "Use to punch")}  ·  jump and land to stomp");
            _cdL -= Time.deltaTime; _cdR -= Time.deltaTime;
            float s = _m.WorldScale;

            if (_vr && _vrRig != null)
            {
                // real punches: a fast fist next to the cube
                Punch(_vrRig.Left, ref _cdL, s);
                Punch(_vrRig.Right, ref _cdR, s);
            }
            else if (use && _cdR <= 0 && WorldHit(Aim, 2.2f * s, out var p))
            {
                _cdR = 0.3f;
                _tc.TitanPunchRpc(p);
            }

            // stomp: landing after a real fall
            if (!_m.Grounded) _fallSpeed = Mathf.Min(_fallSpeed, _m.Velocity.y);
            else
            {
                if (_fallSpeed < -6f * Mathf.Sqrt(s)) _tc.TitanStompRpc(transform.position);
                _fallSpeed = 0;
            }
        }

        void Punch(VRRig.HandState h, ref float cd, float s)
        {
            if (cd > 0 || h.velocity.magnitude < 2.5f * s) return;
            var cube = VoxelCube.Instance;
            if (cube && cube.SolidNear(h.t.position, 0.5f * s, out var at)) { cd = 0.3f; _tc.TitanPunchRpc(at); }
        }

        // ------------------------------------------------------------------ end

        public void End()
        {
            if (Active != this) return;
            Active = null;
            StreakState.BlocksTools = false;
            GameMenus.Instance?.SetBanner(null);
            if (_m) { _m.forcedVelocity = null; if (Kind == "TitanBuddy") _m.WorldScale = _scale0 > 0 ? _scale0 : 1f; }
            if (_orb) Destroy(_orb);
            if (_marker) Destroy(_marker);
            _trigL?.Disable(); _trigR?.Disable();
            Destroy(this);
        }

        void OnDestroy() { if (Active == this) { Active = null; StreakState.BlocksTools = false; } }
    }
}
