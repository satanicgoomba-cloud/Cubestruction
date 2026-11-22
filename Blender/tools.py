"""Hand-held tools / weapons / gadgets.

Output: Assets/Cubestruction/Models/Tools/<Id>.fbx, one file per tool.
  * Each file is a single mesh object named exactly <Id> (multi-material).
  * Origin = grip point (where the hand holds it). For wearables (Jetpack,
    RocketBoots, ClimbingGloves) origin = attach point (back centre, ankles, palm).
  * Tools point along Blender -Y (Unity +Z after export), top is Blender +Z.
  * Materials are named Tool_<Something>.
"""

import math
import os
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import common as C  # noqa: E402
from common import rot  # noqa: E402

CH, AQ, LI, SK, WH, DK = "Tool_Chrome", "Tool_Aqua", "Tool_Lime", "Tool_Sky", "Tool_White", "Tool_Dark"
RD, OR, YE, PU, VO = "Tool_Red", "Tool_Orange", "Tool_Yellow", "Tool_Purple", "Tool_Void"
GL, GG, GR = "Tool_Glow", "Tool_GlowGreen", "Tool_GlowRed"


# ---------------------------------------------------------------------------
# shared bits
# ---------------------------------------------------------------------------

def long_handle(p, length=0.62, r=0.02, mat=LI, back=0.12):
    """Handle running from behind the hand (+Y) forward to -length."""
    p.cyl((0, back, 0), (0, -length, 0), r, mat, segs=10)
    p.sphere((0, back, 0), r * 1.6, CH, segs=10, rings=6)          # pommel
    for y in (0.05, -0.05):                                       # grip wraps
        p.cyl((0, y + 0.025, 0), (0, y - 0.025, 0), r * 1.25, DK, segs=10)


def pistol_grip(p, mat=DK, h=0.13, y=0.012):
    p.box((0, y, -0.045), (0.034, 0.05, h), mat, bevel=0.014, matrix=rot(x=14))


def trigger_guard(p, y=-0.035, z=0.0):
    p.torus((0, y, z), 0.026, 0.006, DK, matrix=rot(y=90), segs=10, minor=4)


# ---------------------------------------------------------------------------
# digging tools
# ---------------------------------------------------------------------------

def pickaxe(p):
    long_handle(p, 0.6)
    hy = -0.6
    p.box((0, hy, 0), (0.06, 0.07, 0.08), AQ, bevel=0.02)
    for s in (1, -1):  # two curved picks (two tapered segments each)
        mid = (0, hy + 0.035, s * 0.14)
        tip = (0, hy + 0.1, s * 0.27)
        p.cyl((0, hy, s * 0.03), mid, 0.03, CH, r2=0.022, segs=8)
        p.sphere(mid, 0.022, CH, segs=8, rings=4)
        p.cyl(mid, tip, 0.022, CH, r2=0.0, segs=8)


def shovel(p):
    long_handle(p, 0.62)
    # D-handle at the back
    p.torus((0, 0.19, 0), 0.055, 0.012, CH, matrix=rot(y=90), segs=12, minor=5)
    p.cyl((0, -0.62, 0), (0, -0.7, 0), 0.03, CH, r2=0.045, segs=10)
    # blade: flattened rounded slab + pointed tip
    p.box((0, -0.82, 0), (0.22, 0.26, 0.03), SK, bevel=0.014)
    p.sphere((0, -0.95, 0), 0.11, SK, segs=12, rings=6, scale=(1.0, 0.9, 0.14))
    for s in (1, -1):  # raised side lips so the blade reads as a scoop
        p.capsule((s * 0.105, -0.7, 0.012), (s * 0.1, -0.93, 0.012), 0.012, SK, segs=6, hemi=2)
    p.box((0, -0.72, 0.012), (0.12, 0.05, 0.02), CH, bevel=0.008)


def sledgehammer(p):
    long_handle(p, 0.66, r=0.022)
    hy = -0.7
    p.box((0, hy, 0), (0.13, 0.13, 0.26), CH, bevel=0.03, segments=2)
    for s in (1, -1):
        p.cyl((0, hy, s * 0.13), (0, hy, s * 0.165), 0.06, LI, r2=0.055, segs=12)
    p.cyl((0, hy + 0.07, 0), (0, hy + 0.09, 0), 0.04, AQ, segs=10)


def drill(p):
    pistol_grip(p, SK)
    p.box((0, 0.03, -0.12), (0.06, 0.09, 0.05), LI, bevel=0.015)          # battery
    p.capsule((0, 0.07, 0.065), (0, -0.09, 0.065), 0.045, SK, segs=12)   # motor body
    p.box((0, 0.0, 0.03), (0.05, 0.08, 0.02), WH, bevel=0.008)
    trigger_guard(p, -0.03, 0.0)
    p.cyl((0, -0.12, 0.065), (0, -0.17, 0.065), 0.038, CH, r2=0.025, segs=12)  # chuck
    p.cyl((0, -0.17, 0.065), (0, -0.33, 0.065), 0.009, AQ, r2=0.0, segs=8)     # bit
    p.torus((0, -0.15, 0.065), 0.034, 0.006, AQ, matrix=rot(x=90), segs=12, minor=4)


