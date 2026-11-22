"""Environment props.

Output (Assets/Cubestruction/Models/Env/):
  * Pedestal.fbx  - glossy rounded floating island, ~14 x 14 units, top surface at
                    height 0 (Blender Z = Unity Y = 0), origin at the top centre.
  * Bubble.fbx    - low-poly decorative bubble, diameter 1, origin at its centre.
  * ShopKiosk.fbx - glossy rounded kiosk ~2.1 units tall, origin on the ground,
                    counter facing Blender -Y (Unity +Z).
  * Heli.fbx      - Heli-Gunner gunship (hierarchy: Body, Rotor, TailRotor, Seat empty).
Each file is a single mesh object named after the file.
"""

import os
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import common as C  # noqa: E402


def pedestal():
    p = C.Part()
    H = 7.0   # half size -> 14 x 14
    N = 6.0   # superellipse exponent: rounded square
    segs = 48
    # grass top: rim wall rolling over into a flat top at z=0, fan to the centre
    p.lathe([(1.0, -0.32), (1.0, -0.12), (0.99, -0.035), (0.975, 0.0), (0.6, 0.0), (0.0, 0.0)],
            "Env_Grass", segs=segs, rx=H, ry=H, n=N, caps=False)
    # glossy white trim band
    p.lathe([(0.995, -0.62), (1.03, -0.57), (1.04, -0.46), (1.03, -0.35), (0.995, -0.29)],
            "Env_Rim", segs=segs, rx=H, ry=H, n=N, caps=False)
    # aqua underside tapering into a rounded point
    p.lathe([(0.55, -2.8), (0.75, -2.0), (0.9, -1.2), (0.985, -0.6)],
            "Env_Under", segs=segs, rx=H, ry=H, n=N, caps=False)
    p.lathe([(0.0, -4.3), (0.12, -4.15), (0.3, -3.6), (0.56, -2.78)],
            "Env_UnderDeep", segs=segs, rx=H, ry=H, n=N - 2, caps=False)
    return C.make_object("Pedestal", p)


def bubble():
    p = C.Part()
    p.sphere((0, 0, 0), 0.5, "Env_Bubble", segs=16, rings=10)
    return C.make_object("Bubble", p)


def shop_kiosk():
    p = C.Part()
    W, D, N = 0.72, 0.46, 4.0
    # counter body
    p.lathe([(0.9, 0.0), (0.98, 0.04), (1.0, 0.1), (1.0, 0.95), (0.98, 1.0), (0.0, 1.0)],
            "Env_KioskWhite", segs=24, rx=W, ry=D, n=N)
    p.lathe([(1.0, 0.52), (1.03, 0.56), (1.035, 0.68), (1.03, 0.74), (1.0, 0.78)],
            "Env_KioskAqua", segs=24, rx=W, ry=D, n=N, caps=False)
    # lime counter top
    p.lathe([(1.02, 0.98), (1.08, 1.0), (1.09, 1.05), (1.06, 1.09), (0.0, 1.1)],
            "Env_KioskLime", segs=24, rx=W, ry=D, n=N)
    # display dome with an item inside, plus a little register
    dome = [pt for pt in C.sphere_profile(0.17, 8) if pt[1] >= -1e-6]
    p.lathe(dome, "Env_KioskGlass", c=(-0.3, -0.05, 1.1), segs=14)
    p.sphere((-0.3, -0.05, 1.18), 0.07, "Env_KioskLime", segs=10, rings=6)
    p.box((0.3, 0.05, 1.18), (0.22, 0.16, 0.16), "Env_KioskAqua", bevel=0.04)
    p.box((0.3, -0.03, 1.25), (0.16, 0.02, 0.06), "Env_KioskWhite", bevel=0.008, matrix=C.rot(x=-30))
    # poles
    for x in (-0.62, 0.62):
        for y in (-0.32, 0.32):
            p.cyl((x, y, 1.1), (x, y, 1.82), 0.028, "Env_KioskChrome", segs=10)
    # bubbly canopy
    p.lathe([(1.0, 1.8), (1.05, 1.84), (1.03, 1.9), (0.9, 1.97), (0.6, 2.02), (0.0, 2.04)],
            "Env_KioskLime", segs=24, rx=0.82, ry=0.56, n=3.0)
    p.box((0, -0.5, 1.9), (0.7, 0.06, 0.16), "Env_KioskAqua", bevel=0.03)   # sign board
    p.box((0, -0.535, 1.9), (0.5, 0.01, 0.06), "Env_KioskWhite", bevel=0.0)
    p.sphere((0, 0, 2.1), 0.07, "Env_KioskGlass", segs=10, rings=6)
    return C.make_object("ShopKiosk", p)


