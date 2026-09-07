#!/usr/bin/env python3
"""Build the seven ``Resources/driving/*.mp4`` clips from scratch.

Edit the SETTINGS block below, then from this folder:

    python3 build.py

That installs MB-Lab, creates and dresses the character, renders every
clip, and encodes the mp4s into the package's ``Resources/driving/``
folder (the path Unity loads). Gesture timings stay in ``clips.py`` —
everything else that changes the picture lives here.

  python3 build.py --preview          # every 25th frame, 16 samples, no mp4
  python3 build.py --clips smile      # one clip
  python3 build.py --force            # rebuild blends even if they exist
"""
from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PACKAGE_ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
DEFAULT_MP4 = os.path.join(PACKAGE_ROOT, "Resources", "driving")

# =============================================================================
# SETTINGS — tweak these, then run  python3 build.py
# =============================================================================

# --- tools -------------------------------------------------------------------
# Empty BLENDER = auto-detect (macOS app bundle, then PATH).
BLENDER = "/Applications/Blender.app/Contents/MacOS/Blender"
# MB-Lab 1.8.1 is the release that matches Blender 4.5.
MBLAB_TAG = "1_8_1"
MBLAB_URL = "https://github.com/animate1978/MB-Lab/archive/refs/tags/%s.zip" % MBLAB_TAG

# --- paths -------------------------------------------------------------------
# All intermediates land under WORK (next to this script). MP4_DIR is the
# package folder Unity reads; leave it as the default unless you are
# writing a throwaway set.
WORK = os.path.join(HERE, "work")
MP4_DIR = DEFAULT_MP4

# --- character (MB-Lab) ------------------------------------------------------
# ``f_ca01`` is the shipped Caucasian female. Other ids print when install
# runs (``characters:`` line). Changing this rebuilds the .blend.
CHARACTER = "f_ca01"
USE_IK = False
USE_MUSCLE = False

# --- scene / render (must match the bundled clips unless you mean to) --------
# LivePortrait's extractor was measured on 512² / 25 fps / 85 mm / face
# filling ~60 % of the frame. Changing these changes every avatar rebuild.
RESOLUTION = 512
FPS = 25
FOCAL_MM = 85.0
SENSOR_MM = 36.0
HEAD_FRACTION = 0.85          # chin→crown as a fraction of the frame
DOF_FSTOP = 4.0
BG = "green"                  # "green" (extractor-friendly) or "grey"
LIGHTS = "hard"               # "hard" (carved nose/folds) or "soft"
LOOK = "AgX - High Contrast"
ENGINE = "EEVEE"              # "EEVEE" or "CYCLES" (~4× slower)
WORLD_HDR = "interior.exr"
WORLD_HDR_YAW_DEG = 35.0
FILTER_SIZE = 1.8             # pixel filter; thinner hair shimmers below ~1.8

# Dressing writes these samples into the .blend; clip render overrides.
DRESS_SAMPLES = 48
RENDER_SAMPLES = 32           # ~2.5 s/frame on Apple Silicon; full set ~1 h
PREVIEW_SAMPLES = 16
PREVIEW_STRIDE = 25
ENCODE_CRF = 21               # H.264 High / yuv420p, same family as stock clips

# --- look (hair / skin / clothes) --------------------------------------------
HAIR_COLOR = (0.052, 0.030, 0.017, 1.0)
HAIR_STRANDS = 16000
HAIR_POINTS = 8
HAIR_ROOT_RADIUS = 0.00048    # mm-scale; thinner than a pixel flickers
HAIR_TIP_RADIUS = 0.00018
HAIR_ROUGHNESS = 0.58         # matte: bright strand sparkle flickers
BROW_COLOR = (0.055, 0.033, 0.019, 1.0)
BROW_STRANDS = 760            # per brow
BROW_ROOT_RADIUS = 0.00017
LASH_COLOR = (0.016, 0.010, 0.008, 1.0)
LASH_UPPER_PER_ROOT = 1
LASH_LOWER_PER_ROOT = 0
LASH_RADIUS = 0.00011
TOP_COLOR = (0.075, 0.105, 0.150, 1.0)
SKIN_SSS_WEIGHT = 0.22
SKIN_SSS_SCALE = 0.006
SKIN_ROUGHNESS = 0.46
LOOK_SEED = 7

# --- which clips -------------------------------------------------------------
# None = every key in clips.py (talk-neutral + six expressions).
CLIPS = None
STRIDE = 1                    # 1 = every frame (required to encode mp4)