def jackhammer(p):
    # T-bar handles at the grip, heavy body forward
    p.cyl((-0.2, 0, 0), (0.2, 0, 0), 0.02, CH, segs=10)
    for s in (1, -1):
        p.capsule((s * 0.12, 0, 0), (s * 0.2, 0, 0), 0.028, DK, segs=10)
    p.cyl((0, 0.0, 0), (0, -0.08, 0), 0.04, CH, segs=12)
    p.capsule((0, -0.1, 0), (0, -0.42, 0), 0.085, YE, segs=14)
    for y in (-0.18, -0.26, -0.34):
        p.torus((0, y, 0), 0.088, 0.012, OR, matrix=rot(x=90), segs=14, minor=4)
    p.cyl((0, -0.5, 0), (0, -0.58, 0), 0.04, CH, segs=12)
    p.cyl((0, -0.58, 0), (0, -0.78, 0), 0.022, CH, r2=0.0, segs=10)


# ---------------------------------------------------------------------------
# beam tools
# ---------------------------------------------------------------------------

def mining_laser(p):
    pistol_grip(p, DK)
    trigger_guard(p)
    p.box((0, -0.02, 0.06), (0.07, 0.24, 0.075), WH, bevel=0.03)
    p.cyl((0, -0.14, 0.06), (0, -0.22, 0.06), 0.04, AQ, r2=0.034, segs=12)
    p.sphere((0, -0.235, 0.06), 0.03, GL, segs=12, rings=6)
    p.torus((0, -0.2, 0.06), 0.042, 0.008, LI, matrix=rot(x=90), segs=12, minor=4)
    p.capsule((0, 0.05, 0.12), (0, -0.08, 0.12), 0.018, AQ, segs=8)  # glass sight
    for s in (1, -1):  # fins
        p.box((s * 0.045, 0.04, 0.06), (0.02, 0.07, 0.05), LI, bevel=0.008)


def plasma_cutter(p):
    pistol_grip(p, DK)
    trigger_guard(p)
    p.capsule((0, 0.06, 0.06), (0, -0.1, 0.06), 0.04, SK, segs=12)
    p.cyl((0, -0.12, 0.06), (0, -0.26, 0.06), 0.022, CH, r2=0.014, segs=10)
    p.cyl((0, -0.26, 0.06), (0, -0.31, 0.06), 0.014, GL, r2=0.004, segs=8)
    p.sphere((0.055, 0.0, 0.08), 0.04, AQ, segs=12, rings=6)       # plasma tank
    p.sphere((0.055, 0.0, 0.08), 0.022, PU, segs=8, rings=5)
    p.torus((0, -0.13, 0.06), 0.03, 0.007, LI, matrix=rot(x=90), segs=10, minor=4)


# ---------------------------------------------------------------------------
# explosives (origin = centre of the throwable / hand position)
# ---------------------------------------------------------------------------

def grenade(p):
    p.sphere((0, 0, 0), 0.05, LI, segs=14, rings=9, scale=(1, 1, 1.15))
    for z in (-0.02, 0.02):
        p.torus((0, 0, z), 0.05, 0.005, DK, segs=14, minor=4)
    p.cyl((0, 0, 0.05), (0, 0, 0.075), 0.018, CH, segs=10)
    p.box((0, 0.025, 0.06), (0.016, 0.05, 0.01), CH, bevel=0.004, matrix=rot(x=-60))  # spoon
    p.torus((0.025, 0, 0.078), 0.014, 0.003, CH, matrix=rot(x=90), segs=10, minor=4)  # pin ring


def sticky_bomb(p):
    p.sphere((0, 0, 0), 0.06, AQ, segs=14, rings=9)
    p.sphere((0, 0, 0), 0.036, GG, segs=10, rings=6)
    for a in range(5):  # goo blobs
        t = 2 * math.pi * a / 5
        p.sphere((0.052 * math.cos(t), 0.052 * math.sin(t), -0.03), 0.018, LI, segs=8, rings=5)
    p.cyl((0, 0, 0.055), (0, 0, 0.07), 0.016, CH, segs=10)
    p.sphere((0, 0, 0.072), 0.011, GR, segs=8, rings=5)


def dynamite(p):
    for x, z in ((-0.022, 0), (0.022, 0), (0, 0.036)):
        p.capsule((x, 0.09, z), (x, -0.09, z), 0.021, RD, segs=10, hemi=2)
    for y in (0.05, -0.05):
        p.box((0, y, 0.012), (0.09, 0.018, 0.085), WH, bevel=0.03, segments=2)
    p.cyl((0, -0.11, 0.036), (0, -0.16, 0.05), 0.004, DK, segs=5)
    p.sphere((0, -0.162, 0.051), 0.009, GL, segs=8, rings=4)


def tnt(p):
    # origin at the carry handle on top
    p.box((0, 0, -0.16), (0.3, 0.22, 0.26), RD, bevel=0.03)
    p.box((0, 0, -0.16), (0.305, 0.225, 0.06), WH, bevel=0.02)
    for x in (-0.11, 0.11):
        p.box((x, 0, -0.16), (0.04, 0.226, 0.262), DK, bevel=0.015)
    p.torus((0, 0, -0.02), 0.05, 0.012, CH, matrix=rot(x=90), segs=12, minor=5)


