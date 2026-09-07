"""Turn the bald finalized MB-Lab character into a person: authored
eyebrows, eyelashes and a short swept-back hairstyle as hair Curves that
ride the skin through *Deform Curves on Surface*, subsurface skin, a wet
cornea, and a crew-neck top.  Also installs the shared camera / lights /
backdrop.

  Blender --background --python mblab_rich.py -- \
      --src work/mblab_char.blend --out work/mblab_char_rich.blend \
      [--test work/rich_test] [--flicker 10]

--test renders five poses (neutral, open mouth + brows, smile, yaw, blink +
pitch) so the look can be judged before any clip is rendered.  --flicker N
renders N consecutive frames of a slow yaw for a frame-to-frame stability
check of the hair.

Why Curves and not a particle system: particle hair keys can be written
(`ParticleHairKey.co_object_set`) but in background mode the original
particle system never receives them, so a combed style is lost on save.
Hair Curves store their points in the datablock.
"""
import os, sys, math, random, argparse
import bpy
from mathutils import Vector, noise

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lp_scene as S  # noqa: E402
import driver_config  # noqa: E402

argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
ap = argparse.ArgumentParser()
ap.add_argument("--src", required=True)
ap.add_argument("--out", required=True)
ap.add_argument("--test", default=None, help="folder for test stills")
ap.add_argument("--flicker", type=int, default=0)
ap.add_argument("--samples", type=int, default=None)
args = ap.parse_args(argv)
if args.samples is None:
    args.samples = int((driver_config.load().get("scene") or {}).get("dress_samples") or 48)

# ---------------------------------------------------------------- look knobs (overridden by build.py via LP_DRIVER_CONFIG)
HAIR_COLOR = (0.052, 0.030, 0.017, 1.0)      # dark brown
HAIR_STRANDS = 16000   # short cut needs density for coverage
HAIR_POINTS = 8
HAIR_ROOT_RADIUS = 0.00048                  # fat enough to cover a pixel: thinner strands shimmer frame to frame
HAIR_TIP_RADIUS = 0.00018
HAIR_ROUGHNESS = 0.58
BROW_COLOR = (0.055, 0.033, 0.019, 1.0)
BROW_STRANDS = 760                           # per brow
BROW_ROOT_RADIUS = 0.00017
LASH_COLOR = (0.016, 0.010, 0.008, 1.0)
LASH_UPPER_PER_ROOT = 1
LASH_LOWER_PER_ROOT = 0
LASH_RADIUS = 0.00011
TOP_COLOR = (0.075, 0.105, 0.150, 1.0)       # slate blue cotton
SKIN_SSS_WEIGHT = 0.22
SKIN_SSS_SCALE = 0.006
SKIN_ROUGHNESS = 0.46
LOOK_SEED = 7


def _as_color(v, fallback):
    if not v:
        return fallback
    t = tuple(v)
    return t if len(t) == 4 else fallback


_look = (driver_config.load().get("look") or {})
if _look:
    HAIR_COLOR = _as_color(_look.get("hair_color"), HAIR_COLOR)
    HAIR_STRANDS = int(_look.get("hair_strands", HAIR_STRANDS))
    HAIR_POINTS = int(_look.get("hair_points", HAIR_POINTS))
    HAIR_ROOT_RADIUS = float(_look.get("hair_root_radius", HAIR_ROOT_RADIUS))
    HAIR_TIP_RADIUS = float(_look.get("hair_tip_radius", HAIR_TIP_RADIUS))
    HAIR_ROUGHNESS = float(_look.get("hair_roughness", HAIR_ROUGHNESS))
    BROW_COLOR = _as_color(_look.get("brow_color"), BROW_COLOR)
    BROW_STRANDS = int(_look.get("brow_strands", BROW_STRANDS))
    BROW_ROOT_RADIUS = float(_look.get("brow_root_radius", BROW_ROOT_RADIUS))
    LASH_COLOR = _as_color(_look.get("lash_color"), LASH_COLOR)
    LASH_UPPER_PER_ROOT = int(_look.get("lash_upper_per_root", LASH_UPPER_PER_ROOT))
    LASH_LOWER_PER_ROOT = int(_look.get("lash_lower_per_root", LASH_LOWER_PER_ROOT))
    LASH_RADIUS = float(_look.get("lash_radius", LASH_RADIUS))
    TOP_COLOR = _as_color(_look.get("top_color"), TOP_COLOR)
    SKIN_SSS_WEIGHT = float(_look.get("skin_sss_weight", SKIN_SSS_WEIGHT))
    SKIN_SSS_SCALE = float(_look.get("skin_sss_scale", SKIN_SSS_SCALE))
    SKIN_ROUGHNESS = float(_look.get("skin_roughness", SKIN_ROUGHNESS))
    LOOK_SEED = int(_look.get("seed", LOOK_SEED))

scn, body, arm = S.open_character(args.src)
hm = S.HeadMeasure(body, arm)
print("[rich]", hm.describe())
me = body.data
rng = random.Random(LOOK_SEED)
C = hm.skull_center


def slot_index(suffix):
    return next(i for i, m in enumerate(me.materials) if m.name.endswith(suffix))


