#!/usr/bin/env python3
"""
Cubestruction balance simulator.

Reads the same JSON balance files the Unity game loads
(Assets/Cubestruction/Resources/Balance/*.json) and simulates:

  mining  - a solo player (and 2/4-player co-op) clearing the full voxel cube
            with a greedy "buy the best $/s tool" shopping strategy, on a real
            voxel grid with the same damage/falloff/tier rules as VoxelCube.cs.
  siege   - God Siege mode: a Commander spending income on units vs a God of a
            given skill level (0..1). Reports win rate across skill levels.

Usage:
  python3 Tools/balance_sim.py mining [--players N] [--seed S]
  python3 Tools/balance_sim.py siege  [--runs N]
  python3 Tools/balance_sim.py titan  [--runs N]   (Titan Siege win rates by line-up)
  python3 Tools/balance_sim.py all
"""
import argparse, json, math, os, random, sys

try:
    import numpy as np
except ImportError:
    sys.exit("numpy required: pip install numpy")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
BAL = os.path.join(ROOT, "Assets", "Cubestruction", "Resources", "Balance")


def load(name):
    with open(os.path.join(BAL, name)) as f:
        return json.load(f)


LAYERS = load("layers.json")
TOOLS = load("tools.json")
SIEGE = load("siege.json")

# Human-efficiency factors (fraction of theoretical uptime a real player achieves),
# plus fixed per-use overhead in seconds (aiming, throwing, running from a fuse).
EFFICIENCY = {"Melee": 0.70, "Beam": 0.80, "Gun": 0.75, "Throwable": 0.85, "Placed": 0.9}
OVERHEAD = {"Melee": 0.0, "Beam": 0.0, "Gun": 0.0, "Throwable": 1.2, "Placed": 3.0}
SPECIAL_OVERHEAD = {"NukeCrate": 14.0, "C4": 1.5, "BlackHoleGrenade": 2.0}
UPTIME = 0.85   # fraction of time spent actually mining (rest: climbing / repositioning)


# ----------------------------------------------------------------------------- voxel grid
class Cube:
    """Mirror of VoxelCube.cs rules: layer by depth fraction, HP, tier penalty, falloff."""

    def __init__(self, n):
        self.n = n
        half = n / 2.0
        idx = np.arange(n)
        d1 = np.minimum(idx, n - 1 - idx)
        dx, dy, dz = np.meshgrid(d1, d1, d1, indexing="ij")
        depth = np.minimum(np.minimum(dx, dy), dz)
        frac = depth / half
        self.layer = np.zeros((n, n, n), dtype=np.int8)
        for i, L in reversed(list(enumerate(LAYERS["layers"]))):
            self.layer[frac < L["maxDepthFraction"]] = i
        self.hp_table = np.array([L["hp"] for L in LAYERS["layers"]], dtype=np.float32)
        self.val_table = np.array([L["value"] for L in LAYERS["layers"]], dtype=np.float32)
        self.tier_table = np.array([L["tier"] for L in LAYERS["layers"]], dtype=np.int32)
        self.hp = self.hp_table[self.layer].copy()
        self.solid = np.ones((n, n, n), dtype=bool)
        self.alive = int(self.solid.sum())

    def exposed(self):
        s = self.solid
        p = np.pad(s, 1, constant_values=False)
        neigh_empty = (~p[2:, 1:-1, 1:-1]) | (~p[:-2, 1:-1, 1:-1]) | (~p[1:-1, 2:, 1:-1]) | \
                      (~p[1:-1, :-2, 1:-1]) | (~p[1:-1, 1:-1, 2:]) | (~p[1:-1, 1:-1, :-2])
        return s & neigh_empty

    def damage_sphere(self, c, radius, dmg, tier, explosive):
        """Returns money earned. Same maths as VoxelCube.ApplyDamage."""
        n = self.n
        r = max(radius, 0.5)
        lo = np.maximum(np.floor(np.array(c) - r).astype(int), 0)
        hi = np.minimum(np.ceil(np.array(c) + r).astype(int) + 1, n)
        sl = tuple(slice(lo[i], hi[i]) for i in range(3))
        gx, gy, gz = np.meshgrid(*[np.arange(lo[i], hi[i]) for i in range(3)], indexing="ij")
        dist = np.sqrt((gx - c[0]) ** 2 + (gy - c[1]) ** 2 + (gz - c[2]) ** 2)
        inside = (dist <= r) & self.solid[sl]
        if not inside.any():
            return 0.0
        t = np.clip(dist / r, 0, 1)
        fall = (1 - t) if explosive else (1 - 0.5 * t)
        lt = self.tier_table[self.layer[sl]]
        pen = np.where(tier >= lt, 1.0, LAYERS["tierPenalty"] ** np.maximum(lt - tier, 0))
        hp = self.hp[sl]
        hp -= np.where(inside, dmg * fall * pen, 0)
        killed = inside & (hp <= 0)
        money = float(self.val_table[self.layer[sl]][killed].sum())
        self.solid[sl][killed] = False
        self.alive -= int(killed.sum())
        money += self.crumble(lo, hi)
        return money

    def crumble(self, lo, hi):
        """'Sand' rule: a voxel with <= 1 solid face-neighbour crumbles (and pays out)."""
        n = self.n
        lo = np.maximum(lo - 1, 0); hi = np.minimum(hi + 1, n)
        sl = tuple(slice(lo[i], hi[i]) for i in range(3))
        p = np.pad(self.solid, 1, constant_values=False)
        psl = tuple(slice(lo[i] + 1, hi[i] + 1) for i in range(3))
        cnt = np.zeros(self.solid[sl].shape, dtype=np.int8)
        for ax in range(3):
            for d in (-1, 1):
                s2 = list(psl); s2[ax] = slice(psl[ax].start + d, psl[ax].stop + d)
                cnt += p[tuple(s2)]
        loose = self.solid[sl] & (cnt <= 1)
        money = float(self.val_table[self.layer[sl]][loose].sum())
        self.solid[sl][loose] = False
        self.alive -= int(loose.sum())
        return money


