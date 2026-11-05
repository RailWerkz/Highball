"""Builds Highball's EMD GP40-2 in Blender and exports it for the game.

    blender -b --factory-startup --python Tools/Blender/gp40_2.py -- <Content folder> [preview folder]

Writes under Models/Locomotives/GP40-2: body_csx_blue.glb, body_csx_gray.glb, body_up.glb (one body, three paints),
truck.glb, wheelset.glb, coupler.glb, fan.glb and assembly.json, which says where each part goes on the body.
Assembled/ holds the whole engine in each paint, as .glb and .blend, for looking at; the game loads the separate parts.

Blender is Z-up, metres; the glTF exporter turns that into the game's Y-up. The short hood faces -Y. Every object is one
paint and is named for it, with a material of the same name.
    body      origin on the railhead, midway between the trucks
    truck     origin on the railhead at the bolster's centre, turning about Z
    wheelset  origin at the axle's centre, rolling about X
    coupler   origin at the draft pivot, swinging about Z, pulling face towards -Y; Moving_Knuckle opens about its pin
    fan       origin at the hub, spinning about Z
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
PREVIEW = os.path.abspath(args[1]) if len(args) > 1 else None
OUT = os.path.join(CONTENT, "Models", "Locomotives", "GP40-2")

# Dimensions. 59'2" over the couplers, 9' truck wheelbase, 40" wheels, 15'5" high.
OVER_COUPLERS = 18.034
TRUCK_CENTRE = 5.182  # half of 34' between truck centres
AXLE = 1.372  # half of the 9' wheelbase
WHEEL_R = 0.508
COUPLER_Z = 0.876  # 34.5" to the coupler's centre
COUPLER_REACH = 1.2  # draft pivot to pulling face
COUPLER_PIVOT = OVER_COUPLERS / 2 - COUPLER_REACH
KNUCKLE_PIN = (0.105, -0.93)
FRAME_END = 8.53
FRAME_X = 1.525
# Heights and stations measured off side and head-on references, scaled to 15'5" and 59'2".
DECK = 1.5
SILL_LOW = 1.22  # the tops of the trucks come up to just under this
HOOD_X = 0.92
HOOD_TOP = 4.33
HOOD_SPLIT = 3.75
FORWARD_SPLIT = 3.5  # ahead of the jog, the roof colour comes down to the cab's line
JOG = (-1.65, -1.3)
RECESS = 0.07  # how far the grilles sit in from the hood's side
GRILLES = ((-3.64, -2.0, 3.59, 4.31), (.06, 1.97, 3.78, 4.17), (3.11, 7.51, 3.46, 4.12))
BLISTER_TOP = 4.565
NOSE_X = 0.95
NOSE_TOP = 3.25
NOSE_FRONT = -8.14
BOX_TOP = 2.18
BOX_ENDS = (-7.39, -3.58)  # the boxes beside the cab; ahead of them is the front porch
CAB_FRONT = -6.1
CAB_REAR = -4.1
EAVE = 3.79
CAB_TOP = 4.33
HOOD_REAR = 8.06
STAIR = (7.75, 8.4)
END_RAIL = 2.6
RAIL_TOP = 2.44
RADIATOR_FANS = (4.2, 5.46, 6.76)
FAN_Z = 4.46
BRAKE_FAN = 1.0
BRAKE_FAN_Z = 4.46


def srgb(code):
    """A #rrggbb paint as linear colour."""
    out = []
    for at in (1, 3, 5):
        c = int(code[at:at + 2], 16) / 255
        out.append(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4)
    return tuple(out)


# Paint: colour, metallic, roughness.
COMMON = {"Frame": ("#17181b", 0, .6), "Deck": ("#2b2c2e", 0, .85), "Truck": ("#1b1b1d", 0, .7), "Tank": ("#17181b", 0, .55),
          "Pilot": ("#17181b", 0, .55), "Sill": ("#17181b", 0, .55), "Steel": ("#74787c", 1, .45), "Tread": ("#a2a5a8", 1, .3),
          "Dark": ("#08090a", 0, .8), "Glass": ("#22303a", 0, .08), "Lamp": ("#f3f0dc", 0, .15), "Rubber": ("#0d0d0e", 0, .9),
          "Board": ("#e6e4d8", 0, .4), "BoardText": ("#08090a", 0, .6), "Red": ("#a4161e", 0, .3), "ShieldBlue": ("#1b2a5c", 0, .4), "ShieldWhite": ("#ece9de", 0, .4),
          "Moving_Knuckle": ("#4a4540", 0, .7), "Coupler": ("#4a4540", 0, .7), "Blade": ("#3a3c3f", .6, .5)}
BLUE, YELLOW, GRAY = "#264563", "#e8ac28", "#8d9297"
UP_YELLOW, UP_GRAY, UP_RED = "#f0b400", "#7e8388", "#b3172a"
LIVERIES = {
    "csx_blue": {"paint": {"Hood": BLUE, "HoodRoof": BLUE, "Cab": BLUE, "CabRoof": "#c7ccd0", "CabHeader": BLUE, "Pin": BLUE, "Nose": BLUE, "NoseFront": YELLOW,
                           "Stripe": YELLOW, "Rail": YELLOW, "Logo": YELLOW, "Number": YELLOW, "Plate": BLUE},
                 "logo": ("CSX", "serif", 3.7, 1.42, 1.15, 2.7), "number": "6130", "plate": "CSX"},
    "csx_gray": {"paint": {"Hood": GRAY, "HoodRoof": GRAY, "Cab": BLUE, "CabRoof": BLUE, "CabHeader": BLUE, "Pin": GRAY, "Nose": BLUE, "NoseFront": YELLOW,
                           "Stripe": YELLOW, "Rail": "#b9bcbe", "Logo": BLUE, "Number": YELLOW, "Plate": BLUE},
                 "logo": ("CSX", "serif", 3.7, 1.42, 1.15, 2.7), "number": "6942", "plate": "CSX"},
    "up": {"paint": {"Hood": UP_YELLOW, "HoodRoof": UP_GRAY, "Cab": UP_YELLOW, "CabRoof": UP_GRAY, "CabHeader": UP_GRAY, "Pin": UP_RED, "Nose": UP_YELLOW, "NoseFront": UP_YELLOW,
                     "Stripe": UP_GRAY, "Frame": UP_GRAY, "Tank": UP_GRAY, "Rail": UP_GRAY, "Logo": UP_RED, "Number": UP_RED, "Plate": UP_RED, "Board": "#15171a", "BoardText": "#ece9de", "Sill": UP_GRAY, "Pilot": UP_GRAY, "Deck": UP_GRAY},
           "logo": ("UNION PACIFIC", "sans", 6.25, .5, 1.18, 2.97), "number": "1535", "plate": "shield"},
}
FONTS = {"serif": ["rockb.ttf", "georgiab.ttf", "timesbd.ttf"], "sans": ["arialbd.ttf", "segoeuib.ttf"]}


def rrect(x0, x1, z0, z1, rt=0.0, rb=0.0, segs=4):
    """A rectangle, its top and bottom corners rounded, counter-clockwise."""
    pts = []

    def corner(cx, cz, r, a0):
        if r <= 0:
            pts.append((cx, cz))
            return
        for k in range(segs + 1):
            t = a0 + math.pi / 2 * k / segs
            pts.append((cx + r * math.cos(t), cz + r * math.sin(t)))

    corner(x1 - rb, z0 + rb, rb, -math.pi / 2)
    corner(x1 - rt, z1 - rt, rt, 0.0)
    corner(x0 + rt, z1 - rt, rt, math.pi / 2)
    corner(x0 + rb, z0 + rb, rb, math.pi)
    return pts


def circle(cx, cz, rx, rz=None, sides=20):
    rz = rx if rz is None else rz
    return [(cx + rx * math.cos(2 * math.pi * k / sides), cz + rz * math.sin(2 * math.pi * k / sides)) for k in range(sides)]


def perpendicular(d):
    other = Vector((0, 0, 1)) if abs(d.z) < 0.9 else Vector((1, 0, 0))
    return d.cross(other).normalized()


