"""Turns the GE ES44AC source model into Highball's parts, laid out as the SD40's are.

    blender -b --factory-startup --python Tools/Blender/es44ac.py -- <Content folder>

The source is "General Electric ES44AC Black" by OUTPISTON, CC BY-NC-SA 4.0:
https://sketchfab.com/3d-models/general-electric-es44ac-black-ab967059a9504a209c4993816ccb2c9b
Its file stays in Models/Locomotives/ES44AC/Black/Source. Under ES44AC/Black this writes CREDITS.txt, assembly.json (where each part goes), one
folder per level of detail, of which only LOD0 is built (the source is already light, about 23,000 triangles), and
    LOD0/body_<livery>.glb   origin on the railhead, midway between the trucks; the headlights, ditch lights, horn and bell
                             are nodes of their own in it (see FIXTURES), listed with where they are and face in assembly.json
    LOD0/truck.glb           origin on the railhead at the truck's pivot (its middle axle), turning about Z; both trucks at yaw 0
    LOD0/wheelset.glb        origin at the axle's centre, rolling about X
    Assembled/es44ac_<livery>.glb and .blend, the whole engine for looking at; the game loads the separate parts.
The source has no couplers: the engine takes the shared one from Models/Couplers (written by Tools/Blender/sd40.py), set on
the pilots at its standard height.

Blender is Z-up, metres; the glTF exporter turns that into the game's Y-up. The cab faces -Y. The source runs along X with the
cab towards -X, so it's turned a quarter about Z. Its materials and textures are kept as they are, one material per texture.
"""
import json
import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
CONTENT = os.path.abspath(args[0] if args else "Content")
OUT = os.path.join(CONTENT, "Models", "Locomotives", "ES44AC", "Black")  # ES44AC/CSX is the same engine, another model: gevo.py
COUPLERS = os.path.join(CONTENT, "Models", "Couplers")
SOURCE = os.path.join(OUT, "Source", "general_electric_es44ac_black.glb")
LIVERIES = {"black": "Black, as the source has it"}

# Measured off the source's meshes, in its own axes after import (X along the engine, cab at -X; Z up).
AXLE_Z = 0.6017
TREAD_R = 0.542  # the wheel's radius where it meets the rail, outboard of the flange
RAIL = AXLE_Z - TREAD_R
CENTRE_Y = -0.1395  # the source's centreline is off its origin
PILOT_FRONT = -9.538  # the end plates of the pilots
PILOT_REAR = 11.258
COUPLER_OUT = .48  # how far the coupler's pulling face stands out from the pilot's end plate
# Parts by the source's node names: one truck and one axle's wheels to make the shared models from.
TRUCK_NODES = {"front": "DrawCall_0047_26", "rear": "DrawCall_163_0"}
WHEEL_NODES = ("DrawCall_0048_3", "DrawCall_0049_30")  # the leading axle: the wheels and their faces
# Small stickers on the journal boxes, with the trucks. Every other node is the body.
TRUCK_DETAIL = ("DrawCall_0056_18", "DrawCall_0050_19", "DrawCall_0053_23", "DrawCall_154_27", "DrawCall_156_33", "DrawCall_159_2")

# Lamps, horn and bell, cut out of the body into parts of their own so the game can find them by name. Each is the body's faces
# lying wholly inside a box, in the game's axes (after to_game: metres, rail at zero, cab towards -Y), with the way it faces.
# Its object's origin is at the box's middle and its local -Y points that way (the glTF node's local +Z), drawn with its axes.
# The lamps are their lens and rim and bolts (the ditch lights with their housings); the plate behind both headlights stays.
FIXTURES = {
    "Headlight_Front_Upper": ((-0.12, -9.83, 3.375), (0.12, -9.69, 3.60), "lamp", (0, -1, 0)),
    "Headlight_Front_Lower": ((-0.12, -9.83, 3.15), (0.12, -9.69, 3.375), "lamp", (0, -1, 0)),
    "DitchLight_Front_Left": ((0.69, -10.59, 1.74), (1.04, -10.29, 2.15), "lamp", (0, -1, 0)),  # left facing forward: +X
    "DitchLight_Front_Right": ((-1.04, -10.59, 1.74), (-0.69, -10.29, 2.15), "lamp", (0, -1, 0)),
    "Headlight_Rear_Upper": ((-0.13, 9.98, 2.812), (0.10, 10.10, 3.03), "lamp", (0, 1, 0)),
    "Headlight_Rear_Lower": ((-0.13, 9.98, 2.59), (0.10, 10.10, 2.812), "lamp", (0, 1, 0)),
    "DitchLight_Rear_Left": ((-1.03, 10.70, 1.80), (-0.70, 10.99, 2.21), "lamp", (0, 1, 0)),  # left facing back: -X
    "DitchLight_Rear_Right": ((0.70, 10.70, 1.80), (1.03, 10.99, 2.21), "lamp", (0, 1, 0)),
    "Horn": ((-0.30, 0.82, 4.79), (0.24, 1.65, 4.98), "horn", (0, -1, 0)),  # three trumpets on the long hood
    "Bell": ((0.93, -3.60, 0.95), (1.28, -3.20, 1.36), "bell", (0, 0, -1)),  # under the left walkway (+X), mouth down
}