# ----------------------------------------------------------------------------- tools
def use_seconds(t):
    cat = t["category"]
    base = t["cooldown"] / EFFICIENCY.get(cat, 0.8)
    return (base + SPECIAL_OVERHEAD.get(t["id"], OVERHEAD.get(cat, 0))) / UPTIME


def charge_cost(t):
    return t["price"] / t["stack"] if t["consumable"] else 0.0


def estimate_rate(t, layer_idx):
    """Analytic $/s estimate a player would 'feel' for tool t on a given layer."""
    L = LAYERS["layers"][layer_idx]
    explosive = t["category"] in ("Throwable", "Placed") or t["id"] == "RocketLauncher"
    r = max(t["radiusCells"], 0.5)
    vol = 4 / 3 * math.pi * r ** 3
    cells = max(1.0, vol * (0.55 if explosive else 0.45))
    if t["pierceCells"]:
        cells = t["pierceCells"] * max(1.0, math.pi * r * r)
    hits = max(1, t["pellets"])
    pen = 1.0 if t["tier"] >= L["tier"] else LAYERS["tierPenalty"] ** (L["tier"] - t["tier"])
    dmg = t["damage"] * (0.25 if explosive else 0.625) * pen   # mean falloff over the sphere
    killed = cells * hits * min(1.0, dmg / L["hp"])
    return (killed * L["value"] - charge_cost(t)) / use_seconds(t)


def rate_table():
    names = [L["id"] for L in LAYERS["layers"]]
    print(f"{'tool':18}{'price':>7} " + "".join(f"{n:>9}" for n in names) + "   ($/s estimate per layer)")
    for t in sorted(mining_tools(), key=lambda t: t["price"]):
        print(f"{t['id']:18}{t['price']:>7} " + "".join(f"{estimate_rate(t, i):9.1f}" for i in range(len(names))))


def mining_tools():
    return [t for t in TOOLS["tools"] if t["category"] not in ("Movement", "Utility")]