def c4(p):
    p.box((0, 0, 0), (0.12, 0.18, 0.045), LI, bevel=0.012)
    p.box((0, 0.02, 0.028), (0.08, 0.08, 0.016), DK, bevel=0.006)
    p.box((0, 0.02, 0.037), (0.06, 0.055, 0.004), GL, bevel=0.0)
    p.sphere((0.035, -0.05, 0.03), 0.01, GR, segs=8, rings=4)
    for x, mat in ((-0.03, RD), (0.0, SK), (0.03, YE)):
        p.capsule((x, -0.04, 0.025), (x * 0.6, -0.1, 0.03), 0.004, mat, segs=5, hemi=1)
    for y in (-0.06, 0.06):
        p.box((0, y, 0), (0.124, 0.015, 0.049), WH, bevel=0.005)


def detonator(p):
    p.box((0, 0, 0), (0.07, 0.15, 0.045), SK, bevel=0.02)
    p.cyl((0, 0, 0.022), (0, 0, 0.035), 0.022, CH, segs=12)
    p.sphere((0, 0, 0.035), 0.018, GR, segs=12, rings=6, scale=(1, 1, 0.6))
    p.box((0, 0.045, 0.024), (0.04, 0.035, 0.006), GL, bevel=0.0)
    p.cyl((0.022, -0.07, 0.01), (0.022, -0.17, 0.01), 0.004, CH, segs=6)
    p.sphere((0.022, -0.172, 0.01), 0.009, LI, segs=8, rings=4)


# ---------------------------------------------------------------------------
# guns
# ---------------------------------------------------------------------------

def shotgun(p):
    pistol_grip(p, DK)
    trigger_guard(p)
    p.box((0, -0.04, 0.045), (0.055, 0.18, 0.07), SK, bevel=0.02)                 # receiver
    p.box((0, 0.2, 0.02), (0.05, 0.32, 0.07), WH, bevel=0.025, matrix=rot(x=-8))  # stock
    p.box((0, 0.34, 0.0), (0.056, 0.05, 0.11), LI, bevel=0.02, matrix=rot(x=-8))  # butt pad
    for x in (-0.016, 0.016):
        p.cyl((x, -0.12, 0.06), (x, -0.62, 0.06), 0.016, CH, segs=10)
    p.box((0, -0.34, 0.025), (0.05, 0.16, 0.04), LI, bevel=0.018)                 # pump
    p.box((0, -0.6, 0.06), (0.07, 0.03, 0.04), AQ, bevel=0.01)                    # muzzle band


def minigun(p):
    pistol_grip(p, DK)
    trigger_guard(p)
    p.capsule((0, 0.14, 0.08), (0, -0.08, 0.08), 0.075, SK, segs=14)              # motor housing
    p.torus((0, 0.0, 0.08), 0.078, 0.012, LI, matrix=rot(x=90), segs=14, minor=4)
    p.cyl((0, -0.1, 0.08), (0, -0.16, 0.08), 0.06, CH, segs=12)
    for i in range(6):
        t = 2 * math.pi * i / 6
        x, z = 0.035 * math.cos(t), 0.08 + 0.035 * math.sin(t)
        p.cyl((x, -0.16, z), (x, -0.62, z), 0.011, CH, segs=6)
    for y in (-0.36, -0.6):
        p.cyl((0, y, 0.08), (0, y - 0.03, 0.08), 0.055, AQ, segs=12)
    p.capsule((0, 0.08, 0.17), (0, -0.04, 0.17), 0.014, DK, segs=8)               # carry handle
    for y in (0.08, -0.04):
        p.cyl((0, y, 0.15), (0, y, 0.17), 0.01, DK, segs=6)
    p.box((0.085, 0.04, 0.04), (0.06, 0.1, 0.09), LI, bevel=0.02)                 # ammo box


def railgun(p):
    pistol_grip(p, DK)
    trigger_guard(p)
    p.box((0, 0.02, 0.06), (0.07, 0.3, 0.08), WH, bevel=0.03)
    p.box((0, 0.22, 0.04), (0.05, 0.16, 0.08), SK, bevel=0.02)                    # stock
    for z in (0.025, 0.095):  # twin rails
        p.box((0, -0.36, z), (0.03, 0.5, 0.018), CH, bevel=0.007)
    p.cyl((0, -0.13, 0.06), (0, -0.6, 0.06), 0.01, GL, segs=6)                    # charge core
    for y in (-0.18, -0.29, -0.4, -0.51):
        p.torus((0, y, 0.06), 0.045, 0.01, AQ, matrix=rot(x=90), segs=12, minor=4)
    p.capsule((0, 0.04, 0.115), (0, -0.07, 0.115), 0.02, AQ, segs=8)              # scope


def rocket_launcher(p):
    pistol_grip(p, DK)
    trigger_guard(p)
    p.box((0, -0.18, -0.02), (0.035, 0.05, 0.1), DK, bevel=0.012, matrix=rot(x=10))  # front grip
    tz = 0.1
    p.cyl((0, 0.42, tz), (0, -0.48, tz), 0.075, LI, segs=16, caps=False)
    p.cyl((0, 0.42, tz), (0, 0.5, tz), 0.075, CH, r2=0.1, segs=16, caps=False)   # rear flare
    p.torus((0, -0.48, tz), 0.075, 0.012, CH, matrix=rot(x=90), segs=16, minor=4)
    p.torus((0, 0.1, tz), 0.078, 0.012, SK, matrix=rot(x=90), segs=16, minor=4)
    p.cyl((0, -0.4, tz), (0, -0.46, tz), 0.06, WH, segs=12, caps=False)
    p.cyl((0, -0.46, tz), (0, -0.56, tz), 0.06, RD, r2=0.0, segs=12)             # rocket tip
    p.box((0.085, -0.05, tz + 0.03), (0.03, 0.12, 0.05), AQ, bevel=0.012)       # sight


