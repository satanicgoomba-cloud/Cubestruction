using System.Collections;
using Cubestruction.Net;
using Cubestruction.Player;
using Cubestruction.Voxels;
using UnityEngine;

namespace Cubestruction
{
    /// <summary>
    /// Developer smoke test, only active when the game is launched with <c>-autotest</c>.
    /// Hosts a LAN game, starts Mining, then checks that the starter pick mines voxels, that
    /// the arena floor is walkable and that falling off the world respawns the player.
    /// Results go to the player log as lines starting with "[AutoTest]"; the app quits at the end.
    /// </summary>
    [DefaultExecutionOrder(-40)]   // after FlatRig (-50), so our aim and trigger win; before ToolController
    public class AutoTest : MonoBehaviour
    {
        bool _holdUse;
        Quaternion _aim = Quaternion.identity;
        int _failures;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-autotest") < 0) return;
            DontDestroyOnLoad(new GameObject("AutoTest").AddComponent<AutoTest>().gameObject);
        }

        void Log(string msg) => Debug.Log("[AutoTest] " + msg);
        void Check(bool ok, string what) { if (!ok) _failures++; Log((ok ? "PASS " : "FAIL ") + what); }

        IEnumerator Start()
        {
            yield return new WaitForSeconds(1f);
            NetBootstrap.Instance.HostLan(7790);   // own port, so a normal game can run alongside
            yield return Until(() => MatchManager.Instance != null && MatchManager.Instance.IsSpawned, 10f, "match manager spawned");
            MatchManager.Instance.ServerStartMatch(GameMode.Mining);
            yield return Until(() => PlayerAvatar.Local != null && VoxelCube.Instance != null && VoxelCube.Instance.IsSpawned
                                     && MatchManager.Instance.state.Value == MatchState.Playing, 10f, "mining match started");
            yield return new WaitForSeconds(1.5f);

            var me = PlayerAvatar.Local;
            var cube = VoxelCube.Instance;

            // ---- mining: stand 1 m from the -Z face, look straight at it, hold Use
            int before = cube.alive.Value, cash0 = me.money.Value;
            var face = cube.Center + Vector3.back * cube.HalfExtent;
            me.Motor.Teleport(new Vector3(face.x, cube.transform.position.y + 0.2f, face.z - 1f), 0f);
            me.Tools.SelectHotbarSlot(0);
            _aim = Quaternion.identity;
            _holdUse = true;
            yield return new WaitForSeconds(4f);
            _holdUse = false;
            yield return new WaitForSeconds(0.5f);
            int mined = before - cube.alive.Value;
            Log($"voxels {before} -> {cube.alive.Value}, money {cash0} -> {me.money.Value}");
            Check(mined > 0, $"pick mined {mined} voxels in 4 s");
            Check(me.money.Value > cash0, "mining paid money");

            // ---- arena floor: stand on the arena ring, off the pedestal
            me.Motor.Teleport(new Vector3(0, 1f, -18f), 0f);
            yield return new WaitForSeconds(2f);
            float y = me.transform.position.y;
            Check(y > -1f, $"arena floor holds the player (y = {y:0.00})");

            // ---- falling off the world respawns on the pedestal edge
            me.Motor.Teleport(new Vector3(0, 1f, -40f), 0f);
            yield return new WaitForSeconds(4f);
            var p = me.transform.position;
            Check(p.y > -1f && Mathf.Abs(p.z) < 15f, $"fall respawns near the cube (pos = {p})");

            Log(_failures == 0 ? "ALL PASSED" : _failures + " FAILED");
            yield return new WaitForSeconds(0.5f);
            Application.Quit(_failures == 0 ? 0 : 1);
        }

        IEnumerator Until(System.Func<bool> cond, float timeout, string what)
        {
            float t = 0;
            while (!cond() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            Check(cond(), what);
            if (!cond()) { Log("aborting"); Application.Quit(2); yield break; }
        }

        void Update()
        {
            var me = PlayerAvatar.Local;
            if (me == null || me.Tools == null) return;
            if (_holdUse)
            {
                me.transform.rotation = _aim;
                me.headAnchor.localRotation = Quaternion.identity;
                me.Tools.SetTriggers(false, true);
            }
        }
    }
}