def facing(direction):
    """A rotation whose -Y is the direction given, kept upright (its Z up) unless it points straight up or down."""
    back = -Vector(direction).normalized()
    up = Vector((0, 0, 1)) if abs(back.z) < .9 else Vector((0, 1, 0))
    right = back.cross(up).normalized()
    up = right.cross(back)
    return Matrix((right, back, up)).transposed().to_4x4()


def cut_fixtures(body):
    """Moves each fixture's faces out of the body's meshes into an object of its own; returns the body left and the fixtures."""
    import bmesh
    fixtures, found = [], {}
    for name, (lo, hi, kind, direction) in FIXTURES.items():
        inside = lambda co: all(lo[i] <= co[i] <= hi[i] for i in range(3))
        pieces = []
        for obj in body:
            bm = bmesh.new()
            bm.from_mesh(obj.data)
            mine = [f for f in bm.faces if all(inside(v.co) for v in f.verts)]
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
        place = Matrix.Translation(middle) @ facing(direction)
        obj.data.transform(place.inverted())
        obj.matrix_world = place
        obj.show_axis = True
        fixtures.append(obj)
        found[name] = {"kind": kind, "position": [round(c, 4) for c in middle], "facing": list(direction), "triangles": sum(len(p.vertices) - 2 for p in obj.data.polygons)}
        if kind == "horn":
            found[name]["trumpets"] = trumpets(obj)
        print("fixture", name, found[name])
    return [o for o in body if o.data.polygons], fixtures, found


def trumpets(horn):
    """Each trumpet of the horn, in the game's axes: where its mouth is and which way along the engine it opens."""
    import bmesh
    bm = bmesh.new()
    bm.from_mesh(horn.data)
    bm.transform(horn.matrix_world)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=.002)
    out, seen = [], set()
    for f in bm.faces:
        if f in seen:
            continue
        stack, comp = [f], set()
        seen.add(f)
        while stack:
            g = stack.pop()
            comp.add(g)
            for e in g.edges:
                for h in e.link_faces:
                    if h not in seen:
                        seen.add(h)
                        stack.append(h)
        vs = list({v for g in comp for v in g.verts})
        if len(comp) < 40:
            continue  # not a trumpet: a stray bracket
        ys = [v.co.y for v in vs]
        y0, y1 = min(ys), max(ys)
        cx = sum(v.co.x for v in vs) / len(vs)
        cz = sum(v.co.z for v in vs) / len(vs)
        spread = lambda end: max(math.hypot(v.co.x - cx, v.co.z - cz) for v in vs if abs(v.co.y - end) < .03)
        opens = -1 if spread(y0) > spread(y1) else 1
        mouth = y0 if opens < 0 else y1
        out.append({"mouth": [round(cx, 4), round(mouth, 4), round(cz, 4)], "facing": [0, opens, 0]})
    bm.free()
    return sorted(out, key=lambda t: t["mouth"][0])


LODS = ("LOD0",)
LATER_LODS = ("LOD1", "LOD2")


