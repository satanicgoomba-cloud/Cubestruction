# Blender asset generators

Procedural Blender Python scripts that generate every Cubestruction FBX model
(Frutiger Aero style: glossy, bubbly, sky-blue / aqua / lime). Nothing is
hand-modelled, so edit the scripts and rebuild rather than editing the FBX files.

## Rebuild

From the repo root:

```sh
blender -b --python Blender/build_all.py                    # all FBX + preview PNGs
blender -b --python Blender/build_all.py -- --no-preview    # FBX only (fast)
```

Without a Blender install you can use the `bpy` pip module (Blender as a Python
module; the wheel for Python 3.13 is bpy 5.x):

```sh
pip install bpy
python3 Blender/build_all.py [--no-preview]
```

Individual generators also run on their own, e.g. `blender -b --python Blender/tools.py`.

## Scripts

| Script | Output (under `Assets/Cubestruction/Models/`) |
|---|---|
| `common.py` | helpers: primitives, glossy materials (`PALETTE`), FBX export |
| `buddy.py` | `Buddy.fbx` |
| `tools.py` | `Tools/<Id>.fbx` (24 tools) |
| `units.py` | `Units/<Id>.fbx` (6 RTS enemy units) |
| `environment.py` | `Env/Pedestal.fbx`, `Env/Bubble.fbx`, `Env/ShopKiosk.fbx` |
| `preview.py` | re-imports the FBX files and renders `Docs/previews/*.png` (Cycles, CPU) |
| `build_all.py` | runs everything, prints triangle counts, checks every file exists |

## Conventions

* FBX export: `axis_forward='-Z'`, `axis_up='Y'`, `bake_space_transform=True`,
  `apply_scale_options='FBX_SCALE_ALL'`, meshes + empties only. 1 unit = 1 m.
* Models face / point along **Blender -Y**, which these settings turn into
  **Unity +Z** (forward). Up is Blender +Z = Unity +Y. A character's left side is
  Blender +X (Unity -X).
* Buddy: root `Buddy` at the feet, ~1.0 tall, child meshes `Head` (pivot at neck),
  `Torso` (centre), `ArmL`/`ArmR` (shoulders), `LegL`/`LegR` (hips). Materials
  `Buddy_Body` / `Buddy_Head` are near-white so Unity can tint them.
* Units: same part names/pivots as Buddy (CannonCart has a single `Body`), props
  merged into the part that carries them; `Buddy_*` materials are pre-coloured.
* Tools: single mesh named exactly the tool Id, origin at the grip point
  (wearables: attach point), materials `Tool_*`.
* Budgets (Quest standalone): characters < 3k tris, tools < 1.5k tris.