class Kit:
    def __init__(self):
        self.parts = {}

    def bm(self, role):
        if role not in self.parts:
            self.parts[role] = bmesh.new()
        return self.parts[role]

    def hexa(self, role, c):
        """Eight corners: four round the bottom, then the same four round the top."""
        bm = self.bm(role)
        v = [bm.verts.new(p) for p in c]
        faces = []
        for q in ((0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)):
            faces.append(bm.faces.new([v[i] for i in q]))
        return v, faces

    def box(self, role, lo, hi, bevel=0.0):
        (x0, y0, z0), (x1, y1, z1) = lo, hi
        v, faces = self.hexa(role, ((x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)))
        if bevel > 0:
            bm = self.bm(role)
            edges = list({e for f in faces for e in f.edges})
            bmesh.ops.bevel(bm, geom=edges, offset=bevel, segments=1, affect="EDGES", profile=0.5)

    def rings(self, role, rings, closed_ends=True):
        """Skin rings of equal size, capped."""
        bm = self.bm(role)
        vs = [[bm.verts.new(p) for p in ring] for ring in rings]
        for a, b in zip(vs, vs[1:]):
            n = len(a)
            for k in range(n):
                bm.faces.new((a[k], a[(k + 1) % n], b[(k + 1) % n], b[k]))
        if closed_ends:
            bm.faces.new(vs[0])
            bm.faces.new(list(reversed(vs[-1])))

    def loft(self, role, stations, axis="y", warp=None):
        """Profiles strung along an axis. y: profiles are (x, z). x: (y, z). z: (x, y)."""
        rings = []
        for at, profile in stations:
            ring = []
            for a, b in profile:
                p = {"y": (a, at, b), "x": (at, a, b), "z": (a, b, at)}[axis]
                ring.append(warp(*p) if warp else p)
            rings.append(ring)
        self.rings(role, rings)

    def prism(self, role, profile, axis, lo, hi, warp=None):
        self.loft(role, [(lo, profile), (hi, profile)], axis, warp)

    def cyl(self, role, p0, p1, r, sides=16, r1=None):
        p0, p1 = Vector(p0), Vector(p1)
        d = (p1 - p0).normalized()
        a = perpendicular(d)
        b = d.cross(a)
        r1 = r if r1 is None else r1
        ring = lambda c, rr: [c + (a * math.cos(2 * math.pi * k / sides) + b * math.sin(2 * math.pi * k / sides)) * rr for k in range(sides)]
        self.rings(role, [ring(p0, r), ring(p1, r1)])

    def lathe(self, role, origin, axis, profile, sides=32):
        """Turn a closed (along, radius) outline about an axis through origin."""
        origin, axis = Vector(origin), Vector(axis).normalized()
        a = perpendicular(axis)
        b = axis.cross(a)
        bm = self.bm(role)
        rows = []
        for t, r in profile:
            rows.append([bm.verts.new(origin + axis * t + (a * math.cos(2 * math.pi * k / sides) + b * math.sin(2 * math.pi * k / sides)) * r) for k in range(sides)])
        for i in range(len(rows)):
            r0, r1 = rows[i], rows[(i + 1) % len(rows)]
            for k in range(sides):
                bm.faces.new((r0[k], r0[(k + 1) % sides], r1[(k + 1) % sides], r1[k]))

    def tube(self, role, points, r, fillet=0.07, sides=8):
        """A rod along a polyline, its corners rounded."""
        pts = [Vector(p) for p in points]
        path = [pts[0]]
        for i in range(1, len(pts) - 1):
            p, a, b = pts[i], pts[i - 1], pts[i + 1]
            da, db = (a - p), (b - p)
            f = min(fillet, da.length * 0.45, db.length * 0.45)
            if f <= 0:
                path.append(p)
                continue
            s, e = p + da.normalized() * f, p + db.normalized() * f
            for k in range(6):
                t = k / 5
                path.append(s * (1 - t) ** 2 + p * (2 * t * (1 - t)) + e * t ** 2)
        path.append(pts[-1])
        rings = []
        tangent = (path[1] - path[0]).normalized()
        a = perpendicular(tangent)
        for i, p in enumerate(path):
            nxt = (path[min(i + 1, len(path) - 1)] - path[max(i - 1, 0)]).normalized()
            a = (a - nxt * a.dot(nxt)).normalized()
            b = nxt.cross(a)
            rings.append([p + (a * math.cos(2 * math.pi * k / sides) + b * math.sin(2 * math.pi * k / sides)) * r for k in range(sides)])
        self.rings(role, rings)

    def panel(self, role, origin, u, v, outline, t):
        """A plate on a wall: outline in (u, v) about origin, standing out by t along u x v."""
        o, u, v = Vector(origin), Vector(u), Vector(v)
        n = u.cross(v).normalized()
        low = [o + u * a + v * b for a, b in outline]
        self.rings(role, [low, [p + n * t for p in low]])

    def text(self, role, words, font_kind, origin, u, v, width, height, t=0.003):
        """Lettering fitted to a width and height, centred on origin, facing along u x v."""
        curve = bpy.data.curves.new("Words", "FONT")
        curve.body = words
        curve.resolution_u = 5
        curve.extrude = 0.0
        for name in FONTS[font_kind]:
            path = os.path.join(os.environ.get("WINDIR", "C:/Windows"), "Fonts", name)
            if os.path.exists(path):
                curve.font = bpy.data.fonts.load(path, check_existing=True)
                break
        obj = bpy.data.objects.new("Words", curve)
        bpy.context.collection.objects.link(obj)
        depsgraph = bpy.context.evaluated_depsgraph_get()
        evaluated = obj.evaluated_get(depsgraph)
        mesh = evaluated.to_mesh()
        xs, ys = [p.co.x for p in mesh.vertices], [p.co.y for p in mesh.vertices]
        cx, cy, sx, sy = (min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2, width / (max(xs) - min(xs)), height / (max(ys) - min(ys))
        o, u, v = Vector(origin), Vector(u), Vector(v)
        n = u.cross(v).normalized()
        bm = self.bm(role)
        base = [bm.verts.new(o + u * ((p.co.x - cx) * sx) + v * ((p.co.y - cy) * sy)) for p in mesh.vertices]
        tops = [bm.verts.new(q.co + n * t) for q in base]
        for poly in mesh.polygons:
            bm.faces.new([tops[i] for i in poly.vertices])
        evaluated.to_mesh_clear()
        bpy.data.objects.remove(obj)
        bpy.data.curves.remove(curve)

    def export(self, path, paint):
        for obj in list(bpy.data.objects):
            bpy.data.objects.remove(obj)
        objects = []
        for role, bm in self.parts.items():
            bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
            bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
            mesh = bpy.data.meshes.new(role)
            bm.to_mesh(mesh)
            bm.free()
            mesh.shade_smooth()
            mesh.set_sharp_from_angle(angle=math.radians(50))
            obj = bpy.data.objects.new(role, mesh)
            bpy.context.collection.objects.link(obj)
            mesh.materials.append(material(role, paint))
            objects.append(obj)
        for obj in objects:
            obj.select_set(True)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in objects)
        bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=True, export_yup=True,
                                  export_materials="EXPORT", export_normals=True, export_texcoords=False)
        print("wrote", path, tris, "triangles")


def material(role, paint):
    code, metal, rough = COMMON.get(role, ("#ff00ff", 0, .38))
    if paint and role in paint:
        code, metal, rough = paint[role], 0, .38 if role not in COMMON else rough
    mat = bpy.data.materials.new(role)
    mat.use_nodes = True
    node = mat.node_tree.nodes["Principled BSDF"]
    node.inputs["Base Color"].default_value = (*srgb(code), 1.0)
    node.inputs["Metallic"].default_value = metal
    node.inputs["Roughness"].default_value = rough
    return mat


# ---------------------------------------------------------------- the body


def door(m, s, y0, y1, z0, z1, role="Hood", hinges=True, wall=HOOD_X):
    """A hood door on side s: a dark seam round a raised plate, with hinges and a latch."""
    yc, zc, hw, hh = (y0 + y1) / 2, (z0 + z1) / 2, (y1 - y0) / 2, (z1 - z0) / 2
    o, u, v = (s * wall, yc, zc), (0, s, 0), (0, 0, 1)
    m.panel("Dark", o, u, v, rrect(-hw, hw, -hh, hh, .025, .025, 2), 0.003)
    m.panel(role, o, u, v, rrect(-hw + .012, hw - .012, -hh + .012, hh - .012, .02, .02, 2), 0.008)
    if hinges:
        x = s * (wall + 0.008)
        for z in (z0 + hh * .35, z1 - hh * .35):
            m.box(role, (min(x, x + s * .012), y0 + .004, z - .035), (max(x, x + s * .012), y0 + .06, z + .035))
        m.cyl("Steel", (x, y1 - .06, zc), (x + s * .02, y1 - .06, zc), .022, 8)


def grille(m, s, y0, y1, z0, z1, wall=HOOD_X, role="Hood"):
    """Louvres in a dark opening with a frame round it."""
    x = s * wall
    a, b = sorted((x, x + s * .004))
    m.box("Dark", (a, y0, z0), (b, y1, z1))
    a, b = sorted((x, x + s * .014))
    for lo, hi in (((a, y0 - .03, z0 - .03), (b, y1 + .03, z0)), ((a, y0 - .03, z1), (b, y1 + .03, z1 + .03)),
                   ((a, y0 - .03, z0), (b, y0, z1)), ((a, y1, z0), (b, y1 + .03, z1))):
        m.box(role, lo, hi)
    a, b = sorted((x, x + s * .012))
    z = z0 + .03
    while z < z1 - .012:
        m.box("Rubber", (a, y0, z), (b, y1, z + .012))
        z += .05
    y = y0 + .6
    while y < y1 - .1:
        m.box("Rubber", (a, y, z0), (b, y + .02, z1))
        y += .6


