"""Turns OUTPISTON's locomotive models into Highball's parts, laid out as the SD40's and ES44AC's are.

    blender -b --factory-startup --python Tools/Blender/outpiston.py -- <Content folder> [--loco KEY ...] [--preview DIR] [--list]

Each source is a Sketchfab download by OUTPISTON, kept unchanged in Models/Locomotives/<folder>/Source (see SPECS for which,
and each folder's CREDITS.txt for its licence). For each one this writes, under its folder, CREDITS.txt, assembly.json (where
each part goes), one folder per level of detail, of which only LOD0 is built (the sources are already light), and
    LOD0/body_<livery>.glb   origin on the railhead, midway between the trucks
    LOD0/truck.glb           origin on the railhead at the truck's pivot, turning about Z
    LOD0/wheelset.glb        origin at the axle's centre, rolling about X
    Assembled/<key>_<livery>.glb and .blend, the whole engine for looking at; the game loads the separate parts.
The source's own couplers are taken off and the shared one from Models/Couplers (written by Tools/Blender/sd40.py) goes on
in their place, its pulling face where the source's was, at the standard height.

The sources aren't laid out alike (some have a node per part, some are merged into a few meshes), so the parts are found by
shape rather than by name: everything is joined and split into loose pieces, then
    wheels      pieces as round as they are tall, a wheel's size, outboard of the centreline; they give the axles and the rail
    wheelset    pieces centred on the leading axle that fit inside its wheels: wheels, axle, gear, bearing caps
    truck       the other pieces wholly inside the front truck's footprint and below its top
    body        everything else, less the other trucks and wheelsets, which are the same models placed again
Blender is Z-up, metres; the glTF exporter turns that into the game's Y-up. The cab faces -Y. The sources' materials and
textures are kept as they are, one material per texture, unless a spec gives its textures' roughness (see ROUGHNESS_NOTE).

A spec may give fixtures: its headlights, ditch lights, horn and bell, cut out of the body into parts of their own (as
Tools/Blender/es44ac.py does the ES44AC's), so the game finds them by name. Each is the body's faces lying wholly inside a box,
in the game's axes (metres, rail at zero, cab towards -Y), with the way it faces. Its object's origin is at the box's middle and
its local -Y points that way (the glTF node's local +Z); assembly.json lists where each is and which way it faces.
"""
import json
import math
import os
import re
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
from rollingstock import coupler as shared_coupler  # noqa: E402

NC_SA = ("Creative Commons Attribution-NonCommercial-ShareAlike 4.0", "http://creativecommons.org/licenses/by-nc-sa/4.0/", "CC BY-NC-SA 4.0",
         "Not for commercial use; the models made from it are under the same licence.")
BY = ("Creative Commons Attribution 4.0", "http://creativecommons.org/licenses/by/4.0/", "CC BY 4.0", "")

