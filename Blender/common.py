"""Shared helpers for the Cubestruction Blender asset generators.

Everything is built procedurally from simple lathed / bevelled primitives so the
assets stay low poly (Quest standalone budget) and fully reproducible.

Conventions (Blender space):
  * Z is up, 1 unit = 1 metre.
  * The FRONT of a model / the direction a tool points is Blender -Y.
    With the standard Unity-friendly FBX settings used here
    (axis_forward='-Z', axis_up='Y', bake_space_transform=True), Blender -Y
    becomes Unity +Z (forward) and Blender +Z becomes Unity +Y (up).
  * A character's LEFT side is Blender +X (it faces -Y), which becomes Unity -X,
    i.e. still the character's left when it faces Unity +Z.
"""

import math
import os
import sys

sys.dont_write_bytecode = True

import bpy  # noqa: E402  (must come before bmesh when using the pip `bpy` module)
import bmesh  # noqa: E402
from mathutils import Matrix, Vector  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.abspath(os.path.join(HERE, ".."))
MODELS_DIR = os.path.join(REPO_ROOT, "Assets", "Cubestruction", "Models")
PREVIEW_DIR = os.path.join(REPO_ROOT, "Docs", "previews")

# Filled by export_fbx(): list of (relative path, total tris, {object: tris}).
REPORT = []


# --------------------------------------------------------------------------
# Colours / materials
# --------------------------------------------------------------------------

def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def hexrgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) / 255.0 for i in (0, 2, 4))


def M(hex_color, rough=0.18, metal=0.0, coat=0.8, alpha=1.0, emit=0.0, transmission=0.0):
    return dict(color=hexrgb(hex_color), rough=rough, metal=metal, coat=coat,
                alpha=alpha, emit=emit, transmission=transmission)


# All material names used by the generators. Glossy "Frutiger Aero" palette.
PALETTE = {
    # Buddy (white-ish so Unity can tint per player)
    "Buddy_Body": M("#EEF5FF", rough=0.22, coat=1.0),
    "Buddy_Head": M("#FFFFFF", rough=0.18, coat=1.0),

    # Tools
    "Tool_Chrome": M("#DCE6F0", rough=0.12, metal=1.0, coat=0.5),
    "Tool_Aqua": M("#3FD8E8", rough=0.06, coat=1.0, alpha=0.85, transmission=0.3),
    "Tool_Lime": M("#8EE33A", rough=0.15, coat=1.0),
    "Tool_Sky": M("#4AA8FF", rough=0.15, coat=1.0),
    "Tool_White": M("#F4F8FF", rough=0.2, coat=1.0),
    "Tool_Dark": M("#25344A", rough=0.35, coat=0.6),
    "Tool_Red": M("#FF4B4B", rough=0.15, coat=1.0),
    "Tool_Orange": M("#FF9A2E", rough=0.15, coat=1.0),
    "Tool_Yellow": M("#FFD93B", rough=0.15, coat=1.0),
    "Tool_Purple": M("#6A3BD8", rough=0.1, coat=1.0),
    "Tool_Void": M("#140A2A", rough=0.05, coat=1.0),
    "Tool_Glow": M("#7DF9FF", rough=0.1, coat=0.5, emit=3.0),
    "Tool_GlowGreen": M("#9DFF5A", rough=0.1, coat=0.5, emit=3.0),
    "Tool_GlowRed": M("#FF3B3B", rough=0.1, coat=0.5, emit=2.5),

    # Units (enemy buddies keep Buddy_* names on body parts so tint code works)
    "Unit_Prop": M("#DCE6F0", rough=0.12, metal=1.0, coat=0.5),
    "Unit_Accent": M("#FF9A2E", rough=0.15, coat=1.0),
    "Unit_Gold": M("#FFC93B", rough=0.12, metal=1.0, coat=0.8),
    "Unit_Dark": M("#25344A", rough=0.35, coat=0.6),
    "Unit_Glow": M("#7DF9FF", rough=0.1, coat=0.5, emit=3.0),
    "Unit_Spark": M("#FFE36B", rough=0.1, coat=0.5, emit=4.0),
    "Unit_Wheel": M("#3A4A60", rough=0.3, coat=0.8),

    # Environment
    "Env_Grass": M("#7FDB3A", rough=0.2, coat=1.0),
    "Env_Rim": M("#F4FAFF", rough=0.15, coat=1.0),
    "Env_Under": M("#2FA6D9", rough=0.12, coat=1.0),
    "Env_UnderDeep": M("#1F6FB8", rough=0.15, coat=1.0),
    "Env_Bubble": M("#BFF4FF", rough=0.02, coat=1.0, alpha=0.3, transmission=0.6),
    "Env_KioskWhite": M("#F4F8FF", rough=0.15, coat=1.0),
    "Env_KioskAqua": M("#3FD8E8", rough=0.08, coat=1.0),
    "Env_KioskLime": M("#8EE33A", rough=0.12, coat=1.0),
    "Env_KioskChrome": M("#DCE6F0", rough=0.1, metal=1.0, coat=0.5),
    "Env_KioskGlass": M("#BFF4FF", rough=0.02, coat=1.0, alpha=0.4, transmission=0.6),
}

