"""Intermodal containers, as cargo for the well cars (and anything else that carries them).

Each is a ContainerSpec (its size and where its corner castings are) in a ContainerPaint (the owner's colours and marks). A
container's model has its origin on the middle of its underside, between its bottom corner castings, its length along Y and
its doors at +Y; the build writes them once to Models/RollingStock/Containers/container_<size>_<owner>.glb, and a car's
assembly.json places them for each load.

Steel boxes: corrugated sides and front, a roof with shallow ribs, corner posts and castings, and the doors at the back with
their locking bars, cams and handles, hinges and seals. The marks are the owner's code and serial and check digit as an ISO
container has them, its size and type, and the owner's name along the sides.
"""
from dataclasses import dataclass

from .kit import Kit, rrect


@dataclass(frozen=True)
class ContainerSpec:
    key: str  # in the model's name: container_<key>_<owner>.glb
    title: str
    length: float
    width: float
    height: float
    castings: tuple = ()  # more castings along the bottom and top rails, at these distances out from the middle
    code: str = ""  # the size and type marked on the doors


# 53' domestic: 102" wide, 9'6" high, with castings at the 40' positions too so a 40' box can stack on it.
C53 = ContainerSpec("53", "53 ft domestic", 16.154, 2.591, 2.896, (6.096,), "53' HC")
# 40' ISO high cube.
C40HC = ContainerSpec("40hc", "40 ft high cube", 12.192, 2.438, 2.896, (), "45G1")
SPECS = {c.key: c for c in (C53, C40HC)}


@dataclass(frozen=True)
class ContainerPaint:
    key: str  # in the model's name
    name: str  # along the sides
    owner: str  # the owner code, four letters ending in U
    serial: str
    check: str
    box: str  # the walls, roof and doors
    frame: str  # rails, posts and castings' surrounds
    lettering: str


PAINTS = {p.key: p for p in (
    ContainerPaint("highball", "HIGHBALL", "HBLU", "530217", "4", "#e6e6e1", "#d9d9d4", "#1f4e9a"),
    ContainerPaint("rwkz", "RAILWERKZ", "RWKU", "401388", "2", "#7a2622", "#6e221f", "#efede6"),
)}

POST = .16  # how far in the corner posts and the end rails come
CAST_L, CAST_W, CAST_H = .178, .162, .118  # an ISO corner casting


def model_name(spec: ContainerSpec, paint: ContainerPaint):
    return f"container_{spec.key}_{paint.key}"


class OwnedKit(Kit):
    """A Kit whose roles carry the owner's code, so each owner's materials keep their own names (Box becomes HBLU_Box) when
    containers are put together with each other and with the car, whose own Lettering they'd otherwise share."""

    def __init__(self, owner):
        super().__init__()
        self.prefix = owner + "_"

    def bm(self, role):
        return super().bm(self.prefix + role)


def colours(paint: ContainerPaint):
    """The kit's paint for a container: its roles and their colours."""
    roles = {"Box": (paint.box, 0, .6), "BoxFrame": (paint.frame, 0, .65), "Lettering": (paint.lettering, 0, .55),
             "Casting": ("#2a2a2a", 0, .7), "DoorGear": ("#8b8e90", .6, .5), "Seal": ("#151515", 0, .9)}
    return {f"{paint.owner}_{role}": v for role, v in roles.items()}


def _corrugated(width_lo, width_hi, face, depth, inward, pitch=.278):
    """A corrugated sheet's outline across a wall, as (along, out) points: crests at face, troughs depth in from it."""
    crest, slope = .07, .069
    out, back = [], []
    a = width_lo
    t = .003
    while a < width_hi - 1e-6:
        steps = [(a, face - depth), (a + slope, face), (a + slope + crest, face), (a + 2 * slope + crest, face - depth), (a + pitch, face - depth)]
        for u, d in steps:
            u = min(u, width_hi)
            out.append((u, d))
        a += pitch
    for u, d in reversed(out):
        back.append((u, d - inward * t))
    return out + back


def container(spec: ContainerSpec, paint: ContainerPaint) -> Kit:
    m = OwnedKit(paint.owner)
    L, W, H = spec.length, spec.width, spec.height
    hl, hw = L / 2, W / 2
    top = H - .08  # under the top rails

    # Bottom and top side rails, the end sills and headers.
    for s in (-1, 1):
        m.box("BoxFrame", (s * (hw - .06), -hl, 0), (s * hw, hl, .16))
        m.box("BoxFrame", (s * (hw - .06), -hl, top), (s * hw, hl, H))
    for e in (-1, 1):
        m.box("BoxFrame", (-hw, e * (hl - POST), 0), (hw, e * hl, .16))
        m.box("BoxFrame", (-hw, e * (hl - POST), H - .13), (hw, e * hl, H))
    # Corner posts.
    for s in (-1, 1):
        for e in (-1, 1):
            m.box("BoxFrame", (s * (hw - POST), e * (hl - POST), .16), (s * hw, e * hl, H - .13))
    # Corner castings, a little proud, at the ends and wherever else the size has them.
    for s in (-1, 1):
        x0, x1 = s * (hw - CAST_W), s * (hw + .003)
        for e in (-1, 1):
            for z0 in (-.003, H - CAST_H):
                m.box("Casting", (x0, e * (hl - CAST_L), z0), (x1, e * (hl + .003), z0 + CAST_H + .003))
        for y in spec.castings:
            for e in (-1, 1):
                for z0 in (-.003, H - CAST_H):
                    m.box("Casting", (x0, e * y - CAST_L / 2, z0), (x1, e * y + CAST_L / 2, z0 + CAST_H + .003))
    # Corrugated sides, between the corner posts.
    for s in (-1, 1):
        outline = _corrugated(-(hl - POST), hl - POST, hw - .008, .036, 1)
        m.prism("Box", [(s * d, u) for u, d in outline], "z", .16, top)
    # The front wall, corrugated the same way.
    outline = _corrugated(-(hw - POST - .003), hw - POST - .003, hl - .01, .036, 1)  # its edges clear of the corner posts
    m.prism("Box", [(u, -d) for u, d in outline], "z", .16, H - .13)
    # Roof: a sheet just under the rails with shallow ribs across it, and the floor's edge seen from below.
    m.box("Box", (-(hw - .06), -(hl - POST), H - .05), (hw - .06, hl - POST, H - .035))
    n = int((L - 2 * POST) / .5)
    for k in range(n + 1):
        y = -(hl - POST) + (L - 2 * POST) * k / n
        m.box("Box", (-(hw - .07), y - .05, H - .035), (hw - .07, y + .05, H - .022))
    m.box("Seal", (-(hw - .06), -(hl - POST), .09), (hw - .06, hl - POST, .16))
    doors(m, spec, paint)
    lettering(m, spec, paint)
    return m