def grapple_hook(p):
    pistol_grip(p, DK)
    trigger_guard(p)
    p.capsule((0, 0.07, 0.06), (0, -0.1, 0.06), 0.042, SK, segs=12)
    p.torus((0, 0.0, 0.06), 0.05, 0.015, YE, matrix=rot(x=90), segs=14, minor=5)  # rope spool
    p.cyl((0, -0.12, 0.06), (0, -0.2, 0.06), 0.025, CH, segs=10)
    p.sphere((0, -0.215, 0.06), 0.025, CH, segs=10, rings=6)
    for i in range(3):  # claw prongs
        t = 2 * math.pi * i / 3 + math.pi / 2
        dx, dz = math.cos(t), math.sin(t)
        base = (0.02 * dx, -0.22, 0.06 + 0.02 * dz)
        mid = (0.06 * dx, -0.27, 0.06 + 0.06 * dz)
        tip = (0.035 * dx, -0.31, 0.06 + 0.035 * dz)
        p.cyl(base, mid, 0.01, CH, r2=0.008, segs=6)
        p.cyl(mid, tip, 0.008, CH, r2=0.0, segs=6)


# ---------------------------------------------------------------------------
# special
# ---------------------------------------------------------------------------

def black_hole_grenade(p):
    p.sphere((0, 0, 0), 0.055, VO, segs=14, rings=9)
    p.sphere((0, 0, 0), 0.062, AQ, segs=14, rings=9)       # glass shell
    p.torus((0, 0, 0), 0.085, 0.009, GL, matrix=rot(x=20), segs=20, minor=5)  # accretion ring
    p.torus((0, 0, 0), 0.072, 0.006, PU, matrix=rot(x=-35, y=25), segs=18, minor=4)
    p.cyl((0, 0, 0.058), (0, 0, 0.08), 0.015, CH, segs=10)
    p.torus((0.02, 0, 0.082), 0.012, 0.003, CH, matrix=rot(x=90), segs=10, minor=4)


def sonic_hammer(p):
    long_handle(p, 0.6, mat=SK)
    hy = -0.64
    p.cyl((0, hy, -0.12), (0, hy, 0.12), 0.07, WH, segs=16)
    for s in (1, -1):
        p.cyl((0, hy, s * 0.12), (0, hy, s * 0.15), 0.07, AQ, r2=0.085, segs=16, caps=False)
        p.sphere((0, hy, s * 0.13), 0.06, GL, segs=12, rings=6, scale=(1, 1, 0.3))  # speaker
    for z in (-0.05, 0.05):
        p.torus((0, hy, z), 0.072, 0.01, LI, segs=16, minor=4)


def nuke_crate(p):
    # origin at the carry handle on top
    p.box((0, 0, -0.22), (0.4, 0.32, 0.36), YE, bevel=0.04)
    for x in (-0.12, 0.0, 0.12):  # hazard stripes
        p.box((x, -0.158, -0.22), (0.04, 0.012, 0.37), DK, bevel=0.005, matrix=rot(y=30))
    p.sphere((0, 0, -0.04), 0.1, AQ, segs=14, rings=8)      # glass dome window
    p.sphere((0, 0, -0.06), 0.055, GG, segs=10, rings=6)    # glowing core
    p.torus((0, 0, -0.04), 0.105, 0.012, CH, segs=16, minor=4)
    p.torus((0, 0.0, 0.06), 0.06, 0.012, CH, matrix=rot(x=90, z=90), segs=12, minor=5)
    for x in (-0.15, 0.15):
        p.box((x, 0, -0.03), (0.04, 0.04, 0.04), CH, bevel=0.01)


# ---------------------------------------------------------------------------
# wearables
# ---------------------------------------------------------------------------

def jetpack(p):
    # origin = centre of the back plate (sits on the wearer's back, wearer faces -Y)
    p.box((0, 0.02, 0), (0.28, 0.05, 0.34), WH, bevel=0.025)
    for s in (1, -1):
        x = s * 0.085
        p.capsule((x, 0.11, -0.12), (x, 0.11, 0.14), 0.07, SK, segs=14)
        p.torus((x, 0.11, 0.08), 0.072, 0.01, LI, segs=14, minor=4)
        p.cyl((x, 0.11, -0.19), (x, 0.11, -0.25), 0.04, CH, r2=0.055, segs=12, caps=False)
        p.sphere((x, 0.11, -0.24), 0.04, GL, segs=10, rings=5, scale=(1, 1, 0.4))
        p.torus((s * 0.09, -0.02, 0.0), 0.11, 0.012, DK, matrix=rot(y=90), segs=14, minor=4)  # straps
    p.sphere((0, 0.1, 0.19), 0.035, AQ, segs=10, rings=6)


