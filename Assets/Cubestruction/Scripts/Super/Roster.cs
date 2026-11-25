using UnityEngine;

namespace Cubestruction.Super
{
    /// <summary>What a special does on the server (SuperDirector.ServerSpecial runs each kind).</summary>
    public enum MoveKind : byte
    {
        FlashStep, Rush, RisingStrike, Slam, Grab, Blast, Volley, Freeze, Slash, Beam, Tornado,
        // ultimates
        Sphere, MeteorDrop, Storm, AbsoluteZero, Eclipse, TimeStop
    }

    public sealed class MoveDef
    {
        public string name, desc;
        public MoveKind kind;
        public int tier;            // tier that unlocks it (Sandbox: everything)
        public float cost;          // Power spent
        public float cooldown;
        public float damage;
        public float radius;        // hit / blast radius (m)
        public float speed;         // projectiles (m/s)
        public int count = 1;       // volley shots, rush hits
        public float voxels;        // voxel blast radius in cells
        public float length;        // beams: reach in metres (0 = Roster.BeamLength)
        public MoveDef(string name, MoveKind kind, int tier, float cost, float cooldown, float damage, float radius, float speed, float voxels, string desc, int count = 1)
        {
            this.name = name; this.kind = kind; this.tier = tier; this.cost = cost; this.cooldown = cooldown; this.damage = damage;
            this.radius = radius; this.speed = speed; this.voxels = voxels; this.desc = desc; this.count = count;
        }
        public bool IsUltimate => kind >= MoveKind.Sphere;
    }

    public sealed class FighterDef
    {
        public string name, title, style, blurb;
        public Color color, aura, hair;
        public float hpMul = 1f, speedMul = 1f, damageMul = 1f;
        /// <summary>Tier that unlocks flight bursts (full flight is always one tier later).</summary>
        public int flyTier = 3;
        /// <summary>Specials A, B, C (unlocked at tiers 2, 3, 4) and the ultimate (tier 5).</summary>
        public MoveDef[] moves;
        public MoveDef Ult => moves[3];
    }

    /// <summary>
    /// The SUPER! roster: six original fighters, each with its own three specials and ultimate.
    /// Everyone shares the basics: a 3-hit punch chain, kicks (look up = launcher, in the air looking
    /// down = axe kick), block (tap it just before a hit to parry), Power Up and flight.
    /// </summary>
    public static class Roster
    {
        public const float BaseHp = 1000f;
        public const int MaxTier = 5;
        public static readonly string[] TierNames = { "", "Brawler", "Fighter", "Energy", "Ascended", "ULTIMATE" };