def doors(m: Kit, spec: ContainerSpec, paint: ContainerPaint):
    """The two leaves at the back, each with two locking bars, their cams in keepers top and bottom, and a handle."""
    hl, hw, H = spec.length / 2, spec.width / 2, spec.height
    y0, y1 = hl - .03, hl - .012  # the leaves' plate
    z0, z1 = .16, H - .13
    leaf = hw - POST
    for s in (-1, 1):
        a, b = s * .004, s * (leaf - .003)  # its outer edge clear of the corner post
        m.box("Box", (a, y0, z0), (b, y1, z1))
        # Pressed panels, four up each leaf.
        lo, hi = min(a, b), max(a, b)
        cx = (lo + hi) / 2
        ph = (z1 - z0) / 4
        for k in range(4):
            zc = z0 + ph * (k + .5)
            m.panel("Box", (cx, y1, zc), (-1, 0, 0), (0, 0, 1), rrect(-(leaf / 2 - .1), leaf / 2 - .1, -ph / 2 + .06, ph / 2 - .06, .03, .03, 2), .014)
        # The seal round the opening, between the leaves.
        m.box("Seal", (s * .0, y1 - .002, z0 + .003), (s * .012, y1 + .006, z1 - .003))
        # Hinges on the outer edge.
        for zh in (z0 + .25, z0 + (z1 - z0) * .36, z0 + (z1 - z0) * .64, z1 - .25):
            m.box("DoorGear", (s * (leaf - .05), y1 - .003, zh - .06), (s * (leaf + .03), y1 + .035, zh + .06))
        # Locking bars, cams and keepers, handles.
        yb = y1 + .035
        for f in (.28, .72):
            x = s * leaf * f
            m.cyl("DoorGear", (x, yb, z0 - .05), (x, yb, z1 + .05), .014, 8)
            for zk in (z0 - .05, z1 + .05):
                m.box("DoorGear", (x - .04, y1 - .003, zk - .04), (x + .04, yb + .03, zk + .04))
            for zg in (z0 + .5, z1 - .5):
                m.box("DoorGear", (x - .03, y1 - .003, zg - .02), (x + .03, yb + .015, zg + .02))
            hz = 1.05
            m.box("DoorGear", (x - .035, yb - .01, hz - .03), (x + .035, yb + .03, hz + .03))
            m.tube("DoorGear", [(x, yb + .02, hz), (x - s * .07, yb + .05, hz), (x - s * .3, yb + .05, hz)], .012, .03, 6)
            m.box("DoorGear", (x - s * .34 - .02, y1 - .003, hz - .045), (x - s * .26 + .02, yb + .02, hz + .045))


def lettering(m: Kit, spec: ContainerSpec, paint: ContainerPaint):
    hl, hw, H = spec.length / 2, spec.width / 2, spec.height
    marks = f"{paint.owner} {paint.serial} {paint.check}"
    for s in (-1, 1):
        u = (0, s, 0)
        x = s * (hw + .001)
        # The owner's name along the side, big; the marks up at the door end's top corner.
        m.text("Lettering", paint.name, "sans", (x, 0, H * .55), u, (0, 0, 1), .62, spec.length * .62)
        m.text("Lettering", marks, "narrow", (x, s * (hl - .5), H - .32), u, (0, 0, 1), .12, 1.6, align="right")
        m.text("Lettering", spec.code, "narrow", (x, s * (hl - .5), H - .52), u, (0, 0, 1), .1, 1.0, align="right")
    # On the doors: the marks across the top of the right leaf, size and type under, and the name low across both.
    y = hl - .012 + .015
    m.text("Lettering", marks, "narrow", (-(hw - POST) * .5, y, H - .45), (-1, 0, 0), (0, 0, 1), .12, (hw - POST) * .9)
    m.text("Lettering", spec.code, "narrow", (-(hw - POST) * .5, y, H - .65), (-1, 0, 0), (0, 0, 1), .1, .6)
    m.text("Lettering", paint.name, "sans", (0, y, .55), (-1, 0, 0), (0, 0, 1), .22, (hw - POST) * 1.6)
    # The front: the name.
    m.text("Lettering", paint.name, "sans", (0, -(hl - .01) - .001, H * .6), (1, 0, 0), (0, 0, 1), .25, spec.width * .7)