# front: the source's axis and direction the cab faces, which becomes -Y. truck_top: the highest a truck's piece reaches.
SPECS = {
    "p42dc": dict(folder="P42DC", file="amtrak_general_electric_genesis_p42dc.glb", title="Amtrak General Electric Genesis P42DC",
                  url="https://sketchfab.com/3d-models/amtrak-general-electric-genesis-p42dc-1428d705eb3142cc8f6e029fbbf3bbef",
                  licence=NC_SA, livery=("amtrak", "Amtrak, as the source has it"), front="-y"),
    "sd70m2": dict(folder="SD70M-2", file="cn_emd_sd70m-2.glb", title="CN EMD SD70M-2",
                   url="https://sketchfab.com/3d-models/cn-emd-sd70m-2-15361de102854f73bc3fbd40587a3a76",
                   licence=NC_SA, livery=("cn", "Canadian National, as the source has it"), front="-y"),
    "dash8_40c": dict(folder="Dash8-40C", file="csx_general_electric_dash_8-40c.glb", title="CSX General Electric Dash 8-40C",
                      url="https://sketchfab.com/3d-models/csx-general-electric-dash-8-40c-dcbb2df4034d4a3089ff431d440f4f37",
                      licence=NC_SA, livery=("csx", "CSX, as the source has it"), front="-y"),
    "dash9_44cw": dict(folder="Dash9-44CW", file="norfolk_southern_general_electric_dash_9-44cw.glb", title="Norfolk Southern General Electric Dash 9-44CW",
                       url="https://sketchfab.com/3d-models/norfolk-southern-general-electric-dash-9-44cw-eba372f8dd6f418c95f8699e653c0e07",
                       licence=NC_SA, livery=("ns", "Norfolk Southern, as the source has it"), front="-y"),
    "sd40_2": dict(folder="SD40-2", file="emd_sd40-2.glb", title="EMD SD40-2",
                   url="https://sketchfab.com/3d-models/emd-sd40-2-dac8255019734a339c271288e8b9f3f5",
                   licence=NC_SA, livery=("black", "Black, as the source has it"), front="-x",
                   # The source has these glossy (0.345) to mirror-smooth (0), which turns the black paint to the sky's colour
                   # in a low sun; weathered paint and grimy running gear are far duller. Untextured materials keep their own.
                   roughness={
                       "Image_3": .62,  # the body: long hood sides, radiators, fans
                       "Image_5": .62,  # the body: hood and cab sides
                       "Image_1": .62,  # the cab and nose ends, number boards, headlight housings
                       "Image_0": .85,  # underframe, fuel tank, piping and small fittings
                       "Image_4": .85,  # truck frames and the underframe
                       "Image_2": .6,   # wheel faces
                   },
                   # The headlights on the cab face above the windows (two, one over the other) and on the long hood's end (two
                   # side by side); the ditch lights on the front pilot (none at the rear, as on the real engine); the horn's
                   # three trumpets on the cab roof; the bell under the walkway on the left (+X), ahead of the fuel tank.
                   fixtures={
                       "Headlight_Front_Upper": ((-.11, -7.48, 4.37), (.11, -7.41, 4.59), "lamp", (0, -1, 0)),
                       "Headlight_Front_Lower": ((-.11, -7.48, 4.14), (.11, -7.41, 4.37), "lamp", (0, -1, 0)),
                       "DitchLight_Front_Left": ((.78, -10.43, 1.42), (1.03, -10.20, 1.67), "lamp", (0, -1, 0)),  # left facing forward: +X
                       "DitchLight_Front_Right": ((-1.04, -10.43, 1.42), (-.78, -10.20, 1.67), "lamp", (0, -1, 0)),
                       "Headlight_Rear_Left": ((-.21, 7.89, 3.94), (.015, 7.96, 4.16), "lamp", (0, 1, 0)),  # left facing back: -X
                       "Headlight_Rear_Right": ((.02, 7.89, 3.94), (.24, 7.96, 4.16), "lamp", (0, 1, 0)),
                       "Horn": ((-.26, -7.61, 4.54), (.26, -6.96, 4.89), "horn", (0, -1, 0)),
                       "Bell": ((1.2, -3.79, 1.18), (1.57, -3.43, 1.71), "bell", (0, 0, -1)),
                   }),
    "sw1500": dict(folder="SW1500", file="emd_sw1500.glb", title="EMD SW1500",
                   url="https://sketchfab.com/3d-models/emd-sw1500-4777c1cf85ca4f3a9cd4898119a83d48",
                   licence=BY, livery=("blue", "Blue, as the source has it"), front="-x"),
}
TRUCK_TOP = 1.3
TRUCK_MARGIN = .55  # how far a truck's pieces may reach past its end wheels
TRUCK_HALF_WIDTH = 1.5
HALF_GAUGE = 1.95  # no piece of an engine is centred further than this from its centreline
COUPLER_OUT = .48  # where a source has no coupler: how far its pulling face stands out from the end sill, as on the ES44AC
FLANGE = .0254  # a wheel's flange stands this far proud of its tread
COUPLER_NODE = re.compile(r"^Coupler(\.\d+)?_\d+$|^Coupler_pin")  # the named sources' couplers; their lift levers stay
LODS = ("LOD0",)
LATER_LODS = ("LOD1", "LOD2")
COUPLER_MODEL = shared_coupler.MODEL


