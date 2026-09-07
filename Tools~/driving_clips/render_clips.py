"""Render the driving clips described in ``clips.py`` from the rich
character.

  Blender --background --python render_clips.py -- \
      --blend work/mblab_char_rich.blend --frames work/frames \
      [--clips talk-neutral,smile] [--stride 25] [--mp4 ../../Resources/driving] [--save-blend]

For each clip: bake every channel per frame (procedural layers + gesture
curves), render frames/<clip>/frame_NNNN.png, and when --mp4 is given
encode <clip>.mp4 with ffmpeg (H.264 High, yuv420p, 25 fps — the container
family of the stock clips).  --stride N renders every Nth frame only, for a
quick look.  --save-blend writes frames/<clip>.blend with the keyed
animation for inspection in the GUI.
"""
import os, sys, math, random, argparse, subprocess, shutil
import bpy
from mathutils import noise, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lp_scene as S            # noqa: E402
import clips as CLIPDEF         # noqa: E402
import driver_config            # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
ap = argparse.ArgumentParser()
ap.add_argument("--blend", required=True)
ap.add_argument("--frames", required=True)
ap.add_argument("--clips", default=",".join(CLIPDEF.CLIPS.keys()))
ap.add_argument("--stride", type=int, default=1)
ap.add_argument("--mp4", default=None, help="folder to write <clip>.mp4 into")
ap.add_argument("--samples", type=int, default=0, help="override TAA samples")
ap.add_argument("--save-blend", action="store_true")
ap.add_argument("--crf", type=int, default=21)
args = ap.parse_args(argv)

FPS = CLIPDEF.FPS

# channel -> (max shape key, min shape key). None = no key for that sign.
CHANNELS = {
    "browL": ("Expressions_browOutVertL_max", "Expressions_browOutVertL_min"),
    "browR": ("Expressions_browOutVertR_max", "Expressions_browOutVertR_min"),
    "browMid": ("Expressions_browsMidVert_max", "Expressions_browsMidVert_min"),
    "squeezeL": ("Expressions_browSqueezeL_max", "Expressions_browSqueezeL_min"),
    "squeezeR": ("Expressions_browSqueezeR_max", "Expressions_browSqueezeR_min"),
    "eyeL": ("Expressions_eyeClosedL_max", "Expressions_eyeClosedL_min"),
    "eyeR": ("Expressions_eyeClosedR_max", "Expressions_eyeClosedR_min"),
    "squintL": ("Expressions_eyeSquintL_max", None),
    "squintR": ("Expressions_eyeSquintR_max", None),
    "cheekL": ("Expressions_cheekSneerL_max", None),
    "cheekR": ("Expressions_cheekSneerR_max", None),
    "smile": ("Expressions_mouthSmile_max", "Expressions_mouthSmile_min"),
    "smileL": ("Expressions_mouthSmileL_max", None),
    "smileR": ("Expressions_mouthSmileR_max", None),
    "open": ("Expressions_mouthOpen_max", None),
    "press": ("Expressions_mouthClosed_max", "Expressions_mouthClosed_min"),
    "wide": ("Expressions_mouthHoriz_max", "Expressions_mouthHoriz_min"),
    "lowerOut": ("Expressions_mouthLowerOut_max", "Expressions_mouthLowerOut_min"),
    "gazeH": ("Expressions_eyesHoriz_max", "Expressions_eyesHoriz_min"),
    "gazeV": ("Expressions_eyesVert_max", "Expressions_eyesVert_min"),
    "chest": ("Expressions_chestExpansion_max", "Expressions_chestExpansion_min"),
    "swallow": ("Expressions_deglutition_max", None),
    "nostrils": ("Expressions_nostrilsExpansion_max", None),
}
BONE_CHANNELS = ("pitch", "yaw", "roll", "spine_roll", "spine_pitch", "rise")
# Channels allowed above 1.0 (both the _max and _min key). LivePortrait
# transfers brows and mouth corners conservatively (measured 0.3-0.7x), and
# MB-Lab's mouthSmile_min at 1.0 drops the corners by only ~5 px at 512, so
# a legible frown needs the key over-driven.
OVERDRIVE = ("browL", "browR", "browMid", "smile", "lowerOut", "wide")
OVERDRIVE_MAX = 1.7
GAZE_PER_DEG = 1.0 / 36.0       # eyesHoriz 1.0 ~ 36 deg (measured on the atlas)


def deep_merge(base, over):
    out = dict(base)
    for k, v in over.items():
        out[k] = deep_merge(base[k], v) if isinstance(v, dict) and isinstance(base.get(k), dict) else v
    return out


# ---------------------------------------------------------------- curves
def smoothstep(x):
    x = max(0.0, min(1.0, x))
    return x * x * (3 - 2 * x)