        public static readonly FighterDef[] All =
        {
            new FighterDef
            {
                name = "Kenji", title = "Blaze", style = "All-rounder · fire",
                blurb = "A street fighter with a hot temper. Good at everything, with a fireball, an anti-air and a big beam.",
                color = new Color(1f, 0.45f, 0.15f), aura = new Color(1f, 0.55f, 0.1f), hair = new Color(1f, 0.85f, 0.2f),
                moves = new[]
                {
                    new MoveDef("Rising Flame", MoveKind.RisingStrike, 2, 8, 2.5f, 140, 2.3f, 0, 3, "Flaming uppercut that launches you and them skyward."),
                    new MoveDef("Blaze Palm", MoveKind.Blast, 3, 12, 1.6f, 120, 0.55f, 24, 4.5f, "Fireball that blows holes in walls."),
                    new MoveDef("Phoenix Cannon", MoveKind.Beam, 4, 20, 7f, 30, 1.5f, 0, 8f, "Hold to charge, release: a huge 2.5 s beam that tunnels through anything. Steer it!"),
                    new MoveDef("Sun Crash", MoveKind.Sphere, 5, 0, 8f, 520, 13f, 12, 27, "Gather a giant sun overhead, then hurl it."),
                },
            },
            new FighterDef
            {
                name = "Rin", title = "Volt", style = "Rushdown · lightning", speedMul = 1.18f, hpMul = 0.9f,
                blurb = "Fastest on the roster. Teleports in, hits a hundred times, gets out.",
                color = new Color(1f, 0.9f, 0.25f), aura = new Color(0.55f, 0.95f, 1f), hair = new Color(0.6f, 0.95f, 1f),
                moves = new[]
                {
                    new MoveDef("Flash Step", MoveKind.FlashStep, 2, 6, 1.4f, 90, 14f, 0, 0, "Blink behind the nearest foe and strike."),
                    new MoveDef("Thunder Rush", MoveKind.Rush, 3, 12, 3f, 22, 10f, 0, 2, "Dash in for a crackling 10-hit flurry.", 10),
                    new MoveDef("Pierce Bolt", MoveKind.Beam, 4, 18, 6f, 34, 0.3f, 0, 3f, "A needle-thin bolt from your fingertip that pierces right through everything.") { length = 80f },
                    new MoveDef("Thousand Strikes", MoveKind.Storm, 5, 0, 8f, 65, 26f, 0, 3, "Lightning strikes every foe nearby, again and again.", 7),
                },
            },
            new FighterDef
            {
                name = "Brom", title = "Boulder", style = "Grappler · earth", hpMul = 1.35f, speedMul = 0.86f, damageMul = 1.1f,
                blurb = "Slow, huge and stubborn. Grabs you, jumps, and plants you in the floor.",
                color = new Color(0.55f, 0.4f, 0.25f), aura = new Color(0.65f, 0.95f, 0.35f), hair = new Color(0.4f, 0.85f, 0.3f),
                moves = new[]
                {
                    new MoveDef("Mountain Drop", MoveKind.Grab, 2, 8, 2.5f, 210, 2.6f, 0, 6, "Grab a foe close by and slam them into the ground."),
                    new MoveDef("Quake Stomp", MoveKind.Slam, 3, 12, 3f, 130, 5f, 0, 7, "Stomp a crater; everyone near you goes flying."),
                    new MoveDef("Boulder Toss", MoveKind.Blast, 4, 18, 3.5f, 230, 1.3f, 16, 8, "Rip up a boulder and throw it through anything."),
                    new MoveDef("Meteor Drop", MoveKind.MeteorDrop, 5, 0, 8f, 520, 9f, 0, 16, "Leap into the sky and come down like a meteor."),
                },
            },
            new FighterDef
            {
                name = "Yuki", title = "Frost", style = "Zoner · ice", hpMul = 0.95f,
                blurb = "Keeps you at arm's length with ice shards, freezes you solid, then shatters you.",
                color = new Color(0.55f, 0.85f, 1f), aura = new Color(0.7f, 0.95f, 1f), hair = new Color(0.9f, 0.97f, 1f),
                moves = new[]
                {
                    new MoveDef("Ice Shards", MoveKind.Volley, 2, 8, 1.3f, 32, 0.25f, 32, 1.5f, "A fan of five ice shards.", 5),
                    new MoveDef("Frost Dome", MoveKind.Freeze, 3, 12, 4f, 60, 6f, 0, 0, "Freeze everyone near you for a moment."),
                    new MoveDef("Blizzard Beam", MoveKind.Beam, 4, 20, 7f, 22, 0.9f, 0, 3f, "A wide freezing beam that slows what it touches."),
                    new MoveDef("Absolute Zero", MoveKind.AbsoluteZero, 5, 0, 8f, 400, 17f, 0, 6, "Freeze the whole area, then shatter it."),
                },
            },
            new FighterDef
            {
                name = "Kage", title = "Shade", style = "Ninja · shadow", speedMul = 1.08f,
                blurb = "Appears behind you, cuts the building in half, and is gone before it falls.",
                color = new Color(0.45f, 0.3f, 0.7f), aura = new Color(0.65f, 0.35f, 1f), hair = new Color(0.25f, 0.15f, 0.35f),
                moves = new[]
                {
                    new MoveDef("Shadow Step", MoveKind.FlashStep, 2, 6, 1.6f, 100, 16f, 0, 0, "Vanish and reappear behind your target."),
                    new MoveDef("Void Shuriken", MoveKind.Volley, 3, 10, 1.6f, 45, 0.3f, 36, 2.5f, "Three spinning shadow stars.", 3),
                    new MoveDef("Crescent Slash", MoveKind.Slash, 4, 18, 3.5f, 140, 1.6f, 28, 2.5f, "A flying sword wave that cuts through walls."),
                    new MoveDef("Eclipse Cut", MoveKind.Eclipse, 5, 0, 8f, 460, 8f, 40, 6, "One giant slash that splits the whole arena."),
                },
            },
            new FighterDef
            {
                name = "Sora", title = "Gale", style = "Aerial · wind", speedMul = 1.1f, hpMul = 0.95f, flyTier = 2,
                blurb = "Lives in the air. Flies a tier earlier than everyone else and kicks like a hurricane.",
                color = new Color(0.4f, 0.95f, 0.7f), aura = new Color(0.6f, 1f, 0.85f), hair = new Color(0.85f, 1f, 0.95f),
                moves = new[]
                {
                    new MoveDef("Cyclone Kick", MoveKind.Rush, 2, 8, 2.2f, 30, 8f, 0, 2, "Spin through the air, kicking all the way.", 5),
                    new MoveDef("Gust Wave", MoveKind.Slash, 3, 12, 2.5f, 90, 2.2f, 24, 1.5f, "A wall of wind that blows foes away."),
                    new MoveDef("Hurricane", MoveKind.Tornado, 4, 20, 7f, 30, 6f, 4, 3, "A tornado that sucks in foes and the scenery."),
                    new MoveDef("Eye of the Storm", MoveKind.TimeStop, 5, 0, 10f, 1.3f, 60f, 0, 0, "Stop time for everyone else for 4 seconds."),
                },
            },
        };

