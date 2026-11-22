using Cubestruction.Player;
using Cubestruction.Tools;
using UnityEngine;

namespace Cubestruction.Siege
{
    /// <summary>
    /// A Titan Siege defender (owner only). Standing next to a turret loads it (the server counts the time);
    /// Use fires it instead of your RPG while you're there. Away from turrets you have your RPG
    /// (Bloop Launcher), the web-swing (Q / Swing / empty off-hand trigger) and a short jetpack.
    /// </summary>
    public class DefenderController : MonoBehaviour
    {
        PlayerAvatar _avatar;
        FlatRig _flat;
        VRRig _vr;
        bool _useWas, _blocking;
        public static int NearTurret = -1;
        public static int TurretShotsRequested;   // -autotest

        void OnEnable()
        {
            _avatar = GetComponent<PlayerAvatar>();
            _flat = GetComponent<FlatRig>();
            _vr = GetComponent<VRRig>();
        }

        void OnDisable()
        {
            if (_blocking) StreakState.BlocksTools = false;
            _blocking = false;
            NearTurret = -1;
        }

        void Update()
        {
            var d = TitanDirector.Instance;
            NearTurret = -1;
            if (d != null && d.IsActive)
            {
                int i = d.TurretNear(transform.position);
                if (i >= 0 && i / TitanDirector.Layout.Length == d.wall.Value) NearTurret = i;
            }
            bool near = NearTurret >= 0;
            if (near != _blocking) { _blocking = near; StreakState.BlocksTools = near; }   // Use goes to the turret

            bool use = _avatar.IsVR && _vr && _vr.enabled
                ? _vr.Right.trigger.IsPressed() || _vr.Left.trigger.IsPressed()
                : _flat && _flat.UseHeld;
            if (near && use && !_useWas)
            {
                if (d.ammo[NearTurret] > 0) { d.FireTurretRpc(NearTurret); TurretShotsRequested++; }
                else UI.GameMenus.Instance?.Toast("Empty! Stay next to it to load.", AeroPalette.Danger);
            }
            _useWas = use;
        }

        /// <summary>One line for the HUD about the turret you're standing at.</summary>
        public static string TurretHint()
        {
            var d = TitanDirector.Instance;
            if (d == null || NearTurret < 0 || NearTurret >= d.ammo.Count) return null;
            var k = d.KindOf(NearTurret);
            int a = d.ammo[NearTurret], cap = TitanDirector.Capacity(k);
            return a >= cap ? $"{TitanDirector.Name(k)} loaded {a}/{cap}: Use to fire!"
                 : a > 0 ? $"{TitanDirector.Name(k)} {a}/{cap} (loading...): Use to fire"
                 : $"{TitanDirector.Name(k)}: loading...";
        }
    }
}
