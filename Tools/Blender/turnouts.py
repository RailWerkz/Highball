"""Builds Highball's switch kit in Blender and exports it for the game, with a picture of each size for its switch picker.

    blender -b --factory-startup --python Tools/Blender/turnouts.py -- <Content folder> [preview folder] [--kit]

Writes Models/Track/switch_kit_r.glb and switch_kit_l.glb (right and left hand) under the Content folder: the parts of a
switch that have a look of their own, which the game fits to each switch's routes, whatever curve and frog angle those
come out at (World/NetworkMeshes.cs). The rails it sweeps along the routes itself. Each part says below how it's fitted:

  Machine        the point machine on its skids, as it stands: X across and Y along the normal route from the point of switch
  FrogCasting    X from -1 to 1 across the frog, as wide as the rails on it have parted; Y along it from the frog point
  Stretcher      X from -1 to 1 between the two blades, wherever they are
  ThrowRod       X from -1 to 1 from the point machine to the first stretcher
  CrossingCasting  under a diamond's frog: X and Y from -1 to 1 along the two tracks, however they cross

Unless --kit is given, also writes turnout_N.png for each size N in SIZES (a look at a standard right-hand turnout) from
the whole turnout built below, and, given a preview folder, renders the No. 10 there: from above, and close up set for
each route.

The rails stop FROG_HEEL past the frog (beyond it is plain track, which the game lays); the timbers run on to
TIMBER_TAIL past it. The geometry is the one the game fits junctions to (World/TurnoutFitting.cs): the diverging route leaves the normal
one tangent at the point of switch on a lead curve of radius R, out to the frog, where the two routes' inner rails
cross (their centrelines two rail spacings apart), then runs straight on at the frog angle. Everything is measured
along the normal route.

Blender is Z-up; the glTF exporter turns that into the game's Y-up. X is across the track (the diverging side of a
right-hand turnout is +X), Y along it from the point of switch, and the origin is on the normal route's centreline at
the point of switch, at the crib ballast's surface (where the game stands its ties). Each part is its own object,
named for the game to find:

  StockStraight, StockCurved   the stock rails, through the whole turnout
  BladeStraight, BladeCurved   the switch blades, each modelled closed with its origin at its heel (the game swings the
                               open one about it)
  Stretchers                   the stretcher bars tying the blades together, at their closed-straight position
  ThrowRod                     from the point machine to the first stretcher
  Closures                     the closure rails, heel to frog, each running on as a wing rail
  Frog, FrogCasting            the frog's point rails, and the casting under them
  Guards                       a guard rail facing the frog inside each outer rail
  Timbers                      the switch timbers, lengthening as the routes part
  Plates                       a plate under every rail on every timber
  Machine                      the point machine, on two long timbers on the straight side
"""

import math
import os
import sys

import bpy
import bmesh

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
KIT_ONLY = "--kit" in args
args = [a for a in args if a != "--kit"]
CONTENT = os.path.abspath(args[0] if args else "Content")
PREVIEW = os.path.abspath(args[1]) if len(args) > 1 else None

INCH = 0.0254
GAUGE = 1.435
HEAD_HALF = 0.0373
RAIL_OFFSET = GAUGE / 2 + HEAD_HALF         # rail centre from track centre
RAIL_TOP = 0.1857
TIE_RISE = 0.02                              # timber top above the crib ballast (the origin)
PLATE_DEPTH = 0.014
RAIL_BASE = TIE_RISE + PLATE_DEPTH
TIMBER_WIDTH = 9 * INCH
TIMBER_DEPTH = 7 * INCH
TIE_LENGTH = 102 * INCH
TIMBER_SPACING = 0.495
THROW = 0.12                                 # blade tip travel
INSET = 0.07                                 # a closed blade's tip inside its stock rail
HEEL_SPREAD = 0.14                           # the routes' rails apart at the blades' heels, so an open blade clears its stock rail
FLANGEWAY = 1.875 * INCH                     # the gap a wheel flange runs in, beside the frog point and the guard rails
FROG_POINT = 0.5 * INCH                      # the frog point's blunt tip
GUARD_LENGTH = 5.0
GUARD_OFF = 2 * HEAD_HALF + FLANGEWAY        # a guard rail's centre from its running rail's
TIMBER_TAIL = 4.0
FROG_HEEL = 2.5                              # past the frog the rails are plain track, which the game lays

