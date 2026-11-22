"""Build every Cubestruction FBX asset (and preview renders).

From the repo root:
    blender -b --python Blender/build_all.py                # assets + previews
    blender -b --python Blender/build_all.py -- --no-preview
or, with the `bpy` pip module (Python 3.13: `pip install bpy`):
    python3 Blender/build_all.py [--no-preview]
"""

import os
import sys

sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import common as C  # noqa: E402
import buddy  # noqa: E402
import environment  # noqa: E402
import tools  # noqa: E402
import units  # noqa: E402

EXPECTED = (["Buddy.fbx"]
            + [f"Tools/{t}.fbx" for t in tools.TOOLS]
            + [f"Units/{u}.fbx" for u in units.UNITS]
            + [f"Env/{e}.fbx" for e in environment.ENV])


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else sys.argv[1:]
    buddy.build()
    tools.build()
    units.build()
    environment.build()
    C.print_report()

    missing = [p for p in EXPECTED
               if not os.path.isfile(os.path.join(C.MODELS_DIR, p))
               or os.path.getsize(os.path.join(C.MODELS_DIR, p)) == 0]
    if missing:
        raise SystemExit(f"Missing/empty FBX files: {missing}")
    print(f"[cubestruction] all {len(EXPECTED)} FBX files present")

    if "--no-preview" not in args:
        import preview
        preview.preview_buddy()
        preview.preview_tools(list(tools.TOOLS))
        preview.preview_units(list(units.UNITS))
        preview.preview_env()


if __name__ == "__main__":
    main()
