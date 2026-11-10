"""Builds the bridge kit's detail parts in Blender and exports them for the game.

    blender -b --factory-startup --python Tools/Blender/bridges.py -- <Content folder>

Writes Models/Track/bridge_kit.glb under the Content folder. Every part is made to a unit size and stretched by the game
(NetworkMeshes.BridgeGroups.cs, Kit) to fit where it goes: across along model X, up along Z (the game's Y), its depth along Y.
Each stands on its origin, at the bottom middle. A part whose ends mustn't stretch with its length (the pier wall's rounded
noses) keeps its ends beyond x = +-0.5, and the game scales those by its depth instead. Every object is one paint, named
<Part>_<paint>: the paints are the ones line.frag knows (World/NetworkMeshes.cs, Paints).
"""
import math
import os
import sys

import bmesh
import bpy

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
CONTENT = os.path.abspath(args[0] if args else "Content")

COLORS = {"Black": (0.02, 0.02, 0.02), "Concrete": (0.35, 0.34, 0.31), "Galv": (0.33, 0.34, 0.35)}


class Kit:
    def __init__(self):
        self.parts = {}

    def bm(self, name):
        if name not in self.parts:
            self.parts[name] = bmesh.new()
        return self.parts[name]

    def prism(self, name, outline, z0, z1, smooth=False, axis="z"):
        """An outline (a closed loop of 2D points, counter-clockwise) extruded from z0 to z1 along an axis: z (the outline in X, Y)
        or x (the outline in Y, Z)."""
        bm = self.bm(name)

        def at(u, v, w):
            return (u, v, w) if axis == "z" else (w, u, v)

        low = [bm.verts.new(at(u, v, z0)) for u, v in outline]
        high = [bm.verts.new(at(u, v, z1)) for u, v in outline]
        bm.faces.new(list(reversed(low)))
        bm.faces.new(high)
        n = len(outline)
        for k in range(n):
            k2 = (k + 1) % n
            face = bm.faces.new((low[k], low[k2], high[k2], high[k]))
            face.smooth = smooth

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


def circle(cx, cy, r, start, end, steps):
    return [(cx + r * math.cos(start + (end - start) * k / steps), cy + r * math.sin(start + (end - start) * k / steps)) for k in range(steps + 1)]


def build():
    kit = Kit()

    # A round column: a unit across, a unit tall.
    kit.prism("Column_Concrete", circle(0.0, 0.0, 0.5, 0.0, 2.0 * math.pi, 24)[:-1], 0.0, 1.0, smooth=True)

    # A pier wall, a unit long between its noses and a unit thick, a unit tall: a flat face each side and a rounded nose each end
    # (past x = +-0.5, which the game scales by its thickness, so they stay round however long the wall is).
    outline = [(-0.5, -0.5), (0.5, -0.5)]
    outline += circle(0.5, 0.0, 0.5, -math.pi * 0.5, math.pi * 0.5, 10)[1:-1]
    outline += [(0.5, 0.5), (-0.5, 0.5)]
    outline += circle(-0.5, 0.0, 0.5, math.pi * 0.5, math.pi * 1.5, 10)[1:-1]
    kit.prism("Wall_Concrete", outline, 0.0, 1.0)

    # A cap across the top of a pier: a unit long, a unit deep, a unit tall, its lower edges cut back.
    kit.prism("Cap_Concrete", [(-0.5, 0.35), (-0.3, 0.0), (0.3, 0.0), (0.5, 0.35), (0.5, 1.0), (-0.5, 1.0)], -0.5, 0.5, axis="x")

    # A bearing pad under a girder: a unit block.
    kit.prism("Bearing_Black", [(-0.5, -0.5), (0.5, -0.5), (0.5, 0.5), (-0.5, 0.5)], 0.0, 1.0)

    kit.export(os.path.join(CONTENT, "Models", "Track", "bridge_kit.glb"))


build()
