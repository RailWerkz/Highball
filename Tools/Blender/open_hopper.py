"""Turns the WITX four-bay open hopper source model into Highball's parts, laid out as the CSX autorack's are
(Tools/Blender/csx_autorack.py, which this is adapted from).

    blender -b --factory-startup --python Tools/Blender/open_hopper.py -- <Content folder>

The source is Meshes/RollingStock/OpenHopper/WITX/Source/Hopper-1.obj (unpacked, with its .mtl, Hopp.jpg and
Reflection.hdr, from Meshes/RollingStock/closed_hopper/Hopper-1-obj.rar, which stays where it is): 330 loose objects and
565,000 triangles, built in centimetre-ish units at about a twenty-fifth of full size. It's a four-bay open
hopper lettered WITX 5861 for carbon black ("DO NOT LOAD CARBON BLACK OVER 225 F"). Everything but the running gear is
baked into one body; the trucks and wheelsets are kept apart so they can swivel. Under OpenHopper/WITX this writes
assembly.json (where each part goes), the parts it's put together from, in Source/Parts (working files, not loaded by the
game):
    body_witx.glb        origin on the railhead, midway between the trucks
    truck_front.glb      origin on the railhead at the truck's centre pin, turning about Z
    truck_rear.glb
    wheelset.glb         origin at the axle's centre, rolling about X
and LOD0/open_hopper_witx.glb, the whole car, which the game loads (Truck_Front and Truck_Rear apart), with its .blend in
Source. (No LOD1 or LOD2: the game's mesh optimizer makes its own.)

What's done to the source:
  - It's scaled so its truck centres are 45 ft (TRUCK_CENTRES): about 0.0403 times. Nothing in the source gives a size, so
    the truck centres were picked to fit its proportions: at 45 ft its body comes out about 17.7 m (58 ft) over the end
    sills, 3.1 m (10 ft 2 in) wide and 4.7 m (15 ft 5 in) above the rail, its wheels 36 in on the tread: a typical North
    American 286k open hopper's (a 5,161 cu ft three-bay is 59-60 ft over the strikers, 45-46 ft between truck
    centres, 15 ft 5 in high, 10 ft 8 in wide). The model's truck wheelbase comes out long (about 7 ft 2 in against a
    real 100-ton truck's 5 ft 10 in); that's left as built.
  - The running gear is told apart by where it is, as on the autorack: a wheelset is what's centred on an axle (two wheels,
    their tread rings and the axle), a truck what lies wholly under the car within its span of the centre pin; everything
    else is the body. The source's names don't help: every truck part is called "Wheel...".
  - The running gear's built about 6% wide of standard gauge once scaled: the trucks and wheelsets are squeezed across
    (only them) so the treads come over the game's rails, and centred under the body.
  - Its own couplers (head, knuckle, pins and shank at each end: COUPLER_PARTS) are taken off and the shared one from
    Meshes/Couplers goes on at the standard height, its knuckle's nose where the source's was but where its shank
    wouldn't reach the end sill: the A end's is pulled in 0.16 m (and the B end's 0.01 m) so the shank goes into the
    sill (SHANK_INTO). The draft-gear blocks inside the end sills (Box4338, Box4339) stay: they're hidden.
  - The lettering on the body's sides and ends is mapped afresh (reletter_sides): the source's UVs there are pieced
    together face by face and come out scrambled (on one side the reporting marks turned on end and mirrored and the
    herald smeared along the car, on the other no reporting marks at all, mirrored scraps on the ends). Each side gets
    its own row of the texture's side lettering, each end its half of the top row, projected straight on.
  - Dropped: the ground plane (Plane001), and a plain box between each truck's wheels from axle to axle
    (Box4397_pivot, Box4399_pivot: 44 triangles each, standing in for nothing on a real truck, and showing red through
    the side-frame windows where it fills them).
  - Triangles: coplanar faces are merged (but on the lettered body shell, Hopp, where that skews the lettering), then
    anything still denser than its size calls for is collapsed down to a budget that grows with the object's size
    (BUDGET; a fifth of that for the trucks and wheelsets, GEAR_BUDGET, as the source's trucks are 200,000 triangles
    each).
  - Materials: every one non-metallic but the wheel treads (Crome); the texture (10240 square, not found by the OBJ
    importer, as the .mtl names it by an absolute path on the author's machine) is linked to the Hopp material here, cut
    to MAX_SIZE and written as JPEG.

Which end's which: the hand brake wheel's on the end at -X in the source, so that's the B end; the A end (the car's front)
is +X. Blender is Z-up, metres; the glTF exporter turns that into the game's Y-up. The car's front faces -Y, so the source,
which runs along X, is turned a quarter about Z, as the autorack's is.
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
OUT = os.path.join(CONTENT, "Meshes", "RollingStock", "OpenHopper", "WITX")
COUPLERS = os.path.join(CONTENT, "Meshes", "Couplers")
PARTS = os.path.join(OUT, "Source", "Parts")
SOURCE = os.path.join(OUT, "Source", "Hopper-1.obj")
TEXTURES = os.path.join(OUT, "Source")
LIVERY = "witx"
MODEL = f"open_hopper_{LIVERY}"
SOURCE_NAME = "Four-bay open hopper, WITX 5861, author and licence to be filled in: see CREDITS.txt"

TRUCK_CENTRES = 13.716  # 45 ft: see above
DROP = {"Plane001", "Box4397_pivot", "Box4399_pivot"}  # the ground it stands on; a box between each truck's wheels
# The source's couplers, at the A end (+X) and the B end (-X): head, knuckle, knuckle pin, lock pin, shank.
COUPLER_PARTS = {"Box039", "Box038", "Cylinder027", "Cylinder028", "Box040",
                 "Box4340", "Box4341", "Cylinder112", "Cylinder113", "Box4342"}
COUPLER_HEADS = (("Box039", "Box038"), ("Box4340", "Box4341"))  # head and knuckle: their furthest point is the knuckle's nose
TREAD_MATERIAL = "Crome"  # the tread rings: one on each wheel
BODY_SIDES = ("Hopp",)  # the body shell: the body's centreline is its middle
TEXTURED = {"Hopp": "Hopp.jpg"}  # materials whose colour map the importer couldn't find

MAX_SIZE = 4096  # the source's map is 10240 square, the lettering big on it
FLAT = 1.0  # degrees: faces this near coplanar are merged


def BUDGET(diagonal):
    """The most triangles an object of this size (its bounding box's diagonal, metres) is left with."""
    return max(32, int(3000 * diagonal))


def GEAR_BUDGET(diagonal):
    """The same for the running gear, which the source details far more finely than the body (springs, bolts and
    castings of thousands of triangles each: 200,000 a truck), and which is mostly in shadow under the car."""
    return max(24, int(600 * diagonal))


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


def link_textures():
    """Gives each material in TEXTURED its colour map from Source, where the importer didn't find it."""
    for mat in list(bpy.data.materials):  # (a list: renaming one reorders them)
        file = TEXTURED.get(mat.name)
        if file is None or not mat.use_nodes:
            continue
        nodes, links = mat.node_tree.nodes, mat.node_tree.links
        bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
        if any(l.to_node == bsdf and l.to_socket.name == "Base Color" and l.from_node.type == "TEX_IMAGE" for l in links):
            continue
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = bpy.data.images.load(os.path.join(TEXTURES, file), check_existing=True)
        links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
        print("linked", file, "to", mat.name)


def game_materials():
    link_textures()
    for image in bpy.data.images:
        found = os.path.join(TEXTURES, os.path.basename(image.filepath.replace("\\", "/")))
        if os.path.exists(found):
            image.filepath = found
            image.reload()
    made = {}
    for mat in list(bpy.data.materials):  # (a list: renaming one reorders them)
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
        mat.name = f"OpenHopper_{mat.name.replace(' ', '')}"
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


def lighten(objects, gear):
    before = sum(tris(o) for o in objects)
    for obj in objects:
        # Not the lettered body shell: merging its faces skews and breaks up the lettering (even with UV seams kept),
        # and it's light enough as it is.
        if (material(obj) or "").removeprefix("OpenHopper_") not in TEXTURED:
            d = obj.modifiers.new("flat", "DECIMATE")
            d.decimate_type = "DISSOLVE"
            d.angle_limit = math.radians(FLAT)
            d.delimit = {"UV", "MATERIAL", "SEAM", "SHARP"}
        obj.modifiers.new("tri", "TRIANGULATE")
    apply_all(objects)
    for obj in objects:
        lo, hi = bounds(obj)
        budget = (GEAR_BUDGET if obj in gear else BUDGET)((hi - lo).length)
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
# Each OBJ group its own object, the file's axes kept as they are (it's Z-up: the ground plane's at z = 0).
bpy.ops.wm.obj_import(filepath=SOURCE, use_split_groups=True, forward_axis="Y", up_axis="Z")
for o in [o for o in bpy.data.objects if o.type != "MESH" or o.name in DROP]:
    bpy.data.objects.remove(o)


def reletter_sides():
    """Maps the lettering on to the body's two sides afresh. The source's UVs on its sides are pieced together face by face
    and come out wrong: on the +Y side the reporting marks are turned on end and mirrored and the herald smeared along
    the car; on the -Y side there are no reporting marks at all. The texture has one row of side lettering for each side
    (reporting marks at the left, the herald right of the middle, data at the right), the second SIDE_ROW_STEP below
    the first; each side's flat sheets (SIDE_PLANES: the outer sheet and the one set back behind it, not the posts) are
    projected straight on to its row, read from outside the car, its width spanning the straight part of the side
    between the end slope sheets (SIDE_SPAN). The faces mapped to the orange reflective stripes (ORANGE) keep theirs.
    The sides' inner faces, which show above the end slope sheets with scraps of mirrored lettering on them, are made
    plain (PLAIN: a texel of the body colour). The ends' outer faces, garbled the same way, get the texture's top row
    ("DO NOT LOAD CARBON BLACK", reporting marks: its left half for the B end, its right for the A end) across the band
    along their tops (END_BAND up), and plain below it."""
    body = bpy.data.objects[BODY_SIDES[0]]
    me = body.data
    uv = me.uv_layers.active.data
    xs = [v.co.x for v in me.vertices]
    centre = (min(xs) + max(xs)) / 2
    ys = [v.co.y for v in me.vertices]
    middle_y = (min(ys) + max(ys)) / 2
    changed = 0
    for p in me.polygons:
        end = 1 if p.normal.x > 0.95 else -1 if p.normal.x < -0.95 else 0
        if end and end * (p.center.x - centre) > (max(xs) - min(xs)) / 2 - END_DEPTH:
            low = min(me.vertices[i].co.z for i in p.vertices)
            for l in p.loop_indices:
                co = me.vertices[me.loops[l].vertex_index].co
                uv[l].uv = (END_ROW_U[end] + end * (co.y - middle_y) / END_SPAN, END_ROW_V + (co.z - END_ROW_Z) / END_SPAN) if low >= END_BAND else PLAIN
            changed += 1
            continue
        side = 1 if p.normal.y > 0.99 else -1 if p.normal.y < -0.99 else 0
        if side and side * p.center.y < 0:  # the sides' inner faces, seen from the end bays: plain
            for l in p.loop_indices:
                uv[l].uv = PLAIN
            changed += 1
            continue
        if not side or round(p.center.y, 1) not in SIDE_PLANES[side]:
            continue
        u, v = (sum(uv[l].uv[i] for l in p.loop_indices) / p.loop_total for i in (0, 1))
        if ORANGE[0] <= u <= ORANGE[2] and ORANGE[1] <= v <= ORANGE[3]:
            continue
        row = SIDE_ROW[side]
        for l in p.loop_indices:
            co = me.vertices[me.loops[l].vertex_index].co
            uv[l].uv = (min(max(0.5 - side * (co.x - centre) / SIDE_SPAN, 0.001), 0.999), row + (co.z - SIDE_ROW_Z) / SIDE_SPAN)
        changed += 1
    print("relettered", changed, "faces on the body's sides, inside and out")


# The body's sides' lettering (reletter_sides), in the source's units: its flat sheets on each side (their faces'
# y, to a tenth), the straight part of the side's length, the height the bottom of each side's row of lettering is put
# at, and where in the texture that is (v): -Y's row is the upper one.
SIDE_PLANES = {-1: {-36.8, -36.2, -36.1}, 1: {37.1, 36.9}}
SIDE_SPAN = 342.0
SIDE_ROW_Z = 40.0
SIDE_ROW_STEP = 542 / 1932
SIDE_ROW = {-1: 0.69, 1: 0.69 - SIDE_ROW_STEP}
# The ends' (the same): how far in from the car's furthest point their outer faces are, the width the texture's
# half-row spans, the height the top of its lettering is put at, the faces lettered (those wholly above END_BAND), and
# where the row is in the texture: the middle of each half (u) and the top of the lettering (v).
END_DEPTH = 1.5
END_SPAN = 2 * 77.3
END_ROW_Z = 109.0
END_BAND = 88.0
END_ROW_U = {1: 0.75, -1: 0.25}
END_ROW_V = 1 - 20 / 1932
PLAIN = (0.3, 0.3)
ORANGE = (0.0, 0.0, 0.36, 0.19)  # (u, v) to (u, v): the reflective stripes' patches
reletter_sides()

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
nose_front = max(bounds(bpy.data.objects[n])[1].x for n in COUPLER_HEADS[0])
nose_rear = min(bounds(bpy.data.objects[n])[0].x for n in COUPLER_HEADS[1])
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
TRUCK_TOP = RAIL + 0.85  # above the side frames and bolster; the body's lowest over the trucks (the end platforms) is higher


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
lighten(meshes, {o for k, v in groups.items() if k != "body" for o in v})

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


def shank():
    """How far the shared coupler's shank reaches back from its pivot (Meshes/Couplers/coupler.glb, pulling face
    towards -Y)."""
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=os.path.join(COUPLERS, "coupler.glb"))
    made = set(bpy.data.objects) - before
    bpy.context.view_layer.update()
    back = max(float(world_verts(o)[:, 1].max()) for o in made if o.type == "MESH")
    for o in made:
        bpy.data.objects.remove(o)
    return back