# --- optional authoring checks (off for a straight generate) -----------------
RICH_TEST = False             # five stills after dressing
RICH_FLICKER = 0              # N frames of a slow yaw; 0 = skip
ATLAS = False                 # one still per shape key / head axis
ANALYZE = False               # consecutive-frame MAD on the PNG folders

# --- resume ------------------------------------------------------------------
# True: skip install / character / dress when their outputs already exist.
# After changing CHARACTER or any look knob, set FORCE True or pass --force.
RESUME = True
FORCE = False

# =============================================================================
# pipeline (you should not need to edit below here)
# =============================================================================

STAGES = ("install", "character", "dress", "render")


def die(msg, code=1):
    print("[build]", msg, file=sys.stderr)
    sys.exit(code)


def find_blender(explicit):
    if explicit and os.path.isfile(explicit):
        return explicit
    if explicit:
        print("[build] BLENDER not found at %s — auto-detecting" % explicit)
    cands = [
        "/Applications/Blender.app/Contents/MacOS/Blender",
        shutil.which("blender") or "",
        os.path.join(os.environ.get("ProgramFiles", r"C:\Program Files"),
                     "Blender Foundation", "Blender 4.5", "blender.exe"),
    ]
    for p in cands:
        if p and os.path.isfile(p):
            return p
    die("Blender 4.5 not found. Set BLENDER at the top of build.py.")


def blender_version(binary):
    out = subprocess.check_output([binary, "--version"], text=True)
    line = out.splitlines()[0] if out else ""
    # "Blender 4.5.3"
    parts = line.split()
    return parts[1] if len(parts) >= 2 else line


def download_mblab(work, tag, url, force):
    dest = os.path.join(work, "mblab_src", "MB-Lab-%s" % tag)
    marker = os.path.join(dest, "__init__.py")
    if os.path.isfile(marker) and not force:
        print("[build] MB-Lab already at", dest)
        return dest
    zip_path = os.path.join(work, "mblab_src", "%s.zip" % tag)
    os.makedirs(os.path.dirname(zip_path), exist_ok=True)
    print("[build] downloading", url)
    urllib.request.urlretrieve(url, zip_path)
    extract_root = os.path.join(work, "mblab_src")
    with zipfile.ZipFile(zip_path) as zf:
        zf.extractall(extract_root)
    if not os.path.isfile(marker):
        die("MB-Lab zip did not contain %s" % marker)
    print("[build] extracted", dest)
    return dest


def write_config(path, ns):
    payload = {
        "scene": {
            "resolution": ns.resolution,
            "fps": ns.fps,
            "focal_mm": ns.focal_mm,
            "sensor_mm": ns.sensor_mm,
            "head_fraction": ns.head_fraction,
            "dof_fstop": ns.dof_fstop,
            "bg": ns.bg,
            "lights": ns.lights,
            "look": ns.look,
            "engine": ns.engine,
            "world_hdr": ns.world_hdr,
            "world_hdr_yaw_deg": ns.world_hdr_yaw_deg,
            "filter_size": ns.filter_size,
            "dress_samples": ns.dress_samples,
            "render_samples": ns.render_samples,
        },
        "character": {
            "name": ns.character,
            "use_ik": ns.use_ik,
            "use_muscle": ns.use_muscle,
        },
        "look": {
            "hair_color": list(ns.hair_color),
            "hair_strands": ns.hair_strands,
            "hair_points": ns.hair_points,
            "hair_root_radius": ns.hair_root_radius,
            "hair_tip_radius": ns.hair_tip_radius,
            "hair_roughness": ns.hair_roughness,
            "brow_color": list(ns.brow_color),
            "brow_strands": ns.brow_strands,
            "brow_root_radius": ns.brow_root_radius,
            "lash_color": list(ns.lash_color),
            "lash_upper_per_root": ns.lash_upper_per_root,
            "lash_lower_per_root": ns.lash_lower_per_root,
            "lash_radius": ns.lash_radius,
            "top_color": list(ns.top_color),
            "skin_sss_weight": ns.skin_sss_weight,
            "skin_sss_scale": ns.skin_sss_scale,
            "skin_roughness": ns.skin_roughness,
            "seed": ns.look_seed,
        },
        "encode": {"crf": ns.crf},
    }
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(payload, f, indent=2)
        f.write("\n")
    return path


def run_blender(binary, script, args, config_path, extra_env=None):
    env = os.environ.copy()
    env["LP_DRIVER_CONFIG"] = config_path
    if extra_env:
        env.update(extra_env)
    cmd = [binary, "--background", "--python", os.path.join(HERE, script), "--"] + list(args)
    print("[build] $", " ".join(cmd))
    subprocess.run(cmd, cwd=HERE, env=env, check=True)


