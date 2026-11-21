"""The TTX well car's whole-car models, empty and with each stack of containers it can be seen carrying.

    blender -b --factory-startup --python Tools/Blender/wellcar_loads.py -- <Content folder>

Run on its own after wellcar_doublestack.py has made the parts (Meshes/RollingStock/WellCar/TTX/Source/Parts and
assembly.json), or by it at its end. Writes LOD0/wellcar_ttx.glb (empty) and LOD0/wellcar_ttx_<load>.glb for each of LOADS,
and the loads into assembly.json. No LOD1 or LOD2 is made for them: the game's mesh optimizer makes its own.

A stack's bottom box sits where the source seats it (assembly.json's first container), on the well's floor; each box above
sits on the one under it, their corner castings overlapping by as much as the source's two did. Their nodes are
Container_Lower and Container_Upper whichever boxes they are.
"""
import json
import math
import os
import sys

import bpy
from mathutils import Matrix

YANG_MING = "container_yangming_40"
EVERGREEN = "container_evergreen_40hc"

# Each load the game offers: its id (the model's suffix and vehicle.json's load id) and its boxes from the bottom up.
LOADS = (
    ("single", (YANG_MING,)),
    ("single_evergreen", (EVERGREEN,)),
    ("double", (YANG_MING, EVERGREEN)),
    ("double_evergreen_yangming", (EVERGREEN, YANG_MING)),
    ("double_yangming", (YANG_MING, YANG_MING)),
    ("double_evergreen", (EVERGREEN, EVERGREEN)),
)
NODES = ("Container_Lower", "Container_Upper")
LIVERY = "ttx"
MODEL = f"wellcar_{LIVERY}"


def build(content, save_blend=False):
    out = os.path.join(content, "Meshes", "RollingStock", "WellCar", "TTX")
    parts = os.path.join(out, "Source", "Parts")
    couplers = os.path.join(content, "Meshes", "Couplers")
    with open(os.path.join(out, "assembly.json")) as f:
        assembly = json.load(f)

    # Where the bottom box goes, each box's height, and how far the source's top box sank into the bottom one.
    boxes = {c["model"]: c for c in assembly["containers"]}
    first = assembly["containers"][0]
    floor_x, floor_y, floor_z = first["position"]
    overlap = 0.0
    if len(assembly["containers"]) > 1:
        second = assembly["containers"][1]
        overlap = floor_z + first["size"][2] - second["position"][2]

    def stacked(stack):
        at, z = [], floor_z
        for model in stack:
            at.append((floor_x, floor_y, round(z, 4)))
            z += boxes[model]["size"][2] - overlap
        return at

    def holder(name, at=(0, 0, 0), yaw=0.0, parent=None):
        e = bpy.data.objects.new(name, None)
        e.empty_display_size = .3
        bpy.context.collection.objects.link(e)
        e.parent = parent
        e.location = at
        e.rotation_euler = (0, 0, yaw)
        return e

    def load(file, parent, prefix):
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=file)
        for obj in set(bpy.data.objects) - before:
            if obj.parent is None:
                obj.parent = parent
                obj.matrix_parent_inverse = Matrix.Identity(4)
            obj.name = f"{prefix}_{obj.name}"

    def combined(load_id, stack, save):
        """The whole car: every part on an empty named for it, so a truck turns with its wheels; and the stack's boxes, each
        on its own empty, part of the body."""
        bpy.ops.wm.read_factory_settings(use_empty=True)
        root = holder("WellCar")
        load(os.path.join(parts, f"body_{LIVERY}.glb"), holder("Body", parent=root), "Body")
        for label, truck in zip(("Front", "Rear"), assembly["trucks"]):
            truck_at = holder(f"Truck_{label}", tuple(truck["position"]), 0.0, root)
            load(os.path.join(parts, f"{truck['model']}.glb"), truck_at, f"Truck_{label}")
            for k, at in enumerate(truck["wheelsets"], 1):
                load(os.path.join(parts, "wheelset.glb"), holder(f"Wheelset_{label}_{k}", tuple(at), 0.0, truck_at), f"Wheelset_{label}_{k}")
        for label, coupler in zip(("Front", "Rear"), assembly["couplers"]):
            load(os.path.join(couplers, "coupler.glb"), holder(f"Coupler_{label}", tuple(coupler["position"]), math.radians(coupler["yaw"]), root), f"Coupler_{label}")
        for node, model, at in zip(NODES, stack, stacked(stack)):
            load(os.path.join(parts, f"{model}.glb"), holder(node, at, 0.0, root), node)
        bpy.context.view_layer.update()
        folder = os.path.join(out, "LOD0")
        os.makedirs(folder, exist_ok=True)
        name = f"{MODEL}.glb" if load_id is None else f"{MODEL}_{load_id}.glb"
        for obj in bpy.data.objects:
            obj.select_set(True)
        bpy.ops.export_scene.gltf(filepath=os.path.join(folder, name), export_format="GLB", use_selection=True, export_yup=True,
                                  export_materials="EXPORT", export_normals=True, export_texcoords=True, export_image_format="JPEG",
                                  export_jpeg_quality=90, export_animations=False)
        print("wrote", os.path.join(folder, name), len(bpy.data.objects), "objects")
        if save:
            bpy.ops.file.pack_all()
            bpy.context.preferences.filepaths.save_version = 0
            bpy.ops.wm.save_as_mainfile(filepath=os.path.join(out, "Source", f"{MODEL}.blend"), check_existing=False)

    combined(None, (), False)
    for load_id, stack in LOADS:
        combined(load_id, stack, save_blend and load_id == "double")

    assembly["models"] = (f"The game loads LOD0/{MODEL}.glb empty, and LOD0/{MODEL}_<load>.glb for each load below (no hand-made "
                          "LOD1 or LOD2: the game's mesh optimizer makes its own), made by Tools/Blender/wellcar_loads.py. The parts below are "
                          "Source/Parts/<model>.glb, put together into them, except the couplers: Meshes/Couplers/coupler.glb, the "
                          "same on every engine and car")
    assembly["loads"] = {load_id: {"model": f"LOD0/{MODEL}_{load_id}.glb", "containers": list(stack),
                                   "positions": [list(p) for p in stacked(stack)]}
                         for load_id, stack in LOADS}
    with open(os.path.join(out, "assembly.json"), "w") as f:
        json.dump(assembly, f, indent=2)


if __name__ == "__main__":
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    build(os.path.abspath(args[0] if args else "Content"))
