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
          "Red": (0.6, 0.03, 0.02), "Green": (0.03, 0.4, 0.15), "Concrete": (0.35, 0.34, 0.31)}


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

    def hood(self, x, y, z, r):
        """A lens's hood: an open half tube over it, reaching out towards -Y."""
        bm = self.bm("Fixed_Black")
        sides = 8
        for radius, turn in ((r, False), (r * 0.94, True)):
            verts = []
            for k in range(sides + 1):
                t = math.pi * k / sides
                verts.append((bm.verts.new((x + radius * math.cos(t), y, z + radius * math.sin(t))), bm.verts.new((x + radius * math.cos(t), y - r * 1.4, z + radius * math.sin(t)))))
            for (a, b), (c, d) in zip(verts, verts[1:]):
                bm.faces.new((b, d, c, a) if turn else (a, c, d, b))

    def export(self, path):
        bpy.ops.wm.read_factory_settings(use_empty=True)
        objects = []
        for name, bm in self.parts.items():
            mesh = bpy.data.meshes.new(name)
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
    # The foundation, the mast and its ladder, and a relay case at the foot.
    m.post("Fixed_Concrete", 0.0, 0.0, -0.2, 0.25, 0.42)
    m.post("Fixed_Galv", 0.0, 0.0, 0.25, 5.4, 0.075)
    for x in (-0.2, 0.2):
        m.box("Fixed_Galv", (x, 0.2, 2.8), (0.016, 0.016, 2.5))
    for k in range(17):
        m.box("Fixed_Galv", (0.0, 0.2, 0.55 + k * 0.3), (0.2, 0.011, 0.011))
    m.box("Fixed_Galv", (0.0, 0.14, 1.0), (0.2, 0.012, 0.012))
    # A platform under the head.
    m.box("Fixed_Galv", (0.0, -0.1, 4.05), (0.55, 0.42, 0.018))
    # The head: its backing board, three lenses and their hoods.
    m.box("Fixed_Black", (0.0, -0.1, 4.75), (0.26, 0.09, 0.62))
    m.disc("Fixed_Black", 0.0, -0.08, 5.15, 0.55, 0.03, 16)
    m.disc("Fixed_Black", 0.0, -0.08, 4.35, 0.55, 0.03, 16)
    m.box("Fixed_Black", (0.0, -0.065, 4.75), (0.55, 0.015, 0.4))
    for z, lens in ((5.15, "Fixed_Green"), (4.75, "Fixed_Yellow"), (4.35, "Fixed_Red")):
        m.disc(lens, 0.0, -0.2, z, 0.13, 0.025, 14)
        m.hood(0.0, -0.2, z, 0.15)
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
    for x in (-0.43, 0.43):
        m.disc("Fixed_Black", x, -0.08, 2.9, 0.26, 0.02)
        m.disc("Fixed_Red", x, -0.1, 2.9, 0.12, 0.022, 12)
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