def sim_mining(players=1, seed=1, n=68, verbose=True, time_cap=3 * 3600):
    rng = random.Random(seed)
    cube = Cube(n)
    tools = mining_tools()
    movement = [t for t in TOOLS["tools"] if t["category"] == "Movement"]
    # each player state
    P = [{"money": 0.0, "money_earned": 0.0, "owned": {"Pickaxe"}, "bought": set(), "charges": {}, "t": 0.0, "use": {}} for _ in range(players)]
    total_money = 0.0
    purchases = []
    milestones = {}
    start_alive = cube.alive
    coop_scale = players ** LAYERS.get("coopValueExponent", 0.0)
    cube.hp *= players ** LAYERS.get("coopHpExponent", 0.0)
    while cube.alive > 0:
        p = min(P, key=lambda q: q["t"])       # next player to act
        if p["t"] > time_cap:
            break
        ex = cube.exposed()
        ids = np.argwhere(ex)
        if len(ids) == 0:
            break
        # players dig: favour cells in the most-exposed (outermost) layer to mimic peeling
        target = ids[rng.randrange(len(ids))]
        layer_idx = int(cube.layer[tuple(target)])

        # shopping: pick the best tool reachable within ~4 minutes of current income, save for it.
        # Consumables are restocked when they are the best owned option and we can afford a pack.
        def owned_ok(t):
            return (t["id"] in p["owned"] and not t["consumable"]) or p["charges"].get(t["id"], 0) > 0
        best_owned = max((estimate_rate(t, layer_idx) for t in tools if owned_ok(t)), default=0.0)
        income = max(p["money_earned"] / max(p["t"], 1.0), 1.0)
        horizon = p["money"] + income * 240
        cands = [t for t in tools if not owned_ok(t) and t["price"] <= horizon
                 and (t["consumable"] or t["id"] not in p["owned"])
                 and estimate_rate(t, layer_idx) > best_owned * 1.1]
        # quick wins first: self-paying consumable packs and cheap upgrades (payback < ~45s)
        quick = [t for t in cands if t["price"] <= p["money"] and (t["consumable"] or t["price"] <= income * 45)]
        if quick:
            cands = quick
        if cands:
            goal = max(cands, key=lambda t: estimate_rate(t, layer_idx))
            if goal["price"] <= p["money"]:
                p["money"] -= goal["price"]
                if goal["consumable"]:
                    p["charges"][goal["id"]] = p["charges"].get(goal["id"], 0) + goal["stack"]
                else:
                    p["owned"].add(goal["id"])
                if goal["id"] not in p["bought"]:
                    p["bought"].add(goal["id"]); purchases.append((round(p["t"]), goal["id"]))
        # movement tools are bought as luxury when rich (not modelled for income)
        for m in movement:
            if m["id"] not in p["owned"] and p["money"] > m["price"] * 3:
                p["money"] -= m["price"]; p["owned"].add(m["id"]); purchases.append((round(p["t"]), m["id"]))

        usable = [t for t in tools if (t["id"] in p["owned"] and not t["consumable"]) or p["charges"].get(t["id"], 0) > 0]
        t = max(usable, key=lambda t: estimate_rate(t, layer_idx))
        explosive = t["category"] in ("Throwable", "Placed") or t["id"] == "RocketLauncher"
        earned = 0.0
        if t["consumable"]:
            p["charges"][t["id"]] -= 1
        if t["pierceCells"]:
            # straight line through the cube from the target, toward the centre
            centre = np.array([n / 2 - 0.5] * 3)
            d = centre - target; d = d / (np.linalg.norm(d) + 1e-6)
            for k in range(t["pierceCells"]):
                c = target + d * k
                if (c < 0).any() or (c >= n).any():
                    break
                earned += cube.damage_sphere(c, t["radiusCells"], t["damage"], t["tier"], False)
        else:
            for _ in range(max(1, t["pellets"])):
                c = target + (np.array([rng.uniform(-1.5, 1.5) for _ in range(3)]) if t["pellets"] > 1 and t["category"] == "Gun" else 0)
                # explosives tend to embed a bit into the surface
                if explosive:
                    centre = np.array([n / 2 - 0.5] * 3)
                    dd = centre - target; dd = dd / (np.linalg.norm(dd) + 1e-6)
                    c = target + dd * min(1.5, t["radiusCells"] * 0.3)
                earned += cube.damage_sphere(c, t["radiusCells"], t["damage"], t["tier"], explosive)
        earned *= coop_scale
        p["money"] += earned
        p["money_earned"] += earned
        total_money += earned
        dt = use_seconds(t)
        p["t"] += dt
        p["use"][t["id"]] = p["use"].get(t["id"], 0.0) + dt
        frac_left = cube.alive / start_alive
        for m in (0.75, 0.5, 0.25, 0.1, 0.0):
            if frac_left <= m and m not in milestones:
                milestones[m] = max(q["t"] for q in P)

    end = max(q["t"] for q in P)
    if verbose:
        print(f"\n=== MINING  players={players}  cube={n}^3 ({start_alive} voxels) seed={seed}")
        print(f"cleared: {cube.alive == 0}   match time: {end/60:.1f} min   total money: ${total_money:,.0f}")
        print("progress:", "  ".join(f"{int((1-k)*100)}%@{v/60:.1f}m" for k, v in sorted(milestones.items(), reverse=True)))
        print("purchases (player 1 + others, t=seconds):")
        print("  " + ", ".join(f"{tid}@{s}s" for s, tid in sorted(purchases)))
        use = {}
        for q in P:
            for k, v in q["use"].items():
                use[k] = use.get(k, 0) + v
        tot = sum(use.values())
        print("time share by tool:", ", ".join(f"{k} {100*v/tot:.0f}%" for k, v in sorted(use.items(), key=lambda kv: -kv[1])))
    return end, cube.alive == 0


