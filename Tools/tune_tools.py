#!/usr/bin/env python3
"""Fits each tool's damage (and radius if needed) so its estimated $/s on its
'home' layer hits a target that rises with price. Rewrites tools.json in place.
Targets are the design intent; balance_sim.py mining then validates on a real grid."""
import json, os, sys
sys.path.insert(0, os.path.dirname(__file__))
import balance_sim as B

# id: (home layer index, target $/s)
TARGETS = {
    "Pickaxe": (0, 6), "Shovel": (0, 14), "Grenade": (1, 30), "SledgeHammer": (1, 24),
    "StickyBomb": (2, 50), "Dynamite": (2, 70), "TNT": (3, 130), "Drill": (2, 40),
    "Shotgun": (1, 30), "MiningLaser": (2, 45), "C4": (3, 180), "Jackhammer": (3, 110),
    "BlackHoleGrenade": (4, 320), "Minigun": (2, 115), "PlasmaCutter": (3, 125),
    "RocketLauncher": (3, 150), "SonicHammer": (3, 230), "RailGun": (4, 300), "NukeCrate": (4, 700),
}
path = os.path.join(B.BAL, "tools.json")
data = json.load(open(path))
def fit(t, key, layer, target, lo, hi, integer):
    for _ in range(60):
        mid = (lo + hi) / 2; t[key] = mid
        if B.estimate_rate(t, layer) < target: lo = mid
        else: hi = mid
    t[key] = round(hi) if integer else round(hi, 2)

for t in data["tools"]:
    if t["id"] not in TARGETS: continue
    layer, target = TARGETS[t["id"]]
    hp = B.LAYERS["layers"][layer]["hp"]
    continuous = t["cooldown"] <= 0.12 or t["pellets"] > 1
    if continuous:
        fit(t, "damage", layer, target, 1, 2000, True)
    else:
        # single-hit tools must one-shot a home-layer voxel at the centre: fix damage, fit radius
        t["damage"] = int(round(hp * 1.15))
        fit(t, "radiusCells", layer, target, 0.5, 16, False)
        if B.estimate_rate(t, layer) > target * 1.1:      # even the minimum radius is too strong
            t["radiusCells"] = 0.5
            fit(t, "damage", layer, target, 1, 6000, True)
    print(f"{t['id']:18} dmg {t['damage']:5} r {t['radiusCells']:5}  -> {B.estimate_rate(t, layer):7.1f} $/s on {B.LAYERS['layers'][layer]['id']}")
text = json.dumps(data, indent=2)
open(path, "w").write(text + "\n")
