"""Builds Highball's placed lineside models in Blender and exports them for the game.

    blender -b --factory-startup --python Tools/Blender/lineside.py -- <Content folder> [preview folder]

Writes Models/Track/signal.glb, hand_throw.glb and crossing_mast.glb under the Content folder. Blender is Z-up; the glTF
exporter turns that into the game's Y-up. Each model stands on the ground at the origin and faces -Y, which the game turns
to the way it's placed. Every object is one paint, named Fixed_<paint> or, on the hand throw, Moving_<paint> for what the
game turns with the switch (about the origin): the paints are the ones line.frag knows (World/NetworkMeshes.cs, Paints).
"""
import math
import os
import sys

import bmesh
import bpy

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
CONTENT = os.path.abspath(args[0] if args else "Content")
PREVIEW = os.path.abspath(args[1]) if len(args) > 1 else None

COLORS = {"Black": (0.02, 0.02, 0.02), "White": (0.75, 0.75, 0.72), "Galv": (0.33, 0.34, 0.35), "Yellow": (0.7, 0.5, 0.05),
          "Red": (0.6, 0.03, 0.02), "Green": (0.03, 0.4, 0.15), "Concrete": (0.35, 0.34, 0.31),
          "LensRed": (0.6, 0.03, 0.02), "LensYellow": (0.7, 0.5, 0.05), "LensGreen": (0.03, 0.4, 0.15)}


class Model:
    def __init__(self):
        self.parts = {}

    def bm(self, name):
        if name not in self.parts:
            self.parts[name] = bmesh.new()
        return self.parts[name]

    def box(self, name, centre, half, rotate=0.0):
        """A box about a place; rotate turns it in the XZ plane (facing -Y), as a sign board does."""
        bm = self.bm(name)
        cos, sin = math.cos(rotate), math.sin(rotate)
        verts = {}
        for sx in (-1, 1):
            for sy in (-1, 1):
                for sz in (-1, 1):
                    x, z = half[0] * sx, half[2] * sz
                    verts[(sx, sy, sz)] = bm.verts.new((centre[0] + x * cos - z * sin, centre[1] + half[1] * sy, centre[2] + x * sin + z * cos))
        for loop in (((-1, -1, -1), (1, -1, -1), (1, -1, 1), (-1, -1, 1)), ((1, 1, -1), (-1, 1, -1), (-1, 1, 1), (1, 1, 1)),
                     ((-1, -1, -1), (-1, -1, 1), (-1, 1, 1), (-1, 1, -1)), ((1, -1, 1), (1, -1, -1), (1, 1, -1), (1, 1, 1)),
                     ((-1, -1, 1), (1, -1, 1), (1, 1, 1), (-1, 1, 1)), ((-1, 1, -1), (1, 1, -1), (1, -1, -1), (-1, -1, -1))):
            bm.faces.new([verts[k] for k in loop])

    def post(self, name, x, y, z0, z1, r, sides=8):
        """Upright."""
        bm = self.bm(name)
        low = [bm.verts.new((x + r * math.cos(2 * math.pi * k / sides), y + r * math.sin(2 * math.pi * k / sides), z0)) for k in range(sides)]
        high = [bm.verts.new((x + r * math.cos(2 * math.pi * k / sides), y + r * math.sin(2 * math.pi * k / sides), z1)) for k in range(sides)]
        bm.faces.new(list(reversed(low)))
        bm.faces.new(high)
        for k in range(sides):
            k2 = (k + 1) % sides
            bm.faces.new((low[k], low[k2], high[k2], high[k]))

    def disc(self, name, x, y, z, r, depth, sides=16):
        """Facing -Y, from y back by depth."""
        bm = self.bm(name)
        front = [bm.verts.new((x + r * math.cos(2 * math.pi * k / sides), y, z + r * math.sin(2 * math.pi * k / sides))) for k in range(sides)]
        back = [bm.verts.new((x + r * math.cos(2 * math.pi * k / sides), y + depth, z + r * math.sin(2 * math.pi * k / sides))) for k in range(sides)]
        bm.faces.new(front)
        bm.faces.new(list(reversed(back)))
        for k in range(sides):
            k2 = (k + 1) % sides
            bm.faces.new((back[k], back[k2], front[k2], front[k]))

    def hood(self, x, y, z, r, length=None, sweep=220.0, thick=0.007, sides=14):
        """A lens's hood: a curved visor round the top of it, its back edge on the backplate at y, reaching out towards -Y.
        Solid, with a lip at the front and ends, so it doesn't vanish edge-on."""
        bm = self.bm("Fixed_Black")
        length = length or r * 1.4
        start = math.radians(90.0 - sweep * 0.5)
        step = math.radians(sweep) / sides
        rings = []
        for k in range(sides + 1):
            t = start + step * k
            c, s_ = math.cos(t), math.sin(t)
            rings.append([bm.verts.new((x + radius * c, yy, z + radius * s_))
                          for radius, yy in ((r + thick, y), (r + thick, y - length), (r, y - length), (r, y))])
        for a, b in zip(rings, rings[1:]):
            for i in range(4):
                j = (i + 1) % 4
                bm.faces.new((a[i], a[j], b[j], b[i]))
        bm.faces.new(rings[0])
        bm.faces.new(list(reversed(rings[-1])))

    def lens(self, name, x, y, z, r, bulge=0.03, rings=5, sides=24):
        """A domed glass lens set in the backplate at y, bulging out towards -Y, held by a black ring."""
        self.ring("Fixed_Black", x, y, z, r * 0.95, r * 1.2, 0.016, sides)
        bm = self.bm(name)
        sphere = (r * r + bulge * bulge) / (2.0 * bulge)
        centre = y - bulge + sphere
        widest = math.asin(r / sphere)
        loops = []
        for i in range(rings):
            a = widest * (1.0 - i / rings)
            rho, yy = sphere * math.sin(a), centre - sphere * math.cos(a)
            loops.append([bm.verts.new((x + rho * math.cos(2 * math.pi * k / sides), yy, z + rho * math.sin(2 * math.pi * k / sides))) for k in range(sides)])
        apex = bm.verts.new((x, y - bulge, z))
        for a, b in zip(loops, loops[1:]):
            for k in range(sides):
                k2 = (k + 1) % sides
                bm.faces.new((a[k], a[k2], b[k2], b[k]))
        for k in range(sides):
            bm.faces.new((loops[-1][k], loops[-1][(k + 1) % sides], apex))
        bm.faces.new(list(reversed(loops[0])))
        for face in bm.faces:
            face.smooth = True

    def ring(self, name, x, y, z, inner, outer, depth, sides=24):
        """A flat ring facing -Y, from y out to y - depth."""
        bm = self.bm(name)
        loops = [[bm.verts.new((x + radius * math.cos(2 * math.pi * k / sides), yy, z + radius * math.sin(2 * math.pi * k / sides))) for k in range(sides)]
                 for radius, yy in ((outer, y), (outer, y - depth), (inner, y - depth), (inner, y))]
        for k in range(sides):
            k2 = (k + 1) % sides
            for i in range(4):
                j = (i + 1) % 4
                bm.faces.new((loops[i][k], loops[i][k2], loops[j][k2], loops[j][k]))

    def export(self, path):
        bpy.ops.wm.read_factory_settings(use_empty=True)
        objects = []
        for name, bm in self.parts.items():
            mesh = bpy.data.meshes.new(name)
            bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
            bm.to_mesh(mesh)
            bm.free()
            obj = bpy.data.objects.new(name, mesh)
            bpy.context.collection.objects.link(obj)
            paint = name.split("_")[1]
            mat = bpy.data.materials.get(paint) or bpy.data.materials.new(paint)
            mat.use_nodes = True
            mat.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (*COLORS[paint], 1.0)
            mesh.materials.append(mat)
            objects.append(obj)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        for obj in objects:
            obj.select_set(True)
        bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=True, export_yup=True, export_materials="NONE")
        print("wrote", path, sum(len(o.data.vertices) for o in objects), "verts")