# ----------------------------------------------------------------------------- siege
def sim_siege(skill, seed=1, verbose=False):
    """Coarse 1-second tick model. Units walk to the cube, then dig. The God spends
    mana (regen + kills) on the most mana-efficient answer, with `skill` scaling
    aim/uptime. Commander buys the strongest affordable unit of its era each tick."""
    rng = random.Random(seed)
    S = SIEGE; C = S["commander"]; G = S["god"]
    units_def = {u["id"]: u for u in S["units"]}
    skills = {s["id"]: s for s in S["skills"]}
    cube = Cube(S["cubeSize"])
    core_idx = len(LAYERS["layers"]) - 1
    core_total = int((cube.layer == core_idx).sum())
    money, xp, era = C["startMoney"], 0.0, 1
    mana = G["maxMana"]
    cds = {k: 0.0 for k in skills}
    hand_cd = [0.0, 0.0]
    field = []   # live units: dict(id, hp, eta, atk_cd)
    next_wave, wave_budget = C["autoWaveInterval"], C["autoWaveBaseBudget"]
    walk_dist = 22.0
    shield_t = 0.0; slow_t = 0.0
    kills = 0; spent = 0
    ex_cache = None; ex_age = 99
    for t in range(S["matchSeconds"]):
        # --- commander income & spending (keeps a small reserve for bigger units)
        money += C["baseIncome"] + C["incomePerEra"] * (era - 1)
        while era < len(C["eraXp"]) and xp >= C["eraXp"][era]:
            era += 1
        avail = [u for u in S["units"] if u["era"] <= era]
        # spend: prefer the newest era's best unit, else swarm
        for u in sorted(avail, key=lambda u: -u["cost"]):
            while money >= u["cost"] and (u["cost"] >= 100 or rng.random() < 0.7):
                money -= u["cost"]; spent += u["cost"]; xp += u["cost"] * C["xpPerMoneySpent"]
                field.append({"u": u, "hp": u["hp"], "eta": walk_dist / u["speed"], "cd": 0.0})
                if u["cost"] < 100 and rng.random() < 0.5:
                    break
        # --- automatic waves (free units)
        if t >= next_wave:
            b = wave_budget
            while b > 0:
                u = rng.choice(avail)
                if u["cost"] > b:
                    break
                b -= u["cost"]
                field.append({"u": u, "hp": u["hp"], "eta": walk_dist / u["speed"], "cd": 0.0})
            wave_budget *= C["autoWaveGrowth"]
            next_wave += C["autoWaveInterval"]

        # --- god actions (skill scales effectiveness)
        mana = min(G["maxMana"], mana + G["manaRegen"])
        for k in cds: cds[k] = max(0, cds[k] - 1)
        hand_cd = [max(0, h - 1) for h in hand_cd]
        shield_t = max(0, shield_t - 1); slow_t = max(0, slow_t - 1)
        threats = sorted([f for f in field if f["eta"] <= 4], key=lambda f: -f["u"]["voxelDamage"])
        # two hands: grab-and-throw or slap, each hand acts ~once per second at full skill
        for h in range(2):
            if not threats or rng.random() > skill:
                continue
            f = threats[0]
            grab_cost = skills["Grab"]["mana"] * f["u"]["mass"]
            can_grab = f["u"]["grabbable"] or f["hp"] < 0.3 * f["u"]["hp"]
            if can_grab and mana >= grab_cost and cds["Grab"] <= 0 and f["hp"] > G["handSlapDamage"] * 2:
                mana -= grab_cost; cds["Grab"] = skills["Grab"]["cooldown"]
                throw_speed = 6 + 10 * skill
                f["hp"] -= G["throwDamagePerMps"] * throw_speed
                f["eta"] = walk_dist * 0.6 / f["u"]["speed"]   # thrown far away, must walk back
            elif hand_cd[h] <= 0:
                f["hp"] -= G["handSlapDamage"] * (1 + skill)
                hand_cd[h] = G["handSlapCooldown"]
            threats = [x for x in threats if x["hp"] > 0]
        # area skills when clumped
        near = [f for f in field if f["eta"] <= 6]
        if len(near) >= 6 and mana >= skills["Meteor"]["mana"] and cds["Meteor"] <= 0:
            mana -= skills["Meteor"]["mana"]; cds["Meteor"] = skills["Meteor"]["cooldown"]
            for f in rng.sample(near, max(1, int(len(near) * (0.4 + 0.4 * skill)))):
                f["hp"] -= skills["Meteor"]["damage"]
        elif len(near) >= 3 and mana >= skills["Smite"]["mana"] and cds["Smite"] <= 0 and rng.random() < skill:
            mana -= skills["Smite"]["mana"]; cds["Smite"] = skills["Smite"]["cooldown"]
            for f in rng.sample(near, max(1, min(len(near), int(1 + 3 * skill)))):
                f["hp"] -= skills["Smite"]["damage"]
        if len([f for f in field if f["eta"] <= 0]) >= 8 and mana >= skills["TimeSlow"]["mana"] and cds["TimeSlow"] <= 0:
            mana -= skills["TimeSlow"]["mana"]; cds["TimeSlow"] = skills["TimeSlow"]["cooldown"]; slow_t = skills["TimeSlow"]["duration"]
        if mana >= skills["Shield"]["mana"] + 20 and cds["Shield"] <= 0 and len(near) >= 5 and rng.random() < skill:
            mana -= skills["Shield"]["mana"]; cds["Shield"] = skills["Shield"]["cooldown"]; shield_t = skills["Shield"]["duration"]

        before = len(field)
        field = [f for f in field if f["hp"] > 0]
        k = before - len(field); kills += k; mana = min(G["maxMana"], mana + k * G["manaPerKill"])

        # --- units advance and attack
        speed_mul = 0.4 if slow_t > 0 else 1.0
        if ex_age >= 3:
            ex_cache = np.argwhere(cube.exposed()); ex_age = 0
        ex_age += 1
        for f in field:
            u = f["u"]
            reach = u["rangeM"] / u["speed"]
            if f["eta"] > reach:
                f["eta"] -= speed_mul; continue
            f["cd"] -= speed_mul
            if f["cd"] > 0 or len(ex_cache) == 0:
                continue
            f["cd"] = u["attackCooldown"] if u["attackCooldown"] > 0 else 1
            shielded = shield_t > 0 and rng.random() < 0.5 * (0.5 + skill)
            if not shielded:
                c = ex_cache[rng.randrange(len(ex_cache))]
                gain = cube.damage_sphere(c, u["attackRadiusCells"], u["voxelDamage"], u["tier"], u["attack"] in ("Explode", "Siege"))
                xp += gain * C["xpPerVoxel"]
            if u["attack"] == "Explode":
                f["hp"] = 0
        field = [f for f in field if f["hp"] > 0]
        core_left = int(((cube.layer == core_idx) & cube.solid).sum())
        if core_left == 0:
            if verbose: print(f"  skill {skill:.2f}: COMMANDER wins at {t/60:.1f} min (era {era}, kills {kills})")
            return "commander", t
    if verbose:
        integ = cube.alive / (cube.n ** 3)
        print(f"  skill {skill:.2f}: GOD survives (cube {integ*100:.0f}% intact, core {core_left}/{core_total}, era {era}, kills {kills})")
    return "god", S["matchSeconds"]


