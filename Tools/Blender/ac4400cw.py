"""Turns a GE AC4400CW source model into Highball's parts, laid out as the ES44AC CSX's are: one per livery.

    blender -b --factory-startup --python Tools/Blender/ac4400cw.py -- <Content folder> [--livery csx|bnsf]

Each livery is its own source model, with its own textures, under Meshes/Locomotives/AC4400CW/<Livery>/Source (LIVERIES):
CSX's Locomotive_FBX.fbx (textures in CSX-max), BNSF's BNSF.fbx (textures in Textures). They're the same engine from the
same maker, each some 3,500 loose objects and 4.7 million triangles, but not the same file: BNSF's parts are named
otherwise (its trucks' parts plain Cylinder and Box, not HiAd-*), so before anything else each of its objects takes the
name of the CSX object that's the same (as many vertices, the same middle), and all that follows goes by those names. Its
textures are its own, as it has them: to change a livery's paint, change its textures and run this again. The game sees
one engine, its vehicle.json (under CSX) listing each livery's model.

The source is one of those models: some 3,500
loose objects and 4.7 million triangles. For now everything but the running gear is baked into one body; the trucks and
wheelsets are kept apart so they can swivel. Under AC4400CW/CSX this writes assembly.json (where each part goes), the
parts it's put together from, in Source/Parts (working files, not loaded by the game):
    body_csx.glb             origin on the railhead, midway between the trucks; the headlights, ditch lights, horn, exhaust
                             and bell are empty nodes in it (the geometry stays baked), listed in assembly.json; the
                             windows are a mesh of their own in it, Glass, which the game draws see-through
    truck_front.glb          origin on the railhead at the truck's pivot (its middle axle), turning about Z; each truck is
    truck_rear.glb           its own model, as the source has them (they're mirror images, not one turned round)
    wheelset.glb             origin at the axle's centre, rolling about X
and LOD0/ac4400cw_csx.glb, the whole engine, which the game loads (Truck_Front and Truck_Rear apart), with its .blend in
Source. Tools/Blender/lods.py then makes LOD1 and LOD2 from it, beside LOD0.
The source has no couplers: the engine takes the shared one from Meshes/Couplers, set on the pilots at its standard height.

What's done to the source:
  - The running gear is told apart by name: the source calls the Hi-Ad truck's parts HiAd-*. A wheelset is the two wheels
    (material Wheel), their tread rings (Ref) and the axle; everything else of the truck goes on the truck. The three HiAd
    objects running the whole length (pipes along the frame) and the threaded rods at the trucks' inner ends stay on the
    body.
  - The running gear's built to a gauge of about 1.73 m, and sits some 9 cm off the body's centreline: the trucks and
    wheelsets are squeezed across (only them) so the treads come over the game's 1.435 m rails, and centred under the body.
  - Triangles: first coplanar faces are merged (a planar dissolve within UV islands, materials and seams, which doesn't
    change the shape), then anything still denser than its size calls for is collapsed down to a budget that grows with
    the object's size (BUDGET), so a 3 cm bolt isn't 600 triangles.
  - Materials: the FBX came in with every material fully metallic, and its "-B" maps (grayscale bump maps) wired in as
    normal maps. Metallic is set to nought (bar the wheel treads and lamp reflectors), each bump map is turned into a
    tangent-space normal map, the opacity maps go into their colour's alpha (alpha clip), the glass keeps the source's
    opacity as a blended material (glTF BLEND: the game draws it after the solid world, over what's behind), and every map is cut to at most MAX_SIZE and written as JPEG.

Blender is Z-up, metres; the glTF exporter turns that into the game's Y-up. The cab faces -Y. The source runs along X with
the cab towards +X, so it's turned a quarter about Z (the source's +Y, the engineer's left, becomes +X).
"""
import json
import math
import os
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
LIVERY = args[args.index("--livery") + 1].lower() if "--livery" in args else "csx"
args = [a for i, a in enumerate(args) if a != "--livery" and (i == 0 or args[i - 1] != "--livery")]
CONTENT = os.path.abspath(args[0] if args else "Content")
ENGINE = os.path.join(CONTENT, "Meshes", "Locomotives", "AC4400CW")
# Each livery: its folder, its source model and its textures (under the folder's Source).
LIVERIES = {"csx": ("CSX", "Locomotive_FBX.fbx", "CSX-max"), "bnsf": ("BNSF", "BNSF.fbx", "Textures")}
if LIVERY not in LIVERIES:
    raise SystemExit(f"no livery {LIVERY}: there's {', '.join(LIVERIES)}")
