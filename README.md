# Cubestruction

A cross-platform multiplayer voxel-demolition game in a glossy **Frutiger Aero** style. You play as a little
glass-plastic *Buddy*. Climb, swing around and mine a giant 68×68×68 pixel cube with 38 tools and weapons,
call in killstreaks, go super, blow it up, get blown up, and get paid.
The match ends the moment the **last pixel** is cleared.

Players on **Meta Quest (VR)**, **PC**, **Mac** and **Mobile** can play together over Unity Relay.

| Mode | Who | Goal |
|---|---|---|
| **Mining** (co-op, 1–6) | everyone | Clear every voxel. Inner layers pay more and are harder. The MVP is whoever cleared the most. |
| **God Siege** (1 v many) | VR = **God**; everyone else votes **Raider** (on foot, with tools and the shop) or **Minion Caster** (one is picked at random from the volunteers) | The Caster sends ever-stronger waves of units (Age of War style) while Raiders dig; together they must destroy the Core. The God (a giant sun with shades and human hands) defends for 15 minutes with gesture skills and grabs, swats and squashes anything. Mana regen +15% per Raider. |
| **Titan Siege** (always ≥ 5 v 1) | VR = **Titan** (no headset: an **AI Titan**); everyone else **defends**, bots fill the team to 5 | The Titan starts as a little Buddy at the foot of the first wall; when the prep time ends they bite their hand and lightning makes them a Titan. It smashes through three thick walls (Stone, Crystal, Magma) and the packed voxel towns between them. Defenders dig the mine wall on top of their wall for cannonballs, rockets and hidden guns, carry the ammo to cannons, artillery and rocket pods, and fire their RPG. Kill the Titan before the third wall falls. |

![Buddies](Docs/previews/buddy.png)
![Tools](Docs/previews/tools.png)

---

## Getting it running

**Requirements:** Unity **6000.6.8f1** with Android Build Support (+ OpenJDK/SDK/NDK) and Mac/Windows modules as
needed. Blender is optional: the generated models are committed.

> **Don't open the project from an exFAT drive.** Unity's asset database and IPC need a real Unix filesystem: on
> exFAT, batch mode silently aborts ("Failed to initialise UDS client", LMDB `MDB_BAD_RSLOT`). On macOS keep it on
> APFS (e.g. an APFS sparse bundle on the external drive).

1. Open the folder in Unity Hub. Packages resolve from `Packages/manifest.json`.
2. Menu **Cubestruction ▸ Build Everything** generates the URP pipeline, materials, every network prefab, tool
   visuals and the scene `Assets/Cubestruction/Scenes/Cubestruction.unity` (all gitignored: regenerate, don't
   commit). Re-run it after changing `tools.json` or models.
3. **VR:** menu **Cubestruction ▸ Configure VR** sets up OpenXR (Meta Quest + Touch/Touch Plus on Android, Touch +
   Index on PC VR), single-pass instanced and ASTC. No manual XR clicking needed.
4. **Builds:**
   - Quest: **Cubestruction ▸ Build Quest APK** → `Builds/Quest/Cubestruction.apk`.
   - Android phones/tablets: **Build Android Mobile APK** → `Builds/Mobile/Cubestruction-mobile.apk` (no XR
     loader, so it starts flat with touch controls).
   - Both from the command line:
     `Unity -batchmode -buildTarget Android -projectPath . -executeMethod Cubestruction.EditorTools.QuestBuild.BuildAll`
   - Mac/PC: a normal standalone build (`-novr` forces flat mode with a headset attached, `-mobileui` forces touch UI).
5. **Online play (Relay):** link a Unity Cloud project in *Project Settings ▸ Services*. LAN needs nothing.
6. **Smoke test:** launch any desktop build with `-autotest`. It hosts a LAN game (port 7790), runs ~130 checks
   (mining, swinging, every weapon, loot, killstreaks, supers, God Siege, Titan Siege, hotbar); `-shotdir <folder>` saves screenshots of the giants and logs `[AutoTest] PASS/FAIL` lines,
   then quits (exit code 1 on failure).