# ======================================================================================== TITAN SIEGE
# Measured in-game with `-autotest -titanbench` (AI Titan, Mac build). Update these when the
# map or the Titan's attacks change; the damage/HP/ammo numbers come from titan.json.
TITAN_BENCH = {
    # unopposed AI Titan: seconds to break wall 1, then town 1 + wall 2, then town 2 + wall 3
    "breach": [59.0, 123.0, 140.0],
    "town_share": [0.0, 0.30, 0.28],     # part of each leg spent crossing town (turrets can't reach early on)
    "dig_voxels_per_s": 4.2,              # sledgehammer on the mine wall
    "mine_cells_per_wall": 96 * 4 * 7,
    # gun DPS on the Titan at 12 m / 35 m with every shot aimed (bench), before player accuracy
    "gun_dps": {"Minigun": (204, 204), "Shotgun": (235, 126), "BoostBlaster": (75, 103), "PinballPopper": (210, 210),
                "RippleDisc": (208, 224), "FireworkLauncher": (84, 0), "BloomMortar": (120, 0), "RailGun": (350, 350)},
    "ai_block": 0.35,                      # share of RPGs the AI Titan blocks or catches (bench)
}


def load_titan():
    return load("titan.json")


def sim_titan_match(T, humans, bots, human_titan, rng, titan_skill=None, def_skill=None, verbose=False):
    """One Titan Siege match, 0.5 s steps. Returns ('titan'|'defenders', seconds, info)."""
    B = TITAN_BENCH
    dt = 0.5
    weapon_hit = {w["id"]: w["damage"] for w in T["weaponDamage"]}
    gun_dps = {}
    for gid, (near, far) in B["gun_dps"].items():
        old = {"Minigun": 8.5, "Shotgun": 21, "BoostBlaster": 27, "PinballPopper": 30, "RippleDisc": 80,
               "FireworkLauncher": 60, "BloomMortar": 120, "RailGun": 350}[gid]   # per-hit values the bench ran with
        k = weapon_hit.get(gid, old) / old
        gun_dps[gid] = ((near + far) / 2) * k
    # --- the Titan
    tsk = titan_skill if titan_skill is not None else (rng.uniform(0.3, 1.0) if human_titan else 0.5)
    speed = (0.75 + 0.6 * tsk) if human_titan else rng.uniform(0.9, 1.1)     # vs the AI bench
    block = (0.10 + 0.30 * tsk) if human_titan else B["ai_block"]                    # share of RPGs blocked or caught
    deflect = (0.05 + 0.15 * tsk) if human_titan else 0.0                    # turret shots swatted
    disrupt = (0.05 + 0.15 * tsk) if human_titan else 0.08                   # defender time lost to knocks/grabs at the wall
    hp_max = T["baseHp"] + T["hpPerDefender"] * (humans + bots * T["botHpFactor"])
    if not human_titan: hp_max *= T.get("aiHpMultiplier", 1.0)
    hp = hp_max
    last_hurt = -99.0
    # --- defenders: skill, carried ammo, guns
    D = []
    for i in range(humans + bots):
        human = i < humans
        sk = def_skill if (def_skill is not None and human) else (rng.uniform(0.35, 1.0) if human else 0.45)
        D.append({"human": human, "skill": sk, "balls": 0, "rockets": 0, "gun": None, "mined": 0,
                  "mode": "dig", "t": 0.0, "slot": i % 4})
    turrets = None
    cap = {"cannon": T["cannon"]["capacity"], "artillery": T["artillery"]["capacity"], "rocket": T["rocketPod"]["capacity"]}
    kinds = ["cannon", "artillery", "rocket", "cannon"]
    cool = [0.0] * 4
    p_gun = T["gunsPerMineWall"] / B["mine_cells_per_wall"]
    t = 0.0
    wall = 0
    leg_t = 0.0
    prep = T["prepSeconds"]
    dmg_by = {"turret": 0.0, "rpg": 0.0, "gun": 0.0}
    while True:
        fighting = t >= prep
        if turrets is None:
            turrets = [0] * 4
        # ---- the Titan advances (rage speeds it up)
        frac = hp / hp_max
        rage = 2 if frac <= 0.25 else 1 if frac <= 0.5 else 0
        if fighting:
            leg_t += dt * speed * (1 + 0.3 * rage)
            if leg_t >= B["breach"][wall]:
                wall += 1; leg_t = 0.0
                if wall >= 3:
                    return "titan", t, dict(hp=hp / hp_max, dmg=dmg_by)
                turrets = [0] * 4      # the old wall's turrets are gone; defenders move up
                for d in D: d["mode"] = "dig"
        in_range = fighting and leg_t >= B["breach"][wall] * B["town_share"][wall]
        at_wall = fighting and leg_t >= B["breach"][wall] * (B["town_share"][wall] + (1 - B["town_share"][wall]) * 0.15)
        # ---- defenders
        for d in D:
            eff = 0.55 + 0.45 * d["skill"]
            if at_wall and rng.random() < disrupt * dt: d["t"] = max(d["t"], 4.0)   # knocked off / grabbed: a few seconds lost
            if d["t"] > 0: d["t"] -= dt; continue
            k = kinds[d["slot"]]
            need_rocket = k == "rocket"
            carry = d["rockets"] if need_rocket else d["balls"]
            # a human with a gun shoots whenever the Titan is in range and they carry nothing to load
            if d["gun"] and in_range and carry == 0 and rng.random() < 0.6:
                dmg = gun_dps[d["gun"]] * (0.35 + 0.5 * d["skill"]) * dt
                if rng.random() < 0.05 * d["skill"]: dmg *= T["napeMultiplier"]
                hp -= dmg; dmg_by["gun"] += dmg; last_hurt = t
                continue
            if d["mode"] == "dig":
                v = B["dig_voxels_per_s"] * eff * dt
                for _ in range(int(v) + (1 if rng.random() < v - int(v) else 0)):
                    r = rng.random()
                    if r < T["ballChance"]: d["balls"] = min(T["maxBalls"], d["balls"] + 1)
                    elif r < T["ballChance"] + T["rocketChance"]: d["rockets"] = min(T["maxRockets"], d["rockets"] + 1)
                    if d["human"] and d["gun"] is None and rng.random() < p_gun:
                        d["gun"] = rng.choice(list(gun_dps))
                carry = d["rockets"] if need_rocket else d["balls"]
                full = turrets[d["slot"]] >= cap[k]
                if carry >= 4 or (carry > 0 and in_range and not full):
                    d["mode"] = "haul"; d["t"] = 2.0 / eff        # walk to the turret
            elif d["mode"] == "haul":
                if carry > 0 and turrets[d["slot"]] < cap[k]:
                    turrets[d["slot"]] += 1
                    if need_rocket: d["rockets"] -= 1
                    else: d["balls"] -= 1
                    d["t"] = 0.3
                elif in_range and turrets[d["slot"]] > 0:
                    d["mode"] = "fire"
                else:
                    d["mode"] = "dig"; d["t"] = 2.0 / eff
            if d["mode"] == "fire":
                s = d["slot"]
                if turrets[s] > 0 and cool[s] <= 0 and in_range:
                    turrets[s] -= 1
                    cool[s] = T[{"cannon": "cannon", "artillery": "artillery", "rocket": "rocketPod"}[k]]["cooldown"]
                    hit = rng.random() < (0.92 if k == "rocket" else 0.8 * (0.7 + 0.3 * d["skill"])) and rng.random() >= deflect
                    if hit:
                        dmg = T[{"cannon": "cannon", "artillery": "artillery", "rocket": "rocketPod"}[k]]["damage"]
                        if rng.random() < 0.04: dmg *= T["napeMultiplier"]
                        hp -= dmg; dmg_by["turret"] += dmg; last_hurt = t
                # RPG between turret shots
                if d["rockets"] > (2 if k == "rocket" else 0) and in_range and rng.random() < dt / 1.5:
                    d["rockets"] -= 1
                    if rng.random() < 0.55 + 0.35 * d["skill"]:
                        dmg = T["rpgDamage"] * (T["blockMultiplier"] if rng.random() < block else 1.0)
                        if rng.random() < 0.06 * d["skill"]: dmg *= T["napeMultiplier"]
                        hp -= dmg; dmg_by["rpg"] += dmg; last_hurt = t
                if turrets[s] == 0 and d["rockets"] <= (2 if k == "rocket" else 0):
                    d["mode"] = "dig"; d["t"] = 2.0 / eff
        for i in range(4): cool[i] -= dt
        # ---- regen
        if fighting and hp < hp_max and t - last_hurt > T["regenDelay"]:
            hp = min(hp_max, hp + T["regenPerSecond"] * dt)
        if hp <= 0:
            return "defenders", t, dict(wall=wall, dmg=dmg_by)
        t += dt
        if t >= prep + T["matchSeconds"]:
            return "defenders", t, dict(wall=wall, dmg=dmg_by, timeout=True)