FOLDER, SOURCE_FILE, TEXTURE_FOLDER = LIVERIES[LIVERY]
OUT = os.path.join(ENGINE, FOLDER)
COUPLERS = os.path.join(CONTENT, "Meshes", "Couplers")
PARTS = os.path.join(OUT, "Source", "Parts")
SOURCE = os.path.join(OUT, "Source", SOURCE_FILE)
TEXTURES = os.path.join(OUT, "Source", TEXTURE_FOLDER)
NAMES_FROM = os.path.join(ENGINE, *LIVERIES["csx"][:1], "Source", LIVERIES["csx"][1])  # whose names everything goes by
SOURCE_NAME = f"GE AC4400CW ({FOLDER}), author and licence to be filled in: see CREDITS.txt"

# Measured off the source, in its own axes (X along the engine, cab at +X; Z up; centred near Y = 0).
AXLE_Z = -1.9245
TREAD_R = 0.5125  # the tread ring's radius; the flange reaches 0.5565
RAIL = AXLE_Z - TREAD_R
BODY_Y = 0.0365  # the body's centreline: the headlights' and the stack's middle
STRIKER = 11.151  # the face of the coupler pocket at each end
COUPLER_OUT = 0.0  # the pulling faces on that plane: 22.30 m over them, the real engine's 73 ft 2 in

# The running gear: parts named so, wholly inside a truck's span; the ones below run the whole frame and stay on the body.
TRUCK_PREFIXES = ("HiAd", "Trucks")
BODY_HIAD = {"HiAd", "HiAd.001", "HiAd-zzOK"}
AXLES = {"HiAd-483", "HiAd-484", "HiAd-485", "HiAd-499.001", "HiAd-500.001", "HiAd-501.001"}
WHEELSET_MATERIALS = {"Wheel", "Ref"}

# Lamps by the object that is the lens, in the source; which way each faces, in the game's axes.
LAMPS = {
    "Headlight_Front_Upper": ("Cylinder3517", (0, -1, 0)),
    "Headlight_Front_Lower": ("Cylinder6832", (0, -1, 0)),
    "DitchLight_Front_Left": ("Cylinder006", (0, -1, 0)),
    "DitchLight_Front_Right": ("Cylinder3452", (0, -1, 0)),
    "Headlight_Rear_Upper": ("Cylinder3546", (0, 1, 0)),
    "Headlight_Rear_Lower": ("Cylinder3557", (0, 1, 0)),
}
HORN = ("Replace", "Cylinder146", "Cylinder6830", "Cylinder6834", "Cylinder6835", "Cylinder6836")  # on the long hood
# The stack's mouth, in the source: the top of the oblong in Mid at x -3.35..-2.81, y -0.41..0.48.
EXHAUST = ((-3.081, 0.0365, 2.401), (0.883, 0.542))
# The source has no bell: an empty under the left walkway behind the front truck, where the ES44AC's is; game axes.
BELL = ((1.105, -3.40, 1.155), (0, 0, -1))

MAX_SIZE = 2048
BUMP_STRENGTH = 3.0
FLAT = 1.0  # degrees: faces this near coplanar are merged


def BUDGET(diagonal):
    """The most triangles an object of this size (its bounding box's diagonal, metres) is left with."""
    return max(32, int(3000 * diagonal))


def facing(direction):
    """A rotation whose -Y is the direction given, kept upright (its Z up) unless it points straight up or down."""
    back = -Vector(direction).normalized()
    up = Vector((0, 0, 1)) if abs(back.z) < .9 else Vector((0, 1, 0))
    right = back.cross(up).normalized()
    up = right.cross(back)
    return Matrix((right, back, up)).transposed().to_4x4()


RAIL_CENTRE = 1.435 / 2 + 0.0373  # the game's rails' middles off the track's centre (DisplayTrack.RailOffset)


def to_game(origin_x, origin_z, origin_y=BODY_Y, squeeze=1.0):
    """The source's axes to the game's: (x, y, z) to (y, -x, z), after moving the origin given to nought, and squeezed
    across by the factor given."""
    turn = Matrix(((0, 1, 0, 0), (-1, 0, 0, 0), (0, 0, 1, 0), (0, 0, 0, 1)))
    return turn @ Matrix.Diagonal((1, squeeze, 1, 1)) @ Matrix.Translation((-origin_x, -origin_y, -origin_z))


