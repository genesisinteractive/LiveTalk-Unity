"""Create and finalize an MB-Lab character.

  Blender --background --python mblab_build.py -- --out work/mblab_char.blend \\
      [--character f_ca01]
"""
import os
import sys
import traceback
import argparse

import bpy
import addon_utils

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import driver_config  # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
ap = argparse.ArgumentParser()
ap.add_argument("--out", required=True)
ap.add_argument("--character", default=None)
args = ap.parse_args(argv)

cfg = (driver_config.load().get("character") or {})
character = args.character or cfg.get("name") or "f_ca01"
use_ik = bool(cfg.get("use_ik", False))
use_muscle = bool(cfg.get("use_muscle", False))

# clean default scene first, then enable (default_set=True registers the
# addon in preferences.addons so remove_censors() can find its prefs)
bpy.ops.wm.read_homefile(use_empty=True)
addon_utils.modules_refresh()
addon_utils.enable("MB-Lab", default_set=True, persistent=False)
print("prefs entry:", bpy.context.preferences.addons.get("MB-Lab"))
scn = bpy.context.scene

scn.mblab_character_name = character
scn.mblab_use_ik = use_ik
scn.mblab_use_muscle = use_muscle
scn.mblab_use_cycles = True      # build the skin node material
scn.mblab_use_eevee = False      # addon would set legacy 'BLENDER_EEVEE' (gone in 4.5)
scn.mblab_use_lamps = False

print("character:", character)
print("engine ids:", [i.identifier for i in scn.render.bl_rna.properties["engine"].enum_items])

try:
    r = bpy.ops.mbast.init_character()
    print("init_character ->", r)
except Exception:
    traceback.print_exc()
    sys.exit(2)

body = None
for o in scn.objects:
    print("obj:", o.name, o.type, o.get("manuellab_id"))
    if o.type == "MESH" and o.get("manuellab_id"):
        body = o
if body is None:
    print("NO BODY")
    sys.exit(3)

# finalize -> expressions become shape keys
bpy.context.view_layer.objects.active = body
body.select_set(True)
scn.mblab_remove_all_modifiers = False
scn.mblab_final_prefix = "LP"
try:
    r = bpy.ops.mbast.finalize_character()
    print("finalize ->", r)
except Exception:
    traceback.print_exc()
    sys.exit(4)

for o in scn.objects:
    print("post:", o.name, o.type, o.parent.name if o.parent else None)
    if o.type == "MESH" and o.data.shape_keys:
        names = [k.name for k in o.data.shape_keys.key_blocks]
        print("  shapekeys:", len(names))
        print("  ", [n for n in names if "eyeClosed" in n or "Smile" in n or "brow" in n])
        print("  mats:", [m.name for m in o.data.materials])
        print("  verts:", len(o.data.vertices), "dims:", tuple(round(v, 3) for v in o.dimensions))
    if o.type == "ARMATURE":
        print("  bones:", [b.name for b in o.data.bones if "head" in b.name.lower() or "neck" in b.name.lower()])

out = os.path.abspath(args.out)
parent = os.path.dirname(out)
if parent:
    os.makedirs(parent, exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=out)
print("SAVED", out)
