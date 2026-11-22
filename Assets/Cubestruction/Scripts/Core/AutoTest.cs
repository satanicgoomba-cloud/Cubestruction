using System.Collections;
using Cubestruction.Net;
using Cubestruction.Player;
using Cubestruction.Voxels;
using UnityEngine;

namespace Cubestruction
{
    /// <summary>
    /// Developer smoke test, only active when the game is launched with <c>-autotest</c>.
    /// Hosts a LAN game and checks Mining (pick mines and pays, arena floor, fall respawn),
    /// the shop (buying, double-charge, consumables, can't-afford, bought tool mines) and a solo
    /// God Siege (unit purchase, units march, Smite, auto-wave, units damage the cube, cleanup).
    /// Results go to the player log as lines starting with "[AutoTest]"; the app quits at the end.
    /// </summary>
    [DefaultExecutionOrder(-40)]   // after FlatRig (-50), so our aim and trigger win; before ToolController
    public class AutoTest : MonoBehaviour
    {
        bool _holdUse, _pulseUse;
        float _pitch;
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

            // ---- shop (host writes money directly to set up each case)
            int spade = Balance.Tool("Shovel").index, grenade = Balance.Tool("Grenade").index, sledge = Balance.Tool("SledgeHammer").index;
            me.money.Value = 1000;
            me.BuyRpc(spade);
            yield return new WaitForSeconds(0.3f);
            Check(me.Owns(spade) && me.money.Value == 880, $"bought Aqua Spade for 120 (money {me.money.Value})");
            me.BuyRpc(spade);
            yield return new WaitForSeconds(0.3f);
            Check(me.money.Value == 880, "an owned tool isn't charged twice");
            int g0 = me.charges[grenade];
            me.BuyRpc(grenade);
            yield return new WaitForSeconds(0.3f);
            Check(me.charges[grenade] == g0 + 5 && me.money.Value == 680, $"bought 5 Fizz Grenades for 200 (charges {me.charges[grenade]}, money {me.money.Value})");
            me.money.Value = 10;
            me.BuyRpc(sledge);
            yield return new WaitForSeconds(0.3f);
            Check(!me.Owns(sledge) && me.money.Value == 10, "can't buy a tool you can't afford");

            // the bought spade mines (fresh -X face, facing +X)
            me.Tools.SelectTool(spade);
            before = cube.alive.Value;
            var side = cube.Center + Vector3.left * cube.HalfExtent;
            me.Motor.Teleport(new Vector3(side.x - 1f, cube.transform.position.y + 0.2f, side.z), 90f);
            _aim = Quaternion.Euler(0, 90, 0);
            _holdUse = true;
            yield return new WaitForSeconds(3f);
            _holdUse = false;
            yield return new WaitForSeconds(0.5f);
            Check(before - cube.alive.Value > 0, $"Aqua Spade mined {before - cube.alive.Value} voxels in 3 s");

