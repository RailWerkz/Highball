"""What every car shares, and the build: parts out to .glb, assembly.json, the assembled car, and previews.

A car type subclasses Car and gives its body, any moving parts painted with it (a door, a hatch), and where they go. The
running gear is built from its TruckSpec. Under Models/RollingStock/<folder> the build writes
    body_<livery>.glb        origin on the railhead, midway between the trucks; the A end faces -Y, the B end (hand brake) +Y
    <part>_<livery>.glb      each moving part, at its place on the body when its origin is on the body's (see assembly.json)
    truck.glb, wheelset.glb
    assembly.json            where each part goes, as the SD40's does, plus "parts" for the moving ones and "loads"
The coupler is the shared one in Models/Couplers (see coupler.py): the build reads its height and reach before making the
body, so that every car's draft gear and striker are made round it, and the assembly names it from there.
    Assembled/<key>_<livery>.glb and .blend, the whole car for looking at; the game loads the separate parts.
A car that carries containers (or other cargo models) gives them in cargo(), built once into Models/RollingStock/Containers,
and its loads in loads(): each load's models and where they sit. Then Assembled has <key>_<livery>_<load> for each load.
"""
import json
import math
import os

import bpy
from mathutils import Matrix, Vector

from . import coupler as shared_coupler
from . import running_gear
from .kit import Kit
from .running_gear import TruckSpec

AXES = "Blender Z-up metres, A end towards -Y; in the files, glTF Y-up: (x, y, z) here is (x, z, -y) there"


class Car:
    key = "car"
    folder = "Car"
    title = "Car"
    truck_spec: TruckSpec = running_gear.TRUCK_100T
    truck_centre = 6.0
    over_couplers = 16.0
    liveries: dict = {}
    coupler: shared_coupler.Coupler = None  # set by build() from Models/Couplers

    @property
    def coupler_z(self):
        return self.coupler.height

    @property
    def coupler_pivot(self):
        return self.over_couplers / 2 - self.coupler.reach

    def body(self, livery) -> Kit:
        raise NotImplementedError

    def parts(self, livery) -> dict:
        """Moving parts painted with the livery: {name: Kit}."""
        return {}

    def places(self) -> dict:
        """Where each moving part goes: {name: [{"position", "yaw" (degrees), "travel" (metres, in the part's own axes)}]}."""
        return {}

    def cargo(self) -> dict:
        """Models it carries, shared with other cars: {model name: (a function making its Kit, its paint)}."""
        return {}

    def loads(self) -> dict:
        """What it can carry: {load: [{"model": a cargo model's name, "position", "yaw" (degrees)}]}, "empty" an empty list."""
        return {}


CARGO = "Containers"  # under Models/RollingStock


def cargo_path(content, name):
    return os.path.join(content, "Models", "RollingStock", CARGO, name + ".glb")


def out_folder(content, car):
    return os.path.join(content, "Models", "RollingStock", car.folder)