def bounds(obj):
    co = np.empty(len(obj.data.vertices) * 3)
    obj.data.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    return co.min(0), co.max(0)


def facing(front):
    """A turn about Z that brings the source's front direction to -Y."""
    angle = {"-y": 0, "+x": -math.pi / 2, "+y": math.pi, "-x": math.pi / 2}[front]
    return Matrix.Rotation(angle, 4, "Z")


def load(spec, source):
    """The source joined into one mesh in world space, turned to face -Y, less its couplers; and their outermost points."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=source)
    turn = facing(spec["front"])
    meshes, coupler_faces = [], []
    for obj in [o for o in bpy.data.objects if o.type == "MESH"]:
        node = obj.parent.name if obj.parent else obj.name
        mesh = obj.data.copy()
        m = turn @ obj.matrix_world
        mesh.transform(m)
        if m.determinant() < 0:  # a mirrored part: baking the mirror in turns its faces inside out
            mesh.flip_normals()
        if COUPLER_NODE.match(node):
            ys = [v.co.y for v in mesh.vertices]
            coupler_faces += [min(ys), max(ys)]
            continue
        meshes.append(mesh)
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    objs = []
    for mesh in meshes:
        o = bpy.data.objects.new("Piece", mesh)
        bpy.context.collection.objects.link(o)
        objs.append(o)
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    bpy.ops.object.join()
    joined = bpy.context.view_layer.objects.active
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.separate(type="LOOSE")
    bpy.ops.object.mode_set(mode="OBJECT")
    pieces = [o for o in bpy.data.objects if o.type == "MESH"]
    return pieces, coupler_faces


def find_wheels(boxes):
    """Indices of pieces shaped like a wheel: round in Y and Z, thin in X, off the centreline, the size of one."""
    found = []
    for i, (lo, hi) in enumerate(boxes):
        size = hi - lo
        d = (size[1] + size[2]) / 2
        if .8 < d < 1.35 and abs(size[1] - size[2]) < .06 * d and size[0] < .4 and .45 < abs((lo[0] + hi[0]) / 2) < .9:
            found.append(i)
    return found


def cluster(values, gap):
    groups = []
    for v in sorted(values):
        if groups and v - groups[-1][-1] < gap:
            groups[-1].append(v)
        else:
            groups.append([v])
    return groups


def analyse(pieces):
    boxes = [bounds(o) for o in pieces]
    wheels = find_wheels(boxes)
    if not wheels:
        raise SystemExit("no wheels found")
    # an axle has wheels on both sides, and every axle's are the same size; that leaves out round lights, tanks and drums.
    # An axle's size is its largest round piece, the tread; smaller ones inside it (the web, a hub) still count as its wheels.
    height = lambda i: boxes[i][1][2] - boxes[i][0][2]  # noqa: E731
    mid = lambda i: (boxes[i][0][1] + boxes[i][1][1]) / 2  # noqa: E731
    groups = []
    for g in cluster([mid(i) for i in wheels], .25):
        members = [i for i in wheels if g[0] - 1e-6 <= mid(i) <= g[-1] + 1e-6]
        if {np.sign(boxes[i][0][0] + boxes[i][1][0]) for i in members} >= {-1.0, 1.0}:
            groups.append(members)
    size = float(np.median([max(height(i) for i in m) for m in groups]))
    wheels = [i for m in groups if abs(max(height(i) for i in m) - size) < .04 * size for i in m]
    centres = [((boxes[i][0] + boxes[i][1]) / 2, (boxes[i][1] - boxes[i][0])) for i in wheels]
    axles = [float(np.mean(g)) for g in cluster([c[1] for c, s in centres], .25)]
    axle_z = float(np.median([c[2] for c, s in centres if s[2] > .97 * size]))
    flange_r = size / 2
    tread_r = flange_r - FLANGE
    x_mid = float(np.mean([c[0] for c, s in centres]))
    trucks = cluster(axles, 3.2)
    if len(trucks) != 2 or len(trucks[0]) != len(trucks[1]):
        raise SystemExit(f"expected two like trucks, found axles {[[round(a, 3) for a in t] for t in trucks]}")
    pivots = [t[len(t) // 2] if len(t) % 2 else (t[0] + t[-1]) / 2 for t in trucks]
    return dict(boxes=boxes, wheels=wheels, axles=axles, axle_z=axle_z, tread_r=tread_r, flange_r=flange_r, x_mid=x_mid, trucks=trucks,
                pivots=pivots)


def classify(pieces, a):
    """Each piece's part: ('wheelset', axle), ('truck', n) or 'body'."""
    out = []
    r = a["flange_r"]
    for lo, hi in a["boxes"]:
        c, s = (lo + hi) / 2, hi - lo
        part = "body"
        for k, y in enumerate(a["axles"]):
            if abs(c[1] - y) < .06 and abs(c[2] - a["axle_z"]) < .06 and s[1] <= 2 * r + .02 and s[2] <= 2 * r + .02 and max(-lo[0], hi[0]) < 1.0:
                part = ("wheelset", k)
                break
        else:
            for n, t in enumerate(a["trucks"]):
                if (lo[1] > t[0] - r - TRUCK_MARGIN and hi[1] < t[-1] + r + TRUCK_MARGIN and hi[2] < a["rail"] + TRUCK_TOP
                        and max(-lo[0], hi[0]) < TRUCK_HALF_WIDTH):
                    part = ("truck", n)
                    break
        out.append(part)
    return out


