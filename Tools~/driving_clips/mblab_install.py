"""Download is handled by ``build.py``. This script copies MB-Lab into
this Blender's user addons folder and enables it.

  Blender --background --python mblab_install.py -- --src /path/to/MB-Lab-1_8_1
"""
import os
import shutil
import sys
import traceback
import argparse

import bpy
import addon_utils

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
ap = argparse.ArgumentParser()
ap.add_argument("--src", required=True, help="extracted MB-Lab folder (contains __init__.py)")
ap.add_argument("--force", action="store_true", help="replace an existing install")
args = ap.parse_args(argv)

src = os.path.abspath(args.src)
if not os.path.isfile(os.path.join(src, "__init__.py")):
    print("not an addon folder:", src)
    sys.exit(2)

addons = bpy.utils.user_resource("SCRIPTS", path="addons", create=True)
dst = os.path.join(addons, "MB-Lab")
if os.path.isdir(dst) and args.force:
    shutil.rmtree(dst)
    print("removed", dst)
if not os.path.isdir(dst):
    shutil.copytree(src, dst)
    print("copied MB-Lab to", dst)
else:
    print("MB-Lab already at", dst)

addon_utils.modules_refresh()
try:
    mod = addon_utils.enable("MB-Lab", default_set=True, persistent=False)
    print("enable ->", mod)
except Exception:
    traceback.print_exc()
    sys.exit(2)

print("has mbast.init_character:", hasattr(bpy.ops.mbast, "init_character"))
scn = bpy.context.scene
print("characters:", [i.identifier for i in scn.bl_rna.properties["mblab_character_name"].enum_items])