def heli():
    """Aero gunship for the Heli-Gunner streak. Root "Heli" at the body centre, facing Blender -Y
    (Unity +Z). Children: Body, Rotor (spins about Z/Unity Y), TailRotor (spins about X), and an
    empty "Seat" at the open left door (Blender +X = Unity -X) where the gunner sits."""
    root = C.make_empty("Heli")
    b = C.Part()
    b.capsule((0, 1.1, 0), (0, -0.9, 0), 1.05, "Env_KioskWhite", segs=16)               # cabin
    b.sphere((0, -1.55, 0.15), 0.85, "Env_KioskGlass", segs=16, rings=8, scale=(1, 1.1, 0.85))  # bubble canopy
    b.cyl((0, 1.7, 0.25), (0, 4.6, 0.55), 0.32, "Env_KioskAqua", r2=0.16, segs=12)        # tail boom
    b.box((0, 4.55, 1.0), (0.08, 0.55, 0.9), "Env_KioskLime", bevel=0.06)                # fin
    b.box((0, 4.4, 0.55), (1.2, 0.35, 0.07), "Env_KioskLime", bevel=0.03)                # stabiliser
    for x in (-0.95, 0.95):                                                               # skids
        b.cyl((x, -1.3, -1.35), (x, 1.4, -1.35), 0.07, "Env_KioskChrome", segs=8)
        for y in (-0.6, 0.7):
            b.cyl((x * 0.75, y, -0.75), (x, y, -1.35), 0.05, "Env_KioskChrome", segs=6)
    b.cyl((0, 0, 1.0), (0, 0, 1.35), 0.22, "Env_KioskChrome", segs=10)                   # mast
    b.box((-1.08, 0.1, -0.05), (0.1, 1.2, 0.95), "Env_KioskAqua", bevel=0.05)            # right door (closed)
    b.cyl((1.25, -0.2, 0.1), (1.25, -1.05, 0.1), 0.09, "Env_KioskChrome", segs=8)        # door gun barrel (open left door)
    b.box((1.2, 0.0, 0.1), (0.25, 0.35, 0.25), "Env_UnderDeep", bevel=0.05)              # gun body
    C.make_object("Body", b, (0, 0, 0), parent=root)
    r = C.Part()
    r.cyl((0, 0, 1.35), (0, 0, 1.5), 0.28, "Env_KioskLime", segs=10)                     # hub
    for a in (0, 90, 180, 270):
        r.box((0, 2.3, 1.43), (0.3, 4.2, 0.05), "Env_KioskWhite", bevel=0.02, matrix=C.rot(z=a))
    C.make_object("Rotor", r, (0, 0, 1.43), parent=root)
    t = C.Part()
    for a in (0, 90):
        t.box((0.12, 4.75, 1.0), (0.04, 0.9, 0.12), "Env_KioskWhite", bevel=0.02, matrix=C.rot(x=a))
    C.make_object("TailRotor", t, (0.12, 4.75, 1.0), parent=root)
    C.make_empty("Seat", location=(0.75, 0.15, -0.35), parent=root)   # left side = Blender +X = Unity -X
    return root


ENV = {"Pedestal": pedestal, "Bubble": bubble, "ShopKiosk": shop_kiosk, "Heli": heli}


def build():
    for name, fn in ENV.items():
        C.clear_scene()
        obj = fn()
        C.export_fbx(obj, os.path.join("Env", name + ".fbx"))


if __name__ == "__main__":
    build()
    C.print_report()
