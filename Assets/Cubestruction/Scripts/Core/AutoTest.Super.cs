using System.Collections;
using System.Linq;
using Cubestruction.Player;
using Cubestruction.Super;
using Cubestruction.Voxels;
using UnityEngine;

namespace Cubestruction
{
    /// <summary>-autotest: SUPER! and Sandbox (also on its own with -supertest).</summary>
    public partial class AutoTest
    {
        IEnumerator Frames(int n) { for (int i = 0; i < n; i++) yield return null; }

        /// <summary>Press a touch button for one frame (the same path the mobile buttons use).</summary>
        IEnumerator Tap(System.Action press) { press(); yield return null; yield return null; }

        IEnumerator HoldSpecial(int slot, float seconds)
        {
            FightInput.Special[slot] = true;
            yield return new WaitForSeconds(seconds);
            FightInput.Special[slot] = false;
            yield return null; yield return null;
        }

        IEnumerator SuperTest()
        {
            yield return SuperCubeTest();
            SuperDirector.Rules = 1;   // the rest is the Brawl rules
            yield return SuperBrawlTest();
            SuperDirector.Rules = 0;
        }

        /// <summary>Default SUPER! rules: break the Mining cube with anime moves.</summary>
        IEnumerator SuperCubeTest()
        {
            var mm = MatchManager.Instance;
            var me = PlayerAvatar.Local;
            if (mm.state.Value != MatchState.Lobby) { mm.RequestLobbyRpc(); yield return new WaitForSeconds(0.5f); }
            SuperDirector.Rules = 0;
            var fs = me.GetComponent<FighterState>();
            fs.character.Value = 0;
            mm.ServerStartMatch(GameMode.Super);
            yield return new WaitForSeconds(1.5f);
            var sd = SuperDirector.Instance; var fc = FighterController.Local; var cube = VoxelCube.Instance;
            Check(!sd.IsBrawl && cube.shapeMode.Value == 0 && me.role.Value == PlayerRole.Fighter && sd.Bots.Count == 0 && fs.Tier == 1,
                  $"SUPER! (break the cube): the Mining cube, you're a Tier 1 fighter, no bots (shape {cube.shapeMode.Value}, bots {sd.Bots.Count})");
            var face = cube.Center + Vector3.back * cube.HalfExtent;
            me.Motor.Teleport(new Vector3(face.x, cube.transform.position.y + 0.2f, face.z - 1.1f), 0f);
            yield return new WaitForSeconds(0.4f);
            fc.autoAim = new Vector3(face.x, me.transform.position.y + 0.9f, face.z + 0.3f);
            int v0 = cube.alive.Value; float p0 = fs.power.Value;
            for (int i = 0; i < 8; i++) { yield return Tap(() => FightInput.Light = true); yield return new WaitForSeconds(0.3f); }
            Check(cube.alive.Value < v0 - 10 && fs.power.Value > p0 + 0.5f, $"punches break the cube and fill Power ({v0 - cube.alive.Value} voxels, power {p0:0.0} -> {fs.power.Value:0.0})");
            Check(me.voxels.Value > 0, $"broken voxels count for you ({me.voxels.Value})");
            fs.power.Value = Roster.PowerMax;
            yield return Tap(() => FightInput.PowerUp = true);
            yield return new WaitForSeconds(0.4f);
            Check(fs.Tier == 2, "Power Up from breaking voxels: Tier 2");
            // Rising Flame into the cube
            fs.power.Value = 50; v0 = cube.alive.Value;
            yield return HoldSpecial(0, 0.05f);
            yield return new WaitForSeconds(1.2f);
            Check(cube.alive.Value < v0, $"Rising Flame breaks the cube ({v0 - cube.alive.Value} voxels)");
            // Kage's Shadow Step blinks to the face and strikes; Rin's rush flurries it
            me.Motor.Teleport(new Vector3(face.x, cube.transform.position.y + 0.2f, face.z - 6f), 0f);
            yield return new WaitForSeconds(0.4f);
            fs.character.Value = 4; fs.power.Value = 50; fs.cooldownEnd[0] = 0; v0 = cube.alive.Value;
            yield return null;
            fc.autoAim = new Vector3(face.x, me.transform.position.y + 0.9f, face.z + 0.3f);
            Check(sd.ServerSpecial(fs, 0, fs.Chest, (fc.autoAim.Value - fs.Chest).normalized), "Shadow Step with no foe to hit still works");
            yield return new WaitForSeconds(0.5f);
            Check(cube.alive.Value < v0, $"Shadow Step blinks to the cube and strikes it ({v0 - cube.alive.Value} voxels)");
            // ultimate
            fs.character.Value = 0; fs.tier.Value = 5; fs.cooldownEnd[3] = 0;
            me.Motor.Teleport(new Vector3(face.x, cube.transform.position.y + 0.2f, face.z - 10f), 0f);
            yield return new WaitForSeconds(0.4f);
            fc.autoAim = cube.Center; v0 = cube.alive.Value;
            yield return HoldSpecial(3, 1.3f);
            yield return new WaitForSeconds(3f);
            Check(cube.alive.Value < v0 - 500, $"Sun Crash blows a crater in the cube ({v0 - cube.alive.Value} voxels)");
            Shot("super_cube");
            // Sora's time stop has nobody to stop: a cyclone instead
            fs.character.Value = 5; fs.tier.Value = 5; fs.cooldownEnd[3] = 0; v0 = cube.alive.Value;
            yield return null;
            me.Motor.Teleport(new Vector3(face.x, cube.transform.position.y + 0.2f, face.z - 3f), 0f);
            yield return new WaitForSeconds(0.3f);
            sd.ServerSpecial(fs, 3, fs.Chest, Vector3.forward);
            yield return new WaitForSeconds(3f);
            Check(cube.alive.Value < v0, $"Eye of the Storm becomes a cube-eating cyclone ({v0 - cube.alive.Value} voxels)");
            fs.character.Value = 0;
            mm.ServerEndMatch("autotest");
            yield return new WaitForSeconds(0.5f);
            mm.RequestLobbyRpc();
            yield return new WaitForSeconds(0.5f);
            mm.ServerStartMatch(GameMode.Sandbox);
            yield return new WaitForSeconds(1.5f);
            Check(!sd.IsBrawl && VoxelCube.Instance.shapeMode.Value == 0 && fs.Tier == 5 && sd.Bots.Count == 0, "Sandbox (break the cube): Tier 5 on the cube, no dummies");
            mm.ServerEndMatch("autotest");
            yield return new WaitForSeconds(0.5f);
            mm.RequestLobbyRpc();
            yield return new WaitForSeconds(0.5f);
            fc.autoAim = null;
        }