def verts_of_slot(idx):
    vs = set()
    for p in me.polygons:
        if p.material_index == idx:
            vs.update(p.vertices)
    return vs


# ---------------------------------------------------------------- materials
def hair_material(name, color, roughness=0.32, tip_lighten=0.25, random_dark=0.55):
    m = bpy.data.materials.new(name); m.use_nodes = True
    nt = m.node_tree
    bsdf = nt.nodes["Principled BSDF"]
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Specular IOR Level"].default_value = 0.25
    bsdf.inputs["Coat Weight"].default_value = 0.0
    hi = nt.nodes.new("ShaderNodeHairInfo")
    ramp = nt.nodes.new("ShaderNodeValToRGB")
    ramp.color_ramp.elements[0].color = tuple(c * random_dark for c in color[:3]) + (1,)
    ramp.color_ramp.elements[1].color = color
    nt.links.new(hi.outputs["Random"], ramp.inputs["Fac"])
    mix = nt.nodes.new("ShaderNodeMix"); mix.data_type = 'RGBA'
    mix.inputs[7].default_value = tuple(min(1, c * 1.9) for c in color[:3]) + (1,)
    nt.links.new(ramp.outputs["Color"], mix.inputs[6])
    mul = nt.nodes.new("ShaderNodeMath"); mul.operation = 'MULTIPLY'
    mul.inputs[1].default_value = tip_lighten
    nt.links.new(hi.outputs["Intercept"], mul.inputs[0])
    nt.links.new(mul.outputs[0], mix.inputs["Factor"])
    nt.links.new(mix.outputs[2], bsdf.inputs["Base Color"])
    return m


def tune_skin():
    g = bpy.data.node_groups["MBLab_Skin3"]
    bsdf = next(n for n in g.nodes if n.type == 'BSDF_PRINCIPLED')
    bsdf.inputs["Subsurface Weight"].default_value = SKIN_SSS_WEIGHT
    bsdf.inputs["Subsurface Scale"].default_value = SKIN_SSS_SCALE
    bsdf.inputs["Subsurface Radius"].default_value = (1.0, 0.35, 0.18)
    bsdf.subsurface_method = 'RANDOM_WALK'
    # Skin is not one roughness. A uniform value renders as plastic; real
    # skin has oily T-zone / matte cheek variation and pore-scale breakup
    # of the specular. Two noise octaves drive roughness around the base
    # value and a faint bump gives the highlight something to break on.
    # Everything here is driven by generated coordinates, so it is stable
    # frame to frame and does not depend on the low-res MB-Lab maps.
    if bsdf.inputs["Roughness"].links:
        for l in list(bsdf.inputs["Roughness"].links):
            g.links.remove(l)
    coord = g.nodes.new("ShaderNodeTexCoord")
    macro = g.nodes.new("ShaderNodeTexNoise"); macro.inputs["Scale"].default_value = 18.0
    macro.inputs["Detail"].default_value = 3.0; macro.inputs["Roughness"].default_value = 0.5
    pores = g.nodes.new("ShaderNodeTexNoise"); pores.inputs["Scale"].default_value = 900.0
    pores.inputs["Detail"].default_value = 2.0
    g.links.new(coord.outputs["Object"], macro.inputs["Vector"])
    g.links.new(coord.outputs["Object"], pores.inputs["Vector"])
    mix = g.nodes.new("ShaderNodeMix"); mix.data_type = 'FLOAT'; mix.inputs["Factor"].default_value = 0.35
    g.links.new(macro.outputs["Fac"], mix.inputs[2]); g.links.new(pores.outputs["Fac"], mix.inputs[3])
    ramp = g.nodes.new("ShaderNodeMapRange")
    ramp.inputs["From Min"].default_value = 0.0; ramp.inputs["From Max"].default_value = 1.0
    ramp.inputs["To Min"].default_value = SKIN_ROUGHNESS - 0.14
    ramp.inputs["To Max"].default_value = SKIN_ROUGHNESS + 0.16
    g.links.new(mix.outputs[0], ramp.inputs["Value"])
    g.links.new(ramp.outputs["Result"], bsdf.inputs["Roughness"])
    bump = g.nodes.new("ShaderNodeBump"); bump.inputs["Strength"].default_value = 0.08
    bump.inputs["Distance"].default_value = 0.0004
    g.links.new(pores.outputs["Fac"], bump.inputs["Height"])
    if not bsdf.inputs["Normal"].links:
        g.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    # take a little saturation out of the albedo: the MB-Lab map is pink
    if bsdf.inputs["Base Color"].links:
        src = bsdf.inputs["Base Color"].links[0].from_socket
        g.links.remove(bsdf.inputs["Base Color"].links[0])
        hsv = g.nodes.new("ShaderNodeHueSaturation")
        hsv.inputs["Saturation"].default_value = 0.92
        hsv.inputs["Value"].default_value = 0.94
        g.links.new(src, hsv.inputs["Color"])
        g.links.new(hsv.outputs["Color"], bsdf.inputs["Base Color"])
    # The material's Displacement output (fed by the 2048 displacement map)
    # is rendered as bump by EEVEE and produces blocky red patches at the
    # nostrils and eye corners; the Displace *modifier* already applies
    # that map to the geometry, so drop the shader-side copy.
    nt = bpy.data.materials["LP_MBLab_skin3"].node_tree
    for l in list(nt.links):
        if l.to_socket.name == "Displacement":
            nt.links.remove(l)


