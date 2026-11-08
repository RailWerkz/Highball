"""Builds Highball's utility poles, light poles and loose ties in Blender and exports them for the game.

    blender -b --factory-startup --python Tools/Blender/poles.py -- <Content folder> [preview folder]

Writes these under Content/Models/Poles, and all of them side by side in Assembled/poles.blend:

    pole_telegraph     a railroad pole line's: bare wood, two crossarms of glass insulators
    pole_distribution  a street's: three phases on a crossarm, a transformer, then the phone cable
    pole_telecom       a telephone company's: no arm, a splice closure, a terminal box and a guy to hold it
    light_cobra        a roadway light: tapered steel, a davit arm and a cobra head
    light_yard_mast    a rail yard's mast: slip-jointed steel, a ring of eight floods, bollards round the foot
    light_platform     a station platform's: a black post, a ladder rest and an acorn globe
    tie_single         one bare tie, across the way it faces, for laying one at a time round any curve
    tie_section        4.95 m of ties with their plates and no rails, each a little out of true
    tie_section_ballast the same on a bed of ballast

As in lineside.py: Blender is Z-up, the exporter turns that into the game's Y-up, each model stands on the ground at the origin
facing -Y (any pole line's wires would run along Y), and every object is one paint named Fixed_<paint>, one line.frag knows
(World/NetworkMeshes.cs, Paints). Wood and Ballast alone have a map's coordinates, in metres: v along, u round. Sizes are real, in
metres. Wires between poles aren't part of these.
"""
import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
CONTENT = os.path.abspath(args[0] if args else "Content")
PREVIEW = os.path.abspath(args[1]) if len(args) > 1 else None

COLORS = {"Black": (0.02, 0.02, 0.02), "White": (0.75, 0.75, 0.72), "Galv": (0.33, 0.34, 0.35), "Yellow": (0.7, 0.5, 0.05),
          "Red": (0.6, 0.03, 0.02), "Green": (0.03, 0.4, 0.15), "Concrete": (0.35, 0.34, 0.31), "Wood": (0.13, 0.085, 0.05), "Ballast": (0.2, 0.19, 0.17)}

UP = Vector((0, 0, 1))

# The paints that wear a map on coordinates of their own.
MAPPED = ("Wood", "Ballast")


def frame(axis, hint=None):
    """Two sides square to an axis, so that (side, other, axis) is right-handed."""
    axis = axis.normalized()
    hint = hint or (Vector((0, 1, 0)) if abs(axis.z) > 0.99 else UP)
    side = hint.cross(axis).normalized()
    return side, axis.cross(side)