        IEnumerator SuperBrawlTest()
        {
            var mm = MatchManager.Instance;
            var me = PlayerAvatar.Local;
            if (mm.state.Value != MatchState.Lobby) { mm.RequestLobbyRpc(); yield return new WaitForSeconds(0.5f); }
            SuperDirector.BotsOverride = 2;
            GameSettings.Fighter = 0;
            var fs = me.GetComponent<FighterState>();
            fs.character.Value = 0;   // Kenji
            int hits0 = SuperDirector.Hits;
            mm.ServerStartMatch(GameMode.Super);
            yield return new WaitForSeconds(1.5f);
            var sd = SuperDirector.Instance;
            var fc = FighterController.Local;
            var map = VoxelCube.Instance;
            Check(me.role.Value == PlayerRole.Fighter && fs.inMatch.Value && fs.Tier == 1 && sd.Bots.Count == 2, $"SUPER! starts: you're a Tier 1 fighter with 2 bots (role {me.role.Value}, tier {fs.Tier}, bots {sd.Bots.Count})");
            Check(map.shapeMode.Value == 2 && map.Grid.NX == SuperArena.SizeX && map.alive.Value > 30000, $"the SUPER! arena is built ({map.Grid.NX}x{map.Grid.NY}x{map.Grid.NZ}, {map.alive.Value} voxels)");
            Check(fc != null && fc.enabled && !Roles.UsesTools(me.role.Value), "fighter controls on, no tools");
            Shot("super_start");

            // the bots stand still for the scripted part
            foreach (var b in sd.Bots) b.dummy.Value = true;
            var bot = sd.Bots[0];
            var bs = bot.State;
            var o = sd.Origin;
            var spot = new Vector3(o.x + 3f, SuperArena.TopY(o) + 0.1f, o.z - 8f);
            me.Motor.Teleport(spot, 0f);
            bot.Place(spot + Vector3.forward * 1.4f, 180f);
            sd.Bots[1].Place(new Vector3(o.x - 20f, SuperArena.TopY(o) + 0.1f, o.z + 10f), 0f);
            yield return new WaitForSeconds(0.4f);
            fc.autoAim = bs.Chest;

            // ---- punch chain
            float hp0 = bs.hp.Value, pow0 = fs.power.Value;
            for (int i = 0; i < 3; i++) { yield return Tap(() => FightInput.Light = true); yield return new WaitForSeconds(0.3f); }
            Check(bs.hp.Value < hp0 - 80f && FighterController.SentMelee >= 3, $"punch chain lands ({hp0 - bs.hp.Value:0} damage from {FighterController.SentMelee} punches, combo {fs.combo.Value})");
            Check(fs.power.Value > pow0, $"landing hits fills Power ({pow0:0.0} -> {fs.power.Value:0.0})");
            bot.Place(spot + Vector3.forward * 1.4f, 180f);   // the hook knocked it away
            yield return new WaitForSeconds(0.3f);
            fc.autoAim = bs.Chest;
            yield return Tap(() => FightInput.Heavy = true);
            yield return new WaitForSeconds(0.5f);
            Check(SuperDirector.Hits >= hits0 + 4, $"kick lands ({SuperDirector.Hits - hits0} hits so far)");

            // ---- block and parry
            me.Motor.Teleport(spot, 0f);
            bot.Place(spot + Vector3.forward * 1.4f, 180f);
            yield return new WaitForSeconds(0.6f);
            fc.autoAim = bs.Chest;
            bs.blocking.Value = true;
            yield return null;
            bs.blockSince = Time.time;
            int parries = SuperDirector.Parries;
            yield return new WaitForSeconds(0.05f);
            yield return Tap(() => FightInput.Light = true);
            yield return new WaitForSeconds(0.1f);
            Check(SuperDirector.Parries > parries, "a block right before the hit parries it");
            yield return new WaitForSeconds(1.0f);   // the parry stun wears off
            me.Motor.Teleport(spot, 0f);              // (and it knocked you back)
            bot.Place(spot + Vector3.forward * 1.4f, 180f);
            yield return new WaitForSeconds(0.3f);
            fc.autoAim = bs.Chest;
            int blocks = SuperDirector.Blocks; float hpB = bs.hp.Value;
            yield return Tap(() => FightInput.Light = true);
            yield return new WaitForSeconds(0.2f);
            Check(SuperDirector.Blocks > blocks && hpB - bs.hp.Value < 20f, $"a held block takes chip damage only ({hpB - bs.hp.Value:0.0})");
            bs.blocking.Value = false;

            // ---- tier gate, then Power Up
            Check(!sd.ServerSpecial(fs, 0, fs.Chest, Vector3.forward), "specials are locked at Tier 1");
            fs.power.Value = Roster.PowerMax;
            yield return Tap(() => FightInput.PowerUp = true);
            yield return new WaitForSeconds(0.4f);
            Check(fs.Tier == 2 && SuperDirector.Transforms > 0, $"Power Up: Tier 2 (tier {fs.Tier})");
            Shot("super_powerup");

            // ---- Tier 2: Rising Flame
            bot.Place(spot + Vector3.forward * 1.4f, 180f);
            yield return new WaitForSeconds(1.0f);
            fc.autoAim = bs.Chest;
            fs.power.Value = 50;
            float hpR = bs.hp.Value;
            yield return HoldSpecial(0, 0.05f);
            yield return new WaitForSeconds(0.5f);
            Check(bs.hp.Value < hpR - 100f || bs.down.Value, $"Rising Flame (Tier 2 special) lands ({hpR - bs.hp.Value:0})");
            yield return new WaitForSeconds(1.5f);

            // ---- Tier 3: Blaze Palm (a fireball through the scenery)
            fs.tier.Value = 3; fs.power.Value = 60;
            yield return new WaitForSeconds(0.2f);
            int shots = SuperDirector.Shots, vBefore = map.alive.Value;
            var pillar = new Vector3(o.x + Mathf.Cos(Mathf.PI / 8f) * 12f, SuperArena.TopY(o) + 1.5f, o.z + Mathf.Sin(Mathf.PI / 8f) * 12f);
            me.Motor.Teleport(pillar + (spot - pillar).normalized * 7f + Vector3.down * 1.4f, 0f);
            yield return new WaitForSeconds(0.3f);
            fc.autoAim = pillar;
            yield return HoldSpecial(1, 0.05f);
            yield return new WaitForSeconds(1.2f);
            Check(SuperDirector.Shots > shots && map.alive.Value < vBefore, $"Blaze Palm fires and blasts the pillar ({vBefore - map.alive.Value} voxels)");

            // ---- Tier 4: Phoenix Cannon (hold to charge, release to fire)
            fs.tier.Value = 4; fs.power.Value = 80;
            me.Motor.Teleport(spot, 0f);
            bot.Place(spot + Vector3.forward * 9f, 180f);
            yield return new WaitForSeconds(0.4f);
            fc.autoAim = bs.Chest;
            int ticks = SuperDirector.BeamTicks; float hpBeam = bs.hp.Value; vBefore = map.alive.Value;
            yield return HoldSpecial(2, 1.3f);
            yield return new WaitForSeconds(1.5f);
            Check(SuperDirector.BeamTicks > ticks + 5 && (bs.hp.Value < hpBeam || bs.down.Value), $"Phoenix Cannon: {SuperDirector.BeamTicks - ticks} beam ticks, {hpBeam - bs.hp.Value:0} damage, {vBefore - map.alive.Value} voxels carved");
            Shot("super_beam");
            yield return new WaitForSeconds(1.6f);

            // ---- beam clash: the bot fires back, you mash
            yield return Until(() => !bs.down.Value, 6f, "bot back up for the clash");
            bs.character.Value = 0; bs.tier.Value = 4; bs.power.Value = 100; bs.hp.Value = bs.maxHp.Value;
            yield return Wait(() => fc.CooldownLeft(2) <= 0f, 8f, _ => { });
            var spot2 = new Vector3(o.x - 9f, SuperArena.TopY(o) + 0.1f, o.z - 8f);   // untouched ground (the last beam dug a trench)
            me.Motor.Teleport(spot2, 0f);
            bot.Place(spot2 + Vector3.forward * 14f, 180f);
            fs.power.Value = 100; fs.cooldownEnd[2] = 0;
            for (int i = 0; i < 4; i++) bs.cooldownEnd[i] = 0;
            yield return new WaitForSeconds(0.3f);
            fc.autoAim = bs.Chest;
            FightInput.Special[2] = true;
            yield return new WaitForSeconds(1.25f);
            sd.ServerSpecial(bs, 2, bs.Chest, (fs.Chest - bs.Chest).normalized, 1f);
            FightInput.Special[2] = false;
            int clashes = SuperDirector.Clashes;
            bool clashed = false;
            yield return Wait(() => SuperDirector.Clashes > clashes, 2f, ok => clashed = ok);
            Check(clashed, $"two beams meeting head-on start a BEAM CLASH (my beam {sd.HasBeam(fs)}, bot beam {sd.HasBeam(bs)}, me down {fs.down.Value} frozen {fs.Frozen(sd.Now)}, cd {fc.CooldownLeft(2):0.0}, power {fs.power.Value:0}, me y {me.transform.position.y - o.y:0.0}, bot y {bot.transform.position.y - o.y:0.0}, dist {Vector3.Distance(me.transform.position, bot.transform.position):0.0})");
            float hpClash = bs.hp.Value;
            for (float t = 0; t < 3f && fc.InClash; t += Time.deltaTime) { FightInput.Light = true; yield return null; }
            yield return new WaitForSeconds(0.4f);
            Check(clashed && (bs.hp.Value < hpClash - 200f || bs.down.Value), $"mashing wins the clash ({hpClash - bs.hp.Value:0} damage to the bot)");
            yield return new WaitForSeconds(1.5f);

            // ---- Tier 5: Sun Crash
            yield return Until(() => !bs.down.Value, 6f, "bot back up for the ultimate");
            fs.tier.Value = 5; fs.power.Value = 0; fs.cooldownEnd[3] = 0;
            me.Motor.Teleport(spot2, 0f);
            bot.Place(spot2 + Vector3.forward * 12f, 180f);
            yield return new WaitForSeconds(0.3f);
            fc.autoAim = bs.transform.position;
            int ults = SuperDirector.Ults; vBefore = map.alive.Value;
            yield return HoldSpecial(3, 1.3f);
            yield return new WaitForSeconds(2.5f);
            Log($"ult debug: down {fs.down.Value} alive {fs.Alive} cd {fc.CooldownLeft(3):0.0} denied {SuperDirector.Denied} sent {FighterController.SentSpecials}");
            Check(SuperDirector.Ults > ults && fs.Tier == 4, $"Sun Crash fires and drops you back to Tier 4 (tier {fs.Tier}, {vBefore - map.alive.Value} voxels)");
            Shot("super_ult");

            // ---- KO, respawn and ring-out
            yield return Until(() => !bs.down.Value, 6f, "bot back up for the KO");
            int kos = fs.kos.Value;
            me.Motor.Teleport(spot, 0f);
            bot.Place(spot + Vector3.forward * 1.4f, 180f);
            bs.hp.Value = 5f;
            yield return new WaitForSeconds(0.3f);
            fc.autoAim = bs.Chest;
            yield return Tap(() => FightInput.Light = true);
            yield return new WaitForSeconds(0.3f);
            Check(bs.down.Value && fs.kos.Value == kos + 1, $"K.O.: the bot goes down and you score ({fs.kos.Value} KOs)");
            yield return new WaitForSeconds(Roster.RespawnSeconds + 0.5f);
            Check(!bs.down.Value && bs.hp.Value >= bs.maxHp.Value - 1f, "the bot gets back up with full health");
            int falls = bs.falls.Value;
            bot.Place(new Vector3(o.x + 45f, o.y - 6f, o.z), 0f);
            yield return new WaitForSeconds(0.3f);
            Check(bs.falls.Value == falls + 1, "falling off the stage is a ring-out");

            // ---- Sora's time stop + freezing
            yield return new WaitForSeconds(Roster.RespawnSeconds + 0.5f);
            fs.character.Value = 5; fs.tier.Value = 5; fs.cooldownEnd[3] = 0;
            yield return null;
            Check(sd.ServerSpecial(fs, 3, fs.Chest, Vector3.forward) && bs.Frozen(sd.Now) && sd.timeStopUntil.Value > sd.Now, "Eye of the Storm stops time for everyone else");
            yield return new WaitForSeconds(4.3f);
            Check(!bs.Frozen(sd.Now), "time starts again");

            // ---- flight (Sora flies from Tier 2; Tier 3+ is full flight)
            fs.tier.Value = 4;
            var spot3 = new Vector3(o.x + 9f, SuperArena.TopY(o) + 0.1f, o.z + 4f);   // flat, untouched ground
            me.Motor.Teleport(spot3, 0f);
            yield return Wait(() => me.Motor.Grounded, 2f, _ => { });
            yield return new WaitForSeconds(0.2f);
            float y0 = me.transform.position.y;
            me.Motor.Jump();
            yield return new WaitForSeconds(0.15f);
            MobileInput.Jump = true; MobileInput.JumpHeld = true;
            yield return null;
            for (float t = 0; t < 1.2f; t += Time.deltaTime) { MobileInput.JumpHeld = true; yield return null; }
            MobileInput.JumpHeld = false;
            float risen = me.transform.position.y - y0;
            yield return new WaitForSeconds(0.5f);
            Check(risen > 4f && fc.Flying, $"flying: rose {risen:0.0} m and hovering (grappling {me.Motor.IsGrappling}, frozen {me.Motor.Frozen}, ragdoll {me.Ragdoll.Active}, flyLevel {fc.FlyLevel})");
            fs.character.Value = 0;
            for (float t = 0; t < 2f && fc.Flying; t += Time.deltaTime) { FightInput.Land = true; yield return null; }
            FightInput.Land = false;

            // ---- the bots fight for real
            foreach (var b in sd.Bots) b.dummy.Value = false;
            me.Motor.Teleport(spot, 0f);
            int acts = FighterBot.Actions; float myHp = fs.hp.Value;
            yield return new WaitForSeconds(7f);
            Check(FighterBot.Actions > acts + 3, $"bots fight ({FighterBot.Actions - acts} attacks; you took {myHp - fs.hp.Value:0})");
            Shot("super_bots");

            // ---- save, end, load
            foreach (var b in sd.Bots) b.dummy.Value = true;
            fs.tier.Value = 3; fs.power.Value = 42f;
            yield return new WaitForSeconds(1f);
            int aliveSaved = map.alive.Value;
            var err = mm.ServerSave();
            Check(err == null && SaveGame.Exists, $"save the game ({err ?? SaveGame.FilePath})");
            mm.ServerEndMatch("autotest");
            yield return new WaitForSeconds(1f);
            Check(sd.scoreboard.Value.Length > 0 && mm.resultText.Value.ToString().Length > 0, "the end screen has a KO scoreboard");
            mm.RequestLobbyRpc();
            yield return new WaitForSeconds(0.5f);
            var save = SaveGame.Read();
            mm.ServerStartMatch(GameMode.Mining, save);
            yield return new WaitForSeconds(1.5f);
            map = VoxelCube.Instance;
            Check(mm.mode.Value == GameMode.Super && Mathf.Abs(map.alive.Value - aliveSaved) < 50 && fs.Tier == 3 && Mathf.Abs(fs.power.Value - 42f) < 2f,
                  $"load brings back the wrecked arena and your tier ({map.alive.Value} vs {aliveSaved} voxels, tier {fs.Tier}, power {fs.power.Value:0})");
            mm.ServerEndMatch("autotest");
            yield return new WaitForSeconds(0.5f);
            mm.RequestLobbyRpc();
            yield return new WaitForSeconds(0.5f);

            // ---- Sandbox: everything unlocked, infinite Power, training dummies
            SuperDirector.BotsOverride = -1;
            mm.ServerStartMatch(GameMode.Sandbox);
            yield return new WaitForSeconds(1.5f);
            Check(fs.Tier == 5 && fs.power.Value >= 99f && sd.Bots.Count == 2 && sd.Bots.All(b => b.dummy.Value), $"Sandbox: Tier 5, full Power, 2 dummies (tier {fs.Tier}, bots {sd.Bots.Count})");
            bool all = true;
            for (int s = 0; s < 4; s++) { fs.cooldownEnd[s] = 0; all &= sd.ServerSpecial(fs, s, fs.Chest, Vector3.forward); }
            Check(all && fs.Tier == 5, "Sandbox: every special and the ultimate work at once, and you stay at Tier 5");
            yield return new WaitForSeconds(3f);
            Shot("sandbox");
            mm.ServerEndMatch("autotest");
            yield return new WaitForSeconds(0.5f);
            mm.RequestLobbyRpc();
            yield return new WaitForSeconds(0.5f);
            SaveGame.Delete();
            fc.autoAim = null;
            SuperDirector.BotsOverride = -1;
        }
    }
}