def fan_shroud(m, y, z, roof):
    """A fan's rim standing on the roof, tall enough to hold the fan; z is the fan's hub."""
    top = z + .12
    m.lathe("HoodRoof", (0, y, roof - .02), (0, 0, 1), [(0, .67), (top - roof - .03, .655), (top - roof + .02, .67), (top - roof + .02, .62), (0, .62)], 40)
    m.cyl("Dark", (0, y, roof - .02), (0, y, roof + .01), .625, 32)
    m.lathe("Rubber", (0, y, top), (0, 0, 1), [(0, .625), (.012, .625), (.012, .612), (0, .612)], 40)
    for k in range(-5, 6):
        o = k * .11
        h = math.sqrt(.62 ** 2 - o ** 2)
        m.tube("Rubber", [(o, y - h, top + .006), (o, y + h, top + .006)], .005, 0, 6)
    for k in (-2, 0, 2):
        o = k * .22
        h = math.sqrt(.62 ** 2 - o ** 2)
        m.tube("Rubber", [(-h, y + o, top + .012), (h, y + o, top + .012)], .006, 0, 6)


def end_of_frame(m, e):
    """The pilot, plow, anticlimber, coupler pocket and hoses at one end; e is -1 at the front."""
    y0, y1 = sorted((e * (FRAME_END - .15), e * FRAME_END))
    m.box("Sill", (-FRAME_X, y0, SILL_LOW), (FRAME_X, y1, DECK), .006)
    ya, yb = sorted((e * FRAME_END, e * (FRAME_END + .08)))
    m.box("Frame", (-1.46, ya, DECK - .13), (1.46, yb, DECK), .012)
    a, b = sorted((e * (FRAME_END + .001), e * (FRAME_END + .004)))
    m.box("Stripe", (-FRAME_X, a, SILL_LOW + .04), (FRAME_X, b, DECK - .15))
    y0, y1 = sorted((e * (FRAME_END - .09), e * FRAME_END))
    gap_x, gap_lo, gap_hi = .25, .70, 1.05
    m.box("Pilot", (-FRAME_X + .1, y0, .42), (FRAME_X - .1, y1, gap_lo))
    m.box("Pilot", (-FRAME_X + .02, y0, gap_hi), (FRAME_X - .02, y1, SILL_LOW))
    for s in (-1, 1):
        a, b = sorted((s * gap_x, s * (FRAME_X - .04)))
        m.box("Pilot", (a, y0, gap_lo), (b, y1, gap_hi))
    # The pocket: walls and a roof, open for the coupler to swing in.
    yb0, yb1 = sorted((e * 7.46, e * (FRAME_END - .09)))
    m.box("Frame", (-.34, yb0, gap_hi), (.34, yb1, SILL_LOW + .05))
    m.box("Frame", (-.34, yb0, gap_lo - .04), (.34, yb1, gap_lo))
    for s in (-1, 1):
        a, b = sorted((s * .3, s * .34))
        m.box("Frame", (a, yb0, gap_lo), (b, yb1, gap_hi))
    # The plow, the width of the pilot.
    yt, yl = e * (FRAME_END + .005), e * (FRAME_END + .24)
    t = e * .05
    m.hexa("Pilot", ((-1.2, yl, .13), (1.2, yl, .13), (1.2, yl - t, .13), (-1.2, yl - t, .13),
                     (-1.43, yt, .6), (1.43, yt, .6), (1.43, yt - t, .6), (-1.43, yt - t, .6)))
    m.hexa("Stripe", ((-1.2, yl, .13), (1.2, yl, .13), (1.2, yl + e * .004, .13), (-1.2, yl + e * .004, .13),
                      (-1.21, yl - e * .008, .17), (1.21, yl - e * .008, .17), (1.21, yl - e * .004, .17), (-1.21, yl - e * .004, .17)))
    # Cut lever, air hoses and MU receptacles.
    yy = e * (FRAME_END + .03)
    m.tube("Rail", [(-1.32, yy, .98), (-.4, yy + e * .03, .98), (-.14, yy + e * .06, 1.0)], .011, .05)
    for x, hang in ((-.62, .06), (.62, -.04), (.88, 0.0)):
        m.tube("Rubber", [(x, yy, 1.1), (x, yy + e * .08, .98), (x + hang, yy + e * .13, .76), (x + hang * 2, yy + e * .18, .68)], .022, .05, 8)
        m.cyl("Red" if x < 0 else "Steel", (x, yy - e * .02, 1.11), (x, yy + e * .02, 1.11), .03, 10)
    for x in (-1.1, 1.1):
        m.box("Steel", (x - .1, min(yy, yy + e * .05), .78), (x + .1, max(yy, yy + e * .05), .93), .006)


def stairs(m, s, e):
    """A corner's steps: side plates, a back plate and three treads."""
    y0, y1 = sorted((e * STAIR[0], e * STAIR[1]))
    a, b = sorted((s * 1.18, s * 1.52))
    for ya, yb in ((y0, y0 + .025), (y1 - .025, y1)):
        m.box("Frame", (a, ya, .46), (b, yb, DECK - .06))
    i0, i1 = sorted((s * 1.18, s * 1.205))
    m.box("Frame", (i0, y0, .46), (i1, y1, DECK - .06))
    for z in (.5, .82, 1.14):
        t0, t1 = sorted((s * 1.2, s * 1.515))
        m.box("Deck", (t0, y0, z), (t1, y1, z + .03))
        k0, k1 = sorted((s * 1.495, s * 1.518))
        m.box("Stripe", (k0, y0 + .02, z), (k1, y1 - .02, z + .03))


def railings(m):
    top, mid = RAIL_TOP, 2.0
    r = .018
    side_end = STAIR[0] - .05
    posts = (-3.33, -2.0, -.78, .43, 1.7, 2.92, 4.14, 5.36, 6.37, side_end)
    for s in (-1, 1):
        x = s * 1.475
        # Along the long hood; forward it sweeps up to a grab on the cab; aft it comes down for the steps.
        m.tube("Rail", [(x, -3.33, mid), (x, side_end, mid)], r, 0)
        m.tube("Rail", [(x, CAB_REAR - .02, 3.15), (x, -4.03, 3.15), (x, -3.67, top), (x, side_end, top), (s * 1.535, side_end + .02, DECK), (s * 1.535, side_end + .02, 1.06)], r, .1)
        for y in posts:
            m.cyl("Rail", (x, y, DECK), (x, y, top), r * 1.1, 8)
            m.box("Rail", (x - .04, y - .04, DECK), (x + .04, y + .04, DECK + .01))
        # On the porch: a grab from the steps up on to the box.
        y = BOX_ENDS[0] - .05
        m.tube("Rail", [(s * 1.535, y, 1.06), (s * 1.535, y, DECK), (s * 1.47, y, 2.5)], r, .06)
        # The end railing: up from the outer step, slanting in over it to one level rail on four posts.
        for e in (-1, 1):
            Y = e * 8.45
            m.tube("Rail", [(s * 1.535, Y, 1.06), (s * 1.535, Y, DECK), (s * 1.08, Y, END_RAIL), (0, Y, END_RAIL)], r, .07)
            for px in (.24, .74):
                m.cyl("Rail", (s * px, Y, DECK), (s * px, Y, END_RAIL), r * 1.1, 8)
        # Grabs on the end walls.
        m.tube("Rail", [(s * .4, HOOD_REAR + .01, 2.05), (s * .4, HOOD_REAR + .07, 2.1), (s * .4, HOOD_REAR + .07, 3.2), (s * .4, HOOD_REAR + .01, 3.25)], .012, .03)


def mirrored(half):
    return half + [(-x, z) for x, z in reversed(half)]