# The source's couplers stand out further than the shared one reaches: where their knuckles' noses were, its shank
# would stop short of the end sill, with daylight between them under the end platform (0.11 m at the A end). So each is
# pulled in, where it needs to be, until its shank goes SHANK_INTO past the end sill's face (the body at the coupler's
# height within 0.5 m of the centreline) into the draft-gear pocket behind it.
SHANK_INTO = 0.05
SHANK = shank()
w = world_verts(body)
line = w[(np.abs(w[:, 0]) < 0.5) & (np.abs(w[:, 2] - COUPLER_Z) < 0.1)]
SILL_FRONT, SILL_REAR = float(line[:, 1].min()), float(line[:, 1].max())
COUPLER_FRONT = max(front_face + REACH, SILL_FRONT - SHANK + SHANK_INTO)
COUPLER_REAR = min(rear_face - REACH, SILL_REAR + SHANK - SHANK_INTO)
print("sills at", round(SILL_FRONT, 4), round(SILL_REAR, 4), "shank", round(SHANK, 4), "couplers pulled in by",
      round(COUPLER_FRONT - (front_face + REACH), 4), round((rear_face - REACH) - COUPLER_REAR, 4))
front_face, rear_face = COUPLER_FRONT - REACH, COUPLER_REAR + REACH
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
    "lods": ["LOD0"],
    "trianglesWholeCar": {"LOD0": whole},
    "models": f"The game loads LOD0/{MODEL}.glb (no hand-made LOD1 or LOD2: the game's mesh optimizer makes its own). The parts below are Source/Parts/<model>.glb, put together into it, except the couplers: Meshes/Couplers/coupler.glb, the same on every engine and car",
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

    root = holder("OpenHopper")
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