def tune_eyes():
    cornea = bpy.data.materials["LP_MBlab_cornea"]
    b = next(n for n in cornea.node_tree.nodes if n.type == 'BSDF_PRINCIPLED')
    b.inputs["Specular IOR Level"].default_value = 0.6
    b.inputs["IOR"].default_value = 1.376
    b.inputs["Roughness"].default_value = 0.02
    b.inputs["Coat Weight"].default_value = 0.6
    b.inputs["Coat Roughness"].default_value = 0.02
    cornea.surface_render_method = 'BLENDED'
    cornea.use_transparency_overlap = True
    eyes = bpy.data.materials["LP_MBlab_human_eyes"]
    for n in eyes.node_tree.nodes:
        if n.type == 'BSDF_PRINCIPLED':
            n.inputs["Roughness"].default_value = 0.08
            n.inputs["Specular IOR Level"].default_value = 0.55
    lash = bpy.data.materials["LP_MBlab_eyelash"]
    lash.surface_render_method = 'BLENDED'   # hashed alpha leaves pink speckle under the eyes
    for n in lash.node_tree.nodes:
        if n.type == 'BSDF_PRINCIPLED':
            n.inputs["Roughness"].default_value = 0.4


def tune_teeth():
    for name in ("LP_MBlab_human_teeth", "LP_MBLab_tongue"):
        for n in bpy.data.materials[name].node_tree.nodes:
            if n.type == 'BSDF_PRINCIPLED':
                n.inputs["Subsurface Weight"].default_value = 0.15
                n.inputs["Subsurface Scale"].default_value = 0.004


# ---------------------------------------------------------------- surface sampling
uv_layer = me.uv_layers["UVMap"].data


def ensure_rest_position():
    if "rest_position" not in me.attributes:
        a = me.attributes.new("rest_position", 'FLOAT_VECTOR', 'POINT')
        a.data.foreach_set("vector", [c for v in me.vertices for c in v.co])


def face_sample(p, w_tri):
    """Random point + normal + UV on polygon p (fan triangulation)."""
    li = list(p.loop_indices)
    vi = [me.loops[l].vertex_index for l in li]
    k = rng.randrange(1, len(vi) - 1)
    ia, ib, ic = 0, k, k + 1
    r1, r2 = rng.random(), rng.random()
    if r1 + r2 > 1:
        r1, r2 = 1 - r1, 1 - r2
    w = (1 - r1 - r2, r1, r2)
    pos = me.vertices[vi[ia]].co * w[0] + me.vertices[vi[ib]].co * w[1] + me.vertices[vi[ic]].co * w[2]
    uv = uv_layer[li[ia]].uv * w[0] + uv_layer[li[ib]].uv * w[1] + uv_layer[li[ic]].uv * w[2]
    return pos, p.normal.copy(), uv


def uv_at(poly_index, pos):
    """UV of a point on polygon poly_index (barycentric over the fan)."""
    p = me.polygons[poly_index]
    li = list(p.loop_indices)
    vi = [me.loops[l].vertex_index for l in li]
    best = None
    for k in range(1, len(vi) - 1):
        a, b, c = me.vertices[vi[0]].co, me.vertices[vi[k]].co, me.vertices[vi[k + 1]].co
        v0, v1, v2 = b - a, c - a, pos - a
        d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
        den = d00 * d11 - d01 * d01
        if abs(den) < 1e-12:
            continue
        v = (d11 * d20 - d01 * d21) / den
        w = (d00 * d21 - d01 * d20) / den
        u = 1 - v - w
        err = max(0, -u) + max(0, -v) + max(0, -w)
        if best is None or err < best[0]:
            best = (err, uv_layer[li[0]].uv * u + uv_layer[li[k]].uv * v + uv_layer[li[k + 1]].uv * w)
    return best[1] if best else None      # None: every fan triangle degenerate


_bvh = None


def skin_hit(origin, direction):
    """Ray cast against the *base* body mesh (Object.ray_cast would hit the
    subdivided evaluated mesh and return its face indices); returns
    (pos, normal, uv) or None."""
    global _bvh
    if _bvh is None:
        from mathutils.bvhtree import BVHTree
        _bvh = BVHTree.FromPolygons([v.co for v in me.vertices], [tuple(p.vertices) for p in me.polygons])
    loc, nrm, idx, dist = _bvh.ray_cast(origin, direction)
    if loc is None:
        return None
    uv = uv_at(idx, loc)
    return None if uv is None else (loc, nrm, uv)


def scalp_polys():
    """Hairline: forehead ~6.3 cm above the eyes (higher at the temples),
    above the ears on the sides, down to the nape at the back."""
    skin = slot_index("skin3")
    out = []
    for p in me.polygons:
        if p.material_index != skin:
            continue
        c = p.center
        if c.z < hm.neck_z + 0.015:
            continue
        dz = c.z - hm.eye_z
        dx, dy = c.x - C.x, c.y - C.y
        phi = math.atan2(abs(dx), -dy)
        if phi < math.radians(55):
            temple = max(0.0, (abs(dx) - 0.04) / 0.03)
            ok = dz > 0.063 + 0.012 * temple
        elif phi < math.radians(115):
            ok = dz > 0.036 and not (abs(dx) > 0.066 and dz < 0.055)   # skip ears
        else:
            ok = dz > -0.05
        if ok:
            out.append(p)
    return out


