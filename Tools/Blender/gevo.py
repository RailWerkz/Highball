"""Turns the GE GEVO ES44AC (CSX) source model into Highball's parts, laid out as the ES44AC's are.

    blender -b --factory-startup --python Tools/Blender/gevo.py -- <Content folder>

Run Tools/Blender/gevo_textures.py first: it turns the source's Gevo.blend into Source/gevo_es44ac.blend (and .glb) with its
textures on; this reads the .blend, as a glTF's vertices come split at every seam and its loose pieces with them. Under Models/Locomotives/ES44AC/CSX this writes assembly.json (where each part goes), one folder per level of
detail, of which only LOD0 is built (the source is about 36,000 triangles), and
    LOD0/body_<livery>.glb   origin on the railhead, midway between the trucks; the headlights, ditch lights and horn are
                             nodes of their own in it, and the bell an empty node (the source has no bell), all listed with
                             where they are and face in assembly.json
    LOD0/truck.glb           origin on the railhead at the truck's pivot (its middle axle), turning about Z; both trucks at yaw 0
    LOD0/wheelset.glb        origin at the axle's centre, rolling about X
    Assembled/gevo_<livery>.glb and .blend, the whole engine for looking at; the game loads the separate parts.
The source has no couplers: the engine takes the shared one from Models/Couplers (written by Tools/Blender/sd40.py), set on
the pilots at its standard height.

Blender is Z-up, metres; the glTF exporter turns that into the game's Y-up. The cab faces -Y. The source runs along X with the
cab towards -X, so it's turned a quarter about Z. Its three materials (exterior, cab, control stand) are kept as they are.
The source's trucks, wheels and all are one mesh, Bogies; it's taken apart by its loose pieces.
"""
import json
import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
CONTENT = os.path.abspath(args[0] if args else "Content")
OUT = os.path.join(CONTENT, "Models", "Locomotives", "ES44AC", "CSX")  # ES44AC/Black is the same engine, another model: es44ac.py
COUPLERS = os.path.join(CONTENT, "Models", "Couplers")
SOURCE = os.path.join(OUT, "Source", "gevo_es44ac.blend")
LIVERIES = {"csx": "CSX, as the source has it"}
SOURCE_NAME = "GE GEVO ES44AC (CSX), author and licence to be filled in: see CREDITS.txt"

# Measured off the source's meshes, in its own axes (X along the engine, cab at -X; Z up; centred on Y).
AXLE_Z = 0.6015
TREAD_R = 0.539  # the wheel's radius where it meets the rail, outboard of the flange (the flange reaches 0.568)
RAIL = AXLE_Z - TREAD_R
PILOT_FRONT = -10.065  # the end plates of the pilot decks
PILOT_REAR = 11.703
COUPLER_OUT = .48  # how far the coupler's pulling face stands out from the pilot's end plate, as on the ES44AC

# Lamps and horn, cut out of the body into parts of their own so the game can find them by name: a box in the source's
# axes, what it is, the way it faces in the game's axes (cab towards -Y, left facing forward +X: the source's +Y is the
# game's -X), and how it's cut: "parts" takes the loose pieces lying wholly inside the box (so a handrail running through
# it stays), "faces" the faces wholly inside (for the rear headlights, both lamps one piece). The object's origin is at
# its middle and its local -Y points the way it faces (the glTF node's local +Z), drawn with its axes. The source has
# no ditch lights at the rear, and no bell at all: BELL puts an empty there for its sound. The exhaust's position is the
# middle of its mouth, at the top, where the smoke comes out.
FIXTURES = {
    "Headlight_Front_Upper": ((-9.21, -0.11, 3.435), (-9.08, 0.115, 3.645), "lamp", (0, -1, 0), "parts"),
    "Headlight_Front_Lower": ((-9.21, -0.11, 3.22), (-9.08, 0.115, 3.435), "lamp", (0, -1, 0), "parts"),
    "DitchLight_Front_Left": ((-10.0, -1.04, 1.81), (-9.70, -0.71, 2.26), "lamp", (0, -1, 0), "parts"),
    "DitchLight_Front_Right": ((-10.0, 0.71, 1.81), (-9.70, 1.04, 2.26), "lamp", (0, -1, 0), "parts"),
    "Headlight_Rear_Upper": ((10.80, -0.12, 4.1765), (10.92, 0.12, 4.40), "lamp", (0, 1, 0), "faces"),
    "Headlight_Rear_Lower": ((10.80, -0.12, 3.95), (10.92, 0.12, 4.1765), "lamp", (0, 1, 0), "faces"),
    "Horn": ((1.59, -0.25, 4.53), (2.14, 0.28, 4.90), "horn", (0, -1, 0), "parts"),  # four trumpets on the long hood
    # The exhaust stack just behind the horn, a rounded oblong and its base plate, mouth up; its opening's size is listed too.
    "Exhaust": ((3.46, -0.51, 4.61), (3.99, 0.58, 4.84), "exhaust", (0, 0, 1), "parts"),
}
# Under the left walkway (+X) behind the front truck, mouth down, where the ES44AC's is; in the game's axes.
BELL = ((1.105, -3.40, 1.155), (0, 0, -1))


