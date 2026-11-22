"""Render preview PNGs into Docs/previews/ by RE-IMPORTING the exported FBX files.

Re-importing doubles as a sanity check that the FBX files load and keep their
hierarchy. Rendering uses Cycles on the CPU so it works on headless machines.
"""

import math
import os
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import bpy  # noqa: E402
from mathutils import Vector  # noqa: E402

import common as C  # noqa: E402

RES = (800, 450)


def _lin_to_srgb(c):
    c = max(0.0, min(1.0, c))
    return c * 12.92 if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055


def import_fbx(rel_path, location=(0, 0, 0), rot_z=0.0, scale=1.0, tint=None, fit=None, rot_x=0.0):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=os.path.join(C.MODELS_DIR, rel_path))
    new = [o for o in bpy.data.objects if o not in before]
    roots = [o for o in new if o.parent is None]
    holder = bpy.data.objects.new("holder_" + os.path.basename(rel_path), None)
    C.link(holder)
    for r in roots:
        r.parent = holder
    holder.location = location
    holder.rotation_euler = (math.radians(rot_x), 0, math.radians(rot_z))
    holder.scale = (scale, scale, scale)
    if fit:
        # scale + centre the model so its bounding box fits a `fit`-sized cell
        bpy.context.view_layer.update()
        pts = [o.matrix_world @ Vector(c) for o in new if o.type == "MESH" for c in o.bound_box]
        lo = Vector((min(p.x for p in pts), min(p.y for p in pts), min(p.z for p in pts)))
        hi = Vector((max(p.x for p in pts), max(p.y for p in pts), max(p.z for p in pts)))
        size = hi - lo
        k = fit / max(size.x, size.z)
        centre = (lo + hi) / 2 - holder.location
        holder.scale = (k, k, k)
        holder.location = Vector(location) - centre * k
    # Restore the glossy look (FBX only carries basic material values).
    for o in new:
        if o.type != "MESH":
            continue
        for slot in o.material_slots:
            m = slot.material
            if m is None:
                continue
            base = m.name.split(".")[0]
            if base in C.PALETTE:
                spec = dict(C.PALETTE[base])
                # keep the colour stored in the FBX (units recolour Buddy_* per file)
                bsdf = next((n for n in m.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None) \
                    if m.node_tree else None
                if bsdf is not None:
                    lin = bsdf.inputs["Base Color"].default_value
                    spec["color"] = tuple(_lin_to_srgb(c) for c in lin[:3])
                if tint and base in tint:
                    spec["color"] = C.hexrgb(tint[base])
                key = base + "_" + "".join(f"{c:.3f}" for c in spec["color"])
                nm = bpy.data.materials.get("pv_" + key)
                if nm is None:
                    nm = bpy.data.materials.new("pv_" + key)
                    C.apply_material_spec(nm, spec)
                slot.material = nm
    return holder, new


def setup_scene(cam_loc, cam_target, lens=50, floor=True, ortho=None):
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 48
    try:
        scene.cycles.use_denoising = True
    except Exception:
        pass
    scene.render.resolution_x, scene.render.resolution_y = RES
    scene.render.resolution_percentage = 100
    scene.render.film_transparent = False
    try:
        scene.view_settings.view_transform = "Standard"
    except Exception:
        pass

    world = bpy.data.worlds.get("pv_world") or bpy.data.worlds.new("pv_world")
    scene.world = world
    try:
        world.use_nodes = True
    except Exception:
        pass
    nt = world.node_tree
    nt.nodes.clear()
    out = nt.nodes.new("ShaderNodeOutputWorld")
    bg = nt.nodes.new("ShaderNodeBackground")
    grad = nt.nodes.new("ShaderNodeTexGradient")
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    coord = nt.nodes.new("ShaderNodeTexCoord")
    mapping = nt.nodes.new("ShaderNodeMapping")
    mapping.inputs["Rotation"].default_value = (0, math.radians(-90), 0)
    nt.links.new(coord.outputs["Generated"], mapping.inputs["Vector"])
    nt.links.new(mapping.outputs["Vector"], grad.inputs["Vector"])
    nt.links.new(grad.outputs["Fac"], ramp.inputs["Fac"])
    ramp.color_ramp.elements[0].color = tuple(C.srgb_to_linear(c) for c in C.hexrgb("#E8FBFF")) + (1,)
    ramp.color_ramp.elements[1].color = tuple(C.srgb_to_linear(c) for c in C.hexrgb("#3E9BEA")) + (1,)
    nt.links.new(ramp.outputs["Color"], bg.inputs["Color"])
    bg.inputs["Strength"].default_value = 1.0
    nt.links.new(bg.outputs[0], out.inputs[0])

    sun = bpy.data.objects.new("pv_sun", bpy.data.lights.new("pv_sun", "SUN"))
    sun.data.energy = 2.2
    sun.data.angle = math.radians(20)
    sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(-35))
    C.link(sun)
    area = bpy.data.objects.new("pv_fill", bpy.data.lights.new("pv_fill", "AREA"))
    area.data.energy = 120
    area.data.size = 6
    d = Vector(cam_loc) - Vector(cam_target)
    area.location = Vector(cam_target) + d * 0.8 + Vector((0, 0, 3))
    area.rotation_euler = (Vector(cam_target) - area.location).to_track_quat("-Z", "Y").to_euler()
    C.link(area)

    if floor:
        me = bpy.data.meshes.new("pv_floor")
        s = 60
        me.from_pydata([(-s, -s, 0), (s, -s, 0), (s, s, 0), (-s, s, 0)], [], [(0, 1, 2, 3)])
        fl = bpy.data.objects.new("pv_floor", me)
        fm = bpy.data.materials.new("pv_floor")
        C.apply_material_spec(fm, C.M("#DDF4FF", rough=0.25, coat=0.6))
        me.materials.append(fm)
        C.link(fl)

    cam = bpy.data.objects.new("pv_cam", bpy.data.cameras.new("pv_cam"))
    cam.data.lens = lens
    if ortho:
        cam.data.type = "ORTHO"
        cam.data.ortho_scale = ortho
    cam.location = cam_loc
    cam.rotation_euler = (Vector(cam_target) - Vector(cam_loc)).to_track_quat("-Z", "Y").to_euler()
    C.link(cam)
    scene.camera = cam