class Model:
    def __init__(self):
        self.parts = {}

    def bm(self, paint):
        return self.parts.setdefault(f"Fixed_{paint}", bmesh.new())

    def rings(self, paint, rings, smooth=True):
        """A closed skin through rings of as many points each, going along the way each ring's points turn anticlockwise."""
        bm = self.bm(paint)
        verts = [[bm.verts.new(p) for p in ring] for ring in rings]
        layer = bm.loops.layers.uv.verify() if paint in MAPPED else None
        n = len(rings[0])
        step = sum((rings[0][(k + 1) % n] - rings[0][k]).length for k in range(n)) / n
        along = 0.0
        for j, (low, high) in enumerate(zip(verts, verts[1:])):
            length = (sum(rings[j + 1], Vector()) - sum(rings[j], Vector())).length / n
            for k in range(n):
                k2 = (k + 1) % n
                face = bm.faces.new((low[k], low[k2], high[k2], high[k]))
                face.smooth = smooth
                if layer:
                    # (Each corner of its own, so the wrap's last face runs on past the seam.)
                    corners = {low[k]: (k * step, along), low[k2]: ((k + 1) * step, along), high[k2]: ((k + 1) * step, along + length), high[k]: (k * step, along + length)}
                    for loop in face.loops:
                        loop[layer].uv = corners[loop.vert]
            along += length

    def caps(self, paint, first, last):
        bm = self.bm(paint)
        layer = bm.loops.layers.uv.verify() if paint in MAPPED else None
        for ring in (list(reversed(first)), last):
            face = bm.faces.new([bm.verts.new(p) for p in ring])
            if layer:
                # End grain: flat across the face.
                face.normal_update()
                u, v = frame(face.normal)
                for loop in face.loops:
                    loop[layer].uv = (loop.vert.co.dot(u), loop.vert.co.dot(v))

    def lathe(self, paint, at, profile, sides=8, axis=UP):
        """Turned about an axis from at. Profile is (radius, height) going up; a None between points leaves a hard edge there."""
        at = Vector(at)
        u, v = frame(Vector(axis))
        axis = Vector(axis).normalized()

        def ring(r, h):
            return [at + axis * h + (u * math.cos(2 * math.pi * k / sides) + v * math.sin(2 * math.pi * k / sides)) * r for k in range(sides)]

        run = []
        runs = [run]
        for point in profile:
            if point is None:
                run = [runs[-1][-1]]
                runs.append(run)
            else:
                run.append(point)
        for run in runs:
            self.rings(paint, [ring(*p) for p in run])
        self.caps(paint, ring(*profile[0]), ring(*profile[-1]))

    def tube(self, paint, a, b, r0, r1=None, sides=8):
        a, b = Vector(a), Vector(b)
        self.lathe(paint, a, [(r0, 0.0), (r0 if r1 is None else r1, (b - a).length)], sides, b - a)

    def sweep(self, paint, points, r, sides=8):
        """A round section along a path of points, its radius r or a function of how far along (0 to 1)."""
        points = [Vector(p) for p in points]
        u = None
        rings = []
        for k, p in enumerate(points):
            along = points[min(k + 1, len(points) - 1)] - points[max(k - 1, 0)]
            if u is None:
                u, _ = frame(along)
            u = (u - along.normalized() * u.dot(along.normalized())).normalized()
            v = along.normalized().cross(u)
            radius = r(k / (len(points) - 1)) if callable(r) else r
            rings.append([p + (u * math.cos(2 * math.pi * s / sides) + v * math.sin(2 * math.pi * s / sides)) * radius for s in range(sides)])
        self.rings(paint, rings)
        self.caps(paint, rings[0], rings[-1])

    def beam(self, paint, a, b, width, height, up=UP):
        """A square-cut timber from a to b: width across, height along the side nearest up."""
        a, b = Vector(a), Vector(b)
        u, v = frame(b - a, Vector(up))
        corners = [(1, -1), (1, 1), (-1, 1), (-1, -1)]
        ring = lambda p: [p + u * (x * width / 2) + v * (y * height / 2) for x, y in corners]
        low, high = ring(a), ring(b)
        self.rings(paint, [low, high], smooth=False)
        self.caps(paint, low, high)

    def box(self, paint, centre, half):
        centre = Vector(centre)
        self.beam(paint, centre - Vector((0, 0, half[2])), centre + Vector((0, 0, half[2])), half[0] * 2, half[1] * 2, Vector((0, 1, 0)))

    def to_objects(self, offset=(0, 0, 0)):
        objects = []
        for name, bm in self.parts.items():
            mesh = bpy.data.meshes.new(name)
            bm.to_mesh(mesh)
            bm.free()
            obj = bpy.data.objects.new(name, mesh)
            obj.location = offset
            bpy.context.collection.objects.link(obj)
            paint = name.split("_")[1]
            mat = bpy.data.materials.get(paint) or bpy.data.materials.new(paint)
            mat.use_nodes = True
            bsdf = mat.node_tree.nodes["Principled BSDF"]
            bsdf.inputs["Base Color"].default_value = (*COLORS[paint], 1.0)
            bsdf.inputs["Roughness"].default_value = 0.75
            mesh.materials.append(mat)
            objects.append(obj)
        self.parts = {}
        return objects

    def export(self, path):
        # From an empty file each time, so the parts keep their names rather than taking a .001.
        bpy.ops.wm.read_factory_settings(use_empty=True)
        objects = self.to_objects()
        os.makedirs(os.path.dirname(path), exist_ok=True)
        for obj in objects:
            obj.select_set(True)
        bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=True, export_yup=True)
        verts = sum(len(o.data.vertices) for o in objects)
        tris = sum(len(o.data.polygons) * 2 for o in objects)
        print("wrote", path, verts, "verts, about", tris, "tris")


# What many of them have.

def pin_insulator(m, x, y, z, paint="Green", scale=1.0):
    """A pin on a crossarm with its insulator: a skirt, a wire groove and a crown."""
    m.tube("Galv", (x, y, z), (x, y, z + 0.06 * scale), 0.012 * scale, sides=5)
    s = scale
    m.lathe(paint, (x, y, z + 0.04 * s), [(0.042 * s, 0.0), (0.045 * s, 0.03 * s), (0.03 * s, 0.05 * s), (0.03 * s, 0.07 * s), (0.036 * s, 0.085 * s), (0.02 * s, 0.11 * s), (0.001, 0.115 * s)], 7)


def crossarm(m, z, half, r, out=0.65, drop=0.6):
    """An arm bolted to the pole's front, with the flat steel brace each side. Returns where along Y its pins stand."""
    y = -r - 0.0425
    m.beam("Wood", (-half, y, z), (half, y, z), 0.085, 0.11)
    m.tube("Black", (0, y - 0.05, z), (0, r + 0.02, z), 0.009, sides=5)
    for s in (-1, 1):
        m.beam("Galv", (s * out, y + 0.04, z - 0.05), (s * 0.03, -r * 0.92, z - drop), 0.032, 0.007, Vector((0, 1, 0)))
    return y


def wood_pole(m, top, r_top, r_foot, sunk=1.5):
    """Tapered over its length, with a touch of swell and a slightly crowned top."""
    m.lathe("Wood", (0, 0, -sunk), [(r_foot, 0.0), (r_foot * 0.93, sunk + 1.0), ((r_foot + r_top) * 0.5, (top + sunk) * 0.55), (r_top, top + sunk - 0.01), None,
                                    (r_top, top + sunk - 0.01), (r_top * 0.6, top + sunk + 0.025)], 9)