def parse_args():
    p = argparse.ArgumentParser(
        description="Build Resources/driving/*.mp4 from scratch. Settings: top of build.py.")
    p.add_argument("--blender", default=BLENDER,
                   help="Blender 4.5 binary (default: SETTINGS.BLENDER / auto-detect)")
    p.add_argument("--force", action="store_true", default=FORCE,
                   help="rebuild character + dress even if .blend files exist")
    p.add_argument("--preview", action="store_true",
                   help="stride %d, %d samples, write work/preview, no mp4" % (
                       PREVIEW_STRIDE, PREVIEW_SAMPLES))
    p.add_argument("--clips", default=None,
                   help="comma-separated clip names (default: SETTINGS.CLIPS or all)")
    p.add_argument("--from", dest="from_stage", choices=STAGES, default="install",
                   help="skip earlier stages (e.g. --from dress, --from render)")
    p.add_argument("--skip-install", action="store_true")
    p.add_argument("--atlas", action="store_true", default=ATLAS)
    p.add_argument("--analyze", action="store_true", default=ANALYZE)
    p.add_argument("--test", action="store_true", default=RICH_TEST,
                   help="render five dress stills into work/rich_test")
    p.add_argument("--flicker", type=int, default=RICH_FLICKER)
    p.add_argument("--mp4", default=None, help="mp4 output folder (default: SETTINGS.MP4_DIR)")
    p.add_argument("--samples", type=int, default=None, help="override RENDER_SAMPLES")
    p.add_argument("--stride", type=int, default=None, help="override STRIDE")
    p.add_argument("--crf", type=int, default=ENCODE_CRF)
    return p.parse_args()


class NS:
    """Flat namespace so write_config and main share one object."""
    pass


def settings_ns(args):
    ns = NS()
    ns.blender = args.blender
    ns.force = args.force
    ns.preview = args.preview
    ns.character = CHARACTER
    ns.use_ik = USE_IK
    ns.use_muscle = USE_MUSCLE
    ns.resolution = RESOLUTION
    ns.fps = FPS
    ns.focal_mm = FOCAL_MM
    ns.sensor_mm = SENSOR_MM
    ns.head_fraction = HEAD_FRACTION
    ns.dof_fstop = DOF_FSTOP
    ns.bg = BG
    ns.lights = LIGHTS
    ns.look = LOOK
    ns.engine = ENGINE
    ns.world_hdr = WORLD_HDR
    ns.world_hdr_yaw_deg = WORLD_HDR_YAW_DEG
    ns.filter_size = FILTER_SIZE
    ns.dress_samples = DRESS_SAMPLES
    ns.render_samples = args.samples if args.samples is not None else RENDER_SAMPLES
    ns.crf = args.crf
    ns.hair_color = HAIR_COLOR
    ns.hair_strands = HAIR_STRANDS
    ns.hair_points = HAIR_POINTS
    ns.hair_root_radius = HAIR_ROOT_RADIUS
    ns.hair_tip_radius = HAIR_TIP_RADIUS
    ns.hair_roughness = HAIR_ROUGHNESS
    ns.brow_color = BROW_COLOR
    ns.brow_strands = BROW_STRANDS
    ns.brow_root_radius = BROW_ROOT_RADIUS
    ns.lash_color = LASH_COLOR
    ns.lash_upper_per_root = LASH_UPPER_PER_ROOT
    ns.lash_lower_per_root = LASH_LOWER_PER_ROOT
    ns.lash_radius = LASH_RADIUS
    ns.top_color = TOP_COLOR
    ns.skin_sss_weight = SKIN_SSS_WEIGHT
    ns.skin_sss_scale = SKIN_SSS_SCALE
    ns.skin_roughness = SKIN_ROUGHNESS
    ns.look_seed = LOOK_SEED
    ns.clips = args.clips if args.clips is not None else CLIPS
    ns.stride = args.stride if args.stride is not None else STRIDE
    ns.mp4_dir = args.mp4 if args.mp4 is not None else MP4_DIR
    ns.work = WORK
    ns.resume = RESUME and not args.force
    ns.from_stage = args.from_stage
    ns.skip_install = args.skip_install
    ns.atlas = args.atlas
    ns.analyze = args.analyze
    ns.rich_test = args.test
    ns.flicker = args.flicker
    if args.preview:
        ns.stride = PREVIEW_STRIDE
        ns.render_samples = PREVIEW_SAMPLES
        ns.mp4_dir = None
        ns.frames = os.path.join(WORK, "preview")
    else:
        ns.frames = os.path.join(WORK, "frames")
    return ns


