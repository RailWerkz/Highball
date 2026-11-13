import bpy, os, sys
argv = sys.argv[sys.argv.index("--")+1:]
tex_root, out_glb = argv
MAP = {"Gevo": ("Exterior", "Gevo"), "Gevo CAB": ("CAB", "Cab"), "Control": ("CAB/Control", "Control")}

def load(path, noncolor):
    img = bpy.data.images.load(path, check_existing=True)
    if noncolor: img.colorspace_settings.name = "Non-Color"
    print("IMG", os.path.basename(path), tuple(img.size))
    return img

def gltf_output_group():
    g = bpy.data.node_groups.get("glTF Material Output")
    if g: return g
    g = bpy.data.node_groups.new("glTF Material Output", "ShaderNodeTree")
    g.interface.new_socket("Occlusion", in_out="INPUT", socket_type="NodeSocketFloat")
    return g

for mat_name, (sub, prefix) in MAP.items():
    m = bpy.data.materials[mat_name]
    nt = m.node_tree; nt.nodes.clear()
    d = os.path.join(tex_root, sub)
    out = nt.nodes.new("ShaderNodeOutputMaterial"); out.location = (600, 0)
    bsdf = nt.nodes.new("ShaderNodeBsdfPrincipled"); bsdf.location = (250, 0)
    nt.links.new(bsdf.outputs[0], out.inputs[0])
    bsdf.inputs["Metallic"].default_value = 0.0
    bsdf.inputs["Roughness"].default_value = 0.6
    def tex(kind, noncolor, y):
        n = nt.nodes.new("ShaderNodeTexImage"); n.location = (-400, y)
        n.image = load(os.path.join(d, f"{prefix}_{kind}.png"), noncolor); return n
    diff = tex("Diffuse", False, 300)
    nt.links.new(diff.outputs["Color"], bsdf.inputs["Base Color"])
    nrm = tex("Normal", True, 0)
    nm = nt.nodes.new("ShaderNodeNormalMap"); nm.location = (-100, 0)
    nt.links.new(nrm.outputs["Color"], nm.inputs["Color"]); nt.links.new(nm.outputs["Normal"], bsdf.inputs["Normal"])
    spec = tex("Spec", True, -300)
    nt.links.new(spec.outputs["Color"], bsdf.inputs["Specular IOR Level"])
    ao = tex("Ao", True, -600)
    grp = nt.nodes.new("ShaderNodeGroup"); grp.node_tree = gltf_output_group(); grp.location = (250, -500)
    nt.links.new(ao.outputs["Color"], grp.inputs["Occlusion"])

# drop stale checker images
for img in list(bpy.data.images):
    if img.users == 0: bpy.data.images.remove(img)

bpy.ops.export_scene.gltf(filepath=out_glb, export_format="GLB", use_selection=False,
                          export_image_format="AUTO", export_apply=True, export_yup=True)
bpy.ops.wm.save_as_mainfile(filepath=os.path.splitext(out_glb)[0] + ".blend", relative_remap=True)
print("DONE", out_glb)