def leaning(pieces, parts, n, pivot):
    """Where a truck's mass sits along Y from its pivot, to tell whether the two trucks face the same way."""
    total, moment = 0.0, 0.0
    for o, p in zip(pieces, parts):
        if p == ("truck", n):
            lo, hi = bounds(o)
            w = float(np.prod(np.maximum(hi - lo, .01)))
            total += w
            moment += w * ((lo[1] + hi[1]) / 2 - pivot)
    return moment / total if total else 0.0


def end_couplers(pieces, a):
    """For sources without named couplers: the pieces out at each end at coupler height near the centreline, and their faces."""
    boxes = a["boxes"]
    found, faces = set(), []
    for sign in (-1, 1):
        reach = [sign * (hi[1] if sign > 0 else lo[1]) for lo, hi in boxes]
        # the end sill: the outermost wide piece clear of the rail, which leaves out a plow
        sill = max(reach[i] for i, (lo, hi) in enumerate(boxes) if hi[0] - lo[0] > 1.5 and lo[2] - a["rail"] > .45 and hi[2] - a["rail"] > .9)
        band = [i for i, (lo, hi) in enumerate(boxes)
                if .55 < (lo[2] + hi[2]) / 2 - a["rail"] < 1.1 and abs((lo[0] + hi[0]) / 2) < .25 and hi[0] - lo[0] < .6]
        tip = max((reach[i] for i in band), default=sill)
        if tip - sill > .25:  # a coupler stands well out past the sill
            near = [i for i in band if reach[i] > sill - .6 and (boxes[i][1][1] - boxes[i][0][1]) < 1.6]
            found.update(near)
            faces.append(sign * tip)
            print("coupler at", sign, "sill", round(sill, 3), "tip", round(tip, 3), "pieces", len(near))
        else:
            faces.append(sign * (sill + COUPLER_OUT))
            print("no coupler at", sign, "sill", round(sill, 3), "nearest centre piece", round(tip, 3))
    return found, faces


ROUGHNESS_NOTE = "each texture's roughness set to suit worn paint and grimy running gear"