TITAN_SCENARIOS = [
    ("VR Titan vs you + 4 bots", 1, 4, True),
    ("VR Titan vs 3 people + 2 bots", 3, 2, True),
    ("VR Titan vs 5 people", 5, 0, True),
    ("AI Titan vs you + 4 bots", 1, 4, False),
    ("AI Titan vs 3 people + 2 bots", 3, 2, False),
]


def sim_titan(runs=2000, seed=1, T=None, quiet=False):
    T = T or load_titan()
    rng = random.Random(seed)
    out = []
    for name, h, b, human_titan in TITAN_SCENARIOS:
        tw = 0; times = []; timeouts = 0; walls = [0, 0, 0, 0]; parts = {"turret": 0, "rpg": 0, "gun": 0}
        for _ in range(runs):
            who, tt, info = sim_titan_match(T, h, b, human_titan, rng)
            tw += who == "titan"; times.append(tt)
            if info.get("timeout"): timeouts += 1
            walls[info.get("wall", 3)] += 1
            for k in parts: parts[k] += info["dmg"][k]
        total = sum(parts.values()) or 1
        rate = tw / runs
        out.append((name, rate))
        if not quiet:
            print(f"  {name:32s} Titan wins {100*rate:4.0f}%   avg match {sum(times)/runs/60:4.1f} min   "
                  f"defender wins at wall 1/2/3: {walls[0]*100//runs}/{walls[1]*100//runs}/{walls[2]*100//runs}%  "
                  f"timeouts {100*timeouts//runs}%   damage: turrets {100*parts['turret']//total}% rpg {100*parts['rpg']//total}% guns {100*parts['gun']//total}%")
    return out