# Per-file colour overrides for Buddy_Body / Buddy_Head (used by enemy units).
COLOR_OVERRIDES = {}


def _set_input(node, names, value):
    for n in names:
        if n in node.inputs:
            try:
                node.inputs[n].default_value = value
            except Exception:
                pass
            return


def get_material(name):
    mat = bpy.data.materials.get(name)
    if mat:
        return mat
    spec = dict(PALETTE[name])
    if name in COLOR_OVERRIDES:
        spec["color"] = hexrgb(COLOR_OVERRIDES[name])
    mat = bpy.data.materials.new(name)
    apply_material_spec(mat, spec)
    return mat


def apply_material_spec(mat, spec):
    lin = tuple(srgb_to_linear(c) for c in spec["color"]) + (1.0,)
    mat.diffuse_color = lin[:3] + (spec["alpha"],)
    mat.roughness = spec["rough"]
    mat.metallic = spec["metal"]
    try:
        mat.use_nodes = True
    except Exception:
        pass
    nt = mat.node_tree
    bsdf = next((n for n in nt.nodes if n.type == "BSDF_PRINCIPLED"), None)
    if bsdf is None:
        nt.nodes.clear()
        bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled")
        out = nt.nodes.new("ShaderNodeOutputMaterial")
        nt.links.new(bsdf.outputs[0], out.inputs[0])
    _set_input(bsdf, ["Base Color"], lin)
    _set_input(bsdf, ["Metallic"], spec["metal"])
    _set_input(bsdf, ["Roughness"], spec["rough"])
    _set_input(bsdf, ["Coat Weight", "Clearcoat"], spec["coat"])
    _set_input(bsdf, ["Coat Roughness", "Clearcoat Roughness"], 0.03)
    _set_input(bsdf, ["Alpha"], spec["alpha"])
    _set_input(bsdf, ["Transmission Weight", "Transmission"], spec["transmission"])
    if spec["emit"] > 0:
        _set_input(bsdf, ["Emission Color", "Emission"], lin)
        _set_input(bsdf, ["Emission Strength"], spec["emit"])
    if spec["alpha"] < 1.0:
        for attr, val in (("surface_render_method", "BLENDED"), ("blend_method", "BLEND")):
            if hasattr(mat, attr):
                try:
                    setattr(mat, attr, val)
                except Exception:
                    pass


# --------------------------------------------------------------------------
# Scene helpers
# --------------------------------------------------------------------------

def clear_scene():
    for coll in (bpy.data.objects, bpy.data.meshes, bpy.data.materials,
                 bpy.data.cameras, bpy.data.lights, bpy.data.images, bpy.data.curves):
        for item in list(coll):
            coll.remove(item)
    COLOR_OVERRIDES.clear()


def link(obj):
    bpy.context.scene.collection.objects.link(obj)
    return obj


def make_empty(name, location=(0, 0, 0), parent=None):
    e = bpy.data.objects.new(name, None)
    e.empty_display_type = "PLAIN_AXES"
    e.empty_display_size = 0.2
    e.location = location
    link(e)
    if parent:
        e.parent = parent
    return e


# --------------------------------------------------------------------------
# Geometry generators: return (verts, faces, flat_face_indices)
# --------------------------------------------------------------------------

def _ring_point(t, n):
    c, s = math.cos(t), math.sin(t)
    if n == 2.0:
        return c, s
    e = 2.0 / n
    return math.copysign(abs(c) ** e, c), math.copysign(abs(s) ** e, s)