def render(name):
    os.makedirs(C.PREVIEW_DIR, exist_ok=True)
    path = os.path.join(C.PREVIEW_DIR, name)
    bpy.context.scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print(f"[cubestruction] preview {os.path.relpath(path, C.REPO_ROOT)}")


def reset():
    C.clear_scene()
    for w in list(bpy.data.worlds):
        if w.name.startswith("pv_"):
            bpy.data.worlds.remove(w)


def preview_buddy():
    reset()
    tints = [
        {"Buddy_Body": "#5BC236", "Buddy_Head": "#7FDB4A"},   # lime
        None,                                                 # untinted white
        {"Buddy_Body": "#2F8FE8", "Buddy_Head": "#4FB0FF"},   # sky blue
    ]
    for i, t in enumerate(tints):
        import_fbx("Buddy.fbx", location=((i - 1) * 0.75, 0, 0), rot_z=(1 - i) * 18, tint=t)
    setup_scene((0.6, -3.1, 1.15), (0, 0, 0.5), lens=50)
    render("buddy.png")


TOOL_IDS = None


def preview_tools(ids):
    reset()
    cols = 6
    for i, tid in enumerate(ids):
        r, c = divmod(i, cols)
        # tools point Blender -Y; rotate so they point screen-right (+X)
        import_fbx(f"Tools/{tid}.fbx", location=((c - (cols - 1) / 2) * 1.0, 0, 3.0 - r * 1.0),
                   rot_z=65, rot_x=-15, fit=0.8)
    setup_scene((0, -9.5, 1.45), (0, 0, 1.45), lens=50, floor=False, ortho=6.9)
    render("tools.png")


def preview_units(ids):
    reset()
    xs = [-3.1, -2.2, -1.2, 0.2, 1.6, 3.3]
    for x, uid in zip(xs, ids):
        import_fbx(f"Units/{uid}.fbx", location=(x, 0, 0), rot_z=-15)
    setup_scene((0.3, -8.6, 2.0), (0.3, 0, 1.1), lens=35)
    render("units.png")


def preview_env():
    reset()
    import_fbx("Env/Pedestal.fbx")
    import_fbx("Env/ShopKiosk.fbx", location=(-4.5, -3.5, 0), rot_z=-20)
    import_fbx("Buddy.fbx", location=(-3.2, -4.2, 0))
    for p, s in (((3, -4, 2), 1.0), ((5, 1, 3.5), 1.6), ((-1, 4, 5), 2.0), ((7.5, -6, -1.5), 1.2)):
        import_fbx("Env/Bubble.fbx", location=p, scale=s)
    # stand-in for the destructible cube
    import bpy as _b
    _b.ops.mesh.primitive_cube_add(size=8, location=(1.0, 1.0, 4.0))
    cube = _b.context.active_object
    cm = _b.data.materials.new("pv_cube")
    C.apply_material_spec(cm, C.M("#B07A4A", rough=0.5, coat=0.2))
    cube.data.materials.append(cm)
    setup_scene((-15, -23, 3.5), (0, 0, 0.5), lens=32, floor=False)
    render("environment.png")


if __name__ == "__main__":
    which = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else ["buddy"]
    if "buddy" in which:
        preview_buddy()
    if "tools" in which:
        import tools
        preview_tools(list(tools.TOOLS))
    if "units" in which:
        import units
        preview_units(list(units.UNITS))
    if "env" in which:
        preview_env()