def steps(m, r_at, z0, z1, gap=0.46):
    """Climbing spikes, side to side up the pole. r_at gives the pole's radius at a height."""
    z = z0
    side = 1
    while z < z1:
        r = r_at(z)
        m.beam("Galv", (side * (r - 0.02), 0, z), (side * (r + 0.13), 0, z + 0.015), 0.018, 0.018)
        m.beam("Galv", (side * (r + 0.13), 0, z + 0.012), (side * (r + 0.13), 0, z + 0.05), 0.018, 0.018, Vector((1, 0, 0)))
        z += gap
        side = -side


def ground_wire(m, r_at, z0, z1, y):
    """Down the back of a pole, hugging it."""
    n = 6
    m.sweep("Galv", [(0, y * (r_at(z0 + (z1 - z0) * k / n) + 0.006), z0 + (z1 - z0) * k / n) for k in range(n + 1)], 0.006, 4)


def number_plate(m, r, z):
    m.box("White", (0, -r - 0.004, z), (0.05, 0.004, 0.07))
    m.box("Black", (0, -r - 0.009, z), (0.03, 0.002, 0.045))


def footing(m, z, half):
    """A steel base plate on its footing's top with four anchor bolts."""
    m.box("Galv", (0, 0, z + 0.015), (half, half, 0.015))
    for sx in (-1, 1):
        for sy in (-1, 1):
            m.tube("Black", (sx * half * 0.78, sy * half * 0.78, z + 0.03), (sx * half * 0.78, sy * half * 0.78, z + 0.08), half * 0.09, sides=6)


def bezier(a, b, c, d, n):
    a, b, c, d = map(Vector, (a, b, c, d))
    return [a * (1 - t) ** 3 + b * 3 * t * (1 - t) ** 2 + c * 3 * t * t * (1 - t) + d * t ** 3 for t in (k / n for k in range(n + 1))]


# The utility poles.

def pole_telegraph():
    """A railroad's pole line, as still stands along plenty of main lines: 10 m, two ten-pin arms."""
    m = Model()
    top, r_top, r_foot = 10.0, 0.1, 0.155
    r_at = lambda z: r_foot + (r_top - r_foot) * max(z, 0.0) / top
    wood_pole(m, top, r_top, r_foot)
    for z in (9.45, 8.8):
        y = crossarm(m, z, 1.5, r_at(z), out=0.62, drop=0.55)
        for s in (-1, 1):
            for x in (0.24, 0.5, 0.85, 1.12, 1.38):
                pin_insulator(m, s * x, y, z + 0.05)
    steps(m, r_at, 2.6, 8.4)
    ground_wire(m, r_at, 0.0, 9.0, 1)
    number_plate(m, r_at(2.0), 2.0)
    return m, 10.5