def cab(m, livery):
    # Straight sides to the eave, then the long shoulders in to a flat roof. The front wall stands straight.
    m.prism("Cab", mirrored([(1.5, DECK), (1.5, EAVE), (.92, CAB_TOP)]), "y", CAB_FRONT, CAB_REAR)
    m.prism("CabRoof", mirrored([(1.54, EAVE - .035), (1.54, EAVE), (.93, CAB_TOP + .02)]), "y", CAB_FRONT + .01, CAB_REAR + .02)
    # The raised hatch along the middle of the roof.
    m.prism("CabRoof", mirrored([(.62, CAB_TOP), (.5, 4.5)]), "y", -5.84, -4.82)

    # The colour above the window line, on the front wall and along the sides.
    m.panel("CabHeader", (0, CAB_FRONT, 0), (1, 0, 0), (0, 0, 1), mirrored([(1.5, FORWARD_SPLIT), (1.5, EAVE), (.92, CAB_TOP)]), .004)
    m.panel("Pin", (0, CAB_FRONT, 0), (1, 0, 0), (0, 0, 1), rrect(-1.5, 1.5, FORWARD_SPLIT - .03, FORWARD_SPLIT), .004)
    for s in (-1, 1):
        a, b = sorted((s * 1.5, s * 1.504))
        m.box("CabHeader", (a, CAB_FRONT, FORWARD_SPLIT), (b, CAB_REAR, EAVE - .03))
        a, b = sorted((s * 1.5, s * 1.505))
        m.box("Pin", (a, CAB_FRONT, FORWARD_SPLIT - .03), (b, CAB_REAR, FORWARD_SPLIT))

    # The number board box: a V standing out of the wall, a board on each face, turned so it reads from the side too.
    f = CAB_FRONT + .01
    tip = -6.52
    plan = [(.83, f), (.13, tip), (-.13, tip), (-.83, f)]
    m.loft("CabHeader", [(3.87, plan), (4.16, plan), (4.3, [(.83, f), (.13, f - .02), (-.13, f - .02), (-.83, f)])], "z")
    m.box("CabHeader", (-.13, tip - .05, 3.85), (.13, tip + .02, 4.32), .012)
    for z in (3.97, 4.2):
        m.cyl("Rubber", (0, tip - .05, z), (0, tip - .07, z), .105, 18)
        m.cyl("Lamp", (0, tip - .07, z), (0, tip - .078, z), .09, 18)

    for s in (-1, 1):
        def side_panel(role, yc, zc, outline, depth):
            m.panel(role, (s * 1.5, yc, zc), (0, s, 0), (0, 0, 1), outline, depth)

        face = Vector((.858, s * .515, 0))
        n = face.cross(Vector((0, 0, 1)))
        o = Vector((s * .48, -6.31, 4.015))
        m.panel("Dark", o, face, (0, 0, 1), rrect(-.36, .36, -.135, .135, .03, .03, 2), .006)
        m.panel("Board", o, face, (0, 0, 1), rrect(-.34, .34, -.115, .115, .025, .025, 2), .01)
        m.text("BoardText", livery["number"], "sans", o + n * .01, face, (0, 0, 1), .5, .15)

        # The side windows: two sliding sashes under the eave.
        for yc in (-4.92, -5.44):
            side_panel("Rubber", yc, 3.275, rrect(-.26, .26, -.315, .315, .03, .03, 3), .008)
            side_panel("Steel", yc, 3.275, rrect(-.245, .245, -.3, .3, .025, .025, 3), .012)
            side_panel("Glass", yc, 3.275, rrect(-.225, .225, -.28, .28, .02, .02, 3), .016)
        # The visor over them: wide at the wall, narrower at its lip, sloping down.
        x0, x1 = s * 1.5, s * 1.62
        m.hexa("CabHeader", ((x0, -5.95, EAVE - .05), (x1, -5.72, 3.62), (x1, -4.66, 3.62), (x0, -4.4, EAVE - .05),
                       (x0, -5.95, EAVE - .03), (x1, -5.72, 3.64), (x1, -4.66, 3.64), (x0, -4.4, EAVE - .03)))
        m.tube("Rail", [(s * 1.5, CAB_REAR - .08, 2.25), (s * 1.56, CAB_REAR - .08, 2.29), (s * 1.56, CAB_REAR - .08, 2.95), (s * 1.5, CAB_REAR - .08, 2.99)], .012, .03)
        # Under the windows: the seam on to the box and its doors.
        for y0, y1 in ((-6.05, -5.1), (-5.05, -4.15)):
            door(m, s, y0, y1, DECK + .1, BOX_TOP - .03, "Cab", True, 1.5)

        # The tall corner windows, beside the nose, reaching down past its top.
        xc = s * 1.155
        o = (xc, CAB_FRONT, 2.98)
        m.panel("Dark", o, (1, 0, 0), (0, 0, 1), rrect(-.27, .27, -.78, .78, .02, .02, 2), .003)
        o = (xc, CAB_FRONT, 3.535)
        m.panel("Rubber", o, (1, 0, 0), (0, 0, 1), rrect(-.225, .225, -.325, .325, .1, .05, 4), .009)
        m.panel("Glass", o, (1, 0, 0), (0, 0, 1), rrect(-.205, .205, -.305, .305, .085, .04, 4), .013)
        # The windscreens, between the nose and the board box.
        xc = s * .435
        o = (xc, CAB_FRONT, 3.665)
        m.panel("Rubber", o, (1, 0, 0), (0, 0, 1), rrect(-.335, .335, -.195, .195, .04, .04, 3), .008)
        m.panel("Glass", o, (1, 0, 0), (0, 0, 1), rrect(-.315, .315, -.175, .175, .03, .03, 3), .012)
        m.tube("Dark", [(xc - s * .2, CAB_FRONT - .02, 3.82), (xc + s * .08, CAB_FRONT - .02, 3.5)], .007, 0, 6)
        # The rear wall's windows.
        m.panel("Rubber", (s * 1.21, CAB_REAR, 3.4), (-1, 0, 0), (0, 0, 1), rrect(-.25, .25, -.25, .25, .03, .03, 3), .006)
        m.panel("Glass", (s * 1.21, CAB_REAR, 3.4), (-1, 0, 0), (0, 0, 1), rrect(-.23, .23, -.23, .23, .025, .025, 3), .01)

    # The door to the walk behind the cab.
    y0, y1 = sorted((CAB_REAR, CAB_REAR + .004))
    m.box("Dark", (-1.47, y0, BOX_TOP + .02), (-.96, y1, 3.15))
    y0, y1 = sorted((CAB_REAR, CAB_REAR + .01))
    m.box("Cab", (-1.455, y0, BOX_TOP + .035), (-.975, y1, 3.135))

    # On the roof: three horns on the hatch, and an antenna.
    m.box("Frame", (-.3, -5.42, 4.5), (.3, -5.26, 4.525))
    for x, length, r in ((-.2, .46, .075), (0, .3, .06), (.2, .38, .07)):
        m.cyl("Steel", (x, -5.34, 4.55), (x, -5.34 - length, 4.59), .022, 10, r)
    m.cyl("Dark", (0, -4.5, CAB_TOP), (0, -4.5, CAB_TOP + .05), .05, 10)
    m.cyl("Dark", (0, -4.5, CAB_TOP + .05), (0, -4.5, CAB_TOP + .17), .01, 6)

    # The road number, big, under the windows.
    for s in (-1, 1):
        m.text("Number", livery["number"], "sans", (s * 1.5005, -5.1, 2.65), (0, s, 0), (0, 0, 1), 1.7, .5)


def hood_section(lo, hi, cut):
    """One side of the hood from lo up to hi, with the grille's recess taken out of it, as (x, z) going up."""
    if not cut or cut[3] <= lo or cut[2] >= hi:
        return [(HOOD_X, lo), (HOOD_X, hi)]
    z0, z1 = cut[2], cut[3]
    pts = []
    if z0 > lo:
        pts += [(HOOD_X, lo), (HOOD_X, z0)]
    pts += [(HOOD_X - RECESS, max(z0, lo)), (HOOD_X - RECESS, min(z1, hi))]
    if z1 < hi:
        pts += [(HOOD_X, z1), (HOOD_X, hi)]
    return pts