def facing(direction):
    """A rotation whose -Y is the direction given, kept upright (its Z up) unless it points straight up or down."""
    back = -Vector(direction).normalized()
    up = Vector((0, 0, 1)) if abs(back.z) < .9 else Vector((0, 1, 0))
    right = back.cross(up).normalized()
    up = right.cross(back)
    return Matrix((right, back, up)).transposed().to_4x4()


def islands(bm):
    """The faces of each loose piece of a bmesh."""
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


def cut_fixtures(body, to):
    """Moves each fixture's faces out of the body's meshes into an object of its own; returns the body left, the fixtures
    and what assembly.json says of them. The boxes are in the source's axes; `to` turns them into the game's."""
    fixtures, found = [], {}
    back = to.inverted()
    for name, (lo, hi, kind, direction, how) in FIXTURES.items():
        inside = lambda co: all(lo[i] <= co[i] <= hi[i] for i in range(3))
        pieces = []
        for obj in body:
            bm = bmesh.new()
            bm.from_mesh(obj.data)
            at = {v: back @ v.co for v in bm.verts}  # each vertex in the source's axes
            if how == "parts":
                mine = [f for comp in islands(bm) if all(inside(at[v]) for f in comp for v in f.verts) for f in comp]
            else:
                mine = [f for f in bm.faces if all(inside(at[v]) for v in f.verts)]
            if mine:
                piece = bpy.data.objects.new(name, obj.data.copy())
                bpy.context.collection.objects.link(piece)
                pm = bmesh.new()
                pm.from_mesh(piece.data)
                pm.faces.ensure_lookup_table()
                keep = {f.index for f in mine}
                bmesh.ops.delete(pm, geom=[f for f in pm.faces if f.index not in keep], context="FACES")
                pm.to_mesh(piece.data)
                pm.free()
                bmesh.ops.delete(bm, geom=mine, context="FACES")
                bm.to_mesh(obj.data)
                pieces.append(piece)
            bm.free()
        if not pieces:
            raise RuntimeError(f"no faces for {name}: has the source changed?")
        bpy.ops.object.select_all(action="DESELECT")
        for p in pieces:
            p.select_set(True)
        bpy.context.view_layer.objects.active = pieces[0]
        if len(pieces) > 1:
            bpy.ops.object.join()
        obj = bpy.context.view_layer.objects.active
        obj.name = obj.data.name = name
        vs = [v.co for v in obj.data.vertices]
        middle = Vector([(min(v[i] for v in vs) + max(v[i] for v in vs)) / 2 for i in range(3)])
        extent = [round(max(v[i] for v in vs) - min(v[i] for v in vs), 3) for i in range(3)]  # before vs is moved below
        if kind == "exhaust":
            middle.z = max(v[2] for v in vs)  # its origin at its mouth
        place = Matrix.Translation(middle) @ facing(direction)
        obj.data.transform(place.inverted())
        obj.matrix_world = place
        obj.show_axis = True
        fixtures.append(obj)
        found[name] = {"kind": kind, "position": [round(c, 4) for c in middle], "facing": list(direction), "triangles": sum(len(p.vertices) - 2 for p in obj.data.polygons)}
        if kind == "horn":
            found[name]["trumpets"] = trumpets(obj)
        if kind == "exhaust":
            # Across and along the engine (x, y in the game's axes).
            found[name]["opening"] = extent[:2]
        print("fixture", name, found[name])

    position, direction = BELL
    bell = bpy.data.objects.new("Bell", None)
    bell.empty_display_type, bell.empty_display_size = "ARROWS", .2
    bpy.context.collection.objects.link(bell)
    bell.matrix_world = Matrix.Translation(position) @ facing(direction)
    fixtures.append(bell)
    found["Bell"] = {"kind": "bell", "position": list(position), "facing": list(direction), "triangles": 0,
                     "note": "the source has no bell: an empty node, placed where the ES44AC's is, for its sound"}
    return [o for o in body if o.data.polygons], fixtures, found