def want_stage(ns, name):
    return STAGES.index(name) >= STAGES.index(ns.from_stage)


def main():
    args = parse_args()
    ns = settings_ns(args)
    os.makedirs(ns.work, exist_ok=True)

    blender = find_blender(ns.blender)
    ver = blender_version(blender)
    print("[build] blender", blender, "(%s)" % ver)
    if not ver.startswith("4.5"):
        print("[build] warning: MB-Lab 1.8.1 is documented against Blender 4.5, got %s" % ver)

    if ns.mp4_dir and ns.stride == 1:
        ffmpeg = shutil.which("ffmpeg")
        if not ffmpeg:
            die("ffmpeg not on PATH (needed to encode mp4)")
        print("[build] ffmpeg", ffmpeg)

    config_path = write_config(os.path.join(ns.work, "driver_config.json"), ns)
    print("[build] wrote", config_path)

    char_blend = os.path.join(ns.work, "mblab_char.blend")
    rich_blend = os.path.join(ns.work, "mblab_char_rich.blend")

    if want_stage(ns, "install") and not ns.skip_install:
        src = download_mblab(ns.work, MBLAB_TAG, MBLAB_URL, ns.force)
        run_blender(blender, "mblab_install.py", ["--src", src] + (["--force"] if ns.force else []),
                    config_path)

    if want_stage(ns, "character"):
        if ns.resume and os.path.isfile(char_blend):
            print("[build] resume: character exists", char_blend)
        else:
            run_blender(blender, "mblab_build.py", [
                "--out", char_blend,
                "--character", ns.character,
            ], config_path)

    if want_stage(ns, "dress"):
        if ns.resume and os.path.isfile(rich_blend):
            print("[build] resume: dressed character exists", rich_blend)
        else:
            if not os.path.isfile(char_blend):
                die("missing %s — run without --from dress" % char_blend)
            rich_args = [
                "--src", char_blend, "--out", rich_blend,
                "--samples", str(ns.dress_samples),
            ]
            if ns.rich_test:
                rich_args += ["--test", os.path.join(ns.work, "rich_test")]
            if ns.flicker:
                rich_args += ["--flicker", str(ns.flicker)]
            extra = {}
            if ns.bg:
                extra["LP_BG"] = ns.bg
            extra["LP_LIGHTS"] = ns.lights
            extra["LP_LOOK"] = ns.look
            run_blender(blender, "mblab_rich.py", rich_args, config_path, extra)

    if ns.atlas:
        if not os.path.isfile(rich_blend):
            die("missing %s" % rich_blend)
        run_blender(blender, "sk_atlas.py", [
            "--blend", rich_blend, "--out", os.path.join(ns.work, "atlas"),
            "--samples", "16",
        ], config_path)

    if want_stage(ns, "render"):
        if not os.path.isfile(rich_blend):
            die("missing %s — run without --from render" % rich_blend)
        render_args = [
            "--blend", rich_blend,
            "--frames", ns.frames,
            "--stride", str(ns.stride),
            "--samples", str(ns.render_samples),
            "--crf", str(ns.crf),
        ]
        if ns.clips:
            render_args += ["--clips", ns.clips]
        if ns.mp4_dir and ns.stride == 1:
            os.makedirs(ns.mp4_dir, exist_ok=True)
            render_args += ["--mp4", ns.mp4_dir]
        run_blender(blender, "render_clips.py", render_args, config_path)
        if ns.mp4_dir and ns.stride == 1:
            print("[build] mp4s ->", ns.mp4_dir)

    if ns.analyze:
        names = ns.clips.split(",") if ns.clips else [
            "talk-neutral", "approve", "confused", "disapprove", "sad", "smile", "surprised"]
        pairs = []
        for name in names:
            folder = os.path.join(ns.frames, name)
            if os.path.isdir(folder):
                pairs.append("%s=%s" % (name, folder))
        if not pairs:
            die("no frame folders under %s" % ns.frames)
        cmd = [sys.executable, os.path.join(HERE, "analyze.py")] + pairs
        print("[build] $", " ".join(cmd))
        subprocess.run(cmd, cwd=HERE, check=True)

    print("[build] done")


if __name__ == "__main__":
    try:
        main()
    except subprocess.CalledProcessError as e:
        die("command failed (exit %s)" % e.returncode, e.returncode or 1)
