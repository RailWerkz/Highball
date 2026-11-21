"""Turns the TTX (DDTX) 53 ft double-stack well car source model into Highball's parts, laid out as the CSX autorack's are.

    blender -b --factory-startup --python Tools/Blender/wellcar_doublestack.py -- <Content folder>

The source is Meshes/RollingStock/wellcar_doublestack/Green-obj.rar (left where it is), unpacked into
Meshes/RollingStock/WellCar/TTX/Source/Green-obj (done here with Windows' tar.exe if it isn't there yet): Green.obj and
Green.mtl, the car's lettering map Car.jpg (DDTX 785369, "IL 53' 1-3/4"") and the two containers' maps, Yang.jpg (a 40 ft
Yang Ming box, 42G1) and Green.jpg (a 40 ft Evergreen high cube, 45G1); Reflection.hdr isn't used. The OBJ is some 1,000
groups and 1.28 million triangles in inch-like units, already running along Y with the A end towards -Y (the hand brake's on
the +Y end, the B end) and Z up, so nothing's turned. It carries the two containers double stacked in the well.

Under WellCar/TTX this writes assembly.json (where each part goes), the parts it's put together from, in Source/Parts
(working files, not loaded by the game):
    body_ttx.glb                  origin on the railhead, midway between the trucks
    truck_front.glb               origin on the railhead at the truck's centre pin, turning about Z
    truck_rear.glb
    wheelset.glb                  origin at the axle's centre, rolling about X
    container_yangming_40.glb     origin at the box's bottom centre (its corner castings' undersides)
    container_evergreen_40hc.glb
and then, by Tools/Blender/wellcar_loads.py, the models the game loads, the whole car each (Truck_Front and Truck_Rear
apart, Coupler_Front and Coupler_Rear on their draft pivots):
    LOD0/wellcar_ttx.glb          empty
    LOD0/wellcar_ttx_<load>.glb   with one box in the well (node Container_Lower), or two stacked (and Container_Upper):
                                  each of wellcar_loads.LOADS, either box alone or two in any order
with the Evergreen-on-Yang-Ming one's .blend in Source. (No LOD1 or LOD2: the game's mesh optimizer makes its own.)

What's done to the source:
  - It's scaled so the well's inside, between its two bulkheads (WELL_BULKHEADS), is the 53 ft 1-3/4 in lettered on the car
    (WELL_INSIDE): about 0.072 m a unit. Measured on the model that gives a 68 in wheelbase, wheels 34 in on the tread
    (37.6 in over the flanges), a well 102 in wide inside and 9 ft 7 in over the side girders, near enough a real 53 ft
    well car's; it also gives 65 ft 8 in truck centres and about 82 ft over the couplers, longer than most stand-alone
    53 ft wells (the model's end platforms reach in over the trucks), but scaling by a shorter truck centres (60 ft, say)
    would shrink the well to 48 ft, too short for the 53 ft box it's lettered and built for.
  - About 40% of the source's objects are wound inside out, their stored normals pointing out (outward): their faces are
    turned outward and every normal is worked out afresh from the faces, sharp over SHARP degrees.
  - The lettering is mapped afresh where the source turned it a quarter on each face (streaks): the side girders' panels
    and the containers' doors and ends face by face (flat_uvs), the containers' corrugated sides by one flat projection
    each (side_uvs). Those objects (UNCAPPED) are kept out of the parts' triangle caps, so the marks stay sharp.
  - The running gear is told apart by where it is: a wheelset is what's centred on an axle (the wheels' tread rings and the
    axle with its wheel centres), a truck what lies wholly low under the car, within its span of the centre pin and the
    width of the side frames; everything else is the body.
  - The running gear's built about 1.61 m between tread centres once scaled: the trucks and wheelsets are squeezed across
    (only them) so the treads come over the game's rails, and centred under the body.
  - Its own couplers are taken off (COUPLER_PARTS) and the shared one from Meshes/Couplers goes on at the standard height,
    its knuckle's nose where the source's was; the air hoses, glad hands and uncoupling levers stay.
  - The containers are lifted out into parts of their own. At the car's scale the source's boxes are 42 ft long and about
    8 ft 2 in square in section, both alike, so each is stretched to its ISO size: 40 ft by 8 ft by 8 ft 6 in for the Yang Ming
    (42G1, as lettered) and 9 ft 6 in high for the Evergreen high cube (45G1). The bottom one stays seated where the source
    has it, on the well's floor and centred lengthwise as it was; the top one sits on it as it did.
  - Triangles: coplanar faces are merged, then anything still denser than its size calls for is collapsed down to a budget
    that grows with the object's size (BUDGET), then each part put together to its CAP (the body 90,000, a truck 14,000,
    a wheelset 2,400, a container 22,000).
  - Materials: every one non-metallic but the wheel treads (Crome); the textures are cut to MAX_SIZE (the car's lettering
    to 4096, the containers' big marks to 2048) and written as JPEG.
  - The ground plane under it (Plane032) is dropped.

Blender is Z-up, metres; the glTF exporter turns that into the game's Y-up. The car's front (its A end) faces -Y.
"""
import json
import math
import os
import subprocess
import sys

