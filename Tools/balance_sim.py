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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["mining", "siege", "all", "table"])
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


if __name__ == "__main__":
    main()