def trumpets(horn):
    """Each trumpet of the horn, in the game's axes: where its mouth is and which way along the engine it opens."""
    bm = bmesh.new()
    bm.from_mesh(horn.data)
    bm.transform(horn.matrix_world)
    out = []
    for comp in islands(bm):
        vs = list({v for g in comp for v in g.verts})
        if len(comp) < 40:
            continue  # not a trumpet: its stand or the bar along them
        ys = [v.co.y for v in vs]
        y0, y1 = min(ys), max(ys)
        cx = sum(v.co.x for v in vs) / len(vs)
        cz = sum(v.co.z for v in vs) / len(vs)
        spread = lambda end: max(math.hypot(v.co.x - cx, v.co.z - cz) for v in vs if abs(v.co.y - end) < .03)
        opens = -1 if spread(y0) > spread(y1) else 1
        mouth = y0 if opens < 0 else y1
        out.append({"mouth": [round(cx, 4), round(mouth, 4), round(cz, 4)], "facing": [0, opens, 0]})
    bm.free()
    return sorted(out, key=lambda t: (t["mouth"][0], t["mouth"][2]))


LODS = ("LOD0",)
LATER_LODS = ("LOD1", "LOD2")


def load_source():
    """The source's meshes, each in world space with its own copy of the mesh; Bogies split into its loose pieces,
    named Bogies_<n>. Returns the body's meshes by name and the bogie pieces."""
    bpy.ops.wm.open_mainfile(filepath=SOURCE, load_ui=False)
    # Its textures by file name under Source/Texture, wherever the .blend thought they were (it keeps whole paths, which a
    # move of the folder breaks).
    textures = {f: os.path.join(root, f) for root, _, files in os.walk(os.path.join(OUT, "Source", "Texture")) for f in files}
    for image in bpy.data.images:
        found = textures.get(os.path.basename(image.filepath.replace("\\", "/")))
        if found:
            image.filepath = found
            image.reload()
    body, bogie = {}, []
    for obj in [o for o in bpy.data.objects if o.type == "MESH"]:
        mesh = obj.data.copy()
        mesh.transform(obj.matrix_world)
        if obj.name.startswith("Bogies"):
            bm = bmesh.new()
            bm.from_mesh(mesh)
            for comp in islands(bm):
                piece = mesh.copy()
                pm = bmesh.new()
                pm.from_mesh(piece)
                pm.faces.ensure_lookup_table()
                keep = {f.index for f in comp}
                bmesh.ops.delete(pm, geom=[f for f in pm.faces if f.index not in keep], context="FACES")
                pm.to_mesh(piece)
                pm.free()
                piece.name = f"Bogies_{len(bogie)}"
                bogie.append(piece)
            bm.free()
        else:
            body[obj.name] = mesh
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    return body, bogie


# The game reads a material's colour, normal and roughness, not the source's occlusion and specular maps, and the source's
# full maps would make each part some 40 MB. So the game's parts have the occlusion laid into the colour (at this strength,
# as the game draws its own), every map at most this size, roughness as one factor per material (the source has no map of
# it), and the images written as JPEG.
OCCLUSION = .6
MAX_SIZE = 2048
ROUGHNESS = {"Gevo": .62, "Gevo CAB": .8, "Control": .7}  # exterior and running gear, cab interior, control stand


