"""Shared scene helpers for the driving-clip pipeline: load a finalized
MB-Lab character, repath its textures, measure the head, and build the
camera / lights / backdrop / render settings every clip is rendered with.

Import from a script run as ``Blender --background --python x.py`` after
adding this folder to ``sys.path`` (see ``mblab_rich.py``).
"""
import os, sys, math
import bpy
from mathutils import Vector, Euler

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)
import driver_config  # noqa: E402

BODY_NAME = "LP_body"
ARM_NAME = "LP_armature"


def mblab_tex_dir():
    """MB-Lab textures in this Blender's user addons folder."""
    addons = bpy.utils.user_resource("SCRIPTS", path="addons")
    return os.path.join(addons, "MB-Lab", "data", "textures")


# Framing family shared by every clip: 85 mm, chin->crown = 85 % of a
# 512x512 frame (face ~60 %), neutral grey backdrop, soft three-point.
RES = 512
FPS = 25
FOCAL_MM = 85.0
SENSOR_MM = 36.0
HEAD_FRACTION = 0.85
BG_WORLD = (0.32, 0.32, 0.33, 1.0)
BG_WORLD_STRENGTH = 0.35
WORLD_HDR = "interior.exr"      # ships with Blender; soft indoor skylight
WORLD_HDR_YAW_DEG = 35.0        # turn the HDR so its brighter side is the key side
DOF_FSTOP = 4.0
FILTER_SIZE = 1.8
ENGINE = "EEVEE"
# Saturated green: maximal separation from skin, hair and lips for the
# extractor's landmark tracker, and no grey-on-grey with the shadow side.
BG_PLANE_GREEN = (0.05, 0.42, 0.12, 1.0)
BG_PLANE_GREY = (0.36, 0.36, 0.37, 1.0)
BG_PLANE = BG_PLANE_GREEN
LOOK = "AgX - High Contrast"
LIGHTS = "hard"


def _apply_driver_config():
    """Overlay ``build.py`` settings, then the LP_* env A/B knobs."""
    global RES, FPS, FOCAL_MM, SENSOR_MM, HEAD_FRACTION, DOF_FSTOP
    global FILTER_SIZE, ENGINE, WORLD_HDR, WORLD_HDR_YAW_DEG, BG_PLANE, LOOK, LIGHTS
    scene = driver_config.load().get("scene") or {}
    if "resolution" in scene:
        RES = int(scene["resolution"])
    if "fps" in scene:
        FPS = int(scene["fps"])
    if "focal_mm" in scene:
        FOCAL_MM = float(scene["focal_mm"])
    if "sensor_mm" in scene:
        SENSOR_MM = float(scene["sensor_mm"])
    if "head_fraction" in scene:
        HEAD_FRACTION = float(scene["head_fraction"])
    if "dof_fstop" in scene:
        DOF_FSTOP = float(scene["dof_fstop"])
    if "filter_size" in scene:
        FILTER_SIZE = float(scene["filter_size"])
    if "engine" in scene:
        ENGINE = scene["engine"]
    if "world_hdr" in scene:
        WORLD_HDR = scene["world_hdr"]
    if "world_hdr_yaw_deg" in scene:
        WORLD_HDR_YAW_DEG = float(scene["world_hdr_yaw_deg"])
    bg = scene.get("bg") or os.environ.get("LP_BG")
    if bg == "grey":
        BG_PLANE = BG_PLANE_GREY
    elif bg == "green":
        BG_PLANE = BG_PLANE_GREEN
    LIGHTS = scene.get("lights") or os.environ.get("LP_LIGHTS", LIGHTS)
    LOOK = scene.get("look") or os.environ.get("LP_LOOK", LOOK)


_apply_driver_config()


def open_character(path):
    bpy.ops.wm.open_mainfile(filepath=path)
    repath_images()
    return bpy.context.scene, bpy.data.objects[BODY_NAME], bpy.data.objects[ARM_NAME]