import bpy
import numpy as np
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
CONTENT = os.path.abspath(args[0] if args else "Content")
OUT = os.path.join(CONTENT, "Meshes", "RollingStock", "WellCar", "TTX")
COUPLERS = os.path.join(CONTENT, "Meshes", "Couplers")
PARTS = os.path.join(OUT, "Source", "Parts")
ARCHIVE = os.path.join(CONTENT, "Meshes", "RollingStock", "wellcar_doublestack", "Green-obj.rar")
UNPACKED = os.path.join(OUT, "Source", "Green-obj")
SOURCE = os.path.join(UNPACKED, "Green.obj")
LIVERY = "ttx"
MODEL = f"wellcar_{LIVERY}"
SOURCE_NAME = "53 ft double-stack well car, TTX (DDTX 785369), with Yang Ming and Evergreen 40 ft containers, author and licence to be filled in: see CREDITS.txt"

WELL_INSIDE = 53 * 0.3048 + 1.75 * 0.0254  # 53' 1-3/4", lettered on the car (IL)
WELL_BULKHEADS = ("Box5120", "Box5236")  # the well's end bulkheads, at the A end (-Y) and the B end (+Y)
DROP = {"Plane032"}  # the ground it stands on
# The source's couplers, at the A end (-Y) and the B end (+Y): head and shank, knuckle, two knuckle pins, the lift-pin bits
# under the head.
COUPLER_PARTS = {"Cylinder1080", "Cylinder1081", "Cylinder1082", "Cylinder1083", "Cylinder1133", "Cylinder1134",
                 "Cylinder1171", "Cylinder1172", "Cylinder1173", "Cylinder1174", "Cylinder1185", "Cylinder1186"}
COUPLER_HEADS = ("Cylinder1081", "Cylinder1172")  # the knuckles: their furthest points are the noses
TREAD_MATERIAL = "Crome"  # the tread rings: one on each wheel
# The containers: (part name, node, the textured side that gives its length and width, its group's names, the ISO size
# it's stretched to: length, width, height).
CONTAINERS = (
    ("container_yangming_40", "Container_Lower", "Yang", ("Top_Cont", "Yang", "Box5433"), (12.192, 2.438, 2.591)),
    ("container_evergreen_40hc", "Container_Upper", "Green", ("Cont", "Green", "Box5432"), (12.192, 2.438, 2.896)),
)
CARGOES = (("single", 1), ("double", 2))  # the loaded models: how many of CONTAINERS each carries, from the bottom

MAX_SIZE = {"Car": 4096}  # by the image's name; the rest (the containers') to DEFAULT_SIZE
DEFAULT_SIZE = 2048
FLAT = 1.0  # degrees: faces this near coplanar are merged


def BUDGET(diagonal):
    """The most triangles an object of this size (its bounding box's diagonal, metres) is left with."""
    return max(24, int(900 * diagonal))