def world_verts(obj):
    n = len(obj.data.vertices)
    co = np.empty(n * 3, np.float32)
    obj.data.vertices.foreach_get("co", co)
    m = np.array(obj.matrix_world)
    return co.reshape(-1, 3) @ m[:3, :3].T + m[:3, 3]


def bounds(obj):
    w = world_verts(obj)
    return Vector(w.min(0)), Vector(w.max(0))


def middle(obj):
    lo, hi = bounds(obj)
    return (lo + hi) / 2


def tris(obj):
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def islands(bm):
    seen, out = set(), []
    for f in bm.faces:
        if f in seen:
            continue
        stack, comp = [f], []
        seen.add(f)
        while stack:
            g = stack.pop()
            comp.append(g)
            for e in g.edges:
                for h in e.link_faces:
                    if h not in seen:
                        seen.add(h)
                        stack.append(h)
        out.append(comp)
    return out


# ---- materials --------------------------------------------------------------------------------------------------------

def pixels(image):
    px = np.empty(len(image.pixels), np.float32)
    image.pixels.foreach_get(px)
    return px.reshape(image.size[1], image.size[0], 4)


def scaled(image):
    """The image's pixels, scaled down to at most MAX_SIZE on its longer side."""
    copy = image.copy()
    w, h = copy.size
    limit = MAX_SIZE
    if max(w, h) > limit:
        k = limit / max(w, h)
        copy.scale(max(1, int(w * k)), max(1, int(h * k)))
    px = pixels(copy)
    bpy.data.images.remove(copy)
    return px


def new_image(name, px, colorspace):
    h, w = px.shape[:2]
    made = bpy.data.images.new(name, w, h, alpha=True)
    made.colorspace_settings.name = colorspace
    made.pixels.foreach_set(px.astype(np.float32).ravel())
    made.pack()
    return made


_made = {}


def colour_map(image, opacity=None):
    key = ("colour", image.filepath, opacity.filepath if opacity else None)
    if key not in _made:
        px = scaled(image)
        if opacity is not None:
            o = opacity.copy()
            o.scale(px.shape[1], px.shape[0])
            px[..., 3] = pixels(o)[..., 0]
            bpy.data.images.remove(o)
        _made[key] = new_image(os.path.splitext(os.path.basename(image.filepath))[0] + "_Colour", px, "sRGB")
    return _made[key]


def normal_map(bump):
    """A tangent-space normal map (OpenGL, +Y up the texture) from a grayscale bump map: its slopes, steepened."""
    key = ("normal", bump.filepath)
    if key not in _made:
        h = scaled(bump)[..., :3].mean(2)
        dx = (np.roll(h, -1, 1) - np.roll(h, 1, 1)) * 0.5 * BUMP_STRENGTH * h.shape[1] / 512
        dy = (np.roll(h, -1, 0) - np.roll(h, 1, 0)) * 0.5 * BUMP_STRENGTH * h.shape[0] / 512
        n = np.stack([-dx, -dy, np.ones_like(h)], 2)
        n /= np.linalg.norm(n, axis=2, keepdims=True)
        px = np.concatenate([n * 0.5 + 0.5, np.ones_like(h)[..., None]], 2)
        _made[key] = new_image(os.path.splitext(os.path.basename(bump.filepath))[0] + "_Normal", px, "Non-Color")
    return _made[key]


METALLIC = {"Ref": (0.9, 0.35), "M-Light": (1.0, 0.25)}  # wheel treads, lamp reflectors: (metallic, roughness)