# 136 lb rail, from the outside of the foot over the head to the other side (u across, v up).
RAIL = [
    (-0.0762, 0.0), (-0.0762, 0.011), (-0.012, 0.030), (-0.0088, 0.045), (-0.0088, 0.125), (-0.025, 0.140),
    (-0.0373, 0.145), (-0.0373, 0.178), (-0.030, RAIL_TOP), (0.030, RAIL_TOP), (0.0373, 0.178), (0.0373, 0.145),
    (0.025, 0.140), (0.0088, 0.125), (0.0088, 0.045), (0.012, 0.030), (0.0762, 0.011), (0.0762, 0.0),
]

SIZES = {6: 80.0, 7: 112.0, 8: 145.0, 10: 237.0, 15: 500.0, 20: 890.0}


class Geometry:
    """The routes of a turnout of one size and hand: offsets and headings along the normal route."""

    def __init__(self, number, side):
        self.number = number
        self.side = side
        self.radius = SIZES[number]
        self.frog = math.sqrt(4 * self.radius * RAIL_OFFSET)
        self.frog_angle = self.frog / self.radius
        self.blade = min(max(math.sqrt(2 * self.radius * HEEL_SPREAD), 3.0), 16.0)
        self.end = self.frog + TIMBER_TAIL     # the timbers
        self.rail_end = self.frog + FROG_HEEL

    def offset(self, s):
        """How far the diverging route's centreline is from the normal one's, s metres along."""
        if s <= 0:
            return 0.0
        if s <= self.frog:
            return s * s / (2 * self.radius)
        return self.frog * self.frog / (2 * self.radius) + (s - self.frog) * self.frog_angle

    def heading(self, s):
        """The diverging route's slope off the normal one (dx/dy)."""
        return min(max(s, 0.0), self.frog) / self.radius

    def normal(self, s, u):
        """A point u across from the normal route's centreline (towards the diverging side), s along."""
        return (self.side * u, s)

    def diverging(self, s, u):
        """A point u across from the diverging route's centreline (towards its outside), s along the normal route."""
        slope = self.heading(s)
        length = math.hypot(1.0, slope)
        across = (1.0 / length, -slope / length)   # the diverging route's right, before handing
        x = self.offset(s) + u * across[0]
        y = s + u * across[1]
        return (self.side * x, y)


# --- Building meshes. ---

def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 32
    scene.unit_settings.system = "METRIC"


def material(name, color, metallic=0.0, roughness=0.6):
    mat = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    mat.use_nodes = True
    shader = mat.node_tree.nodes["Principled BSDF"]
    shader.inputs["Base Color"].default_value = (*color, 1.0)
    shader.inputs["Metallic"].default_value = metallic
    shader.inputs["Roughness"].default_value = roughness
    return mat


def new_object(name, bm, mat, origin=(0.0, 0.0, 0.0)):
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    obj.data.materials.append(mat)
    if origin != (0.0, 0.0, 0.0):
        mesh.transform(__import__("mathutils").Matrix.Translation((-origin[0], -origin[1], -origin[2])))
        obj.location = origin
    for poly in mesh.polygons:
        poly.use_smooth = False
    return obj


def sweep(bm, path, profile, base=RAIL_BASE):
    """A section swept along a path: path is (x, y, rx, ry) points (a point and the horizontal right there); profile(i)
    gives the section at point i as (u, v), u along the right, v up from base. The ends are capped."""
    rings = []
    for i, (x, y, rx, ry) in enumerate(path):
        rings.append([bm.verts.new((x + rx * u, y + ry * u, base + v)) for u, v in profile(i)])
    count = len(rings[0])
    for a, b in zip(rings, rings[1:]):
        for k in range(count):
            k2 = (k + 1) % count
            bm.faces.new((a[k], a[k2], b[k2], b[k]))
    bm.faces.new(list(reversed(rings[0])))
    bm.faces.new(rings[-1])


def rail_path(point_at, s0, s1, step=0.5):
    """(x, y, rx, ry) along a rail from s0 to s1: point_at(s) gives its centre, the right from the direction it runs."""
    n = max(1, math.ceil((s1 - s0) / step))
    points = [point_at(s0 + (s1 - s0) * k / n) for k in range(n + 1)]
    path = []
    for i, (x, y) in enumerate(points):
        a = points[max(i - 1, 0)]
        b = points[min(i + 1, n)]
        fx, fy = b[0] - a[0], b[1] - a[1]
        length = math.hypot(fx, fy) or 1.0
        path.append((x, y, fy / length, -fx / length))
    return path