# ---------------------------------------------------------------- curves builder
def make_curves(name, strands, material, parent=body):
    """strands: list of (points[list[Vector]], uv, radii[list[float]])."""
    cd = bpy.data.hair_curves.new(name)
    cd.add_curves([len(pts) for pts, _, _ in strands])
    co, rad, uvs = [], [], []
    for pts, uv, radii in strands:
        for p in pts:
            co.extend(p)
        rad.extend(radii)
        uvs.extend((uv.x, uv.y))
    cd.attributes["position"].data.foreach_set("vector", co)
    cd.attributes.new("radius", 'FLOAT', 'POINT').data.foreach_set("value", rad)
    cd.attributes.new("surface_uv_coordinate", 'FLOAT2', 'CURVE').data.foreach_set("vector", uvs)
    cd.surface = body
    cd.surface_uv_map = "UVMap"
    cd.materials.append(material)
    ob = bpy.data.objects.new(name, cd)
    scn.collection.objects.link(ob)
    ob.parent = parent
    ng = bpy.data.node_groups.get("LP_DeformOnSurface")
    if ng is None:
        ng = bpy.data.node_groups.new("LP_DeformOnSurface", 'GeometryNodeTree')
        ng.interface.new_socket("Geometry", in_out='INPUT', socket_type='NodeSocketGeometry')
        ng.interface.new_socket("Geometry", in_out='OUTPUT', socket_type='NodeSocketGeometry')
        gi = ng.nodes.new("NodeGroupInput"); go = ng.nodes.new("NodeGroupOutput")
        d = ng.nodes.new("GeometryNodeDeformCurvesOnSurface")
        ng.links.new(gi.outputs[0], d.inputs[0]); ng.links.new(d.outputs[0], go.inputs[0])
    mod = ob.modifiers.new("Deform", 'NODES'); mod.node_group = ng
    print("[rich] curves", name, len(strands), "strands", len(co) // 3, "points")
    return ob


def radii(n, root, tip):
    return [root + (tip - root) * (i / (n - 1)) ** 0.8 for i in range(n)]


def coherent(v, scale, seed_off):
    """Spatially coherent random vector in [-1,1]^3 (neighbouring roots get
    similar jitter -> natural clumping without hard structure)."""
    p = v * scale + Vector((seed_off, seed_off * 1.7, seed_off * 0.3))
    return Vector((noise.noise(p), noise.noise(p + Vector((31.7, 0, 0))), noise.noise(p + Vector((0, 47.1, 0)))))


DOWN = Vector((0, 0, -1))


def head_hair_strands():
    polys = scalp_polys()
    area = sum(p.area for p in polys)
    strands = []
    for p in polys:
        n_here = max(1, round(HAIR_STRANDS * p.area / area))
        for _ in range(n_here):
            r, n, uv = face_sample(p, None)
            rel = r - C
            dz = r.z - hm.eye_z
            phi = math.atan2(abs(rel.x), -rel.y)
            radial_xy = Vector((rel.x, rel.y, 0)).length
            # Short, combed-back cut. Long procedural hair reads as straw and
            # swings past the face edges — the extractor then sees hair, not
            # face, changing frame to frame. Short strands hug the scalp:
            # lift off the surface a little at the root, then follow a comb
            # direction projected onto the scalp tangent plane, clamped to a
            # few millimetres above the skull all the way to the tip.
            jit = coherent(r, 60.0, 3.0) * 0.018 + Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))) * 0.005
            front = phi < math.radians(58)
            crown = dz > 0.085
            if front:
                length = 0.030 + 0.010 * rng.random()
                comb = Vector((0.15 * math.copysign(1, rel.x), 1.0, -0.15))      # back, slightly out
            elif crown:
                length = 0.036 + 0.010 * rng.random()
                comb = Vector((0.35 * math.copysign(1, rel.x), 0.75, -0.55))     # back and down over the crown
            else:
                length = 0.026 + 0.009 * rng.random()
                comb = (DOWN * 0.8 + Vector((rel.x, rel.y, 0)).normalized() * 0.12 + Vector((0, 0.35, 0)))
            # project the comb direction onto the tangent plane at the root
            comb = (comb - n * comb.dot(n)).normalized()
            pad_end = 0.003
            k = HAIR_POINTS
            seg = length / (k - 1)
            pos = r.copy(); pts = [pos.copy()]
            wave_phase = rng.random() * 6.283
            wave_amp = 0.0002 + 0.0003 * rng.random()
            side = Vector((-rel.y, rel.x, 0)).normalized() if radial_xy > 1e-4 else Vector((1, 0, 0))
            for i in range(1, k):
                t = i / (k - 1)
                w = min(1.0, t * 2.5)                 # off the scalp quickly, then lie down
                d = ((1 - w) * (n * 0.6 + comb * 0.4) + w * (comb + jit)).normalized()
                pos = pos + d * seg
                pad = 0.0015 + pad_end * w
                if front or pos.z > C.z + 0.015:
                    # spherical clamp around the skull centre over the top
                    rr = pos - C
                    rmin = rel.length + pad
                    if rr.length < rmin:
                        pos = C + rr.normalized() * rmin
                else:
                    # cylindrical clamp down the sides / back
                    rr = Vector((pos.x - C.x, pos.y - C.y, 0))
                    rmin = radial_xy + pad
                    if rr.length < rmin:
                        pos = Vector((C.x, C.y, pos.z)) + rr.normalized() * rmin
                pts.append(pos + side * (math.sin(wave_phase + t * 5.0) * wave_amp * t))
            strands.append((pts, uv, radii(k, HAIR_ROOT_RADIUS, HAIR_TIP_RADIUS)))
    return strands


