"""Buddy character: a glossy bubble head floating above a rounded teardrop body.

Output: Assets/Cubestruction/Models/Buddy.fbx

Contract (Unity code depends on it):
  root empty `Buddy` at the feet (origin on the ground), ~1.0 unit tall,
  children (separate meshes) `Head`, `Torso`, `ArmL`, `ArmR`, `LegL`, `LegR`.
  Origins: Head at the neck, Arms at the shoulders, Legs at the hips,
  Torso at its centre. Materials `Buddy_Body` (torso + limbs), `Buddy_Head`.
  Faces Blender -Y, which the FBX export turns into Unity +Z (forward).
  ArmL/LegL are on the character's left (Blender +X, Unity -X).
"""

import os
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import common as C  # noqa: E402

TORSO_TEARDROP = [(0.0, 0.17), (0.11, 0.18), (0.17, 0.215), (0.198, 0.27), (0.2, 0.33),
                  (0.192, 0.4), (0.172, 0.47), (0.14, 0.535), (0.095, 0.59), (0.045, 0.615),
                  (0.0, 0.622)]
TORSO_ROUND = [(r * 1.0, z) for r, z in C.sphere_profile(0.235, 9)]


def make_buddy(root_name="Buddy", scale=1.0, torso="teardrop", head="sphere", head_r=0.18,
               limb=1.0, width=1.0, depth=0.86, props=None, root=None):
    """Build a buddy. `props(parts, geo)` may add extra geometry to the parts.

    All dimensions are authored for a 1.0-unit-tall buddy and multiplied by `scale`.
    Returns the root empty and the dict of created objects.
    """
    B, H = "Buddy_Body", "Buddy_Head"
    parts = {k: C.Part() for k in ("Head", "Torso", "ArmL", "ArmR", "LegL", "LegR")}

    # --- torso ------------------------------------------------------------
    torso_c = 0.40
    if torso == "teardrop":
        parts["Torso"].lathe(TORSO_TEARDROP, B, segs=16, rx=width, ry=depth)
        top = 0.622
    elif torso == "round":
        parts["Torso"].lathe(TORSO_ROUND, B, c=(0, 0, 0.405), segs=16, rx=width, ry=depth * 1.05)
        top = 0.64
    elif torso == "box":
        parts["Torso"].box((0, 0, 0.40), (0.42 * width, 0.34 * depth / 0.86, 0.46), B,
                           bevel=0.09, segments=3)
        top = 0.63
    else:
        raise ValueError(torso)

    # --- head (floats just above the torso) -------------------------------
    neck = (0, 0, top + 0.005)
    hc = (0, 0, top + 0.02 + head_r)
    if head == "sphere":
        parts["Head"].sphere(hc, head_r, H, segs=16, rings=10)
    elif head == "cube":
        parts["Head"].box(hc, (head_r * 2, head_r * 1.9, head_r * 1.9), H,
                          bevel=head_r * 0.35, segments=3)
    else:
        raise ValueError(head)

    # --- arms (stubby mittens) ---------------------------------------------
    sx = 0.165 * width
    ar = 0.052 * limb
    shoulders = {}
    for side, sgn in (("L", 1), ("R", -1)):
        sh = (sgn * sx, 0, 0.50)
        hand = (sgn * (sx + 0.085 + 0.01 * limb), -0.012, 0.335)
        parts["Arm" + side].capsule((sgn * (sx + 0.005), 0, 0.5), hand, ar, B, segs=10, hemi=3)
        parts["Arm" + side].sphere(hand, ar * 1.18, B, segs=10, rings=6)
        shoulders[side] = (sh, hand)

    # --- legs (stubby, with rounded feet) ---------------------------------
    hx = min(0.085 * width, 0.13)
    lr = 0.064 * limb
    hips = {}
    for side, sgn in (("L", 1), ("R", -1)):
        hip = (sgn * hx, 0, 0.24)
        fr = 0.072 * limb
        fz = fr * 0.62
        parts["Leg" + side].capsule((sgn * hx, 0, 0.23), (sgn * hx * 1.04, 0, fz + 0.02), lr, B,
                                    segs=10, hemi=3)
        parts["Leg" + side].sphere((sgn * hx * 1.04, -0.03 * limb, fz), fr, B, segs=10, rings=6,
                                   scale=(0.95, 1.3, 0.62))
        hips[side] = hip

    geo = dict(neck=neck, head_center=hc, head_r=head_r, top=top, shoulders=shoulders, hips=hips)
    if props:
        props(parts, geo)

    if root is None:
        root = C.make_empty(root_name)
    pivots = {
        "Head": neck,
        "Torso": (0, 0, torso_c),
        "ArmL": shoulders["L"][0], "ArmR": shoulders["R"][0],
        "LegL": hips["L"], "LegR": hips["R"],
    }
    objs = {}
    for name in ("Torso", "Head", "ArmL", "ArmR", "LegL", "LegR"):
        objs[name] = C.make_object(name, parts[name], pivots[name], parent=root, scale=scale)
    return root, objs


def build():
    C.clear_scene()
    root, _ = make_buddy("Buddy")
    C.export_fbx(root, "Buddy.fbx")


if __name__ == "__main__":
    build()
    C.print_report()