def rocket_boots(p):
    # pair; origin between the ankles
    for s in (1, -1):
        x = s * 0.1
        p.box((x, -0.03, -0.09), (0.11, 0.24, 0.07), LI, bevel=0.03)              # sole
        p.capsule((x, 0.0, -0.06), (x, 0.0, 0.08), 0.06, WH, segs=12)            # shaft
        p.sphere((x, -0.09, -0.06), 0.06, WH, segs=12, rings=6, scale=(0.95, 1.1, 0.75))  # toe
        p.torus((x, 0.0, 0.08), 0.062, 0.012, SK, segs=14, minor=4)
        p.cyl((x, 0.08, -0.03), (x, 0.08, -0.13), 0.035, CH, r2=0.045, segs=10, caps=False)  # thruster
        p.sphere((x, 0.08, -0.13), 0.035, GL, segs=10, rings=5, scale=(1, 1, 0.4))


def climbing_gloves(p):
    # single (right) glove; origin at the palm centre, fingers point -Y, back of hand +Z
    p.box((0, 0, 0), (0.1, 0.11, 0.045), LI, bevel=0.02)
    for i, x in enumerate((-0.036, -0.012, 0.012, 0.036)):
        length = 0.06 if i in (1, 2) else 0.05
        tip = (x, -0.05 - length, -0.015)
        p.capsule((x, -0.045, 0), tip, 0.014, LI, segs=8, hemi=2)
        p.cyl(tip, (x, tip[1] - 0.012, -0.035), 0.008, CH, r2=0.0, segs=6)   # climbing claw
    p.capsule((-0.052, 0.02, -0.005), (-0.075, -0.035, -0.015), 0.016, LI, segs=8, hemi=2)  # thumb
    p.cyl((0, 0.05, 0), (0, 0.085, 0), 0.052, SK, r2=0.056, segs=12, caps=False)      # cuff
    p.torus((0, 0.085, 0), 0.056, 0.009, WH, matrix=rot(x=90), segs=12, minor=4)
    p.box((0, 0.0, 0.024), (0.07, 0.07, 0.012), AQ, bevel=0.005)                     # back plate
    p.sphere((0, 0.0, 0.031), 0.012, GL, segs=8, rings=4)
    for x in (-0.025, 0.025):   # palm spikes
        p.cyl((x, -0.01, -0.02), (x, -0.01, -0.04), 0.009, CH, r2=0.0, segs=6)


def pinball_popper(p):
    """Rapid-fire bouncy-ball blaster: glass hopper of balls on top, spring plunger at the back."""
    pistol_grip(p, DK)
    trigger_guard(p)
    p.box((0, -0.05, 0.05), (0.07, 0.24, 0.085), WH, bevel=0.03)                  # body
    p.box((0, -0.2, -0.015), (0.035, 0.05, 0.09), DK, bevel=0.012, matrix=rot(x=10))  # front grip
    p.cyl((0, -0.17, 0.06), (0, -0.44, 0.06), 0.042, PU, segs=14, caps=False)     # barrel
    p.cyl((0, -0.44, 0.06), (0, -0.52, 0.06), 0.042, PU, r2=0.065, segs=14, caps=False)  # flared muzzle
    p.torus((0, -0.52, 0.06), 0.065, 0.012, YE, matrix=rot(x=90), segs=16, minor=4)
    for y in (-0.24, -0.34):
        p.torus((0, y, 0.06), 0.045, 0.009, LI, matrix=rot(x=90), segs=12, minor=3)
    p.cyl((0, -0.04, 0.09), (0, -0.04, 0.2), 0.06, SK, segs=16, caps=False)        # glass hopper
    p.torus((0, -0.04, 0.2), 0.06, 0.01, CH, segs=16, minor=4)
    for (x, y, z, m) in ((0.025, -0.02, 0.12, RD), (-0.025, -0.06, 0.125, YE), (0.0, -0.03, 0.16, LI),
                         (-0.02, -0.01, 0.165, AQ), (0.022, -0.065, 0.165, OR)):  # balls in the hopper
        p.sphere((x, y, z), 0.022, m, segs=8, rings=4)
    p.cyl((0, 0.07, 0.06), (0, 0.2, 0.06), 0.012, CH, segs=8)                       # plunger rod
    for i in range(4):                                                             # spring coils
        y = 0.085 + i * 0.03
        p.torus((0, y, 0.06), 0.026, 0.006, CH, matrix=rot(x=90), segs=8, minor=3)
    p.sphere((0, 0.21, 0.06), 0.034, RD, segs=10, rings=6)                          # plunger knob


def boost_blaster(p):
    """Stubby blaster with twin thruster nozzles at the back: the kick is the point."""
    pistol_grip(p, DK)
    trigger_guard(p)
    p.box((0, -0.06, 0.05), (0.08, 0.26, 0.09), OR, bevel=0.03)                     # body
    p.box((0, -0.2, -0.015), (0.035, 0.05, 0.09), DK, bevel=0.012, matrix=rot(x=10))  # front grip
    p.cyl((0, -0.19, 0.055), (0, -0.36, 0.055), 0.05, CH, r2=0.065, segs=12, caps=False)  # wide short barrel
    p.torus((0, -0.36, 0.055), 0.065, 0.011, YE, matrix=rot(x=90), segs=12, minor=3)
    for x in (-0.045, 0.045):                                                       # thrusters
        p.cyl((x, 0.08, 0.06), (x, 0.2, 0.06), 0.028, WH, r2=0.04, segs=10, caps=False)
        p.cyl((x, 0.19, 0.06), (x, 0.205, 0.06), 0.038, GR, segs=10)
    p.box((0, 0.12, 0.11), (0.03, 0.12, 0.02), LI, bevel=0.008)                       # top rail