class Curve:
    """Monotone cubic (Fritsch-Carlson) through (t, v) breakpoints, 0 outside."""

    def __init__(self, pts):
        pts = sorted(pts)
        self.t = [p[0] for p in pts]; self.v = [p[1] for p in pts]
        n = len(pts)
        d = [(self.v[i + 1] - self.v[i]) / (self.t[i + 1] - self.t[i]) for i in range(n - 1)]
        m = [0.0] * n
        for i in range(1, n - 1):
            if d[i - 1] * d[i] <= 0:
                m[i] = 0.0
            else:
                w1 = 2 * (self.t[i + 1] - self.t[i]) + (self.t[i] - self.t[i - 1])
                w2 = (self.t[i + 1] - self.t[i]) + 2 * (self.t[i] - self.t[i - 1])
                m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i])
        self.m = m                     # zero tangents at both ends: eased in and out

    def __call__(self, t):
        if t <= self.t[0] or t >= self.t[-1]:
            return 0.0 if (t < self.t[0] or t > self.t[-1]) else (self.v[0] if t == self.t[0] else self.v[-1])
        i = 0
        while self.t[i + 1] < t:
            i += 1
        h = self.t[i + 1] - self.t[i]
        s = (t - self.t[i]) / h
        h00 = 2 * s ** 3 - 3 * s ** 2 + 1; h10 = s ** 3 - 2 * s ** 2 + s
        h01 = -2 * s ** 3 + 3 * s ** 2; h11 = s ** 3 - s ** 2
        return h00 * self.v[i] + h10 * h * self.m[i] + h01 * self.v[i + 1] + h11 * h * self.m[i + 1]


def envelope(t, T, e):
    return smoothstep(t / e) * smoothstep((T - t) / e)


def snoise(x, seed):
    return noise.noise(Vector((x, seed * 0.731, seed * 1.37)))