def game_materials():
    for image in bpy.data.images:
        found = os.path.join(TEXTURES, os.path.basename(image.filepath.replace("\\", "/")))
        if os.path.exists(found):
            image.filepath = found
            image.reload()
    for mat in list(bpy.data.materials):  # (a copy: renaming re-sorts the list)
        if not mat.use_nodes:
            continue
        nodes, links = mat.node_tree.nodes, mat.node_tree.links
        bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
        base = next((l.from_node.image for l in links if l.to_node == bsdf and l.to_socket.name == "Base Color" and l.from_node.type == "TEX_IMAGE"), None)
        alpha = next((l.from_node.image for l in links if l.to_node == bsdf and l.to_socket.name == "Alpha" and l.from_node.type == "TEX_IMAGE"), None)
        bump = next((l.from_node.image for l in links if l.to_node.type == "NORMAL_MAP" and l.from_node.type == "TEX_IMAGE"), None)
        colour = tuple(bsdf.inputs["Base Color"].default_value)
        opacity = bsdf.inputs["Alpha"].default_value
        metallic, roughness = METALLIC.get(mat.name, (0.0, bsdf.inputs["Roughness"].default_value))
        if mat.name == "Glass":
            roughness = 0.05

        nodes.clear()
        out = nodes.new("ShaderNodeOutputMaterial")
        p = nodes.new("ShaderNodeBsdfPrincipled")
        links.new(p.outputs[0], out.inputs[0])
        p.inputs["Metallic"].default_value = metallic
        p.inputs["Roughness"].default_value = roughness
        p.inputs["Base Color"].default_value = colour
        if mat.name == "Glass":
            # A constant alpha under 1, which the glTF exporter writes as BLEND.
            p.inputs["Alpha"].default_value = opacity
        if base is not None:
            tex = nodes.new("ShaderNodeTexImage")
            tex.image = colour_map(base, alpha)
            links.new(tex.outputs["Color"], p.inputs["Base Color"])
            if alpha is not None:
                # Alpha clip, in the form the glTF exporter reads as MASK at 0.5.
                less = nodes.new("ShaderNodeMath")
                less.operation = "LESS_THAN"
                less.inputs[1].default_value = 0.5
                links.new(tex.outputs["Alpha"], less.inputs[0])
                flip = nodes.new("ShaderNodeMath")
                flip.operation = "SUBTRACT"
                flip.inputs[0].default_value = 1.0
                links.new(less.outputs[0], flip.inputs[1])
                links.new(flip.outputs[0], p.inputs["Alpha"])
        if bump is not None:
            tex = nodes.new("ShaderNodeTexImage")
            tex.image = normal_map(bump)
            nm = nodes.new("ShaderNodeNormalMap")
            links.new(tex.outputs["Color"], nm.inputs["Color"])
            links.new(nm.outputs["Normal"], p.inputs["Normal"])
        mat.name = f"AC44_{mat.name}"
        print("material", mat.name, "colour map" if base else "flat", "+ normal" if bump else "", "+ alpha clip" if alpha else "", f"metallic {metallic} roughness {roughness:.2f}")


# ---- triangles --------------------------------------------------------------------------------------------------------

def lighten(objects):
    before = sum(tris(o) for o in objects)
    graph = bpy.context.evaluated_depsgraph_get()
    for obj in objects:
        lo, hi = bounds(obj)
        budget = BUDGET((hi - lo).length)
        if tris(obj) > 4 * budget:
            # Far over: collapsed as it is. Dissolved first, the big dense ones (BNSF's truck frames, HiAd-47x) come out
            # as a mesh collapsing never finishes on.
            d = obj.modifiers.new("collapse", "DECIMATE")
            d.decimate_type = "COLLAPSE"
            d.ratio = budget / tris(obj)
            d.use_collapse_triangulate = True
            continue
        d = obj.modifiers.new("flat", "DECIMATE")
        d.decimate_type = "DISSOLVE"
        d.angle_limit = math.radians(FLAT)
        d.delimit = {"UV", "MATERIAL", "SEAM", "SHARP"}
        obj.modifiers.new("tri", "TRIANGULATE")
    apply_all(objects)
    for obj in objects:
        lo, hi = bounds(obj)
        budget = BUDGET((hi - lo).length)
        n = tris(obj)
        if n > budget:
            d = obj.modifiers.new("collapse", "DECIMATE")
            d.decimate_type = "COLLAPSE"
            d.ratio = budget / n
            d.use_collapse_triangulate = True
    apply_all(objects)
    print("triangles", before, "->", sum(tris(o) for o in objects))


def apply_all(objects):
    graph = bpy.context.evaluated_depsgraph_get()
    for obj in objects:
        if not obj.modifiers:
            continue
        mesh = bpy.data.meshes.new_from_object(obj.evaluated_get(graph))
        old = obj.data
        obj.modifiers.clear()
        obj.data = mesh
        if old.users == 0:
            bpy.data.meshes.remove(old)


# ---- parts ------------------------------------------------------------------------------------------------------------

def join(objects, name):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    if len(objects) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    obj.name = obj.data.name = name
    return obj


def place(obj, matrix):
    """Bakes the object's world transform and the matrix given into its mesh, leaving it at the origin."""
    obj.data.transform(matrix @ obj.matrix_world)
    obj.matrix_world = Matrix.Identity(4)


def empty(name, matrix):
    e = bpy.data.objects.new(name, None)
    e.empty_display_type, e.empty_display_size = "ARROWS", .2
    bpy.context.collection.objects.link(e)
    e.matrix_world = matrix
    return e