            // ---- mobile UI (-mobileui): hotbar slots must be on top of the touch zones, and a tap switches weapon
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-mobileui") >= 0)
            {
                yield return new WaitForSeconds(0.5f);   // hotbar refreshes after the purchases above
                var orbs = new System.Collections.Generic.List<UnityEngine.UI.Button>();
                foreach (var b in FindObjectsByType<UnityEngine.UI.Button>())
                    if (b.name == "Orb" && b.transform.parent.name.StartsWith("Slot") && b.gameObject.activeInHierarchy) orbs.Add(b);
                orbs.Sort((a, b) => a.transform.position.x.CompareTo(b.transform.position.x));
                Check(orbs.Count >= 2, $"mobile hotbar shows {orbs.Count} slots");
                int blocked = 0;
                foreach (var orb in orbs)
                {
                    var ped = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
                        { position = RectTransformUtility.WorldToScreenPoint(null, orb.transform.position) };
                    var hits = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                    UnityEngine.EventSystems.EventSystem.current.RaycastAll(ped, hits);
                    if (hits.Count == 0 || !hits[0].gameObject.transform.IsChildOf(orb.transform)) { blocked++; Log($"slot under {(hits.Count > 0 ? hits[0].gameObject.name : "nothing")}"); }
                }
                Check(blocked == 0, $"every hotbar slot is tappable ({orbs.Count - blocked}/{orbs.Count})");
                if (orbs.Count >= 2)
                {
                    int before0 = me.Tools.rightTool.Value;
                    var target = orbs[orbs.Count - 1];
                    var ped = new UnityEngine.EventSystems.PointerEventData(UnityEngine.EventSystems.EventSystem.current)
                        { position = RectTransformUtility.WorldToScreenPoint(null, target.transform.position) };
                    var hits = new System.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
                    UnityEngine.EventSystems.EventSystem.current.RaycastAll(ped, hits);
                    if (hits.Count > 0) UnityEngine.EventSystems.ExecuteEvents.ExecuteHierarchy(hits[0].gameObject, ped, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                    yield return new WaitForSeconds(0.3f);
                    Check(me.Tools.rightTool.Value != before0, $"tapping a hotbar slot switches weapon ({Balance.Tool(before0)?.name} -> {Balance.Tool(me.Tools.rightTool.Value)?.name})");
                }
            }

            // ---- physics guns: each must fire bullets that hit, ricochet and dig (fired from 3 m off the +Z face)
            var front = cube.Center + Vector3.forward * cube.HalfExtent;
            foreach (var (gunId, seconds, pulse) in new[] { ("Minigun", 3f, false), ("PinballPopper", 3f, false), ("Shotgun", 3f, true), ("RailGun", 3f, true) })
            {
                var gun = Balance.Tool(gunId);
                me.ownedTools.Value |= 1UL << gun.index;
                me.Tools.SelectTool(gun.index);
                me.Motor.Teleport(new Vector3(front.x, cube.transform.position.y + 0.2f, front.z + 3f), 180f);
                _aim = Quaternion.Euler(0, 180, 0);
                _pitch = gunId == "RailGun" ? 0f : -8f;   // lob slightly up so ricochets have room
                yield return new WaitForSeconds(0.3f);
                int v0 = cube.alive.Value, f0 = Tools.Bullets.Fired, i0 = Tools.Bullets.Impacts, b0 = Tools.Bullets.Bounces;
                _holdUse = true; _pulseUse = pulse;
                yield return new WaitForSeconds(seconds);
                _holdUse = false; _pulseUse = false;
                yield return new WaitForSeconds(1.5f);   // let bouncing bullets finish
                int fired = Tools.Bullets.Fired - f0, hits = Tools.Bullets.Impacts - i0, bounces = Tools.Bullets.Bounces - b0, dug = v0 - cube.alive.Value;
                Log($"{gun.name}: {fired} bullets, {hits} impacts, {bounces} ricochets, {dug} voxels in {seconds:0} s");
                Check(fired > 0 && hits > 0 && dug > 0, $"{gun.name} fires physics bullets that dig");
                if (gun.bounces > 0) Check(bounces > 0, $"{gun.name} bullets ricochet");
            }
            _pitch = 0f;

            // ---- God Siege, solo: the one player becomes the God
            var mm = MatchManager.Instance;
            var sd = Siege.SiegeDirector.Instance;
            mm.ServerEndMatch("autotest");
            yield return new WaitForSeconds(1f);
            mm.ServerStartMatch(GameMode.GodSiege);
            bool started = false;
            yield return Wait(() => mm.state.Value == MatchState.Playing && sd.IsActive && VoxelCube.Instance != null && VoxelCube.Instance.IsSpawned,
                              10f, ok => started = ok);
            Check(started, "God Siege match started");
            if (started)
            {
                cube = VoxelCube.Instance;
                Check(me.role.Value == PlayerRole.God, $"solo player is the God (role {me.role.Value})");

                // Commander buys a unit (switch role for the test; RPCs check the sender's role)
                me.role.Value = PlayerRole.Commander;
                float purse = sd.money.Value;
                sd.BuyUnitRpc(0, 0);
                yield return new WaitForSeconds(0.5f);
                Check(Siege.EnemyUnit.Live.Count >= 1 && sd.money.Value < purse, $"Commander bought a unit (units {Siege.EnemyUnit.Live.Count}, purse {purse:0} -> {sd.money.Value:0})");

                if (Siege.EnemyUnit.Live.Count > 0)
                {
                    var u = Siege.EnemyUnit.Live[0];
                    float Flat(Vector3 a) => Vector3.ProjectOnPlane(a - cube.Center, Vector3.up).magnitude;
                    float d0 = Flat(u.transform.position);
                    yield return new WaitForSeconds(4f);
                    float d1 = u != null && u.IsSpawned ? Flat(u.transform.position) : 0f;
                    Check(d1 < d0 - 1f, $"unit marches on the cube ({d0:0.0} m -> {d1:0.0} m)");

                    // God smites it
                    me.role.Value = PlayerRole.God;
                    float mana0 = sd.mana.Value, hp0 = u != null && u.IsSpawned ? u.hp.Value : 0f;
                    if (u != null && u.IsSpawned) sd.CastSkillRpc(sd.Skill("Smite").index, u.transform.position);
                    yield return new WaitForSeconds(0.5f);
                    bool hurt = u == null || !u.IsSpawned || u.hp.Value < hp0;
                    Check(hurt && sd.mana.Value < mana0, $"Smite hit the unit (hp {hp0:0} -> {(u != null && u.IsSpawned ? u.hp.Value : 0):0}, mana {mana0:0} -> {sd.mana.Value:0})");
                }
                me.role.Value = PlayerRole.God;

                // a free auto-wave arrives (every 40 s)
                int w0 = sd.wave.Value;
                bool waved = false;
                yield return Wait(() => sd.wave.Value > w0, 45f, ok => waved = ok);
                Check(waved && Siege.EnemyUnit.Live.Count > 0, $"auto-wave {sd.wave.Value} spawned ({Siege.EnemyUnit.Live.Count} units live)");

                // units reach and damage the cube
                int alive0 = cube.alive.Value;
                bool damaged = false;
                yield return Wait(() => cube.alive.Value < alive0, 40f, ok => damaged = ok);
                Check(damaged, $"units damaged the cube ({alive0} -> {cube.alive.Value} voxels)");

                mm.ServerEndMatch("autotest");
                yield return new WaitForSeconds(1f);
                Check(!sd.IsActive && Siege.EnemyUnit.Live.Count == 0, "ending Siege despawns all units");
            }

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

        /// <summary>Non-fatal wait: reports whether the condition came true within the timeout.</summary>
        static IEnumerator Wait(System.Func<bool> cond, float timeout, System.Action<bool> done)
        {
            float t = 0;
            while (!cond() && t < timeout) { t += Time.unscaledDeltaTime; yield return null; }
            done(cond());
        }

        void Update()
        {
            var me = PlayerAvatar.Local;
            if (me == null || me.Tools == null) return;
            if (_holdUse)
            {
                me.transform.rotation = _aim;
                me.headAnchor.localRotation = Quaternion.Euler(_pitch, 0, 0);
                // pulse = tap the trigger (semi-auto guns fire on each press)
                me.Tools.SetTriggers(false, !_pulseUse || Mathf.Repeat(Time.time, 0.25f) < 0.12f);
            }
        }
    }
}
