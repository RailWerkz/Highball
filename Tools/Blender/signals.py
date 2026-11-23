"""Builds Highball's signal heads, the mast they go on and the signal bridge kit, and exports them for the game.

    blender -b --factory-startup --python Tools/Blender/signals.py -- <Content folder> [preview folder]

Writes under Meshes/Lineside/Signals in the Content folder:

    head_triple.glb     three lenses (green, yellow, red top to bottom), each with its own hood, on a round-ended target
    head_hooded.glb     the same three lenses under one long hood that covers the lot, on a narrow target
    head_search.glb     a searchlight: one lens (Fixed_LensSearch) that shows one aspect at a time, red, yellow, green or lunar
                        white, on a round target, with its mechanism's case behind
    mast.glb            a mast signal's foundation (Base_), a metre of mast (Pole_, stretched to the stack), a 0.3 m section
                        of ladder (Ladder_, repeated up to the platform) and the platform under the lowest head (Platform_)
    bridge.glb          a signal bridge's kit: a leg's foundation (Base_), its lattice tower a metre tall (Leg_, stretched to
                        the span's height), one 1.5 m bay of the box truss (Bay_, repeated and stretched across the tracks),
                        the walkway behind it with its railing (Walk_), and the truss's closing end frame (End_)

As lineside.py's: Blender is Z-up and the glTF exporter makes that the game's Y-up; each model faces -Y, which the game turns to the
way it's placed, and X is to the right of a driver looking at it. A head's origin is on its mast's axis at the head's foot, so
the game stacks heads by their height (World/NetworkMeshes.Signals.cs). Every object is one paint, named <role>_<paint>; the
lenses keep the old signal's names (Fixed_LensGreen, Fixed_LensYellow, Fixed_LensRed) so each is lit on its own.
"""
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lineside  # noqa: E402
from lineside import Model, render  # noqa: E402

lineside.COLORS.update({"LensWhite": (0.6, 0.65, 0.7), "LensSearch": (0.3, 0.3, 0.3)})
CONTENT = lineside.CONTENT
PREVIEW = lineside.PREVIEW

# Where a lens's backplate face is, in front of the mast's axis.
PLATE = -0.09
LENS = 0.107  # 8 3/8 in lenses
PITCH = 0.4


def bar(m, name, a, b, t):
    """A square bar t across from a to b."""
    bm = m.bm(name)
    d = [b[i] - a[i] for i in range(3)]
    length = math.sqrt(sum(c * c for c in d)) or 1e-6
    f = [c / length for c in d]
    up = (0.0, 0.0, 1.0) if abs(f[2]) < 0.9 else (1.0, 0.0, 0.0)
    u = (f[1] * up[2] - f[2] * up[1], f[2] * up[0] - f[0] * up[2], f[0] * up[1] - f[1] * up[0])
    ul = math.sqrt(sum(c * c for c in u))
    u = [c / ul for c in u]
    v = (f[1] * u[2] - f[2] * u[1], f[2] * u[0] - f[0] * u[2], f[0] * u[1] - f[1] * u[0])
    h = t * 0.5
    corners = [(-h, -h), (h, -h), (h, h), (-h, h)]
    ends = []
    for p in (a, b):
        ends.append([bm.verts.new(tuple(p[i] + u[i] * cu + v[i] * cv for i in range(3))) for cu, cv in corners])
    bm.faces.new(list(reversed(ends[0])))
    bm.faces.new(ends[1])
    for k in range(4):
        k2 = (k + 1) % 4
        bm.faces.new((ends[0][k], ends[0][k2], ends[1][k2], ends[1][k]))


def head_triple():
    """The old signal's head as it was, its foot at the origin: a case, a target with round ends, three lenses each hooded."""
    m = Model()
    m.box("Fixed_Black", (0.0, 0.03, 0.95), (0.26, 0.09, 0.62))
    m.disc("Fixed_Black", 0.0, PLATE, 1.35, 0.55, 0.03, 24)
    m.disc("Fixed_Black", 0.0, PLATE, 0.55, 0.55, 0.03, 24)
    m.box("Fixed_Black", (0.0, PLATE + 0.015, 0.95), (0.55, 0.015, 0.4))
    # Its bracket onto the mast.
    m.box("Fixed_Galv", (0.0, 0.1, 0.95), (0.08, 0.06, 0.5))
    for z, lens in ((1.35, "Fixed_LensGreen"), (0.95, "Fixed_LensYellow"), (0.55, "Fixed_LensRed")):
        m.lens(lens, 0.0, PLATE, z, LENS)
        m.hood(0.0, PLATE, z, 0.15, length=0.3)
    return m, 1.9