def pole_distribution():
    """A street's pole, 13 m: three phases above a pole-mount transformer, then the low-voltage and phone cables."""
    m = Model()
    top, r_top, r_foot = 13.0, 0.105, 0.175
    r_at = lambda z: r_foot + (r_top - r_foot) * max(z, 0.0) / top
    wood_pole(m, top, r_top, r_foot, 1.8)
    arm_z = 12.45
    arm_y = crossarm(m, arm_z, 1.2, r_at(arm_z), out=0.7, drop=0.6)
    # The middle phase rides a pin on top of the pole; the outer two on the arm.
    m.beam("Galv", (0, -r_top - 0.012, 12.75), (0, -r_top - 0.012, top + 0.14), 0.05, 0.012, Vector((0, 1, 0)))
    pin_insulator(m, 0, -r_top - 0.012, top + 0.1, "White", 1.6)
    for s in (-1, 1):
        pin_insulator(m, s * 1.08, arm_y, arm_z + 0.05, "White", 1.6)

    # A transformer on the front of the pole, hung from its bracket.
    can_z = 9.75
    y = -r_at(can_z) - 0.3
    m.lathe("Galv", (0, y, can_z), [(0.2, 0.0), (0.26, 0.012), (0.26, 0.03), None, (0.26, 0.03), (0.25, 0.05), (0.25, 0.76), None, (0.25, 0.76), (0.27, 0.77), (0.27, 0.8), (0.1, 0.87), (0.001, 0.875)], 14)
    for z in (can_z + 0.15, can_z + 0.62):
        m.beam("Black", (0, -r_at(z) + 0.03, z), (0, y + 0.22, z), 0.1, 0.03, Vector((0, 0, 1)))
    # Cooling fins down the can's sides.
    for sx in (-1, 1):
        for fy in (-0.11, -0.055, 0.0, 0.055, 0.11):
            m.box("Galv", (sx * 0.27, y + fy, can_z + 0.38), (0.035, 0.008, 0.26))
    # Its bushings, and the fused cutout on an arm above that feeds it.
    bushings = [(-0.1, y - 0.03), (0.1, y - 0.03)]
    for x, by in bushings:
        m.lathe("White", (x, by, can_z + 0.84), [(0.03, 0.0), (0.042, 0.02), (0.022, 0.04), (0.038, 0.06), (0.018, 0.09), (0.008, 0.13)], 6)
    cut_z = 11.35
    m.beam("Galv", (0, -r_at(cut_z) + 0.02, cut_z), (0, -r_at(cut_z) - 0.28, cut_z), 0.05, 0.05)
    m.lathe("White", (-0.24, y + 0.02, cut_z - 0.18), [(0.022, 0.0), (0.042, 0.03), (0.02, 0.07), (0.042, 0.1), (0.02, 0.14), (0.042, 0.17), (0.02, 0.2), (0.042, 0.23), (0.02, 0.3)], 6, Vector((0.1, 0, 1)))
    m.beam("Galv", (-0.21, y + 0.02, cut_z - 0.18), (0, y + 0.02, cut_z), 0.03, 0.012, Vector((0, 1, 0)))
    # The leads: phase to cutout to bushing, then the tank's ground.
    m.sweep("Black", bezier((-1.08, arm_y, arm_z + 0.2), (-0.8, y * 0.5, arm_z - 0.3), (-0.4, y, cut_z + 0.4), (-0.21, y + 0.02, cut_z + 0.12), 8), 0.007, 4)
    m.sweep("Black", bezier((-0.25, y + 0.02, cut_z - 0.18), (-0.25, y + 0.02, cut_z - 0.6), (-0.1, y - 0.03, can_z + 1.3), (-0.1, y - 0.03, can_z + 0.97), 8), 0.007, 4)
    m.sweep("Black", bezier((0.1, y - 0.03, can_z + 0.97), (0.14, y - 0.03, can_z + 1.25), (0.12, y + 0.1, cut_z - 0.5), (0.02, -r_at(11.0) - 0.008, 11.0), 8), 0.007, 4)

    # The neutral and secondary rack, then the phone cable with its clamp.
    for z in (9.25, 9.05, 8.85):
        m.lathe("White", (0, -r_at(z) - 0.09, z - 0.05), [(0.03, 0.0), (0.04, 0.012), (0.024, 0.03), (0.04, 0.09), (0.03, 0.1)], 6)
    m.beam("Black", (0, -r_at(9.05) - 0.012, 8.75), (0, -r_at(9.05) - 0.012, 9.35), 0.05, 0.012, Vector((0, 1, 0)))
    for z in (5.7, 5.2):
        m.beam("Galv", (0, -r_at(z) - 0.01, z), (0, -r_at(z) - 0.1, z), 0.05, 0.015)
        m.tube("Black", (0, -r_at(z) - 0.11, z - 0.03), (0.0, -r_at(z) - 0.11, z + 0.03), 0.03, sides=6)
    # A riser up the foot of the pole, and a ground wire all the way.
    m.lathe("Galv", (0, r_at(1.5) + 0.03, 0.0), [(0.03, 0.0), (0.03, 4.0)], 6)
    ground_wire(m, r_at, 0.0, top - 0.1, -1)
    number_plate(m, r_at(1.9), 1.9)
    steps(m, r_at, 2.8, 8.2)
    return m, 13.6


