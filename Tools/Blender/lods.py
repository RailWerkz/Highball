"""Lower-detail copies of assembled vehicles, for the renderer to draw far away.

    blender -b --factory-startup --python Tools/Blender/lods.py -- <Content folder> [model.glb ...]

With no models named, every Assembled/*.glb under Models/Locomotives and Models/RollingStock. Each gets LOD1 (a quarter of
its triangles) and LOD2 (a fifteenth) beside it, in Assembled/LOD1 and Assembled/LOD2, with the same name, nodes and
materials, so VehicleModel splits them into trucks and body just as it does the full model. The game uses them when they're
there (RenderModel.ForDistance) and the full model when they're not.
"""

import glob
import os
import sys

import bpy

LEVELS = (("LOD1", 0.25), ("LOD2", 1.0 / 15.0))


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def triangles(objects):
    return sum(sum(len(p.vertices) - 2 for p in o.evaluated_get(bpy.context.evaluated_depsgraph_get()).data.polygons)
               for o in objects if o.type == "MESH")


def make(path):
    folder, name = os.path.split(path)
    for level, ratio in LEVELS:
        clear()
        bpy.ops.import_scene.gltf(filepath=path)
        meshes = [o for o in bpy.data.objects if o.type == "MESH"]
        for obj in meshes:
            # Collapse keeps the UVs and seams; the triangle count is what's asked for, whatever the mesh.
            modifier = obj.modifiers.new("lod", "DECIMATE")
            modifier.decimate_type = "COLLAPSE"
            modifier.ratio = ratio
            modifier.use_collapse_triangulate = True
        out = os.path.join(folder, level)
        os.makedirs(out, exist_ok=True)
        target = os.path.join(out, name)
        bpy.ops.object.select_all(action="SELECT")
        bpy.ops.export_scene.gltf(filepath=target, export_format="GLB", use_selection=False, export_apply=True, export_yup=True,
                                  export_materials="EXPORT", export_normals=True, export_texcoords=True, export_image_format="JPEG",
                                  export_jpeg_quality=85, export_animations=False)
        print("wrote", target, triangles(meshes), "triangles", os.path.getsize(target) // 1024, "KB")


def main():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    if not args:
        raise SystemExit("usage: blender -b --factory-startup --python Tools/Blender/lods.py -- <Content folder> [model.glb ...]")
    content = args[0]
    models = [os.path.join(content, "Models", m) if not os.path.isabs(m) else m for m in args[1:]]
    if not models:
        for kind in ("Locomotives", "RollingStock"):
            models += glob.glob(os.path.join(content, "Models", kind, "*", "Assembled", "*.glb"))
            models += glob.glob(os.path.join(content, "Models", kind, "*", "*", "Assembled", "*.glb"))
    for model in sorted(set(models)):
        make(model)


main()
