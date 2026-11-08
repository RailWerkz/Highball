"""Turns the EMD SD40 source model into Highball's parts, at three levels of detail, in each livery.

    blender -b --factory-startup --python Tools/Blender/sd40.py -- <Content folder>

The source is "EMD-SD40 Diesel Electric Locomotive" by Heataker, CC BY 4.0:
https://sketchfab.com/3d-models/emd-sd40-diesel-electric-locomotive-7271826d3a584a22849c949a7755f79d
Its files stay in Models/Locomotives/SD40/Source. Under SD40 this writes CREDITS.txt, assembly.json (where each part goes), one
folder per level of detail, of which only LOD0 is built for now (see LATER_LODS), and
    LOD0/body_<livery>.glb   origin on the railhead, midway between the trucks
    LOD0/truck.glb           origin on the railhead at the truck's pivot, turning about Z; the front truck at yaw 0
    LOD0/wheelset.glb        origin at the axle's centre, rolling about X
    Assembled/sd40_<livery>.glb and .blend, the whole engine for looking at; the game loads the separate parts.

Its coupler is the one every engine and car in the game uses, so it goes to Models/Couplers instead, with coupler.json
(its height above the railhead and its reach to the pulling face) and CREDITS.txt:
    Couplers/coupler.glb     origin at the draft pivot, swinging about Z, pulling face towards -Y

Blender is Z-up, metres; the glTF exporter turns that into the game's Y-up. The short hood faces -Y.

The source maps the engine's left and right sides on to the same part of its texture, which would mirror any lettering. So the
body's texture here is two pages side by side: the source's atlas twice, with the shell's -X half moved on to the right-hand page.
A livery is then painted from where each texel lands on the engine: its colours and lettering are given in the engine's own
metres, and the source's seams, hinges and handles are kept as shading under them.

The source's roughness map is kept, weathered, but it marks nothing as metal and has no normal map. So bare metal and glass are
set here part by part (BARE, GEAR_BARE), and the shell's normal map is raised from the seams and hinges in its base colour.
"""
import json
import math
import os
import re
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Matrix

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
CONTENT = os.path.abspath(args[0] if args else "Content")
OUT = os.path.join(CONTENT, "Models", "Locomotives", "SD40")
COUPLERS = os.path.join(CONTENT, "Models", "Couplers")
SOURCE = os.path.join(OUT, "Source", "emd-sd40_diesel_electric_locomotive_4k.glb")

# Measured off the source's meshes.
AXLE_Z = 0.8283
TREAD_R = 0.4895  # the wheel's radius where it meets the rail, just outboard of the flange
RAIL = AXLE_Z - TREAD_R
TRUCK_Y = 6.4117
AXLES = (-1.9641, 0.0, 2.0565)  # on the front truck, from its pivot
COUPLER_Y = 9.7005
COUPLER_Z = 1.1522
KNUCKLE_NOSE = 0.098
FLANGE_X = 0.745  # where the source's flange faces are; they should touch the rails' gauge faces
GAUGE_X = 0.7175  # half of 4' 8.5"

# keep: parts this small are left alone. ratio: what the rest come down to. page: the height of the body's texture, twice as wide.
LODS = (
    {"folder": "LOD0", "suffix": "", "keep": 3000, "ratio": 1.0, "page": 4096, "gear_tex": 4096, "drop": (), "floors": {}},
)
# Not built until LOD0 is finished; their folders are kept ready. Move them up into LODS to build them.
LATER_LODS = (
    {"folder": "LOD1", "suffix": "_mid", "keep": 500, "ratio": .12, "page": 2048, "gear_tex": 512,
     "drop": ("Detail_Hose", "Detail_Chain", "Detail_Plug", "Detail_Holder", "Detail_PipeJoint", "Detail_Bulb", "Detail_FuelCap"),
     "floors": {"Base": .25}},
    {"folder": "LOD2", "suffix": "_far", "keep": 150, "ratio": .03, "page": 1024, "gear_tex": 256,
     "drop": ("Detail_", "RailBars", "Railing", "Horns", "Brakes"),
     "floors": {"Base": .1, "Bogee_": .08, "Suspension": .12}},
)
# What gives the engine its shape: these lose little even far away.
SHAPE = ("Body", "Cab", "Cab_Windows", "Non_Mirrored", "CowCatcher", "Vent_Module")

# What each part of the body takes from a livery.
SHELL, SILL, RAILS, ROOFGEAR, TRIM, BOARD = 1, 2, 3, 4, 5, 6
GROUPS = ((BOARD, ("Detail_Signs",)),
          (TRIM, ("Horns", "Detail_FuelCap", "Detail_PipeJoint", "Detail_Bulb")),
          (ROOFGEAR, ("Exhaust_Fans", "Vent_Grid")),
          (RAILS, ("RailBars", "Railing", "Detail_HandleBar", "Detail_Chain", "Detail_Holder")),
          (SILL, ("Base",)),
          (SHELL, SHAPE[:1] + ("Cab", "Non_Mirrored", "Vent_Module")))
RUNNING = ("Bogee_", "Brakes", "Suspension", "Wheel_", "Coupling")

# What isn't paint: the parts, how metal they are and how rough. Everything else on the engine is painted, so not a metal to look at.
BARE = ((("Horns",), 1.0, .35),
        (("Detail_FuelCap", "Detail_PipeJoint", "Detail_Plug", "Detail_Hose_Plug"), .9, .5),
        (("Detail_Lamp",), 1.0, .2))
GLASS = ("Cab_Windows", "Detail_Bulb")  # only their dark texels: the windows' frames share the mesh, and are painted
GEAR_BARE = ((("Wheel_",), 1.0, .45), (("Coupling",), .8, .55))
GEAR_ROUGH = .6  # the rest of the running gear: greasy castings, whatever the source's map says
SEAM_HEIGHT = 4.0  # how steeply a dark line in the base colour dips in the normal map

# Stations on the engine, in metres from the rail and from midway between the trucks.
NOSE_END = -8.0  # the nose's front face is ahead of this
CAB = (-6.99, -4.46)
HOOD_END = 7.5  # the long hood's rear face is behind this
FAN_CENTRES = (.299, 1.669, 3.844, 5.214, 6.584)  # along the roof
FAN_BOTTOM = 4.65  # in the source, above its own origin


def code(text):
    return np.array([int(text[k:k + 2], 16) / 255 for k in (1, 3, 5)], dtype=np.float32)