def brow_base_z(sign):
    centre = getattr(hm, "brow_center", {}).get(sign)
    if not centre:
        return 0.0165
    zs = sorted(c[1] - hm.eye_z for c in centre)
    return zs[len(zs) // 2] + 0.0012            # median band height, a hair above it


def brow_strands(sign):
    """One eyebrow as an arc of skin-hugging hairs. sign=+1 character's
    left (+X), -1 right. Landmarks are relative to the eye centre."""
    eye = hm.eye_l if sign > 0 else hm.eye_r
    strands = []
    n_pts = 5
    for _ in range(BROW_STRANDS):
        s = rng.random() ** 0.9                    # denser toward the inner end
        x = sign * (0.014 + 0.040 * s)
        arch = math.sin(math.pi * min(1.0, s / 0.92) ** 0.85)
        # One smooth arc, lowered onto the brow ridge the painted map defines
        # (paint_albedo measures the painted band at 9-14.5 mm above the eye
        # line; the old 16.5 mm left a gap the painted brow showed through).
        # Per-column snapping to that band split the brow into two rows.
        zc = hm.eye_z + brow_base_z(sign) + 0.0062 * arch
        half_h = 0.0026 * (1 - 0.6 * s) + 0.0006
        z = zc + rng.uniform(-half_h, half_h)
        hit = skin_hit(Vector((x, hm.face_front_y - 0.08, z)), Vector((0, 1, 0)))
        if hit is None:
            continue
        r, n, uv = hit
        # tangent along the arc toward the tail; inner hairs stand up more
        dzds = 0.0085 * (math.cos(math.pi * s) * math.pi) * 0.043 / 0.043
        tangent = Vector((sign * 1.0, 0, dzds * 0.9)).normalized()
        up = 0.8 * (1 - s) ** 1.6 - 0.15 * s
        d0 = (tangent + Vector((0, 0, up)) + n * 0.3).normalized()
        d1 = (tangent + Vector((0, 0, up * 0.3 - 0.2 * s)) + n * 0.0).normalized()
        length = 0.0065 - 0.002 * s + rng.uniform(-0.0006, 0.0006)
        pts = [r.copy()]; pos = r.copy()
        for i in range(1, n_pts):
            t = i / (n_pts - 1)
            d = ((1 - t) * d0 + t * d1).normalized()
            pos = pos + d * (length / (n_pts - 1))
            pts.append(pos.copy())
        strands.append((pts, uv, radii(n_pts, BROW_ROOT_RADIUS, BROW_ROOT_RADIUS * 0.35)))
    return strands


def lash_strands():
    """Lid-margin positions come from the eyelash-card vertices (about one
    eye radius from the eye centre), but each strand is ROOTED on the nearest
    skin vertex, not on the card. Deform Curves on Surface finds a root by
    sampling the surface at the strand's UV; MB-Lab's lash cards are stacks
    of overlapping planes sharing UV space, so that lookup landed on the
    wrong card or nowhere and the lashes stayed still while the lids closed
    (the cards themselves move 13 mm under eyeClosed — the skin 4 mm away
    moves 12.7 mm and has an unambiguous UV island). Upper lashes sweep
    forward and up, lower ones forward and down."""
    from mathutils.bvhtree import BVHTree
    from mathutils.geometry import barycentric_transform
    lash_vs = verts_of_slot(slot_index("eyelash"))
    skin_i = slot_index("skin3")
    skin_polys = [p for p in me.polygons if p.material_index == skin_i]
    bvh = BVHTree.FromPolygons(
        [v.co for v in me.vertices],
        [tuple(p.vertices) for p in skin_polys], all_triangles=False)

    def skin_uv_at(co):
        """UV on the skin island under `co`, interpolated inside the nearest
        skin face — one distinct UV per lash, all on unambiguous skin."""
        loc, _, pi, _ = bvh.find_nearest(co)
        p = skin_polys[pi]
        vs = [me.vertices[v].co for v in p.vertices]
        uvs = [uv_layer[li].uv for li in p.loop_indices]
        # fan-triangulate and take the triangle whose barycentric weights are valid
        for a in range(1, len(vs) - 1):
            tri = (vs[0], vs[a], vs[a + 1]); tuv = (uvs[0], uvs[a], uvs[a + 1])
            uv3 = barycentric_transform(loc, tri[0], tri[1], tri[2],
                                        Vector((tuv[0].x, tuv[0].y, 0)), Vector((tuv[1].x, tuv[1].y, 0)), Vector((tuv[2].x, tuv[2].y, 0)))
            if -0.05 <= uv3.x <= 1.05 and -0.05 <= uv3.y <= 1.05:
                return uv3.xy
        return uvs[0].copy()
    # uv per vertex (first loop)
    vuv = {}
    for p in me.polygons:
        for li in p.loop_indices:
            vuv.setdefault(me.loops[li].vertex_index, uv_layer[li].uv)
    strands = []
    n_pts = 5
    seen = set()                                    # many card verts snap to one skin vert
    for card_i in lash_vs:
        card_r = me.vertices[card_i].co
        # the lid-margin test runs on the CARD vertex (that is what sits one
        # eye radius out); the snapped skin vertex is only where we root
        eye = hm.eye_l if card_r.x > 0 else hm.eye_r
        if abs((card_r - eye).length - hm.eye_r_radius) > 0.0022:
            continue                              # card outer edge, not the lid margin
        key = (round(card_r.x, 4), round(card_r.y, 4), round(card_r.z, 4))
        if key in seen:                             # stacked cards share positions
            continue
        seen.add(key)
        r = card_r.copy()                           # margin position from the card…
        root_uv = skin_uv_at(card_r)                # …deformation from the skin under it
        upper = r.z > eye.z + 0.001
        radial = (r - eye).normalized()
        count = LASH_UPPER_PER_ROOT if upper else LASH_LOWER_PER_ROOT
        for _ in range(count):
            lateral = math.copysign(1, r.x - eye.x) * 0.25 * abs(r.x - eye.x) / max(1e-4, hm.eye_r_radius)
            if upper:
                d0 = (radial * 0.6 + Vector((lateral, -0.6, 0.35))).normalized()
                d1 = (radial * 0.1 + Vector((lateral, -0.3, 1.0))).normalized()
                length = 0.0055 + rng.uniform(-0.0007, 0.0008)
            else:
                d0 = (radial * 0.6 + Vector((lateral, -0.6, -0.4))).normalized()
                d1 = (radial * 0.1 + Vector((lateral, -0.3, -1.0))).normalized()
                length = 0.0038 + rng.uniform(-0.0005, 0.0006)
            jitter = Vector((rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1))) * 0.12
            pts = [r.copy()]; pos = r.copy()
            for k in range(1, n_pts):
                t = k / (n_pts - 1)
                d = ((1 - t) * d0 + t * d1 + jitter).normalized()
                pos = pos + d * (length / (n_pts - 1))
                pts.append(pos.copy())
            strands.append((pts, root_uv, radii(n_pts, LASH_RADIUS, LASH_RADIUS * 0.3)))
    return strands