# ---------------------------------------------------------------- bake one clip
def bake(name, spec):
    spec = deep_merge(CLIPDEF.DEFAULTS, spec)
    T = spec["seconds"]
    n_frames = int(round(T * FPS)) + 1          # frame 0 .. N, first == last pose
    rng = random.Random(spec["seed"])
    E = spec["envelope"]
    gest = {ch: Curve(pts) for ch, pts in spec["gestures"].items()}
    br, sw, bl, sac, mic, asym = (spec[k] for k in ("breath", "sway", "blinks", "saccades", "micro", "asym"))

    # ---- blink schedule
    beats = list(spec["beats"])
    blink_times = list(bl.get("forced", []))
    t = bl["edge"] + rng.uniform(*bl["interval"]) * 0.5
    while t < T - bl["edge"] - 0.4:
        if not bl.get("forced"):
            ok = all(abs(t - b) > bl["avoid"] for b in beats) and all(abs(t - f) > 0.6 for f in blink_times)
            if ok:
                blink_times.append(t)
            else:
                t += 0.5
                continue
        t += rng.uniform(*bl["interval"])
    blink_times.sort()
    cf, of = bl["close_frames"], bl["open_frames"]

    def blink_value(tt):
        best = 0.0
        for b in blink_times:
            f = (tt - b) * FPS
            if 0 <= f <= cf:
                best = max(best, smoothstep(f / cf))
            elif cf < f <= cf + of:
                best = max(best, 1 - smoothstep((f - cf) / of))
        return best

    def dart_value(tt):
        # a small gaze flick during the 3 frames before a blink
        for b in blink_times:
            f = (tt - b) * FPS
            if -3 <= f < 0:
                return smoothstep((f + 3) / 3.0)
        return 0.0

    # ---- saccade schedule: (time, dh_deg, dv_deg) targets, eased in over move_frames
    sacs = []
    t = 0.6
    while t < T - E:
        sacs.append((t, rng.uniform(-1, 1) * sac["max_deg"], rng.uniform(-0.6, 0.6) * sac["max_deg"]))
        t += rng.uniform(*sac["interval"])
    mf = sac["move_frames"] / FPS

    def gaze_offset(tt):
        h = v = 0.0
        prev = (0.0, 0.0, 0.0)
        for s in sacs:
            if tt < s[0]:
                break
            prev = s
        # current target eased from the previous one
        idx = sacs.index(prev) if prev in sacs else -1
        if idx < 0:
            return 0.0, 0.0
        before = sacs[idx - 1] if idx > 0 else (0.0, 0.0, 0.0)
        a = smoothstep((tt - prev[0]) / mf)
        h = before[1] + (prev[1] - before[1]) * a
        v = before[2] + (prev[2] - before[2]) * a
        return h, v

    # ---- breathing
    period = 60.0 / br["bpm"]
    deep = br.get("deep_at", [])

    def breath(tt):
        ph = (tt / period) % 1.0
        # inhale ~40 % of the cycle, exhale slower
        x = ph / 0.4 if ph < 0.4 else 1 - (ph - 0.4) / 0.6
        b = smoothstep(x)
        gain = 1.0
        for d in deep:
            gain += (br["deep_gain"] - 1.0) * math.exp(-((tt - d) / 1.6) ** 2)
        return b * gain

    # ---- sway
    P = sw["periods"]
    seed = spec["seed"]

    def sway(tt):
        yaw = sw["yaw"] * (0.6 * math.sin(2 * math.pi * tt / P[0] + 0.4) + 0.4 * sw["noise"] * snoise(tt * 0.18, seed + 1) * 2)
        pit = sw["pitch"] * (0.6 * math.sin(2 * math.pi * tt / P[1] + 2.1) + 0.4 * sw["noise"] * snoise(tt * 0.16, seed + 2) * 2)
        rol = sw["roll"] * (0.6 * math.sin(2 * math.pi * tt / P[2] + 1.0) + 0.4 * sw["noise"] * snoise(tt * 0.14, seed + 3) * 2)
        return yaw, pit, rol

    # ---- bake per frame
    frames = []
    for f in range(n_frames):
        tt = f / FPS
        env = envelope(tt, T, E)
        ch = {c: 0.0 for c in list(CHANNELS) + list(BONE_CHANNELS)}
        for c, curve in gest.items():
            ch[c] += curve(tt)
        yaw, pit, rol = sway(tt)
        ch["yaw"] += yaw * env; ch["pitch"] += pit * env; ch["roll"] += rol * env
        ch["spine_roll"] += sw["spine"] * math.sin(2 * math.pi * tt / (P[0] * 1.9) + 0.7) * env
        ch["spine_pitch"] += sw["spine"] * 0.6 * math.sin(2 * math.pi * tt / (P[1] * 1.6)) * env
        b = breath(tt) * env
        ch["chest"] += br["chest"] * b
        ch["rise"] += br["rise"] * b
        ch["pitch"] += br["pitch"] * b
        # gaze: saccades + vestibulo-ocular counter-rotation against head yaw/pitch
        gh, gv = gaze_offset(tt)
        dart = dart_value(tt) * bl["dart"] * env
        # signs verified on renders: eyesHoriz_max follows +yaw, eyesVert_max follows +pitch
        ch["gazeH"] += (gh * env + dart) * GAZE_PER_DEG + sac["vor"] * ch["yaw"] * GAZE_PER_DEG
        ch["gazeV"] += gv * env * GAZE_PER_DEG + sac["vor_pitch"] * ch["pitch"] * GAZE_PER_DEG
        # blinks
        bv = blink_value(tt)
        ch["eyeL"] = max(ch["eyeL"], bv * bl["asym"][0]) if bv > 0 else ch["eyeL"]
        ch["eyeR"] = max(ch["eyeR"], bv * bl["asym"][1]) if bv > 0 else ch["eyeR"]
        ch["browL"] -= bl["brow_drop"] * bv; ch["browR"] -= bl["brow_drop"] * bv
        # micro life
        ch["browL"] += mic["brow"] * snoise(tt / mic["period"], seed + 11) * 2 * env
        ch["browR"] += mic["brow"] * snoise(tt / mic["period"], seed + 12) * 2 * env
        ch["smileL"] += max(0.0, mic["mouth"] * snoise(tt / mic["period"], seed + 13) * 2) * env
        ch["smileR"] += max(0.0, mic["mouth"] * snoise(tt / mic["period"], seed + 14) * 2) * env
        ch["press"] += mic["mouth"] * snoise(tt / mic["period"], seed + 15) * 2 * env
        # asymmetry on paired expression channels
        ch["browL"] *= asym["brow"][0]; ch["browR"] *= asym["brow"][1]
        ch["squintL"] *= asym["squint"][0]; ch["squintR"] *= asym["squint"][1]
        ch["cheekL"] *= asym["cheek"][0]; ch["cheekR"] *= asym["cheek"][1]
        if ch["smile"] > 0:
            ch["smileL"] += ch["smile"] * (asym["smile"][0] - 1.0) * 0.5
            ch["smileR"] += ch["smile"] * (asym["smile"][1] - 1.0) * 0.5
        frames.append(ch)
    return frames, n_frames, blink_times


# ---------------------------------------------------------------- apply to rig
def clear_animation(body, arm):
    if body.data.shape_keys.animation_data:
        body.data.shape_keys.animation_data_clear()
    if arm.animation_data:
        arm.animation_data_clear()
    for k in body.data.shape_keys.key_blocks:
        k.value = 0.0
    for pb in arm.pose.bones:
        pb.rotation_mode = 'XYZ'
        pb.rotation_euler = (0, 0, 0)
        pb.location = (0, 0, 0)


