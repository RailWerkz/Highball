"""A 50 ft exterior-post boxcar: 10 ft sliding door, Improved Dreadnaught ends, diagonal-panel roof, 100-ton trucks.

The sizes are a BoxcarSpec, so longer cars, a 60 ft high-cube, or one with a wider door are a new spec and a catalog entry.
The doors are a moving part: door_<livery>.glb is the +X side's door shut, in the body's own axes, and assembly.json puts it
at yaw 0 and again at yaw 180 for the -X side; each slides by its travel along its own -Y, to the left as you face it.
"""
import math
from dataclasses import dataclass

from . import details
from .car import Car
from .kit import Kit, rrect
from .running_gear import TRUCK_100T, TruckSpec


@dataclass(frozen=True)
class BoxcarSpec:
    inside_length: float = 15.39  # 50'6"
    side: float = 1.44  # outside of the side sheet
    post_depth: float = .09  # how far the exterior posts stand off it
    floor: float = 1.14  # top of the floor above the railhead
    sill: float = .98  # foot of the side sheet
    eave: float = 4.30  # top of the side sheet; the roof sits on it
    crown: float = 4.56  # the roof's middle
    door: float = 3.05  # door opening, 10'
    door_top: float = 4.09
    panels: int = 6  # side panels between the door post and the corner post
    roof_panels: int = 12
    ribs: int = 8  # end corrugations
    truck_centre: float = 6.17  # half of 40'6"
    over_strikers: float = 15.98
    over_couplers: float = 16.88  # 55'5"
    truck: TruckSpec = TRUCK_100T
    data: tuple = ("EX W 10-8", "EX HT 15-1", "IL 50-6", "IW 9-0", "IH 10-5", "CU FT 5077")


TUSCAN, BOXCAR_RED, MINERAL = "#6a2a1d", "#5b2b22", "#6b3626"
WHITE = "#ece9de"


def paint(body, lettering=WHITE, roof=None):
    roof = roof or body
    return {"Body": body, "Door": body, "Frame": body, "Grab": body, "Roof": roof, "Lettering": lettering}


LIVERIES = {
    "rwkz": {"paint": paint(TUSCAN, roof=("#9a9c9d", .75, .42)), "marks": "RWKZ", "number": "150317", "name": ("RAIL", "WERKZ"),
             "capacity": ("CAPY 200000", "LD LMT 207800", "LT WT 78200", "BLT 6-81")},
    "csx": {"paint": paint(BOXCAR_RED), "marks": "CSXT", "number": "137551", "name": ("CSX",),
            "capacity": ("CAPY 200000", "LD LMT 208300", "LT WT 77700", "BLT 3-79")},
    "up": {"paint": paint(MINERAL), "marks": "UP", "number": "501203", "name": ("UNION", "PACIFIC"),
           "capacity": ("CAPY 200000", "LD LMT 207400", "LT WT 78600", "BLT 9-80")},
}