RAIL_CENTRE = 1.435 / 2 + 0.0373  # the game's rails' middles off the track's centre (DisplayTrack.RailOffset)


def to_game(origin_x, origin_y, origin_z, squeeze=1.0):
    """Moves the origin given to nought and squeezes across (X) by the factor given: the source already runs along Y with
    its A end at -Y."""
    return Matrix.Diagonal((squeeze, 1, 1, 1)) @ Matrix.Translation((-origin_x, -origin_y, -origin_z))


def world_verts(obj):
    n = len(obj.data.vertices)
    co = np.empty(n * 3, np.float32)
    obj.data.vertices.foreach_get("co", co)
    m = np.array(obj.matrix_world)
    return co.reshape(-1, 3) @ m[:3, :3].T + m[:3, 3]


def bounds(obj):
    w = world_verts(obj)
    return Vector(w.min(0)), Vector(w.max(0))


def bounds_of(objects):
    w = np.concatenate([world_verts(o) for o in objects])
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
    """The image, scaled down to at most its MAX_SIZE on its longer side, as a new packed image."""
    stem = os.path.splitext(os.path.basename(image.filepath.replace("\\", "/")))[0]
    most = MAX_SIZE.get(stem, DEFAULT_SIZE)
    copy = image.copy()
    w, h = copy.size
    if max(w, h) > most:
        k = most / max(w, h)
        copy.scale(max(1, int(w * k)), max(1, int(h * k)))
    px = pixels(copy)
    bpy.data.images.remove(copy)
    made = bpy.data.images.new(f"WellCar_{stem}_Colour", px.shape[1], px.shape[0], alpha=True)
    made.colorspace_settings.name = "sRGB"
    made.pixels.foreach_set(px.ravel())
    made.pack()
    return made


METALLIC = {TREAD_MATERIAL: (0.9, 0.35)}  # (metallic, roughness)


def game_materials():
    made = {}
    for mat in bpy.data.materials:
        if not mat.use_nodes:
            continue
        nodes, links = mat.node_tree.nodes, mat.node_tree.links
        bsdf = next((n for n in nodes if n.type == "BSDF_PRINCIPLED"), None)
        if bsdf is None:
            continue
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
        mat.name = f"WellCar_{mat.name.replace(' ', '').strip('_')}"
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


def outward(objects):
    """Turns every object's faces outward and drops the source's own normals for ones worked out from the faces, sharp
    over SHARP. About 40% of the source's objects are wound inside out (mirrored in whatever built it) with their normals
    stored pointing out; the reduction keeps the winding but not those normals, so they'd come out inside out, dark and
    culled. An object's faces point out when the volume they enclose, signed by their winding, is positive."""
    flipped = 0
    for obj in objects:
        me = obj.data
        n = len(me.polygons)
        if n == 0:
            continue
        centre = np.empty(n * 3)
        normal = np.empty(n * 3)
        area = np.empty(n)
        me.polygons.foreach_get("center", centre)
        me.polygons.foreach_get("normal", normal)
        me.polygons.foreach_get("area", area)
        centre, normal = centre.reshape(-1, 3), normal.reshape(-1, 3)
        if float(((centre - centre.mean(0)) * normal).sum(1) @ area) < 0:
            me.flip_normals()
            flipped += 1
        if "custom_normal" in me.attributes:  # normals from the faces
            me.attributes.remove(me.attributes["custom_normal"])
        me.shade_smooth()
        me.set_sharp_from_angle(angle=math.radians(SHARP))
    print("faces turned outward on", flipped, "of", len(objects), "objects")


SHARP = 35.0  # degrees: edges between faces this far apart are shaded sharp