def ripple_disc(p):
    """Disc launcher: a drum of saw-discs on top, a slot barrel in front."""
    pistol_grip(p, DK)
    trigger_guard(p)
    p.box((0, -0.06, 0.04), (0.06, 0.28, 0.07), WH, bevel=0.025)                     # body
    p.box((0, -0.3, 0.05), (0.13, 0.22, 0.025), PU, bevel=0.01)                       # flat slot barrel
    p.box((0, -0.41, 0.05), (0.15, 0.02, 0.035), CH, bevel=0.006)
    p.cyl((0, -0.04, 0.085), (0, -0.04, 0.13), 0.085, SK, segs=16)                    # disc drum
    p.torus((0, -0.04, 0.13), 0.085, 0.008, CH, segs=16, minor=3)
    p.cyl((0, -0.04, 0.13), (0, -0.04, 0.145), 0.075, PU, segs=16)                    # top disc
    p.sphere((0, -0.04, 0.15), 0.018, YE, segs=8, rings=4)                            # hub
    p.box((0, 0.17, 0.03), (0.05, 0.14, 0.06), SK, bevel=0.02)                        # stock


def bloom_mortar(p):
    """Shoulder mortar with a flower-petal muzzle."""
    pistol_grip(p, DK)
    trigger_guard(p)
    tz = 0.1
    p.cyl((0, 0.3, tz), (0, -0.34, tz), 0.07, LI, segs=14, caps=False)                # tube
    p.cyl((0, 0.3, tz), (0, 0.36, tz), 0.075, WH, segs=14)                            # breech cap
    p.box((0, -0.16, -0.02), (0.035, 0.05, 0.1), DK, bevel=0.012, matrix=rot(x=10))   # front grip
    p.torus((0, -0.34, tz), 0.072, 0.012, CH, matrix=rot(x=90), segs=14, minor=3)
    for i in range(6):                                                                # petals
        a = 2 * math.pi * i / 6
        x, z = 0.085 * math.cos(a), tz + 0.085 * math.sin(a)
        p.box((x, -0.38, z), (0.05, 0.08, 0.012), RD if i % 2 else OR, bevel=0.006,
              matrix=rot(y=math.degrees(a) + 90, x=-25))
    p.box((0.085, 0.0, tz + 0.04), (0.03, 0.12, 0.05), AQ, bevel=0.012)               # sight


def boing_pad(p):
    """Bounce pad, origin at the bottom centre: a base, springs, a squishy top."""
    p.cyl((0, 0, 0), (0, 0, 0.06), 0.42, WH, segs=20)                                 # base
    p.torus((0, 0, 0.06), 0.4, 0.025, CH, segs=20, minor=4)
    for i in range(4):                                                                # springs
        a = 2 * math.pi * i / 4 + math.pi / 4
        x, y = 0.24 * math.cos(a), 0.24 * math.sin(a)
        for k in range(3):
            p.torus((x, y, 0.09 + k * 0.035), 0.04, 0.008, CH, segs=8, minor=3)
    p.cyl((0, 0, 0.18), (0, 0, 0.24), 0.4, LI, r2=0.36, segs=20)                       # cushion
    p.sphere((0, 0, 0.24), 0.36, LI, segs=20, rings=4, scale=(1, 1, 0.12))
    p.torus((0, 0, 0.23), 0.3, 0.015, YE, segs=20, minor=3)


def raptor_laptop(p):
    """Rugged open laptop (killstreak). Origin at the grip: held by the base's edge."""
    p.box((0, -0.14, 0.0), (0.34, 0.24, 0.025), DK, bevel=0.01)                       # base
    p.box((0, -0.14, 0.014), (0.3, 0.2, 0.004), CH, bevel=0.002)                      # keyboard deck
    p.box((0, -0.27, 0.12), (0.34, 0.015, 0.23), DK, bevel=0.01, matrix=rot(x=-15))   # lid
    p.box((0, -0.262, 0.12), (0.3, 0.004, 0.19), GG, bevel=0.002, matrix=rot(x=-15))  # glowing screen
    for x in (-0.17, 0.17):
        p.box((x, -0.14, 0.0), (0.02, 0.26, 0.04), YE, bevel=0.008)                    # bumpers
    p.box((0, -0.01, -0.005), (0.12, 0.03, 0.02), RD, bevel=0.006)                    # handle


def heli_gunner(p):
    """Signal flare that calls in the chopper (killstreak)."""
    p.cyl((0, 0.06, 0), (0, -0.2, 0), 0.03, RD, segs=12)
    p.cyl((0, -0.2, 0), (0, -0.25, 0), 0.034, WH, segs=12)
    p.sphere((0, -0.27, 0), 0.03, GR, segs=10, rings=6)
    p.torus((0, -0.05, 0), 0.034, 0.008, YE, matrix=rot(x=90), segs=12, minor=3)
    p.box((0, 0.03, 0.035), (0.02, 0.05, 0.02), DK, bevel=0.005)                     # pull tab