# ---------------------------------------------------------------- garment
def add_top():
    """Crew-neck top: a copy of the torso/shoulder skin pushed 4.5 mm out
    with a fabric material. Keeps the armature modifier + shape keys so it
    breathes with the chest."""
    import bmesh
    top = body.copy()
    top.data = me.copy()
    top.name = "LP_top"; top.data.name = "LP_top"
    scn.collection.objects.link(top)
    top.parent = arm
    top.matrix_parent_inverse = body.matrix_parent_inverse.copy()
    tme = top.data
    keep = set()
    for v in tme.vertices:
        p = v.co
        frontness = max(0.0, min(1.0, -(p.y - C.y) / 0.08))
        # crew neck: high at the back, dipping ~4 cm at the front
        neckline = hm.neck_z + 0.048 - 0.042 * frontness
        neck_col = Vector((p.x - C.x, p.y - C.y, 0)).length < 0.064 and p.z > hm.neck_z - 0.004
        if p.z < neckline and not neck_col:
            keep.add(v.index)
    bm = bmesh.new(); bm.from_mesh(tme)
    bm.verts.ensure_lookup_table()
    bmesh.ops.delete(bm, geom=[bv for bv in bm.verts if bv.index not in keep], context='VERTS')
    bm.to_mesh(tme); bm.free()
    for m in list(top.modifiers):
        if m.type in ('PARTICLE_SYSTEM', 'DISPLACE', 'CORRECTIVE_SMOOTH'):
            top.modifiers.remove(m)
    disp = top.modifiers.new("Offset", 'DISPLACE')
    # the body's own displacement map moves skin +-5 mm, so the cloth must
    # sit further out than that or the skin pokes through in blobs
    disp.strength = 0.011; disp.mid_level = 0.0; disp.direction = 'NORMAL'
    sol = top.modifiers.new("Thickness", 'SOLIDIFY')
    sol.thickness = 0.0025; sol.offset = -1.0
    tme.materials.clear()
    fab = bpy.data.materials.new("LP_fabric"); fab.use_nodes = True
    nt = fab.node_tree
    b = nt.nodes["Principled BSDF"]
    b.inputs["Base Color"].default_value = TOP_COLOR
    b.inputs["Roughness"].default_value = 0.88
    b.inputs["Specular IOR Level"].default_value = 0.25
    b.inputs["Sheen Weight"].default_value = 0.35
    nz = nt.nodes.new("ShaderNodeTexNoise")
    nz.inputs["Scale"].default_value = 900.0; nz.inputs["Detail"].default_value = 2.0
    bump = nt.nodes.new("ShaderNodeBump")
    bump.inputs["Strength"].default_value = 0.35; bump.inputs["Distance"].default_value = 0.0005
    nt.links.new(nz.outputs["Fac"], bump.inputs["Height"])
    nt.links.new(bump.outputs["Normal"], b.inputs["Normal"])
    tme.materials.append(fab)
    for p in tme.polygons:
        p.material_index = 0
    print("[rich] top: kept", len(keep), "verts")
    return top