def flat_uvs(obj, across=0):
    """Maps the object's faces that face along axis `across` (0 X, 1 Y) afresh, face by face. The source's faces carry the
    right stretch of the map, but many turned a quarter (the marks come out as streaks or on their sides), so each such
    face is given that stretch flat: v up its height, u across it, reading from outside."""
    me = obj.data
    uv_layer = me.uv_layers.active.data
    along_axis = 1 - across  # the horizontal in the face: Y on a face facing X, X on one facing Y
    fixed = 0
    for poly in me.polygons:
        facing = poly.normal[across]
        if abs(facing) < 0.9:
            continue
        loops = list(poly.loop_indices)
        uvs = np.array([uv_layer[i].uv[:] for i in loops])
        if np.ptp(uvs[:, 0]) < 0.005 or np.ptp(uvs[:, 1]) < 0.005:  # plain: one colour of the map
            continue
        p = np.array([me.vertices[me.loops[i].vertex_index].co[:] for i in loops])
        along = (p[:, along_axis] - p[:, along_axis].min()) / max(np.ptp(p[:, along_axis]), 1e-9)
        # Seen from outside, the right hand is +Y on a face towards +X, -Y towards -X, +X towards -Y, -X towards +Y.
        if (facing < 0) == (across == 0):
            along = 1.0 - along
        up = (p[:, 2] - p[:, 2].min()) / max(np.ptp(p[:, 2]), 1e-9)
        (u0, v0), (u1, v1) = uvs.min(0), uvs.max(0)
        for i, a, b in zip(loops, along, up):
            uv_layer[i].uv = (u0 + a * (u1 - u0), v0 + b * (v1 - v0))
        fixed += 1
    print("flat", obj.name, "facing", "XY"[across], fixed, "faces")


def side_uvs(obj):
    """Maps the container's long sides afresh. The source gives each face of their corrugations the right stretch of the
    map but turned a quarter on the face, so the marks come out as streaks. Here every corner of a side's faces is
    projected flat: v from the height, exactly as the source's faces span it (each face's lowest v is at its bottom, its
    highest at its top), and u along the box, fitted to where the source puts each face's middle; each side reads from
    outside."""
    me = obj.data
    nl, n = len(me.loops), len(me.polygons)
    uv = np.empty(nl * 2)
    me.uv_layers.active.data.foreach_get("uv", uv)
    uv = uv.reshape(-1, 2)
    corner = np.empty(nl, int)
    me.loops.foreach_get("vertex_index", corner)
    co = np.empty(len(me.vertices) * 3)
    me.vertices.foreach_get("co", co)
    co = co.reshape(-1, 3)
    start, total = np.empty(n, int), np.empty(n, int)
    me.polygons.foreach_get("loop_start", start)
    me.polygons.foreach_get("loop_total", total)
    centre, normal = np.empty(n * 3), np.empty(n * 3)
    me.polygons.foreach_get("center", centre)
    me.polygons.foreach_get("normal", normal)
    centre, normal = centre.reshape(-1, 3), normal.reshape(-1, 3)
    lo, hi = co.min(0), co.max(0)
    mid_y, length = (lo[1] + hi[1]) / 2, hi[1] - lo[1]
    for side in (centre[:, 0] > hi[0] - 1.5, centre[:, 0] < lo[0] + 1.5):
        faces = np.where(side & (np.abs(normal[:, 2]) < 0.5) & (np.abs(centre[:, 1] - mid_y) < length / 2 - 1.0))[0]
        middles = np.array([uv[start[f]:start[f] + total[f]].mean(0) for f in faces])
        u_fit = np.polyfit(centre[faces, 1], middles[:, 0], 1)
        z, v = [], []
        for f in faces:
            corners = slice(start[f], start[f] + total[f])
            p, q = co[corner[corners]], uv[corners]
            if np.ptp(p[:, 2]) > 0.1 * (hi[2] - lo[2]):
                z += [p[:, 2].min(), p[:, 2].max()]
                v += [q[:, 1].min(), q[:, 1].max()]
        v_fit = np.polyfit(z, v, 1)
        loops = np.concatenate([np.arange(start[f], start[f] + total[f]) for f in faces])
        p = co[corner[loops]]
        uv[loops, 0] = np.polyval(u_fit, p[:, 1])
        uv[loops, 1] = np.polyval(v_fit, p[:, 2])
        print("container sides", obj.name, len(faces), "faces: u = %.5f y + %.4f, v = %.5f z + %.4f" % (*u_fit, *v_fit))
    me.uv_layers.active.data.foreach_set("uv", uv.ravel())