def build(car: Car, content, preview=None, assembled=True):
    out = out_folder(content, car)
    car.coupler = shared_coupler.load(content)
    report = {}
    for key, livery in car.liveries.items():
        report[f"body_{key}"] = car.body(livery).export(os.path.join(out, f"body_{key}.glb"), livery["paint"])
        for name, kit in car.parts(livery).items():
            report[f"{name}_{key}"] = kit.export(os.path.join(out, f"{name}_{key}.glb"), livery["paint"])
    t = car.truck_spec
    for name, kit in (("truck", running_gear.truck(t)), ("wheelset", running_gear.wheelset(t))):
        report[name] = kit.export(os.path.join(out, f"{name}.glb"), {})
    for name, (make, colours) in car.cargo().items():
        report[name] = make().export(cargo_path(content, name), colours)
    car.content = content

    tc, pivot, cz = car.truck_centre, car.coupler_pivot, car.coupler_z
    assembly = {
        "name": car.title,
        "lengthOverCouplers": car.over_couplers,
        "trucks": [{"model": "truck.glb", "position": [0, -tc, 0], "yaw": 180}, {"model": "truck.glb", "position": [0, tc, 0], "yaw": 0}],
        "wheelsets": {"model": "wheelset.glb", "onTruck": [[0, -t.axle, t.wheel_r], [0, t.axle, t.wheel_r]], "radius": t.wheel_r},
        "couplers": [{"model": shared_coupler.MODEL, "position": [0, -pivot, cz], "yaw": 0}, {"model": shared_coupler.MODEL, "position": [0, pivot, cz], "yaw": 180}],
        "parts": {name: {"models": [f"{name}_{key}.glb" for key in car.liveries], "places": places} for name, places in car.places().items()},
        "bodies": [f"body_{key}.glb" for key in car.liveries],
        **({"loads": {load: [{**item, "model": f"RollingStock/{CARGO}/{item['model']}.glb"} for item in items] for load, items in car.loads().items()}}
           if car.loads() else {}),
        "models": "Each model is in this folder, except the couplers (Models/Couplers/coupler.glb, the same on every engine and car) and "
                  "the loads' models, which are under Models and shared",
        "axes": AXES,
    }
    with open(os.path.join(out, "assembly.json"), "w") as f:
        json.dump(assembly, f, indent=2)
    print("wrote", os.path.join(out, "assembly.json"))

    if assembled:
        for key in car.liveries:
            for load in car.loads() or [None]:
                combined(car, out, key, load)
    if preview:
        previews(car, out, preview)
    return report


# ---------------------------------------------------------------- the assembled car


def _holder(name, at=(0, 0, 0), yaw=0.0, parent=None):
    empty = bpy.data.objects.new(name, None)
    empty.empty_display_size = .3
    bpy.context.collection.objects.link(empty)
    empty.parent = parent
    empty.location = at
    empty.rotation_euler = (0, 0, yaw)
    return empty


def assemble(car: Car, folder, livery, open_parts=False, spin=0.0, carrying=None):
    """Every part on an empty named for it, so a truck turns with its wheels on it. open_parts moves the parts to their travel's end."""
    shared = {}

    def load(file, parent, prefix):
        before = set(bpy.data.objects)
        bpy.ops.import_scene.gltf(filepath=file if os.path.isabs(file) else os.path.join(folder, file))
        for obj in set(bpy.data.objects) - before:
            obj.parent = parent
            obj.matrix_parent_inverse = Matrix.Identity(4)
            obj.name = f"{prefix}_{obj.name.split('.')[0]}"
            for slot in obj.material_slots:
                slot.material = shared.setdefault(slot.material.name.split(".")[0], slot.material)

    t = car.truck_spec
    root = _holder(car.title.replace(" ", "_"))
    load(f"body_{livery}.glb", _holder("Body", parent=root), "Body")
    for label, y, yaw in (("A", -car.truck_centre, math.pi), ("B", car.truck_centre, 0.0)):
        truck_at = _holder(f"Truck_{label}", (0, y, 0), yaw, root)
        load("truck.glb", truck_at, f"Truck_{label}")
        for k, a in enumerate((-t.axle, t.axle), 1):
            w = _holder(f"Wheelset_{label}_{k}", (0, a, t.wheel_r), 0.0, truck_at)
            w.rotation_euler = (spin, 0, 0)
            load("wheelset.glb", w, f"Wheelset_{label}_{k}")
    for label, y, yaw in (("A", -car.coupler_pivot, 0.0), ("B", car.coupler_pivot, math.pi)):
        load(car.coupler.path, _holder(f"Coupler_{label}", (0, y, car.coupler_z), yaw, root), f"Coupler_{label}")
    for name, places in car.places().items():
        for k, place in enumerate(places, 1):
            yaw = math.radians(place.get("yaw", 0))
            at = Vector(place["position"])
            if open_parts and "travel" in place:
                at += Matrix.Rotation(yaw, 3, "Z") @ Vector(place["travel"])
            label = f"{name.capitalize()}_{k}"
            load(f"{name}_{livery}.glb", _holder(label, tuple(at), yaw, root), label)
    for k, item in enumerate(car.loads().get(carrying, []) if carrying else [], 1):
        label = f"Load_{k}"
        load(cargo_path(car.content, item["model"]), _holder(label, tuple(item["position"]), math.radians(item.get("yaw", 0)), root), label)
    bpy.context.view_layer.update()
    for mat in list(bpy.data.materials):
        if mat.users == 0:
            bpy.data.materials.remove(mat)
    for mat in bpy.data.materials:
        base = mat.name.split(".")[0]
        if base != mat.name and bpy.data.materials.get(base) is None:
            mat.name = base