CSX_BLUE, CSX_YELLOW, SILVER = "#264563", "#e8ac28", "#c7ccd0"
UP_YELLOW, UP_GRAY, UP_RED = "#f0b400", "#7e8388", "#b3172a"
NS_BLACK, NS_WHITE = "#17181b", "#e8e8e4"


def csx_shell(p, n):
    """Blue; a silver cab roof; a yellow nose front."""
    out = np.broadcast_to(code(CSX_BLUE), p.shape).copy()
    cab = (p[..., 1] > CAB[0]) & (p[..., 1] < CAB[1])
    out[cab & (n[..., 2] > .35) & (p[..., 2] > 3.5)] = code(SILVER)
    out[(n[..., 1] < -.6) & (p[..., 1] < NOSE_END)] = code(CSX_YELLOW)
    return out


def up_shell(p, n):
    """Yellow; gray above a red line level with the top of the cab's windows."""
    line = 3.78
    z = p[..., 2]
    out = np.broadcast_to(code(UP_YELLOW), p.shape).copy()
    out[z > line] = code(UP_GRAY)
    out[(z > line - .045) & (z <= line) & (np.abs(n[..., 2]) < .5)] = code(UP_RED)
    return out


def ns_shell(p, n):
    return np.broadcast_to(code(NS_BLACK), p.shape).copy()


def white_shell(p, n):
    return np.ones_like(p)


def shield(cx, z, s=1.0):
    """A railroad shield: a blue head with its name, then red and white stripes coming to a point."""
    w, top, mid, shoulder, tip = .42 * s, .46 * s, .12 * s, -.14 * s, -.5 * s

    def low(x):
        ax = abs(x)
        return tip + (shoulder - tip) * ax / (w * .85) if ax < w * .85 else shoulder + (mid - shoulder) * (ax - w * .85) / (w * .15)

    layers = [{"where": "nose", "at": (cx, z), "poly": [(-w, mid), (w, mid), (w, top), (-w, top)], "color": "#1b2a5c"}]
    bars = 7
    for k in range(bars):
        x0, x1 = -w + 2 * w * k / bars, -w + 2 * w * (k + 1) / bars
        tipped = [(x0, low(x0)), (0, tip), (x1, low(x1))] if x0 * x1 < 0 else [(x0, low(x0)), (x1, low(x1))]
        layers.append({"where": "nose", "at": (cx, z), "poly": tipped + [(x1, mid), (x0, mid)], "color": UP_RED if k % 2 == 0 else "#ece9de"})
    layers.append({"where": "nose", "text": "UNION", "font": "sans", "at": (cx, z + .36 * s), "size": (.56 * s, .12 * s), "color": "#ece9de"})
    layers.append({"where": "nose", "text": "PACIFIC", "font": "sans", "at": (cx, z + .21 * s), "size": (.66 * s, .12 * s), "color": "#ece9de"})
    return layers


def number(color, road):
    """The road number on the cab's side, centred under its window, wherever that was measured to be."""
    return {"where": "side", "text": road, "font": "sans", "at": ("window", 2.62), "size": (1.35, .42), "color": color}


def speed_lines(color):
    """Five tapering bars running back from the logo, on each side."""
    layers = []
    for k in range(5):
        z = 3.72 - k * .17
        layers.append({"where": "side", "at": (2.9, z), "poly": [(-3.8 + k * .22, -.05), (-1.0, -.05), (-1.0, .05), (-3.8 + k * .22, .05)], "color": color})
    return layers


LIVERIES = {
    "white": {"shell": white_shell, "sill": "#ffffff", "rails": "#ffffff", "roofgear": "#ffffff", "trim": "#ffffff", "keep_art": True, "road": "1387",
              "stamps": [number("#101114", "1387")]},
    "csx": {"road": "6130", "weather": True, "shell": csx_shell, "sill": CSX_YELLOW, "rails": CSX_YELLOW, "roofgear": CSX_BLUE, "trim": CSX_BLUE,
            "stamps": [{"where": "side", "text": "CSX", "font": "serif", "at": ("vent", 3.0), "size": (3.8, 1.35), "color": CSX_YELLOW},
                       {"where": "nose", "text": "CSX", "font": "serif", "at": (0, 2.7), "size": (1.5, .55), "color": CSX_BLUE},
                       number(CSX_YELLOW, "6130")]},
    "up": {"road": "1535", "weather": True, "gear": UP_GRAY, "shell": up_shell, "sill": UP_GRAY, "rails": UP_GRAY, "roofgear": UP_GRAY, "trim": UP_GRAY, "frame": UP_GRAY,
           "stamps": [{"where": "side", "text": "UNION PACIFIC", "font": "sans", "at": (2.75, 3.2), "size": (8.0, .55), "color": UP_RED},
                      number(UP_RED, "1535")] + shield(0, 2.75)},
    "ns": {"road": "3074", "weather": True, "shell": ns_shell, "sill": NS_WHITE, "rails": NS_BLACK, "roofgear": NS_BLACK, "trim": NS_BLACK, "invert_ends": True,
           "stamps": speed_lines(NS_WHITE)
           + [{"where": "side", "text": "NS", "font": "sans", "at": (2.9, 3.38), "size": (1.5, .7), "color": NS_WHITE},
              {"where": "side", "text": "NORFOLK", "font": "sans", "at": (2.9, 2.82), "size": (2.3, .26), "color": NS_WHITE},
              {"where": "side", "text": "SOUTHERN", "font": "sans", "at": (2.9, 2.5), "size": (2.5, .26), "color": NS_WHITE},
              number(NS_WHITE, "3074")]},
}
# The liveries built for now; the others above are kept for later.
BUILT = {key: LIVERIES[key] for key in ("csx",)}
FONTS = {"serif": ["rockb.ttf", "georgiab.ttf", "timesbd.ttf"], "sans": ["arialbd.ttf", "segoeuib.ttf"]}


def clean(name):
    return re.sub(r"_\d+$", "", name)


def on_right_page(name):
    return name.startswith(("Body", "Cab", "Non_Mirrored", "Vent_Module")) and not name.startswith("Cab_Windows")