def render(path, height):
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 16
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.data.energy = 4.0
    sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(-30))
    scene.collection.objects.link(sun)
    world = bpy.data.worlds.new("World")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.55, 0.6, 0.65, 1.0)
    scene.world = world
    camera = bpy.data.objects.new("Camera", bpy.data.cameras.new("Camera"))
    scene.collection.objects.link(camera)
    scene.camera = camera
    location = (height * 0.5, -height * 1.1, height * 0.65)
    target = (0.0, 0.0, height * 0.5)
    d = tuple(t - c for t, c in zip(target, location))
    camera.location = location
    camera.rotation_euler = (math.atan2(math.hypot(d[0], d[1]), -d[2]), 0, math.atan2(d[1], d[0]) - math.pi / 2)
    camera.data.lens = 35
    scene.render.resolution_x, scene.render.resolution_y = 500, 640
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)


def signal():
    m = Model()
    # The foundation, the mast and its ladder, and a relay case at the foot. The foundation stands tall and reaches well down: the
    # game sets the model on the ground under the ballast's edge, so it's what takes up the ballast there, sunk a little into it.
    m.post("Fixed_Concrete", 0.0, 0.0, -0.6, 0.6, 0.42)
    m.post("Fixed_Galv", 0.0, 0.0, 0.6, 5.4, 0.075)
    for x in (-0.2, 0.2):
        m.box("Fixed_Galv", (x, 0.2, 2.95), (0.016, 0.016, 2.35))
    for k in range(16):
        m.box("Fixed_Galv", (0.0, 0.2, 0.75 + k * 0.3), (0.2, 0.011, 0.011))
    m.box("Fixed_Galv", (0.0, 0.14, 1.0), (0.2, 0.012, 0.012))
    # A platform under the head, clear of its backplate.
    m.box("Fixed_Galv", (0.0, -0.1, 3.7), (0.55, 0.42, 0.018))
    # The head: its case, the backplate (target) on the case's face, and in it three domed lenses, each with its hood round it.
    # Front to back: hood lip, lens dome, ring, backplate face at PLATE, the case behind.
    plate = -0.09
    m.box("Fixed_Black", (0.0, 0.03, 4.75), (0.26, 0.09, 0.62))
    m.disc("Fixed_Black", 0.0, plate, 5.15, 0.55, 0.03, 24)
    m.disc("Fixed_Black", 0.0, plate, 4.35, 0.55, 0.03, 24)
    m.box("Fixed_Black", (0.0, plate + 0.015, 4.75), (0.55, 0.015, 0.4))
    for z, lens in ((5.15, "Fixed_LensGreen"), (4.75, "Fixed_LensYellow"), (4.35, "Fixed_LensRed")):
        m.lens(lens, 0.0, plate, z, 0.107)
        m.hood(0.0, plate, z, 0.15, length=0.3)
    return m, 5.8