def load_source():
    """The source's meshes by node name, each in world space with its own copy of the mesh."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=SOURCE)
    meshes = {}
    for obj in [o for o in bpy.data.objects if o.type == "MESH"]:
        name = obj.parent.name if obj.parent and obj.parent.name.startswith("DrawCall") else obj.name
        mesh = obj.data.copy()
        mesh.transform(obj.matrix_world)
        meshes[name] = mesh
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    return meshes


def bounds(mesh):
    vs = [v.co for v in mesh.vertices]
    return Vector([min(v[i] for v in vs) for i in range(3)]), Vector([max(v[i] for v in vs) for i in range(3)])


def to_game(origin_x, origin_z):
    """The source's axes to the game's: a quarter turn about Z, so +X (to the rear) becomes +Y, then moved to an origin."""
    turn = Matrix(((0, -1, 0, 0), (1, 0, 0, 0), (0, 0, 1, 0), (0, 0, 0, 1)))
    return turn @ Matrix.Translation((-origin_x, -CENTRE_Y, -origin_z))


# How rough each texture's surfaces are (glTF roughness: 0 a mirror, 1 matte), by the source's image names. The source has
# them glossy (0.345) to mirror-smooth (0), which turns the black paint to the sky's colour in a low sun; weathered paint and
# grimy running gear are far duller. Glass (no image) keeps its own.
ROUGHNESS = {
    "Image_4": .62,  # the body: hood and cab sides, roof
    "Image_5": .62,  # the body: long hood, radiators, ends
    "Image_0": .85,  # underframe, trucks, fuel tank and small fittings
    "Image_1": .75,  # walkway tread plate
    "Image_3": .8,   # cab interior
    "Image_2": .6,   # wheel faces
}


def shared_materials(objects):
    """One material per texture image, named for the engine and the texture, in place of the source's one per draw call,
    and each given its texture's roughness (the source gives one texture several, so which survived was luck)."""
    by_image = {}
    for obj in objects:
        for slot in obj.material_slots:
            mat = slot.material
            image = next((n.image for n in mat.node_tree.nodes if n.type == "TEX_IMAGE" and n.image), None) if mat and mat.use_nodes else None
            key = image.name if image else mat.name
            if key not in by_image:
                by_image[key] = mat
                mat.name = f"ES44AC_{len(by_image)}"
                bsdf = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None) if mat.use_nodes else None
                if bsdf and image and image.name.split(".")[0] in ROUGHNESS:
                    for link in list(bsdf.inputs["Roughness"].links):
                        mat.node_tree.links.remove(link)
                    bsdf.inputs["Roughness"].default_value = ROUGHNESS[image.name.split(".")[0]]
                    bsdf.inputs["Metallic"].default_value = 0.0
                elif image:
                    print("no roughness for", image.name)
            slot.material = by_image[key]


# The source paints the trucks and the fuel tank a pale, dusty cream; on the real engine they're black under road dust. They
# share their texture with the handrails' yellow and the steps' white, so rather than darken the texture they get copies of
# its material with the colour multiplied down (glTF's base colour factor): every truck face gets GRIME, and the body's faces
# on that texture low down between the trucks (the tank and air reservoirs, in the game's axes) get TANK_GRIME, deeper, as
# the tank's patch of the texture is paler still.
GRIME = (.19, .18, .17)
TANK_GRIME = (.08, .075, .07)
GRIME_IMAGE = "Image_0"
TANK = ((-1.6, -4.4, 0.0), (1.6, 4.4, 1.55))


def grimy_copy(source, name, factor):
    mat = source.copy()
    mat.name = name
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    tex = next(n for n in nodes if n.type == "TEX_IMAGE")
    mix = nodes.new("ShaderNodeMix")
    mix.data_type, mix.blend_type = "RGBA", "MULTIPLY"
    mix.inputs["Factor"].default_value = 1.0
    links.new(tex.outputs["Color"], mix.inputs["A"])
    mix.inputs["B"].default_value = (*factor, 1.0)
    links.new(mix.outputs["Result"], bsdf.inputs["Base Color"])
    return mat


def grime(body, truck):
    source = next((m for m in bpy.data.materials if m.use_nodes and any(n.type == "TEX_IMAGE" and n.image and n.image.name.split(".")[0] == GRIME_IMAGE
                                                                          for n in m.node_tree.nodes)), None)
    if source is None:
        raise RuntimeError(f"no material on {GRIME_IMAGE}: has the source changed?")
    trucks, tank = grimy_copy(source, "ES44AC_Grime", GRIME), grimy_copy(source, "ES44AC_Tank", TANK_GRIME)
    lo, hi = TANK
    faces = {trucks.name: 0, tank.name: 0}
    for obj in body + truck:
        slots = [i for i, s in enumerate(obj.material_slots) if s.material == source]
        if not slots:
            continue
        mat = trucks if obj in truck else tank
        obj.data.materials.append(mat)
        grimy = len(obj.data.materials) - 1
        to_world = obj.matrix_world  # the fixtures (bell and so on) are drawn in their own axes
        for p in obj.data.polygons:
            at = to_world @ p.center
            if p.material_index in slots and (obj in truck or all(lo[i] <= at[i] <= hi[i] for i in range(3))):
                p.material_index = grimy
                faces[mat.name] += 1
    print("grime on faces", faces)