class Boxcar(Car):
    def __init__(self, key="boxcar_50", folder="Boxcar_50ft", title="50ft Boxcar", spec=BoxcarSpec(), liveries=None):
        self.key, self.folder, self.title, self.c = key, folder, title, spec
        self.truck_spec = spec.truck
        self.truck_centre = spec.truck_centre
        self.over_couplers = spec.over_couplers
        self.liveries = liveries or LIVERIES
        c = spec
        self.S = c.side
        self.E = c.inside_length / 2 + .085  # outside of the end sheet
        self.OS = c.over_strikers / 2
        self.D = c.door / 2
        self.W = self.D + .075  # half the door, which laps the door posts
        self.travel = 2 * self.W + .01
        p0, p1 = self.D + .12, self.E - .12
        self.posts = [p0 + (p1 - p0) * k / c.panels for k in range(1, c.panels)]
        self.centres = [p0 + (p1 - p0) * (k + .5) / c.panels for k in range(c.panels)]

    def places(self):
        return {"door": [{"position": [0, 0, 0], "yaw": 0, "travel": [0, -self.travel, 0]},
                         {"position": [0, 0, 0], "yaw": 180, "travel": [0, -self.travel, 0]}]}

    # ------------------------------------------------------------ the body

    def body(self, lv):
        m = Kit()
        self.underframe(m)
        self.shell(m)
        self.roof(m)
        for s in (-1, 1):
            self.side(m, s, lv)
        for e in (-1, 1):
            self.end(m, e, lv)
        self.brakes(m)
        return m

    def roof_z(self, x):
        c = self.c
        x = abs(x)
        knee = self.S - .08
        if x <= knee:
            return c.crown - (c.crown - (c.eave + .12)) * (x / knee) ** 2
        t = min((x - knee) / (self.S + .055 - knee), 1)
        return c.eave + .03 + .09 * math.sqrt(max(1 - t * t, 0))

    def shell(self, m):
        c, S, E, D = self.c, self.S, self.E, self.D
        m.box("Frame", (-S, -E, .99), (S, E, 1.10))
        m.box("Floor", (-S + .06, -E + .06, 1.10), (S - .06, E - .06, c.floor))
        for s in (-1, 1):
            for y0, y1 in ((-E, -D), (D, E)):
                m.box("Body", (s * (S - .006), y0, c.sill), (s * S, y1, c.eave))
                m.box("Interior", (s * (S - .06), y0, c.floor), (s * (S - .006), y1, c.eave))
            # Over the door and under it.
            m.box("Body", (s * (S - .006), -D, c.door_top), (s * S, D, c.eave))
            m.box("Interior", (s * (S - .06), -D, c.door_top), (s * (S - .006), D, c.eave))
            m.box("Body", (s * (S - .006), -D, c.sill), (s * S, D, c.floor))
            m.box("Steel", (s * (S - .07), -D, c.floor - .004), (s * S, D, c.floor + .004))
        for e in (-1, 1):
            m.box("Body", (-S, e * (E - .06), c.sill), (S, e * E, c.eave))
            m.box("Interior", (-S + .06, e * (E - .1), c.floor), (S - .06, e * (E - .06), c.eave))

    def roof(self, m):
        c, S, E = self.c, self.S, self.E
        edge = S + .055
        xs = [edge, edge - .002, edge - .01, edge - .025, edge - .045, edge - .075, edge - .11, S - .08, 1.2, 1.0, .8, .6, .4, .2, .05]
        half = [(edge, c.eave)] + [(x, self.roof_z(x)) for x in xs]
        m.prism("Roof", half + [(-x, z) for x, z in reversed(half)], "y", -E - .04, E + .04)
        # Seams between the roof sheets, capped, and the diagonal stiffeners pressed into each sheet.
        across = [edge - .01 - (2 * edge - .02) * k / 30 for k in range(31)]
        cap = [(x, self.roof_z(x) + .016) for x in across] + [(x, self.roof_z(x) - .004) for x in reversed(across)]
        n = c.roof_panels
        seams = [-E + 2 * E * k / n for k in range(n + 1)]
        for k, y in enumerate(seams):
            w = .05 if 0 < k < n else .03
            m.prism("Roof", cap, "y", y - w, y + w)
        for y0, y1 in zip(seams, seams[1:]):
            for q in (-1, 1):
                pts = []
                for j in range(8):
                    f = j / 7
                    x = q * (S - .1) * (1 - f) + q * .1 * f
                    y = (y0 + .14) * (1 - f) + (y1 - .14) * f
                    pts.append((x, y, self.roof_z(x) + .002))
                m.tube("Roof", pts, .013, 0, 6)

    def underframe(self, m):
        c, S, E, OS, tc = self.c, self.S, self.E, self.OS, self.truck_centre
        # The centre sill, the backbone, open below the floor for the couplers' shanks and draft gear.
        for s in (-1, 1):
            m.box("Frame", (s * .22, -OS, .74), (s * .24, OS, 1.04))
            m.box("Frame", (s * .22, -OS, .72), (s * .34, OS, .74))
        m.box("Frame", (-.30, -OS, 1.0), (.30, OS, 1.04))
        # Beyond the trucks it deepens into the draft sills, down round the coupler's shank, with the draft gear behind it.
        cz, p = self.coupler_z, self.coupler_pivot
        low = cz - .17
        for e in (-1, 1):
            for s in (-1, 1):
                m.box("Frame", (s * .22, e * (tc + .45), low), (s * .24, e * OS, .74))
                m.box("Frame", (s * .22, e * (tc + .45), low - .02), (s * .34, e * OS, low))
            m.box("Dark", (-.2, e * (p - .2), cz - .12), (.2, e * (p - .85), cz + .12), .01)
        # Body bolsters, tapering from the centre sill out to the side sills, on the centre plates and side bearings.
        bolster = [(-S, .98), (-.34, .70), (.34, .70), (S, .98), (S, 1.0), (-S, 1.0)]
        for y in (-tc, tc):
            m.prism("Frame", bolster, "y", y - .18, y + .18)
            m.cyl("Steel", (0, y, self.c.truck.centre_plate), (0, y, .70), .19, 24)
            for s in (-1, 1):
                x = s * self.c.truck.side_bearing
                m.box("Frame", (x - .08, y - .1, .705), (x + .08, y + .1, .78))
        crossbearer = [(-S, .98), (-.34, .80), (.34, .80), (S, .98), (S, 1.0), (-S, 1.0)]
        for y in (-4.05, -1.95, 1.95, 4.05):
            m.prism("Frame", crossbearer, "y", y - .05, y + .05)

    def side(self, m, s, lv):
        c, S, E, D, W = self.c, self.S, self.E, self.D, self.W
        Y = lambda yv: s * yv  # along the side as you face it, left negative
        # Side sill, and the side plate along the top.
        m.box("Body", (s * (S - .002), -E, .96), (s * (S + .04), E, 1.12))
        m.box("Body", (s * (S - .002), -E - .01, c.eave - .04), (s * (S + .055), E + .01, c.eave + .03))
        # Posts: the door posts flat, the corners wrapped round, the rest hat sections standing out.
        for e in (-1, 1):
            m.box("Body", (s * S, e * D, c.sill + .02), (s * (S + .04), e * (D + .12), c.eave - .04))
            m.box("Body", (s * (S - .12), e * (E - .12), c.sill), (s * (S + .05), e * (E + .05), c.eave - .04))
            for y in self.posts:
                yc = e * y
                at = lambda d: [(s * (S - .003), yc - .065), (s * (S + d), yc - .035), (s * (S + d), yc + .035), (s * (S - .003), yc + .065)]
                m.loft("Body", [(c.sill + .02, at(.025)), (c.sill + .3, at(c.post_depth)), (c.eave - .04, at(c.post_depth))], "z")
        # The door's tracks, from where it shuts to where it stops open, and the stops.
        lo, hi = -(W + self.travel + .12), W + .12
        m.box("Body", (s * (S + .04), Y(lo), 1.0), (s * (S + .14), Y(hi), 1.035))
        m.box("Body", (s * (S + .12), Y(lo), 1.035), (s * (S + .14), Y(hi), 1.06))
        top = c.door_top + .13
        m.box("Body", (s * (S - .002), Y(lo), top), (s * (S + .2), Y(hi), top + .03))
        m.box("Body", (s * (S + .18), Y(lo), top - .1), (s * (S + .2), Y(hi), top + .03))
        for y0, y1 in ((lo, lo + .08), (W + .02, W + .1)):
            m.box("Body", (s * (S + .04), Y(y0), 1.035), (s * (S + .16), Y(y1), 1.2))
            m.box("Body", (s * (S - .002), Y(y0), top - .14), (s * (S + .18), Y(y1), top))
        # Ladders at the corners, a sill step under each.
        for e in (-1, 1):
            y0, y1 = e * (E - .56), e * (E - .16)
            a = min(y0, y1)
            rungs = [1.3 + .42 * k for k in range(5)]
            details.ladder(m, "Grab", (s * S, a, 0), (0, 1, 0), (s, 0, 0), .4, rungs, .14)
            details.sill_step(m, "Grab", (s * (S + .02), a, .97), (0, 1, 0), (s, 0, 0), .4, .5)
        self.lettering(m, s, lv)
        # Reflective strips along the bottom, and the AEI tag.
        for k in (0, 2, 4):
            for q in (-1, 1):
                details.reflector(m, (s * (S + .001), Y(q * self.centres[k]), 1.24), (0, s, 0), (0, 0, 1))
        m.box("Dark", (s * (S + .04), Y(-(E - .9)) - .12, 1.02), (s * (S + .052), Y(-(E - .9)) + .12, 1.08))
        # A tack board for the waybill, beside the door.
        m.panel("TackBoard", (s * (S + .001), Y(self.centres[0]), 1.95), (0, s, 0), (0, 0, 1), rrect(-.24, .24, -.17, .17), .022)

    def lettering(self, m, s, lv):
        S = self.S
        P = lambda k: -self.centres[k - 1]  # panels to the left of the door, counting out from it
        R = lambda k: self.centres[k - 1]

        def put(yv, z, words, h, w=.8, kind="narrow"):
            m.text("Lettering", words, kind, (s * (S + .001), s * yv, z), (0, s, 0), (0, 0, 1), h, w)

        put(P(5), 3.55, lv["marks"], .2)
        put(P(5), 3.22, lv["number"], .2)
        for k, line in enumerate(lv["capacity"]):
            put(P(5), 2.36 - .11 * k, line, .062)
        name = lv["name"]
        if len(name) == 1:
            put(R(2), 3.3, name[0], .42, .66, kind="sans")
        else:
            for k, line in enumerate(name):
                put(R(2), 3.45 - .34 * k, line, .24, .66, kind="sans")
        put(R(5), 2.78, "PLATE C", .1)
        for k, line in enumerate(self.c.data):
            put(R(5), 2.48 - .1 * k, line, .055)

    def end(self, m, e, lv):
        c, S, E, OS = self.c, self.S, self.E, self.OS
        face = E + .055  # the face of the corrugations
        # Improved Dreadnaught corrugations: full-width ribs between shorter tapered ones.
        for k in range(c.ribs):
            zc = 1.32 + (3.92 - 1.32) * k / (c.ribs - 1)
            full = k % 2 == 0
            hb, ht, dep, reach, taper = (.11, .065, .055, S - .14, .1) if full else (.075, .04, .045, S - .39, .4)

            def profile(f):
                b, t, d = hb * (.45 + .55 * f), ht * (.45 + .55 * f), max(dep * f, .004)
                return [(e * (E - .003), zc - b), (e * (E + d), zc - t), (e * (E + d), zc + t), (e * (E - .003), zc + b)]

            m.loft("Body", [(-reach, profile(0)), (-reach + taper, profile(1)), (reach - taper, profile(1)), (reach, profile(0))], "x")
        # The end sill, with the coupler's opening in it, and the striker round that.
        for s in (-1, 1):
            m.box("Frame", (s * .24, e * E, .80), (s * S, e * OS, 1.08))
        cz = self.coupler_z
        lo, hi = cz - .17, cz + .17
        m.box("Frame", (-.24, e * E, hi + .02), (.24, e * OS, 1.08))
        m.box("Frame", (-.32, e * OS, hi), (.32, e * (OS + .03), hi + .08))
        m.box("Frame", (-.32, e * (OS - .1), lo - .06), (.32, e * (OS + .03), lo))
        for s in (-1, 1):
            m.box("Frame", (s * .24, e * OS, lo), (s * .32, e * (OS + .03), hi))
        # The end ladder on one side, a corner grab on the other.
        sx = -e
        rungs = [1.3 + .42 * k for k in range(7)]
        x0 = min(sx * .94, sx * 1.34)
        details.ladder(m, "Grab", (x0, e * face, 0), (1, 0, 0), (0, e, 0), .4, rungs, .13)
        details.grab(m, "Grab", (-sx * 1.3, e * face, 1.5), (-sx * 1.3, e * face, 2.4), (0, e, 0))
        # Reporting marks along the top of the end.
        m.text("Lettering", f"{lv['marks']} {lv['number']}", "narrow", (0, e * (E + .001), 4.17), (-e, 0, 0), (0, 0, 1), .085, 1.6)
        # The hand brake is on the B end.
        if e > 0:
            details.hand_brake(m, .72, face, e, 2.5, 1.36, (.25, 1.2))
        # The train line's end, its hose, and the uncoupling lever across the sill to the top of the coupler's head.
        details.air_hose(m, .4, OS, e, .74)
        lift_y = self.coupler_pivot + .33
        details.uncoupling_lever(m, e * 1.42, -e * .05, OS, e, .95, cz + .26, lift_y - OS)

    def brakes(self, m):
        """The AB brake set under the floor: reservoir, valve, cylinder, the levers and rods, and the train line."""
        S, E, OS, tc = self.S, self.E, self.OS, self.truck_centre
        x, z, r = .82, .80, .17
        m.cyl("Brake", (x, -3.85, z), (x, -2.75, z), r, 22)
        for y, d in ((-3.85, -1), (-2.75, 1)):
            m.lathe("Brake", (x, y, z), (0, d, 0), [(0, r), (.05, r * .85), (.08, r * .45), (.09, .02), (0, .02)], 22)
        for y in (-3.6, -3.0):
            m.box("Frame", (x - .2, y - .03, z + r - .01), (x + .2, y + .03, .99))
        m.box("Brake", (.68, -2.62, .72), (.98, -2.3, .95), .015)
        m.box("Frame", (.7, -2.6, .95), (.96, -2.32, .99))
        # The cylinder, the levers off its rod, and the rods to the trucks' brake beams.
        cx = -.72
        m.cyl("Brake", (cx, 2.3, z), (cx, 2.82, z), .13, 20)
        m.lathe("Brake", (cx, 2.3, z), (0, -1, 0), [(0, .13), (.04, .11), (.06, .05), (0, .05)], 20)
        m.box("Frame", (cx - .15, 2.4, z + .12), (cx + .15, 2.7, .99))
        m.cyl("Steel", (cx, 2.82, z), (cx, 3.12, z), .02, 8)
        m.box("Brake", (cx - .04, 3.1, z - .03), (.12, 3.16, z + .03))
        m.box("Brake", (cx - .04, 2.22, z - .03), (.12, 2.28, z + .03))
        m.tube("Brake", [(.08, 3.13, z), (.08, tc - .9, .72)], .016, 0, 8)
        m.tube("Brake", [(.08, 2.25, z), (.08, 1.3, z), (.08, -tc + .9, .72)], .016, 0, 8)
        # The hand brake's rod from the bell crank in the B end sill.
        m.tube("Brake", [(.9, E + .08, .9), (.9, E - .3, .84), (.4, E - 1.2, .8), (.08, 3.4, z)], .014, .1, 8)
        # Pipes: the train line along the centre sill, the branch to the valve, valve to cylinder.
        m.tube("Steel", [(.4, -OS + .02, .74), (.4, -OS + .3, .78), (.3, -OS + .5, .8), (.3, OS - .5, .8), (.4, OS - .3, .78), (.4, OS - .02, .74)], .02, .1, 8)
        m.tube("Steel", [(.3, -2.0, .8), (.6, -2.2, .84), (.7, -2.4, .84)], .013, .05, 6)
        m.tube("Steel", [(.7, -2.5, .9), (.5, -1.6, .95), (-.5, 1.6, .95), (cx, 2.3, .9)], .013, .1, 6)

    # ------------------------------------------------------------ the door

    def parts(self, lv):
        return {"door": self.door(lv)}

    def door(self, lv):
        """The +X side's door, shut: a plate with six pressed panels, on rollers below and guides above, with its lock and pull."""
        m = Kit()
        c, S, W = self.c, self.S, self.W
        x0 = S + .10
        h0, h1 = 1.075, c.door_top + .07
        m.box("Door", (x0, -W, h0), (x0 + .012, W, h1))
        for e in (-1, 1):
            m.box("Door", (x0, e * (W - .08), h0), (x0 + .04, e * W, h1))
        m.box("Door", (x0, -W, h0), (x0 + .04, W, h0 + .08))
        m.box("Door", (x0, -W, h1 - .08), (x0 + .04, W, h1))
        n = 6
        z0, z1 = h0 + .08, h1 - .08
        ph = (z1 - z0) / n
        for k in range(n):
            zc = z0 + ph * (k + .5)
            m.panel("Door", (x0 + .012, 0, zc), (0, 1, 0), (0, 0, 1), rrect(-(W - .13), W - .13, -ph / 2 + .05, ph / 2 - .05, .04, .04, 3), .022)
        # The lock rod on the shutting edge, its brackets and lever, and the pull on the other edge.
        xr = x0 + .06
        yr = W - .2
        m.cyl("Door", (xr, yr, h0 + .12), (xr, yr, h1 - .12), .017, 8)
        for z in (h0 + .25, (h0 + h1) / 2, h1 - .25):
            m.box("Door", (x0 + .012, yr - .04, z - .03), (xr + .02, yr + .04, z + .03))
        m.tube("Door", [(xr, yr, 2.05), (xr + .03, yr - .1, 2.0), (xr + .03, yr - .45, 1.8)], .014, .05, 8)
        m.box("Door", (x0 + .034, yr - .5, 1.74), (xr + .05, yr - .44, 1.86))
        details.grab(m, "Door", (x0 + .034, -W + .18, 1.75), (x0 + .034, -W + .18, 2.45), (1, 0, 0), .05, .014)
        # Rollers on the bottom track, guides in the top one.
        for e in (-1, 1):
            y = e * 1.05
            m.box("Dark", (S + .05, y - .12, 1.07), (x0 + .02, y + .12, h0 + .06))
            m.cyl("Dark", (S + .055, y, 1.08), (S + .115, y, 1.08), .045, 14)
            m.box("Dark", (x0, y - .08, h1), (x0 + .05, y + .08, h1 + .06))
        m.panel("TackBoard", (x0 + .034, -.85, z0 + ph * 2.5), (0, 1, 0), (0, 0, 1), rrect(-.25, .25, -.17, .17), .02)
        return m