def long_hood(m, x, y, z0, z1, half, depth, thick=0.008):
    """One hood over a column of lenses: a lid over the top, curving down into two side walls that run the column's length,
    deep at the top and shallower at the foot, as a tunnel visor is."""
    bm = m.bm("Fixed_Black")
    # The lid: a half-round over the top lens, out to depth.
    sides = 10
    rings = []
    for k in range(sides + 1):
        t = math.pi * k / sides
        c, s_ = math.cos(t), math.sin(t)
        rings.append([bm.verts.new((x + r * c, yy, z1 + r * s_ * 0.6))
                      for r, yy in ((half + thick, y), (half + thick, y - depth), (half, y - depth), (half, y))])
    for a, b in zip(rings, rings[1:]):
        for i in range(4):
            j = (i + 1) % 4
            bm.faces.new((a[i], a[j], b[j], b[i]))
    # The side walls, from the lid's ends down the column, tapering from depth at the top to a third of it at the foot.
    for side in (-1.0, 1.0):
        sx = x + side * (half + thick * 0.5)
        top, foot = depth, depth * 0.35
        quad = [(y, z1), (y - top, z1), (y - foot, z0), (y, z0)]
        verts = []
        for dx in (-thick * 0.5, thick * 0.5):
            verts.append([bm.verts.new((sx + dx, yy, zz)) for yy, zz in quad])
        bm.faces.new(verts[0])
        bm.faces.new(list(reversed(verts[1])))
        for k in range(4):
            k2 = (k + 1) % 4
            bm.faces.new((verts[0][k], verts[0][k2], verts[1][k2], verts[1][k]))


def head_hooded():
    """Three lenses on a narrow target, all under one long hood."""
    m = Model()
    zs = (0.35, 0.75, 1.15)
    m.box("Fixed_Black", (0.0, 0.03, 0.75), (0.19, 0.09, 0.66))
    # The target: narrow, its top rounded under the lid.
    m.box("Fixed_Black", (0.0, PLATE + 0.015, 0.73), (0.27, 0.015, 0.68))
    m.disc("Fixed_Black", 0.0, PLATE, 1.41, 0.27, 0.03, 20)
    m.box("Fixed_Galv", (0.0, 0.1, 0.75), (0.08, 0.06, 0.5))
    for z, lens in zip(reversed(zs), ("Fixed_LensGreen", "Fixed_LensYellow", "Fixed_LensRed")):
        m.lens(lens, 0.0, PLATE, z, LENS)
    long_hood(m, 0.0, PLATE, 0.12, 1.3, 0.17, 0.34)
    return m, 1.6


def head_search():
    """A searchlight: one lens in a round target, hooded, and the round case behind that holds the mechanism and its roundels."""
    m = Model()
    z = 0.45
    m.disc("Fixed_Black", 0.0, PLATE, z, 0.42, 0.03, 28)
    # The mechanism's case, a drum behind the target, and its back cover.
    m.disc("Fixed_Black", 0.0, PLATE + 0.03, z, 0.2, 0.36, 20)
    m.disc("Fixed_Black", 0.0, PLATE + 0.39, z, 0.16, 0.05, 16)
    m.box("Fixed_Galv", (0.0, 0.1, z), (0.08, 0.06, 0.3))
    m.lens("Fixed_LensSearch", 0.0, PLATE, z, LENS)
    m.hood(0.0, PLATE, z, 0.16, length=0.38)
    return m, 0.9


def mast():
    m = Model()
    # As the old signal's: a tall foundation sunk into the ballast's edge.
    m.post("Base_Concrete", 0.0, 0.0, -0.6, 0.6, 0.42)
    m.post("Pole_Galv", 0.0, 0.0, 0.0, 1.0, 0.075)
    for x in (-0.2, 0.2):
        m.box("Ladder_Galv", (x, 0.2, 0.15), (0.016, 0.016, 0.15))
    m.box("Ladder_Galv", (0.0, 0.2, 0.15), (0.2, 0.011, 0.011))
    m.box("Ladder_Galv", (0.0, 0.14, 0.05), (0.2, 0.012, 0.012))
    # The platform, its deck at the origin, clear of the head's target, with a guard rail round its front.
    m.box("Platform_Galv", (0.0, -0.1, 0.0), (0.55, 0.42, 0.018))
    for x in (-0.53, 0.53):
        m.box("Platform_Galv", (x, -0.1, 0.5), (0.014, 0.014, 0.5))
        m.box("Platform_Galv", (x, -0.1, 0.98), (0.012, 0.4, 0.012))
    return m, 2.0