def write(path, objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=False, export_yup=True, export_materials="EXPORT",
                              export_normals=True, export_texcoords=True, export_image_format="AUTO", export_animations=False)
    tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in objects)
    print("wrote", path, tris, "triangles", len(objects), "parts", os.path.getsize(path) // 1024, "KB")
    return tris


def part(name, mesh, matrix):
    mesh.transform(matrix)
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    return obj


def axle_x(mesh):
    lo, hi = bounds(mesh)
    return (lo.x + hi.x) / 2


meshes = load_source()
wheel_axles = sorted(axle_x(m) for n, m in meshes.items() if len(m.polygons) == 392)
front_axles, rear_axles = wheel_axles[:3], wheel_axles[3:]
FRONT_PIVOT, REAR_PIVOT = front_axles[1], rear_axles[1]
MIDDLE = (FRONT_PIVOT + REAR_PIVOT) / 2
TRUCK_Y = (REAR_PIVOT - FRONT_PIVOT) / 2
AXLES = tuple(round(x - FRONT_PIVOT, 4) for x in front_axles)
print("axles", [round(x, 4) for x in wheel_axles], "truck centres", round(FRONT_PIVOT, 4), round(REAR_PIVOT, 4), "axles on the truck", AXLES)

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
    meshes = load_source()
    body = [part(n, m, to_game(MIDDLE, RAIL)) for n, m in meshes.items()
            if n not in TRUCK_NODES.values() and n not in TRUCK_DETAIL and len(m.polygons) not in (392, 80)]
    body, fixtures, FOUND = cut_fixtures(body)
    body += fixtures
    near_front = [n for n in TRUCK_DETAIL if bounds(meshes[n])[0].x < 0]
    truck = [part(n, meshes[n], to_game(FRONT_PIVOT, RAIL)) for n in [TRUCK_NODES["front"]] + near_front]
    wheels = [part(n, meshes[n], to_game(front_axles[0], AXLE_Z)) for n in WHEEL_NODES]
    shared_materials(body + truck + wheels)
    grime(body, truck)
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
    "source": "General Electric ES44AC Black by OUTPISTON, CC BY-NC-SA 4.0",
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

with open(os.path.join(OUT, "CREDITS.txt"), "w") as f:
    f.write("""General Electric ES44AC Black by OUTPISTON
https://sketchfab.com/3d-models/general-electric-es44ac-black-ab967059a9504a209c4993816ccb2c9b
Licensed under Creative Commons Attribution-NonCommercial-ShareAlike 4.0: http://creativecommons.org/licenses/by-nc-sa/4.0/
Not for commercial use; the models made from it are under the same licence.

The source file is in Source/, unchanged.
The other models in these folders are made from it by Tools/Blender/es44ac.py. Changes: split into body, truck and wheelset;
turned to run along Y and moved so the rail is at zero; one material per texture; the headlights, ditch lights,
horn and bell cut out as parts of their own. Its couplers are the shared one in
Models/Couplers, from the SD40 (see the CREDITS.txt there).
""")


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
            obj.parent = parent
            obj.matrix_parent_inverse = Matrix.Identity(4)
            obj.name = f"{prefix}_{obj.name}"

    root = holder("ES44AC")
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
    bpy.ops.export_scene.gltf(filepath=os.path.join(folder, f"es44ac_{key}.glb"), export_format="GLB", use_selection=True, export_yup=True,
                              export_materials="EXPORT", export_normals=True, export_texcoords=True, export_animations=False)
    bpy.ops.file.pack_all()
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(folder, f"es44ac_{key}.blend"), check_existing=False)
    print("wrote", folder, key, len(bpy.data.objects), "objects")


for key in LIVERIES:
    combined(key)
