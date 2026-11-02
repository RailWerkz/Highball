"""Builds Highball's track parts in Blender and exports them for the game.

    blender -b --factory-startup --python Tools/Blender/track_parts.py -- <Content folder> [preview.png]

Writes Models/Track/tie.glb, Models/Track/plate.glb and Textures/Track/ballast.png under the Content folder, and,
given a second path, renders a close-up of a tie, its plates and a stub of rail there to check the parts by eye.

Blender is Z-up; the glTF exporter turns that into the game's Y-up. Each part is modelled with X across the track and
Y along it, its origin where the game places it:
  tie    the top face's centre 2 cm above the origin (the game's instance point is the crib ballast's surface)
  plate  centred on the rail seat, standing on the origin (the tie's top); the field side towards +X
All sizes are real US practice: an 8 ft 6 in x 9 in x 7 in tie, a 14 in x 7.75 in double-shoulder plate, 5/8 in cut spikes.
"""

import math
import os
import random
import sys

import bpy
import bmesh

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
CONTENT = os.path.abspath(args[0] if args else "Content")
PREVIEW = os.path.abspath(args[1]) if len(args) > 1 else None

INCH = 0.0254
TIE_LENGTH = 102 * INCH
TIE_WIDTH = 9 * INCH
TIE_DEPTH = 7 * INCH
TIE_RISE = 0.02
PLATE_LENGTH = 14 * INCH
PLATE_WIDTH = 7.75 * INCH
PLATE_DEPTH = 0.016
RAIL_BASE = 6 * INCH

random.seed(7)


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 16
    scene.unit_settings.system = "METRIC"


def node_tree(material):
    material.use_nodes = True
    tree = material.node_tree
    tree.nodes.clear()
    return tree, tree.nodes, tree.links


def finish(obj):
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    for modifier in list(obj.modifiers):
        bpy.ops.object.modifier_apply(modifier=modifier.name)


def bake(obj, image_name, width, height, build_emission):
    """Bakes an emission-only procedural material into an image, then gives the object a plain material that shows
    that image (which is what the glTF exporter writes out)."""
    image = bpy.data.images.new(image_name, width, height, alpha=False)
    bake_material = bpy.data.materials.new(image_name + "_bake")
    tree, nodes, links = node_tree(bake_material)
    output = nodes.new("ShaderNodeOutputMaterial")
    emission = nodes.new("ShaderNodeEmission")
    links.new(emission.outputs["Emission"], output.inputs["Surface"])
    build_emission(nodes, links, emission.inputs["Color"])
    target = nodes.new("ShaderNodeTexImage")
    target.image = image
    nodes.active = target

    obj.data.materials.clear()
    obj.data.materials.append(bake_material)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.context.scene.cycles.bake_type = "EMIT"
    bpy.context.scene.render.bake.margin = 8
    bpy.ops.object.bake(type="EMIT")

    material = bpy.data.materials.new(image_name)
    tree, nodes, links = node_tree(material)
    output = nodes.new("ShaderNodeOutputMaterial")
    bsdf = nodes.new("ShaderNodeBsdfPrincipled")
    bsdf.inputs["Roughness"].default_value = 0.85
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = image
    links.new(texture.outputs["Color"], bsdf.inputs["Base Color"])
    links.new(bsdf.outputs["BSDF"], output.inputs["Surface"])
    obj.data.materials.clear()
    obj.data.materials.append(material)
    image.pack()
    return image


def decimated(obj, name, ratio):
    """A low-detail copy for distant instances: the same UVs, so it shows the same baked texture."""
    copy = obj.copy()
    copy.data = obj.data.copy()
    copy.name = name
    bpy.context.collection.objects.link(copy)
    modifier = copy.modifiers.new("Decimate", "DECIMATE")
    modifier.ratio = ratio
    bpy.ops.object.select_all(action="DESELECT")
    finish(copy)
    return copy


def export(obj, path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=True,
                              export_yup=True, export_texcoords=True, export_normals=True, export_materials="EXPORT",
                              export_image_format="AUTO")
    print("wrote", path)


def unwrap(obj, island_margin=0.02):
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(60), island_margin=island_margin, scale_to_bounds=True)
    bpy.ops.object.mode_set(mode="OBJECT")


