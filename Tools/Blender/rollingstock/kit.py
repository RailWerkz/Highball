"""Mesh building shared by the rolling stock: a Kit gathers bmesh geometry by role, one paint per role, and exports it as glTF.

Blender is Z-up, metres; the glTF exporter turns that into the game's Y-up. Every object is one paint and is named for it, with a
material of the same name. A livery's paint maps a role to "#rrggbb" (gloss paint) or to ("#rrggbb", metallic, roughness).
"""
import math
import os

import bmesh
import bpy
from mathutils import Vector


def srgb(code):
    """A #rrggbb paint as linear colour."""
    out = []
    for at in (1, 3, 5):
        c = int(code[at:at + 2], 16) / 255
        out.append(c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4)
    return tuple(out)


# Colour, metallic, roughness for the roles a livery doesn't paint.
COMMON = {
    "Frame": ("#1c1c1e", 0, .7), "Truck": ("#1b1b1d", 0, .75), "Steel": ("#74787c", 1, .45), "Tread": ("#a2a5a8", 1, .3),
    "Bearing": ("#3d3f42", .6, .5), "Spring": ("#2a2b2d", .3, .6), "Dark": ("#0b0b0c", 0, .8), "Rubber": ("#0d0d0e", 0, .9),
    "Red": ("#a4161e", 0, .4), "Coupler": ("#4a4540", 0, .75), "Moving_Knuckle": ("#4a4540", 0, .75), "Brake": ("#1c1c1e", 0, .7),
    "Grating": ("#85898c", .6, .5), "Interior": ("#4b4740", 0, .9), "Floor": ("#6b5a44", 0, .9), "TackBoard": ("#7d6a50", 0, .85),
    "Reflector": ("#f2c230", 0, .25), "Lettering": ("#ece9de", 0, .5), "Glass": ("#22303a", 0, .08),
}

FONTS = {
    "sans": ["arialbd.ttf", "segoeuib.ttf"],
    "narrow": ["ARIALNB.TTF", "arialnb.ttf", "bahnschrift.ttf", "arialbd.ttf"],
    "serif": ["rockb.ttf", "georgiab.ttf", "timesbd.ttf"],
}


def rrect(x0, x1, z0, z1, rt=0.0, rb=0.0, segs=4):
    """A rectangle, its top and bottom corners rounded, counter-clockwise."""
    pts = []

    def corner(cx, cz, r, a0):
        if r <= 0:
            pts.append((cx, cz))
            return
        for k in range(segs + 1):
            t = a0 + math.pi / 2 * k / segs
            pts.append((cx + r * math.cos(t), cz + r * math.sin(t)))

    corner(x1 - rb, z0 + rb, rb, -math.pi / 2)
    corner(x1 - rt, z1 - rt, rt, 0.0)
    corner(x0 + rt, z1 - rt, rt, math.pi / 2)
    corner(x0 + rb, z0 + rb, rb, math.pi)
    return pts


def circle(cx, cz, rx, rz=None, sides=20):
    rz = rx if rz is None else rz
    return [(cx + rx * math.cos(2 * math.pi * k / sides), cz + rz * math.sin(2 * math.pi * k / sides)) for k in range(sides)]


def mirrored(half):
    """A profile given for x >= 0 from the bottom up, made whole."""
    return half + [(-x, z) for x, z in reversed(half)]


def perpendicular(d):
    other = Vector((0, 0, 1)) if abs(d.z) < 0.9 else Vector((1, 0, 0))
    return d.cross(other).normalized()


def span(a, b):
    """Two coordinates in order, so a box can be given from a wall outwards on either side."""
    return (a, b) if a <= b else (b, a)


_font_cache = {}


def _font(kind):
    # A font loaded for an earlier car is gone once the scene's been reset for the next: load it again.
    cached = _font_cache.get(kind)
    if cached is not None:
        try:
            cached.name
        except ReferenceError:
            del _font_cache[kind]
    if kind not in _font_cache:
        _font_cache[kind] = None
        for name in FONTS[kind]:
            path = os.path.join(os.environ.get("WINDIR", "C:/Windows"), "Fonts", name)
            if os.path.exists(path):
                _font_cache[kind] = bpy.data.fonts.load(path, check_existing=True)
                break
    return _font_cache[kind]


def _glyphs(words, kind, resolution):
    """The words' outline as a flat mesh in font units: (vertices, polygons)."""
    curve = bpy.data.curves.new("Words", "FONT")
    curve.body = words
    curve.resolution_u = resolution
    font = _font(kind)
    if font:
        curve.font = font
    obj = bpy.data.objects.new("Words", curve)
    bpy.context.collection.objects.link(obj)
    evaluated = obj.evaluated_get(bpy.context.evaluated_depsgraph_get())
    mesh = evaluated.to_mesh()
    verts = [(p.co.x, p.co.y) for p in mesh.vertices]
    polys = [tuple(p.vertices) for p in mesh.polygons]
    evaluated.to_mesh_clear()
    bpy.data.objects.remove(obj)
    bpy.data.curves.remove(curve)
    return verts, polys