def pole_telecom():
    """A telephone company's pole, 9 m, where a span ends: a splice closure, a terminal, a riser and a guy."""
    m = Model()
    top, r_top, r_foot = 9.0, 0.095, 0.145
    r_at = lambda z: r_foot + (r_top - r_foot) * max(z, 0.0) / top
    wood_pole(m, top, r_top, r_foot)

    # The strand and its clamp, and the cable lashed under it through a closure on the side of the pole.
    strand = 7.7
    off = -r_at(strand) - 0.1
    m.beam("Galv", (0, -r_at(strand) + 0.02, strand), (0, off, strand), 0.06, 0.02)
    m.tube("Black", (0, -r_at(strand) - 0.02, strand), (0, r_at(strand) + 0.03, strand), 0.009, sides=5)
    m.tube("Galv", (0, off - 0.35, strand), (0, off + 0.55, strand), 0.006, sides=4)
    m.tube("Black", (0, off - 0.35, strand - 0.05), (0, off + 0.55, strand - 0.05), 0.012, sides=5)
    # The closure.
    m.lathe("Black", (0, off - 0.28, strand - 0.16), [(0.012, 0.0), (0.05, 0.03), (0.075, 0.08), None, (0.075, 0.08), (0.08, 0.09), (0.08, 0.13), (0.075, 0.14), None,
                                                       (0.075, 0.14), (0.075, 0.39), None, (0.075, 0.39), (0.08, 0.4), (0.08, 0.44), (0.075, 0.45), None, (0.075, 0.45), (0.05, 0.51), (0.012, 0.54)], 9, Vector((0, 1, 0)))
    for k in (-0.2, 0.2):
        m.beam("Galv", (0, off + k + 0.02, strand), (0, off + k + 0.02, strand - 0.09), 0.02, 0.008, Vector((0, 1, 0)))

    # A second, lower cable on its own clamp.
    z = 7.15
    m.beam("Galv", (r_at(z) - 0.02, 0, z), (r_at(z) + 0.08, 0, z), 0.015, 0.05)
    m.tube("Black", (r_at(z) + 0.09, -0.3, z), (r_at(z) + 0.09, 0.3, z), 0.011, sides=5)

    # The terminal box, with a drop cable up from it to the strand and the riser below.
    box_z = 5.35
    box_y = -r_at(box_z) - 0.07
    m.box("Galv", (0, box_y, box_z), (0.11, 0.065, 0.2))
    m.box("Galv", (0, box_y - 0.068, box_z), (0.1, 0.004, 0.18))
    m.box("Black", (0.07, box_y - 0.073, box_z), (0.012, 0.003, 0.025))
    m.lathe("Galv", (0, box_y, box_z + 0.2), [(0.1, 0.0), (0.09, 0.012), (0.04, 0.03)], 8)
    m.sweep("Black", bezier((0, box_y, box_z + 0.21), (0, box_y, 6.1), (0, off, 6.9), (0, off + 0.02, strand - 0.06), 7), 0.008, 4)
    m.lathe("Galv", (0, -r_at(2) - 0.022, 0.0), [(0.022, 0.0), (0.022, box_z - 0.2)], 6)
    m.lathe("Galv", (0, -r_at(2) - 0.022, 0.0), [(0.03, 0.0), (0.03, 2.4)], 6)
    for z in (0.7, 1.5, 2.2, 3.2, 4.3):
        m.tube("Galv", (0, -r_at(2) + 0.02, z), (0, -r_at(2) - 0.06, z), 0.03 if z < 2.4 else 0.024, sides=6)

    # The guy that takes the span's pull: its clamp, the wire, a guard over its foot and the anchor rod.
    guy_top = (0, r_at(7.35) + 0.008, 7.35)
    anchor = (0, 3.6, 0.15)
    wire = Vector(anchor) - Vector(guy_top)
    m.beam("Galv", (0, r_at(7.35) - 0.02, 7.35), (0, r_at(7.35) + 0.03, 7.35), 0.05, 0.05)
    m.tube("Galv", guy_top, anchor, 0.0075, sides=5)
    m.tube("Yellow", Vector(anchor) - wire.normalized() * 2.4, anchor, 0.024, sides=7)
    m.tube("Galv", anchor, (0, anchor[1] + 0.3, anchor[2] - 0.65), 0.016, sides=6)
    m.lathe("Black", (0, anchor[1], anchor[2] - 0.02), [(0.03, 0.0), (0.03, 0.05)], 6)

    steps(m, r_at, 2.9, 7.0)
    ground_wire(m, r_at, 0.0, 8.9, 1)
    number_plate(m, r_at(1.9), 1.9)
    return m, 9.6


# The light poles.

def light_cobra():
    """A roadway light on a 9.5 m tapered steel pole, reaching out 2.4 m towards -Y."""
    m = Model()
    m.lathe("Concrete", (0, 0, -0.3), [(0.34, 0.0), (0.34, 0.75), None, (0.34, 0.75), (0.31, 0.78), (0.3, 0.78)], 14)
    footing(m, 0.48, 0.2)
    shaft_top = 9.2
    m.lathe("Galv", (0, 0, 0.5), [(0.105, 0.0), (0.056, shaft_top - 0.5), None, (0.056, shaft_top - 0.5), (0.04, shaft_top - 0.48)], 12)
    m.lathe("Galv", (0, 0, 0.53), [(0.13, 0.0), (0.112, 0.08)], 12)
    # The handhole: its cover and the grounding lug below.
    m.box("Galv", (0, -0.097, 1.1), (0.055, 0.012, 0.11))
    m.box("Black", (0, -0.11, 1.1), (0.012, 0.004, 0.012))
    # The davit arm, swept up and out from the shaft's top.
    head = Vector((0, -2.38, 10.28))
    path = bezier((0, 0, shaft_top - 0.25), (0, 0, shaft_top + 0.6), (0, -1.0, 10.15), head, 12)
    m.sweep("Galv", path, lambda t: 0.052 - 0.018 * t, 10)
    # The cobra head: a body tilted up a little, with its bowl beneath and a photocell on top.
    tilt = math.radians(6)
    along = Vector((0, -math.cos(tilt), math.sin(tilt)))
    up = Vector((0, math.sin(tilt), math.cos(tilt)))
    start = head + along * -0.05

    def section(t, w, top_h, low_h):
        """A squared oval, its top and bottom set separately, t along the head."""
        points = []
        n = 12
        for k in range(n):
            a = 2 * math.pi * (k + 0.5) / n
            c, s = math.cos(a), math.sin(a)
            x = -math.copysign(abs(c) ** 0.7, c) * w
            h = (top_h if s > 0 else low_h) * math.copysign(abs(s) ** 0.7, s)
            points.append(start + along * t + Vector((x, 0, 0)) + up * h)
        return points

    body = [(0.0, 0.045, 0.035, 0.035), (0.1, 0.1, 0.075, 0.06), (0.26, 0.17, 0.09, 0.05), (0.5, 0.185, 0.085, 0.045), (0.7, 0.16, 0.065, 0.035), (0.8, 0.1, 0.03, 0.02)]
    rings = [section(*row) for row in body]
    m.rings("Galv", rings)
    m.caps("Galv", rings[0], rings[-1])
    # A bowl under the front two-thirds of the head.
    bowl = [(0.26, 0.13, 0.0, 0.05), (0.33, 0.145, 0.0, 0.09), (0.5, 0.15, 0.0, 0.1), (0.66, 0.125, 0.0, 0.065), (0.74, 0.08, 0.0, 0.03)]
    rings = [[p for p in section(t, w, 0.005, low)] for t, w, _, low in bowl]
    m.rings("White", rings)
    m.caps("White", rings[0], rings[-1])
    cell = start + along * 0.3 + up * 0.085
    m.lathe("Black", cell, [(0.028, 0.0), (0.028, 0.035), (0.034, 0.04), (0.034, 0.075), (0.02, 0.085)], 8, up)
    return m, 10.7