def raptor_missile(p):
    """The missile itself (Raptor strikes and Bloop rockets). Nose toward Blender -Y."""
    p.cyl((0, 0.22, 0), (0, -0.2, 0), 0.05, WH, segs=12)
    p.cyl((0, -0.2, 0), (0, -0.32, 0), 0.05, RD, r2=0.0, segs=12)                  # nose
    p.cyl((0, 0.22, 0), (0, 0.26, 0), 0.04, GR, segs=10)                           # exhaust glow
    for a in (0, 90, 180, 270):
        p.box((0, 0.17, 0), (0.16, 0.08, 0.008), YE, bevel=0.003, matrix=rot(y=a))  # fins
    p.torus((0, -0.05, 0), 0.05, 0.008, AQ, matrix=rot(x=90), segs=12, minor=3)


def goo_gun(p):
    """Squeezy goo sprayer: a bulbous pink tank and a stubby nozzle."""
    pistol_grip(p, DK)
    trigger_guard(p)
    p.box((0, -0.04, 0.04), (0.06, 0.2, 0.07), WH, bevel=0.025)
    p.sphere((0, 0.02, 0.13), 0.075, RD, segs=12, rings=8, scale=(1, 1.2, 1))           # goo tank
    p.torus((0, 0.02, 0.13), 0.075, 0.01, CH, segs=12, minor=3)
    p.cyl((0, -0.14, 0.045), (0, -0.3, 0.045), 0.022, LI, r2=0.034, segs=10)            # nozzle
    p.sphere((0, -0.31, 0.045), 0.03, RD, segs=10, rings=5)                             # goo drip


def chain_zapper(p):
    """Tesla rod: coils and a glowing ball at the tip."""
    pistol_grip(p, DK)
    trigger_guard(p)
    p.box((0, -0.03, 0.045), (0.055, 0.2, 0.065), SK, bevel=0.022)
    p.cyl((0, -0.12, 0.05), (0, -0.42, 0.05), 0.014, CH, segs=8)
    for y in (-0.17, -0.23, -0.29, -0.35):
        p.torus((0, y, 0.05), 0.035, 0.009, YE, matrix=rot(x=90), segs=10, minor=3)
    p.sphere((0, -0.46, 0.05), 0.04, GL, segs=12, rings=6)
    p.box((0, 0.12, 0.03), (0.045, 0.1, 0.06), WH, bevel=0.018)


def firework_launcher(p):
    """Party tube: striped barrel with a star on the muzzle."""
    pistol_grip(p, DK)
    trigger_guard(p)
    tz = 0.08
    p.cyl((0, 0.18, tz), (0, -0.42, tz), 0.055, WH, segs=14)
    for i, y in enumerate((0.1, -0.02, -0.14, -0.26, -0.38)):
        p.cyl((0, y, tz), (0, y - 0.05, tz), 0.058, (RD, YE, LI, SK, PU)[i], segs=14)
    for a in range(5):                                                                    # star points
        p.box((0, -0.45, tz), (0.012, 0.012, 0.07), YE, bevel=0.004, matrix=rot(y=a * 72))
    p.box((0, -0.15, -0.015), (0.035, 0.05, 0.09), DK, bevel=0.012, matrix=rot(x=10))


def drill_torpedo(p):
    """Two-handed launcher with a drill-tipped torpedo loaded."""
    pistol_grip(p, DK)
    trigger_guard(p)
    tz = 0.1
    p.cyl((0, 0.3, tz), (0, -0.3, tz), 0.07, OR, segs=14, caps=False)
    p.cyl((0, 0.3, tz), (0, 0.36, tz), 0.075, DK, segs=14)
    p.cyl((0, -0.3, tz), (0, -0.48, tz), 0.06, CH, r2=0.0, segs=12)                      # drill bit
    for k in range(3):
        p.torus((0, -0.33 - k * 0.045, tz), 0.045 - k * 0.012, 0.007, DK, matrix=rot(x=90), segs=10, minor=3)
    p.box((0, -0.15, -0.02), (0.035, 0.05, 0.1), DK, bevel=0.012, matrix=rot(x=10))
    p.box((0.085, 0.0, tz + 0.04), (0.03, 0.12, 0.05), AQ, bevel=0.012)


def frost_ray(p):
    """Ice beam emitter: crystal shards around a glowing core."""
    pistol_grip(p, DK)
    trigger_guard(p)
    p.box((0, -0.04, 0.045), (0.06, 0.22, 0.075), WH, bevel=0.025)
    p.cyl((0, -0.15, 0.05), (0, -0.36, 0.05), 0.03, SK, segs=10)
    p.sphere((0, -0.38, 0.05), 0.035, GL, segs=12, rings=6)
    for a in (0, 120, 240):                                                               # ice shards
        p.box((0, -0.3, 0.05), (0.018, 0.12, 0.018), AQ, bevel=0.005, matrix=rot(y=a, x=-20))
    p.box((0, 0.14, 0.03), (0.05, 0.12, 0.06), SK, bevel=0.02)


