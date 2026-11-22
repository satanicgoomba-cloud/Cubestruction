"""Titan model for Titan Siege: imports the supplied rigged FBX (Blender/sources/titan/Titan.fbx),
decimates it to ~16k triangles for Quest/mobile and exports Models/Titan/Titan.fbx with its armature.
Textures (Titan_1/6/11.png, 1024 px) sit next to it; the project builder makes the materials.
Run: Blender -b --factory-startup --python Blender/titan.py"""
import bpy, os
HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "sources", "titan", "Titan.fbx")
OUT = os.path.join(HERE, "..", "Assets", "Cubestruction", "Models", "Titan", "Titan.fbx")
TARGET = {"blank.004": 11000, "blank.001": 2600, "blank.002": 2200}   # body, head, hair/helmet
NAMES = {"blank.004": "TitanBody", "blank.001": "TitanHead", "blank.002": "TitanHair"}
MATS = {"Material.007": "Titan_11", "Material.010": "Titan_1", "Material.006": "Titan_6"}

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=SRC)
for o in list(bpy.data.objects):
    if o.type != "MESH": continue
    tris = sum(len(p.vertices) - 2 for p in o.data.polygons)
    want = TARGET.get(o.name, 2000)
    if tris > want:
        m = o.modifiers.new("dec", "DECIMATE"); m.ratio = want / tris
        bpy.context.view_layer.objects.active = o
        mods = [x for x in o.modifiers if x.type == "DECIMATE"]
        bpy.ops.object.modifier_move_to_index(modifier="dec", index=0)
        bpy.ops.object.modifier_apply(modifier="dec")
    o.name = NAMES.get(o.name, o.name)
    for mat in o.data.materials:
        if mat and mat.name in MATS: mat.name = MATS[mat.name]
    print("TITAN", o.name, sum(len(p.vertices) - 2 for p in o.data.polygons))
bpy.ops.export_scene.fbx(filepath=OUT, use_selection=False, object_types={"ARMATURE", "MESH"},
                         add_leaf_bones=False, bake_anim=False, path_mode="STRIP",
                         apply_scale_options="FBX_SCALE_ALL", axis_forward="-Z", axis_up="Y",
                         bake_space_transform=True, mesh_smooth_type="FACE")
print("TITAN exported", OUT)