def rail(bm, point_at, s0, s1, profile=None):
    path = rail_path(point_at, s0, s1)
    sweep(bm, path, profile or (lambda i: RAIL))


def blade_profile(path_length, away):
    """The planed blade: thin and low at the tip (point 0), the full rail at the heel; away is the side its stock rail
    is on, which the planing keeps its back to."""
    def profile(i, n=path_length):
        t = i / max(n - 1, 1)
        thin = 0.3 + 0.7 * math.sqrt(t)
        low = 0.82 + 0.18 * t
        return [(u * thin + away * HEAD_HALF * (1 - thin), v * low) for u, v in RAIL]
    return profile


def box(bm, center, right, forward, half_across, half_along, bottom, top):
    """A box: center (x, y), right and forward unit vectors, half sizes, from bottom to top."""
    cx, cy = center
    corners = []
    for sa, sf in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
        corners.append((cx + right[0] * half_across * sa + forward[0] * half_along * sf,
                        cy + right[1] * half_across * sa + forward[1] * half_along * sf))
    low = [bm.verts.new((x, y, bottom)) for x, y in corners]
    high = [bm.verts.new((x, y, top)) for x, y in corners]
    bm.faces.new(list(reversed(low)))
    bm.faces.new(high)
    for k in range(4):
        k2 = (k + 1) % 4
        bm.faces.new((low[k], low[k2], high[k2], high[k]))


# --- The turnout. ---

