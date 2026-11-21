"""Turns the CSX (ETTX) enclosed autorack source model into Highball's parts, laid out as the AC4400CW's are.

    blender -b --factory-startup --python Tools/Blender/csx_autorack.py -- <Content folder>

The source is Meshes/RollingStock/Autorack/CSX/Source/Carrier.fbx (unpacked from fbx.zip beside it) with its one texture in
Source/Textures/Carrier.jpg: some 1,400 loose objects and 960,000 triangles, built at a little over half size. Everything but
the running gear is baked into one body; the trucks and wheelsets are kept apart so they can swivel. Under Autorack/CSX
this writes assembly.json (where each part goes), the parts it's put together from, in Source/Parts (working files, not
loaded by the game):
    body_csx.glb         origin on the railhead, midway between the trucks
    truck_front.glb      origin on the railhead at the truck's centre pin, turning about Z
    truck_rear.glb
    wheelset.glb         origin at the axle's centre, rolling about X
and LOD0/autorack_csx.glb, the whole car, which the game loads (Truck_Front and Truck_Rear apart), with its .blend in
Source. Tools/Blender/lods.py then makes LOD1 and LOD2 from it, beside LOD0.

What's done to the source:
  - It's scaled up so its truck centres are the real car's 64 ft (TRUCK_CENTRES): about 1.92 times. Its body then comes out
    at 90 ft over the end sills, 3.15 m wide and 6.1 m above the rail, as an 89 ft enclosed bi-level is.
  - The running gear is told apart by where it is: a wheelset is what's centred on an axle (two wheels, their tread rings
    and the axle), a truck what lies wholly under the car within its span of the centre pin; everything else is the body.
  - The running gear's built to a gauge of about 2 m once scaled: the trucks and wheelsets are squeezed across (only them)
    so the treads come over the game's rails, and centred under the body.
  - Its own couplers are taken off (COUPLER_PARTS) and the shared one from Meshes/Couplers goes on at the standard height,
    its knuckle's nose where the source's was; the air hoses and angle cocks stay.
  - Triangles: coplanar faces are merged, then anything still denser than its size calls for is collapsed down to a budget
    that grows with the object's size (BUDGET).
  - Materials: every one non-metallic but the wheel treads (Crome); the texture's cut to MAX_SIZE and written as JPEG.
  - The ground plane under it (Plane001) is dropped.

Blender is Z-up, metres; the glTF exporter turns that into the game's Y-up. The car's front (its A end; the brake wheel's at
the B end) faces -Y. The source runs along X with the A end towards +X, so it's turned a quarter about Z.
"""
import json
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
CONTENT = os.path.abspath(args[0] if args else "Content")
OUT = os.path.join(CONTENT, "Meshes", "RollingStock", "Autorack", "CSX")
COUPLERS = os.path.join(CONTENT, "Meshes", "Couplers")
PARTS = os.path.join(OUT, "Source", "Parts")
SOURCE = os.path.join(OUT, "Source", "Carrier.fbx")
TEXTURES = os.path.join(OUT, "Source", "Textures")
LIVERY = "csx"
MODEL = f"autorack_{LIVERY}"
SOURCE_NAME = "Enclosed bi-level autorack, CSX (ETTX 710524), author and licence to be filled in: see CREDITS.txt"

TRUCK_CENTRES = 19.507  # 64 ft, an 89 ft autorack's
DROP = {"Plane001"}  # the ground it stands on
# The source's couplers, at the A end (+X) and the B end (-X): head and shank, knuckle, knuckle pin, lift-pin bracket.
COUPLER_PARTS = {"Cylinder1922", "Cylinder1920", "Cylinder1921", "Box5422", "Cylinder174", "Cylinder175", "Cylinder176", "Box4464"}
COUPLER_HEADS = ("Cylinder1922", "Cylinder174")  # their furthest points are the knuckles' noses
TREAD_MATERIAL = "Crome"  # the tread rings: one on each wheel
BODY_SIDES = ("Box5785", "Box5771")  # the side sheets: the body's centreline is midway between them

MAX_SIZE = 4096  # the source's map is 8192 square, the lettering small on it
FLAT = 1.0  # degrees: faces this near coplanar are merged


def BUDGET(diagonal):
    """The most triangles an object of this size (its bounding box's diagonal, metres) is left with."""
    return max(32, int(3000 * diagonal))


RAIL_CENTRE = 1.435 / 2 + 0.0373  # the game's rails' middles off the track's centre (DisplayTrack.RailOffset)