# --- The tie. ---

def make_tie():
    mesh = bpy.data.meshes.new("Tie")
    obj = bpy.data.objects.new("Tie", mesh)
    bpy.context.collection.objects.link(obj)

    bm = bmesh.new()
    bmesh.ops.create_cube(bm, size=1.0)
    for v in bm.verts:
        v.co.x *= TIE_LENGTH
        v.co.y *= TIE_WIDTH
        v.co.z = (v.co.z + 0.5) * TIE_DEPTH - TIE_DEPTH + TIE_RISE
    # Enough cuts along it for the sag and the end checks to have somewhere to go.
    bmesh.ops.subdivide_edges(bm, edges=[e for e in bm.edges if abs(e.verts[0].co.x - e.verts[1].co.x) > 1.0], cuts=10)
    bm.to_mesh(mesh)
    bm.free()

    bevel = obj.modifiers.new("Bevel", "BEVEL")
    bevel.width = 0.008
    bevel.segments = 1
    bevel.limit_method = "ANGLE"
    finish(obj)

    # Worn: the top a little hollow between the rail seats and rounded at the ends, every face slightly uneven.
    for v in mesh.vertices:
        x = v.co.x / (TIE_LENGTH * 0.5)
        if v.co.z > TIE_RISE - 0.01:
            v.co.z -= 0.004 * (1.0 - x * x) + 0.006 * max(0.0, abs(x) - 0.85) / 0.15
        v.co.z += random.uniform(-0.0012, 0.0012)
        v.co.y += random.uniform(-0.0012, 0.0012)

    # End checks: splits opening into each end grain.
    bm = bmesh.new()
    bm.from_mesh(mesh)
    for side in (-1, 1):
        for _ in range(2):
            y = random.uniform(-TIE_WIDTH * 0.3, TIE_WIDTH * 0.3)
            depth = random.uniform(0.04, 0.09)
            for v in bm.verts:
                distance_from_end = TIE_LENGTH * 0.5 - side * v.co.x
                if distance_from_end < depth and abs(v.co.y - y) < 0.006 and v.co.z > TIE_RISE - 0.06:
                    v.co.z -= 0.012 * (1.0 - distance_from_end / depth)
    bm.to_mesh(mesh)
    bm.free()

    for polygon in mesh.polygons:
        polygon.use_smooth = False
    unwrap(obj)

    def wood(nodes, links, color_input):
        coords = nodes.new("ShaderNodeTexCoord")
        mapping = nodes.new("ShaderNodeMapping")
        mapping.inputs["Scale"].default_value = (1.0, 9.0, 9.0)
        links.new(coords.outputs["Object"], mapping.inputs["Vector"])

        # Grain runs along the tie (x): rings as stretched waves, warped by noise.
        warp = nodes.new("ShaderNodeTexNoise")
        warp.inputs["Scale"].default_value = 3.0
        warp.inputs["Detail"].default_value = 4.0
        rings = nodes.new("ShaderNodeTexWave")
        rings.wave_type = "RINGS"
        rings.rings_direction = "X"
        rings.inputs["Scale"].default_value = 4.0
        rings.inputs["Distortion"].default_value = 1.2
        rings.inputs["Detail"].default_value = 2.0
        rings.inputs["Detail Scale"].default_value = 0.6
        links.new(mapping.outputs["Vector"], warp.inputs["Vector"])
        links.new(mapping.outputs["Vector"], rings.inputs["Vector"])

        grain_ramp = nodes.new("ShaderNodeValToRGB")
        grain_ramp.color_ramp.elements[0].position = 0.25
        grain_ramp.color_ramp.elements[0].color = (0.034, 0.028, 0.022, 1)  # creosote-dark late wood
        grain_ramp.color_ramp.elements[1].position = 0.85
        grain_ramp.color_ramp.elements[1].color = (0.070, 0.058, 0.046, 1)  # weathered early wood
        links.new(rings.outputs["Fac"], grain_ramp.inputs["Fac"])

        # Sun-bleached grey in patches, darker oil-soaked patches.
        weather = nodes.new("ShaderNodeTexNoise")
        weather.inputs["Scale"].default_value = 1.2
        weather.inputs["Detail"].default_value = 6.0
        links.new(coords.outputs["Object"], weather.inputs["Vector"])
        weather_ramp = nodes.new("ShaderNodeValToRGB")
        weather_ramp.color_ramp.elements[0].position = 0.40
        weather_ramp.color_ramp.elements[0].color = (0.70, 0.66, 0.62, 1)
        weather_ramp.color_ramp.elements[1].position = 0.65
        weather_ramp.color_ramp.elements[1].color = (1.25, 1.25, 1.30, 1)
        links.new(weather.outputs["Fac"], weather_ramp.inputs["Fac"])

        # Hairline checks along the grain.
        cracks = nodes.new("ShaderNodeTexVoronoi")
        cracks.feature = "DISTANCE_TO_EDGE"
        cracks.inputs["Scale"].default_value = 6.0
        crack_map = nodes.new("ShaderNodeMapping")
        crack_map.inputs["Scale"].default_value = (0.25, 4.0, 4.0)
        links.new(coords.outputs["Object"], crack_map.inputs["Vector"])
        links.new(crack_map.outputs["Vector"], cracks.inputs["Vector"])
        crack_ramp = nodes.new("ShaderNodeValToRGB")
        crack_ramp.color_ramp.elements[0].position = 0.0
        crack_ramp.color_ramp.elements[0].color = (0.25, 0.25, 0.25, 1)
        crack_ramp.color_ramp.elements[1].position = 0.03
        crack_ramp.color_ramp.elements[1].color = (1, 1, 1, 1)
        links.new(cracks.outputs["Distance"], crack_ramp.inputs["Fac"])

        mix1 = nodes.new("ShaderNodeMix")
        mix1.data_type = "RGBA"
        mix1.blend_type = "MULTIPLY"
        mix1.inputs["Factor"].default_value = 1.0
        links.new(grain_ramp.outputs["Color"], mix1.inputs["A"])
        links.new(weather_ramp.outputs["Color"], mix1.inputs["B"])
        mix2 = nodes.new("ShaderNodeMix")
        mix2.data_type = "RGBA"
        mix2.blend_type = "MULTIPLY"
        mix2.inputs["Factor"].default_value = 1.0
        links.new(mix1.outputs["Result"], mix2.inputs["A"])
        links.new(crack_ramp.outputs["Color"], mix2.inputs["B"])
        links.new(mix2.outputs["Result"], color_input)

    bake(obj, "tie_wood", 1024, 512, wood)
    return obj