def repath_images():
    """humanoid_library.blend ships absolute iris texture paths into a 4.1
    addons dir; point every missing FILE image at the installed copy."""
    for img in bpy.data.images:
        if not img.has_data and img.source == 'FILE':
            cand = os.path.join(mblab_tex_dir(), os.path.basename(img.filepath))
            if os.path.isfile(cand):
                img.filepath = cand
                img.reload()


class HeadMeasure:
    """Rest-pose landmarks of the character in world space (armature at the
    origin, character facing -Y, +Z up)."""

    def __init__(self, body, arm):
        self.body, self.arm = body, arm
        mw = body.matrix_world
        bones = arm.data.bones
        self.neck_z = (arm.matrix_world @ bones["neck"].head_local).z
        self.head_top = (arm.matrix_world @ bones["head"].tail_local).z
        verts = [mw @ v.co for v in body.data.vertices]
        face_verts = [v for v in verts if v.z > self.neck_z + 0.02]
        xs = [v.x for v in face_verts]; ys = [v.y for v in face_verts]; zs = [v.z for v in face_verts]
        self.minv = Vector((min(xs), min(ys), min(zs)))
        self.maxv = Vector((max(xs), max(ys), max(zs)))
        self.head_h = self.maxv.z - self.minv.z
        self.face_front_y = self.minv.y
        self.face_center = Vector(((self.minv.x + self.maxv.x) / 2, self.minv.y,
                                   self.minv.z + self.head_h * 0.47))
        # eyes: vertices of the sclera material slot, split by side
        me = body.data
        eye_slot = next(i for i, m in enumerate(me.materials) if m.name.endswith("human_eyes"))
        eye_vs = set()
        for p in me.polygons:
            if p.material_index == eye_slot:
                eye_vs.update(p.vertices)
        eye_pts = [mw @ me.vertices[i].co for i in eye_vs]
        left = [p for p in eye_pts if p.x > 0]     # character's left is +X (she faces -Y)
        right = [p for p in eye_pts if p.x < 0]
        self.eye_l = sum(left, Vector()) / len(left)
        self.eye_r = sum(right, Vector()) / len(right)
        self.eye_z = (self.eye_l.z + self.eye_r.z) / 2
        self.eye_r_radius = max((p - self.eye_r).length for p in right)
        # skull centre estimate: middle of the head box in x/y, a little above the eyes
        self.skull_center = Vector(((self.minv.x + self.maxv.x) / 2,
                                    (self.minv.y + self.maxv.y) / 2 + 0.005,
                                    self.eye_z + 0.04))

    def describe(self):
        return ("head box %s..%s h=%.3f eye_z=%.3f neck_z=%.3f eyeL=%s eyeR=%s skull=%s" % (
            tuple(round(c, 3) for c in self.minv), tuple(round(c, 3) for c in self.maxv), self.head_h,
            self.eye_z, self.neck_z, tuple(round(c, 3) for c in self.eye_l),
            tuple(round(c, 3) for c in self.eye_r), tuple(round(c, 3) for c in self.skull_center)))


def setup_camera(scn, hm):
    """Fixed framing for every clip. Returns the camera object."""
    frame_h = hm.head_h / HEAD_FRACTION
    dist = frame_h * FOCAL_MM / SENSOR_MM
    cam_data = bpy.data.cameras.new("LPCam")
    cam_data.lens = FOCAL_MM
    cam_data.sensor_width = SENSOR_MM
    cam_data.sensor_height = SENSOR_MM
    cam_data.sensor_fit = 'VERTICAL'
    cam_data.clip_start = 0.05
    cam = bpy.data.objects.new("LPCam", cam_data)
    scn.collection.objects.link(cam)
    fc = hm.face_center
    cam.location = Vector((fc.x, hm.face_front_y - dist, fc.z))
    cam.rotation_euler = Euler((math.radians(90), 0, 0), 'XYZ')  # look along +Y
    # A real lens: focus on the eye plane, f/4 at 85 mm ≈ 4 cm of depth of
    # field, so the ears and backdrop soften the way a phone/webcam frame
    # does. Shallow enough to read as a photograph, deep enough that every
    # facial landmark the extractor tracks stays in focus.
    cam_data.dof.use_dof = True
    cam_data.dof.focus_distance = dist
    cam_data.dof.aperture_fstop = DOF_FSTOP
    scn.camera = cam
    return cam, frame_h