def long_hood(m, livery):
    y0, y1 = CAB_REAR, HOOD_REAR
    # The hood in slices along its length, so that each grille really is let into its side.
    edges = sorted({y0, y1, *[g[0] for g in GRILLES], *[g[1] for g in GRILLES]})
    for a, b in zip(edges, edges[1:]):
        mid = (a + b) / 2
        cut = next((g for g in GRILLES if g[0] < mid < g[1]), None)
        low = hood_section(DECK, HOOD_SPLIT, cut)
        m.prism("Hood", low + [(-x, z) for x, z in reversed(low)], "y", a, b)
        side = hood_section(HOOD_SPLIT, HOOD_TOP - .14, cut)
        arc = [(HOOD_X - .14 + .14 * math.cos(math.pi / 2 * k / 5), HOOD_TOP - .14 + .14 * math.sin(math.pi / 2 * k / 5)) for k in range(1, 6)]
        if cut and cut[3] > HOOD_TOP - .14:
            # The tall forward grille reaches up into the rounded edge: finish it square under the blister.
            side = hood_section(HOOD_SPLIT, cut[3], cut)
            arc = [(HOOD_X - RECESS - .02, HOOD_TOP)]
        up = side + arc
        m.prism("HoodRoof", up + [(-x, z) for x, z in reversed(up)], "y", a, b)

    jog = [(JOG[0], FORWARD_SPLIT), (JOG[1], HOOD_SPLIT), (y0, HOOD_SPLIT), (y0, FORWARD_SPLIT)]
    for s in (-1, 1):
        wall = HOOD_X
        o, u, v = (s * wall, 0, 0), (0, s, 0), (0, 0, 1)
        # Ahead of the jog the roof colour comes down, round the forward grille's recess.
        g = GRILLES[0]
        m.panel("HoodRoof", o, u, v, [(s * p, q) for p, q in ((y0, FORWARD_SPLIT), (g[0], FORWARD_SPLIT), (g[0], HOOD_SPLIT), (y0, HOOD_SPLIT))], .003)
        m.panel("HoodRoof", o, u, v, [(s * p, q) for p, q in ((g[0], FORWARD_SPLIT), (g[1], FORWARD_SPLIT), (g[1], g[2]), (g[0], g[2]))], .003)
        m.panel("HoodRoof", o, u, v, [(s * p, q) for p, q in ((g[1], FORWARD_SPLIT), (JOG[0], FORWARD_SPLIT), (JOG[1], HOOD_SPLIT), (g[1], HOOD_SPLIT))], .003)
        # The line under it, stepping up at the jog and stopping at the radiator grille.
        for ya, yb, z in ((y0, JOG[0], FORWARD_SPLIT), (JOG[1], GRILLES[2][0], HOOD_SPLIT)):
            m.panel("Pin", o, u, v, [(s * ya, z - .03), (s * yb, z - .03), (s * yb, z), (s * ya, z)], .004)
        m.panel("Pin", o, u, v, [(s * JOG[0], FORWARD_SPLIT - .03), (s * JOG[1], HOOD_SPLIT - .03), (s * JOG[1], HOOD_SPLIT), (s * JOG[0], FORWARD_SPLIT)], .004)
        # The grilles, in their recesses.
        for ga, gb, gl, gh in GRILLES:
            grille(m, s, ga, gb, gl, gh, HOOD_X - RECESS, "HoodRoof")
        # The doors, on their real seams: a tall row over a low row, up to the grilles.
        seams = (7.53, 6.58, 5.68, 4.89, 3.82, 2.92, 2.02, 1.12, .22, -.52, -1.37, -2.0, -2.82, -3.64)
        for a, b in zip(seams[1:], seams):
            top = 3.4 if a > 3.0 else 3.45
            door(m, s, a + .02, b - .02, 1.87, top)
            door(m, s, a + .02, b - .02, DECK + .1, 1.83, hinges=False)
        # A rib along the foot of the hood.
        a, b = sorted((s * HOOD_X, s * (HOOD_X + .012)))
        m.box("Hood", (a, y0, DECK), (b, y1, DECK + .09))
    # The roof: hatches with seams round them.
    for ya, yb in ((-.9, .25), (1.75, 2.4)):
        m.box("Dark", (-.72, ya, HOOD_TOP - .002), (.72, yb, HOOD_TOP + .001))
        m.box("HoodRoof", (-.71, ya + .012, HOOD_TOP), (.71, yb - .012, HOOD_TOP + .012), .004)

    # The blister over the forward grille, against the cab.
    m.loft("HoodRoof", [(y, rrect(-HOOD_X, HOOD_X, HOOD_TOP - .03, z, .1, 0, 4)) for y, z in ((y0, BLISTER_TOP), (-2.0, BLISTER_TOP), (-1.85, HOOD_TOP + .12))], "y")

    # The dynamic brake fan, then the radiator fans, side by side.
    fan_shroud(m, BRAKE_FAN, BRAKE_FAN_Z, HOOD_TOP)
    for y in RADIATOR_FANS:
        fan_shroud(m, y, FAN_Z, HOOD_TOP)
    m.cyl("HoodRoof", (0, 2.71, HOOD_TOP), (0, 2.71, 4.5), .16, 18)

    # Exhaust: the turbo's single wide stack on a raised base.
    stack_y = -1.42
    m.loft("HoodRoof", [(HOOD_TOP - .01, circle(0, stack_y, .4, .34)), (HOOD_TOP + .14, circle(0, stack_y, .36, .3))], "z")
    m.loft("Dark", [(HOOD_TOP + .14, circle(0, stack_y, .3, .2)), (HOOD_TOP + .22, circle(0, stack_y, .28, .18))], "z")
    m.loft("Rubber", [(HOOD_TOP + .22, circle(0, stack_y, .3, .2)), (HOOD_TOP + .24, circle(0, stack_y, .3, .2))], "z")

    # The rear wall: door, twin lamps and number boards.
    m.box("Hood", (-.34, HOOD_REAR, DECK + .08), (.34, HOOD_REAR + .01, 3.1))
    for s in (-1, 1):
        m.box("Dark", (s * .5 - .3, HOOD_REAR + .001, 3.5), (s * .5 + .3, HOOD_REAR + .008, 3.78))
        m.box("Board", (s * .5 - .28, HOOD_REAR + .002, 3.52), (s * .5 + .28, HOOD_REAR + .012, 3.76))
        m.text("BoardText", livery["number"], "sans", (s * .5, HOOD_REAR + .012, 3.64), (-1, 0, 0), (0, 0, 1), .42, .14)
    for z in (3.4, 3.88):
        m.cyl("Hood", (0, HOOD_REAR, z), (0, HOOD_REAR + .07, z), .095, 16)
        m.cyl("Lamp", (0, HOOD_REAR + .07, z), (0, HOOD_REAR + .078, z), .08, 16)

    logo, kind, w, h, yc, zc = livery["logo"]
    for s in (-1, 1):
        m.text("Logo", logo, kind, (s * (HOOD_X + .0125), yc, zc), (0, s, 0), (0, 0, 1), w, h)


def shield(m, z):
    """A railroad shield, on the front of the nose: a blue head with its name, red and white stripes below."""
    y = NOSE_FRONT
    u, v = (1, 0, 0), (0, 0, 1)
    w, top, mid, tip = .34, .38, .1, -.42
    m.panel("Dark", (0, y, z), u, v, [(w + .025, top + .025), (-w - .025, top + .025), (-w - .025, mid), (-w * .85 - .02, -.12), (0, tip - .03), (w * .85 + .02, -.12), (w + .025, mid)], .002)
    m.panel("ShieldBlue", (0, y, z), u, v, [(w, top), (-w, top), (-w, mid), (w, mid)], .004)
    m.text("ShieldWhite", "UNION", "sans", (0, y - .004, z + .3), u, v, .44, .09, .001)
    m.text("ShieldWhite", "PACIFIC", "sans", (0, y - .004, z + .17), u, v, .52, .09, .001)
    bars = 7

    def low(x):
        if abs(x) < w * .85:
            return tip + (-.12 - tip) * abs(x) / (w * .85)
        return -.12 + (mid + .12) * (abs(x) - w * .85) / (w * .15)

    for k in range(bars):
        x0, x1 = -w + 2 * w * k / bars, -w + 2 * w * (k + 1) / bars
        outline = [(x1, mid), (x0, mid), (x0, low(x0)), (x1, low(x1))] if x0 * x1 >= 0 else [(x1, mid), (x0, mid), (x0, low(x0)), (0, tip), (x1, low(x1))]
        m.panel("Red" if k % 2 == 0 else "ShieldWhite", (0, y, z), u, v, outline, .004)


def nose(m, livery):
    profile = lambda half, top: rrect(-half, half, DECK, top, .15, 0, 5)
    m.loft("NoseFront", [(NOSE_FRONT, profile(NOSE_X - .06, NOSE_TOP - .07)), (NOSE_FRONT + .08, profile(NOSE_X, NOSE_TOP)), (NOSE_FRONT + .33, profile(NOSE_X, NOSE_TOP))], "y")
    m.prism("Nose", profile(NOSE_X, NOSE_TOP), "y", NOSE_FRONT + .33, CAB_FRONT)
    # The non-slip on its top.
    m.box("Dark", (-NOSE_X + .17, NOSE_FRONT + .1, NOSE_TOP), (NOSE_X - .17, CAB_FRONT, NOSE_TOP + .002))
    # The front: two hatches up in the corners, a seam low down, the railroad mark in the middle.
    u, v = (1, 0, 0), (0, 0, 1)
    for s in (-1, 1):
        m.panel("Dark", (s * .6, NOSE_FRONT, 2.92), u, v, rrect(-.2, .2, -.15, .15, .02, .02, 2), .002)
        m.panel("NoseFront", (s * .6, NOSE_FRONT, 2.92), u, v, rrect(-.19, .19, -.14, .14, .015, .015, 2), .005)
    m.box("Dark", (-.8, NOSE_FRONT - .002, 1.85), (.8, NOSE_FRONT, 1.86))
    if livery["plate"] == "shield":
        shield(m, 2.5)
    else:
        m.text("Plate", livery["plate"], "serif", (0, NOSE_FRONT, 2.5), u, v, 1.56, .56)
    for s in (-1, 1):
        # The nose's side: one big door.
        door(m, s, -7.4, -6.2, BOX_TOP + .06, NOSE_TOP - .06, "Nose", True, NOSE_X)
        # The boxes beside nose and cab stop short of the front, leaving the porch.
        a, b = sorted((s * NOSE_X, s * 1.49))
        m.prism("Nose", rrect(BOX_ENDS[0], BOX_ENDS[1], DECK, BOX_TOP, .12, 0, 4), "x", a, b, lambda x, y, z: (x, y, z))
        door(m, s, -7.33, -6.16, DECK + .1, BOX_TOP - .03, "Nose", True, 1.49)
        # A grab up the cab's front from the box.
        m.tube("Rail", [(s * 1.42, CAB_FRONT - .01, BOX_TOP + .08), (s * 1.42, CAB_FRONT - .07, BOX_TOP + .12), (s * 1.42, CAB_FRONT - .07, 3.1), (s * 1.42, CAB_FRONT - .01, 3.14)], .012, .03)
        # The ditch lights, low on the corners of the nose.
        m.box("Dark", (s * .78 - .1, NOSE_FRONT - .1, DECK + .2), (s * .78 + .1, NOSE_FRONT + .05, DECK + .42), .012)
        m.cyl("Lamp", (s * .78, NOSE_FRONT - .1, DECK + .31), (s * .78, NOSE_FRONT - .11, DECK + .31), .085, 16)
    # The bell, up on the nose.
    m.box("Steel", (-.05, -7.0, NOSE_TOP), (.05, -6.88, NOSE_TOP + .05))
    m.lathe("Tread", (0, -6.94, NOSE_TOP + .04), (0, 0, 1), [(0, .095), (.035, .08), (.09, .058), (.13, .05), (.13, .01), (0, .01)], 18)