# --- The tie plate and its spikes. ---

def box(bm, cx, cy, cz, sx, sy, sz):
    geom = bmesh.ops.create_cube(bm, size=1.0)
    for v in geom["verts"]:
        v.co.x = cx + v.co.x * sx
        v.co.y = cy + v.co.y * sy
        v.co.z = cz + v.co.z * sz
    return geom["verts"]


def make_plate():
    mesh = bpy.data.meshes.new("Plate")
    obj = bpy.data.objects.new("Plate", mesh)
    bpy.context.collection.objects.link(obj)
    bm = bmesh.new()

    # The plate: thicker on the field side (+x) to cant the rail inward 1:40, and a shoulder each side of the seat.
    plate = box(bm, 0.0, 0.0, PLATE_DEPTH * 0.5, PLATE_LENGTH, PLATE_WIDTH, PLATE_DEPTH)
    for v in plate:
        if v.co.z > PLATE_DEPTH * 0.5:
            v.co.z = PLATE_DEPTH * (0.75 + 0.5 * (v.co.x / PLATE_LENGTH + 0.5) * 0.5)
    seat = RAIL_BASE * 0.5 + 0.003
    for side in (-1, 1):
        box(bm, side * (seat + 0.008), 0.0, PLATE_DEPTH + 0.005, 0.016, PLATE_WIDTH * 0.94, 0.010)

    # Four cut spikes: square shanks into the tie, heads with a lip reaching over the rail's foot. Two on the gauge
    # side, two on the field side, staggered.
    shank = 0.625 * INCH
    for side, y in ((-1, -0.055), (-1, 0.055), (1, -0.035), (1, 0.035)):
        x = side * (seat + 0.026)
        box(bm, x, y, PLATE_DEPTH - 0.010, shank, shank, 0.04)
        box(bm, x, y, PLATE_DEPTH + 0.019, 0.026, 0.030, 0.010)
        lip = box(bm, x - side * 0.016, y, PLATE_DEPTH + 0.016, 0.012, 0.024, 0.008)
        for v in lip:
            if v.co.x * side < x * side - 0.016:
                v.co.z -= 0.004

    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-5)
    bm.to_mesh(mesh)
    bm.free()

    unwrap(obj, 0.04)

    def rusty_steel(nodes, links, color_input):
        coords = nodes.new("ShaderNodeTexCoord")
        rust = nodes.new("ShaderNodeTexNoise")
        rust.inputs["Scale"].default_value = 60.0
        rust.inputs["Detail"].default_value = 8.0
        rust.inputs["Roughness"].default_value = 0.65
        links.new(coords.outputs["Object"], rust.inputs["Vector"])
        ramp = nodes.new("ShaderNodeValToRGB")
        ramp.color_ramp.elements[0].position = 0.35
        ramp.color_ramp.elements[0].color = (0.045, 0.028, 0.018, 1)
        ramp.color_ramp.elements[1].position = 0.7
        ramp.color_ramp.elements[1].color = (0.16, 0.075, 0.032, 1)
        links.new(rust.outputs["Fac"], ramp.inputs["Fac"])
        links.new(ramp.outputs["Color"], color_input)

    bake(obj, "plate_steel", 256, 256, rusty_steel)
    return obj