def low_fan_mesh(sides, white_uv, dark_uv):
    """Plain fans for the far-off levels: reducing the source's fine grilles shreds them. The same outline, a dark disc on top."""
    profile = ((.67, 0), (.66, .04), (.58, .06), (.56, .2), (.6, .21), (.6, .305), (.56, .305), (.56, .26))
    bm = bmesh.new()
    uv_layer = bm.loops.layers.uv.new("UVMap")
    for y in FAN_CENTRES:
        rows = [[bm.verts.new((r * math.cos(2 * math.pi * k / sides), y + r * math.sin(2 * math.pi * k / sides), FAN_BOTTOM + z)) for k in range(sides)] for r, z in profile]
        for a, b in zip(rows, rows[1:]):
            for k in range(sides):
                face = bm.faces.new((a[k], a[(k + 1) % sides], b[(k + 1) % sides], b[k]))
                for loop in face.loops:
                    loop[uv_layer].uv = white_uv
        disc = bm.faces.new(rows[-1])
        for loop in disc.loops:
            loop[uv_layer].uv = dark_uv
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    mesh = bpy.data.meshes.new("Exhaust_Fans")
    bm.to_mesh(mesh)
    bm.free()
    mesh.shade_smooth()
    mesh.set_sharp_from_angle(angle=math.radians(35))
    return mesh


def to_pages(mesh, name):
    """The body's UVs squeezed on to the left-hand page, with the shell's -X half moved to the right-hand one."""
    count = len(mesh.loops)
    uv = np.empty(count * 2, dtype=np.float32)
    mesh.uv_layers[0].data.foreach_get("uv", uv)
    uv = uv.reshape(-1, 2)
    uv[:, 0] *= .5
    if on_right_page(name):
        co = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
        mesh.vertices.foreach_get("co", co)
        x = co.reshape(-1, 3)[:, 0]
        verts = np.empty(count, dtype=np.int32)
        mesh.loops.foreach_get("vertex_index", verts)
        starts = np.empty(len(mesh.polygons), dtype=np.int32)
        totals = np.empty(len(mesh.polygons), dtype=np.int32)
        mesh.polygons.foreach_get("loop_start", starts)
        mesh.polygons.foreach_get("loop_total", totals)
        centre = np.add.reduceat(x[verts], starts) / totals
        uv[np.repeat(centre < -1e-3, totals), 0] += .5
    elif name.startswith("Vent_Grid"):
        # Its white shares space with the sill's, and the two can differ: all of it goes on the right-hand page, where the sill isn't.
        uv[:, 0] += .5
    mesh.uv_layers[0].data.foreach_set("uv", uv.ravel())


def load_source():
    """The source's meshes, one each, named for its node, in world space with their transforms applied; the body's on its two pages."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=SOURCE)
    bpy.context.view_layer.update()
    parts = {}
    for obj in [o for o in bpy.data.objects if o.type == "MESH"]:
        mesh = obj.data.copy()
        mesh.transform(obj.matrix_world)
        name = clean(obj.parent.name)
        if not name.startswith(RUNNING):
            to_pages(mesh, name)
        parts[name] = mesh
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    return parts


def atlas():
    """The base colour, emissive and roughness images as arrays, bottom row first as Blender holds them."""
    material = next(m for m in bpy.data.materials if m.use_nodes and "Principled BSDF" in m.node_tree.nodes)
    bsdf = material.node_tree.nodes["Principled BSDF"]

    def pixels(socket):
        node = bsdf.inputs[socket].links[0].from_node
        while not hasattr(node, "image"):
            node = node.inputs[0].links[0].from_node
        image = node.image
        w, h = image.size
        data = np.empty(w * h * 4, dtype=np.float32)
        image.pixels.foreach_get(data)
        return data.reshape(h, w, 4)

    return pixels("Base Color"), pixels("Emission Color"), pixels("Roughness")[..., 1]


def halve(pixels, height):
    while pixels.shape[0] > height:
        h, w = pixels.shape[0] // 2, pixels.shape[1] // 2
        pixels = pixels[:h * 2, :w * 2].reshape(h, 2, w, 2, -1).mean(axis=(1, 3))
    return pixels


def fill(target, a, b, c, value):
    """Set a triangle's cells in a 2D array to value, or (when value is None) to 1."""
    h, w = target.shape[:2]
    x0, x1 = max(int(min(a[0], b[0], c[0])), 0), min(int(max(a[0], b[0], c[0])) + 1, w - 1)
    y0, y1 = max(int(min(a[1], b[1], c[1])), 0), min(int(max(a[1], b[1], c[1])) + 1, h - 1)
    if x1 < x0 or y1 < y0:
        return None
    area = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0])
    if abs(area) < 1e-9:
        return None
    ys, xs = np.mgrid[y0:y1 + 1, x0:x1 + 1]
    px, py = xs + .5, ys + .5
    w1 = ((px - a[0]) * (c[1] - a[1]) - (py - a[1]) * (c[0] - a[0])) / area
    w2 = ((b[0] - a[0]) * (py - a[1]) - (b[1] - a[1]) * (px - a[0])) / area
    inside = (w1 >= -.02) & (w2 >= -.02) & (w1 + w2 <= 1.02)
    if not inside.any():
        return None
    return ys[inside], xs[inside], w1[inside], w2[inside]