def shared_materials(objects, prefix, roughness=None):
    """One material per texture image, named for the engine and the texture; given that texture's roughness where the spec
    says (and not metallic), its own roughness map unhooked."""
    by_image = {}
    for obj in objects:
        for slot in obj.material_slots:
            mat = slot.material
            if mat is None:
                continue
            image = next((n.image for n in mat.node_tree.nodes if n.type == "TEX_IMAGE" and n.image), None) if mat.use_nodes else None
            key = image.name if image else mat.name
            if key not in by_image:
                by_image[key] = mat
                mat.name = f"{prefix}_{len(by_image)}"
                base = image.name.split(".")[0] if image else None
                bsdf = next((n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED"), None) if mat.use_nodes else None
                if roughness and bsdf and base in roughness:
                    for link in list(bsdf.inputs["Roughness"].links):
                        mat.node_tree.links.remove(link)
                    bsdf.inputs["Roughness"].default_value = roughness[base]
                    bsdf.inputs["Metallic"].default_value = 0.0
                elif roughness and image:
                    print("no roughness for", image.name)
            slot.material = by_image[key]


def aimed(direction):
    """A rotation whose -Y is the direction given, kept upright (its Z up) unless it points straight up or down."""
    back = -Vector(direction).normalized()
    up = Vector((0, 0, 1)) if abs(back.z) < .9 else Vector((0, 1, 0))
    right = back.cross(up).normalized()
    up = right.cross(back)
    return Matrix((right, back, up)).transposed().to_4x4()


def cut_fixtures(body, fixtures):
    """Moves each fixture's faces out of the body (already in the game's axes) into an object of its own, its origin on it
    and turned to face its way. Returns the objects and what assembly.json says of them."""
    import bmesh
    objects, found = [], {}
    for name, (lo, hi, kind, direction) in fixtures.items():
        def inside(co):
            return all(lo[i] <= co[i] <= hi[i] for i in range(3))
        bm = bmesh.new()
        bm.from_mesh(body.data)
        mine = {f.index for f in bm.faces if all(inside(v.co) for v in f.verts)}
        if not mine:
            raise RuntimeError(f"no faces for {name}: has the source changed?")
        piece = bpy.data.objects.new(name, body.data.copy())
        bpy.context.collection.objects.link(piece)
        pm = bmesh.new()
        pm.from_mesh(piece.data)
        pm.faces.ensure_lookup_table()
        bmesh.ops.delete(pm, geom=[f for f in pm.faces if f.index not in mine], context="FACES")
        pm.to_mesh(piece.data)
        pm.free()
        bm.faces.ensure_lookup_table()
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if f.index in mine], context="FACES")
        bm.to_mesh(body.data)
        bm.free()
        piece.data.name = name
        vs = [v.co for v in piece.data.vertices]
        middle = Vector([(min(v[i] for v in vs) + max(v[i] for v in vs)) / 2 for i in range(3)])
        place = Matrix.Translation(middle) @ aimed(direction)
        piece.data.transform(place.inverted())
        piece.matrix_world = place
        piece.show_axis = True
        objects.append(piece)
        found[name] = {"kind": kind, "position": [round(c, 4) for c in middle], "facing": list(direction),
                       "triangles": sum(len(p.vertices) - 2 for p in piece.data.polygons)}
        if kind == "horn":
            found[name]["trumpets"] = trumpets(piece)
        print("fixture", name, found[name])
    return objects, found


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
        if len(comp) < 100:
            continue  # not a trumpet (a few hundred faces): its mount
        vs = list({v for g in comp for v in g.verts})
        ys = [v.co.y for v in vs]
        y0, y1 = min(ys), max(ys)
        cx = sum(v.co.x for v in vs) / len(vs)
        cz = sum(v.co.z for v in vs) / len(vs)

        def spread(end):
            return max(math.hypot(v.co.x - cx, v.co.z - cz) for v in vs if abs(v.co.y - end) < .03)
        opens = -1 if spread(y0) > spread(y1) else 1
        out.append({"mouth": [round(cx, 4), round(y0 if opens < 0 else y1, 4), round(cz, 4)], "facing": [0, opens, 0]})
    bm.free()
    return sorted(out, key=lambda t: t["mouth"][0])