def light_yard_mast():
    """A rail yard's mast, 24 m in three slip-jointed sections, with eight floods on a ring."""
    m = Model()
    m.lathe("Concrete", (0, 0, -0.4), [(0.85, 0.0), (0.85, 0.9), None, (0.85, 0.9), (0.8, 0.93), (0.72, 0.93)], 18)
    footing(m, 0.53, 0.5)
    shaft = [(0.36, 0.55), (0.285, 8.5), (0.3, 8.5), (0.3, 8.9), (0.225, 16.2), (0.24, 16.2), (0.24, 16.6), (0.13, 24.0)]
    profile = []
    for k, (r, z) in enumerate(shaft):
        if k in (2, 3, 5, 6):
            profile.append(None)
        profile.append((r, z - 0.55))
    m.lathe("Galv", (0, 0, 0.55), profile, 12)
    m.lathe("Galv", (0, 0, 0.58), [(0.46, 0.0), (0.37, 0.09)], 12)
    # The base's door, and a conduit and disconnect box at the foot.
    m.box("Galv", (0, -0.345, 1.45), (0.14, 0.016, 0.3))
    m.box("Black", (0.1, -0.363, 1.45), (0.012, 0.005, 0.03))
    m.box("Black", (0.6, 0, 1.25), (0.13, 0.08, 0.2))
    m.tube("Galv", (0.6, 0, 0.53), (0.6, 0, 1.05), 0.022, sides=6)
    m.beam("Galv", (0.36, 0, 1.35), (0.47, 0, 1.35), 0.06, 0.06)
    m.box("Yellow", (0.6, -0.082, 1.25), (0.055, 0.002, 0.05))
    # Bollards round the foot, against whatever a yard swings past.
    for sx in (-1, 1):
        for sy in (-1, 1):
            m.lathe("Yellow", (sx * 1.15, sy * 1.15, -0.2), [(0.085, 0.0), (0.085, 1.38), (0.05, 1.42), (0.001, 1.44)], 9)
    # The head: an octagonal ring on spokes, the floods on it looking out and down, a mast cap and a lightning rod.
    z = 23.95
    ring_r = 1.2
    n = 8
    corner = [Vector((ring_r * math.cos(2 * math.pi * k / n), ring_r * math.sin(2 * math.pi * k / n), z)) for k in range(n)]
    for k in range(n):
        m.tube("Galv", corner[k], corner[(k + 1) % n], 0.035, sides=6)
    for k in range(0, n, 2):
        m.tube("Galv", (corner[k].x * 0.08, corner[k].y * 0.08, z - 0.15), corner[k], 0.035, sides=6)
    m.lathe("Galv", (0, 0, 23.6), [(0.2, 0.0), (0.2, 0.4), None, (0.2, 0.4), (0.13, 0.46), (0.03, 0.5)], 12)
    m.tube("Black", (0, 0, 24.1), (0, 0, 25.0), 0.012, 0.005, sides=5)
    aim = 28 * math.pi / 180
    for k in range(n):
        a = 2 * math.pi * (k + 0.5) / n
        out = Vector((math.cos(a), math.sin(a), 0))
        look = (out * math.cos(aim) - UP * math.sin(aim)).normalized()
        side = UP.cross(out).normalized()
        mid = (corner[k] + corner[(k + 1) % n]) * 0.5
        at = mid + out * 0.2 + UP * 0.1
        # A yoke from the ring, the housing, its lens and its bezel.
        m.tube("Black", mid + UP * 0.02, at, 0.018, sides=5)
        for s in (-1, 1):
            m.beam("Black", at + side * s * 0.25 - look * 0.1, at + side * s * 0.25 + look * 0.06, 0.018, 0.09, UP)
        m.beam("Black", at + side * 0.25, at - side * 0.25, 0.018, 0.018, UP)
        m.beam("Galv", at - look * 0.14, at + look * 0.08, 0.46, 0.4, look.cross(side) * (1 if look.cross(side).z > 0 else -1))
        m.beam("Black", at + look * 0.08, at + look * 0.11, 0.48, 0.42, look.cross(side) * (1 if look.cross(side).z > 0 else -1))
        m.beam("White", at + look * 0.08, at + look * 0.125, 0.4, 0.34, look.cross(side) * (1 if look.cross(side).z > 0 else -1))
    return m, 25.2