def build(number, side):
    g = Geometry(number, side)
    s = side
    h = RAIL_OFFSET
    steel = material("Rail", (0.32, 0.22, 0.16), metallic=0.8, roughness=0.45)
    timber = material("Timber", (0.20, 0.15, 0.11), roughness=0.9)
    plate_mat = material("Plate", (0.14, 0.11, 0.09), metallic=0.6, roughness=0.7)
    casting = material("Casting", (0.18, 0.16, 0.14), metallic=0.7, roughness=0.5)
    machine_mat = material("Machine", (0.30, 0.31, 0.30), metallic=0.3, roughness=0.6)
    start = -1.5
    objects = []

    def n_at(u):
        return lambda t: g.normal(t, u)

    def d_at(u):
        return lambda t: g.diverging(t, u)

    # Stock rails: the straight one on the normal route's far side; the curved one on the diverging route's outside
    # (the same line as the normal route's near rail up to the point of switch).
    bm = bmesh.new()
    rail(bm, n_at(-h), start, g.rail_end)
    objects.append(new_object("StockStraight", bm, steel))
    bm = bmesh.new()
    rail(bm, lambda t: g.normal(t, h) if t < 0 else g.diverging(t, h), start, g.rail_end)
    objects.append(new_object("StockCurved", bm, steel))

    # Blades, each closed: the straight blade inside the curved stock rail, the curved one inside the straight stock
    # rail, both running from the point of switch to their heels.
    def straight_blade(t):
        return g.normal(t, h - INSET * (1 - t / g.blade))

    def curved_blade(t):
        return g.diverging(t, -h + INSET * (1 - t / g.blade))

    for name, at, away in (("BladeStraight", straight_blade, 1), ("BladeCurved", curved_blade, -1)):
        bm = bmesh.new()
        path = rail_path(at, 0.0, g.blade, 0.25)
        sweep(bm, path, blade_profile(len(path), away * s))
        heel = at(g.blade)
        objects.append(new_object(name, bm, steel, (heel[0], heel[1], 0.0)))

    # Stretchers and the throw rod, across between the blades a little behind their tips.
    bm = bmesh.new()
    for t in (0.6, 1.5):
        a = straight_blade(t)
        b = curved_blade(t)
        cx, cy = (a[0] + b[0]) / 2, (a[1] + b[1]) / 2
        box(bm, (cx, cy), (1, 0), (0, 1), abs(a[0] - b[0]) / 2, 0.03, RAIL_BASE + 0.01, RAIL_BASE + 0.035)
    objects.append(new_object("Stretchers", bm, plate_mat))
    machine_x = -s * (h + 0.85)
    bm = bmesh.new()
    a = straight_blade(0.6)
    box(bm, ((machine_x + a[0]) / 2, 0.6), (1, 0), (0, 1), abs(a[0] - machine_x) / 2, 0.02, RAIL_BASE - 0.02, RAIL_BASE + 0.0)
    objects.append(new_object("ThrowRod", bm, plate_mat))

    # Closure rails, from the heels towards the frog, bending out round the other route's rail as wing rails.
    # Each closure rail runs on its own route until its gauge face comes within a flangeway of the other route's gauge
    # line (the knuckle), then follows that line a flangeway off as a wing rail beside the frog point, flaring away at
    # its end.
    def wing(t):
        end = max(0.0, t - (g.frog + 1.0)) / 0.5
        return max(0.0, g.offset(t) - GAUGE + FLANGEWAY) + 0.03 * end * end

    bm = bmesh.new()
    rail(bm, lambda t: g.normal(t, h + wing(t)), g.blade, g.frog + 1.5)
    rail(bm, lambda t: g.diverging(t, -h - wing(t)), g.blade, g.frog + 1.5)
    objects.append(new_object("Closures", bm, steel))

    # The frog point: from where the two routes' gauge lines are FROG_POINT apart, each route's inner rail with its back
    # planed off so the two make one V between the gauge lines; and the casting under it.
    point = math.sqrt(2 * g.radius * (GAUGE + FROG_POINT))

    def point_profile(keep, s0, s1, n):
        def profile(i):
            t = s0 + (s1 - s0) * i / n
            k = min(1.0, (g.offset(t) - GAUGE) / (2 * HEAD_HALF))
            return [(u * k + keep * HEAD_HALF * (1 - k), v) for u, v in RAIL]
        return profile

    bm = bmesh.new()
    for at, keep in ((n_at(h), -s), (d_at(-h), s)):
        path = rail_path(at, point, g.rail_end, 0.1)
        sweep(bm, path, point_profile(keep, point, g.rail_end, len(path) - 1))
    objects.append(new_object("Frog", bm, steel))
    bm = bmesh.new()
    mid = g.normal(g.frog + 0.8, h + 0.1)
    slope = g.heading(g.frog) * 0.5 * s
    length = math.hypot(1.0, slope)
    forward = (slope / length, 1 / length)
    box(bm, mid, (forward[1], -forward[0]), forward, 0.22, 2.3, RAIL_BASE - 0.004, RAIL_BASE + 0.03)
    objects.append(new_object("FrogCasting", bm, casting))

    # Guard rails inside each outer rail, opposite the frog, their ends flared in.
    guard = [(u, v * 0.92) for u, v in RAIL]

    def flare(t, centre):
        x = abs(t - centre) - (GUARD_LENGTH / 2 - 0.6)
        return 0.05 * max(0.0, x / 0.6)

    bm = bmesh.new()
    rail(bm, lambda t: g.normal(t, -h + GUARD_OFF + flare(t, g.frog)), g.frog - GUARD_LENGTH / 2, g.frog + GUARD_LENGTH / 2, lambda i: guard)
    rail(bm, lambda t: g.diverging(t, h - GUARD_OFF - flare(t, g.frog)), g.frog - GUARD_LENGTH / 2, g.frog + GUARD_LENGTH / 2, lambda i: guard)
    objects.append(new_object("Guards", bm, steel))

    # Timbers, longer as the routes part, and a plate under every rail on each.
    timbers = bmesh.new()
    plates = bmesh.new()
    t = start + TIMBER_SPACING / 2
    while t < g.end:
        spread = g.offset(t)
        length = TIE_LENGTH + spread
        centre = g.normal(t, spread / 2)
        extra = 1.1 if 0.2 < t < 1.3 else 0.0     # the two timbers carrying the point machine reach out under it
        cx = centre[0] - s * extra / 2
        box(timbers, (cx, centre[1]), (1, 0), (0, 1), (length + extra) / 2, TIMBER_WIDTH / 2, TIE_RISE - TIMBER_DEPTH, TIE_RISE)
        rails = [-h, h]
        if spread > 0.3:
            rails += [spread - h, spread + h]
        for u in rails:
            box(plates, (s * u, t), (1, 0), (0, 1), 0.18, 0.10, TIE_RISE, TIE_RISE + PLATE_DEPTH)
        t += TIMBER_SPACING
    objects.append(new_object("Timbers", timbers, timber))
    objects.append(new_object("Plates", plates, plate_mat))

    # The point machine on its two long timbers: a housing with a lid, and its motor end.
    bm = bmesh.new()
    box(bm, (machine_x, 0.75), (1, 0), (0, 1), 0.28, 0.55, TIE_RISE, TIE_RISE + 0.26)
    box(bm, (machine_x, 0.75), (1, 0), (0, 1), 0.30, 0.57, TIE_RISE + 0.26, TIE_RISE + 0.29)
    box(bm, (machine_x - s * 0.05, 1.45), (1, 0), (0, 1), 0.16, 0.18, TIE_RISE, TIE_RISE + 0.22)
    objects.append(new_object("Machine", bm, machine_mat))

    return g, objects