def map_texels(meshes, width, height):
    """For every texel of the two pages: which group of the body it paints, where on the engine it lands, and which way it faces."""
    label = np.zeros((height, width), dtype=np.uint8)
    place = np.zeros((height, width, 3), dtype=np.float32)
    facing = np.zeros((height, width, 3), dtype=np.float32)
    mixed = np.zeros((height, width), dtype=bool)
    for group, starts in GROUPS:
        for name, mesh in meshes.items():
            if not name.startswith(starts) or name.startswith("Cab_Windows"):
                continue
            mesh.calc_loop_triangles()
            uv = np.empty(len(mesh.loops) * 2, dtype=np.float32)
            mesh.uv_layers[0].data.foreach_get("uv", uv)
            uv = uv.reshape(-1, 2) * (width, height)
            verts = np.empty(len(mesh.loops), dtype=np.int32)
            mesh.loops.foreach_get("vertex_index", verts)
            co = np.empty(len(mesh.vertices) * 3, dtype=np.float32)
            mesh.vertices.foreach_get("co", co)
            co = co.reshape(-1, 3) - (0, 0, RAIL)
            for tri in mesh.loop_triangles:
                i, j, k = tri.loops
                a, b, c = uv[i], uv[j], uv[k]
                hit = fill(label, a, b, c, None)
                if hit is None:
                    continue
                ys, xs, w1, w2 = hit
                pa, pb, pc = co[verts[i]], co[verts[j]], co[verts[k]]
                normal = np.cross(pb - pa, pc - pa)
                length = np.linalg.norm(normal)
                if length < 1e-9:
                    continue
                is_end = abs(normal[1]) / length > .6
                if group == SHELL:
                    # Texels the source uses both on an end face and elsewhere aren't treated as end faces.
                    earlier = (label[ys, xs] == SHELL) & ((np.abs(facing[ys, xs, 1]) > .6) != is_end)
                    mixed[ys[earlier], xs[earlier]] = True
                label[ys, xs] = group
                place[ys, xs] = pa + (pb - pa) * w1[:, None] + (pc - pa) * w2[:, None]
                facing[ys, xs] = normal / length
    facing[mixed, 1] = 0
    # The grille's bars are finer than a texel in places, so some cover no texel centre: paint all of its own rectangle.
    grid = np.empty(len(meshes["Vent_Grid"].loops) * 2, dtype=np.float32)
    meshes["Vent_Grid"].uv_layers[0].data.foreach_get("uv", grid)
    grid = grid.reshape(-1, 2)
    x0, y0 = (grid.min(axis=0) * (width, height)).astype(int) - 2
    x1, y1 = (grid.max(axis=0) * (width, height)).astype(int) + 3
    patch = label[y0:y1, x0:x1]
    patch[patch == 0] = ROOFGEAR
    # Spread each island a little, so texture filtering at its edges doesn't pick up unpainted texels.
    for _ in range(3):
        empty = label == 0
        for axis, step in ((0, 1), (0, -1), (1, 1), (1, -1)):
            near = np.roll(label, step, axis)
            take = empty & (near > 0) & (label == 0)
            ys, xs = np.nonzero(take)
            sy = (ys - step) % height if axis == 0 else ys
            sx = (xs - step) % width if axis == 1 else xs
            label[ys, xs] = label[sy, sx]
            place[ys, xs] = place[sy, sx]
            facing[ys, xs] = facing[sy, sx]
    return label, place, facing


def end_faces(place, facing):
    """The flat ends of the nose and the long hood, not the grilles' louvres between them."""
    return (np.abs(facing[..., 1]) > .6) & ((place[..., 1] < NOSE_END) | (place[..., 1] > HOOD_END))


def artwork_to_erase(label, place, facing, light):
    """The source's own paint scheme on the shell: its black bands and stripes, end chevrons and number. Seams and handles stay."""
    shell = label == SHELL
    y, z = place[..., 1], place[..., 2]
    dark = light < .45
    # Whole end faces, not just their dark texels, or the stripes' soft edges stay as faint lines.
    ends = shell & end_faces(place, facing)
    sides = shell & (np.abs(facing[..., 0]) > .7)
    # A height along the hood where most of the side is dark is a painted band, not a seam.
    hood = sides & (y > CAB[1])
    rows = np.clip(((z - 1.2) * 100).astype(np.int32), 0, 399)
    total = np.bincount(rows[hood], minlength=400)
    black = np.bincount(rows[hood & dark], minlength=400)
    banded = (black > .5 * np.maximum(total, 1)) & (total > 0)
    banded[int((2.45 - 1.2) * 100):] = False
    # A little more above and below, for the bands' soft edges.
    banded = np.convolve(banded, np.ones(5), "same") > 0
    bands = hood & banded[rows]
    # The panel just behind the cab has no door seams low down, only the source's stripes.
    bands |= hood & (y < -2.0) & (z < 2.5) & dark
    cab_number = sides & (y > CAB[0]) & (y < CAB[1]) & (z > 2.2) & (z < 3.0)
    cab_rows = sides & (y <= CAB[1]) & (z > 1.6) & (z < 2.45) & dark & banded[rows]
    return ends, bands | cab_rows, cab_number


def stamp_mask(stamp):
    """A stamp as a coverage mask: (mask, where it goes on the engine, its offset from there along the reading direction, cells a metre)."""
    scale = 240
    if "text" in stamp:
        curve = bpy.data.curves.new("Words", "FONT")
        curve.body = stamp["text"]
        curve.resolution_u = 6
        for name in FONTS[stamp["font"]]:
            path = os.path.join(os.environ.get("WINDIR", "C:/Windows"), "Fonts", name)
            if os.path.exists(path):
                curve.font = bpy.data.fonts.load(path, check_existing=True)
                break
        obj = bpy.data.objects.new("Words", curve)
        bpy.context.collection.objects.link(obj)
        evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
        mesh = evaluated.to_mesh()
        mesh.calc_loop_triangles()
        xy = np.array([(v.co.x, v.co.y) for v in mesh.vertices], dtype=np.float32)
        low, high = xy.min(axis=0), xy.max(axis=0)
        w, h = stamp["size"]
        xy = (xy - (low + high) / 2) / (high - low) * (w, h)
        tris = [[xy[i] for i in tri.vertices] for tri in mesh.loop_triangles]
        evaluated.to_mesh_clear()
        bpy.data.objects.remove(obj)
        bpy.data.curves.remove(curve)
    else:
        xy = np.array(stamp["poly"], dtype=np.float32)
        tris = [[xy[0], xy[k], xy[k + 1]] for k in range(1, len(xy) - 1)]
        low, high = xy.min(axis=0), xy.max(axis=0)
        centre = (low + high) / 2
        tris = [[p - centre for p in t] for t in tris]
        w, h = high - low
        stamp = dict(stamp, at=stamp["at"], off=centre)
    # Three samples a cell each way, for clean edges.
    fine = 3
    cols, rows = int(w * scale * fine) + 2, int(h * scale * fine) + 2
    mask = np.zeros((rows, cols), dtype=np.float32)
    k = np.array((scale * fine, scale * fine), dtype=np.float32)
    origin = np.array((cols / 2, rows / 2), dtype=np.float32)
    for tri in tris:
        hit = fill(mask, tri[0] * k + origin, tri[1] * k + origin, tri[2] * k + origin, None)
        if hit:
            mask[hit[0], hit[1]] = 1
    rows, cols = rows // fine, cols // fine
    mask = mask[:rows * fine, :cols * fine].reshape(rows, fine, cols, fine).mean(axis=(1, 3))
    # Soften to about a texel, so edges come out smooth rather than stepped at the texture's resolution.
    for axis in (0, 1):
        mask = sum(np.roll(mask, k, axis) for k in range(-1, 2)) / 3
    return mask, stamp["at"], stamp.get("off", (0, 0)), scale