# Then each part, put together, no more than this many: where the triangles are left is up to the collapse.
CAP = {"body": 90000, "truck": 14000, "wheelset": 2400, "container": 22000}
LETTERING = ("Car",)  # the body's lettering panels, along the side girders
UNCAPPED = {"Car", "Yang", "Green"}  # the lettered objects: the panels, the containers' sides and ends


def capped_join(objects, name, most):
    """The objects joined, capped at `most` triangles, but for the UNCAPPED ones, which are left whole and joined on after:
    collapsing their thin edges would smear the marks."""
    whole = [o for o in objects if o.name in UNCAPPED]
    rest = cap(join([o for o in objects if o.name not in UNCAPPED], name), most - sum(tris(o) for o in whole))
    return join([rest] + whole, name)


def cap(obj, most):
    n = tris(obj)
    if n > most:
        d = obj.modifiers.new("cap", "DECIMATE")
        d.decimate_type = "COLLAPSE"
        d.ratio = most / n
        d.use_collapse_triangulate = True
        apply_all([obj])
        print("capped", obj.name, n, "->", tris(obj))
    return obj


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

if not os.path.exists(SOURCE):
    os.makedirs(os.path.dirname(UNPACKED), exist_ok=True)
    # Windows' bsdtar reads RAR.
    subprocess.run([os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32", "tar.exe"), "-xf", ARCHIVE, "-C", os.path.dirname(UNPACKED)], check=True)

for o in list(bpy.data.objects):
    bpy.data.objects.remove(o)
# Its groups as objects, its axes as they are (the importer would otherwise take it as Y-up).
bpy.ops.wm.obj_import(filepath=SOURCE, use_split_objects=True, use_split_groups=True, forward_axis="Y", up_axis="Z")
for o in [o for o in bpy.data.objects if o.type != "MESH" or o.name in DROP]:
    bpy.data.objects.remove(o)
meshes = [o for o in bpy.data.objects if o.type == "MESH"]
for o in meshes:  # each its own mesh, so baking one's transform doesn't move another
    if o.data.users > 1:
        o.data = o.data.copy()
outward(meshes)
for _, _, side, _, _ in CONTAINERS:
    side_uvs(bpy.data.objects[side])
    flat_uvs(bpy.data.objects[side], across=1)  # the doors and the front end
for name in LETTERING:
    flat_uvs(bpy.data.objects[name], across=0)

# The scale, from the well's inside: the A end bulkhead's inner face (its +Y side) to the B end's (its -Y side).
well_a, well_b = bounds(bpy.data.objects[WELL_BULKHEADS[0]])[1].y, bounds(bpy.data.objects[WELL_BULKHEADS[1]])[0].y
SCALE = WELL_INSIDE / (well_b - well_a)
print("scale", round(SCALE, 5), "well", round(well_a, 3), round(well_b, 3))
for o in meshes:
    place(o, Matrix.Scale(SCALE, 4))

# Everything below is measured after scaling.
rings = [o for o in meshes if material(o) == TREAD_MATERIAL]
axles_y = sorted({round(middle(o).y, 4) for o in rings})
if len(rings) != 8 or len(axles_y) != 4:
    raise RuntimeError(f"{len(rings)} tread rings on axles at {axles_y}, not eight on four: has the source changed?")
front_axles, rear_axles = axles_y[:2], axles_y[2:]  # the A end's at -Y
FRONT_PIVOT, REAR_PIVOT = sum(front_axles) / 2, sum(rear_axles) / 2
MIDDLE = (FRONT_PIVOT + REAR_PIVOT) / 2
AXLE_Z = float(np.mean([middle(o).z for o in rings]))


def tread_radius(ring):
    """The radius of the ring's tread: at its outer edge, away from the flange (which is on the inside, towards the car's
    middle)."""
    w = world_verts(ring)
    c = middle(ring)
    outward = np.sign(c.x - GEAR_X)
    across = (w[:, 0] - c.x) * outward
    edge = across > across.max() - 0.25 * (across.max() - across.min())
    return float(np.hypot(w[edge, 1] - c.y, w[edge, 2] - c.z).max())


ring_x = [middle(o).x for o in rings]
left, right = [x for x in ring_x if x > np.mean(ring_x)], [x for x in ring_x if x <= np.mean(ring_x)]
GEAR_X = (np.mean(left) + np.mean(right)) / 2
SQUEEZE = RAIL_CENTRE / ((np.mean(left) - np.mean(right)) / 2)
TREAD_R = float(np.mean([tread_radius(o) for o in rings]))
FLANGE_R = float(np.mean([(bounds(o)[1].z - bounds(o)[0].z) / 2 for o in rings]))
RAIL = AXLE_Z - TREAD_R
# The body's centreline: midway between the well's side girders' inner faces, measured on the bulkheads that span them.
BODY_X = float(np.mean([middle(bpy.data.objects[n]).x for n in WELL_BULKHEADS]))
print("axles", axles_y, "pivots", round(FRONT_PIVOT, 4), round(REAR_PIVOT, 4), "tread radius", round(TREAD_R, 4), "flange", round(FLANGE_R, 4), "rail", round(RAIL, 4))
print("running gear centred at x", round(GEAR_X, 4), "body at", round(BODY_X, 4), "treads", round(np.mean(left) - np.mean(right), 4), "apart, squeezed by", round(SQUEEZE, 4))

# Where the source's knuckles' noses are, before its couplers come off.
nose_front = bounds(bpy.data.objects[COUPLER_HEADS[0]])[0].y
nose_rear = bounds(bpy.data.objects[COUPLER_HEADS[1]])[1].y
for name in COUPLER_PARTS:
    obj = bpy.data.objects[name]
    meshes.remove(obj)
    bpy.data.objects.remove(obj)


# The containers: each group lifted out of the car.
def container_of(obj):
    for i, (_, _, _, names, _) in enumerate(CONTAINERS):
        for n in names:
            if obj.name == n or (obj.name.startswith(n) and obj.name[len(n):].isdigit()):
                return i
    return None


loads = [[] for _ in CONTAINERS]
for o in list(meshes):
    i = container_of(o)
    if i is not None:
        loads[i].append(o)
        meshes.remove(o)
for (part, *_), group in zip(CONTAINERS, loads):
    print("container", part, len(group), "objects", sum(tris(o) for o in group), "triangles")
    if len(group) < 100:
        raise RuntimeError(f"{part} has only {len(group)} objects: has the source changed?")


WHEELS_X = (min(bounds(o)[0].x for o in rings) - 0.01, max(bounds(o)[1].x for o in rings) + 0.01)  # outer face to outer face


def axle_of(obj):
    """Which axle (an index into axles_y) the object's centred on, or None: its middle on the axle's line, no longer along
    the car than a wheel, and between the wheels' outer faces (the bearing adapters, outside them, are the truck's)."""
    lo, hi = bounds(obj)
    c = (lo + hi) / 2
    if hi.y - lo.y > 2 * FLANGE_R + 0.02 or abs(c.z - AXLE_Z) > 0.03 or lo.x < WHEELS_X[0] or hi.x > WHEELS_X[1]:
        return None
    k = min(range(4), key=lambda i: abs(axles_y[i] - c.y))
    return k if abs(axles_y[k] - c.y) < 0.03 else None


TRUCK_REACH = 1.45  # along the car from the centre pin
TRUCK_WIDTH = 1.25  # across, from the running gear's centre: the side frames' outsides are 1.19 m out, the girders 1.3 m
TRUCK_TOP = RAIL + 1.0  # the side frames and bolster are below this; the end platforms' supports above it


def truck_of(obj):
    """0 for the front truck, 1 for the rear, None if it's not wholly on one."""
    lo, hi = bounds(obj)
    if hi.z > TRUCK_TOP or lo.x < GEAR_X - TRUCK_WIDTH or hi.x > GEAR_X + TRUCK_WIDTH:
        return None
    for i, pivot in enumerate((FRONT_PIVOT, REAR_PIVOT)):
        if lo.y > pivot - TRUCK_REACH and hi.y < pivot + TRUCK_REACH:
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
    names = sorted(o.name for o in v)
    print("group", k, len(v), "objects", sum(tris(o) for o in v), "triangles", names if k.startswith("axle") else [n for n in names if not n.startswith("Track")] if k.startswith("truck") else "")
for k in range(4):
    if len([o for o in groups[f"axle{k}"] if material(o) == TREAD_MATERIAL]) != 2 or len(groups[f"axle{k}"]) < 3:
        raise RuntimeError(f"axle {k} has {sorted(o.name for o in groups[f'axle{k}'])}, not two treads and the axle")
strays = [o.name for o in groups["body"] if o.name.startswith("Track")]
if strays:
    raise RuntimeError(f"truck parts left on the body: {strays}")

game_materials()
lighten(meshes + [o for g in loads for o in g])

# The body: everything else in one object, on the railhead midway between the trucks.
GAME = to_game(BODY_X, MIDDLE, RAIL)
for o in groups["body"]:
    place(o, GAME)
body = capped_join(groups["body"], "Body", CAP["body"])

# Each truck on its own, its origin at its centre pin on the railhead; its wheelsets with their origins at the axles.
trucks, wheelsets = [], []
for i, (pivot, axles) in enumerate(((FRONT_PIVOT, front_axles), (REAR_PIVOT, rear_axles))):
    label = ("Front", "Rear")[i]
    for o in groups[f"truck{i}"]:
        place(o, to_game(GEAR_X, pivot, RAIL, SQUEEZE))
    trucks.append(cap(join(groups[f"truck{i}"], f"Truck_{label}"), CAP["truck"]))
    sets = []
    for a in axles:
        k = axles_y.index(a)
        for o in groups[f"axle{k}"]:
            place(o, to_game(GEAR_X, a, AXLE_Z, SQUEEZE))
        sets.append(cap(join(groups[f"axle{k}"], f"Wheelset_{label}_{len(sets) + 1}"), CAP["wheelset"]))
    wheelsets.append(sets)

# The containers: stretched to their ISO sizes about their bottom centres, each origin there. The bottom one stays where
# the source seats it; the top one goes on it as it was on the source's (their castings overlap by the same little).
containers, container_at = [], []
stack_top = previous_top = None
for (part, node, side, _, (length, width, height)), group in zip(CONTAINERS, loads):
    s_lo, s_hi = bounds(bpy.data.objects[side])  # the side walls: the box's length and width, less its corner fittings
    g_lo, g_hi = bounds_of(group)
    bottom = Vector(((s_lo.x + s_hi.x) / 2, (s_lo.y + s_hi.y) / 2, g_lo.z))
    k = (width / (s_hi.x - s_lo.x), length / (s_hi.y - s_lo.y), height / (g_hi.z - g_lo.z))
    if stack_top is None:
        z = g_lo.z - RAIL
    else:
        z = stack_top + (g_lo.z - previous_top)
    at = (round(bottom.x - BODY_X, 4), round(bottom.y - MIDDLE, 4), round(z, 4))
    previous_top, stack_top = g_hi.z, z + height
    for o in group:
        place(o, Matrix.Diagonal((*k, 1)) @ Matrix.Translation(-bottom))
    containers.append(capped_join(group, node, CAP["container"]))
    container_at.append(at)
    print("container", part, "stretched by", [round(v, 4) for v in k], "bottom at", at, "top", round(stack_top, 4))

with open(os.path.join(COUPLERS, "coupler.json")) as f:
    coupler = json.load(f)
COUPLER_Z = coupler["height"]
REACH = coupler["reach"]
front_face = nose_front - MIDDLE  # game Y: the A end's at -Y
rear_face = nose_rear - MIDDLE
COUPLER_FRONT, COUPLER_REAR = front_face + REACH, rear_face - REACH
OVER_COUPLERS = rear_face - front_face
TRUCK_Y = (REAR_PIVOT - FRONT_PIVOT) / 2

budget = {}
budget["body"] = write(os.path.join(PARTS, f"body_{LIVERY}.glb"), [body])
budget["truck_front"] = write(os.path.join(PARTS, "truck_front.glb"), [trucks[0]])
budget["truck_rear"] = write(os.path.join(PARTS, "truck_rear.glb"), [trucks[1]])
budget["wheelset"] = write(os.path.join(PARTS, "wheelset.glb"), [wheelsets[0][0]])
for (part, *_), obj in zip(CONTAINERS, containers):
    budget[part] = write(os.path.join(PARTS, f"{part}.glb"), [obj])
whole = budget["body"] + budget["truck_front"] + budget["truck_rear"] + sum(tris(w) for s in wheelsets for w in s)
print("whole car triangles, less couplers", whole, "containers", [budget[c[0]] for c in CONTAINERS])


def on_truck(axles, pivot):
    return [[0, round(a - pivot, 4), round(TREAD_R, 4)] for a in axles]


def model_file(cargo=None):
    return f"{MODEL}.glb" if cargo is None else f"{MODEL}_{cargo}.glb"


COUPLER_MODEL = "Couplers/coupler.glb"
assembly = {
    "source": SOURCE_NAME,
    "scale": round(SCALE, 5),
    "lengthOverCouplers": round(OVER_COUPLERS, 3),
    "truckCentres": round(REAR_PIVOT - FRONT_PIVOT, 3),
    "frontOverhang": round(-front_face - TRUCK_Y, 3),  # the A end's knuckle nose to the front truck's centre pin
    "wheelDiameter": round(2 * TREAD_R, 4),
    "lods": ["LOD0"],
    "trianglesWholeCar": {"LOD0": whole},
    "models": f"The game loads LOD0/{model_file()} empty, and LOD0/{model_file('single')} and LOD0/{model_file('double')} loaded (LOD1 and LOD2 beside them, from Tools/Blender/lods.py). The parts below are Source/Parts/<model>.glb, put together into them, except the couplers: Meshes/Couplers/coupler.glb, the same on every engine and car",
    "bodies": [f"body_{LIVERY}"],
    "trucks": [
        {"model": "truck_front", "position": [0, -round(TRUCK_Y, 4), 0], "yaw": 0, "wheelsets": on_truck(front_axles, FRONT_PIVOT)},
        {"model": "truck_rear", "position": [0, round(TRUCK_Y, 4), 0], "yaw": 0, "wheelsets": on_truck(rear_axles, REAR_PIVOT)},
    ],
    "wheelsets": {"model": "wheelset", "onTruck": "each truck's own, in its entry above", "radius": round(TREAD_R, 4)},
    "couplers": [{"model": COUPLER_MODEL, "position": [0, round(COUPLER_FRONT, 4), COUPLER_Z], "yaw": 0},
                 {"model": COUPLER_MODEL, "position": [0, round(COUPLER_REAR, 4), COUPLER_Z], "yaw": 180}],
    "containers": [{"model": part, "node": node, "position": list(at), "size": list(size)}
                   for (part, node, _, _, size), at in zip(CONTAINERS, container_at)],
    "loads": {cargo: {"model": f"LOD0/{model_file(cargo)}", "containers": [c[0] for c in CONTAINERS[:n]], "triangles": whole + sum(budget[c[0]] for c in CONTAINERS[:n])}
              for cargo, n in CARGOES},
    "axes": "Blender Z-up metres, front (the A end) towards -Y; in the files, glTF Y-up: (x, y, z) here is (x, z, -y) there",
}
with open(os.path.join(OUT, "assembly.json"), "w") as f:
    json.dump(assembly, f, indent=2)
print("assembly", {k: assembly[k] for k in ("scale", "lengthOverCouplers", "truckCentres", "frontOverhang", "wheelDiameter", "trucks", "couplers", "containers")})


# The whole-car models, empty and with each stack of boxes, from those parts (Tools/Blender/wellcar_loads.py).
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import wellcar_loads  # noqa: E402

wellcar_loads.build(CONTENT, save_blend=True)