## Controls

**VR (Quest Touch):**

| Input | Action |
|---|---|
| Grip | Climb: grab **any** voxel or prop and pull. Release mid-pull to fling yourself. |
| Trigger | Use the tool in that hand. Swing melee tools for real; hold drills and beams on target. To throw: squeeze, swing, release. |
| A / X | Cycle the tool in that hand. The left hand can be empty, keeping it free for climbing. |
| B / Y | Shop / menu (laser pointer, trigger to click) |
| Left stick | Move: Smooth, Teleport arc, or ArmSwing (hold both grips and swing your arms), set in Settings |
| Off-hand trigger (hand empty) | **Swing**: shoot a web-line from the Zip Tether; right-stick click reels in, let go to fling |
| Trigger with a Warp Kunai / Saw Yo-yo / Ripple Disc | **Throw it for real**: squeeze, swing, let go (how hard you throw sets the speed). Kunai: press again to warp to it |
| Right stick | Snap or smooth turn; click to jump |

Two-handed tools (all guns and the Thumper jackhammer) only fire while your off-hand supports the tool, and they aim along both hands.

**PC/Mac:** WASD move · Mouse look · Space jump (double jump with Hop Boots) · **hold E to spider-climb** any surface you face ·
**hold Q to swing** (Zip Tether; Space reels in) ·
the **hotbar** holds two rows of 8 (shows each tool's cooldown); own more than 16 tools and pin the ones you want under *Pause ▸ Edit hotbar* (or *Shop ▸ Hotbar*, which works in VR) ·
LMB use tool · RMB jetpack · 1–9 / wheel hotbar · Tab shop · Shift sprint. Gamepads also work.

**Mobile:** left-side joystick · right-side drag to look · Jump, Use (hold), Climb (hold), Sprint, Swing (once owned), Shop buttons ·
tap a hotbar slot to switch weapons.

**Killstreaks / supers:** select them in the hotbar and press Use. The Raptor Laptop opens a drone view on PC/mobile
(click/tap to strike) and a targeting laser in VR; the Heli-Gunner seats you in a circling chopper's door gun;
supers take over the Use button while active (charge, aim, throw, punch).

**God (VR, God Siege):** you're an 8× sun with hands; every skill is a gesture.

| Gesture | Effect |
|---|---|
| Grip near a unit or Raider | Grab it (free); let go mid-swing to throw it (Raiders ragdoll) |
| Fast open hand / slam straight down | Swat (ragdolls Raiders) / squash (kills units, pancakes Raiders) |
| Trigger | The selected skill appears in that hand; throw it: **Zeus Bolt, Sun Bomb, Hail of Blades, Bubble Ward, Spring Bloom, Comet Drop** |
| Left stick click / X | Next / previous throwable skill |
| Hands together + both triggers, then push | **Sun Wave** (the longer the charge, the wider and stronger) |
| Clap | **Lazy Clouds** (all units at 40% speed) |

Desktop fallback: LMB grab/throw, 1–6 throw at the crosshair, K Sun Wave, C clap.

**Titan (VR, Titan Siege):** you start as a little Buddy. When the prep time ends, **bite your hand** (bring a hand to
your mouth) and lightning turns you into a 7× Titan. Drag your hands through walls to scrape them (no cooldown);
a fast punch dents harder the faster you swing; walking into a wall grinds it with your legs; **A / X kick**.
Swat cannonballs with a fast hand; an **open hand blocks an RPG** (a quarter of the damage), a **closed fist catches it**
and letting go throws it back; **grip a defender** to pick them up and throw them. Guard your **nape** (triple damage).
Below 50% health you're enraged, below 25% berserk: faster and harder-hitting, steaming.
Desktop fallback: B bite, LMB punch, hold RMB scrape, F kick, hold G to catch.

**Defender (Titan Siege):** you start with the web-swing, an **empty** RPG and a sledgehammer (plus a short jetpack).
Dig the green **mine wall** along the back of your wall for **cannonballs and rockets** (and gold voxels hide guns that hurt
the Titan); you can't break the wall itself. Stand at a turret to load what you carry into it (cannons and artillery
take cannonballs, rocket pods take rockets), Use fires it. Your RPG fires the rockets you carry. **Hold Q to web-swing**
(webs stick to an invisible ceiling 20 voxels above the walls and last 3 s each) to get behind the Titan and hit its nape.
The end screen shows everyone's damage, nape hits, ammo mined and loaded, and an MVP.