def write(path, objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=False, export_yup=True, export_materials="EXPORT",
                              export_normals=True, export_texcoords=True, export_image_format="JPEG", export_jpeg_quality=90, export_animations=False)
    n = sum(tris(o) for o in objects if o.type == "MESH")
    print("wrote", path, n, "triangles", os.path.getsize(path) // 1024, "KB")
    return n


def trumpets(horn):
    """Each trumpet of the horn, in the game's axes: where its mouth is and which way along the engine it opens."""
    bm = bmesh.new()
    bm.from_mesh(horn.data)
    bm.transform(horn.matrix_world)
    out = []
    for comp in islands(bm):
        vs = list({v for g in comp for v in g.verts})
        if len(comp) < 40:
            continue
        ys = [v.co.y for v in vs]
        y0, y1 = min(ys), max(ys)
        if y1 - y0 < .1:
            continue  # not a trumpet: a bracket or the manifold
        cx = sum(v.co.x for v in vs) / len(vs)
        cz = sum(v.co.z for v in vs) / len(vs)
        spread = lambda end: max(math.hypot(v.co.x - cx, v.co.z - cz) for v in vs if abs(v.co.y - end) < .03)
        opens = -1 if spread(y0) > spread(y1) else 1
        mouth = y0 if opens < 0 else y1
        out.append({"mouth": [round(cx, 4), round(mouth, 4), round(cz, 4)], "facing": [0, opens, 0]})
    bm.free()
    return sorted(out, key=lambda t: (t["mouth"][0], t["mouth"][2]))


# ---- build ------------------------------------------------------------------------------------------------------------

def fingerprint(obj):
    """What makes an object the same in both sources: its vertices' count and its middle, to the millimetre."""
    w = world_verts(obj)
    lo, hi = w.min(0), w.max(0)
    return (len(w),) + tuple(int(round(v * 1000)) for v in (lo + hi) / 2)


NAMES_FILE = os.path.join(os.path.dirname(NAMES_FROM), "names.json")  # the CSX source's names, by fingerprint


def write_csx_names():
    """Writes NAMES_FILE: every object of the CSX source by its fingerprint (ones whose fingerprints clash left out). Run in
    a Blender of its own (--names-only): its FBX importer crashes importing a second file in one session."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=NAMES_FROM)
    names, clashes = {}, set()
    for o in bpy.data.objects:
        if o.type == "MESH":
            f = ",".join(map(str, fingerprint(o)))
            if f in names:
                clashes.add(f)
            names[f] = o.name
    with open(NAMES_FILE, "w") as f:
        json.dump({k: v for k, v in names.items() if k not in clashes}, f)
    print("wrote", NAMES_FILE, len(names) - len(clashes), "names")


def csx_names():
    """The CSX source's names by fingerprint, from NAMES_FILE: written first, by another Blender, if it's missing or older
    than the source."""
    if not os.path.exists(NAMES_FILE) or os.path.getmtime(NAMES_FILE) < os.path.getmtime(NAMES_FROM):
        import subprocess
        subprocess.run([bpy.app.binary_path, "-b", "--factory-startup", "--python", os.path.abspath(__file__), "--", CONTENT,
                        "--names-only"], check=True)
    with open(NAMES_FILE) as f:
        return {tuple(int(v) for v in k.split(",")): n for k, n in json.load(f).items()}


if "--names-only" in sys.argv:
    write_csx_names()
    sys.exit(0)

names = csx_names() if SOURCE != NAMES_FROM else None
bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.fbx(filepath=SOURCE)
for o in [o for o in bpy.data.objects if o.type == "MESH"]:
    # Not the engine: anything longer or wider than it could be (BNSF's has a 35 x 17 m ground sheet, Plane076, under it).
    lo, hi = bounds(o)
    if max(hi - lo) > 30.0:
        print("dropped", o.name, "not part of the engine:", tuple(round(c, 1) for c in hi - lo), "m")
        bpy.data.objects.remove(o)
meshes = [o for o in bpy.data.objects if o.type == "MESH"]
for mat in list(bpy.data.materials):  # BNSF's are "Converted_<CSX's name>" (a copy: renaming re-sorts the list)
    if mat.name.startswith("Converted_"):
        mat.name = mat.name[len("Converted_"):]
if names is not None:
    # Each object the name of its CSX double, where it has one (the rest keep theirs: out of the way first, so no name
    # it takes is held by another).
    found = {o: names.get(fingerprint(o)) for o in meshes}
    for o in meshes:
        o.name = "__" + o.name
    for o, name in found.items():
        if name is not None:
            o.name = name
    for o in meshes:
        if o.name.startswith("__"):
            o.name = o.name[2:]
    print("named after CSX's:", sum(1 for n in found.values() if n), "of", len(meshes), "objects")
for o in [o for o in bpy.data.objects if o.type != "MESH"]:
    bpy.data.objects.remove(o)
for o in meshes:  # each its own mesh, so baking one's transform doesn't move another
    if o.data.users > 1:
        o.data = o.data.copy()



wheels = [o for o in meshes if o.active_material and o.active_material.name == "Wheel"]
axles_x = sorted({round(middle(o).x, 3) for o in wheels})
if len(axles_x) != 6:
    raise RuntimeError(f"axles at {axles_x}, not six: has the source changed?")
rear_axles, front_axles = axles_x[:3], axles_x[3:]  # the cab's at +X
FRONT_PIVOT, REAR_PIVOT = front_axles[1], rear_axles[1]
MIDDLE = (FRONT_PIVOT + REAR_PIVOT) / 2
print("axles", axles_x, "pivots", FRONT_PIVOT, REAR_PIVOT)
# The source's running gear is built to a gauge of about 1.73 m, and sits off the body's centreline: it's squeezed across
# so the treads' middles come over the game's rails, and centred under the body.
treads = [middle(o).y for o in meshes if o.active_material and o.active_material.name == "Ref"]
left, right = [y for y in treads if y > 0], [y for y in treads if y < 0]
GEAR_Y = (np.mean(left) + np.mean(right)) / 2
SQUEEZE = RAIL_CENTRE / ((np.mean(left) - np.mean(right)) / 2)
print("running gear centred at y", round(GEAR_Y, 4), "treads", round(np.mean(left) - np.mean(right), 4), "apart, squeezed by", round(SQUEEZE, 4))
GAME = to_game(MIDDLE, RAIL)

# Fixtures, from the source before it's changed.
FOUND = {}
fixtures = []
for name, (part, direction) in LAMPS.items():
    at = GAME @ middle(bpy.data.objects[part])
    fixtures.append(empty(name, Matrix.Translation(at) @ facing(direction)))
    FOUND[name] = {"kind": "lamp", "position": [round(c, 4) for c in at], "facing": list(direction), "source": part}
horn_parts = [bpy.data.objects[n] for n in HORN]
lo = Vector(np.min([bounds(o)[0] for o in horn_parts], 0))
hi = Vector(np.max([bounds(o)[1] for o in horn_parts], 0))
horn_at = GAME @ ((lo + hi) / 2)
horn_copy = bpy.data.objects.new("horn_copy", bpy.data.meshes.new("horn_copy"))
bm = bmesh.new()
for o in horn_parts:
    m = o.data.copy()
    m.transform(GAME @ o.matrix_world)
    bm.from_mesh(m)
    bpy.data.meshes.remove(m)
bm.to_mesh(horn_copy.data)
bm.free()
FOUND["Horn"] = {"kind": "horn", "position": [round(c, 4) for c in horn_at], "facing": [0, -1, 0], "trumpets": trumpets(horn_copy)}
bpy.data.meshes.remove(horn_copy.data)
fixtures.append(empty("Horn", Matrix.Translation(horn_at) @ facing((0, -1, 0))))
exhaust_at = GAME @ Vector(EXHAUST[0])
fixtures.append(empty("Exhaust", Matrix.Translation(exhaust_at) @ facing((0, 0, 1))))
FOUND["Exhaust"] = {"kind": "exhaust", "position": [round(c, 4) for c in exhaust_at], "facing": [0, 0, 1], "opening": list(EXHAUST[1])}
fixtures.append(empty("Bell", Matrix.Translation(BELL[0]) @ facing(BELL[1])))
FOUND["Bell"] = {"kind": "bell", "position": list(BELL[0]), "facing": list(BELL[1]), "triangles": 0,
                 "note": "the source has no bell: an empty node, placed where the ES44AC's is, for its sound"}
for name, f in FOUND.items():
    print("fixture", name, f)


def truck_of(obj):
    """0 for the front truck, 1 for the rear, None if it's not on a truck."""
    if obj.name in BODY_HIAD or not obj.name.startswith(TRUCK_PREFIXES):
        return None
    lo, hi = bounds(obj)
    for i, pivot in enumerate((FRONT_PIVOT, REAR_PIVOT)):
        if lo.x > pivot - 3.0 and hi.x < pivot + 3.0:
            return i
    return None


def axle_of(obj):
    """Which axle (an index into axles_x) a wheelset part belongs to, or None."""
    if obj.name not in AXLES and not (obj.active_material and obj.active_material.name in WHEELSET_MATERIALS):
        return None
    x = middle(obj).x
    k = min(range(6), key=lambda i: abs(axles_x[i] - x))
    return k if abs(axles_x[k] - x) < .05 else None


# The source has every pane of glass (the windscreens, the side and back windows) in this one object.
GLASS = "Glass"
GLASS_OPACITY = next(n for n in bpy.data.objects[GLASS].active_material.node_tree.nodes if n.type == "BSDF_PRINCIPLED").inputs["Alpha"].default_value

groups = {"body": [], "glass": [], "truck0": [], "truck1": [], **{f"axle{k}": [] for k in range(6)}}
for o in meshes:
    if o.name == GLASS:
        groups["glass"].append(o)
        continue
    k = axle_of(o)
    if k is not None:
        groups[f"axle{k}"].append(o)
        continue
    t = truck_of(o)
    groups["body" if t is None else f"truck{t}"].append(o)
for k, v in groups.items():
    print("group", k, len(v), "objects", sum(tris(o) for o in v), "triangles")
for k in range(6):
    if len(groups[f"axle{k}"]) != 5:
        raise RuntimeError(f"axle {k} has {len(groups[f'axle{k}'])} parts, not two wheels, two treads and the axle")

game_materials()
lighten(meshes)

# The body: everything else in one object, on the railhead midway between the trucks.
for o in groups["body"]:
    place(o, GAME)
body = join(groups["body"], "Body")
for o in groups["glass"]:
    place(o, GAME)
glass = join(groups["glass"], "Glass")

# Each truck on its own, its origin at its pivot on the railhead; its wheelsets with their origins at the axles.
trucks, wheelsets = [], []
for i, (pivot, axles) in enumerate(((FRONT_PIVOT, front_axles), (REAR_PIVOT, rear_axles))):
    label = ("Front", "Rear")[i]
    for o in groups[f"truck{i}"]:
        place(o, to_game(pivot, RAIL, GEAR_Y, SQUEEZE))
    trucks.append(join(groups[f"truck{i}"], f"Truck_{label}"))
    sets = []
    for a in axles:
        k = axles_x.index(a)
        for o in groups[f"axle{k}"]:
            place(o, to_game(a, AXLE_Z, GEAR_Y, SQUEEZE))
        sets.append(join(groups[f"axle{k}"], f"Wheelset_{label}_{len(sets) + 1}"))
    wheelsets.append(sets)

with open(os.path.join(COUPLERS, "coupler.json")) as f:
    coupler = json.load(f)
COUPLER_Z = coupler["height"]
REACH = coupler["reach"]
front_face = -(STRIKER - MIDDLE + COUPLER_OUT)  # game Y: the cab's at -Y
rear_face = STRIKER + MIDDLE + COUPLER_OUT
COUPLER_FRONT, COUPLER_REAR = front_face + REACH, rear_face - REACH
OVER_COUPLERS = rear_face - front_face
TRUCK_Y = (FRONT_PIVOT - REAR_PIVOT) / 2

budget = {}
budget["body"] = write(os.path.join(PARTS, f"body_{LIVERY}.glb"), [body, glass] + fixtures)
budget["truck_front"] = write(os.path.join(PARTS, "truck_front.glb"), [trucks[0]])
budget["truck_rear"] = write(os.path.join(PARTS, "truck_rear.glb"), [trucks[1]])
budget["wheelset"] = write(os.path.join(PARTS, "wheelset.glb"), [wheelsets[0][0]])
whole = budget["body"] + budget["truck_front"] + budget["truck_rear"] + sum(tris(w) for s in wheelsets for w in s)
print("whole engine triangles, less couplers", whole)


def on_truck(axles, pivot):
    return [[0, round(-(a - pivot), 4), TREAD_R] for a in axles]


COUPLER_MODEL = "Couplers/coupler.glb"
assembly = {
    "source": SOURCE_NAME,
    "lengthOverCouplers": round(OVER_COUPLERS, 3),
    "lods": ["LOD0", "LOD1", "LOD2"],
    "trianglesWholeEngine": {"LOD0": whole},
    "models": "The game loads LOD0/ac4400cw_csx.glb (LOD1 and LOD2 beside it, from Tools/Blender/lods.py). The parts below are Source/Parts/<model>.glb, put together into it, except the couplers: Meshes/Couplers/coupler.glb, the same on every engine and car",
    "bodies": [f"body_{LIVERY}"],
    "truckByBody": {},
    "trucks": [
        {"model": "truck_front", "position": [0, -round(TRUCK_Y, 4), 0], "yaw": 0, "wheelsets": on_truck(front_axles, FRONT_PIVOT)},
        {"model": "truck_rear", "position": [0, round(TRUCK_Y, 4), 0], "yaw": 0, "wheelsets": on_truck(rear_axles, REAR_PIVOT)},
    ],
    "wheelsets": {"model": "wheelset", "onTruck": "each truck's own, in its entry above (the two trucks are mirror images)", "radius": TREAD_R},
    "couplers": [{"model": COUPLER_MODEL, "position": [0, round(COUPLER_FRONT, 4), COUPLER_Z], "yaw": 0},
                 {"model": COUPLER_MODEL, "position": [0, round(COUPLER_REAR, 4), COUPLER_Z], "yaw": 180}],
    "glass": {"node": "Glass", "material": "AC44_Glass", "triangles": tris(glass), "opacity": round(GLASS_OPACITY, 3),
              "note": "every window, a mesh of its own in the body model, its material blended (glTF BLEND) at the source's opacity: the game draws it see-through"},
    "fixtures": FOUND,
    "fixturesNote": "Empty nodes in the body model by these names (the geometry's baked into the body). position: the node's origin; facing: the way its light, horn or bell points, the node's local -Y in Blender and +Z in the glTF",
    "axes": "Blender Z-up metres, front towards -Y; in the files, glTF Y-up: (x, y, z) here is (x, z, -y) there",
}
with open(os.path.join(OUT, "assembly.json"), "w") as f:
    json.dump(assembly, f, indent=2)
print("assembly", {k: assembly[k] for k in ("lengthOverCouplers", "trucks", "couplers")})


def combined():
    """The whole engine, which the game loads: every part on an empty named for it, so a truck turns with its wheels."""
    bpy.ops.wm.read_factory_settings(use_empty=True)

    def holder(name, at=(0, 0, 0), yaw=0.0, parent=None):
        e = bpy.data.objects.new(name, None)
        e.empty_display_size = .3
        bpy.context.collection.objects.link(e)
        e.parent = parent
        e.location = at
        e.rotation_euler = (0, 0, yaw)
        return e

    def load(file, parent, prefix):
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=os.path.join(PARTS, file))
        for obj in set(bpy.data.objects) - before:
            if obj.parent is None:
                obj.parent = parent
                obj.matrix_parent_inverse = Matrix.Identity(4)
            obj.name = f"{prefix}_{obj.name}"

    root = holder("AC4400CW")
    load(f"body_{LIVERY}.glb", holder("Body", parent=root), "Body")
    for label, y, axles, pivot, model in (("Front", -TRUCK_Y, front_axles, FRONT_PIVOT, "truck_front"), ("Rear", TRUCK_Y, rear_axles, REAR_PIVOT, "truck_rear")):
        truck_at = holder(f"Truck_{label}", (0, y, 0), 0.0, root)
        load(f"{model}.glb", truck_at, f"Truck_{label}")
        for k, (_, at, z) in enumerate(on_truck(axles, pivot), 1):
            load("wheelset.glb", holder(f"Wheelset_{label}_{k}", (0, at, z), 0.0, truck_at), f"Wheelset_{label}_{k}")
    for label, y, yaw in (("Front", COUPLER_FRONT, 0.0), ("Rear", COUPLER_REAR, math.pi)):
        load(os.path.join(COUPLERS, "coupler.glb"), holder(f"Coupler_{label}", (0, y, COUPLER_Z), yaw, root), f"Coupler_{label}")
    bpy.context.view_layer.update()
    folder = os.path.join(OUT, "LOD0")
    os.makedirs(folder, exist_ok=True)
    for obj in bpy.data.objects:
        obj.select_set(True)
    bpy.ops.export_scene.gltf(filepath=os.path.join(folder, f"ac4400cw_{LIVERY}.glb"), export_format="GLB", use_selection=True, export_yup=True,
                              export_materials="EXPORT", export_normals=True, export_texcoords=True, export_image_format="JPEG", export_jpeg_quality=90,
                              export_animations=False)
    bpy.ops.file.pack_all()
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, "Source", f"ac4400cw_{LIVERY}.blend"), check_existing=False)
    print("wrote", folder, len(bpy.data.objects), "objects")


combined()