def _area(scn, name, loc, target, energy, size, color=(1, 1, 1)):
    d = bpy.data.lights.new(name, 'AREA')
    d.energy = energy; d.size = size; d.color = color
    o = bpy.data.objects.new(name, d)
    scn.collection.objects.link(o)
    o.location = loc
    o.rotation_euler = (Vector(target) - Vector(loc)).to_track_quat('-Z', 'Y').to_euler()
    return o


def setup_lights(scn, hm):
    fc = hm.face_center
    # Brighter and harder than a beauty setup on purpose: the driver is
    # read by a motion extractor, not a person. A small key high on the
    # key side carves the nose, the nasolabial folds and the brow ridge
    # into real shadows; the fill is kept ~5:1 under it so those creases
    # survive; a top light lifts the forehead and cheekbones.
    if LIGHTS == "soft":
        # the v2 beauty setup: big soft key, gentle ratio
        _area(scn, "Key",  (fc.x + 0.9, fc.y - 1.1, fc.z + 0.7), fc, 60, 1.0, (1.0, 0.96, 0.92))
        _area(scn, "Fill", (fc.x - 1.1, fc.y - 1.0, fc.z + 0.2), fc, 25, 1.5, (0.92, 0.95, 1.0))
        _area(scn, "Rim",  (fc.x - 0.4, fc.y + 0.9, fc.z + 0.8), fc, 30, 0.6)
    else:
        _area(scn, "Key",  (fc.x + 0.75, fc.y - 1.0, fc.z + 0.95), fc, 80, 0.45, (1.0, 0.96, 0.92))
        _area(scn, "Fill", (fc.x - 1.1, fc.y - 1.0, fc.z + 0.2), fc, 26, 1.5, (0.92, 0.95, 1.0))
        _area(scn, "Top",  (fc.x + 0.1, fc.y - 0.5, fc.z + 1.3), fc, 20, 0.6)
        _area(scn, "Rim",  (fc.x - 0.4, fc.y + 0.9, fc.z + 0.8), fc, 30, 0.6)
    # a small frontal catch light so the eyes carry a wet highlight
    _area(scn, "Catch", (fc.x + 0.15, fc.y - 1.6, fc.z + 0.25), fc, 6, 0.25, (1.0, 0.98, 0.95))


def setup_backdrop(scn, hm):
    fc = hm.face_center
    world = bpy.data.worlds.new("LPWorld"); scn.world = world
    world.use_nodes = True
    nt = world.node_tree
    bg = nt.nodes["Background"]
    bg.inputs[1].default_value = BG_WORLD_STRENGTH
    # Real-world ambient and reflections from one of the HDRs Blender ships
    # for look-dev (see studiolights/world/license.txt): soft directional
    # skylight on the skin and a believable catch light in the cornea. The
    # backdrop plane still sits behind the head, so the *visible* background
    # stays the plain grey the driver needs — the HDR only lights.
    hdr = os.path.join(bpy.utils.resource_path('LOCAL'), "datafiles", "studiolights", "world", WORLD_HDR)
    if os.path.exists(hdr):
        env = nt.nodes.new("ShaderNodeTexEnvironment")
        env.image = bpy.data.images.load(hdr)
        mapping = nt.nodes.new("ShaderNodeMapping")
        mapping.inputs["Rotation"].default_value = (0.0, 0.0, math.radians(WORLD_HDR_YAW_DEG))
        coord = nt.nodes.new("ShaderNodeTexCoord")
        nt.links.new(coord.outputs["Generated"], mapping.inputs["Vector"])
        nt.links.new(mapping.outputs["Vector"], env.inputs["Vector"])
        nt.links.new(env.outputs["Color"], bg.inputs[0])
    else:
        bg.inputs[0].default_value = BG_WORLD
    me = bpy.data.meshes.new("Backdrop")
    s = 3.0
    me.from_pydata([(-s, 0, -s), (s, 0, -s), (s, 0, s), (-s, 0, s)], [], [(0, 1, 2, 3)])
    plane = bpy.data.objects.new("Backdrop", me)
    scn.collection.objects.link(plane)
    plane.location = (fc.x, fc.y + 2.5, fc.z)
    pm = bpy.data.materials.new("BackdropGrey"); pm.use_nodes = True
    bsdf = pm.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = BG_PLANE
    bsdf.inputs["Roughness"].default_value = 1.0
    me.materials.append(pm)
    return plane