**Commander (Siege):** WASD/drag to pan, wheel/pinch to zoom, Q/E rotate. Unit buttons at the bottom (hotkeys 1–6), pick one of 3 portals.

## Gameplay rules

- **Layers** by depth: Crust → Stone → Crystal → Magma → Core. Each is worth more and has more HP.
  A tool below a layer's tier deals only 35% damage per tier of difference, so better tools unlock the core.
- **Sand physics:** any voxel left with ≤1 neighbour crumbles. Any chunk disconnected from the base collapses
  into falling sand. You get paid for those too.
- **Loot:** 38 glowing **gold voxels** are hidden through the cube. Breaking one pays the breaker; deeper layers roll
  better prizes (killstreaks and supers only from crystal down).
- **Marked voxels:** the Frost Ray turns voxels to ice-glass that shatters in one hit; Boing Goo turns them into
  trampolines.
- **Explosives ragdoll you.** Anything with knockback ≥ 9 m/s launches you as a ragdoll, including your own.
  There's a comfort toggle (*Allow ragdoll*) for VR players.
- **Movement:** climbing, Gecko Gloves, **Zip Tether swinging** (Spider-Man style: each web goes a little ahead
  of where you're moving, up to an invisible ceiling 20 voxels above the cube, so you can swing forward while
  shooting sideways; a web lasts 3 s and the next needs a fresh press; pump with the stick, reel in, let go to fling), Hop Boots, Cloud Pack, Boing Pads, Boost Blaster recoil, Warp Kunai, Chain Whip, Bubble Wand
  rides, rocket-jumping.
- **Guns fire physics bullets** (gravity, ricochets, piercing); every impact digs. They're simulated on every peer
  from a shared seed and only the server's copy does damage.
- **Co-op scaling:** voxel HP × players^0.5 and value × players^0.7.

### Tools and weapons (38)

