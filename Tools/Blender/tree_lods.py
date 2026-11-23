"""Reduced copies of trees whose packs have no levels of detail of their own, for Vegetation's middle distance.

    blender -b --factory-startup --python Tools/Blender/tree_lods.py -- <pack.glb> [ratio]

Writes <pack>_reduced.glb beside it: every node and its transform as they are, the bark (any mesh whose material isn't a
branch or leaf atlas) decimated to ratio of its triangles (a fifth by default), the leaf cards left alone (collapsing them
would tear the cut-out cards apart, and they're few already). Geometry only: the game takes the textures from the pack itself.
The game draws the full trees in their place when this file isn't there.
"""

import os
import sys

import bpy

LEAVES = ("branch", "leaf", "leaves", "atlas")


def is_leaves(obj):
    return any(any(word in (slot.material.name if slot.material else "").lower() for word in LEAVES) for slot in obj.material_slots)


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    path = os.path.abspath(args[0])
    ratio = float(args[1]) if len(args) > 1 else 0.2
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=path)

    before = after = 0
    for obj in [o for o in bpy.data.objects if o.type == "MESH"]:
        if is_leaves(obj):
            continue
        before += len(obj.data.polygons)
        modifier = obj.modifiers.new("lod", "DECIMATE")
        modifier.decimate_type = "COLLAPSE"
        modifier.ratio = ratio
        modifier.use_collapse_triangulate = True
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.modifier_apply(modifier=modifier.name)
        after += len(obj.data.polygons)

    out = os.path.splitext(path)[0] + "_reduced.glb"
    bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", export_image_format="NONE", export_materials="PLACEHOLDER",
                              export_yup=True, export_apply=True)
    print(f"tree_lods: bark {before} -> {after} faces; wrote {out}")


main()
