"""RTS enemy units: Buddy variants with props.

Output: Assets/Cubestruction/Models/Units/<Id>.fbx
  * Root empty named <Id> at the feet / ground, facing Blender -Y (Unity +Z).
  * Humanoid units use the Buddy part contract (Head, Torso, ArmL, ArmR, LegL, LegR,
    same pivots) so the ragdoll code works unchanged. Props are merged into the part
    that carries them (hand-held props into ArmR, hats/fuses/crowns into Head).
  * Body parts keep the `Buddy_Body` / `Buddy_Head` material names (coloured per unit)
    so tinting code can find them; props use `Unit_*` materials.
  * CannonCart has a single `Body` part.
"""

import math
import os
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import common as C  # noqa: E402  (imports bpy first, which provides mathutils)
from mathutils import Vector  # noqa: E402
from buddy import make_buddy  # noqa: E402
from common import rot  # noqa: E402

DK, PROP, GOLD, GLOW, SPARK, ACC = "Unit_Dark", "Unit_Prop", "Unit_Gold", "Unit_Glow", "Unit_Spark", "Unit_Accent"


def on_head(geo, d, out=0.0):
    d = Vector(d).normalized()
    return tuple(Vector(geo["head_center"]) + d * (geo["head_r"] + out))


def eyes(part, geo, wink=False, square=False):
    for sx in (-1, 1):
        p = on_head(geo, (sx * 0.36, -1, 0.18), -0.012)
        if square:
            part.box(p, (0.035, 0.02, 0.045), DK, bevel=0.006)
        elif wink and sx == 1:
            part.capsule((p[0] - 0.02, p[1], p[2]), (p[0] + 0.02, p[1], p[2] + 0.004), 0.008, DK,
                         segs=6, hemi=1)
        else:
            part.sphere(p, 0.022, DK, segs=8, rings=5, scale=(0.85, 0.55, 1.2))


def smile(part, geo, n=5, width=0.09):
    for i in range(n):
        t = (i / (n - 1) - 0.5) * 2
        p = on_head(geo, (t * width * 4, -1, -0.32 - 0.12 * (1 - t * t)), -0.006)
        part.sphere(p, 0.011, DK, segs=6, rings=4)


def hand(geo, side="R"):
    return Vector(geo["shoulders"][side][1])


# ---------------------------------------------------------------------------

def smiley_scrapper():
    C.COLOR_OVERRIDES.update({"Buddy_Body": "#FFC93B", "Buddy_Head": "#FFE066"})

    def props(parts, geo):
        eyes(parts["Head"], geo)
        smile(parts["Head"], geo)
        h = hand(geo)
        a = h + Vector((0, 0.06, -0.03))
        b = h + Vector((0, -0.2, 0.1))
        parts["ArmR"].cyl(a, b, 0.012, ACC, segs=8)
        parts["ArmR"].box(tuple(b), (0.03, 0.03, 0.03), PROP, bevel=0.008)
        up = Vector((0, 0.35, 0.94)).normalized()
        for s in (1, -1):
            parts["ArmR"].cyl(b, b + up * 0.1 * s + Vector((0, 0.03, 0)), 0.013, PROP, r2=0.0, segs=6)

    return make_buddy("SmileyScrapper", scale=0.72, props=props)


def wink_bomber():
    C.COLOR_OVERRIDES.update({"Buddy_Body": "#FF7BC0", "Buddy_Head": "#FF9AD2"})

    def props(parts, geo):
        eyes(parts["Head"], geo, wink=True)
        smile(parts["Head"], geo, n=4, width=0.07)
        top = Vector(on_head(geo, (0, 0, 1), -0.01))
        parts["Head"].cyl(top, top + Vector((0, 0, 0.03)), 0.03, PROP, segs=10)
        mid = top + Vector((0.02, 0.0, 0.09))
        tip = top + Vector((0.06, 0.02, 0.13))
        parts["Head"].cyl(top + Vector((0, 0, 0.03)), mid, 0.008, DK, segs=6)
        parts["Head"].cyl(mid, tip, 0.008, DK, segs=6)
        parts["Head"].sphere(tuple(tip), 0.025, SPARK, segs=8, rings=5)
        # a round bomb held in the right hand
        h = hand(geo)
        parts["ArmR"].sphere(tuple(h + Vector((0, -0.05, 0.03))), 0.07, DK, segs=12, rings=7)
        parts["ArmR"].cyl(h + Vector((0, -0.05, 0.095)), h + Vector((0, -0.05, 0.12)), 0.02, PROP, segs=8)

    return make_buddy("WinkBomber", scale=0.85, torso="round", width=1.15, depth=0.95, props=props)


def pixel_archer():
    C.COLOR_OVERRIDES.update({"Buddy_Body": "#2FC6DA", "Buddy_Head": "#7FE8F2"})

    def props(parts, geo):
        hc = Vector(geo["head_center"])
        r = geo["head_r"]
        for sx in (-1, 1):
            parts["Head"].box(tuple(hc + Vector((sx * 0.06, -r * 0.95, 0.03))), (0.04, 0.02, 0.05), DK,
                              bevel=0.004)
        parts["Head"].box(tuple(hc + Vector((0, -r * 0.95, -0.06))), (0.08, 0.02, 0.02), DK, bevel=0.004)
        # small blaster held in the right hand, pointing forward
        h = hand(geo)
        parts["ArmR"].box(tuple(h + Vector((0, -0.06, 0.04))), (0.05, 0.16, 0.06), "Unit_Accent",
                          bevel=0.018)
        parts["ArmR"].cyl(h + Vector((0, -0.14, 0.05)), h + Vector((0, -0.22, 0.05)), 0.016, PROP, segs=8)
        parts["ArmR"].sphere(tuple(h + Vector((0, -0.225, 0.05))), 0.016, GLOW, segs=8, rings=4)

    return make_buddy("PixelArcher", scale=0.9, head="cube", head_r=0.16, props=props)