def underbody(m):
    # The deck, the frame under it and the side sills with their stripe. It all rides right down on the trucks.
    m.box("Deck", (-FRAME_X, -FRAME_END, DECK - .06), (FRAME_X, FRAME_END, DECK), .004)
    m.box("Frame", (-1.49, -STAIR[0] + .05, SILL_LOW + .04), (1.49, STAIR[0] - .05, DECK - .06))
    m.box("Frame", (-.9, -7.46, 1.25), (.9, 7.46, SILL_LOW + .04))
    for s in (-1, 1):
        a, b = sorted((s * 1.49, s * FRAME_X))
        m.box("Sill", (a, -STAIR[0] + .05, SILL_LOW), (b, STAIR[0] - .05, DECK - .04), .004)
        m.box("Sill", (a, -FRAME_END, DECK - .15), (b, FRAME_END, DECK), .004)
        a, b = sorted((s * FRAME_X, s * (FRAME_X + .003)))
        m.box("Stripe", (a, -FRAME_END, DECK - .145), (b, FRAME_END, DECK - .005))
        # A row of stiffeners on the sill.
        y = -7.2
        while y < 7.3:
            x0, x1 = sorted((s * FRAME_X, s * (FRAME_X + .012)))
            m.box("Sill", (x0, y - .02, SILL_LOW + .02), (x1, y + .02, DECK - .16))
            y += 1.2
        for e in (-1, 1):
            stairs(m, s, e)
        # The air reservoirs, along the shoulders of the tank.
        m.cyl("Tank", (s * 1.33, -2.62, 1.07), (s * 1.33, 2.62, 1.07), .135, 20)
        for y in (-2.62, 2.62):
            m.lathe("Tank", (s * 1.33, y, 1.07), (0, 1 if y > 0 else -1, 0), [(0, .135), (.045, .118), (.08, .07), (.09, .02), (0, .02)], 20)
        for y in (-2.1, 0, 2.1):
            m.lathe("Frame", (s * 1.33, y, 1.07), (0, 1, 0), [(-.02, .143), (.02, .143), (.02, .13), (-.02, .13)], 20)
            a, b = sorted((s * 1.1, s * 1.42))
            m.box("Frame", (a, y - .03, 1.07), (b, y + .03, SILL_LOW + .02))
    # The fuel tank: deep, nearly down to the rails.
    m.loft("Tank", [(y, rrect(-x, x, z, SILL_LOW + .02, .1, .32, 6)) for y, x, z in
                    ((-2.64, .98, .34), (-2.56, 1.12, .22), (-2.44, 1.18, .16), (2.3, 1.18, .16), (2.42, 1.12, .22), (2.5, .98, .34))], "y")
    for y in (-1.5, 0, 1.5):
        m.prism("Frame", rrect(-1.192, 1.192, .148, SILL_LOW + .02, .1, .33, 6), "y", y - .025, y + .025)
    for s in (-1, 1):
        # Filler, gauge and the emergency stop box.
        m.cyl("Steel", (s * 1.17, 1.0, .85), (s * 1.24, 1.0, .85), .06, 12)
        a, b = sorted((s * 1.185, s * 1.198))
        m.box("Lamp", (a, -.7, .5), (b, -.62, .85))
        m.box("Stripe", (a, -1.1, .78), (b, -.96, .92))
    # The bolster pads, down on the centre plates of the trucks.
    for y in (TRUCK_CENTRE, -TRUCK_CENTRE):
        m.cyl("Frame", (0, y, 1.25), (0, y, 1.195), .28, 20)


def body(livery):
    m = Kit()
    underbody(m)
    for e in (-1, 1):
        end_of_frame(m, e)
    nose(m, livery)
    cab(m, livery)
    long_hood(m, livery)
    railings(m)
    return m


# ---------------------------------------------------------------- what runs on the rails


def truck():
    """A Blomberg-M: deep cast side frames, leaf springs on a swing-hung plank, clasp brakes, two nose-hung motors. Wheelsets go in at +-AXLE."""
    m = Kit()
    # Half a side frame, from the bottom of the window out over the wheel and back along the top.
    half = [(.0, .5), (.66, .5), (.92, .6), (.98, .34), (1.04, .27), (1.14, .25), (1.18, .3), (1.18, .8), (1.56, .8), (1.56, .3), (1.6, .25), (1.7, .27),
            (1.76, .34), (1.8, .66), (2.02, .78), (2.14, .86), (2.16, .96), (2.06, 1.04), (1.72, 1.12), (1.16, 1.15), (.86, 1.1), (.64, 1.0), (.62, .66), (.0, .66)]
    # The whole frame is that half and its mirror, with the window open at the top.
    right = half
    left = [(-y, z) for y, z in reversed(half[1:-1])]
    outline = right + left[::1]
    outline = [(y, z) for y, z in half[:-1]] + [(.0, .66)] + [(-y, z) for y, z in reversed(half[1:-1])]
    for s in (-1, 1):
        a, b = sorted((s * .93, s * 1.1))
        m.prism("Truck", outline, "x", a, b)
        # The strap over the window, closing the frame.
        m.box("Truck", (a, -.68, 1.03), (b, .68, 1.13), .01)
        # Cast ribs on the outer face, and a boss over each journal.
        a2, b2 = sorted((s * 1.1, s * 1.118))
        for y0, y1, z0, z1 in ((.72, .78, .56, 1.0), (1.76, 1.82, .7, 1.08)):
            m.box("Truck", (a2, y0, z0), (b2, y1, z1))
            m.box("Truck", (a2, -y1, z0), (b2, -y0, z1))
        m.box("Truck", (a2, -2.0, 1.0), (b2, -.7, 1.04))
        m.box("Truck", (a2, .7, 1.0), (b2, 2.0, 1.04))
        for e in (-1, 1):
            y = e * AXLE
            m.cyl("Truck", (s * 1.1, y, .96), (s * 1.122, y, .96), .1, 16, .085)
            # The journal box, cast into the pedestal, with its round cover and bolts.
            a3, b3 = sorted((s * .9, s * 1.14))
            m.box("Truck", (a3, y - .185, .28), (b3, y + .185, .78), .02)
            m.cyl("Truck", (s * 1.14, y, WHEEL_R), (s * 1.17, y, WHEEL_R), .15, 22, .14)
            m.cyl("Steel", (s * 1.17, y, WHEEL_R), (s * 1.21, y, WHEEL_R), .105, 20, .09)
            for k in range(6):
                t = 2 * math.pi * k / 6 + .3
                bo = (s * 1.17, y + .118 * math.cos(t), WHEEL_R + .118 * math.sin(t))
                m.cyl("Truck", bo, (bo[0] + s * .012, bo[1], bo[2]), .014, 6)
            # The pedestal tie under the box.
            m.box("Truck", (a3, y - .3, .22), (b3, y + .3, .26), .006)
            # Sand pipe, dropping to the rail ahead of the wheel.
            m.tube("Dark", [(s * .92, y + e * .42, 1.0), (s * .86, y + e * .66, .55), (s * .76, y + e * .62, .1)], .012, .05, 6)
            # The brake cylinder lying on the frame's shoulder, its rod reaching inward to the rigging.
            m.cyl("Truck", (s * 1.17, y + e * .06, 1.06), (s * 1.17, y + e * .36, 1.06), .085, 16)
            m.lathe("Truck", (s * 1.17, y + e * .36, 1.06), (0, e, 0), [(0, .085), (.03, .07), (.04, .02), (0, .02)], 16)
            m.cyl("Steel", (s * 1.17, y + e * .06, 1.06), (s * 1.17, y - e * .22, 1.06), .018, 8)
            a4, b4 = sorted((s * 1.1, s * 1.24))
            m.box("Truck", (a4, y + e * .13 - .05, .96), (b4, y + e * .13 + .05, 1.0))
            # The hose looping off the end of the frame to the body.
            m.tube("Rubber", [(s * 1.05, e * 2.16, .95), (s * 1.05, e * 2.32, 1.02), (s * 1.0, e * 2.28, 1.18)], .016, .07, 8)
        # The spring plank, slung low on the swing hangers, and the leaf springs on it.
        a5, b5 = sorted((s * .82, s * 1.3))
        m.box("Truck", (a5, -.6, .29), (b5, .6, .35), .008)
        for y in (-.5, .5):
            a6, b6 = sorted((s * 1.13, s * 1.2))
            m.box("Truck", (a6, y - .04, .27), (b6, y + .04, 1.14), .006)
            m.cyl("Steel", (s * 1.1, y, 1.08), (s * 1.23, y, 1.08), .028, 10)
            m.cyl("Steel", (s * 1.1, y, .32), (s * 1.23, y, .32), .028, 10)
        a7, b7 = sorted((s * .96, s * 1.24))
        for k in range(5):
            m.box("Truck", (a7, -.24 - k * .065, .36 + k * .025), (b7, .24 + k * .065, .382 + k * .025))
            m.box("Truck", (a7, -.24 - k * .065, .65 - k * .025), (b7, .24 + k * .065, .672 - k * .025))
        # The bolster's end, riding on the springs and out through the window.
        a8, b8 = sorted((s * .9, s * 1.28))
        m.box("Truck", (a8, -.24, .68), (b8, .24, .96), .014)
    # The bolster, its centre plate and side bearings.
    m.box("Truck", (-.92, -.26, .74), (.92, .26, 1.06), .012)
    m.cyl("Steel", (0, 0, 1.06), (0, 0, 1.19), .31, 28)
    for s in (-1, 1):
        m.box("Steel", (s * .78 - .11, -.13, 1.06), (s * .78 + .11, .13, 1.17), .008)
    # Transoms and end sills.
    for y in (-.66, .66):
        m.box("Truck", (-.95, y - .06, .9), (.95, y + .06, 1.04))
    for y in (-2.06, 2.06):
        m.box("Truck", (-.95, y - .06, .86), (.95, y + .06, 1.0))
    # The traction motors, hung on the transoms by their noses, and the gear cases.
    for e in (-1, 1):
        y = e * .845
        m.cyl("Truck", (-.53, y, .55), (.53, y, .55), .34, 26)
        m.prism("Truck", rrect(-.27, .27, -.2, .2, .08, .08, 3), "x", -.55, .55, lambda x, yy, z: (x, y + yy, .55 + z))
        for s in (-1, 1):
            m.lathe("Truck", (s * .53, y, .55), (s, 0, 0), [(0, .34), (.03, .28), (.03, .02), (0, .02)], 26)
        m.box("Truck", (-.36, e * .56 - .08, .78), (.36, e * .56 + .08, .92))
        # Axle cap bearings on the motor.
        for s in (-1, 1):
            m.cyl("Truck", (s * .42, e * AXLE, WHEEL_R), (s * .55, e * AXLE, WHEEL_R), .14, 18)
            m.hexa("Truck", ((s * .4, e * AXLE, .38), (s * .57, e * AXLE, .38), (s * .57, e * 1.05, .4), (s * .4, e * 1.05, .4),
                             (s * .4, e * AXLE, .64), (s * .57, e * AXLE, .64), (s * .57, e * 1.05, .75), (s * .4, e * 1.05, .75)))
        # The gear case: a long pot on one side, from pinion to axle.
        m.prism("Truck", rrect(-.31, .31, -.27, .27, .23, .23, 4), "x", .56, .665,
                lambda x, yy, z: (x, e * 1.12 + yy, WHEEL_R + z * .9))
        # Clasp brakes: a shoe either side of each wheel, on hangers, joined by a beam.
        for side in (-1, 1):
            inner = e * AXLE + side * .57
            for s in (-1, 1):
                outer, inn = WHEEL_R + .012, WHEEL_R + .06
                pts = []
                for k in range(7):
                    t = math.radians(-22 + 44 * k / 6)
                    pts.append((e * AXLE + side * outer * math.cos(t), WHEEL_R + outer * math.sin(t)))
                for k in range(6, -1, -1):
                    t = math.radians(-22 + 44 * k / 6)
                    pts.append((e * AXLE + side * inn * math.cos(t), WHEEL_R + inn * math.sin(t)))
                a, b = sorted((s * .70, s * .82))
                m.prism("Dark", pts, "x", a, b)
                c = s * .76
                m.tube("Truck", [(c, inner, WHEEL_R), (c, inner + side * .02, .98)], .018, 0, 8)
                m.box("Truck", (c - .05, inner - .035, WHEEL_R - .08), (c + .05, inner + .035, WHEEL_R + .08))
            m.tube("Truck", [(-.76, inner, WHEEL_R - .03), (.76, inner, WHEEL_R - .03)], .022, 0, 8)
    return m