def lathe_geom(profile, segs=16, rx=1.0, ry=1.0, n=2.0, cap_bottom=True, cap_top=True):
    """Revolve a (radius, z) profile (bottom -> top) around Z.

    rx/ry scale the ring into an ellipse, n > 2 turns it into a rounded square
    (superellipse). radius 0 creates a pole vertex.
    """
    verts, faces, flat = [], [], set()
    rings = []
    for r, z in profile:
        if r <= 1e-6:
            rings.append([len(verts)])
            verts.append((0.0, 0.0, z))
        else:
            idx = []
            for i in range(segs):
                x, y = _ring_point(2 * math.pi * i / segs, n)
                idx.append(len(verts))
                verts.append((x * r * rx, y * r * ry, z))
            rings.append(idx)
    for a, b in zip(rings, rings[1:]):
        if len(a) == 1 and len(b) == 1:
            continue
        for i in range(segs):
            j = (i + 1) % segs
            if len(a) == 1:
                faces.append((a[0], b[j], b[i]))
            elif len(b) == 1:
                faces.append((a[i], a[j], b[0]))
            else:
                faces.append((a[i], a[j], b[j], b[i]))
    if cap_bottom and len(rings[0]) > 1:
        flat.add(len(faces))
        faces.append(tuple(reversed(rings[0])))
    if cap_top and len(rings[-1]) > 1:
        flat.add(len(faces))
        faces.append(tuple(rings[-1]))
    return verts, faces, flat


def sphere_profile(r, rings=8):
    return [(r * math.sin(math.pi * k / rings), -r * math.cos(math.pi * k / rings))
            for k in range(rings + 1)]


def capsule_profile(r, length, hemi=3):
    prof = []
    for k in range(hemi + 1):
        a = math.pi / 2 * k / hemi
        prof.append((r * math.sin(a), -r * math.cos(a)))
    for k in range(hemi + 1):
        a = math.pi / 2 * k / hemi
        prof.append((r * math.cos(a), length + r * math.sin(a)))
    return prof


def box_geom(size, bevel=0.02, segments=2):
    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co = Vector((v.co.x * size[0], v.co.y * size[1], v.co.z * size[2]))
    if bevel > 0:
        bevel = min(bevel, min(size) * 0.49)
        bmesh.ops.bevel(bm, geom=list(bm.verts) + list(bm.edges), offset=bevel,
                        offset_type="OFFSET", profile_type="SUPERELLIPSE",
                        segments=segments, profile=0.5, affect="EDGES",
                        clamp_overlap=True)
    bm.verts.index_update()
    verts = [tuple(v.co) for v in bm.verts]
    faces = [tuple(v.index for v in f.verts) for f in bm.faces]
    bm.free()
    return verts, faces, set()


def torus_geom(R, r, segs=16, minor=6):
    verts, faces = [], []
    for i in range(segs):
        a = 2 * math.pi * i / segs
        for j in range(minor):
            b = 2 * math.pi * j / minor
            rr = R + r * math.cos(b)
            verts.append((rr * math.cos(a), rr * math.sin(a), r * math.sin(b)))
    for i in range(segs):
        for j in range(minor):
            i2, j2 = (i + 1) % segs, (j + 1) % minor
            faces.append((i * minor + j, i2 * minor + j, i2 * minor + j2, i * minor + j2))
    return verts, faces, set()


def align_z(a, b):
    """Matrix placing local origin at a and local +Z along a->b. Returns (matrix, length)."""
    a, b = Vector(a), Vector(b)
    d = b - a
    length = d.length
    dn = d.normalized()
    up = "Y" if abs(dn.y) < 0.9 else "X"
    q = dn.to_track_quat("Z", up)
    return Matrix.Translation(a) @ q.to_matrix().to_4x4(), length


def rot(x=0.0, y=0.0, z=0.0):
    """Euler rotation (degrees) as 4x4."""
    return (Matrix.Rotation(math.radians(z), 4, "Z") @ Matrix.Rotation(math.radians(y), 4, "Y")
            @ Matrix.Rotation(math.radians(x), 4, "X"))


def T(x, y, z):
    return Matrix.Translation(Vector((x, y, z)))


def S(x, y=None, z=None):
    y = x if y is None else y
    z = x if z is None else z
    return Matrix.Diagonal(Vector((x, y, z, 1.0)))