def apply(body, arm, frames):
    kb = body.data.shape_keys.key_blocks
    head, neck, spine = arm.pose.bones["head"], arm.pose.bones["neck"], arm.pose.bones["spine03"]
    NECK_SHARE = 0.35
    used_keys = set()
    for ch, (kmax, kmin) in CHANNELS.items():
        used_keys.add(kmax)
        if kmin:
            used_keys.add(kmin)
    # brow keys may be over-driven (LivePortrait's brow channel is conservative)
    for c in OVERDRIVE:
        for k in CHANNELS[c]:
            if k:
                kb[k].slider_max = OVERDRIVE_MAX
    for f, ch in enumerate(frames):
        for c, (kmax, kmin) in CHANNELS.items():
            v = ch[c]
            lim = OVERDRIVE_MAX if c in OVERDRIVE else 1.0
            vmax = max(0.0, min(lim, v)); vmin = max(0.0, min(lim, -v)) if kmin else 0.0
            kb[kmax].value = vmax
            if kmin:
                kb[kmin].value = vmin
        for k in used_keys:
            kb[k].keyframe_insert("value", frame=f)
        hp, hy, hr = (math.radians(ch["pitch"]), math.radians(ch["yaw"]), math.radians(ch["roll"]))
        head.rotation_euler = (hp * (1 - NECK_SHARE), hy * (1 - NECK_SHARE), hr * (1 - NECK_SHARE))
        neck.rotation_euler = (hp * NECK_SHARE, hy * NECK_SHARE, hr * NECK_SHARE)
        spine.rotation_euler = (math.radians(ch["spine_pitch"]), 0.0, math.radians(ch["spine_roll"]))
        spine.location = (0.0, ch["rise"], 0.0)          # bone Y runs up the spine
        for pb in (head, neck, spine):
            pb.keyframe_insert("rotation_euler", frame=f)
        spine.keyframe_insert("location", frame=f)
    # per-frame keys: linear is exact, nothing to ease
    for ad in (body.data.shape_keys.animation_data, arm.animation_data):
        for fc in ad.action.fcurves:
            for kp in fc.keyframe_points:
                kp.interpolation = 'LINEAR'


# ---------------------------------------------------------------- main
scn, body, arm = S.open_character(args.blend)
scene_cfg = driver_config.load().get("scene") or {}
samples = args.samples or int(scene_cfg.get("render_samples") or 0)
S.setup_render(
    scn, 0, 0,
    samples=samples or scn.eevee.taa_render_samples,
    engine=scene_cfg.get("engine") or S.ENGINE)
os.makedirs(args.frames, exist_ok=True)
summary = []
for name in [c for c in args.clips.split(",") if c]:
    spec = CLIPDEF.CLIPS[name]
    frames, n, blinks = bake(name, spec)
    clear_animation(body, arm)
    apply(body, arm, frames)
    out_dir = os.path.join(args.frames, name)
    if os.path.isdir(out_dir):
        shutil.rmtree(out_dir)
    os.makedirs(out_dir)
    scn.frame_start = 0; scn.frame_end = n - 1
    scn.frame_step = args.stride
    scn.render.filepath = os.path.join(out_dir, "frame_")
    print(f"[clips] {name}: {n} frames ({n / FPS:.2f}s) blinks at {[round(b, 2) for b in blinks]}")
    if args.save_blend:
        bpy.ops.wm.save_as_mainfile(filepath=os.path.join(args.frames, name + ".blend"))
    bpy.ops.render.render(animation=True)
    rendered = sorted(p for p in os.listdir(out_dir) if p.endswith(".png"))
    print(f"[clips] {name}: rendered {len(rendered)} frames -> {out_dir}")
    if args.mp4 and args.stride == 1:
        os.makedirs(args.mp4, exist_ok=True)
        mp4 = os.path.join(args.mp4, name + ".mp4")
        cmd = ["ffmpeg", "-y", "-loglevel", "error", "-framerate", str(FPS), "-i", os.path.join(out_dir, "frame_%04d.png"),
               "-c:v", "libx264", "-profile:v", "high", "-level", "3.0", "-pix_fmt", "yuv420p",
               "-crf", str(args.crf), "-preset", "slow", "-movflags", "+faststart", "-r", str(FPS), mp4]
        subprocess.run(cmd, check=True)
        print(f"[clips] {name}: encoded {mp4} ({os.path.getsize(mp4) // 1024} KB)")
    summary.append((name, n))
print("[clips] summary:", summary)