| Tool | Price | Tier | Type | What it does |
|---|---:|:-:|---|---|
| Bubble Pick | free | 1 | Melee | Trusty starter pick. Swing it into the cube. |
| Aqua Spade | 120 | 1 | Melee | Wide scoops. Great on the soft crust. |
| Fizz Grenade | 200/5 | 2 | Throwable | Pull, throw, boom. 5 per pack. |
| Gecko Gloves | 300 | - | Movement | Passive: double climb stamina, 40% faster climbing. |
| Goo Bomb | 450/5 | 3 | Throwable | Sticks to whatever it hits. |
| Gel Sledge | 500 | 2 | Melee | Heavy swings that crack stone. |
| Boing Pad | 600/3 | - | Placed | Place it, step on it, fly. Lasts a minute. 3 per pack. |
| Pop Stick | 700/4 | 3 | Throwable | Long fuse, big pop. 4 per pack. |
| Boing Goo Gun | 900 | 1 | Gun, physics | Hold to spray pink goo. Goo-coated voxels are trampolines: build bounce paths up the cube. |
| Zip Tether | 1,000 | - | Movement | Swing like a spider. Hold Q (PC) / Swing (mobile) / empty off-hand trigger (VR); jump reels in, let go to fling. |
| Fizzy Barrel | 1,200/3 | 4 | Placed | Place it, light it, RUN. 3 per pack. |
| Swirl Drill | 1,500 | 3 | Melee | Hold against the cube. Bores through crystal. |
| Spray Blaster | 1,800 | 2 | Gun (2H), physics | Eight bouncy pellets that ricochet twice. |
| Boost Blaster | 2,200 | 2 | Gun (2H), physics | The kick launches you: blast the floor to fly, or fire mid-swing for speed. |
| Warp Kunai | 2,500 | 2 | Gun, physics | Throw it and teleport to wherever it sticks. On a wall you hang right where it hit. |
| Sky Beam | 2,500 | 3 | Beam | Continuous beam. Mine from a distance. |
| Hop Boots | 2,500 | - | Movement | Double jump and a boost dash off walls. |
| Bubble Wand | 3,000 | 2 | Gun, physics | Blow big floaty bubbles: one that touches the cube pops everything inside it; touch one yourself to ride it up. |
| Pixel Charge | 3,000/3 | 4 | Placed | Stick on, detonate remotely. Comes with a Detonator. |
| Saw Yo-yo | 4,000 | 3 | Gun, physics | Fling a saw that cuts out about 8 m and back to your hand, slicing everything on the way. |
| Thumper | 4,000 | 4 | Melee (2H) | Rattles through magma. |
| Ripple Disc | 4,500 | 3 | Gun, physics | Saw-discs that slice a voxel, ricochet, and slice again. Five bounces each. |
| Chain Whip | 5,000 | 3 | Gun | Lash anything within 9 m: rips a chunk out and yanks you toward it. Chain it with swinging. |
| Chain Zapper | 5,500 | 3 | Gun | Hold to zap. Each bolt arcs across eight more voxels. |
| Firework Launcher | 6,000 | 3 | Gun, physics | Shells burst into fourteen rainbow stars that carve wherever they land. |
| Vortex Orb | 6,000/2 | 5 | Throwable | Pulls everything in, then crushes it (6 pulses). 2 per pack. |
| Cloud Pack | 7,000 | - | Movement | Hold to fly. 6 seconds of fuel, recharges on contact. |
| Bubble Gatling | 7,000 | 3 | Gun (2H), physics | 25 bubbles a second that skip off whatever they hit. |
| Pinball Popper | 8,000 | 4 | Gun, physics | Hold to spray bouncy balls. Each one ricochets up to 5 times. |
| Magnet Glove | 9,000 | 4 | Gun | Hold to rip a chunk out of the cube and float it; let go to throw it as a wrecking ball. |
| Aurora Cutter | 9,000 | 4 | Beam | A hotter beam that slices magma. |
| Bloom Mortar | 9,500 | 4 | Gun (2H), physics | Lob a shell that bursts into eight bouncing bomblets. |
| Bloop Launcher | 10,000 | 4 | Gun (2H) | Rocket-jump if you dare. |
| Frost Ray | 11,000 | 4 | Beam | Freezes voxels (up to magma) into glass that shatters in one hit from anything. |
| Drill Torpedo | 12,000 | 4 | Gun (2H), physics | Burrows 8 m into the cube, leaving a tunnel you can climb into. |
| Chime Hammer | 12,000 | 5 | Melee | Every hit rings out a shockwave. |
| Glass Lance | 15,000 | 5 | Gun (2H), physics | A glass slug that punches through the cube and ricochets. |
| Sunburst Crate | 40,000 | 5 | Placed | 10 second countdown. Get very, very far away. |

### Killstreaks and supers

| Item | Price | What it does |
|---|---:|---|
| Meteor Dive | 9,000 | Super. Blast 28 m into the sky, aim, and dive into the cube: crater and shockwave. Also found from crystal down. |
| Raptor Laptop | 12,000 | Killstreak. Open the laptop and call six missiles from the sky onto the cube. Also hides in crystal and deeper. |
| Aqua Surge | 15,000 | Super. Charge an energy beam (hands together in VR), then fire a 2.5 s tunnel straight through the cube. Found in magma. |
| Echo Clones | 18,000 | Super. Three glassy copies of you copy every shot, swing and throw for 20 s. Found in magma. |
| Sun Orb | 20,000 | Super. A sun grows over your head for 8 s, faster when anyone breaks voxels. Throw it for a giant crater. Found in the core. |
| Titan Buddy | 25,000 | Super. Grow 5x tall for 30 s: punch craters, land to stomp. Found in the core. |
| Heli-Gunner | 30,000 | Killstreak. Ride the door gun of a gunship circling the cube for 60 seconds. Also hides in magma and the core. |