def combined(car: Car, out, livery, carrying=None):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    assemble(car, out, livery, carrying=carrying)
    folder = os.path.join(out, "Assembled")
    os.makedirs(folder, exist_ok=True)
    for obj in bpy.data.objects:
        obj.select_set(True)
    stem = os.path.join(folder, f"{car.key}_{livery}" + (f"_{carrying}" if carrying else ""))
    bpy.ops.export_scene.gltf(filepath=stem + ".glb", export_format="GLB", use_selection=True, export_yup=True,
                              export_materials="EXPORT", export_normals=True, export_texcoords=False)
    bpy.context.preferences.filepaths.save_version = 0
    bpy.ops.wm.save_as_mainfile(filepath=stem + ".blend", check_existing=False)
    print("wrote", stem, len(bpy.data.objects), "objects")


# ---------------------------------------------------------------- previews


def stage():
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 40
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
        scene.collection.objects.link(bpy.data.objects.new("Rail", rail))
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


def shoot(folder, name, location, target, lens=50, size=(1400, 760), ortho=None):
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
    camera.rotation_euler = (Vector(target) - Vector(location)).to_track_quat("-Z", "Y").to_euler()
    scene.render.resolution_x, scene.render.resolution_y = size
    scene.render.filepath = os.path.join(folder, name + ".png")
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(camera)


def previews(car: Car, out, folder):
    os.makedirs(folder, exist_ok=True)
    L = car.over_couplers
    loads = list(car.loads())
    for i, livery in enumerate(car.liveries):
        # Each livery as it's first loaded (empty, if it carries anything), and the first livery in its other loads too.
        for j, carrying in enumerate(loads or [None]):
            if i > 0 and j > 0:
                break
            tag = f"{car.key}_{livery}" + (f"_{carrying}" if carrying else "")
            bpy.ops.wm.read_factory_settings(use_empty=True)
            assemble(car, out, livery, carrying=carrying)
            stage()
            shoot(folder, f"{tag}_hero", (L * .62, -L * 1.05, 4.6 + (1.5 if carrying else 0)), (0, -L * .06, 2.2 + (.8 if carrying else 0)), 55)
            shoot(folder, f"{tag}_side", (40, 0, 2.3 if not carrying else 3.2), (0, 0, 2.3 if not carrying else 3.2), size=(1600, 560 if not carrying else 700),
                  ortho=L * 1.06)
            if j == 0:
                shoot(folder, f"{tag}_far_side", (-40, 0, 2.3), (0, 0, 2.3), size=(1600, 560), ortho=L * 1.06)
        if i == 0:
            bpy.ops.wm.read_factory_settings(use_empty=True)
            assemble(car, out, livery)
            stage()
            shoot(folder, f"{car.key}_b_end", (5.5, L * .5 + 9, 3.2), (0, L * .5, 2.0), 50, (1000, 900))
            shoot(folder, f"{car.key}_a_end", (-4.5, -L * .5 - 8, 2.2), (0, -L * .5, 1.6), 50, (1000, 900))
            shoot(folder, f"{car.key}_truck", (4.0, car.truck_centre + 2.6, 1.1), (0, car.truck_centre, .6), 50, (1100, 700))
            shoot(folder, f"{car.key}_under", (3.2, -2.0, .35), (0, 2.0, .85), 30, (1400, 700))
            shoot(folder, f"{car.key}_roof", (8, -6, 11), (0, 0, 4.2), 45, (1400, 760))
            if car.places():
                bpy.ops.wm.read_factory_settings(use_empty=True)
                assemble(car, out, livery, open_parts=True)
                stage()
                shoot(folder, f"{car.key}_{livery}_open", (L * .45, -L * .8, 3.2), (0, -1.5, 2.2), 50)