def game_materials():
    import numpy as np

    def read(image):
        px = np.empty(len(image.pixels), np.float32)
        image.pixels.foreach_get(px)
        return px.reshape(-1, 4)

    def sized(image, name, size=None):
        """The image's pixels scaled down to at most MAX_SIZE (or to size), as a new image held in the file: a copy of a
        file's image would be read from the file again when it's packed or exported, at full size."""
        copy = image.copy()
        w, h = size or ((MAX_SIZE, MAX_SIZE * copy.size[1] // copy.size[0]) if copy.size[0] > MAX_SIZE else tuple(copy.size))
        copy.scale(w, h)
        px = read(copy)
        bpy.data.images.remove(copy)
        if image.is_float and image.colorspace_settings.name == "sRGB":
            # The source's PNGs are 16-bit, which Blender holds as linear floats; an 8-bit image holds its sRGB values.
            rgb = np.clip(px[:, :3], 0, 1)
            px[:, :3] = np.where(rgb <= .0031308, rgb * 12.92, 1.055 * rgb ** (1 / 2.4) - .055)
        made = bpy.data.images.new(name, w, h, alpha=True)
        made.colorspace_settings.name = image.colorspace_settings.name
        made.pixels.foreach_set(px.ravel())
        made.pack()
        return made

    for mat in bpy.data.materials:
        if not mat.use_nodes or mat.name not in ROUGHNESS:
            continue
        nodes, links = mat.node_tree.nodes, mat.node_tree.links
        maps = {n.image.name.rsplit("_", 1)[1].split(".")[0]: n.image for n in nodes if n.type == "TEX_IMAGE" and n.image}
        prefix = maps["Diffuse"].name.rsplit("_", 1)[0]
        colour = sized(maps["Diffuse"], f"{prefix}_Colour")
        ao = sized(maps["Ao"], f"{prefix}_Ao_Game", tuple(colour.size))
        px, shade = read(colour), read(ao)
        px[:, :3] *= 1 - OCCLUSION * (1 - shade[:, :1])
        colour.pixels.foreach_set(px.ravel())
        colour.pack()
        bpy.data.images.remove(ao)
        normal = sized(maps["Normal"], f"{prefix}_Normal_Game")

        nodes.clear()
        out = nodes.new("ShaderNodeOutputMaterial")
        bsdf = nodes.new("ShaderNodeBsdfPrincipled")
        links.new(bsdf.outputs[0], out.inputs[0])
        bsdf.inputs["Metallic"].default_value = 0.0
        bsdf.inputs["Roughness"].default_value = ROUGHNESS[mat.name]
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = colour
        links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = normal
        bump = nodes.new("ShaderNodeNormalMap")
        links.new(tex.outputs["Color"], bump.inputs["Color"])
        links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
        mat.name = f"GEVO_{mat.name.replace(' ', '_')}"
        print("material", mat.name, tuple(colour.size), tuple(normal.size))


def bounds(mesh):
    vs = [v.co for v in mesh.vertices]
    return Vector([min(v[i] for v in vs) for i in range(3)]), Vector([max(v[i] for v in vs) for i in range(3)])


def is_wheel(mesh):
    """A wheel: as long as it is tall, about the tread's diameter, thin across, and centred on the axle's height."""
    lo, hi = bounds(mesh)
    size = hi - lo
    return abs(size.x - size.z) < .02 and abs(size.z - 2 * .568) < .03 and size.y < .2 and abs((lo.z + hi.z) / 2 - AXLE_Z) < .01


def to_game(origin_x, origin_z):
    """The source's axes to the game's: a quarter turn about Z, so +X (to the rear) becomes +Y, then moved to an origin."""
    turn = Matrix(((0, -1, 0, 0), (1, 0, 0, 0), (0, 0, 1, 0), (0, 0, 0, 1)))
    return turn @ Matrix.Translation((-origin_x, 0, -origin_z))


def write(path, objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=False, export_yup=True, export_materials="EXPORT",
                              export_normals=True, export_texcoords=True, export_image_format="JPEG", export_jpeg_quality=90, export_animations=False)
    tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in objects if o.type == "MESH")
    print("wrote", path, tris, "triangles", len(objects), "parts", os.path.getsize(path) // 1024, "KB")
    return tris


def part(name, mesh, matrix):
    mesh.transform(matrix)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    return obj


def middle_x(mesh):
    lo, hi = bounds(mesh)
    return (lo.x + hi.x) / 2


_, bogie = load_source()
wheel_axles = sorted({round(middle_x(m), 3) for m in bogie if is_wheel(m)})
if len(wheel_axles) != 6:
    raise RuntimeError(f"found axles at {wheel_axles}, not six: has the source changed?")
front_axles, rear_axles = wheel_axles[:3], wheel_axles[3:]
FRONT_PIVOT, REAR_PIVOT = front_axles[1], rear_axles[1]
MIDDLE = (FRONT_PIVOT + REAR_PIVOT) / 2
TRUCK_Y = (REAR_PIVOT - FRONT_PIVOT) / 2
AXLES = tuple(round(x - FRONT_PIVOT, 4) for x in front_axles)
print("axles", wheel_axles, "truck centres", round(FRONT_PIVOT, 4), round(REAR_PIVOT, 4), "axles on the truck", AXLES)

with open(os.path.join(COUPLERS, "coupler.json")) as f:
    coupler = json.load(f)
COUPLER_Z = coupler["height"]
REACH = coupler["reach"]
front_face = PILOT_FRONT - MIDDLE - COUPLER_OUT
rear_face = PILOT_REAR - MIDDLE + COUPLER_OUT
COUPLER_FRONT, COUPLER_REAR = front_face + REACH, rear_face - REACH
OVER_COUPLERS = rear_face - front_face

budget = {}
for folder in LODS:
    body_meshes, bogie = load_source()
    game_materials()
    body =[part(n, m, to_game(MIDDLE, RAIL)) for n, m in body_meshes.items()]
    body, fixtures, FOUND = cut_fixtures(body, to_game(MIDDLE, RAIL))
    body += fixtures
    # The front truck: every bogie piece in the front half but its wheels. The rear truck's the same turned round, so the
    # one model does for both, as on the ES44AC.
    front = [m for m in bogie if middle_x(m) < MIDDLE]
    lead = [m for m in front if is_wheel(m) and abs(middle_x(m) - front_axles[0]) < .01]
    truck_meshes = [m for m in front if not is_wheel(m)]
    if len(lead) != 2:
        raise RuntimeError(f"{len(lead)} wheels on the leading axle: has the source changed?")
    bpy.ops.object.select_all(action="DESELECT")
    truck = [part(m.name, m, to_game(FRONT_PIVOT, RAIL)) for m in truck_meshes]
    for t in truck:
        t.select_set(True)
    bpy.context.view_layer.objects.active = truck[0]
    bpy.ops.object.join()
    truck = [bpy.context.view_layer.objects.active]
    truck[0].name = truck[0].data.name = "Truck"
    wheels = [part(f"Wheel_{side}", m, to_game(front_axles[0], AXLE_Z)) for side, m in zip(("Left", "Right"), sorted(lead, key=lambda m: bounds(m)[0].y))]  # the source's -Y is the game's +X, the left
    for key in LIVERIES:
        budget[f"body{folder}"] = write(os.path.join(OUT, folder, f"body_{key}.glb"), body)
    budget[f"truck{folder}"] = write(os.path.join(OUT, folder, "truck.glb"), truck)
    budget[f"wheelset{folder}"] = write(os.path.join(OUT, folder, "wheelset.glb"), wheels)
for folder in LATER_LODS:
    os.makedirs(os.path.join(OUT, folder), exist_ok=True)
    open(os.path.join(OUT, folder, ".gitkeep"), "a").close()

over = {f: budget[f"body{f}"] + 2 * budget[f"truck{f}"] + 6 * budget[f"wheelset{f}"] for f in LODS}
print("whole engine triangles, less couplers", over)

COUPLER_MODEL = "Couplers/coupler.glb"
assembly = {
    "source": SOURCE_NAME,
    "lengthOverCouplers": round(OVER_COUPLERS, 3),
    "lods": list(LODS),
    "trianglesWholeEngine": over,
    "models": "Each model below is <lod>/<model>.glb, except the couplers: Models/Couplers/coupler.glb, the same on every engine and car",
    "bodies": [f"body_{key}" for key in LIVERIES],
    "truckByBody": {},
    "trucks": [{"model": "truck", "position": [0, -round(TRUCK_Y, 4), 0], "yaw": 0}, {"model": "truck", "position": [0, round(TRUCK_Y, 4), 0], "yaw": 0}],
    "wheelsets": {"model": "wheelset", "onTruck": [[0, a, TREAD_R] for a in AXLES], "radius": TREAD_R},
    "couplers": [{"model": COUPLER_MODEL, "position": [0, round(COUPLER_FRONT, 4), COUPLER_Z], "yaw": 0},
                 {"model": COUPLER_MODEL, "position": [0, round(COUPLER_REAR, 4), COUPLER_Z], "yaw": 180}],
    "fixtures": FOUND,
    "fixturesNote": "Parts of the body model by these node names. position: the part's origin; facing: the way its light, horn or bell points, the node's local -Y in Blender and +Z in the glTF",
    "axes": "Blender Z-up metres, front towards -Y; in the files, glTF Y-up: (x, y, z) here is (x, z, -y) there",
}
with open(os.path.join(OUT, "assembly.json"), "w") as f:
    json.dump(assembly, f, indent=2)


def combined(key):
    """One whole engine for looking at: every part on an empty named for it, so a truck turns with its wheels on it."""
    bpy.ops.wm.read_factory_settings(use_empty=True)

    def holder(name, at=(0, 0, 0), yaw=0.0, parent=None):
        empty = bpy.data.objects.new(name, None)
        empty.empty_display_size = .3
        bpy.context.collection.objects.link(empty)
        empty.parent = parent
        empty.location = at
        empty.rotation_euler = (0, 0, yaw)
        return empty

    def load(file, parent, prefix):
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=os.path.join(OUT, file))
        for obj in set(bpy.data.objects) - before:
            if obj.parent is None:
                obj.parent = parent
                obj.matrix_parent_inverse = Matrix.Identity(4)
            obj.name = f"{prefix}_{obj.name}"

    root = holder("GEVO")
    load(f"LOD0/body_{key}.glb", holder("Body", parent=root), "Body")
    for label, y in (("Front", -TRUCK_Y), ("Rear", TRUCK_Y)):
        truck_at = holder(f"Truck_{label}", (0, y, 0), 0.0, root)
        load("LOD0/truck.glb", truck_at, f"Truck_{label}")
        for k, a in enumerate(AXLES, 1):
            load("LOD0/wheelset.glb", holder(f"Wheelset_{label}_{k}", (0, a, TREAD_R), 0.0, truck_at), f"Wheelset_{label}_{k}")
    for label, y, yaw in (("Front", COUPLER_FRONT, 0.0), ("Rear", COUPLER_REAR, math.pi)):
        load(os.path.join(COUPLERS, "coupler.glb"), holder(f"Coupler_{label}", (0, y, COUPLER_Z), yaw, root), f"Coupler_{label}")
    bpy.context.view_layer.update()
    folder = os.path.join(OUT, "Assembled")
    os.makedirs(folder, exist_ok=True)
    for obj in bpy.data.objects:
        obj.select_set(True)
    bpy.ops.export_scene.gltf(filepath=os.path.join(folder, f"gevo_{key}.glb"), export_format="GLB", use_selection=True, export_yup=True,
                              export_materials="EXPORT", export_normals=True, export_texcoords=True, export_animations=False)
    bpy.ops.file.pack_all()
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(folder, f"gevo_{key}.blend"), check_existing=False)
    print("wrote", folder, key, len(bpy.data.objects), "objects")


for key in LIVERIES:
    combined(key)