# --- The kit. ---

def build_kit(side):
    s = side
    h = RAIL_OFFSET
    plate_mat = material("Plate", (0.14, 0.11, 0.09), metallic=0.6, roughness=0.7)
    casting = material("Casting", (0.18, 0.16, 0.14), metallic=0.7, roughness=0.5)
    machine_mat = material("Machine", (0.30, 0.31, 0.30), metallic=0.3, roughness=0.6)
    objects = []

    # The machine, on two steel skids reaching in under the rail seat's edge.
    machine_x = -s * (h + 0.85)
    bm = bmesh.new()
    box(bm, (machine_x, 0.75), (1, 0), (0, 1), 0.28, 0.55, TIE_RISE, TIE_RISE + 0.26)
    box(bm, (machine_x, 0.75), (1, 0), (0, 1), 0.30, 0.57, TIE_RISE + 0.26, TIE_RISE + 0.29)
    box(bm, (machine_x - s * 0.05, 1.45), (1, 0), (0, 1), 0.16, 0.18, TIE_RISE, TIE_RISE + 0.22)
    for y in (0.45, 1.05):
        box(bm, (machine_x + s * 0.1, y), (1, 0), (0, 1), 0.52, 0.09, TIE_RISE - 0.02, TIE_RISE)
    objects.append(new_object("Machine", bm, machine_mat))

    # The casting: a flat bed with raised edges, in lengths it can bend along, and bolt heads down each side.
    section = [(-1.0, -0.004), (-1.0, 0.024), (-0.88, 0.030), (0.88, 0.030), (1.0, 0.024), (1.0, -0.004)]
    bm = bmesh.new()
    start, end = -2.2, 2.6
    n = 12
    path = [(0.0, start + (end - start) * k / n, 1.0, 0.0) for k in range(n + 1)]
    sweep(bm, path, lambda i: section)
    for k in range(10):
        y = start + 0.2 + (end - start - 0.4) * k / 9
        for x in (-0.8, 0.8):
            box(bm, (x, y), (1, 0), (0, 1), 0.06, 0.035, RAIL_BASE + 0.03, RAIL_BASE + 0.045)
    objects.append(new_object("FrogCasting", bm, casting))

    bm = bmesh.new()
    box(bm, (0.0, 0.0), (1, 0), (0, 1), 1.0, 0.03, RAIL_BASE + 0.01, RAIL_BASE + 0.035)
    for x in (-0.97, 0.97):
        box(bm, (x, 0.0), (1, 0), (0, 1), 0.03, 0.05, RAIL_BASE + 0.005, RAIL_BASE + 0.05)
    objects.append(new_object("Stretcher", bm, plate_mat))

    bm = bmesh.new()
    box(bm, (0.0, 0.0), (1, 0), (0, 1), 1.0, 0.02, RAIL_BASE - 0.02, RAIL_BASE)
    box(bm, (0.95, 0.0), (1, 0), (0, 1), 0.05, 0.035, RAIL_BASE - 0.025, RAIL_BASE + 0.005)
    objects.append(new_object("ThrowRod", bm, plate_mat))

    bm = bmesh.new()
    box(bm, (0.0, 0.0), (1, 0), (0, 1), 1.0, 1.0, RAIL_BASE - 0.004, RAIL_BASE + 0.03)
    for x, y in ((-0.82, -0.82), (0.82, -0.82), (-0.82, 0.82), (0.82, 0.82), (-0.82, 0.0), (0.82, 0.0), (0.0, -0.82), (0.0, 0.82)):
        box(bm, (x, y), (1, 0), (0, 1), 0.07, 0.07, RAIL_BASE + 0.03, RAIL_BASE + 0.045)
    objects.append(new_object("CrossingCasting", bm, casting))
    return objects


def export(objects, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=True, export_yup=True)
    print("wrote", path)


# --- A look at it. ---