def to_game(origin_x, origin_z, origin_y, squeeze=1.0):
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


def material(obj):
    return obj.active_material.name if obj.active_material else None


# ---- materials --------------------------------------------------------------------------------------------------------

def pixels(image):
    px = np.empty(len(image.pixels), np.float32)
    image.pixels.foreach_get(px)
    return px.reshape(image.size[1], image.size[0], 4)


def scaled(image):
    """The image, scaled down to at most MAX_SIZE on its longer side, as a new packed image."""
    copy = image.copy()
    w, h = copy.size
    if max(w, h) > MAX_SIZE:
        k = MAX_SIZE / max(w, h)
        copy.scale(max(1, int(w * k)), max(1, int(h * k)))
    px = pixels(copy)
    bpy.data.images.remove(copy)
    made = bpy.data.images.new(os.path.splitext(os.path.basename(image.filepath))[0] + "_Colour", px.shape[1], px.shape[0], alpha=True)
    made.colorspace_settings.name = "sRGB"
    made.pixels.foreach_set(px.ravel())
    made.pack()
    return made


METALLIC = {TREAD_MATERIAL: (0.9, 0.35)}  # (metallic, roughness)


def game_materials():
    for image in bpy.data.images:
        found = os.path.join(TEXTURES, os.path.basename(image.filepath.replace("\\", "/")))
        if os.path.exists(found):
            image.filepath = found
            image.reload()
    made = {}
    for mat in bpy.data.materials:
        if not mat.use_nodes:
            continue
        nodes, links = mat.node_tree.nodes, mat.node_tree.links
        bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
        base = next((l.from_node.image for l in links if l.to_node == bsdf and l.to_socket.name == "Base Color" and l.from_node.type == "TEX_IMAGE"), None)
        colour = tuple(bsdf.inputs["Base Color"].default_value)
        metallic, roughness = METALLIC.get(mat.name, (0.0, max(0.35, bsdf.inputs["Roughness"].default_value)))

        nodes.clear()
        out = nodes.new("ShaderNodeOutputMaterial")
        p = nodes.new("ShaderNodeBsdfPrincipled")
        links.new(p.outputs[0], out.inputs[0])
        p.inputs["Metallic"].default_value = metallic
        p.inputs["Roughness"].default_value = roughness
        p.inputs["Base Color"].default_value = colour
        if base is not None:
            if base.name not in made:
                made[base.name] = scaled(base)
            tex = nodes.new("ShaderNodeTexImage")
            tex.image = made[base.name]
            links.new(tex.outputs["Color"], p.inputs["Base Color"])
        mat.name = f"Autorack_{mat.name.replace(' ', '')}"
        print("material", mat.name, "colour map" if base else "flat", f"metallic {metallic} roughness {roughness:.2f}")


# ---- triangles --------------------------------------------------------------------------------------------------------

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


def lighten(objects):
    before = sum(tris(o) for o in objects)
    for obj in objects:
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


# ---- build ------------------------------------------------------------------------------------------------------------

for o in list(bpy.data.objects):
    bpy.data.objects.remove(o)
# (The legacy FBX importer, import_scene.fbx, crashes Blender 5.1 on this file; the built-in one reads it.)
bpy.ops.wm.fbx_import(filepath=SOURCE)
for o in [o for o in bpy.data.objects if o.type != "MESH" or o.name in DROP]:
    bpy.data.objects.remove(o)
meshes = [o for o in bpy.data.objects if o.type == "MESH"]
for o in meshes:  # each its own mesh, so baking one's transform doesn't move another
    if o.data.users > 1:
        o.data = o.data.copy()

# The scale, from the truck centres: each truck's centre pin is midway between its two axles.
rings = [o for o in meshes if material(o) == TREAD_MATERIAL]
axles_x = sorted({round(middle(o).x, 3) for o in rings})
if len(rings) != 8 or len(axles_x) != 4:
    raise RuntimeError(f"{len(rings)} tread rings on axles at {axles_x}, not eight on four: has the source changed?")
SCALE = TRUCK_CENTRES / abs((axles_x[2] + axles_x[3]) / 2 - (axles_x[0] + axles_x[1]) / 2)
print("scale", round(SCALE, 4))
for o in meshes:
    place(o, Matrix.Scale(SCALE, 4))