def paint(key, livery, base, glow, label, place, facing):
    """A livery's two pages, from the source's atlas (one page's worth, repeated)."""
    pages = np.concatenate((base, base), axis=1)
    glows = np.concatenate((glow, glow), axis=1)
    rgb = pages[..., :3]
    light = rgb.mean(axis=2)
    grey = np.clip(1 - (rgb.max(axis=2) - rgb.min(axis=2)) * 8, 0, 1)
    t = np.clip((light - .45) / .25, 0, 1)
    whiteness = t * t * (3 - 2 * t) * grey * (1 - np.clip(glows[..., :3].max(axis=2) * 4, 0, 1))
    height, width = light.shape
    out = rgb.copy()

    ends, bands, cab_number = artwork_to_erase(label, place, facing, light)
    shell = label == SHELL
    shade = np.clip(light / .92, 0, 1)
    if livery.get("keep_art"):
        shade[cab_number] = 1
    else:
        shade[ends | bands | cab_number] = 1
    ys, xs = np.nonzero(shell)
    colour = livery["shell"](place[ys, xs], facing[ys, xs])
    out[ys, xs] = colour * shade[ys, xs, None]
    if livery.get("invert_ends"):
        faces = shell & end_faces(place, facing)
        white = code(NS_WHITE)
        out[faces] = white * (1 - np.clip(light[faces] / .92, 0, 1))[:, None] + code(NS_BLACK) * np.clip(light[faces] / .92, 0, 1)[:, None]

    for group, name in ((SILL, "sill"), (RAILS, "rails"), (TRIM, "trim")):
        where = (label == group) & (whiteness > 0)
        tint = code(livery[name])
        out[where] = rgb[where] * (1 - whiteness[where, None]) + (tint * light[where, None]) * whiteness[where, None]
    # The fans and grilles share atlas space with the lamps, so they aren't held back by the lamps' mask.
    where = label == ROOFGEAR
    out[where] = code(livery["roofgear"]) * light[where, None]
    if "frame" in livery:
        # The frame's dark sides, for paints that colour it; the walk on top stays dark.
        where = (label == SILL) & (whiteness == 0) & (facing[..., 2] < .7) & (light > .05)
        out[where] = code(livery["frame"]) * np.clip(light[where] / .17, 0, 1)[:, None]

    for stamp in livery["stamps"]:
        mask, (cx, cz), off, scale = stamp_mask(stamp)
        cx = {"window": WINDOW_Y, "vent": VENT_Y}.get(cx, cx)
        tint = code(stamp["color"])
        rows, cols = mask.shape
        if stamp["where"] == "side":
            faces = [(1, (label == SHELL) & (facing[..., 0] > .7)), (-1, (label == SHELL) & (facing[..., 0] < -.7))]
        else:
            faces = [(0, (label == SHELL) & (facing[..., 1] < -.6) & (place[..., 1] < NOSE_END))]
        for sign, where in faces:
            ys, xs = np.nonzero(where & shell)
            p = place[ys, xs]
            # Reading left to right on either side: the two sides run opposite ways along the engine.
            u = (sign * (p[:, 1] - cx) if sign else p[:, 0] - cx) - off[0]
            v = p[:, 2] - cz - off[1]
            ci = np.floor(u * scale + cols / 2).astype(np.int32)
            ri = np.floor(v * scale + rows / 2).astype(np.int32)
            ok = (ci >= 0) & (ci < cols) & (ri >= 0) & (ri < rows)
            ys, xs, ci, ri = ys[ok], xs[ok], ci[ok], ri[ok]
            m = mask[ri, ci][:, None]
            out[ys, xs] = out[ys, xs] * (1 - m) + tint * shade[ys, xs, None] * m
    # The number boards: one blank for all of them, with this engine's number on it, read from its own face.
    if livery["road"] != "1387":
        ys, xs = np.nonzero(label == BOARD)
        p, f = place[ys, xs], facing[ys, xs]
        centre = (p.min(axis=0) + p.max(axis=0)) / 2
        across = np.cross(-f, np.array((0, 0, 1), dtype=np.float32))
        across /= np.maximum(np.linalg.norm(across, axis=1, keepdims=True), 1e-6)
        u = ((p - centre) * across).sum(axis=1)
        v = p[:, 2] - centre[2]
        inner = (np.abs(u) < .29) & (np.abs(v) < .095)
        out[ys[inner], xs[inner]] = np.array((.94, .94, .91), dtype=np.float32)
        mask, _, _, scale = stamp_mask({"text": livery["road"], "font": "sans", "at": (0, 0), "size": (.5, .15)})
        rows, cols = mask.shape
        ci = np.floor(u * scale + cols / 2).astype(np.int32)
        ri = np.floor(v * scale + rows / 2).astype(np.int32)
        ok = (ci >= 0) & (ci < cols) & (ri >= 0) & (ri < rows)
        m = mask[ri[ok], ci[ok]][:, None]
        out[ys[ok], xs[ok]] = out[ys[ok], xs[ok]] * (1 - m) + np.array((.03, .03, .04), dtype=np.float32) * m

    if livery.get("weather"):
        # Dirt rising from the sill, soft streaks running down the sides, soot on the roof round the exhaust.
        rng = np.random.default_rng(1387)
        ys, xs = np.nonzero(shell)
        p, f = place[ys, xs], facing[ys, xs]
        stations = np.arange(-11, 11, .12)
        streaks = np.convolve(rng.random(stations.size), np.ones(3) / 3, "same")
        streak = np.interp(p[:, 1] + p[:, 0], stations, streaks)
        low = np.clip((2.5 - p[:, 2]) / 1.0, 0, 1)
        grime = .05 + .16 * low + .10 * streak * np.clip((p[:, 2] - 1.6) / 2.4, 0, 1)
        grime += .28 * (f[:, 2] > .5) * np.exp(-((p[:, 1] - 2.8) / 3.5) ** 2)
        out[ys, xs] *= (1 - np.clip(grime, 0, .55))[:, None]
    print("painted", key)
    alpha = np.ones((height, width, 1), dtype=np.float32)
    return (np.concatenate((out, alpha), axis=2).clip(0, 1) * 255).astype(np.uint8)


def blur(values, radius):
    """A box blur, twice over, along both axes."""
    size = radius * 2 + 1
    for _ in range(2):
        for axis in (0, 1):
            total = np.cumsum(np.insert(values, 0, 0, axis=axis), axis=axis, dtype=np.float64)
            end = np.take(total, np.minimum(np.arange(values.shape[axis]) + radius + 1, values.shape[axis]), axis=axis)
            start = np.take(total, np.maximum(np.arange(values.shape[axis]) - radius, 0), axis=axis)
            shape = [1, 1]
            shape[axis] = -1
            count = np.minimum(np.arange(values.shape[axis]) + radius + 1, values.shape[axis]) - np.maximum(np.arange(values.shape[axis]) - radius, 0)
            values = ((end - start) / count.reshape(shape)).astype(np.float32)
    return values