def lights_and_world():
    scene = bpy.context.scene
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.data.energy = 4.0
    sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(30))
    scene.collection.objects.link(sun)
    world = bpy.data.worlds.new("World")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.55, 0.6, 0.65, 1.0)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.6
    scene.world = world
    ground = bpy.data.objects.new("Ballast", bpy.data.meshes.new("Ballast"))
    bm = bmesh.new()
    bmesh.ops.create_grid(bm, x_segments=1, y_segments=1, size=80)
    bm.to_mesh(ground.data)
    bm.free()
    ground.location = (0, 30, -0.002)
    ground.data.materials.append(material("BallastLook", (0.16, 0.15, 0.14), roughness=1.0))
    scene.collection.objects.link(ground)


def render(path, location, target, lens, size=(1280, 720)):
    scene = bpy.context.scene
    camera = scene.camera or bpy.data.objects.new("Camera", bpy.data.cameras.new("Camera"))
    if scene.camera is None:
        scene.collection.objects.link(camera)
        scene.camera = camera
    camera.location = location
    direction = tuple(t - c for t, c in zip(target, location))
    camera.rotation_euler = (math.atan2(math.hypot(direction[0], direction[1]), -direction[2]), 0,
                             math.atan2(direction[1], direction[0]) - math.pi / 2)
    camera.data.lens = lens
    scene.render.resolution_x, scene.render.resolution_y = size
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print("wrote", path)


def thumbnail(g, path):
    """The whole turnout from behind its points and above, for the game's switch picker."""
    reach = g.end
    spread = g.offset(reach)
    # From high on the diverging side, so the turnout runs across the picture, points on the left.
    render(path, (spread * 0.5 + reach * 0.3 * g.side, reach * 0.5, reach * 0.75), (spread * 0.5, reach * 0.5, 0.0), 30, (480, 150))


def preview(g, objects, folder):
    os.makedirs(folder, exist_ok=True)
    blades = {o.name: o for o in objects}
    straight, curved = blades["BladeStraight"], blades["BladeCurved"]
    stretchers, rod = blades["Stretchers"], blades["ThrowRod"]
    swing = THROW / g.blade

    def set_route(reverse):
        # Normal: the straight blade closed, the curved one swung open (towards the track centre); reverse the other way.
        # A blade's tip is behind its heel (its origin), so turning it anticlockwise (seen from above) moves the tip to +X.
        straight.rotation_euler = (0, 0, -swing * g.side if reverse else 0)
        curved.rotation_euler = (0, 0, 0 if reverse else swing * g.side)
        shift = -THROW * g.side if reverse else 0.0
        stretchers.location = (shift, 0, 0)
        rod.location = (shift, 0, 0)

    set_route(False)
    render(os.path.join(folder, "turnout_10_top.png"), (g.offset(g.end) / 2, g.end / 2, 34), (g.offset(g.end) / 2, g.end / 2 - 0.01, 0), 50)
    render(os.path.join(folder, "turnout_10_normal.png"), (-1.2, -3.0, 1.4), (0.6, 5.0, 0.0), 30)
    set_route(True)
    render(os.path.join(folder, "turnout_10_reverse.png"), (-1.2, -3.0, 1.4), (0.6, 5.0, 0.0), 30)
    for reverse, name in ((False, "normal"), (True, "reverse")):
        set_route(reverse)
        render(os.path.join(folder, f"turnout_10_points_{name}.png"), (0.0, 2.6, 5.5), (0.0, 2.61, 0.0), 35)
    set_route(False)
    render(os.path.join(folder, "turnout_10_frog_top.png"), (g.offset(g.frog) / 2, g.frog - 0.5, 4.0), (g.offset(g.frog) / 2, g.frog - 0.49, 0.0), 50)
    render(os.path.join(folder, "turnout_10_frog.png"), (g.offset(g.frog) + 2.2, g.frog - 4.0, 1.3), (g.offset(g.frog) * 0.5, g.frog + 1.0, 0.0), 30)


reset()
lights_and_world()
bpy.context.scene.cycles.samples = 16
for hand, side in (("r", 1), ("l", -1)):
    parts = build_kit(side)
    export(parts, os.path.join(CONTENT, "Models", "Track", f"switch_kit_{hand}.glb"))
    for part in parts:
        bpy.data.objects.remove(part)

for number in () if KIT_ONLY else SIZES:
    geometry, parts = build(number, 1)
    thumbnail(geometry, os.path.join(CONTENT, "Models", "Track", f"turnout_{number}.png"))
    if number == 10 and PREVIEW:
        preview(geometry, parts, PREVIEW)
    for part in parts:
        bpy.data.objects.remove(part)