def setup_render(scn, frame_start=0, frame_end=0, samples=48, engine=None):
    """`engine` is EEVEE (default — BLENDER_EEVEE_NEXT with shadows and
    ray-traced AO/contact shading; the extractor does not reward Cycles'
    extra realism and Eevee renders the seven clips in minutes) or CYCLES
    (random-walk SSS, physically shaded hair, HDR reflections; ~4x slower)."""
    if engine is None:
        engine = ENGINE
    if engine == "CYCLES":
        scn.render.engine = 'CYCLES'
        prefs = bpy.context.preferences.addons.get("cycles")
        if prefs is not None:
            cp = prefs.preferences
            for dev_type in ("METAL", "CUDA", "OPTIX", "HIP", "ONEAPI"):
                try:
                    cp.compute_device_type = dev_type
                    cp.get_devices()
                    if any(d.type == dev_type for d in cp.devices):
                        for d in cp.devices:
                            d.use = d.type == dev_type
                        scn.cycles.device = 'GPU'
                        break
                except TypeError:
                    continue
        scn.cycles.samples = samples
        scn.cycles.use_adaptive_sampling = True
        scn.cycles.adaptive_threshold = 0.02
        scn.cycles.use_denoising = True
        scn.cycles.denoiser = 'OPENIMAGEDENOISE'
        scn.cycles.denoising_input_passes = 'RGB_ALBEDO_NORMAL'
        scn.cycles.use_persistent_data = True       # keep BVH/textures between frames
        scn.cycles.max_bounces = 6
        scn.cycles.transparent_max_bounces = 16     # many thin strands
        scn.cycles.caustics_reflective = False
        scn.cycles.caustics_refractive = False
        scn.cycles.blur_glossy = 1.0
    else:
        scn.render.engine = 'BLENDER_EEVEE_NEXT'
        # Ray-traced screen-space AO / reflections: contact darkening in the
        # nostrils, eye sockets and lip line the extractor can read.
        scn.eevee.use_raytracing = True
        scn.eevee.ray_tracing_options.resolution_scale = '2'
        scn.eevee.shadow_ray_count = 2
        scn.eevee.shadow_step_count = 8
    scn.render.resolution_x = RES
    scn.render.resolution_y = RES
    scn.render.resolution_percentage = 100
    scn.render.fps = FPS
    scn.frame_start = frame_start
    scn.frame_end = frame_end
    scn.render.image_settings.file_format = 'PNG'
    scn.render.image_settings.color_mode = 'RGB'
    scn.render.film_transparent = False
    scn.eevee.taa_render_samples = samples
    scn.render.filter_size = FILTER_SIZE  # softer pixel filter: thin strands alias less between frames
    scn.eevee.use_shadows = True
    # hair as strands, a few subdivisions so curved strands stay smooth
    scn.render.hair_type = 'STRAND'
    scn.render.hair_subdiv = 1
    scn.view_settings.view_transform = 'AgX'
    scn.view_settings.look = LOOK


def render_frames(scn, out_dir, frames):
    """Render an explicit list of frame numbers as PNG stills into out_dir."""
    os.makedirs(out_dir, exist_ok=True)
    for f in frames:
        scn.frame_set(f)
        scn.render.filepath = os.path.join(out_dir, f"frame_{f:04d}.png")
        bpy.ops.render.render(write_still=True)
    return [os.path.join(out_dir, f"frame_{f:04d}.png") for f in frames]