def wheelset():
    m = Kit()
    m.cyl("Steel", (-1.0, 0, 0), (1.0, 0, 0), .095, 24)
    for s in (-1, 1):
        web = [(.60, .1), (.60, .17), (.645, .2), (.66, .4), (.6765, .445), (.8165, .445), (.8, .4), (.74, .2), (.765, .17), (.765, .1)]
        m.lathe("Truck", (0, 0, 0), (s, 0, 0), web, 44)
        rim = [(.6765, .44), (.6765, .532), (.682, .536), (.692, .535), (.7055, .514), (.7205, .508), (.8165, .5015), (.8165, .44)]
        m.lathe("Tread", (0, 0, 0), (s, 0, 0), rim, 64)
        # Plugs in the wheel's face, so that it's seen to turn.
        for k in range(3):
            t = 2 * math.pi * k / 3
            y, z = .32 * math.cos(t), .32 * math.sin(t)
            m.cyl("Dark", (s * .76, y, z), (s * .79, y, z), .03, 10)
            m.cyl("Dark", (s * .66, y, z), (s * .64, y, z), .03, 10)
    return m


def coupler():
    """A type E. The shank reaches back through the pocket to a yoke at the pivot."""
    m = Kit()
    # Yoke and butt.
    m.box("Coupler", (-.13, .05, -.11), (.13, .15, .11), .012)
    m.box("Coupler", (-.11, -.12, -.09), (.11, .05, .09), .012)
    m.cyl("Steel", (0, 0, -.13), (0, 0, .13), .03, 10)
    # The shank.
    m.loft("Coupler", [(-.1, rrect(-.075, .075, -.075, .075, .015, .015, 2)),
                        (-.7, rrect(-.1, .1, -.1, .1, .02, .02, 2))], "y")
    # The head and guard arm, seen from above.
    guard = [(-.1, -.7), (.1, -.7), (.17, -.82), (.17, -.9), (.07, -.97), (.0, -1.01), (-.08, -1.03), (-.145, -1.13), (-.185, -1.19),
             (-.25, -1.18), (-.29, -1.1), (-.285, -.97), (-.225, -.87), (-.15, -.78)]
    z0, z1 = -.145, .145
    m.loft("Coupler", [(z0 - .01, [(x * .96, y) for x, y in guard]), (z0 + .012, guard), (z1 - .012, guard), (z1 + .01, [(x * .96, y) for x, y in guard])], "z")
    # The lock's top and the lift rod.
    m.box("Coupler", (-.06, -.92, z1), (.04, -.8, z1 + .06), .012)
    m.cyl("Steel", (-.01, -.86, z1 + .06), (-.01, -.86, z1 + .1), .018, 8)
    m.tube("Steel", [(-.01, -.86, z1 + .1), (.12, -.88, z1 + .14), (.28, -1.0, z1 + .15)], .009, .03)
    # The knuckle, opening about its pin, kept at KNUCKLE_PIN.
    jaw = [(.04, -.87), (.17, -.84), (.235, -.9), (.26, -1.0), (.262, -1.1), (.215, -1.18), (.14, -1.2), (.03, -1.2), (.015, -1.15), (.05, -1.09), (.11, -1.09),
           (.14, -1.03), (.1, -.98), (.04, -.95)]
    z0, z1 = -.14, .14
    m.loft("Moving_Knuckle", [(z0 - .012, [(x * .98, y * .998) for x, y in jaw]), (z0 + .012, jaw), (z1 - .012, jaw), (z1 + .012, [(x * .98, y * .998) for x, y in jaw])], "z")
    m.cyl("Steel", (KNUCKLE_PIN[0], KNUCKLE_PIN[1], -.17), (KNUCKLE_PIN[0], KNUCKLE_PIN[1], .17), .022, 10)
    return m


def fan():
    m = Kit()
    m.cyl("Blade", (0, 0, -.03), (0, 0, .08), .13, 18)
    m.lathe("Blade", (0, 0, .08), (0, 0, 1), [(0, .13), (.03, .09), (.04, .02), (0, .02)], 18)
    for k in range(8):
        t = 2 * math.pi * k / 8
        root, tip = .12, .58
        # A swept, twisted blade, as two skins back to back.
        rows = []
        for j in range(6):
            f = j / 5
            r = root + (tip - root) * f
            pitch = math.radians(34 - 20 * f)
            chord = .17 + .05 * f
            for side in (-1, 1):
                aa = t + side * chord * .5 / r
                zz = side * math.sin(pitch) * chord * .5 + .03
                rows.append((r * math.cos(aa), r * math.sin(aa), zz))
        bm = m.bm("Blade")
        vs = [bm.verts.new(p) for p in rows]
        vt = [bm.verts.new((p[0], p[1], p[2] + .008)) for p in rows]
        for j in range(5):
            a, b, c, d = j * 2, j * 2 + 1, (j + 1) * 2 + 1, (j + 1) * 2
            bm.faces.new((vs[a], vs[b], vs[c], vs[d]))
            bm.faces.new((vt[d], vt[c], vt[b], vt[a]))
            bm.faces.new((vs[a], vs[d], vt[d], vt[a]))
            bm.faces.new((vs[c], vs[b], vt[b], vt[c]))
        bm.faces.new((vs[1], vs[0], vt[0], vt[1]))
        bm.faces.new((vs[10], vs[11], vt[11], vt[10]))
    return m


# ---------------------------------------------------------------- the assembled engine, for the preview