def titan_skill_table(runs=1000, seed=2, T=None):
    """Titan win rate by Titan skill vs average defenders (you + 4 bots)."""
    T = T or load_titan()
    rng = random.Random(seed)
    print("  Titan skill vs (you + 4 bots, your skill 0.6):")
    for sk in (0.2, 0.4, 0.6, 0.8, 1.0):
        w = sum(sim_titan_match(T, 1, 4, True, rng, titan_skill=sk, def_skill=0.6)[0] == "titan" for _ in range(runs))
        print(f"    titan skill {sk:.1f}: wins {100*w/runs:3.0f}%")


def tune_titan(runs=600, write=False):
    """Fit Titan health so every line-up sits near 50%: scale base+per-defender HP for VR Titans
    (then tilt between small and large teams), then the AI Titan's HP multiplier."""
    T = load_titan()
    def rate(h, b, human, seed):
        rng = random.Random(seed)
        return sum(sim_titan_match(T, h, b, human, rng)[0] == "titan" for _ in range(runs)) / runs
    def bisect(apply, lo, hi, measure, steps=9):
        for _ in range(steps):
            mid = (lo + hi) / 2
            apply(mid)
            if measure() > 0.5: hi = mid      # Titan winning too often: less HP
            else: lo = mid
        apply((lo + hi) / 2)
        return (lo + hi) / 2
    base0, per0 = T["baseHp"], T["hpPerDefender"]
    for it in range(3):
        # overall HP level for VR Titans (average of small and large teams)
        b0, p0 = T["baseHp"], T["hpPerDefender"]
        bisect(lambda k: T.update(baseHp=b0 * k, hpPerDefender=p0 * k), 0.2, 6.0,
               lambda: (rate(1, 4, True, 11) + rate(5, 0, True, 12)) / 2)
        # tilt: if big teams win more than small teams, more HP per defender (and less base)
        small, big = rate(1, 4, True, 13), rate(5, 0, True, 14)
        tilt = 1.0 + max(-0.4, min(0.4, (small - big) * 1.5))
        total5 = T["baseHp"] + 5 * T["hpPerDefender"]   # keep the 5-person total roughly where it was
        T["hpPerDefender"] *= tilt
        T["baseHp"] = max(1000.0, total5 - 5 * T["hpPerDefender"] * (1 + 0.0))
        print(f"  pass {it + 1}: baseHp {T['baseHp']:.0f}  hpPerDefender {T['hpPerDefender']:.0f}   (1+4 bots {100*small:.0f}%, 5 people {100*big:.0f}%)")
    bisect(lambda m: T.update(aiHpMultiplier=m), 0.3, 3.0, lambda: rate(1, 4, False, 15))
    for key in ("baseHp", "hpPerDefender"):
        T[key] = float(round(T[key] / 100.0) * 100)
    T["aiHpMultiplier"] = round(T["aiHpMultiplier"], 2)
    print(f"  fitted: baseHp {T['baseHp']:.0f}, hpPerDefender {T['hpPerDefender']:.0f}, aiHpMultiplier {T['aiHpMultiplier']}")
    sim_titan(runs=1500, T=T)
    titan_skill_table(T=T)
    if write:
        path = os.path.join(BAL, "titan.json")
        with open(path) as f: d = json.load(f)
        for key in ("baseHp", "hpPerDefender", "aiHpMultiplier"): d[key] = T[key]
        with open(path, "w") as f: json.dump(d, f, indent=2)
        print("  wrote", path)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["mining", "siege", "titan", "titantune", "all", "table"])
    ap.add_argument("--write", action="store_true", help="titantune: save the fitted numbers to titan.json")
    ap.add_argument("--players", type=int, default=0)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--runs", type=int, default=8)
    ap.add_argument("--size", type=int, default=68, help="mining cube edge in voxels (game default 68)")
    a = ap.parse_args()
    if a.mode in ("table", "all"):
        rate_table()
    if a.mode in ("mining", "all"):
        for pl in ([a.players] if a.players else [1, 2, 4]):
            sim_mining(players=pl, seed=a.seed, n=a.size)
    if a.mode in ("siege", "all"):
        print("\n=== GOD SIEGE  (God win-rate by skill; target: novice loses, competent God (0.6) wins ~2/3, expert wins)")
        for skill in (0.3, 0.45, 0.6, 0.75, 0.9):
            wins = 0; times = []
            for r in range(a.runs):
                w, tt = sim_siege(skill, seed=r)
                wins += w == "god"; times.append(tt)
            print(f"  skill {skill:.2f}: God wins {100*wins/a.runs:3.0f}%   avg end {sum(times)/len(times)/60:.1f} min")
        sim_siege(0.6, seed=0, verbose=True)
    if a.mode == "titantune":
        print("\n=== TITAN SIEGE tuning (fits HP for ~50% Titan wins)")
        tune_titan(write=a.write)
    if a.mode in ("titan", "all"):
        print("\n=== TITAN SIEGE  (target: ~50% Titan wins in every line-up; matches ~5-8 min)")
        sim_titan(runs=max(a.runs, 2000) if a.mode == "titan" else 500)
        titan_skill_table()


if __name__ == "__main__":
    main()