Exact numbers live in `Assets/Cubestruction/Resources/Balance/tools.json`.

## Balance

All balance data lives in three JSON files under `Assets/Cubestruction/Resources/Balance/` (`layers.json`, `tools.json`, `siege.json`).
The game **and** a Python simulator read these same files:

```bash
pip install numpy
python3 Tools/balance_sim.py table     # $/s of every tool on every layer
python3 Tools/balance_sim.py mining    # simulated 1/2/4-player matches (--size, default 68)
python3 Tools/balance_sim.py siege     # God win-rate vs skill
python3 Tools/tune_tools.py            # re-fit tool damage to the target $/s curve
```

Current results:

| Scenario | Result |
|---|---|
| Mining, solo (68³) | cleared in **~72 min** |
| Mining, 2 players | **~34 min** |
| Mining, 4 players | **~20 min** |
| Siege, novice God (skill 0.3–0.45) | loses |
| Siege, competent God (0.6) | wins **~70%** |
| Siege, expert God (0.75+) | wins **~95%** |

The simulator models hitscan damage, not ricochets, splits or supers, so the newer weapons were tuned by hand
against `-autotest` measurements. Each original tool's damage was fitted so its $/s on its intended layer rises with price. No tool is strictly dominated, and
single-hit tools always one-shot a voxel of their target layer at the centre of the hit, so they feel punchy.

## Project layout

```
Assets/Cubestruction/
  Scripts/Core      Balance loader, settings, MatchManager, scene decor, AutoTest (-autotest smoke test)
  Scripts/Voxels    VoxelGrid (rules), VoxelCube (network, marks: loot/glass/goo), VoxelChunk (meshing), Loot, VoxelDebris
  Scripts/Player    PlayerAvatar, PlayerMotor (walk/climb/swing/float), VRRig, FlatRig, XRTracking (recenter), AvatarVisual, PlayerRagdoll
  Scripts/Tools     ToolController, Bullets (physics bullets), ExplosiveProjectile, FxHub, killstreaks (RaptorView,
                    HeliGunship/HeliView), supers (SuperMode, CloneEcho), StreakState
  Scripts/Siege     SiegeDirector, EnemyUnit, GodController, CommanderController
  Scripts/UI        Aero theme, HUD + hotbar, shop, menus, mobile controls, VR pointer
  Scripts/Net       NetBootstrap (Relay / LAN), OwnerNetworkTransform
  Scripts/Editor    CubestructionProjectBuilder (Build Everything), QuestBuild (Configure VR, Quest/mobile APKs)
  Shaders           AeroGloss (glossy plastic/glass), UnlitTransparent (beams, bubbles, vignette)
  Models            Blender-generated FBX: Buddy, 24 tools, 6 units, environment
  Resources/Balance layers.json, tools.json, siege.json
Blender/            Model generator scripts (python3 Blender/build_all.py with `pip install bpy`)
Tools/              balance_sim.py, tune_tools.py, make_sfx.py
```

## Networking model

- **Netcode for GameObjects 2** with **Unity Relay** (join codes) or direct IP. Host-authoritative.
- The voxel cube is server-authoritative: clients send compact, rate-limited, range-checked tool RPCs.
  The server applies damage and broadcasts destroyed cell indices. Late joiners get a bit-packed snapshot (8 KB).
- Player movement is owner-authoritative (`OwnerNetworkTransform`) so VR head and hands have zero latency.
- Explosions are simulated on the server. Knockback is sent to each affected player's owner, who ragdolls locally,
  and every client builds the same ragdoll for display.