def block_brute():
    C.COLOR_OVERRIDES.update({"Buddy_Body": "#8A6BE8", "Buddy_Head": "#A98CFF"})

    def props(parts, geo):
        hc = Vector(geo["head_center"])
        r = geo["head_r"]
        for sx in (-1, 1):  # grumpy brow + eyes
            parts["Head"].box(tuple(hc + Vector((sx * 0.05, -r * 0.92, 0.02))), (0.035, 0.02, 0.03), DK,
                              bevel=0.005)
            parts["Head"].box(tuple(hc + Vector((sx * 0.05, -r * 0.9, 0.06))), (0.06, 0.02, 0.014), DK,
                              bevel=0.004, matrix=rot(y=sx * 18))
        for side, sx in (("L", 1), ("R", -1)):  # shoulder pads
            sh = Vector(geo["shoulders"][side][0])
            parts["Arm" + side].sphere(tuple(sh + Vector((sx * 0.03, 0, 0.02))), 0.08, PROP,
                                       segs=10, rings=6, scale=(1, 1, 0.7))
            h = hand(geo, side)  # big knuckle bands
            parts["Arm" + side].torus(tuple(h + Vector((0, 0, 0.04))), 0.07, 0.014, PROP, segs=12, minor=4)

    return make_buddy("BlockBrute", scale=1.45, torso="box", head_r=0.14, width=1.4, depth=0.9,
                      limb=1.5, props=props)


def mega_buddy():
    C.COLOR_OVERRIDES.update({"Buddy_Body": "#3A7BFF", "Buddy_Head": "#6FA4FF"})

    def props(parts, geo):
        eyes(parts["Head"], geo)
        smile(parts["Head"], geo, n=6)
        # crown
        top = Vector(on_head(geo, (0, 0, 1), -0.045))
        parts["Head"].cyl(top, top + Vector((0, 0, 0.05)), 0.1, GOLD, segs=16, caps=False)
        parts["Head"].cyl(top + Vector((0, 0, 0.05)), top, 0.092, GOLD, segs=16, caps=False)  # inner wall
        parts["Head"].torus(tuple(top), 0.1, 0.01, GOLD, segs=16, minor=4)
        for i in range(5):
            t = 2 * math.pi * i / 5 - math.pi / 2
            base = top + Vector((0.095 * math.cos(t), 0.095 * math.sin(t), 0.045))
            tipp = base + Vector((0.01 * math.cos(t), 0.01 * math.sin(t), 0.06))
            parts["Head"].cyl(base, tipp, 0.022, GOLD, r2=0.0, segs=6)
            parts["Head"].sphere(tuple(tipp), 0.012, GOLD, segs=6, rings=4)
        parts["Head"].sphere(tuple(top + Vector((0, -0.1, 0.025))), 0.018, "Unit_Accent", segs=8, rings=5)
        # cape-ish collar
        parts["Torso"].torus((0, 0, geo["top"] - 0.07), 0.12, 0.03, GOLD, segs=16, minor=5)

    return make_buddy("MegaBuddy", scale=2.4, width=1.1, limb=1.2, props=props)


def cannon_cart():
    root = C.make_empty("CannonCart")
    p = C.Part()
    RIM, WHEEL = "Unit_Accent", "Unit_Wheel"
    p.box((0, 0, 0.3), (0.5, 0.7, 0.2), "Buddy_Body", bevel=0.06, segments=2)
    p.box((0, 0, 0.41), (0.52, 0.72, 0.04), PROP, bevel=0.018)
    for x in (-0.27, 0.27):
        for y in (-0.22, 0.22):
            p.cyl((x * 0.95, y, 0.15), (x * 1.12, y, 0.15), 0.15, WHEEL, segs=14)
            p.torus((x * 1.12, y, 0.15), 0.12, 0.03, RIM, matrix=rot(y=90), segs=14, minor=4)
            p.sphere((x * 1.14, y, 0.15), 0.04, PROP, segs=8, rings=4)
    # cannon on a ball mount, aimed forward (-Y) and slightly up
    p.sphere((0, 0.05, 0.5), 0.15, "Buddy_Head", segs=14, rings=8)
    d = Vector((0, -math.cos(math.radians(18)), math.sin(math.radians(18))))
    a = Vector((0, 0.05, 0.52))
    p.cyl(a, a + d * 0.5, 0.07, DK, r2=0.06, segs=14)
    p.torus(tuple(a + d * 0.5), 0.068, 0.018, PROP, matrix=rot(x=90 - 18), segs=14, minor=4)
    p.torus(tuple(a + d * 0.25), 0.072, 0.012, GLOW, matrix=rot(x=90 - 18), segs=14, minor=4)
    C.COLOR_OVERRIDES.update({"Buddy_Body": "#8EE33A", "Buddy_Head": "#F4F8FF"})
    C.make_object("Body", p, (0, 0, 0.35), parent=root)
    return root, None


UNITS = {
    "SmileyScrapper": smiley_scrapper,
    "WinkBomber": wink_bomber,
    "PixelArcher": pixel_archer,
    "BlockBrute": block_brute,
    "CannonCart": cannon_cart,
    "MegaBuddy": mega_buddy,
}


def build(ids=None):
    for uid in ids or UNITS:
        C.clear_scene()
        root, _ = UNITS[uid]()
        C.export_fbx(root, os.path.join("Units", uid + ".fbx"))


if __name__ == "__main__":
    build()
    C.print_report()