class Part:
    """Accumulates primitive geometry (in world space) for one mesh object."""

    def __init__(self):
        self.verts = []
        self.faces = []  # (indices, material name, smooth)

    def add(self, geom, mat, matrix=None, smooth=True):
        verts, faces, flat = geom
        m = matrix if matrix is not None else Matrix.Identity(4)
        base = len(self.verts)
        self.verts.extend(m @ Vector(v) for v in verts)
        flip = m.to_3x3().determinant() < 0
        for i, f in enumerate(faces):
            idx = tuple(base + k for k in f)
            if flip:
                idx = tuple(reversed(idx))
            self.faces.append((idx, mat, smooth and i not in flat))
        return self

    # convenience primitives --------------------------------------------
    def sphere(self, c, r, mat, segs=12, rings=8, scale=(1, 1, 1), matrix=None):
        m = T(*c) @ (matrix if matrix is not None else Matrix.Identity(4)) @ S(*scale)
        return self.add(lathe_geom(sphere_profile(r, rings), segs), mat, m)

    def capsule(self, a, b, r, mat, segs=10, hemi=3, scale_xy=(1, 1)):
        m, length = align_z(a, b)
        return self.add(lathe_geom(capsule_profile(r, length, hemi), segs, scale_xy[0], scale_xy[1]), mat, m)

    def cyl(self, a, b, r, mat, r2=None, segs=12, caps=True, n=2.0):
        m, length = align_z(a, b)
        r2 = r if r2 is None else r2
        prof = [(r, 0.0), (r2, length)]
        return self.add(lathe_geom(prof, segs, n=n, cap_bottom=caps, cap_top=caps), mat, m)

    def lathe(self, profile, mat, c=(0, 0, 0), segs=16, rx=1.0, ry=1.0, n=2.0,
              matrix=None, caps=True):
        m = T(*c) @ (matrix if matrix is not None else Matrix.Identity(4))
        return self.add(lathe_geom(profile, segs, rx, ry, n, caps, caps), mat, m)

    def box(self, c, size, mat, bevel=0.02, segments=2, matrix=None, smooth=True):
        m = T(*c) @ (matrix if matrix is not None else Matrix.Identity(4))
        return self.add(box_geom(size, bevel, segments), mat, m, smooth)

    def torus(self, c, R, r, mat, matrix=None, segs=16, minor=6):
        m = T(*c) @ (matrix if matrix is not None else Matrix.Identity(4))
        return self.add(torus_geom(R, r, segs, minor), mat, m)

    def tris(self):
        return sum(len(f[0]) - 2 for f in self.faces)


def make_object(name, part, pivot=(0, 0, 0), parent=None, scale=1.0):
    """Create a mesh object whose origin is at `pivot` (world space)."""
    pivot = Vector(pivot) * scale
    me = bpy.data.meshes.new(name)
    verts = [tuple(v * scale - pivot) for v in part.verts]
    mats = []
    for _, m, _ in part.faces:
        if m not in mats:
            mats.append(m)
    me.from_pydata(verts, [], [f[0] for f in part.faces])
    for mname in mats:
        me.materials.append(get_material(mname))
    for poly, (_, m, smooth) in zip(me.polygons, part.faces):
        poly.material_index = mats.index(m)
        poly.use_smooth = smooth
    me.validate()
    me.update()
    obj = bpy.data.objects.new(name, me)
    link(obj)
    if parent is not None:
        obj.parent = parent
        obj.location = pivot - parent.matrix_world.translation
    else:
        obj.location = pivot
    return obj


# --------------------------------------------------------------------------
# Export
# --------------------------------------------------------------------------

def hierarchy(root):
    out = [root]
    for c in root.children:
        out.extend(hierarchy(c))
    return out


def obj_tris(obj):
    if obj.type != "MESH":
        return 0
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def export_fbx(root, rel_path):
    """Export `root` and its children to Assets/Cubestruction/Models/<rel_path>."""
    path = os.path.join(MODELS_DIR, rel_path)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.context.view_layer.update()
    for o in bpy.context.scene.objects:
        o.select_set(False)
    objs = hierarchy(root)
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = root
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=True,
        object_types={"EMPTY", "MESH"},
        apply_unit_scale=True,
        apply_scale_options="FBX_SCALE_ALL",
        bake_space_transform=True,
        axis_forward="-Z",
        axis_up="Y",
        use_mesh_modifiers=True,
        mesh_smooth_type="FACE",
        use_tspace=False,
        add_leaf_bones=False,
        bake_anim=False,
        path_mode="AUTO",
        embed_textures=False,
    )
    per = {o.name: obj_tris(o) for o in objs if o.type == "MESH"}
    total = sum(per.values())
    rel = os.path.relpath(path, REPO_ROOT)
    REPORT.append((rel, total, per))
    print(f"[cubestruction] exported {rel}: {total} tris")
    return path


def print_report():
    print("\n==== Cubestruction FBX report ====")
    for rel, total, per in REPORT:
        parts = ", ".join(f"{k}={v}" for k, v in per.items()) if len(per) > 1 else ""
        print(f"{total:6d} tris  {rel}  {parts}")
    print("==================================\n")