_cap_cache = {}


def cap_height(kind):
    if kind not in _cap_cache:
        verts, _ = _glyphs("H", kind, 2)
        _cap_cache[kind] = max(v[1] for v in verts) - min(v[1] for v in verts)
    return _cap_cache[kind]


class Kit:
    def __init__(self):
        self.parts = {}

    def bm(self, role):
        if role not in self.parts:
            self.parts[role] = bmesh.new()
        return self.parts[role]

    def hexa(self, role, c):
        """Eight corners: four round the bottom, then the same four round the top."""
        bm = self.bm(role)
        v = [bm.verts.new(p) for p in c]
        faces = []
        for q in ((0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)):
            faces.append(bm.faces.new([v[i] for i in q]))
        return v, faces

    def box(self, role, lo, hi, bevel=0.0):
        (x0, y0, z0), (x1, y1, z1) = lo, hi
        x0, x1 = span(x0, x1)
        y0, y1 = span(y0, y1)
        z0, z1 = span(z0, z1)
        v, faces = self.hexa(role, ((x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0), (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)))
        if bevel > 0:
            bm = self.bm(role)
            edges = list({e for f in faces for e in f.edges})
            bmesh.ops.bevel(bm, geom=edges, offset=bevel, segments=1, affect="EDGES", profile=0.5)

    def rings(self, role, rings, closed_ends=True):
        """Skin rings of equal size, capped."""
        bm = self.bm(role)
        vs = [[bm.verts.new(p) for p in ring] for ring in rings]
        for a, b in zip(vs, vs[1:]):
            n = len(a)
            for k in range(n):
                bm.faces.new((a[k], a[(k + 1) % n], b[(k + 1) % n], b[k]))
        if closed_ends:
            bm.faces.new(vs[0])
            bm.faces.new(list(reversed(vs[-1])))

    def loft(self, role, stations, axis="y", warp=None):
        """Profiles strung along an axis. y: profiles are (x, z). x: (y, z). z: (x, y)."""
        rings = []
        for at, profile in stations:
            ring = []
            for a, b in profile:
                p = {"y": (a, at, b), "x": (at, a, b), "z": (a, b, at)}[axis]
                ring.append(warp(*p) if warp else p)
            rings.append(ring)
        self.rings(role, rings)

    def prism(self, role, profile, axis, lo, hi, warp=None):
        self.loft(role, [(lo, profile), (hi, profile)], axis, warp)

    def cyl(self, role, p0, p1, r, sides=16, r1=None):
        p0, p1 = Vector(p0), Vector(p1)
        d = (p1 - p0).normalized()
        a = perpendicular(d)
        b = d.cross(a)
        r1 = r if r1 is None else r1
        ring = lambda c, rr: [c + (a * math.cos(2 * math.pi * k / sides) + b * math.sin(2 * math.pi * k / sides)) * rr for k in range(sides)]
        self.rings(role, [ring(p0, r), ring(p1, r1)])

    def lathe(self, role, origin, axis, profile, sides=32):
        """Turn a closed (along, radius) outline about an axis through origin."""
        origin, axis = Vector(origin), Vector(axis).normalized()
        a = perpendicular(axis)
        b = axis.cross(a)
        bm = self.bm(role)
        rows = []
        for t, r in profile:
            rows.append([bm.verts.new(origin + axis * t + (a * math.cos(2 * math.pi * k / sides) + b * math.sin(2 * math.pi * k / sides)) * r) for k in range(sides)])
        for i in range(len(rows)):
            r0, r1 = rows[i], rows[(i + 1) % len(rows)]
            for k in range(sides):
                bm.faces.new((r0[k], r0[(k + 1) % sides], r1[(k + 1) % sides], r1[k]))

    def tube(self, role, points, r, fillet=0.07, sides=8):
        """A rod along a polyline, its corners rounded."""
        pts = [Vector(p) for p in points]
        path = [pts[0]]
        for i in range(1, len(pts) - 1):
            p, a, b = pts[i], pts[i - 1], pts[i + 1]
            da, db = (a - p), (b - p)
            f = min(fillet, da.length * 0.45, db.length * 0.45)
            if f <= 0:
                path.append(p)
                continue
            s, e = p + da.normalized() * f, p + db.normalized() * f
            for k in range(6):
                t = k / 5
                path.append(s * (1 - t) ** 2 + p * (2 * t * (1 - t)) + e * t ** 2)
        path.append(pts[-1])
        rings = []
        tangent = (path[1] - path[0]).normalized()
        a = perpendicular(tangent)
        for i, p in enumerate(path):
            nxt = (path[min(i + 1, len(path) - 1)] - path[max(i - 1, 0)]).normalized()
            a = (a - nxt * a.dot(nxt)).normalized()
            b = nxt.cross(a)
            rings.append([p + (a * math.cos(2 * math.pi * k / sides) + b * math.sin(2 * math.pi * k / sides)) * r for k in range(sides)])
        self.rings(role, rings)

    def ring(self, role, centre, normal, radius, r, sides=6, segments=32):
        """A hoop of rod: a brake wheel's rim, a hose clamp."""
        c, n = Vector(centre), Vector(normal).normalized()
        a = perpendicular(n)
        b = n.cross(a)
        bm = self.bm(role)
        rows = []
        for k in range(segments):
            t = 2 * math.pi * k / segments
            radial = a * math.cos(t) + b * math.sin(t)
            p = c + radial * radius
            rows.append([bm.verts.new(p + (radial * math.cos(2 * math.pi * j / sides) + n * math.sin(2 * math.pi * j / sides)) * r) for j in range(sides)])
        for i in range(segments):
            r0, r1 = rows[i], rows[(i + 1) % segments]
            for j in range(sides):
                bm.faces.new((r0[j], r0[(j + 1) % sides], r1[(j + 1) % sides], r1[j]))

    def helix(self, role, base, radius, wire, turns, height, sides=6, per_turn=12):
        """A coil spring standing on base, with flattened end coils."""
        bx, by, bz = base
        n = int(turns * per_turn)
        pts = []
        for k in range(n + 1):
            f = k / n
            t = 2 * math.pi * turns * f
            # The end coils close up flat, so the spring sits square.
            lift = min(max((f * turns - .5) / (turns - 1), 0), 1)
            z = bz + wire + (height - 2 * wire) * lift
            pts.append((bx + radius * math.cos(t), by + radius * math.sin(t), z))
        self.tube(role, pts, wire, 0, sides)

    def panel(self, role, origin, u, v, outline, t):
        """A plate on a wall: outline in (u, v) about origin, standing out by t along u x v."""
        o, u, v = Vector(origin), Vector(u), Vector(v)
        n = u.cross(v).normalized()
        low = [o + u * a + v * b for a, b in outline]
        self.rings(role, [low, [p + n * t for p in low]])

    def text(self, role, words, kind, origin, u, v, height, width=None, align="centre", t=0.002, resolution=4):
        """Lettering on a wall, its capitals height tall (shrunk to fit width if given), facing along u x v.

        origin is the middle of the capitals' height, at the left edge, the centre or the right edge as align says."""
        verts, polys = _glyphs(words, kind, resolution)
        if not verts:
            return
        xs = [p[0] for p in verts]
        x0, x1 = min(xs), max(xs)
        scale = height / cap_height(kind)
        if width and (x1 - x0) * scale > width:
            scale = width / (x1 - x0)
        ox = {"left": x0, "centre": (x0 + x1) / 2, "right": x1}[align]
        oy = cap_height(kind) / 2
        o, u, v = Vector(origin), Vector(u), Vector(v)
        n = u.cross(v).normalized()
        bm = self.bm(role)
        base = [bm.verts.new(o + u * ((x - ox) * scale) + v * ((y - oy) * scale)) for x, y in verts]
        tops = [bm.verts.new(q.co + n * t) for q in base]
        for poly in polys:
            bm.faces.new([tops[i] for i in poly])

    def export(self, path, paint):
        # Start from nothing, so that every object and material keeps its plain name.
        for collection in (bpy.data.objects, bpy.data.meshes, bpy.data.materials):
            for item in list(collection):
                collection.remove(item)
        objects = []
        for role, bm in self.parts.items():
            bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-6)
            bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
            mesh = bpy.data.meshes.new(role)
            bm.to_mesh(mesh)
            bm.free()
            mesh.shade_smooth()
            mesh.set_sharp_from_angle(angle=math.radians(50))
            obj = bpy.data.objects.new(role, mesh)
            bpy.context.collection.objects.link(obj)
            mesh.materials.append(material(role, paint))
            objects.append(obj)
        self.parts = {}
        for obj in objects:
            obj.select_set(True)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        tris = sum(sum(len(p.vertices) - 2 for p in o.data.polygons) for o in objects)
        bpy.ops.export_scene.gltf(filepath=path, export_format="GLB", use_selection=True, export_apply=True, export_yup=True,
                                  export_materials="EXPORT", export_normals=True, export_texcoords=False)
        print("wrote", path, tris, "triangles")
        return tris


def material(role, paint):
    code, metal, rough = COMMON.get(role, ("#ff00ff", 0, .38))
    if paint and role in paint:
        given = paint[role]
        if isinstance(given, tuple):
            code, metal, rough = given
        else:
            code, metal, rough = given, 0, rough if role in COMMON else .45
    mat = bpy.data.materials.new(role)
    mat.use_nodes = True
    node = mat.node_tree.nodes["Principled BSDF"]
    node.inputs["Base Color"].default_value = (*srgb(code), 1.0)
    node.inputs["Metallic"].default_value = metal
    node.inputs["Roughness"].default_value = rough
    return mat