# --- Ballast: a seamless tile of crushed stone, 1.5 m across. ---

def make_ballast(path):
    bpy.ops.mesh.primitive_plane_add(size=2.0)
    plane = bpy.context.active_object

    def stones(nodes, links, color_input):
        # Wrap the plane's UVs round a torus in 4D (x, y, z, w), so the tile repeats with no seam.
        coords = nodes.new("ShaderNodeTexCoord")
        split = nodes.new("ShaderNodeSeparateXYZ")
        links.new(coords.outputs["UV"], split.inputs["Vector"])

        def angle_trig(source, op):
            mult = nodes.new("ShaderNodeMath")
            mult.operation = "MULTIPLY"
            mult.inputs[1].default_value = math.tau
            links.new(source, mult.inputs[0])
            trig = nodes.new("ShaderNodeMath")
            trig.operation = op
            links.new(mult.outputs["Value"], trig.inputs[0])
            return trig.outputs["Value"]

        cu = angle_trig(split.outputs["X"], "COSINE")
        su = angle_trig(split.outputs["X"], "SINE")
        cv = angle_trig(split.outputs["Y"], "COSINE")
        sv = angle_trig(split.outputs["Y"], "SINE")
        vector = nodes.new("ShaderNodeCombineXYZ")
        links.new(cu, vector.inputs["X"])
        links.new(su, vector.inputs["Y"])
        links.new(cv, vector.inputs["Z"])

        def voronoi(scale, feature, output):
            node = nodes.new("ShaderNodeTexVoronoi")
            node.voronoi_dimensions = "4D"
            node.feature = feature
            node.inputs["Scale"].default_value = scale
            node.inputs["Randomness"].default_value = 0.9
            links.new(vector.outputs["Vector"], node.inputs["Vector"])
            links.new(sv, node.inputs["W"])
            return node.outputs[output]

        # Stones about 5 cm across (radius-1 torus: 2 pi x scale cells round the tile), each its own shade of granite
        # or limestone, lit as if rounded (bright in the middle, falling off to dark gaps), speckled; a second,
        # smaller layer shows in the bigger stones' gaps.
        def noise4(scale):
            node = nodes.new("ShaderNodeTexNoise")
            node.noise_dimensions = "4D"
            node.inputs["Scale"].default_value = scale
            node.inputs["Detail"].default_value = 6.0
            links.new(vector.outputs["Vector"], node.inputs["Vector"])
            links.new(sv, node.inputs["W"])
            return node.outputs["Fac"]

        def math_node(op, a, b=None, value=None):
            node = nodes.new("ShaderNodeMath")
            node.operation = op
            links.new(a, node.inputs[0])
            if b is not None:
                links.new(b, node.inputs[1])
            elif value is not None:
                node.inputs[1].default_value = value
            return node.outputs["Value"]

        def ramp(fac, stops):
            node = nodes.new("ShaderNodeValToRGB")
            node.color_ramp.elements[0].position = stops[0][0]
            node.color_ramp.elements[0].color = stops[0][1]
            node.color_ramp.elements[1].position = stops[-1][0]
            node.color_ramp.elements[1].color = stops[-1][1]
            for position, color in stops[1:-1]:
                element = node.color_ramp.elements.new(position)
                element.color = color
            links.new(fac, node.inputs["Fac"])
            return node.outputs["Color"]

        def multiply(a, b):
            mix = nodes.new("ShaderNodeMix")
            mix.data_type = "RGBA"
            mix.blend_type = "MULTIPLY"
            mix.inputs["Factor"].default_value = 1.0
            links.new(a, mix.inputs["A"])
            links.new(b, mix.inputs["B"])
            return mix.outputs["Result"]

        def layer(scale, rounding):
            colour = voronoi(scale, "F1", "Color")
            edge = voronoi(scale, "DISTANCE_TO_EDGE", "Distance")
            bw = nodes.new("ShaderNodeRGBToBW")
            links.new(colour, bw.inputs["Color"])
            tone = ramp(bw.outputs["Val"], [(0.0, (0.07, 0.068, 0.066, 1)), (0.55, (0.17, 0.16, 0.15, 1)),
                                            (0.8, (0.22, 0.19, 0.16, 1)), (1.0, (0.30, 0.29, 0.27, 1))])
            shape = ramp(edge, [(0.0, (0.05, 0.05, 0.05, 1)), (0.04, (0.45, 0.45, 0.45, 1)), (rounding, (1, 1, 1, 1))])
            return multiply(tone, shape), edge

        def voronoi(scale, feature, output):
            node = nodes.new("ShaderNodeTexVoronoi")
            node.voronoi_dimensions = "4D"
            node.feature = feature
            node.inputs["Scale"].default_value = scale
            node.inputs["Randomness"].default_value = 1.0
            links.new(vector.outputs["Vector"], node.inputs["Vector"])
            links.new(sv, node.inputs["W"])
            return node.outputs[output]

        big, big_edge = layer(4.5, 0.30)
        small, _ = layer(10.0, 0.25)
        speckle = ramp(noise4(140.0), [(0.3, (0.75, 0.75, 0.75, 1)), (0.7, (1.2, 1.2, 1.2, 1))])
        grime = ramp(noise4(2.0), [(0.35, (0.75, 0.72, 0.68, 1)), (0.65, (1.05, 1.05, 1.05, 1))])

        mask = nodes.new("ShaderNodeMapRange")
        mask.inputs["From Min"].default_value = 0.01
        mask.inputs["From Max"].default_value = 0.05
        mask.inputs["To Min"].default_value = 0.85
        mask.inputs["To Max"].default_value = 0.0
        links.new(big_edge, mask.inputs["Value"])
        mix = nodes.new("ShaderNodeMix")
        mix.data_type = "RGBA"
        links.new(mask.outputs["Result"], mix.inputs["Factor"])
        links.new(big, mix.inputs["A"])
        links.new(small, mix.inputs["B"])
        links.new(multiply(multiply(mix.outputs["Result"], speckle), grime), color_input)

    image = bake(plane, "ballast", 1024, 1024, stones)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    image.filepath_raw = path
    image.file_format = "PNG"
    image.save()
    print("wrote", path)
    bpy.data.objects.remove(plane)