def texels_of(meshes, starts, width, height):
    """Which texels the named parts use."""
    mask = np.zeros((height, width), dtype=bool)
    for name, mesh in meshes.items():
        if not name.startswith(starts):
            continue
        mesh.calc_loop_triangles()
        uv = np.empty(len(mesh.loops) * 2, dtype=np.float32)
        mesh.uv_layers[0].data.foreach_get("uv", uv)
        uv = uv.reshape(-1, 2) * (width, height)
        for tri in mesh.loop_triangles:
            i, j, k = tri.loops
            hit = fill(mask, uv[i], uv[j], uv[k], None)
            if hit is not None:
                mask[hit[0], hit[1]] = True
    return mask


def finish_pixels(rough, metal):
    """Blue metalness and green roughness, as glTF packs them."""
    one = np.ones_like(rough)
    return np.stack((one, rough, metal, one), axis=2)


def body_finish(rough_atlas, meshes, label, place, facing, light):
    """The body's two pages of roughness and metalness: painted steel, weathered lower down, with the bare parts set apart."""
    height, width = label.shape
    rough = np.maximum(np.concatenate((rough_atlas, rough_atlas), axis=1), .3)
    metal = np.zeros_like(rough)
    shell = label == SHELL
    x, y, z = place[..., 0], place[..., 1], place[..., 2]
    worn = np.sin(y * 1.7 + z * 3.1) * np.sin(y * .6 - z * 2.3 + x * 1.1)
    rough[shell] = np.clip(rough[shell] + .08 * worn[shell] + .12 * np.clip(1 - (z[shell] - 1.2) / 2.5, 0, 1), 0, 1)
    for starts, how_metal, how_rough in BARE:
        where = texels_of(meshes, starts, width, height)
        rough[where] = how_rough
        metal[where] = how_metal
    rough[texels_of(meshes, GLASS, width, height) & (light < .15)] = .08
    # The fans and grilles share atlas space with the lamps, and they're painted.
    metal[label == ROOFGEAR] = 0
    return finish_pixels(rough, metal)


def body_bumps(label, place, facing, light):
    """The shell's seams and hinges, which are thin dark lines in its base colour, as dips. Not the source's paint scheme."""
    lines = np.clip(blur(light, 6) - light, 0, .5)
    ends, bands, cab_number = artwork_to_erase(label, place, facing, light)
    lines[(label != SHELL) | ends | bands | cab_number] = 0
    dx = (np.roll(lines, -1, axis=1) - np.roll(lines, 1, axis=1)) * .5 * SEAM_HEIGHT
    dy = (np.roll(lines, -1, axis=0) - np.roll(lines, 1, axis=0)) * .5 * SEAM_HEIGHT
    # Lines are dips, so the surface leans towards where they get deeper.
    length = np.sqrt(dx * dx + dy * dy + 1)
    return np.stack((dx / length * .5 + .5, dy / length * .5 + .5, 1 / length * .5 + .5, np.ones_like(dx)), axis=2)


def data_image(name, pixels):
    h, w = pixels.shape[:2]
    image = bpy.data.images.new(name, w, h, alpha=False)
    image.colorspace_settings.name = "Non-Color"
    image.pixels.foreach_set(pixels.astype(np.float32).ravel())
    image.pack()
    return image


def make_material(name, pixels, finish=None, bumps=None):
    h, w = pixels.shape[:2]
    image = bpy.data.images.new(name, w, h, alpha=False)
    image.pixels.foreach_set(pixels.astype(np.float32).ravel())
    image.pack()
    mat = bpy.data.materials.new("SD40")
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    tex = nodes.new("ShaderNodeTexImage")
    tex.image = image
    bsdf = nodes["Principled BSDF"]
    mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Metallic"].default_value = 0
    bsdf.inputs["Roughness"].default_value = .6
    if finish is not None:
        map_node = nodes.new("ShaderNodeTexImage")
        map_node.image = data_image(f"{name}_finish", finish)
        split = nodes.new("ShaderNodeSeparateColor")
        mat.node_tree.links.new(map_node.outputs["Color"], split.inputs["Color"])
        mat.node_tree.links.new(split.outputs["Green"], bsdf.inputs["Roughness"])
        mat.node_tree.links.new(split.outputs["Blue"], bsdf.inputs["Metallic"])
    if bumps is not None:
        map_node = nodes.new("ShaderNodeTexImage")
        map_node.image = data_image(f"{name}_normal", bumps)
        normal = nodes.new("ShaderNodeNormalMap")
        mat.node_tree.links.new(map_node.outputs["Color"], normal.inputs["Color"])
        mat.node_tree.links.new(normal.outputs["Normal"], bsdf.inputs["Normal"])
    return mat


def reduce(obj, lod):
    """Bring a dense part down; its normals are then worked out afresh, keeping edges sharp."""
    name = obj.name
    tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    keep, ratio = lod["keep"], lod["ratio"]
    if any(name == s for s in SHAPE):
        keep, ratio = max(keep, 3000), max(ratio, .6)
    for start, floor in lod["floors"].items():
        if name.startswith(start):
            ratio = max(ratio, floor)
    if tris <= keep or ratio >= 1 or (lod["suffix"] and name == "Exhaust_Fans"):
        return
    bpy.context.view_layer.objects.active = obj
    mod = obj.modifiers.new("Reduce", "DECIMATE")
    mod.ratio = max(ratio, keep / tris)
    bpy.ops.object.modifier_apply(modifier=mod.name)
    mesh = obj.data
    if "custom_normal" in mesh.attributes:
        mesh.attributes.remove(mesh.attributes["custom_normal"])
    mesh.shade_smooth()
    mesh.set_sharp_from_angle(angle=math.radians(35))