def hand_throw():
    m = Model()
    # The stand on its plate; what turns with the switch is its spindle, target and lever.
    m.box("Fixed_Black", (0.0, 0.0, 0.42), (0.17, 0.26, 0.012))
    m.post("Fixed_Black", 0.0, 0.0, 0.42, 0.75, 0.085)
    m.box("Fixed_Black", (0.0, 0.0, 0.8), (0.1, 0.1, 0.05))
    m.post("Moving_Black", 0.0, 0.0, 0.8, 1.32, 0.025)
    # The target's two faces: red square across the track when set for the other route, green along it.
    m.box("Moving_Red", (0.0, 0.0, 1.45), (0.17, 0.009, 0.13))
    m.box("Moving_Green", (0.0, 0.0, 1.2), (0.009, 0.17, 0.1))
    m.box("Moving_Black", (0.34, 0.0, 0.84), (0.34, 0.016, 0.016))
    m.post("Moving_Yellow", 0.68, 0.0, 0.74, 0.9, 0.022)
    return m, 1.8


def crossing_mast():
    m = Model()
    m.post("Fixed_Concrete", 0.0, 0.0, -0.2, 0.2, 0.34)
    m.post("Fixed_Galv", 0.0, 0.0, 0.2, 4.6, 0.055)
    # The crossbuck on top.
    for rotate in (math.radians(45), math.radians(-45)):
        m.box("Fixed_White", (0.0, -0.08, 4.0), (0.62, 0.01, 0.1), rotate)
        m.box("Fixed_Black", (0.0, -0.068, 4.0), (0.64, 0.01, 0.115), rotate)
    # The bell and the crossarm with two flashers.
    m.post("Fixed_Black", 0.0, -0.09, 3.35, 3.47, 0.11, 10)
    m.box("Fixed_Galv", (0.0, -0.05, 2.9), (0.55, 0.024, 0.024))
    # Each flasher's lens is a part of its own (FlashA_, FlashB_), so the game can light the two in turn while the crossing's
    # working (NetworkMeshes.PlacedModel); dark, they're red glass.
    for x, lens in ((-0.43, "FlashA_LensRed"), (0.43, "FlashB_LensRed")):
        m.disc("Fixed_Black", x, -0.08, 2.9, 0.26, 0.02)
        m.lens(lens, x, -0.1, 2.9, 0.12, bulge=0.025)
        m.hood(x, -0.1, 2.9, 0.14)
    # The gate's case, and its arm raised, red and white: behind the crossarm and the flashers' backs, clear of them.
    m.box("Fixed_Black", (0.0, -0.05, 1.1), (0.17, 0.14, 0.4))
    m.box("Fixed_Black", (-0.22, 0.1, 1.42), (0.09, 0.05, 0.14))
    for k in range(6):
        paint = "Fixed_Red" if k % 2 == 0 else "Fixed_White"
        m.box(paint, (-0.26, 0.13, 1.6 + k * 0.85 + 0.42), (0.05, 0.025, 0.42))
    return m, 6.8


for name, builder in (("signal", signal), ("hand_throw", hand_throw), ("crossing_mast", crossing_mast)):
    model, height = builder()
    model.export(os.path.join(CONTENT, "Models", "Track", f"{name}.glb"))
    if PREVIEW:
        render(os.path.join(PREVIEW, f"{name}.png"), height)