        public static FighterDef Get(int i) => All[Mathf.Clamp(i, 0, All.Length - 1)];

        /// <summary>The three basic moves everybody has.</summary>
        public const float JabDamage = 38f, CrossDamage = 44f, HookDamage = 62f, KickDamage = 80f, LauncherDamage = 70f, AxeDamage = 95f;
        public const float LightCooldown = 0.26f, HeavyCooldown = 0.6f, MeleeRange = 2.0f;

        /// <summary>Damage multiplier per tier (Tier 1 = 1x, Tier 5 = 1.6x).</summary>
        public static float TierDamage(int tier) => 1f + 0.15f * (Mathf.Clamp(tier, 1, MaxTier) - 1);
        public static float TierSpeed(int tier) => 1f + 0.06f * (Mathf.Clamp(tier, 1, MaxTier) - 1);
        /// <summary>Voxel tool tier for map damage: higher tiers break tougher material.</summary>
        public static int VoxelTier(int tier) => Mathf.Clamp(tier, 1, 5);

        // Power: dealing and taking damage both fill it (comebacks matter), plus a trickle.
        /// <summary>Break the cube: Power per voxel you break (divided by your tier, since higher tiers break far more).</summary>
        public const float PowerPerVoxel = 0.14f;
        public const float PowerMax = 100f, PowerPerDealt = 0.06f, PowerPerTaken = 0.045f, PowerPerSecond = 0.35f, PowerParry = 8f;
        /// <summary>Holding Tier 4 drains Power; at zero you drop back to Tier 3.</summary>
        public const float AscendedUpkeep = 0.8f;
        public const float TransformHeal = 0.2f;
        public const float RespawnSeconds = 3f, MatchSeconds = 300f;
        public const float BeamCharge = 1.2f, BeamFire = 2.5f, BeamLength = 34f;
    }
}