# Everything below is measured after scaling.
axles_x = sorted({round(middle(o).x, 4) for o in rings})
rear_axles, front_axles = axles_x[:2], axles_x[2:]  # the A end's at +X
FRONT_PIVOT, REAR_PIVOT = sum(front_axles) / 2, sum(rear_axles) / 2
MIDDLE = (FRONT_PIVOT + REAR_PIVOT) / 2
AXLE_Z = float(np.mean([middle(o).z for o in rings]))


def tread_radius(ring):
    """The radius of the ring's tread: at its outer edge, away from the flange (which is on the inside, towards the car's
    middle)."""
    w = world_verts(ring)
    c = middle(ring)
    outward = np.sign(c.y - GEAR_Y)
    across = (w[:, 1] - c.y) * outward
    edge = across > across.max() - 0.25 * (across.max() - across.min())
    return float(np.hypot(w[edge, 0] - c.x, w[edge, 2] - c.z).max())


ring_y = [middle(o).y for o in rings]
left, right = [y for y in ring_y if y > np.mean(ring_y)], [y for y in ring_y if y <= np.mean(ring_y)]
GEAR_Y = (np.mean(left) + np.mean(right)) / 2
SQUEEZE = RAIL_CENTRE / ((np.mean(left) - np.mean(right)) / 2)
TREAD_R = float(np.mean([tread_radius(o) for o in rings]))
FLANGE_R = float(np.mean([(bounds(o)[1].z - bounds(o)[0].z) / 2 for o in rings]))
RAIL = AXLE_Z - TREAD_R
BODY_Y = float(np.mean([middle(bpy.data.objects[n]).y for n in BODY_SIDES]))
print("axles", axles_x, "pivots", round(FRONT_PIVOT, 4), round(REAR_PIVOT, 4), "tread radius", round(TREAD_R, 4), "flange", round(FLANGE_R, 4))
print("running gear centred at y", round(GEAR_Y, 4), "body at", round(BODY_Y, 4), "treads", round(np.mean(left) - np.mean(right), 4), "apart, squeezed by", round(SQUEEZE, 4))

# Where the source's knuckles' noses are, before its couplers come off.
nose_front = bounds(bpy.data.objects[COUPLER_HEADS[0]])[1].x
nose_rear = bounds(bpy.data.objects[COUPLER_HEADS[1]])[0].x
for name in COUPLER_PARTS:
    obj = bpy.data.objects[name]
    meshes.remove(obj)
    bpy.data.objects.remove(obj)


WHEELS_Y = (min(bounds(o)[0].y for o in rings) - 0.01, max(bounds(o)[1].y for o in rings) + 0.01)  # outer face to outer face


def axle_of(obj):
    """Which axle (an index into axles_x) the object's centred on, or None: its middle on the axle's line, no longer along
    the car than a wheel, and between the wheels' outer faces (the bearing adapters, outside them, are the truck's)."""
    lo, hi = bounds(obj)
    c = (lo + hi) / 2
    if hi.x - lo.x > 2 * FLANGE_R + 0.02 or abs(c.z - AXLE_Z) > 0.03 or lo.y < WHEELS_Y[0] or hi.y > WHEELS_Y[1]:
        return None
    k = min(range(4), key=lambda i: abs(axles_x[i] - c.x))
    return k if abs(axles_x[k] - c.x) < 0.03 else None


TRUCK_REACH = 1.45  # along the car from the centre pin
TRUCK_TOP = RAIL + 1.30  # the top of the bolster; the body's lowest is higher


def truck_of(obj):
    """0 for the front truck, 1 for the rear, None if it's not wholly on one."""
    lo, hi = bounds(obj)
    if hi.z > TRUCK_TOP:
        return None
    for i, pivot in enumerate((FRONT_PIVOT, REAR_PIVOT)):
        if lo.x > pivot - TRUCK_REACH and hi.x < pivot + TRUCK_REACH:
            return i
    return None


groups = {"body": [], "truck0": [], "truck1": [], **{f"axle{k}": [] for k in range(4)}}
for o in meshes:
    k = axle_of(o)
    if k is not None:
        groups[f"axle{k}"].append(o)
        continue
    t = truck_of(o)
    groups["body" if t is None else f"truck{t}"].append(o)
for k, v in groups.items():
    print("group", k, len(v), "objects", sum(tris(o) for o in v), "triangles", sorted(o.name for o in v)[:12] if k != "body" else "")
for k in range(4):
    if len(groups[f"axle{k}"]) != 5:
        raise RuntimeError(f"axle {k} has {sorted(o.name for o in groups[f'axle{k}'])}, not two wheels, two treads and the axle")