# A signal bridge's truss: a box 0.9 m deep (front to back) and a metre high, in 1.5 m bays; its legs lattice towers.
TRUSS_HALF = 0.45
TRUSS_HIGH = 1.0
BAY = 1.5


def bridge():
    m = Model()
    chord = 0.1
    lace = 0.05
    # A leg: four angles in a tower 0.5 m wide and the truss's depth, laced on every face, a metre tall (stretched).
    m.box("Base_Concrete", (0.0, 0.0, -0.15), (0.45, 0.7, 0.45))
    xs, ys = (-0.25, 0.25), (-TRUSS_HALF + 0.05, TRUSS_HALF - 0.05)
    for x in xs:
        for y in ys:
            bar(m, "Leg_Galv", (x, y, 0.0), (x, y, 1.0), chord)
    panels = 7
    for k in range(panels + 1):
        zk = k / panels
        for y in ys:
            bar(m, "Leg_Galv", (xs[0], y, zk), (xs[1], y, zk), lace)
        for x in xs:
            bar(m, "Leg_Galv", (x, ys[0], zk), (x, ys[1], zk), lace)
        if k < panels:
            z2 = (k + 1) / panels
            flip = k % 2
            for y in ys:
                bar(m, "Leg_Galv", (xs[flip], y, zk), (xs[1 - flip], y, z2), lace)
            for x in xs:
                bar(m, "Leg_Galv", (x, ys[flip], zk), (x, ys[1 - flip], z2), lace)
    m.box("Leg_Galv", (0.0, 0.0, 0.01), (0.35, 0.55, 0.012))

    # A bay of the truss, from x 0 to BAY: four chords, a vertical frame at its start, a diagonal on the front and back faces,
    # and the top and bottom laced across.
    for y in (-TRUSS_HALF, TRUSS_HALF):
        for z in (0.0, TRUSS_HIGH):
            bar(m, "Bay_Galv", (0.0, y, z), (BAY, y, z), chord)
        bar(m, "Bay_Galv", (0.0, y, 0.0), (0.0, y, TRUSS_HIGH), lace * 1.4)
        bar(m, "Bay_Galv", (0.0, y, 0.0), (BAY, y, TRUSS_HIGH), lace)
    for z in (0.0, TRUSS_HIGH):
        bar(m, "Bay_Galv", (0.0, -TRUSS_HALF, z), (0.0, TRUSS_HALF, z), lace)
        bar(m, "Bay_Galv", (0.0, -TRUSS_HALF, z), (BAY, TRUSS_HALF, z), lace)

    # The walkway behind, on brackets off the bottom chord, its grating and a railing at its back edge.
    m.box("Walk_Galv", (BAY * 0.5, TRUSS_HALF + 0.4, 0.0), (BAY * 0.5, 0.37, 0.02))
    bar(m, "Walk_Galv", (0.0, TRUSS_HALF, -0.03), (0.0, TRUSS_HALF + 0.78, -0.03), lace)
    bar(m, "Walk_Galv", (0.0, TRUSS_HALF + 0.76, 0.0), (0.0, TRUSS_HALF + 0.76, 1.07), 0.045)
    for z in (0.55, 1.07):
        bar(m, "Walk_Galv", (0.0, TRUSS_HALF + 0.76, z), (BAY, TRUSS_HALF + 0.76, z), 0.04)

    # The end frame that closes the last bay: its verticals, the top and bottom across, and the railing's end post.
    for y in (-TRUSS_HALF, TRUSS_HALF):
        bar(m, "End_Galv", (0.0, y, 0.0), (0.0, y, TRUSS_HIGH), lace * 1.4)
    for z in (0.0, TRUSS_HIGH):
        bar(m, "End_Galv", (0.0, -TRUSS_HALF, z), (0.0, TRUSS_HALF, z), lace)
    bar(m, "End_Galv", (0.0, TRUSS_HALF + 0.76, 0.0), (0.0, TRUSS_HALF + 0.76, 1.07), 0.045)
    return m, 3.0


if __name__ == "__main__":
    for name, builder in (("head_triple", head_triple), ("head_hooded", head_hooded), ("head_search", head_search), ("mast", mast), ("bridge", bridge)):
        model, height = builder()
        model.export(os.path.join(CONTENT, "Meshes", "Lineside", "Signals", f"{name}.glb"))
        if PREVIEW:
            render(os.path.join(PREVIEW, f"{name}.png"), height)