def skin_albedo_image():
    """The image feeding the skin group's colour input (MB-Lab's painted map)."""
    nt = bpy.data.materials["LP_MBLab_skin3"].node_tree
    imgs = [n.image for n in nt.nodes if n.type == 'TEX_IMAGE' and n.image is not None]
    for key in ("albedo", "diffuse", "color"):
        for im in imgs:
            if key in im.name.lower():
                return im
    return max(imgs, key=lambda im: im.size[0] * im.size[1]) if imgs else None


def paint_albedo():
    """Retouch MB-Lab's painted skin map in place, then pack it into the blend.

    * Eyebrows. The map has eyebrows painted into it, ~4 mm below where the
      hair-curve brows were placed, so a thin red-brown line showed under each
      brow — a second 'edge' for the motion extractor to lock onto. Find that
      painted band by luminance on a grid of skin hits, paint it out with the
      skin colour just above it, and record its centreline so `brow_strands`
      grows the hair brows *on* the ridge the map (and the mesh) define.
    * Lips. The same map's lips are barely redder than the cheeks, and the
      global desaturation in `tune_skin` flattens them further. Find them by
      redness against the surrounding skin and push saturation and darkness
      up inside that mask only, so the mouth reads as a distinct feature.
    """
    import numpy as np
    img = skin_albedo_image()
    if img is None:
        print("[rich] paint_albedo: no skin image"); return
    W, H = img.size
    px = np.array(img.pixels[:], dtype=np.float32).reshape(H, W, 4)

    def texel(uv):
        return int(uv.x * W) % W, int(uv.y * H) % H

    def lum(c):
        return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]

    # ---- eyebrows -------------------------------------------------------
    hm.brow_center = {}
    brow_mask = np.zeros((H, W), dtype=bool)
    repl = {}
    for sign in (+1, -1):
        centre = []
        for xi in range(8, 62):
            x = sign * xi * 0.001
            col = []
            for zi in range(0, 60):
                z = hm.eye_z + 0.003 + zi * 0.0005
                hit = skin_hit(Vector((x, hm.face_front_y - 0.08, z)), Vector((0, 1, 0)))
                if hit is None:
                    continue
                tx, ty = texel(hit[2])
                col.append((z, tx, ty, lum(px[ty, tx])))
            if len(col) < 10:
                continue
            med = float(np.median([c[3] for c in col]))
            dark = [c for c in col if c[3] < 0.82 * med]
            if len(dark) < 3:
                continue
            zc = sum(c[0] for c in dark) / len(dark)
            centre.append((x, zc))
            above = max(col, key=lambda c: c[0])            # skin well above the band
            for z, tx, ty, _ in dark:
                brow_mask[ty, tx] = True
                repl[(ty, tx)] = px[above[2], above[1], :3].copy()
        hm.brow_center[sign] = sorted(centre, key=lambda c: abs(c[0]))
        if centre:
            print("[rich] painted brow %s: z-eye = %.1f..%.1f mm over %d columns" % (
                "L" if sign > 0 else "R", 1000 * (min(c[1] for c in centre) - hm.eye_z),
                1000 * (max(c[1] for c in centre) - hm.eye_z), len(centre)))
    if brow_mask.any():
        # dilate the sampled texels into a solid band, fill from the nearest recorded skin colour
        from collections import deque
        ys, xs = np.nonzero(brow_mask)
        q = deque(zip(ys.tolist(), xs.tolist()))
        seen = set(q)
        fill = {k: v for k, v in repl.items()}
        steps = 0
        while q and steps < 8 * len(seen):
            y, x = q.popleft(); steps += 1
            c = fill[(y, x)]
            for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                ny, nx = y + dy, x + dx
                if (ny, nx) in seen or not (0 <= ny < H and 0 <= nx < W):
                    continue
                # grow while the neighbour is still darker than its replacement (part of the band)
                if lum(px[ny, nx]) < 0.9 * lum(c) or len(seen) < 4 * len(repl):
                    seen.add((ny, nx)); fill[(ny, nx)] = c; q.append((ny, nx))
        for (y, x), c in fill.items():
            px[y, x, :3] = c
        print("[rich] painted out %d brow texels" % len(fill))

    # ---- lips -----------------------------------------------------------
    lip_mask = np.zeros((H, W), dtype=bool)
    samples = []
    for xi in range(-36, 37):
        for zi in range(0, 70):
            x = xi * 0.001
            z = hm.eye_z - 0.045 - zi * 0.0005
            hit = skin_hit(Vector((x, hm.face_front_y - 0.08, z)), Vector((0, 1, 0)))
            if hit is None:
                continue
            tx, ty = texel(hit[2])
            c = px[ty, tx]
            samples.append((tx, ty, c[0] / (c[1] + 1e-4)))
    if samples:
        red = np.array([s[2] for s in samples])
        thr = float(np.median(red)) * 1.16
        for tx, ty, r in samples:
            if r > thr:
                lip_mask[ty, tx] = True
        ys, xs = np.nonzero(lip_mask)
        if len(ys):
            y0, y1, x0, x1 = ys.min() - 3, ys.max() + 4, xs.min() - 3, xs.max() + 4
            region = px[y0:y1, x0:x1, :3]
            rr = region[..., 0] / (region[..., 1] + 1e-4)
            m = rr > thr
            # saturate + darken inside the mask (HSV, vectorised)
            mx = region.max(axis=-1); mn = region.min(axis=-1)
            chroma = mx - mn
            sat = np.where(mx > 1e-4, chroma / (mx + 1e-6), 0.0)
            new_sat = np.clip(sat * 1.45, 0.0, 1.0)
            new_mx = mx * 0.88
            new_mn = new_mx * (1.0 - new_sat)
            # keep hue: rescale each channel's offset from min over the old chroma
            frac = np.where(chroma[..., None] > 1e-6, (region - mn[..., None]) / (chroma[..., None] + 1e-9), 0.0)
            boosted = np.clip(new_mn[..., None] + frac * (new_mx - new_mn)[..., None], 0.0, 1.0)
            region[m] = boosted[m]
            print("[rich] lips: %d texels saturated (redness thr %.3f)" % (int(m.sum()), thr))
    img.pixels.foreach_set(px.ravel())
    img.pack()
    print("[rich] albedo retouched and packed:", img.name, W, H)