def warp_kunai(p):
    """Kunai with a glowing warp ring on the pommel."""
    p.cyl((0, 0.1, 0), (0, -0.02, 0), 0.014, DK, segs=8)                                # wrapped handle
    for y in (0.08, 0.04, 0.0):
        p.cyl((0, y, 0), (0, y - 0.015, 0), 0.017, PU, segs=8)
    p.torus((0, 0.13, 0), 0.028, 0.007, GL, matrix=rot(x=90), segs=12, minor=3)         # ring
    p.box((0, -0.03, 0), (0.05, 0.02, 0.01), CH, bevel=0.003)                            # guard
    p.cyl((0, -0.04, 0), (0, -0.22, 0), 0.03, CH, r2=0.0, segs=4)                        # blade


def bubble_wand(p):
    """Wand with a big soapy loop."""
    p.cyl((0, 0.1, 0), (0, -0.25, 0), 0.014, SK, segs=8)
    p.sphere((0, 0.11, 0), 0.025, PU, segs=8, rings=5)
    p.torus((0, -0.33, 0), 0.08, 0.01, YE, matrix=rot(x=90), segs=16, minor=3)
    p.cyl((0, -0.33, 0), (0, -0.335, 0), 0.075, AQ, segs=16)                           # soap film


def saw_yoyo(p):
    """Finger loop + saw disc yo-yo."""
    p.torus((0, 0.02, 0), 0.025, 0.007, DK, matrix=rot(x=90), segs=10, minor=3)
    p.cyl((0, 0.0, 0), (0, -0.1, 0), 0.003, WH, segs=4)                                # string
    p.cyl((-0.02, -0.15, 0), (0.02, -0.15, 0), 0.055, CH, segs=16)                     # saw disc
    for a in range(8):
        p.box((0, -0.15, 0), (0.012, 0.13, 0.012), RD, bevel=0.002, matrix=rot(x=a * 22.5))   # teeth
    p.cyl((-0.025, -0.15, 0), (0.025, -0.15, 0), 0.02, YE, segs=10)


def chain_whip(p):
    """Handle with a coiled chain."""
    p.cyl((0, 0.08, 0), (0, -0.08, 0), 0.018, DK, segs=8)
    p.sphere((0, 0.09, 0), 0.024, YE, segs=8, rings=5)
    for i in range(6):
        p.torus((0, -0.1 - i * 0.03, 0), 0.016, 0.005, CH, matrix=rot(x=90 if i % 2 else 0), segs=8, minor=3)
    p.torus((0, -0.12, 0.0), 0.07, 0.008, CH, segs=14, minor=3)                        # coil
    p.sphere((0, -0.3, 0), 0.025, OR, segs=8, rings=5)                                  # weight


def magnet_glove(p):
    """Chunky gauntlet with a horseshoe magnet over the knuckles."""
    p.box((0, 0.02, 0), (0.1, 0.16, 0.06), SK, bevel=0.025)                              # glove
    p.box((0, 0.1, 0), (0.11, 0.04, 0.07), DK, bevel=0.015)                              # cuff
    p.torus((0, -0.1, 0.02), 0.05, 0.016, RD, segs=12, minor=4, matrix=rot(x=90))       # magnet arc
    for x in (-0.05, 0.05):
        p.box((x, -0.06, 0.02), (0.032, 0.06, 0.032), WH, bevel=0.006)                   # poles
    p.sphere((0, -0.03, 0.04), 0.02, GL, segs=8, rings=5)


TOOLS = {
    "Pickaxe": pickaxe, "Shovel": shovel, "SledgeHammer": sledgehammer, "Drill": drill,
    "Jackhammer": jackhammer, "MiningLaser": mining_laser, "PlasmaCutter": plasma_cutter,
    "Grenade": grenade, "StickyBomb": sticky_bomb, "Dynamite": dynamite, "TNT": tnt, "C4": c4,
    "Detonator": detonator, "Shotgun": shotgun, "Minigun": minigun, "RailGun": railgun,
    "RocketLauncher": rocket_launcher, "BlackHoleGrenade": black_hole_grenade,
    "SonicHammer": sonic_hammer, "NukeCrate": nuke_crate, "GrappleHook": grapple_hook,
    "Jetpack": jetpack, "RocketBoots": rocket_boots, "ClimbingGloves": climbing_gloves,
    "PinballPopper": pinball_popper, "BoostBlaster": boost_blaster, "RippleDisc": ripple_disc,
    "BloomMortar": bloom_mortar, "BoingPad": boing_pad, "RaptorLaptop": raptor_laptop,
    "HeliGunner": heli_gunner, "RaptorMissile": raptor_missile, "GooGun": goo_gun,
    "ChainZapper": chain_zapper, "FireworkLauncher": firework_launcher, "DrillTorpedo": drill_torpedo,
    "FrostRay": frost_ray, "WarpKunai": warp_kunai, "BubbleWand": bubble_wand, "SawYoyo": saw_yoyo,
    "ChainWhip": chain_whip, "MagnetGlove": magnet_glove,
}


def build(ids=None):
    for tid in ids or TOOLS:
        C.clear_scene()
        p = C.Part()
        TOOLS[tid](p)
        obj = C.make_object(tid, p, (0, 0, 0))
        C.export_fbx(obj, os.path.join("Tools", tid + ".fbx"))


if __name__ == "__main__":
    build()
    C.print_report()