def light_platform():
    """A station platform's lamp, 4.2 m: a stepped cast foot, a slender post, a ladder rest and an acorn globe."""
    m = Model()
    m.lathe("Black", (0, 0, -0.05), [(0.17, 0.0), (0.17, 0.09), None, (0.17, 0.09), (0.14, 0.12), (0.135, 0.3), None, (0.135, 0.3), (0.11, 0.33), (0.1, 0.62), None,
                                     (0.1, 0.62), (0.075, 0.68), (0.07, 0.72), (0.058, 0.78), (0.05, 3.25), None,
                                     (0.05, 3.25), (0.062, 3.27), (0.062, 3.31), (0.045, 3.33), (0.04, 3.72), None,
                                     (0.04, 3.72), (0.08, 3.76), (0.09, 3.78)], 12)
    # The ladder rest: a bar with balled ends, on a collar.
    m.tube("Black", (-0.34, 0, 3.29), (0.34, 0, 3.29), 0.011, sides=6)
    for s in (-1, 1):
        m.lathe("Black", (s * 0.34, 0, 3.26), [(0.012, 0.0), (0.024, 0.015), (0.03, 0.03), (0.024, 0.045), (0.012, 0.06)], 7)
    # A gallery of brackets into the lantern's seat.
    for k in range(4):
        a = math.pi * k / 2
        m.sweep("Black", bezier((0.04 * math.cos(a), 0.04 * math.sin(a), 3.5), (0.05 * math.cos(a), 0.05 * math.sin(a), 3.62), (0.1 * math.cos(a), 0.1 * math.sin(a), 3.72), (0.085 * math.cos(a), 0.085 * math.sin(a), 3.77), 4), 0.008, 4)
    # The globe and its cap.
    m.lathe("White", (0, 0, 3.77), [(0.085, 0.0), (0.135, 0.07), (0.16, 0.16), (0.155, 0.28), (0.125, 0.39), (0.08, 0.47), (0.05, 0.5)], 14)
    m.lathe("Black", (0, 0, 4.25), [(0.058, 0.0), (0.075, 0.015), (0.04, 0.06), (0.018, 0.09), (0.03, 0.11), (0.018, 0.13), (0.001, 0.19)], 10)
    return m, 4.6


# The ties: as track_parts.py's, 8 ft 6 in x 9 in x 7 in at 19.5 in centres.

TIE_LENGTH, TIE_WIDTH, TIE_DEPTH, TIE_SPACING = 2.5908, 0.2286, 0.1778, 0.495
TIES_IN_SECTION = 10
SECTION = TIE_SPACING * TIES_IN_SECTION


def tie(m, y, bottom, skew=0.0, shift=0.0, plates=False):
    """A tie across at y, its ends along X; skew turns it a little (metres at its end), shift moves it along itself."""
    half = TIE_LENGTH / 2
    m.beam("Wood", (shift - half, y - skew, bottom + TIE_DEPTH / 2), (shift + half, y + skew, bottom + TIE_DEPTH / 2), TIE_WIDTH, TIE_DEPTH)
    if plates:
        top = bottom + TIE_DEPTH
        for side in (-1, 1):
            x = shift + side * 0.7175
            at = y + skew * side * 0.55
            m.box("Black", (x, at, top + 0.01), (0.18, 0.1, 0.01))
            # The spikes, standing proud of the plate either side of where the rail was.
            for sx in (-0.11, 0.11):
                m.box("Black", (x + sx, at, top + 0.03), (0.012, 0.012, 0.015))


def section_ties(m, bottom):
    # (Fixed, so a section comes out the same every time it's built.)
    wander = [(0.012, 0.01, 0.004), (-0.018, -0.02, -0.007), (0.006, 0.03, 0.009), (0.02, -0.012, 0.0), (-0.01, 0.018, -0.011),
              (0.016, -0.026, 0.006), (-0.022, 0.006, 0.012), (0.004, 0.024, -0.005), (-0.014, -0.016, 0.008), (0.01, 0.012, -0.009)]
    for k, (skew, shift, along) in enumerate(wander):
        tie(m, (k + 0.5) * TIE_SPACING - SECTION / 2 + along, bottom, skew, shift, plates=True)


def tie_single():
    """One bare tie, let a little into the ground."""
    m = Model()
    tie(m, 0.0, -0.06)
    return m, 1.5


def tie_section():
    """No rails and no ballast: ties with their plates, as where a track's been lifted, let a little into the ground."""
    m = Model()
    section_ties(m, -0.06)
    return m, 3.0