game_materials()
lighten(meshes)

# The body: everything else in one object, on the railhead midway between the trucks.
GAME = to_game(MIDDLE, RAIL, BODY_Y)
for o in groups["body"]:
    place(o, GAME)
body = join(groups["body"], "Body")

# Each truck on its own, its origin at its centre pin on the railhead; its wheelsets with their origins at the axles.
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
front_face = -(nose_front - MIDDLE)  # game Y: the A end's at -Y
rear_face = -(nose_rear - MIDDLE)
COUPLER_FRONT, COUPLER_REAR = front_face + REACH, rear_face - REACH
OVER_COUPLERS = rear_face - front_face
TRUCK_Y = (FRONT_PIVOT - REAR_PIVOT) / 2

budget = {}
budget["body"] = write(os.path.join(PARTS, f"body_{LIVERY}.glb"), [body])
budget["truck_front"] = write(os.path.join(PARTS, "truck_front.glb"), [trucks[0]])
budget["truck_rear"] = write(os.path.join(PARTS, "truck_rear.glb"), [trucks[1]])
budget["wheelset"] = write(os.path.join(PARTS, "wheelset.glb"), [wheelsets[0][0]])
whole = budget["body"] + budget["truck_front"] + budget["truck_rear"] + sum(tris(w) for s in wheelsets for w in s)
print("whole car triangles, less couplers", whole)


def on_truck(axles, pivot):
    return [[0, round(-(a - pivot), 4), round(TREAD_R, 4)] for a in axles]


COUPLER_MODEL = "Couplers/coupler.glb"
assembly = {
    "source": SOURCE_NAME,
    "scale": round(SCALE, 4),
    "lengthOverCouplers": round(OVER_COUPLERS, 3),
    "truckCentres": round(FRONT_PIVOT - REAR_PIVOT, 3),
    "wheelDiameter": round(2 * TREAD_R, 4),
    "lods": ["LOD0", "LOD1", "LOD2"],
    "trianglesWholeCar": {"LOD0": whole},
    "models": f"The game loads LOD0/{MODEL}.glb (LOD1 and LOD2 beside it, from Tools/Blender/lods.py). The parts below are Source/Parts/<model>.glb, put together into it, except the couplers: Meshes/Couplers/coupler.glb, the same on every engine and car",
    "bodies": [f"body_{LIVERY}"],
    "trucks": [
        {"model": "truck_front", "position": [0, -round(TRUCK_Y, 4), 0], "yaw": 0, "wheelsets": on_truck(front_axles, FRONT_PIVOT)},
        {"model": "truck_rear", "position": [0, round(TRUCK_Y, 4), 0], "yaw": 0, "wheelsets": on_truck(rear_axles, REAR_PIVOT)},
    ],
    "wheelsets": {"model": "wheelset", "onTruck": "each truck's own, in its entry above", "radius": round(TREAD_R, 4)},
    "couplers": [{"model": COUPLER_MODEL, "position": [0, round(COUPLER_FRONT, 4), COUPLER_Z], "yaw": 0},
                 {"model": COUPLER_MODEL, "position": [0, round(COUPLER_REAR, 4), COUPLER_Z], "yaw": 180}],
    "axes": "Blender Z-up metres, front (the A end) towards -Y; in the files, glTF Y-up: (x, y, z) here is (x, z, -y) there",
}
with open(os.path.join(OUT, "assembly.json"), "w") as f:
    json.dump(assembly, f, indent=2)
print("assembly", {k: assembly[k] for k in ("scale", "lengthOverCouplers", "truckCentres", "wheelDiameter", "trucks", "couplers")})


def combined():
    """The whole car, which the game loads: every part on an empty named for it, so a truck turns with its wheels."""
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o)
    for block in (bpy.data.meshes, bpy.data.materials, bpy.data.images):
        for item in list(block):
            if item.users == 0:
                block.remove(item)

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

    root = holder("Autorack")
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
    bpy.ops.export_scene.gltf(filepath=os.path.join(folder, f"{MODEL}.glb"), export_format="GLB", use_selection=True, export_yup=True,
                              export_materials="EXPORT", export_normals=True, export_texcoords=True, export_image_format="JPEG", export_jpeg_quality=90,
                              export_animations=False)
    bpy.ops.file.pack_all()
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(OUT, "Source", f"{MODEL}.blend"), check_existing=False)
    print("wrote", folder, len(bpy.data.objects), "objects")


combined()