def write(path, objects, mat):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.data.materials.clear()
        obj.data.materials.append(mat)
        obj.select_set(True)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=False, export_yup=True, export_materials="EXPORT",
                              export_normals=True, export_texcoords=True, export_image_format="AUTO", export_animations=False)
    tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in objects)
    print("wrote", path, tris, "triangles", len(objects), "parts", os.path.getsize(path) // 1024, "KB")
    return tris


def build(lod, base, painted_pages, budget, finish, bumps, gear_finish):
    meshes = load_source()
    bpy.ops.object.select_all(action="DESELECT")
    gear_pixels = halve(base, lod["gear_tex"])

    def part(name, move, x_scale=1.0):
        mesh = meshes[name]
        mesh.transform(Matrix.Diagonal((x_scale, 1, 1, 1)) @ Matrix.Translation(move))
        obj = bpy.data.objects.new(name, mesh)
        bpy.context.collection.objects.link(obj)
        reduce(obj, lod)
        return obj

    if lod["suffix"]:
        # All five fans as one plain mesh in place of the source's two.
        del meshes["Exhaust_Fans.001"]
        meshes["Exhaust_Fans"] = low_fan_mesh(32 if lod["suffix"] == "_mid" else 16, FAN_WHITE, FAN_DARK)

    def wanted(name):
        return not any(name.startswith(d) for d in lod["drop"])

    groups = {"body": [], "truck": [], "wheelset": [], "coupler": []}
    for name in meshes:
        if not name.startswith(RUNNING) and wanted(name):
            groups["body"].append(part(name, (0, 0, -RAIL)))
    for name in meshes:
        if name.startswith(("Bogee_", "Brakes", "Suspension")) and "_F" in name and wanted(name):
            groups["truck"].append(part(name, (0, TRUCK_Y, -RAIL)))
    groups["wheelset"].append(part("Wheel_F.001", (0, TRUCK_Y, -AXLE_Z), GAUGE_X / FLANGE_X))
    groups["coupler"].append(part("Coupling.001", (0, COUPLER_Y, -COUPLER_Z)))

    folder = lod["folder"]
    for key, pages in painted_pages.items():
        budget[f"body{folder}"] = write(os.path.join(OUT, folder, f"body_{key}.glb"), groups["body"], make_material(
            f"SD40_{key}", halve(pages.astype(np.float32) / 255, lod["page"]), halve(finish, lod["page"] // 2), halve(bumps, lod["page"] // 2)))
    gear_map = halve(gear_finish, lod["gear_tex"] // 4)
    gear = make_material("SD40_gear", gear_pixels, gear_map)
    for key in ("truck", "wheelset"):
        budget[f"{key}{folder}"] = write(os.path.join(OUT, folder, f"{key}.glb"), groups[key], gear)
    budget[f"coupler{folder}"] = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in groups["coupler"])
    if folder == "LOD0":
        # The shared coupler, at full detail on everything.
        write(os.path.join(COUPLERS, "coupler.glb"), groups["coupler"], gear)
        reach = -min(v.co.y for o in groups["coupler"] for v in o.data.vertices)
        with open(os.path.join(COUPLERS, "coupler.json"), "w") as f:
            # knuckleNose: measured from this mesh in plan (the hook's rear face, which bears on the mating knuckle's, is 9.8 cm
            # behind its nose), so two coupled heads overlap by twice it. The game spaces vehicles by it (Trains/Vehicles.cs).
            json.dump({"model": "coupler.glb", "height": round(COUPLER_Z - RAIL, 4), "reach": round(reach, 4), "knuckleNose": KNUCKLE_NOSE,
                       "axes": "origin at the draft pivot, swinging about Z, pulling face towards -Y; height is the pivot above the railhead, "
                               "reach the pivot to the knuckle's nose (the coupler's furthest point, where each model's length over couplers "
                               "ends), knuckleNose how far that nose stands past the pulling face: the rear face of the knuckle's hook, which "
                               "bears on the mating knuckle's when coupled, so two coupled heads overlap by twice this (measured from the mesh "
                               "in plan). Blender Z-up metres; in the file, glTF Y-up"}, f, indent=2)
        with open(os.path.join(COUPLERS, "CREDITS.txt"), "w") as f:
            f.write("""The coupler is from "EMD-SD40 Diesel Electric Locomotive" by Heataker
https://sketchfab.com/3d-models/emd-sd40-diesel-electric-locomotive-7271826d3a584a22849c949a7755f79d
Licensed under Creative Commons Attribution 4.0: http://creativecommons.org/licenses/by/4.0/

Taken out of the model and moved to its own origin by Tools/Blender/sd40.py; every engine and car in the game uses it.
""")
    # Liveries with coloured trucks: the same dark casting, in their colour.
    for key, livery in BUILT.items():
        if "gear" in livery:
            tinted = gear_pixels.copy()
            tinted[..., :3] = code(livery["gear"]) * np.clip(gear_pixels[..., :3].mean(axis=2) / .17, 0, 1.3)[..., None]
            write(os.path.join(OUT, folder, f"truck_{key}.glb"), groups["truck"], make_material(f"SD40_gear_{key}", tinted, gear_map))


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

    root = holder("SD40")
    load(f"LOD0/body_{key}.glb", holder("Body", parent=root), "Body")
    for label, y, yaw in (("Front", -TRUCK_Y, 0.0), ("Rear", TRUCK_Y, math.pi)):
        truck_at = holder(f"Truck_{label}", (0, y, 0), yaw, root)
        load(f"LOD0/truck_{key}.glb" if "gear" in LIVERIES[key] else "LOD0/truck.glb", truck_at, f"Truck_{label}")
        for k, a in enumerate(AXLES, 1):
            load("LOD0/wheelset.glb", holder(f"Wheelset_{label}_{k}", (0, a, TREAD_R), 0.0, truck_at), f"Wheelset_{label}_{k}")
    for label, y, yaw in (("Front", -COUPLER_Y, 0.0), ("Rear", COUPLER_Y, math.pi)):
        load(os.path.join("..", "..", "Couplers", "coupler.glb"), holder(f"Coupler_{label}", (0, y, COUPLER_Z - RAIL), yaw, root), f"Coupler_{label}")
    bpy.context.view_layer.update()
    folder = os.path.join(OUT, "Assembled")
    os.makedirs(folder, exist_ok=True)
    for obj in bpy.data.objects:
        obj.select_set(True)
    bpy.ops.export_scene.gltf(filepath=os.path.join(folder, f"sd40_{key}.glb"), export_format="GLB", use_selection=True, export_yup=True,
                              export_materials="EXPORT", export_normals=True, export_texcoords=True, export_animations=False)
    bpy.ops.file.pack_all()
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(folder, f"sd40_{key}.blend"), check_existing=False)
    print("wrote", folder, key, len(bpy.data.objects), "objects")


bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=SOURCE)
base_pixels, glow_pixels, rough_pixels = atlas()
# The base colour comes down to the largest page used, then the emissive mask is matched to it.
top = max(lod["page"] for lod in LODS)
base_pixels, glow_pixels, rough_pixels = halve(base_pixels, top), halve(glow_pixels, top), halve(rough_pixels[..., None], top)[..., 0]
if glow_pixels.shape[0] < top:
    k = top // glow_pixels.shape[0]
    glow_pixels = glow_pixels.repeat(k, axis=0).repeat(k, axis=1)

source_meshes = load_source()
# The cab's side window, from its own mesh: the number is centred under it.
windows = source_meshes["Cab_Windows"]
ys = sorted({round(windows.vertices[i].co.y, 3) for poly in windows.polygons if poly.normal.x > .7 and poly.center.x > 0 for i in poly.vertices})
runs = [[ys[0], ys[0]]]
for y in ys[1:]:
    if y - runs[-1][1] > .08:
        runs.append([y, y])
    runs[-1][1] = y
# Going back along the cab: the small front glass, then the side window's front frame, middle post and rear frame.
WINDOW_Y = (runs[-3][0] + runs[-1][1]) / 2
print("glass along the cab", runs, "side window centre", round(WINDOW_Y, 3))
# The logo on the CSX hood is centred on the vent module.
vent_ys = [v.co.y for v in source_meshes["Vent_Module"].vertices]
VENT_Y = (min(vent_ys) + max(vent_ys)) / 2
print("vent module", round(min(vent_ys), 3), round(max(vent_ys), 3), "centre", round(VENT_Y, 3))
# Where on the atlas the fans' white and dark are, for their plain versions.
fan = source_meshes["Exhaust_Fans"]
fan_uv = np.empty(len(fan.loops) * 2, dtype=np.float32)
fan.uv_layers[0].data.foreach_get("uv", fan_uv)
fan_uv = fan_uv.reshape(-1, 2)
fan_light = base_pixels[(fan_uv[:, 1] * top).astype(int).clip(0, top - 1), (fan_uv[:, 0] * 2 * top).astype(int).clip(0, top - 1), :3].mean(axis=1)
FAN_WHITE, FAN_DARK = tuple(fan_uv[fan_light.argmax()]), tuple(fan_uv[fan_light.argmin()])
texel_label, texel_place, texel_facing = map_texels(source_meshes, top * 2, top)
pages_by_livery = {key: paint(key, livery, base_pixels, glow_pixels, texel_label, texel_place, texel_facing) for key, livery in BUILT.items()}
texel_light = np.concatenate((base_pixels, base_pixels), axis=1)[..., :3].mean(axis=2)
body_finish_pixels = body_finish(rough_pixels, source_meshes, texel_label, texel_place, texel_facing, texel_light)
body_bump_pixels = body_bumps(texel_label, texel_place, texel_facing, texel_light)
gear_rough = np.full_like(rough_pixels, GEAR_ROUGH)
gear_metal = np.zeros_like(gear_rough)
for starts, how_metal, how_rough in GEAR_BARE:
    where = texels_of(source_meshes, starts, top, top)
    gear_rough[where] = how_rough
    gear_metal[where] = how_metal
gear_finish_pixels = finish_pixels(gear_rough, gear_metal)
del texel_label, texel_place, texel_facing, texel_light

budget = {}
for level in LODS:
    build(level, base_pixels, pages_by_livery, budget, body_finish_pixels, body_bump_pixels, gear_finish_pixels)

over = {}
for level in LODS:
    name = level["folder"]
    over[name] = budget[f"body{name}"] + 2 * budget[f"truck{name}"] + 6 * budget[f"wheelset{name}"] + 2 * budget[f"coupler{name}"]
print("whole engine triangles", over)

# Every level has its folder from the start, though only those in LODS have models yet.
for level in LODS + LATER_LODS:
    folder = os.path.join(OUT, level["folder"])
    os.makedirs(folder, exist_ok=True)
    if level in LATER_LODS:
        open(os.path.join(folder, ".gitkeep"), "a").close()

assembly = {
    "source": "EMD-SD40 Diesel Electric Locomotive by Heataker, CC BY 4.0",
    "lengthOverCouplers": round(2 * (10.2187), 3),
    "lods": [level["folder"] for level in LODS],
    "trianglesWholeEngine": over,
    "models": "Each model below is <lod>/<model>.glb, except the couplers: Models/Couplers/coupler.glb, the same on every engine and car",
    "bodies": [f"body_{key}" for key in BUILT],
    "truckByBody": {f"body_{key}": f"truck_{key}" for key, livery in BUILT.items() if "gear" in livery},
    "trucks": [{"model": "truck", "position": [0, -TRUCK_Y, 0], "yaw": 0}, {"model": "truck", "position": [0, TRUCK_Y, 0], "yaw": 180}],
    "wheelsets": {"model": "wheelset", "onTruck": [[0, a, TREAD_R] for a in AXLES], "radius": TREAD_R},
    "couplers": [{"model": "Couplers/coupler.glb", "position": [0, -COUPLER_Y, round(COUPLER_Z - RAIL, 4)], "yaw": 0},
                 {"model": "Couplers/coupler.glb", "position": [0, COUPLER_Y, round(COUPLER_Z - RAIL, 4)], "yaw": 180}],
    "axes": "Blender Z-up metres, front towards -Y; in the files, glTF Y-up: (x, y, z) here is (x, z, -y) there",
}
with open(os.path.join(OUT, "assembly.json"), "w") as f:
    json.dump(assembly, f, indent=2)

with open(os.path.join(OUT, "CREDITS.txt"), "w") as f:
    f.write("""EMD-SD40 Diesel Electric Locomotive by Heataker
https://sketchfab.com/3d-models/emd-sd40-diesel-electric-locomotive-7271826d3a584a22849c949a7755f79d
Licensed under Creative Commons Attribution 4.0: http://creativecommons.org/licenses/by/4.0/

The source files are in Source/, unchanged.
The other models in these folders are made from it by Tools/Blender/sd40.py. Changes: split into body, truck, wheelset and coupler (the coupler now in Models/Couplers);
moved so the rail is at zero; wheelsets narrowed to standard gauge; textures re-laid so the two sides have their own space;
the original paint scheme replaced by a new livery; metalness set by part; a normal map raised from the base colour's seams.

Railroad names in the liveries are their owners' trademarks.
""")

for key in BUILT:
    combined(key)