def join(objects, name, matrix):
    for o in objects:
        o.data.transform(matrix)
    bpy.ops.object.select_all(action="DESELECT")
    for o in objects:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    if len(objects) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    obj.name = obj.data.name = name
    return obj


def write(path, obj, extra=()):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    for o in extra:
        o.select_set(True)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=False, export_yup=True, export_materials="EXPORT",
                              export_normals=True, export_texcoords=True, export_image_format="AUTO", export_animations=False)
    tris = sum(len(p.vertices) - 2 for o in (obj, *extra) for p in o.data.polygons)
    print("wrote", path, tris, "triangles", os.path.getsize(path) // 1024, "KB")
    return tris


def extras(spec):
    """The rest of what was changed, for CREDITS.txt."""
    out = ""
    if spec.get("roughness"):
        out += "; " + ROUGHNESS_NOTE
    if spec.get("fixtures"):
        out += ";\nthe headlights, ditch lights, horn and bell cut out as parts of their own"
    return out


def build(key, spec, content, preview):
    out = os.path.join(content, "Models", "Locomotives", spec["folder"])
    source = os.path.join(out, "Source", spec["file"])
    coupler = shared_coupler.load(content)
    pieces, coupler_faces = load(spec, source)
    a = analyse(pieces)
    a["rail"] = a["axle_z"] - a["tread_r"]
    parts = classify(pieces, a)
    if not coupler_faces:
        drop, coupler_faces = end_couplers(pieces, a)
        for i in drop:
            parts[i] = "drop"
    front_face, rear_face = min(coupler_faces), max(coupler_faces)
    # stray pieces some sources carry, off to the side or past the ends: outside the loading gauge or beyond the couplers
    stray = [i for i, (lo, hi) in enumerate(a["boxes"]) if parts[i] == "body"
             and (abs((lo[0] + hi[0]) / 2 - a["x_mid"]) > HALF_GAUGE or lo[1] > rear_face or hi[1] < front_face)]
    for i in stray:
        parts[i] = "drop"
    if stray:
        print(key, "dropped", len(stray), "stray pieces", [(np.round(a["boxes"][i][0], 2).tolist(), np.round(a["boxes"][i][1], 2).tolist()) for i in stray[:8]])
    middle = (a["pivots"][0] + a["pivots"][1]) / 2
    lean = [leaning(pieces, parts, n, p) for n, p in enumerate(a["pivots"])]
    rear_yaw = 180 if lean[0] * lean[1] < 0 and min(abs(lean[0]), abs(lean[1])) > .02 else 0
    axles = [round(y - a["pivots"][0], 4) for y in a["trucks"][0]]
    print(key, "axles", [round(y, 3) for y in a["axles"]], "axle z", round(a["axle_z"], 4), "tread r", round(a["tread_r"], 4), "rail", round(a["rail"], 4),
          "x mid", round(a["x_mid"], 4), "pivots", [round(p, 3) for p in a["pivots"]], "lean", [round(v, 3) for v in lean], "rear yaw", rear_yaw,
          "coupler faces", round(front_face, 3), round(rear_face, 3), "pieces", len(pieces))

    lead = a["axles"].index(a["trucks"][0][0])
    groups = {"body": [], "truck": [], "wheelset": [], "drop": []}
    for o, p in zip(pieces, parts):
        if p == "body":
            groups["body"].append(o)
        elif p == ("truck", 0):
            groups["truck"].append(o)
        elif p == ("wheelset", lead):
            groups["wheelset"].append(o)
        else:
            groups["drop"].append(o)
    print(key, {k: len(v) for k, v in groups.items()})
    for o in groups["drop"]:
        bpy.data.objects.remove(o)
    prefix = spec["folder"].replace("-", "")
    shared_materials(groups["body"] + groups["truck"] + groups["wheelset"], prefix, spec.get("roughness"))
    to = lambda y, z: Matrix.Translation((-a["x_mid"], -y, -z))  # noqa: E731
    body = join(groups["body"], "Body", to(middle, a["rail"]))
    fixtures, found = cut_fixtures(body, spec["fixtures"]) if spec.get("fixtures") else ([], {})
    truck = join(groups["truck"], "Truck", to(a["pivots"][0], a["rail"]))
    wheelset = join(groups["wheelset"], "Wheelset", to(a["axles"][lead], a["axle_z"]))

    livery, livery_note = spec["livery"]
    budget = {}
    for folder in LODS:
        budget[f"body{folder}"] = write(os.path.join(out, folder, f"body_{livery}.glb"), body, fixtures)
        budget[f"truck{folder}"] = write(os.path.join(out, folder, "truck.glb"), truck)
        budget[f"wheelset{folder}"] = write(os.path.join(out, folder, "wheelset.glb"), wheelset)
    for folder in LATER_LODS:
        os.makedirs(os.path.join(out, folder), exist_ok=True)
        open(os.path.join(out, folder, ".gitkeep"), "a").close()
    n_axles = len(a["axles"])
    whole = {f: budget[f"body{f}"] + 2 * budget[f"truck{f}"] + n_axles * budget[f"wheelset{f}"] for f in LODS}

    truck_y = (a["pivots"][1] - a["pivots"][0]) / 2
    front = front_face - middle + coupler.reach
    rear = rear_face - middle - coupler.reach
    licence = spec["licence"]
    assembly = {
        "source": f"{spec['title']} by OUTPISTON, {licence[2]}",
        "lengthOverCouplers": round(rear_face - front_face, 3),
        "lods": list(LODS),
        "trianglesWholeEngine": whole,
        "models": "Each model below is <lod>/<model>.glb, except the couplers: Models/Couplers/coupler.glb, the same on every engine and car",
        "bodies": [f"body_{livery}"],
        "truckByBody": {},
        "trucks": [{"model": "truck", "position": [0, -round(truck_y, 4), 0], "yaw": 0},
                   {"model": "truck", "position": [0, round(truck_y, 4), 0], "yaw": rear_yaw}],
        "wheelsets": {"model": "wheelset", "onTruck": [[0, y, round(a["tread_r"], 4)] for y in axles], "radius": round(a["tread_r"], 4)},
        "couplers": [{"model": COUPLER_MODEL, "position": [0, round(front, 4), coupler.height], "yaw": 0},
                     {"model": COUPLER_MODEL, "position": [0, round(rear, 4), coupler.height], "yaw": 180}],
        "axes": "Blender Z-up metres, front towards -Y; in the files, glTF Y-up: (x, y, z) here is (x, z, -y) there",
    }
    if found:
        assembly["fixtures"] = found
        assembly["fixturesNote"] = ("Parts of the body model by these node names. position: the part's origin; facing: the way its light, "
                                    "horn or bell points, the node's local -Y in Blender and +Z in the glTF")
    with open(os.path.join(out, "assembly.json"), "w") as f:
        json.dump(assembly, f, indent=2)
    with open(os.path.join(out, "CREDITS.txt"), "w") as f:
        f.write(f"""{spec['title']} by OUTPISTON
{spec['url']}
Licensed under {licence[0]}: {licence[1]}
{licence[3] + chr(10) if licence[3] else ''}
The source file is in Source/, unchanged. Livery: {livery_note}.
The other models in these folders are made from it by Tools/Blender/outpiston.py. Changes: split into body, truck and wheelset;
turned to run along Y and moved so the rail is at zero; one material per texture; its own couplers taken off{extras(spec)}. Its couplers
are the shared one in Models/Couplers, from the SD40 (see the CREDITS.txt there).
""")
    combined(key, spec, out, livery, assembly, coupler, preview)


def combined(key, spec, out, livery, assembly, coupler, preview):
    """One whole engine for looking at: every part on an empty named for it, so a truck turns with its wheels on it."""
    bpy.ops.wm.read_factory_settings(use_empty=True)

    def holder(name, at=(0, 0, 0), yaw=0.0, parent=None):
        empty = bpy.data.objects.new(name, None)
        empty.empty_display_size = .3
        bpy.context.collection.objects.link(empty)
        empty.parent = parent
        empty.location = at
        empty.rotation_euler = (0, 0, math.radians(yaw))
        return empty

    def load_part(file, parent, prefix):
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=file)
        for obj in set(bpy.data.objects) - before:
            obj.parent = parent
            obj.matrix_parent_inverse = Matrix.Identity(4)
            obj.name = f"{prefix}_{obj.name}"

    lod = os.path.join(out, "LOD0")
    root = holder(spec["folder"])
    load_part(os.path.join(lod, f"body_{livery}.glb"), holder("Body", parent=root), "Body")
    for label, t in zip(("Front", "Rear"), assembly["trucks"]):
        truck_at = holder(f"Truck_{label}", t["position"], t["yaw"], root)
        load_part(os.path.join(lod, "truck.glb"), truck_at, f"Truck_{label}")
        for k, w in enumerate(assembly["wheelsets"]["onTruck"], 1):
            load_part(os.path.join(lod, "wheelset.glb"), holder(f"Wheelset_{label}_{k}", w, 0.0, truck_at), f"Wheelset_{label}_{k}")
    for label, c in zip(("Front", "Rear"), assembly["couplers"]):
        load_part(coupler.path, holder(f"Coupler_{label}", c["position"], c["yaw"], root), f"Coupler_{label}")
    bpy.context.view_layer.update()
    folder = os.path.join(out, "Assembled")
    os.makedirs(folder, exist_ok=True)
    for obj in bpy.data.objects:
        obj.select_set(True)
    name = f"{key}_{livery}"
    bpy.ops.export_scene.gltf(filepath=os.path.join(folder, name + ".glb"), export_format="GLB", use_selection=True, export_yup=True,
                              export_materials="EXPORT", export_normals=True, export_texcoords=True, export_animations=False)
    bpy.ops.file.pack_all()
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(folder, name + ".blend"), check_existing=False)
    print("wrote", folder, name, len(bpy.data.objects), "objects")
    if preview:
        from rollingstock.car import stage, shoot
        os.makedirs(preview, exist_ok=True)
        stage()
        L = assembly["lengthOverCouplers"]
        ty = assembly["trucks"][0]["position"][1]
        shoot(preview, f"{key}_hero", (L * .62, -L * 1.05, 4.6), (0, -L * .06, 2.2), 55)
        shoot(preview, f"{key}_side", (40, 0, 2.3), (0, 0, 2.3), size=(1600, 560), ortho=L * 1.06)
        shoot(preview, f"{key}_far_side", (-40, 0, 2.3), (0, 0, 2.3), size=(1600, 560), ortho=L * 1.06)
        shoot(preview, f"{key}_front", (-4.5, -L * .5 - 8, 2.2), (0, -L * .5, 1.4), 50, (1000, 900))
        shoot(preview, f"{key}_truck", (4.5, ty - 3.2, 1.2), (0, ty, .7), 45, (1200, 760))


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    content, keys, preview = None, [], None
    it = iter(args)
    for arg in it:
        if arg == "--loco":
            keys.append(next(it))
        elif arg == "--preview":
            preview = os.path.abspath(next(it))
        elif arg == "--list":
            for k, s in SPECS.items():
                print(k, s["folder"], s["title"])
            return
        else:
            content = arg
    content = os.path.abspath(content or "Content")
    for key in keys or SPECS:
        build(key, SPECS[key], content, preview)


main()