# ================================================================ build
tune_skin(); tune_eyes(); tune_teeth()
ensure_rest_position()
paint_albedo()

brow_mat = hair_material("LP_hair_brow", BROW_COLOR, roughness=0.45, tip_lighten=0.2)
lash_mat = hair_material("LP_hair_lash", LASH_COLOR, roughness=0.45, tip_lighten=0.0, random_dark=0.8)
head_mat = hair_material("LP_hair_head", HAIR_COLOR, roughness=HAIR_ROUGHNESS, tip_lighten=0.04)  # matte: bright strand sparkle flickers frame to frame

make_curves("LP_browL", brow_strands(+1), brow_mat)
make_curves("LP_browR", brow_strands(-1), brow_mat)
make_curves("LP_lashes", lash_strands(), lash_mat)
make_curves("LP_hair", head_hair_strands(), head_mat)
add_top()

cam, frame_h = S.setup_camera(scn, hm)
S.setup_lights(scn, hm)
S.setup_backdrop(scn, hm)
S.setup_render(scn, 0, 0, samples=args.samples, engine=S.ENGINE)
print("[rich] camera", tuple(round(c, 3) for c in cam.location), "frame_h %.3f" % frame_h)

for k in me.shape_keys.key_blocks:
    k.value = 0.0

os.makedirs(os.path.dirname(os.path.abspath(args.out)), exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=os.path.abspath(args.out))
print("[rich] saved", args.out)

# ---------------------------------------------------------------- test stills
if args.test:
    kb = me.shape_keys.key_blocks
    pb = arm.pose.bones["head"]
    pb.rotation_mode = 'XYZ'

    def pose(sk=None, head=(0, 0, 0)):
        for k in kb:
            k.value = 0.0
        for n, v in (sk or {}).items():
            kb[n].value = v
        pb.rotation_euler = tuple(math.radians(a) for a in head)

    poses = [
        ("neutral", {}, (0, 0, 0)),
        ("open", {"Expressions_mouthOpen_max": 0.55, "Expressions_browOutVertL_max": 1.0,
                  "Expressions_browOutVertR_max": 1.0, "Expressions_browsMidVert_max": 0.8}, (0, 0, 0)),
        ("smile", {"Expressions_mouthSmile_max": 1.0, "Expressions_cheekSneerL_max": 0.1,
                   "Expressions_cheekSneerR_max": 0.1, "Expressions_eyeSquintL_max": 0.4,
                   "Expressions_eyeSquintR_max": 0.4}, (0, 0, 0)),
        ("yaw", {}, (0, -10, 0)),
        ("blink_pitch", {"Expressions_eyeClosedL_max": 1.0, "Expressions_eyeClosedR_max": 1.0}, (8, 0, 4)),
    ]
    os.makedirs(args.test, exist_ok=True)
    import time
    for i, (name, sk, head) in enumerate(poses):
        pose(sk, head)
        scn.frame_set(i)
        t0 = time.time()
        scn.render.filepath = os.path.join(args.test, f"test_{i}_{name}.png")
        bpy.ops.render.render(write_still=True)
        print("[rich] still %s  %.1fs" % (scn.render.filepath, time.time() - t0))
    pose()
    if args.flicker:
        pb.rotation_euler = (0, 0, 0); pb.keyframe_insert("rotation_euler", frame=0)
        pb.rotation_euler = (0, math.radians(-4), 0); pb.keyframe_insert("rotation_euler", frame=args.flicker - 1)
        scn.frame_end = args.flicker - 1
        scn.render.filepath = os.path.join(args.test, "flicker", "frame_")
        bpy.ops.render.render(animation=True)
        print("[rich] flicker frames in", os.path.join(args.test, "flicker"))