def tie_section_ballast():
    """The same on a bed of ballast, whose sides run down into the ground; its ends butt up to the next section's."""
    m = Model()
    top, foot = 0.1, -0.35
    half_top, half_foot = 1.7, 2.45
    ends = [[(-half_foot, y, foot), (-half_top, y, top), (half_top, y, top), (half_foot, y, foot)] for y in (-SECTION / 2, SECTION / 2)]
    rings = [[Vector(p) for p in ring] for ring in ends]
    m.rings("Ballast", rings, smooth=False)
    m.caps("Ballast", rings[0], rings[1])
    section_ties(m, top - 0.1)
    return m, 3.0


UTILITY = [("pole_telegraph", pole_telegraph), ("pole_distribution", pole_distribution), ("pole_telecom", pole_telecom)]
LIGHTS = [("light_cobra", light_cobra), ("light_yard_mast", light_yard_mast), ("light_platform", light_platform)]
TIES = [("tie_single", tie_single), ("tie_section", tie_section), ("tie_section_ballast", tie_section_ballast)]


def aim(camera, location, target):
    d = Vector(target) - Vector(location)
    camera.location = location
    camera.rotation_euler = d.to_track_quat("-Z", "Y").to_euler()


def setup_look():
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 48
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.data.energy = 3.5
    sun.data.angle = math.radians(3)
    sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(-30))
    scene.collection.objects.link(sun)
    world = bpy.data.worlds.new("World")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.62, 0.68, 0.76, 1.0)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.8
    scene.world = world
    ground = bpy.data.meshes.new("Ground")
    ground.from_pydata([(-200, -200, 0), (200, -200, 0), (200, 200, 0), (-200, 200, 0)], [], [(0, 1, 2, 3)])
    obj = bpy.data.objects.new("Ground", ground)
    scene.collection.objects.link(obj)
    mat = bpy.data.materials.new("Ground")
    mat.use_nodes = True
    mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.3, 0.27, 0.2, 1.0)
    ground.materials.append(mat)
    camera = bpy.data.objects.new("Camera", bpy.data.cameras.new("Camera"))
    scene.collection.objects.link(camera)
    scene.camera = camera
    return scene, camera


def shoot(scene, camera, path, location, target, lens, size):
    aim(camera, location, target)
    camera.data.lens = lens
    scene.render.resolution_x, scene.render.resolution_y = size
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


folder = os.path.join(CONTENT, "Models", "Poles")
for name, builder in UTILITY + LIGHTS + TIES:
    model, _ = builder()
    model.export(os.path.join(folder, f"{name}.glb"))

# Where the close preview of each looks: the point, and how far back.
CLOSE = {"pole_telegraph": ((0, 0, 9.0), 3.6), "pole_distribution": ((0, -0.2, 11.0), 4.6), "pole_telecom": ((0, 0, 6.6), 3.6),
         "light_cobra": ((0, -1.9, 10.1), 2.2), "light_yard_mast": ((0, 0, 24.0), 4.8), "light_platform": ((0, 0, 3.8), 1.8),
         "tie_single": ((0, 0, 0.1), 2.2), "tie_section": ((0, 0, 0.1), 4.0), "tie_section_ballast": ((0, 0, 0.1), 4.5)}

# All six side by side for looking at, each in a collection of its own.
bpy.ops.wm.read_factory_settings(use_empty=True)
heights = {}
x = 0.0
places = {}
for name, builder in UTILITY + LIGHTS + TIES:
    model, height = builder()
    collection = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(collection)
    places[name] = x
    heights[name] = height
    for obj in model.to_objects((x, 0, 0)):
        bpy.context.collection.objects.unlink(obj)
        obj.name = f"{name}_{obj.name}"
        collection.objects.link(obj)
    x += 7.0
scene, camera = setup_look()
assembled = os.path.join(folder, "Assembled", "poles.blend")
os.makedirs(os.path.dirname(assembled), exist_ok=True)
bpy.ops.wm.save_as_mainfile(filepath=assembled)
print("wrote", assembled)

if PREVIEW:
    middle = (places["pole_telegraph"] + places["tie_section_ballast"]) / 2
    shoot(scene, camera, os.path.join(PREVIEW, "all.png"), (middle - 4, -60, 5), (middle, 0, 12.8), 35, (1800, 900))
    for name, height in heights.items():
        at = places[name]
        shoot(scene, camera, os.path.join(PREVIEW, f"{name}.png"), (at + height * 0.55, -height * 1.25, height * 0.62), (at, 0, height * 0.5), 42, (560, 800))
        (cx, cy, cz), reach = CLOSE[name]
        shoot(scene, camera, os.path.join(PREVIEW, f"{name}_top.png"), (at + cx + reach * 0.8, cy - reach * 1.5, cz + reach * 0.3), (at + cx, cy, cz), 50, (700, 700))
    shoot(scene, camera, os.path.join(PREVIEW, "light_yard_mast_foot.png"), (places["light_yard_mast"] + 2.6, -4.5, 2.2), (places["light_yard_mast"], 0, 0.8), 40, (700, 700))
