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
        bool _swing, _reel; Ray _swingAim; Vector3 _pump;
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

        /// <summary>A screenshot from an explicit spot (outside views of giants).</summary>
        static void ShotFrom(string name, Vector3 pos, Vector3 look)
        {
            var go = new GameObject("ShotCam");
            var cam = go.AddComponent<Camera>();
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(look - pos));
            cam.fieldOfView = 60f; cam.enabled = false;
            Shot(name, cam);
            Object.Destroy(go);
        }

        /// <summary>Saves a screenshot when launched with -shotdir &lt;folder&gt; (to eyeball visuals).</summary>
        static void Shot(string name, Camera useCam = null)
        {
            var args = System.Environment.GetCommandLineArgs();
            int i = System.Array.IndexOf(args, "-shotdir");
            if (i < 0 || i + 1 >= args.Length) return;
            // render the main camera into a texture (the screen-capture module isn't in this build)
            var cam = useCam ? useCam : Camera.main; if (!cam) return;
            var rt = new RenderTexture(960, 540, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = prev;
            var was = RenderTexture.active; RenderTexture.active = rt;
            var tex = new Texture2D(960, 540, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 960, 540), 0, 0); tex.Apply();
            RenderTexture.active = was;
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(args[i + 1], name + ".png"), tex.EncodeToPNG());
            Object.Destroy(tex); rt.Release(); Object.Destroy(rt);
        }

        /// <summary>Point the local camera at a world point (yaw on the body, pitch on the head).</summary>
        static void LookAt(PlayerAvatar me, Vector3 p)
        {
            var d = p - me.headAnchor.position;
            me.transform.rotation = Quaternion.Euler(0, Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 0);
            me.headAnchor.localRotation = Quaternion.Euler(-Mathf.Atan2(d.y, new Vector2(d.x, d.z).magnitude) * Mathf.Rad2Deg, 0, 0);
        }
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

            // ---- floating islands: cut a 3x3x3 block off the top corner; it must collapse, and the
            // local island search must agree with a full flood fill the whole run
            VoxelCube.VerifyIslands = true;
            {
                int n = cube.Grid.N, top = n - 3;
                for (int x = 0; x <= 3; x++) for (int z = 0; z <= 3; z++) for (int cy = top - 1; cy < n; cy++)
                {
                    bool cut = x == 3 || z == 3 || cy == top - 1;   // the walls and floor around the block
                    if (cut) cube.ServerDamage(cube.CellToWorld(new Vector3Int(x, cy, z)), 0.4f, 1e6f, 9, false, me.OwnerClientId);
                }
                yield return new WaitForSeconds(1.5f);
                int left = 0;
                for (int x = 0; x < 3; x++) for (int cy = top; cy < n; cy++) for (int z = 0; z < 3; z++) if (cube.Grid.IsSolid(x, cy, z)) left++;
                Check(left == 0, $"a cut-off corner block collapses ({27 - left}/27 cells fell)");
            }

            // ---- rope swing: the web goes up to the invisible sky ceiling, a bit ahead of you
            {
                var tether = Balance.Tool("GrappleHook");
                me.ownedTools.Value |= 1UL << tether.index;
                var xFace = cube.Center + Vector3.right * cube.HalfExtent;
                float baseY = cube.transform.position.y;
                me.Motor.Teleport(new Vector3(xFace.x + 5f, baseY + 0.2f, xFace.z), 90f);
                yield return new WaitForSeconds(0.5f);
                float y0 = me.transform.position.y;
                int sky0 = Tools.ToolController.SkyWebs, snap0 = Tools.ToolController.WebsSnapped;
                var eye = me.headAnchor.position;
                _swingAim = new Ray(eye, Vector3.right);   // looking out over the meadow; the web goes up and ahead
                _swing = true;
                yield return new WaitForSeconds(0.3f);
                Check(me.Motor.IsGrappling && Tools.ToolController.SkyWebs > sky0, "Zip Tether webs onto the sky ceiling");
                var anchor = me.Motor.RopeAnchor;
                float ceil = Tools.ToolController.SkyCeilingY;
                Check(Mathf.Abs(anchor.y - ceil) < 0.05f && ceil > baseY + cube.Grid.N * cube.cellSize + 4f,
                      $"sky anchor sits 20 voxels above the cube (anchor y {anchor.y - baseY:0.0} m, cube top {cube.Grid.N * cube.cellSize:0.0} m)");
                float ahead = Vector3.ProjectOnPlane(anchor - me.transform.position, Vector3.up).magnitude;
                Check(ahead > 1f && ahead < 8f, $"anchor is a bit in front, nearly overhead ({ahead:0.0} m ahead)");
                yield return new WaitForSeconds(0.9f);
                float lifted = me.transform.position.y - y0;
                Check(lifted > 1.5f, $"the rope lifts you off the ground ({lifted:0.0} m)");
                float len0 = me.Motor.RopeLength;
                _reel = true;
                yield return new WaitForSeconds(0.5f);
                _reel = false;
                Check(me.Motor.RopeLength < len0 - 3f, $"jump reels the rope in ({len0:0.0} -> {me.Motor.RopeLength:0.0} m)");
                _pump = Vector3.forward;
                float best = 0;
                for (float tt = 0; tt < 0.9f; tt += Time.deltaTime) { var v = me.Motor.Velocity; best = Mathf.Max(best, new Vector2(v.x, v.z).magnitude); yield return null; }
                Check(best > 3f, $"pumping builds a swing ({best:0.0} m/s)");
                // keep holding: the web snaps at 3 s and no new one comes without a fresh press
                yield return new WaitForSeconds(0.8f);
                _pump = Vector3.zero;
                Check(Tools.ToolController.WebsSnapped > snap0 && !me.Motor.IsGrappling, "a web snaps after 3 s");
                yield return new WaitForSeconds(0.4f);
                Check(!me.Motor.IsGrappling, "holding on doesn't re-web (no hanging in the sky)");
                _swing = false;
                yield return null; yield return null;
                _swing = true;   // fresh press: a new web, letting go flings you
                yield return new WaitForSeconds(0.6f);
                bool rewebbed = me.Motor.IsGrappling;
                _swing = false;
                yield return null; yield return null;
                var fling = me.Motor.Velocity;
                Check(rewebbed && !me.Motor.IsGrappling && fling.magnitude > 2f, $"a fresh press webs again; letting go flings you ({fling.magnitude:0.0} m/s)");
                yield return new WaitForSeconds(3f);
            }

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
            foreach (var (gunId, seconds, pulse) in new[] { ("Minigun", 3f, false), ("PinballPopper", 3f, false), ("Shotgun", 3f, true), ("RailGun", 3f, true), ("RippleDisc", 3f, true), ("BloomMortar", 3.5f, true), ("FireworkLauncher", 3f, true), ("DrillTorpedo", 4.5f, true) })
            {
                var gun = Balance.Tool(gunId);
                me.ownedTools.Value |= 1UL << gun.index;
                me.Tools.SelectTool(gun.index);
                me.Motor.Teleport(new Vector3(front.x, cube.transform.position.y + 0.2f, front.z + 3f), 180f);
                _aim = Quaternion.Euler(0, 180, 0);
                _pitch = gunId == "RailGun" ? 0f : gunId == "BloomMortar" ? -25f : -8f;   // lob up a little; the mortar arcs
                yield return new WaitForSeconds(0.3f);
                int v0 = cube.alive.Value, f0 = Tools.Bullets.Fired, i0 = Tools.Bullets.Impacts, b0 = Tools.Bullets.Bounces;
                _holdUse = true; _pulseUse = pulse;
                float worst = 0, total = 0; int frames = 0;
                for (float tt = 0; tt < seconds; tt += Time.unscaledDeltaTime) { worst = Mathf.Max(worst, Time.unscaledDeltaTime); total += Time.unscaledDeltaTime; frames++; yield return null; }
                Log($"{gun.name} frame time: avg {1000f * total / Mathf.Max(1, frames):0.0} ms, worst {1000f * worst:0.0} ms, island check {VoxelCube.LastIslandCheckMs:0.0} ms, {Tools.FxHub.Instance?.LiveBullets} bullets live");
                _holdUse = false; _pulseUse = false;
                yield return new WaitForSeconds(1.5f);   // let bouncing bullets finish
                int fired = Tools.Bullets.Fired - f0, hits = Tools.Bullets.Impacts - i0, bounces = Tools.Bullets.Bounces - b0, dug = v0 - cube.alive.Value;
                Log($"{gun.name}: {fired} bullets, {hits} impacts, {bounces} ricochets, {dug} voxels in {seconds:0} s");
                Check(fired > 0 && hits > 0 && dug > 0, $"{gun.name} fires physics bullets that dig");
                if (gun.bounces > 0) Check(bounces > 0, $"{gun.name} bullets ricochet");
            }
            _pitch = 0f;

            // ---- Batch 1 specials, fired at the untouched -Z face from 3 m (the +Z firing line is bored hollow by now)
            var back = cube.Center + Vector3.back * cube.HalfExtent;
            System.Collections.IEnumerator FireAt(string id, float seconds, bool pulse, float pitch, float sweepDeg = 0f)
            {
                var w = Balance.Tool(id);
                me.ownedTools.Value |= 1UL << w.index;
                me.Tools.SelectTool(w.index);
                me.Motor.Teleport(new Vector3(back.x, cube.transform.position.y + 0.2f, back.z - 3f), 0f);
                _aim = Quaternion.identity; _pitch = pitch;
                yield return new WaitForSeconds(0.3f);
                _holdUse = true; _pulseUse = pulse;
                for (float tt = 0; tt < seconds; tt += Time.deltaTime)
                {
                    _aim = Quaternion.Euler(0, Mathf.Sin(tt * 3f) * sweepDeg, 0);   // sweep like a player would
                    yield return null;
                }
                _holdUse = false; _pulseUse = false; _pitch = 0f; _aim = Quaternion.identity;
                yield return new WaitForSeconds(0.8f);
            }
            {
                int v0 = cube.alive.Value, b0 = Tools.FxHub.LightningBolts;
                yield return FireAt("ChainZapper", 2f, false, -20f);
                int bolts = Tools.FxHub.LightningBolts - b0, dug = v0 - cube.alive.Value;
                Check(bolts > 0 && dug >= bolts * 3, $"Chain Zapper arcs across voxels ({bolts} bolts, {dug} voxels)");
            }
            {
                int glass0 = cube.GlassCount;
                yield return FireAt("FrostRay", 1.5f, false, -35f, 25f);
                int glass = cube.GlassCount - glass0;
                bool brittle = true;
                foreach (var gi in cube.CellsMarked(CellMark.Glass)) { if (cube.Grid.hp[gi] > 0.05f) brittle = false; break; }
                Check(glass > 20 && brittle, $"Frost Ray turns voxels to one-hit glass ({glass} frozen)");
            }
            {
                int goo0 = cube.GooCount;
                yield return FireAt("GooGun", 1.5f, false, -45f, 25f);
                Check(cube.GooCount - goo0 > 20, $"Boing Goo coats the cube ({cube.GooCount - goo0} voxels)");
                // drop onto a goo patch on top of the cube: it must bounce you
                int n = cube.Grid.N;
                cube.ServerGoo(new Vector3Int(n / 2, n - 1, n / 2), 3f);
                yield return null;
                int bounces0 = PlayerMotor.GooBounces;
                me.Motor.Teleport(cube.CellToWorld(new Vector3Int(n / 2, n - 1, n / 2)) + Vector3.up * 2.5f, 0f);
                float vy = 0;
                for (float tt = 0; tt < 1.5f; tt += Time.deltaTime) { vy = Mathf.Max(vy, me.Motor.Velocity.y); yield return null; }
                Check(PlayerMotor.GooBounces > bounces0 && vy > 8f, $"landing on goo bounces you ({vy:0.0} m/s up)");
                yield return new WaitForSeconds(2f);
            }

            // ---- Batch 2: physical / movement weapons at the -Z face
            {
                int w0 = Tools.Bullets.Warps;
                var from = new Vector3(back.x, cube.transform.position.y + 0.2f, back.z - 3f);
                System.Collections.IEnumerator Tap()
                {
                    _holdUse = true; _pulseUse = false;
                    yield return new WaitForSeconds(0.1f);
                    _holdUse = false; me.Tools.SetTriggers(false, false);
                    yield return new WaitForSeconds(0.05f);
                }
                yield return FireAt("WarpKunai", 0.1f, false, -35f);
                Check(Tools.Bullets.Warps == w0, "a stuck kunai doesn't warp you by itself");
                _pitch = -35f;
                yield return Tap();   // press again: blink to it
                _pitch = 0f;
                yield return new WaitForSeconds(0.3f);
                float moved = Vector3.Distance(Tools.Bullets.LastWarp, from);
                Check(Tools.Bullets.Warps > w0 && moved > 2f, $"pressing again warps you to the stuck kunai ({moved:0.0} m)");
                // mid-flight: throw it along the face into open air, press again before it lands
                me.Motor.Teleport(new Vector3(back.x, cube.transform.position.y + 0.2f, back.z - 3f), 90f);
                _aim = Quaternion.Euler(0, 90f, 0); _pitch = -20f;
                yield return new WaitForSeconds(0.8f);   // kunai cooldown
                int w1 = Tools.Bullets.Warps;
                var start = me.transform.position;
                yield return Tap();
                yield return new WaitForSeconds(0.3f);
                yield return Tap();
                _pitch = 0f; _aim = Quaternion.identity;
                yield return new WaitForSeconds(0.3f);
                float air = Vector3.Distance(Tools.Bullets.LastWarp, start);
                Check(Tools.Bullets.Warps > w1 && air > 5f, $"pressing again mid-flight warps you to the kunai ({air:0.0} m)");
            }
            {
                int v0 = cube.alive.Value;
                yield return FireAt("ChainWhip", 1.3f, true, -15f);
                Check(v0 - cube.alive.Value > 5, $"Chain Whip rips chunks out ({v0 - cube.alive.Value} voxels)");
            }
            {
                // muzzle: shots leave the end of the barrel, not the camera
                yield return FireAt("Shotgun", 0.15f, true, -10f);
                var camPos = me.headAnchor.position;
                var mz = Tools.ToolController.LastMuzzle;
                var local = me.headAnchor.InverseTransformPoint(mz);
                Check(local.z > 0.5f && local.y < -0.08f, $"bullets spawn at the barrel end (muzzle {local.x:0.00}, {local.y:0.00}, {local.z:0.00} from the eye)");
                int v0 = cube.alive.Value, r0 = Tools.Bullets.Returns;
                yield return FireAt("SawYoyo", 0.15f, true, -10f);
                yield return new WaitForSeconds(1.5f);
                Check(v0 - cube.alive.Value > 3 && Tools.Bullets.Returns > r0, $"Saw Yo-yo cuts ({v0 - cube.alive.Value} voxels) and comes back");
                Check(Tools.Bullets.Strings > 0, "the yo-yo draws its string back to your hand");
                // VR-style throw: the hand's swing sets direction and speed
                int vt0 = Tools.ToolController.VrThrows, f0 = Tools.Bullets.Fired;
                bool limp = me.Tools.TestVrThrow("RippleDisc", Vector3.forward * 0.3f);
                bool thrown = me.Tools.TestVrThrow("RippleDisc", (Vector3.forward + Vector3.up * 0.2f) * 9f);
                Check(!limp && thrown && Tools.ToolController.VrThrows == vt0 + 1 && Tools.Bullets.Fired > f0, "VR throws: a real swing throws, a limp release doesn't");
                yield return new WaitForSeconds(1f);
            }
            {
                int v0 = cube.alive.Value;
                yield return FireAt("MagnetGlove", 0.7f, false, -10f);   // hold to grab, release to throw
                yield return new WaitForSeconds(1f);
                Check(v0 - cube.alive.Value > 20, $"Magnet Glove grabs and throws a chunk ({v0 - cube.alive.Value} voxels)");
            }
            {
                int v0 = cube.alive.Value;
                yield return FireAt("BubbleWand", 0.15f, true, -5f);
                yield return new WaitForSeconds(1.5f);
                Check(v0 - cube.alive.Value > 30, $"a bubble that touches the cube pops what's inside ({v0 - cube.alive.Value} voxels)");
                // ride: a bubble drifting into you floats you up
                me.Motor.Teleport(new Vector3(back.x, cube.transform.position.y + 0.2f, back.z - 7f), 0f);
                yield return new WaitForSeconds(0.4f);
                int r0 = Tools.Bullets.Rides;
                var fwd = me.transform.forward;
                Tools.FxHub.Instance.FireBullets(Balance.Tool("BubbleWand"), me.Motor.Chest + fwd * 2.2f, -fwd, 7, me.OwnerClientId, true, 1f);
                float vy = 0;
                for (float tt = 0; tt < 1.5f; tt += Time.deltaTime) { vy = Mathf.Max(vy, me.Motor.Velocity.y); yield return null; }
                Check(Tools.Bullets.Rides > r0 && vy > 2f, $"touching a bubble floats you up ({vy:0.0} m/s)");
                yield return new WaitForSeconds(3f);
            }

            // ---- movement weapons, out on the open platform behind the -X face
            var open = new Vector3(cube.Center.x - cube.HalfExtent - 3.5f, cube.transform.position.y + 0.2f, cube.Center.z);
            {
                var boost = Balance.Tool("BoostBlaster");
                me.ownedTools.Value |= 1UL << boost.index;
                me.Tools.SelectTool(boost.index);
                me.Motor.Teleport(open, 0f);
                _aim = Quaternion.identity; _pitch = 89f;   // straight down
                yield return new WaitForSeconds(0.4f);
                float yStart = me.transform.position.y, yMax = yStart;
                _holdUse = true; _pulseUse = true;
                yield return new WaitForSeconds(0.13f);
                _holdUse = false; _pulseUse = false;
                for (float tt = 0; tt < 1.2f; tt += Time.deltaTime) { yMax = Mathf.Max(yMax, me.transform.position.y); yield return null; }
                Check(yMax - yStart > 1.5f, $"Boost Blaster fired at the floor launches you ({yMax - yStart:0.0} m up)");
                yield return new WaitForSeconds(1.5f);
            }
            {
                // thrown explosives keep their throw speed (regression: the velocity used to be dropped at spawn)
                var gr = Balance.Tool("Grenade");
                me.charges[gr.index] = 5;
                yield return null;
                me.Tools.SelectTool(gr.index);
                me.Motor.Teleport(open, 270f);
                _aim = Quaternion.Euler(0, 270f, 0); _pitch = -10f;   // away from the cube
                yield return new WaitForSeconds(0.4f);
                _holdUse = true; _pulseUse = true;
                yield return new WaitForSeconds(0.13f);
                _holdUse = false; _pulseUse = false; _pitch = 0f;
                yield return new WaitForSeconds(0.45f);
                float far = 0;
                foreach (var e in Tools.ExplosiveProjectile.Live)
                    if (e.Def != null && e.Def.id == "Grenade")
                        far = Mathf.Max(far, Vector3.ProjectOnPlane(e.transform.position - me.transform.position, Vector3.up).magnitude);
                Check(far > 3f, $"a thrown grenade flies ({far:0.0} m in 0.45 s)");
                yield return new WaitForSeconds(2.5f);
            }
            {
                var pad = Balance.Tool("BoingPad");
                while (me.charges.Count <= pad.index) me.charges.Add(0);
                me.charges[pad.index] = 3;
                yield return null;
                me.Tools.SelectTool(pad.index);
                me.Motor.Teleport(open, 0f);
                _aim = Quaternion.identity; _pitch = 55f;   // at the floor ~1.5 m ahead
                yield return new WaitForSeconds(0.4f);
                _holdUse = true; _pulseUse = true;
                yield return new WaitForSeconds(0.13f);
                _holdUse = false; _pulseUse = false; _pitch = 0f;
                yield return new WaitForSeconds(0.6f);
                Tools.ExplosiveProjectile placed = null;
                foreach (var e in Tools.ExplosiveProjectile.Live) if (e.IsPad) placed = e;
                Check(placed != null, "Boing Pad can be placed");
                if (placed != null)
                {
                    me.Motor.Teleport(placed.transform.position + Vector3.up * 0.15f, 0f);
                    float vy = 0;
                    for (float tt = 0; tt < 0.8f; tt += Time.deltaTime) { vy = Mathf.Max(vy, me.Motor.Velocity.y); yield return null; }
                    Check(vy > 10f, $"stepping on a Boing Pad launches you ({vy:0.0} m/s up)");
                    yield return new WaitForSeconds(2.5f);
                }
                // thrown into the open (nothing within reach): it lands and stays spring-up, parked
                me.Motor.Teleport(open, 0f);
                _aim = Quaternion.identity; _pitch = -10f;
                yield return new WaitForSeconds(0.4f);
                int tilt0 = Tools.ExplosiveProjectile.PadsTilted;
                _holdUse = true; _pulseUse = true;
                yield return new WaitForSeconds(0.13f);
                _holdUse = false; _pulseUse = false; _pitch = 0f;
                yield return new WaitForSeconds(2.5f);
                Tools.ExplosiveProjectile thrownPad = null;
                foreach (var e in Tools.ExplosiveProjectile.Live) if (e.IsPad && e != placed) thrownPad = e;
                bool upright = thrownPad != null && Vector3.Angle(thrownPad.transform.up, Vector3.up) < 2f;
                bool parked = thrownPad != null && thrownPad.GetComponent<Rigidbody>().isKinematic;
                Check(upright && parked && Tools.ExplosiveProjectile.PadsTilted == tilt0, $"a thrown Boing Pad lands spring-up and stays put (upright {upright}, parked {parked})");
            }

            // ---- loot: there is loot hidden in the cube; break the shallowest one and get paid
            {
                Check(cube.LootCount >= 30, $"loot hidden in the cube ({cube.LootCount} gold voxels)");
                int best = -1, bestDepth = int.MaxValue, n = cube.Grid.N;
                foreach (var idx in cube.CellsMarked(CellMark.Loot))
                {
                    var c = cube.Grid.Coord(idx);
                    int d = Mathf.Min(Mathf.Min(Mathf.Min(c.x, n - 1 - c.x), Mathf.Min(c.y, n - 1 - c.y)), Mathf.Min(c.z, n - 1 - c.z));
                    if (d < bestDepth) { bestDepth = d; best = idx; }
                }
                if (best >= 0)
                {
                    int lootCash0 = me.money.Value, ch0 = 0; ulong own0 = me.ownedTools.Value;
                    foreach (var ch in me.charges) ch0 += ch;
                    int lc = cube.LootCount;
                    cube.ServerDamage(cube.CellToWorld(cube.Grid.Coord(best)), 0.4f, 1e6f, 9, false, me.OwnerClientId);
                    yield return new WaitForSeconds(0.3f);
                    int ch1 = 0; foreach (var ch in me.charges) ch1 += ch;
                    bool paid = me.money.Value != lootCash0 || ch1 != ch0 || me.ownedTools.Value != own0;
                    Check(cube.LootCount == lc - 1 && paid, $"breaking a loot voxel (depth {bestDepth}) pays out");
                }
            }

            // ---- Raptor Laptop: opens with 6 missiles, strikes blow up the cube, closing hands control back
            {
                var lap = Balance.Tool("RaptorLaptop");
                me.charges[lap.index] = 1;
                yield return null;
                me.Tools.SelectTool(lap.index);
                _holdUse = true; _pulseUse = true;
                yield return new WaitForSeconds(0.13f);
                _holdUse = false; _pulseUse = false;
                yield return new WaitForSeconds(0.6f);
                var rv = Tools.RaptorView.Active;
                Check(rv != null && rv.MissilesLeft == 6, "Raptor Laptop opens with 6 missiles");
                if (rv != null)
                {
                    int v0 = cube.alive.Value;
                    var top = cube.Center + Vector3.up * cube.HalfExtent;
                    rv.Strike(top);
                    yield return new WaitForSeconds(0.5f);
                    rv.Strike(top + Vector3.right * 3f);
                    yield return new WaitForSeconds(2f);
                    Check(cube.alive.Value < v0 - 100, $"Raptor strikes blow up the cube ({v0 - cube.alive.Value} voxels from 2 missiles)");
                    rv.End();
                    yield return null;
                    Check(Tools.RaptorView.Active == null && !Tools.StreakState.TakesOverInput, "closing the laptop gives control back");
                }
            }

            // ---- Heli-Gunner: chopper arrives, you ride the door seat, the door gun shreds, then you land
            {
                var hg = Balance.Tool("HeliGunner");
                me.charges[hg.index] = 1;
                yield return null;
                me.Tools.SelectTool(hg.index);
                _holdUse = true; _pulseUse = true;
                yield return new WaitForSeconds(0.13f);
                _holdUse = false; _pulseUse = false;
                yield return new WaitForSeconds(1.5f);
                var heli = Tools.HeliGunship.Instance;
                var hv = Tools.HeliView.Active;
                Check(heli != null && hv != null, "Heli-Gunner calls in the chopper and seats you");
                if (heli != null && hv != null)
                {
                    Check(Vector3.Distance(me.transform.position, heli.Seat.position) < 1f, "you ride the door seat");
                    int v0 = cube.alive.Value;
                    hv.aimAt = cube.Center + Vector3.up * cube.HalfExtent * 0.5f;
                    hv.autoFire = true;
                    yield return new WaitForSeconds(3f);
                    hv.autoFire = false;
                    yield return new WaitForSeconds(1f);
                    Check(cube.alive.Value < v0 - 50, $"the door gun shreds the cube ({v0 - cube.alive.Value} voxels in 3 s)");
                    heli.ServerEnd();
                    yield return new WaitForSeconds(1f);
                    Check(Tools.HeliView.Active == null && Tools.HeliGunship.Instance == null && !me.Motor.Frozen &&
                          me.transform.position.y < cube.transform.position.y + 3f, "when the chopper leaves you're back on the platform");
                }
            }

            // ---- super modes, around the -X face
            System.Collections.IEnumerator UseStreak(string id)
            {
                var st = Balance.Tool(id);
                me.charges[st.index] = 1;
                yield return null;
                me.Tools.SelectTool(st.index);
                yield return null;
                _holdUse = true; _pulseUse = false;   // one clean press
                yield return new WaitForSeconds(0.1f);
                _holdUse = false; me.Tools.SetTriggers(false, false);
                yield return new WaitForSeconds(0.5f);
            }
            var xFace2 = cube.Center + Vector3.left * cube.HalfExtent;
            var standX = new Vector3(xFace2.x - 4f, cube.transform.position.y + 0.2f, xFace2.z);
            {
                me.Motor.Teleport(standX, 90f);
                yield return UseStreak("AquaSurge");
                var sm = Tools.SuperMode.Active;
                Check(sm != null && sm.Kind == "AquaSurge", "Aqua Surge starts charging");
                if (sm != null)
                {
                    int v0 = cube.alive.Value;
                    sm.autoAim = cube.Center; sm.autoUse = true;
                    yield return new WaitForSeconds(1.6f);
                    sm.autoUse = false;
                    yield return new WaitForSeconds(3.2f);
                    Check(v0 - cube.alive.Value > 300 && Tools.SuperMode.Active == null, $"Aqua Surge tunnels through the cube ({v0 - cube.alive.Value} voxels)");
                }
            }
            {
                me.Motor.Teleport(standX + Vector3.left * 2f, 90f);
                yield return new WaitForSeconds(0.5f);
                float y0 = me.transform.position.y;
                yield return UseStreak("MeteorDive");
                var sm = Tools.SuperMode.Active;
                Check(sm != null && sm.Kind == "MeteorDive", "Meteor Dive launches");
                if (sm != null)
                {
                    int v0 = cube.alive.Value;
                    sm.autoAim = cube.Center + Vector3.up * cube.HalfExtent;
                    yield return new WaitForSeconds(1.2f);
                    float up = me.transform.position.y - y0;
                    Check(up > 15f, $"Meteor Dive blasts you into the sky ({up:0} m)");
                    yield return new WaitForSeconds(4.5f);
                    Check(v0 - cube.alive.Value > 100 && Tools.SuperMode.Active == null, $"the dive leaves a crater ({v0 - cube.alive.Value} voxels)");
                    yield return new WaitForSeconds(1f);
                }
            }
            {
                me.Motor.Teleport(standX, 90f);
                yield return UseStreak("SunOrb");
                var sm = Tools.SuperMode.Active;
                Check(sm != null && sm.Kind == "SunOrb", "Sun Orb starts gathering");
                if (sm != null)
                {
                    int v0 = cube.alive.Value;
                    sm.sunChargeS = 2.5f; sm.autoAim = xFace2 + Vector3.forward * 5f + Vector3.up * 3f;   // not down the Surge tunnel
                    yield return new WaitForSeconds(1.2f);
                    Check(me.Tools.sunOrbSize.Value > 0.05f, $"the Sun Orb grows over your head ({me.Tools.sunOrbSize.Value:0.00})");
                    yield return new WaitForSeconds(4f);
                    Check(v0 - cube.alive.Value > 150, $"a thrown Sun Orb leaves a crater ({v0 - cube.alive.Value} voxels from a short 2.5 s charge)");
                }
            }
            {
                me.Motor.Teleport(standX, 90f);
                yield return UseStreak("TitanBuddy");
                var sm = Tools.SuperMode.Active;
                yield return new WaitForSeconds(0.8f);
                Check(sm != null && me.Motor.WorldScale > 4.5f, $"Titan Buddy makes you giant ({me.Motor.WorldScale:0.0}x)");
                if (sm != null)
                {
                    int v0 = cube.alive.Value;
                    sm.autoAim = xFace2 + Vector3.up * 3f; sm.autoUse = true;
                    yield return new WaitForSeconds(2f);
                    sm.autoUse = false;
                    yield return new WaitForSeconds(0.5f);
                    Check(v0 - cube.alive.Value > 150, $"titan punches crater the cube ({v0 - cube.alive.Value} voxels)");
                    me.Tools.SuperEndRpc(); sm.End();
                    yield return null;
                    Check(Mathf.Abs(me.Motor.WorldScale - 1f) < 0.01f && Tools.SuperMode.Active == null, "you shrink back when it ends");
                    yield return new WaitForSeconds(1.5f);
                }
            }
            {
                yield return UseStreak("EchoClones");
                int ghosts = 0;
                foreach (var tr in FindObjectsByType<Transform>()) if (tr.name.StartsWith("EchoClone")) ghosts++;
                Check(me.Tools.clonesOn.Value && ghosts == 3, $"Echo Clones appear ({ghosts} clones)");
                int f0 = Tools.Bullets.Fired;
                yield return FireAt("Minigun", 1f, false, -12f);
                int fired = Tools.Bullets.Fired - f0;
                Check(fired > 60, $"the clones copy your shots ({fired} bubbles from a 1 s burst)");
                me.Tools.clonesOn.Value = false;
                yield return null;
            }

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
                yield return new WaitForSeconds(0.3f);
                Check(Siege.SunGodVisual.Built > 0, "the God is a sun with shades and hands");
                Shot("god_hands");
                {
                    var hp = me.headAnchor.position;
                    ShotFrom("god_outside", hp + me.transform.forward * 14f + Vector3.up * 2f, hp - Vector3.up * 2f);
                }

                // gestures: grab is free, a thrown bolt and a Sun Wave hit units, a squash kills one
                sd.mana.Value = 100;
                for (int k = 0; k < 3; k++) sd.BuyUnitRpc(0, 0);
                me.role.Value = PlayerRole.Commander; sd.money.Value = 1000;
                yield return null;
                for (int k = 0; k < 4; k++) sd.BuyUnitRpc(0, k % 3);
                yield return new WaitForSeconds(0.6f);
                me.role.Value = PlayerRole.God;
                yield return new WaitForSeconds(0.3f);
                if (Siege.EnemyUnit.Live.Count > 0)
                {
                    var target = Siege.EnemyUnit.Live[0];
                    float mana0 = sd.mana.Value;
                    sd.GrabRpc(new Unity.Netcode.NetworkObjectReference(target.NetworkObject), false);
                    yield return new WaitForSeconds(0.2f);
                    Check(target.state.Value == Siege.UnitState.Held && sd.mana.Value >= mana0 - 0.01f, "grabbing a unit is free");
                    sd.ReleaseRpc(new Unity.Netcode.NetworkObjectReference(target.NetworkObject), Vector3.up * 3f);
                    yield return new WaitForSeconds(0.5f);

                    int hits0 = Siege.SiegeDirector.SkillHits;
                    var u2 = Siege.EnemyUnit.Live.Count > 0 ? Siege.EnemyUnit.Live[Siege.EnemyUnit.Live.Count - 1] : null;
                    {
                        // at a unit if one is left, else onto the cube
                        var at = u2 ? u2.transform.position : cube.Center + Vector3.up * cube.HalfExtent;
                        var from = at + new Vector3(0, 12f, -10f);
                        sd.ServerThrowSkill(sd.Skill("SunBomb"), from, Siege.GodController.LobVelocity("SunBomb", from, at));
                    }
                    yield return new WaitForSeconds(2f);
                    Check(Siege.SiegeDirector.SkillHits > hits0, $"a thrown Sun Bomb lands ({Siege.SiegeDirector.SkillHits - hits0} hits)");
                    int waveHits = Siege.SiegeDirector.SkillHits;
                    if (Siege.EnemyUnit.Live.Count > 0)
                    {
                        var u3 = Siege.EnemyUnit.Live[0];
                        var o = u3.transform.position + new Vector3(0, 3f, -12f);
                        sd.ServerSunWave(o, (u3.transform.position + Vector3.up * 0.5f - o).normalized, 1f);
                    }
                    yield return new WaitForSeconds(0.3f);
                    Check(Siege.SiegeDirector.Waves > 0 && Siege.SiegeDirector.SkillHits > waveHits, "the Sun Wave beam hits units in its path");
                    if (Siege.EnemyUnit.Live.Count > 0)
                    {
                        var u4 = Siege.EnemyUnit.Live[0];
                        var handHome = me.rightHand.localPosition;
                        me.rightHand.position = u4.transform.position + Vector3.up * 0.5f;
                        yield return null;
                        sd.MashRpc(new Unity.Netcode.NetworkObjectReference(u4.NetworkObject));
                        yield return null;
                        me.rightHand.localPosition = handHome;
                        yield return new WaitForSeconds(0.3f);
                        Check(u4 == null || !u4.IsSpawned, "slamming a hand down squashes a unit");
                    }
                    int claps0 = sd.timeSlowLeft.Value > 0 ? 1 : 0;
                    sd.CastSkillRpc(sd.Skill("TimeSlow").index, Vector3.zero);
                    yield return new WaitForSeconds(0.2f);
                    Check(sd.timeSlowLeft.Value > 0, "a clap brings the Lazy Clouds");
                }

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

            // ---- Titan Siege, solo, playing the Titan (desktop fallback): bots defend
            {
                var td = Siege.TitanDirector.Instance;
                MatchManager.ForceHumanTitan = true;
                mm.ServerStartMatch(GameMode.TitanSiege);
                bool up = false;
                yield return Wait(() => td.IsActive && VoxelCube.Instance != null && VoxelCube.Instance.Grid != null && me.role.Value == PlayerRole.Titan, 10f, ok => up = ok);
                Check(up, "Titan Siege started with you as the Titan");
                var map = VoxelCube.Instance;
                yield return new WaitForSeconds(1.5f);
                Check(map.shapeMode.Value == 1 && map.alive.Value > 20000, $"walls + town built ({map.alive.Value} voxels, {map.Grid.N}^3 grid at {map.cellSize} m)");
                Check(Mathf.Abs(me.transform.localScale.x - Siege.TitanDirector.Scale) < 0.01f, $"you're Titan-sized (x{me.transform.localScale.x:0})");
                Check(Siege.TitanVisual.Built > 0, "the rigged Titan model is on you");
                me.Motor.Teleport(me.transform.position, 0f);
                yield return new WaitForSeconds(0.3f);
                Shot("titan_firstperson");
                ShotFrom("titan_outside_idle", me.transform.position + new Vector3(6f, 5f, 12f), me.transform.position + Vector3.up * 3.5f);
                me.Motor.moveIntent = Vector3.forward;
                td.prepLeft.Value = 0.05f;
                _pump = new Vector3(1f, 0f, 0f);
                yield return new WaitForSeconds(1.2f);
                ShotFrom("titan_outside_walk", me.transform.position + new Vector3(10f, 4f, 6f), me.transform.position + Vector3.up * 3.5f);
                _pump = Vector3.zero;
                td.KickRpc(false);
                yield return new WaitForSeconds(0.45f);
                ShotFrom("titan_outside_kick", me.transform.position + new Vector3(10f, 4f, 2f), me.transform.position + Vector3.up * 3f);
                yield return new WaitForSeconds(1.2f);
                td.prep.Value = true; td.prepLeft.Value = 30f;
                Check(td.BotCount == Siege.TitanDirector.MinDefenders, $"bots fill the defenders to 5 v 1 ({td.BotCount} bots)");
                Check(Siege.TitanDirector.InputLocked(me), "the Titan waits out the prep time");
                yield return new WaitForSeconds(3f);
                int loaded = 0; for (int i = 0; i < 4; i++) loaded += Mathf.Max(0, td.ammo[i]);
                Check(loaded > 0, $"defender bots load the turrets during prep ({loaded} rounds)");
                td.prepLeft.Value = 0.05f;
                yield return new WaitForSeconds(0.3f);

                // walk up to wall 1
                var o = td.Origin;
                float nearZ = Siege.TitanMap.WallNearZ(o, 0);
                me.Motor.Teleport(new Vector3(o.x, o.y + 0.2f, nearZ - 2.2f), 0f);
                yield return new WaitForSeconds(0.4f);
                int v0 = map.alive.Value, sc0 = Siege.TitanDirector.Scrapes;
                var wallFace = new Vector3(o.x, o.y + 3f, nearZ + 0.2f);
                for (int k = 0; k < 6; k++) { td.TitanHitRpc(wallFace + Vector3.right * k * 0.3f, Vector3.right * 0.6f * 7f, 0); yield return new WaitForSeconds(0.07f); }
                yield return new WaitForSeconds(0.3f);
                int scraped = v0 - map.alive.Value;
                Check(Siege.TitanDirector.Scrapes > sc0 && scraped > 0, $"a slow hand scrapes the wall ({scraped} voxels from 6 scrapes, no cooldown)");
                int v1 = map.alive.Value;
                td.TitanHitRpc(wallFace + Vector3.up * 1.5f, Vector3.forward * 4.5f * 7f, 1);
                yield return new WaitForSeconds(0.5f);
                int punched = v1 - map.alive.Value;
                Check(punched > scraped, $"a hard punch dents much more ({punched} voxels)");
                int v2 = map.alive.Value, kicks0 = Siege.TitanDirector.Kicks;
                td.KickRpc(false);
                yield return new WaitForSeconds(0.9f);
                Check(Siege.TitanDirector.Kicks > kicks0 && map.alive.Value < v2, $"a kick smashes the wall ({v2 - map.alive.Value} voxels)");
                int feet0 = Siege.TitanDirector.Scrapes;
                _pump = Vector3.forward;
                yield return new WaitForSeconds(1.2f);
                _pump = Vector3.zero;
                Check(Siege.TitanDirector.Scrapes > feet0, $"walking into the wall grinds it with your legs ({Siege.TitanDirector.Scrapes - feet0} scrapes)");

                // the defenders shoot back
                float hp0 = td.titanHp.Value;
                int hits0 = Siege.TitanDirector.ShotsHit;
                td.ammo[0] = 3;
                td.ServerFireTurret(0);
                yield return new WaitForSeconds(2.5f);
                Check(Siege.TitanDirector.ShotsHit > hits0 && td.titanHp.Value < hp0, $"turret shots hit the Titan ({hp0:0} -> {td.titanHp.Value:0} HP, {Siege.TitanDirector.ShotsHit - hits0} hits)");
                float hp1 = td.titanHp.Value;
                Tools.Explosion.ServerExplode(Siege.TitanDirector.TitanChest(td.Body), Balance.Tool("RocketLauncher"), 999, false);
                yield return null;
                Check(td.titanHp.Value < hp1, $"an RPG hurts the Titan ({hp1:0} -> {td.titanHp.Value:0})");
                Check(Siege.DefenderBot.RocketsFired + Siege.DefenderBot.TurretShots > 0, $"defender bots fight ({Siege.DefenderBot.RocketsFired} RPGs, {Siege.DefenderBot.TurretShots} turret shots)");
                int wallVox = map.alive.Value;

                // through the wall: the defenders fall back to wall 2
                me.Motor.Teleport(new Vector3(o.x, o.y + 0.2f, Siege.TitanMap.WallFarZ(o, 0) + 3f), 0f);
                yield return new WaitForSeconds(0.5f);
                Check(td.wall.Value == 1 && td.ammo[0] < 0, "breaking through wall 1 moves the fight to wall 2");
                Check(map.alive.Value <= wallVox, "player explosions never break the walls");

                // frame time with the Titan, the town and the bots
                float worst = 0, sum = 0; int frames = 0;
                for (float tt = 0; tt < 2f; tt += Time.deltaTime) { sum += Time.deltaTime; worst = Mathf.Max(worst, Time.deltaTime); frames++; yield return null; }
                Log($"Titan Siege frame time: avg {sum / frames * 1000f:0.0} ms, worst {worst * 1000f:0.0} ms, {map.alive.Value} voxels");

                td.titanHp.Value = 0.5f;
                td.ServerDamageTitan(5f);
                yield return new WaitForSeconds(0.5f);
                Check(mm.state.Value == MatchState.Ended && mm.resultText.Value.ToString().Contains("defenders win"), $"bringing the Titan down ends it: \"{mm.resultText.Value}\"");
                yield return new WaitForSeconds(0.5f);
                Check(!td.IsActive && td.BotCount == 0, "ending Titan Siege removes the bots");
            }

            // ---- Titan Siege vs the AI Titan (no headset): you defend with four bots
            {
                var td = Siege.TitanDirector.Instance;
                MatchManager.ForceHumanTitan = false;
                mm.ServerStartMatch(GameMode.TitanSiege);
                yield return new WaitForSeconds(1.5f);
                Check(me.role.Value == PlayerRole.Defender && td.HasAiTitan && td.BotCount == 4, $"AI Titan + you and 4 bots (role {me.role.Value}, bots {td.BotCount})");
                Check(me.Owns(Balance.Tool("RocketLauncher").index) && me.Owns(Balance.Tool("GrappleHook").index) && me.Owns(Balance.Tool("Jetpack").index), "defenders get an RPG, webs and a jetpack");
                var map = VoxelCube.Instance;
                td.prepLeft.Value = 0.05f;
                int v0 = map.alive.Value;
                var bot = Siege.TitanBot.Instance;
                float z0 = bot ? bot.transform.position.z : 0;
                yield return new WaitForSeconds(5f);
                if (bot)
                {
                    // watch it from the wall top
                    me.Motor.Teleport(Siege.TitanMap.WallTop(td.Origin, 0, 9f) + Vector3.up * 0.3f, 180f);
                    for (int f = 0; f < 3; f++) { LookAt(me, bot.transform.position + Vector3.up * 3.5f); yield return null; }
                    _holdUse = false;
                    LookAt(me, bot.transform.position + Vector3.up * 3.5f); Shot("ai_titan_walk");
                    yield return new WaitForSeconds(4f);
                    LookAt(me, bot.transform.position + Vector3.up * 3.5f); Shot("ai_titan_wall");
                }
                yield return new WaitForSeconds(3f);
                float z1 = bot ? bot.transform.position.z : 0;
                Check(z1 > z0 + 5f, $"the AI Titan marches on the wall ({z0:0} -> {z1:0} m)");
                Check(map.alive.Value < v0 && Siege.TitanBot.Punches > 0, $"the AI Titan smashes the wall ({v0 - map.alive.Value} voxels, {Siege.TitanBot.Punches} punches)");
                // standing at a turret loads it
                // the turret on the far side from where the Titan is punching
                int t = td.wall.Value * 4 + (bot && bot.transform.position.x > td.Origin.x ? 0 : 3);
                me.Motor.Teleport(td.TurretPos(t) + Vector3.right * 0.8f + Vector3.up * 0.3f, 180f);
                int a0 = Mathf.Max(0, td.ammo[t]), fired0 = Siege.TitanDirector.ShotsFired;
                yield return new WaitForSeconds(3f);
                // (a bot on the same turret fires each round as soon as it's loaded)
                Check(td.ammo[t] > a0 || Siege.TitanDirector.ShotsFired > fired0, $"standing at a turret loads it (ammo {a0} -> {td.ammo[t]}, {Siege.TitanDirector.ShotsFired - fired0} shots)");
                Log($"AI Titan after ~16 s: {td.titanHp.Value:0}/{td.titanMaxHp.Value:0} HP, wall {td.wall.Value + 1}, {Siege.DefenderBot.TurretShots} bot turret shots, {Siege.DefenderBot.RocketsFired} bot RPGs");
                Check(Siege.DefenderController.NearTurret == t, "the HUD knows which turret you're at");
                mm.ServerEndMatch("autotest");
                yield return new WaitForSeconds(1f);
            }

            // ---- hotbar: two rows of 8 at most, pinned picks first
            {
                mm.RequestLobbyRpc();
                yield return new WaitForSeconds(0.5f);
                foreach (var t in Balance.Tools) if (!t.consumable && t.cat != ToolCategory.Hidden && t.cat != ToolCategory.Streak) me.ownedTools.Value |= 1UL << t.index;
                yield return null;
                var pins = GameSettings.HotbarPins;
                GameSettings.HotbarPins = "";
                int usable = me.Tools.Usable().Count, bar = me.Tools.Hotbar().Count;
                Check(usable > 16 && bar == 16, $"the hotbar holds 16 of your {usable} tools");
                GameSettings.HotbarPins = "SonicHammer,RailGun";
                var hb = me.Tools.Hotbar();
                Check(hb.Exists(t => t.id == "SonicHammer") && hb.Exists(t => t.id == "RailGun"), "pinned tools are on the hotbar");
                GameSettings.HotbarPins = pins;
            }

            Check(VoxelCube.MissedIslandCells == 0, $"local island check matched the full flood fill (missed {VoxelCube.MissedIslandCells} cells)");
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
            if (_swing) { me.Tools.swingHeld = true; me.Tools.swingAim = _swingAim; }
            if (_reel) me.Motor.reelIn = true;
            if (_pump != Vector3.zero) me.Motor.moveIntent = _pump;
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