def clear():
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)


def place(file, at=(0, 0, 0), yaw=0.0, spin_x=0.0):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=os.path.join(OUT, file))
    for obj in set(bpy.data.objects) - before:
        obj.matrix_world = Matrix.Translation(at) @ Matrix.Rotation(yaw, 4, "Z") @ Matrix.Rotation(spin_x, 4, "X") @ obj.matrix_world


def assemble(body_file):
    clear()
    place(body_file)
    for y in (-TRUCK_CENTRE, TRUCK_CENTRE):
        place("truck.glb", (0, y, 0), 0.0 if y > 0 else math.pi)
        for a in (-AXLE, AXLE):
            place("wheelset.glb", (0, y + a, WHEEL_R), 0.0, .4)
    place("coupler.glb", (0, -COUPLER_PIVOT, COUPLER_Z))
    place("coupler.glb", (0, COUPLER_PIVOT, COUPLER_Z), math.pi)
    for y, z in [(BRAKE_FAN, BRAKE_FAN_Z), *[(y, FAN_Z) for y in RADIATOR_FANS]]:
        place("fan.glb", (0, y, z))


def combined(livery):
    """One whole engine for looking at: every part on an empty named for it, so a truck turns with its wheels on it."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    shared = {}

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
            obj.name = f"{prefix}_{obj.name.split('.')[0]}"
            for slot in obj.material_slots:
                slot.material = shared.setdefault(slot.material.name.split(".")[0], slot.material)

    root = holder("GP40-2")
    load(f"body_{livery}.glb", holder("Body", parent=root), "Body")
    for label, y, yaw in (("Front", -TRUCK_CENTRE, math.pi), ("Rear", TRUCK_CENTRE, 0.0)):
        truck_at = holder(f"Truck_{label}", (0, y, 0), yaw, root)
        load("truck.glb", truck_at, f"Truck_{label}")
        for k, a in enumerate((-AXLE, AXLE), 1):
            load("wheelset.glb", holder(f"Wheelset_{label}_{k}", (0, a, WHEEL_R), 0.0, truck_at), f"Wheelset_{label}_{k}")
    for label, y, yaw in (("Front", -COUPLER_PIVOT, 0.0), ("Rear", COUPLER_PIVOT, math.pi)):
        load("coupler.glb", holder(f"Coupler_{label}", (0, y, COUPLER_Z), yaw, root), f"Coupler_{label}")
    fans = [("Brake", BRAKE_FAN, BRAKE_FAN_Z)] + [(f"Radiator_{k}", y, FAN_Z) for k, y in enumerate(RADIATOR_FANS, 1)]
    for label, y, z in fans:
        load("fan.glb", holder(f"Fan_{label}", (0, y, z), 0.0, root), f"Fan_{label}")
    bpy.context.view_layer.update()
    # One material per paint, under its plain name.
    for mat in list(bpy.data.materials):
        if mat.users == 0:
            bpy.data.materials.remove(mat)
    for mat in bpy.data.materials:
        base = mat.name.split(".")[0]
        if base != mat.name and bpy.data.materials.get(base) is None:
            mat.name = base
    folder = os.path.join(OUT, "Assembled")
    os.makedirs(folder, exist_ok=True)
    for obj in bpy.data.objects:
        obj.select_set(True)
    bpy.ops.export_scene.gltf(filepath=os.path.join(folder, f"gp40_2_{livery}.glb"), export_format="GLB", use_selection=True, export_yup=True,
                              export_materials="EXPORT", export_normals=True, export_texcoords=False)
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(folder, f"gp40_2_{livery}.blend"), check_existing=False)
    print("wrote", folder, livery, len(bpy.data.objects), "objects")


def stage():
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 48
    scene.cycles.use_denoising = True
    scene.view_settings.view_transform = "Standard"
    ground = bpy.data.meshes.new("Ground")
    ground.from_pydata([(-200, -200, -.2), (200, -200, -.2), (200, 200, -.2), (-200, 200, -.2)], [], [(0, 1, 2, 3)])
    obj = bpy.data.objects.new("Ground", ground)
    scene.collection.objects.link(obj)
    mat = bpy.data.materials.new("GroundPaint")
    mat.use_nodes = True
    mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (.42, .42, .42, 1)
    ground.materials.append(mat)
    for s in (-1, 1):
        rail = bpy.data.meshes.new("Rail")
        x0, x1 = s * .7175, s * .79
        lo, hi = -40, 40
        rail.from_pydata([(x0, lo, -.2), (x1, lo, -.2), (x1, hi, -.2), (x0, hi, -.2), (x0, lo, 0), (x1, lo, 0), (x1, hi, 0), (x0, hi, 0)], [],
                         [(0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)])
        o = bpy.data.objects.new("Rail", rail)
        scene.collection.objects.link(o)
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.data.energy = 3.6
    sun.data.angle = math.radians(6)
    sun.rotation_euler = (math.radians(52), math.radians(8), math.radians(-40))
    scene.collection.objects.link(sun)
    world = bpy.data.worlds.new("World")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (.82, .86, .92, 1)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = .75
    scene.world = world


def shoot(name, location, target, lens=50, size=(1400, 760), ortho=None):
    scene = bpy.context.scene
    data = bpy.data.cameras.new("Camera")
    if ortho:
        data.type = "ORTHO"
        data.ortho_scale = ortho
    data.lens = lens
    data.clip_end = 500
    camera = bpy.data.objects.new("Camera", data)
    scene.collection.objects.link(camera)
    scene.camera = camera
    camera.location = location
    direction = Vector(target) - Vector(location)
    camera.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
    scene.render.resolution_x, scene.render.resolution_y = size
    scene.render.filepath = os.path.join(PREVIEW, name + ".png")
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(camera)


def previews():
    clear()
    place("coupler.glb", (0, 0, COUPLER_Z))
    stage()
    shoot("coupler", (1.3, -2.6, 2.3), (0, -.7, COUPLER_Z), 55, (1000, 700))
    clear()
    place("wheelset.glb", (0, 0, WHEEL_R))
    stage()
    shoot("wheelset", (2.2, -3.0, 1.2), (0, 0, WHEEL_R), 55, (1000, 700))
    clear()
    for livery in LIVERIES:
        assemble(f"body_{livery}.glb")
        stage()
        shoot(f"{livery}_hero", (11, -19, 4.2), (0, -1.2, 2.2), 58)
        shoot(f"{livery}_front", (0, -30, 2.3), (0, 0, 2.3), 100, (760, 760))
        shoot(f"{livery}_side", (-40, 0, 2.2), (0, 0, 2.2), size=(1600, 560), ortho=19.5)
        if livery == "csx_blue":
            shoot("rear", (6, 22, 4.5), (0, 3, 2.4), 60, (1000, 760))
            shoot("roof", (-9, 6, 12), (0, 1.5, 3.8), 50, (1200, 760))
            shoot("truck", (6.2, 1.4, 1.2), (0, TRUCK_CENTRE, .7), 55, (1100, 700))
            shoot("pilot", (3.6, -13.4, 1.5), (0, -8.8, 1.05), 55, (1100, 700))
            shoot("cab", (6.0, -11.0, 4.3), (0, -5.2, 3.0), 55, (1100, 760))
            shoot("ref3", (12, -18, 6.0), (0, -.8, 2.0), 50, (1476, 828))


for key, livery in LIVERIES.items():
    body(livery).export(os.path.join(OUT, f"body_{key}.glb"), livery["paint"])
for key, kit in (("truck", truck()), ("wheelset", wheelset()), ("coupler", coupler()), ("fan", fan())):
    kit.export(os.path.join(OUT, f"{key}.glb"), {})

assembly = {
    "lengthOverCouplers": OVER_COUPLERS,
    "trucks": [{"model": "truck.glb", "position": [0, -TRUCK_CENTRE, 0], "yaw": 180}, {"model": "truck.glb", "position": [0, TRUCK_CENTRE, 0], "yaw": 0}],
    "wheelsets": {"model": "wheelset.glb", "onTruck": [[0, -AXLE, WHEEL_R], [0, AXLE, WHEEL_R]], "radius": WHEEL_R},
    "couplers": [{"model": "coupler.glb", "position": [0, -COUPLER_PIVOT, COUPLER_Z], "yaw": 0}, {"model": "coupler.glb", "position": [0, COUPLER_PIVOT, COUPLER_Z], "yaw": 180}],
    "knucklePin": [KNUCKLE_PIN[0], KNUCKLE_PIN[1], 0],
    "fans": [{"model": "fan.glb", "position": [0, y, FAN_Z]} for y in RADIATOR_FANS] + [{"model": "fan.glb", "position": [0, BRAKE_FAN, BRAKE_FAN_Z]}],
    "bodies": [f"body_{key}.glb" for key in LIVERIES],
    "axes": "Blender Z-up metres, front towards -Y; in the files, glTF Y-up: (x, y, z) here is (x, z, -y) there",
}
with open(os.path.join(OUT, "assembly.json"), "w") as f:
    json.dump(assembly, f, indent=2)

for key in LIVERIES:
    combined(key)

if PREVIEW:
    previews()