# --- A look at them together. ---

def preview(tie, plate, path):
    scene = bpy.context.scene
    tie.location = (0, 0, 0)
    plate.hide_render = True
    for side in (-1, 1):
        copy = plate.copy()
        copy.data = plate.data
        copy.hide_render = False
        copy.location = (side * 0.755, 0, TIE_RISE)
        copy.rotation_euler = (0, 0, 0 if side > 0 else math.pi)
        scene.collection.objects.link(copy)
    for k in (-1, 1):
        neighbour = tie.copy()
        neighbour.location = (0, k * 0.495, 0)
        scene.collection.objects.link(neighbour)

    # A stub of 136 lb rail on each seat.
    profile = [(-0.0762, 0), (-0.0762, 0.011), (-0.012, 0.030), (-0.0088, 0.045), (-0.0088, 0.125), (-0.025, 0.140),
               (-0.0373, 0.145), (-0.0373, 0.178), (-0.030, 0.1857), (0.030, 0.1857), (0.0373, 0.178), (0.0373, 0.145),
               (0.025, 0.140), (0.0088, 0.125), (0.0088, 0.045), (0.012, 0.030), (0.0762, 0.011), (0.0762, 0)]
    for side in (-1, 1):
        bm = bmesh.new()
        ring0 = [bm.verts.new((side * 0.755 + u, -0.9, TIE_RISE + PLATE_DEPTH + y)) for u, y in profile]
        ring1 = [bm.verts.new((side * 0.755 + u, 0.9, TIE_RISE + PLATE_DEPTH + y)) for u, y in profile]
        for i in range(len(profile)):
            j = (i + 1) % len(profile)
            bm.faces.new((ring0[i], ring0[j], ring1[j], ring1[i]))
        mesh = bpy.data.meshes.new("Rail")
        bm.to_mesh(mesh)
        bm.free()
        rail = bpy.data.objects.new("Rail", mesh)
        steel = bpy.data.materials.new("Steel")
        steel.use_nodes = True
        bsdf = steel.node_tree.nodes["Principled BSDF"]
        bsdf.inputs["Base Color"].default_value = (0.10, 0.065, 0.045, 1)
        bsdf.inputs["Metallic"].default_value = 0.6
        bsdf.inputs["Roughness"].default_value = 0.6
        mesh.materials.append(steel)
        scene.collection.objects.link(rail)

    bpy.ops.mesh.primitive_plane_add(size=6.0, location=(0, 0, -0.16))
    ground = bpy.context.active_object
    gravel = bpy.data.materials.new("Gravel")
    gravel.use_nodes = True
    texture = gravel.node_tree.nodes.new("ShaderNodeTexImage")
    texture.image = bpy.data.images["ballast"]
    mapping = gravel.node_tree.nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = (4.0, 4.0, 1.0)
    coords = gravel.node_tree.nodes.new("ShaderNodeTexCoord")
    gravel.node_tree.links.new(coords.outputs["UV"], mapping.inputs["Vector"])
    gravel.node_tree.links.new(mapping.outputs["Vector"], texture.inputs["Vector"])
    gravel.node_tree.links.new(texture.outputs["Color"], gravel.node_tree.nodes["Principled BSDF"].inputs["Base Color"])
    ground.data.materials.append(gravel)

    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.data.energy = 4.0
    sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(30))
    scene.collection.objects.link(sun)
    world = bpy.data.worlds.new("Sky")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.45, 0.55, 0.75, 1)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.6
    scene.world = world

    camera = bpy.data.objects.new("Camera", bpy.data.cameras.new("Camera"))
    camera.location = (1.9, -1.5, 0.75)
    direction = (0.35 - camera.location[0], 0.1 - camera.location[1], 0.0 - camera.location[2])
    camera.rotation_euler = (math.atan2(math.hypot(direction[0], direction[1]), -direction[2]), 0,
                             math.atan2(direction[1], direction[0]) - math.pi / 2)
    camera.data.lens = 35
    scene.collection.objects.link(camera)
    scene.camera = camera
    scene.render.resolution_x = 1280
    scene.render.resolution_y = 720
    scene.cycles.samples = 64
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print("wrote", path)


reset()
make_ballast(os.path.join(CONTENT, "Textures", "Track", "ballast.png"))
tie = make_tie()
export(tie, os.path.join(CONTENT, "Models", "Track", "tie.glb"))
tie_far = decimated(tie, "TieFar", 0.12)
export(tie_far, os.path.join(CONTENT, "Models", "Track", "tie_far.glb"))
tie_far.hide_render = True
plate = make_plate()
export(plate, os.path.join(CONTENT, "Models", "Track", "plate.glb"))
if PREVIEW:
    preview(tie, plate, PREVIEW)
