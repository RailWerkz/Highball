"""An enclosed bi-level autorack: an 89 ft flatcar carrying a steel rack with screened sides, a galvanized roof and doors at each end.

The flatcar is the usual long-car underframe: a fish-belly centre sill between the bolsters, straight side sills, the draft
sills round the coupler beyond the trucks, 70-ton trucks. The rack stands on its deck, as on a modern TTX car: a solid panel
at each end of each side with a ladder set in it, galvanized perforated screens between with their pressed ribs, flat posts
over the screens' joints, each screen one sheet from sill to eave, the owner's name on a plate, and a galvanized roof arched
across. Each end has two leaves hinged at the corners
that open outward and fold back against the sides; the platform beyond carries the hand brake on the B end.

The doors are moving parts: door_r_<livery>.glb is the leaf on the right-hand side of the B end looking at it from outside
(+X), door_l_<livery>.glb the other, each with its origin on its hinge; assembly.json puts each at the B end (yaw 0) and
again turned to the A end (yaw 180), and gives the swing that opens it.
"""
import math
from dataclasses import dataclass

from . import details
from .car import Car
from .kit import Kit
from .running_gear import TRUCK_70T, TruckSpec


@dataclass(frozen=True)
class AutorackSpec:
    deck: float = 1.06  # top of the flatcar's deck
    flat_side: float = 1.43  # outside of the flatcar's side sills
    rack_side: float = 1.615  # outside of the rack's side sheets: 10 ft 7 in wide over them
    post_depth: float = .06  # how far the flat posts stand out from the screens
    post_width: float = .17
    eave: float = 4.88  # where the sides stop, at a corner, and the roof turns in
    crown: float = 5.84  # 19 ft 2 in over the rail
    # The roof's facets, from the eave in to the middle, as (out from the middle over half the width, up from the eave over
    # the roof's height): a steep facet from the wall's corner, a shallower one, then flat across the top.
    roof_facets: tuple = ((1.0, 0.0), (.865, .64), (.595, 1.0), (0.0, 1.0))
    upper_deck: float = 3.34  # the rack's second deck inside, which the side's lettering is placed by
    screen_bottom: float = .24  # the screens' bottom over the deck, and their top under the eave
    screen_top: float = .17
    end_panel: float = 1.45  # the solid panel at each end of each side, where the ladder is and the doors fold back
    platform: float = .62  # from the doors' face to the end sill's
    panels: int = 13  # screen panels down each side, between the end panels
    truck_centre: float = 9.755  # half of 64 ft
    over_strikers: float = 27.43  # 90 ft
    over_couplers: float = 28.35  # 93 ft
    truck: TruckSpec = TRUCK_70T
    data: tuple = ("BI-LEVEL", "LD LMT 104800", "LT WT 115200", "BLT 7-02", "EXCESS HEIGHT CAR")


GALV = ("#a3a7aa", .6, .42)
SCREEN = ("#9da2a6", .7, .38)  # perforated galvanized sheet
SCREEN_RIB = ("#c3c7ca", .75, .3)  # its pressed ribs, catching the light
DOOR_WHITE = ("#e2e3de", 0, .5)


def paint(rack, flat, lettering, plate, plate_text, door=DOOR_WHITE):
    return {"Rack": rack, "Screen": SCREEN, "ScreenRib": SCREEN_RIB, "Door": door, "DoorTop": rack, "Roof": GALV, "Frame": flat,
            "Deck": ("#55575a", .3, .7), "Grab": flat, "Lettering": lettering, "Plate": plate, "PlateText": plate_text,
            "Patch": ("#141414", 0, .6), "PatchText": ("#f0f0ec", 0, .5)}


YELLOW = "#e0a919"
LIVERIES = {
    "rwkz": {"paint": paint(YELLOW, YELLOW, "#141414", ("#141414", 0, .5), ("#f2f2ee", 0, .5)), "marks": "RWKZ", "number": "812045",
             "flat": "RWKX 812045", "name": "RAILWERKZ"},
    "hblx": {"paint": paint("#4c2b1f", "#4c2b1f", "#ece9de", ("#ece9de", 0, .5), ("#4c2b1f", 0, .5), door=GALV), "marks": "HBLX",
             "number": "860317", "flat": "HBLX 860317", "name": "HIGHBALL"},
}


class Autorack(Car):
    def __init__(self, key="autorack_89", folder="Autorack_89ft", title="89ft Autorack", spec=AutorackSpec(), liveries=None):
        self.key, self.folder, self.title, self.c = key, folder, title, spec
        self.truck_spec = spec.truck
        self.truck_centre = spec.truck_centre
        self.over_couplers = spec.over_couplers
        self.liveries = liveries or LIVERIES
        c = spec
        self.OS = c.over_strikers / 2
        self.E = self.OS - c.platform  # the rack's ends: the corner posts' outer faces, where the doors close
        self.R = c.rack_side
        self.F = c.flat_side
        # Each leaf's hinge, on a bracket standing out past the posts, so that folded back against the side the leaf and its
        # stiffeners clear them; and the leaf's width, hinge to the middle.
        self.hinge = (self.R + .11, self.E + .03)
        self.leaf = self.hinge[0] - .006

    # ------------------------------------------------------------ the doors' places

    def places(self):
        hx, hy = self.hinge
        return {
            "door_r": [{"position": [hx, hy, 0], "yaw": 0, "swing": -270}, {"position": [-hx, -hy, 0], "yaw": 180, "swing": -270}],
            "door_l": [{"position": [-hx, hy, 0], "yaw": 0, "swing": 270}, {"position": [hx, -hy, 0], "yaw": 180, "swing": 270}],
        }

    def hinge_heights(self):
        c = self.c
        z0, z1 = c.deck + .03, c.eave - .2
        return [z0 + .25, z0 + (z1 - z0) / 3, z0 + 2 * (z1 - z0) / 3, z1 - .25]

    def parts(self, lv):
        return {"door_r": self.door(1, lv), "door_l": self.door(-1, lv)}

    # ------------------------------------------------------------ the body

    def body(self, lv):
        m = Kit()
        self.underframe(m)
        self.rack(m)
        self.roof(m)
        for s in (-1, 1):
            self.side(m, s, lv)
        for e in (-1, 1):
            self.end(m, e, lv)
        self.brakes(m)
        return m

    def roof_z(self, x):
        """The roof's height across the car: from the wall's corner at the eave in by its facets to the flat top."""
        c = self.c
        f = min(abs(x) / (self.R + .03), 1.0)
        pts = c.roof_facets
        for (f0, h0), (f1, h1) in zip(pts, pts[1:]):
            if f1 <= f <= f0:
                t = (f - f1) / (f0 - f1) if f0 > f1 else 0.0
                return c.eave + (c.crown - c.eave) * (h1 + (h0 - h1) * t)
        return c.crown

    def across(self, n=2):
        """Points across the roof's width: each facet's corners (the facets are flat, so they need no more), and n between."""
        half = self.R + .03
        fs = {k / n for k in range(n + 1)} | {f for f, _ in self.c.roof_facets}
        return sorted({-half * f for f in fs} | {half * f for f in fs})

    def underframe(self, m):
        c, F, OS, tc = self.c, self.F, self.OS, self.truck_centre
        # Side sills, and the deck plate over the whole car.
        for s in (-1, 1):
            m.box("Frame", (s * (F - .1), -OS, .80), (s * F, OS, c.deck))
        # (Only inside the rack: the end platforms have their own grating at the same height.)
        m.box("Deck", (-(F - .1), -self.E, c.deck - .04), (F - .1, self.E, c.deck))
        # The centre sill: a fish belly between the bolsters, deepest in the middle.
        stations = []
        n = 14
        for k in range(n + 1):
            y = -tc + 2 * tc * k / n
            bottom = .72 - .30 * math.sin(math.pi * k / n) ** .8
            stations.append((y, [(-.32, bottom), (.32, bottom), (.32, c.deck - .04), (-.32, c.deck - .04)]))
        m.loft("Frame", stations, "y")
        # Beyond the trucks, the draft sills round the coupler, the draft gear behind them.
        cz, p = self.coupler_z, self.coupler_pivot
        low = cz - .17
        for e in (-1, 1):
            for s in (-1, 1):
                m.box("Frame", (s * .22, e * tc, .74), (s * .24, e * OS, c.deck - .04))
                m.box("Frame", (s * .22, e * (tc + .45), low), (s * .24, e * OS, .74))
                m.box("Frame", (s * .22, e * (tc + .45), low - .02), (s * .34, e * OS, low))
            m.box("Dark", (-.2, e * (p - .2), cz - .12), (.2, e * (p - .85), cz + .12), .01)
        # Body bolsters on the centre plates and side bearings.
        bolster = [(-F, .82), (-.34, .70), (.34, .70), (F, .82), (F, c.deck - .04), (-F, c.deck - .04)]
        for y in (-tc, tc):
            m.prism("Frame", bolster, "y", y - .2, y + .2)
            m.cyl("Steel", (0, y, c.truck.centre_plate), (0, y, .70), .19, 24)
            for s in (-1, 1):
                x = s * c.truck.side_bearing
                m.box("Frame", (x - .08, y - .1, .705), (x + .08, y + .1, .78))
        # Cross bearers between the bolsters, tapering out to the side sills.
        crossbearer = [(-F, .84), (-.32, .78), (.32, .78), (F, .84), (F, c.deck - .04), (-F, c.deck - .04)]
        k = 4
        for j in range(1, k + 1):
            y = tc * j / (k + 1)
            for q in (-1, 1):
                m.prism("Frame", crossbearer, "y", q * y - .05, q * y + .05)

    def rack(self, m):
        """The sides: a solid panel at each end, galvanized screens between with their pressed ribs, flat posts over the screens'
        joints, the rack's sill and eave rail; the corner posts, the door headers and hinge brackets."""
        c, R, E = self.c, self.R, self.E
        S = E - c.end_panel  # where the screens end and the end panels begin
        top = c.eave - self.UNDER  # everything on the sides stops just inside the roof's underside, so none shows through it
        z0, z1 = c.deck + c.screen_bottom, c.eave - c.screen_top
        for s in (-1, 1):
            for e in (-1, 1):
                m.box("Rack", (s * (R - .006), e * S, c.deck), (s * R, e * E, top))
            m.box("Screen", (s * (R - .027), -S, z0), (s * (R - .024), S, z1))  # its inner face clear of the rack's
            # The screens' pressed ribs, across the whole length (the posts cover their joints).
            n = int((z1 - z0) / .1)
            for k in range(1, n):
                z = z0 + (z1 - z0) * k / n
                m.box("ScreenRib", (s * (R - .024), -S, z - .016), (s * (R - .012), S, z + .016))
            # The rack's sill under the screens and the eave rail over them; each screen is one sheet between them.
            m.box("Rack", (s * (R - .03), -E, c.deck), (s * (R + .02), E, z0))
            m.box("Rack", (s * (R - .03), -E - .02, z1), (s * (R + .05), E + .02, top))
            # Flat posts over the screens' joints, the two at the end panels' edges a little wider.
            for k in range(c.panels + 1):
                y = -S + 2 * S * k / c.panels
                w = c.post_width * (1.3 if k in (0, c.panels) else 1.0)
                m.box("Rack", (s * (R - .03), y - w / 2, c.deck + .02), (s * (R + c.post_depth), y + w / 2, top))
            # Corner posts, wrapping round to the end.
            for e in (-1, 1):
                m.box("Rack", (s * (R - .1), e * (E - .14), c.deck), (s * (R + .06), e * E, top))
        for e in (-1, 1):
            # The header over the doors, following the roof's arch, and the sill under them.
            xs = self.across()
            arch = [(x, self.roof_z(x) - self.HEADER) for x in xs] + [(x, self.roof_z(x) - self.UNDER) for x in reversed(xs)]
            m.prism("Rack", arch, "y", e * (E - .14), e * (E + .005))
            m.box("Rack", (-R, e * (E - .14), c.deck), (R, e * E, c.deck + .05))
            # The doors' hinge brackets, out from the corner posts to the pins.
            hx, hy = self.hinge
            for s in (-1, 1):
                for z in self.hinge_heights():
                    m.box("Steel", (s * (R + .02), e * (E - .1), z - .05), (s * (hx + .035), e * (hy + .02), z + .05))

    HEADER = .14  # the depth of the arched header over the doors
    ROOF = .025  # the roof sheet's thickness
    UNDER = .024  # how far below the roof's surface the sides and header stop: inside the sheet, never level with its face

    def roof(self, m):
        c, R, E = self.c, self.R, self.E
        xs = self.across()
        top = [(x, self.roof_z(x)) for x in xs]
        m.prism("Roof", top + [(x, z - self.ROOF) for x, z in reversed(top)], "y", -E - .05, E + .05)
        # Ribs across it, a sheet's width apart.
        n = int(2 * E / .62)
        cap = [(x, self.roof_z(x) + .02) for x in xs[1:-1]] + [(x, self.roof_z(x) - .005) for x in reversed(xs[1:-1])]
        for k in range(n + 1):
            y = -E + 2 * E * k / n
            m.prism("Roof", cap, "y", y - .025, y + .025)

    def side(self, m, s, lv):
        c, R, E, F, OS = self.c, self.R, self.E, self.F, self.OS
        x = s * (R + .001)
        Y = lambda yv: s * yv  # noqa: E731  along the side as you face it, left negative
        S = E - c.end_panel
        bay = 2 * S / c.panels
        post = s * (R + c.post_depth + .001)

        def put(yv, z, words, h, w, role="Lettering", kind="narrow", at=x):
            m.text(role, words, kind, (at, Y(yv), z), (0, s, 0), (0, 0, 1), h, w)

        # The owner's name on a plate on the middle posts, up in the top level.
        pz = (c.upper_deck + c.eave - c.screen_top) / 2
        pw = bay * 1.9
        m.box("Plate", (s * (R + c.post_depth), Y(-pw / 2), pz - .3), (s * (R + c.post_depth + .012), Y(pw / 2), pz + .3))
        put(0, pz, lv["name"], .34, pw - .25, role="PlateText", kind="sans", at=s * (R + c.post_depth + .013))
        # On the end panels: marks and number at the A end, the data at the B end, as stencils.
        ya, yb = -(E - c.end_panel / 2 - .1), E - c.end_panel / 2 - .1
        put(ya, c.upper_deck + .9, lv["marks"], .12, c.end_panel - .4)
        put(ya, c.upper_deck + .7, lv["number"], .12, c.end_panel - .4)
        put(yb, c.upper_deck + .9, c.data[0], .1, c.end_panel - .4)
        for k, line in enumerate(c.data[1:4]):
            put(yb, c.upper_deck + .65 - .09 * k, line, .06, c.end_panel - .4)
        put(yb, c.upper_deck - .4, c.data[4], .07, c.end_panel - .4)
        # The flatcar's marks, white on a black patch on its side sill.
        fy = -(OS - 4.2)
        m.box("Patch", (s * F, Y(fy) - .7, .84), (s * (F + .006), Y(fy) + .7, 1.0))
        put(fy, .92, lv["flat"], .1, 1.3, role="PatchText", at=s * (F + .007))
        # Reflective strips on the side sill and the posts.
        for yv in (-(OS - 1.0), -OS / 2, 0, OS / 2, OS - 1.0):
            details.reflector(m, (s * (F + .001), Y(yv), .86), (0, s, 0), (0, 0, 1))
        for k in (1, c.panels // 2, c.panels - 1):
            details.reflector(m, (post, Y(-S + bay * k), c.deck + .5), (0, s, 0), (0, 0, 1), .1, .45)
        # Sill steps under each end, and a ladder set in the end panel beside each.
        for e in (-1, 1):
            y0 = e * (OS - .5)
            details.sill_step(m, "Grab", (s * (F + .01), min(y0, e * (OS - .1)), .82), (0, 1, 0), (s, 0, 0), .4, .45)
            ly = e * (E - .75)
            rungs = [1.4 + .42 * k for k in range(6)]
            details.ladder(m, "Grab", (s * R, min(ly, ly + e * .42), 0), (0, 1, 0), (s, 0, 0), .42, rungs, .1)
        m.box("Dark", (s * F, Y(-(OS - 1.6)) - .12, .87), (s * (F + .012), Y(-(OS - 1.6)) + .12, .93))

    def end(self, m, e, lv):
        c, R, E, F, OS = self.c, self.R, self.E, self.F, self.OS
        cz = self.coupler_z
        # The platform: grating from the doors to the end sill.
        # Between the side sills, whose tops are at the deck's height too.
        m.box("Grating", (-(F - .1), e * E, c.deck - .04), (F - .1, e * (OS - .3), c.deck))
        for k in range(13):
            xx = -(F - .13) + 2 * (F - .13) * k / 12
            m.box("Grating", (xx - .006, e * (E + .06), c.deck), (xx + .006, e * (OS - .3), c.deck + .012))
        # The end sill, with the coupler's opening, and the striker.
        for s in (-1, 1):
            m.box("Frame", (s * .24, e * (OS - .3), .80), (s * F, e * OS, c.deck))
        lo, hi = cz - .17, cz + .17
        m.box("Frame", (-.24, e * (OS - .3), hi + .02), (.24, e * OS, c.deck))
        m.box("Frame", (-.32, e * OS, hi), (.32, e * (OS + .03), hi + .08))
        m.box("Frame", (-.32, e * (OS - .1), lo - .06), (.32, e * (OS + .03), lo))
        for s in (-1, 1):
            m.box("Frame", (s * .24, e * OS, lo), (s * .32, e * (OS + .03), hi))
            # A grab across each corner of the end sill.
            details.grab(m, "Grab", (s * (F - .1), e * OS, .95), (s * (F - .55), e * OS, .95), (0, e, 0), .06)
        # The reporting marks across the end sill.
        m.text("Lettering", f"{lv['marks']} {lv['number']}", "narrow", (-e * .85, e * (OS + .001), .93), (-e, 0, 0), (0, 0, 1), .06, .9)
        # The hand brake on the B end, on a stand at the platform's outer edge, facing out.
        if e > 0:
            face = OS - .28
            m.box("Frame", (.42, face - .03, c.deck + .012), (1.1, face, c.deck + 1.2))  # standing on the grating's bars
            details.hand_brake(m, .76, face, 1, c.deck + .8, c.deck + .02, (.44, 1.08))
        # The train line's end, its hose, and the uncoupling lever across the end sill to the top of the coupler's head.
        details.air_hose(m, .4, OS, e, .74)
        details.uncoupling_lever(m, e * 1.4, -e * .05, OS, e, .95, cz + .26, self.coupler_pivot + .33 - OS)

    def brakes(self, m):
        """The AB brake set under the deck between the bolsters: reservoir, valve, cylinder, rods to the trucks, the train line."""
        c, OS, tc = self.c, self.OS, self.truck_centre
        x, z, r = .86, .82, .16
        m.cyl("Brake", (x, -5.6, z), (x, -4.5, z), r, 22)
        for y, d in ((-5.6, -1), (-4.5, 1)):
            m.lathe("Brake", (x, y, z), (0, d, 0), [(0, r), (.05, r * .85), (.08, r * .45), (.09, .02), (0, .02)], 22)
        for y in (-5.35, -4.75):
            m.box("Frame", (x - .2, y - .03, z + r - .01), (x + .2, y + .03, c.deck - .04))
        m.box("Brake", (.7, -4.37, .74), (1.0, -4.05, .96), .015)
        cx = -.72
        m.cyl("Brake", (cx, 4.0, z), (cx, 4.52, z), .13, 20)
        m.lathe("Brake", (cx, 4.0, z), (0, -1, 0), [(0, .13), (.04, .11), (.06, .05), (0, .05)], 20)
        m.box("Frame", (cx - .15, 4.1, z + .12), (cx + .15, 4.4, c.deck - .04))
        m.tube("Brake", [(cx, 4.55, z), (.08, 4.9, z), (.08, tc - .9, .74)], .016, .1, 8)
        m.tube("Brake", [(cx, 3.95, z), (.08, 3.6, z), (.08, -tc + .9, .74)], .016, .1, 8)
        m.tube("Steel", [(.42, -OS + .02, .74), (.42, -OS + .3, .78), (.4, -tc, .79), (.4, tc, .79), (.42, OS - .3, .78), (.42, OS - .02, .74)], .02, .1, 8)

    # ------------------------------------------------------------ the doors

    def door(self, side, lv):
        """One leaf, shut, its origin on its hinge: side 1 the leaf that reaches from the hinge towards -X (the right-hand one at
        the B end, seen from outside), -1 its mirror. White below and the rack's colour across the top, three slim ribs, two
        long lock rods with their keepers and handles, hinges, the seal at the meeting edge; the marks and a warning plate on
        the right-hand leaf."""
        m = Kit()
        c = self.c
        w = self.leaf
        hx = self.hinge[0]
        X = lambda d: -side * d  # noqa: E731  from the hinge out along the leaf
        top = lambda d: self.roof_z(hx - d) - self.HEADER - .01  # noqa: E731  its top, under the arched header
        z0 = c.deck + .03
        band = c.eave - .85  # where the white gives way to the rack's colour
        ds = sorted({min(max(hx - abs(x), 0.0), w) for x in self.across()} | {0.0, w})
        # The leaf, white below and the rack's colour above, its top following the arch.
        m.box("Door", (X(0), -.012, z0), (X(w), .012, band))
        m.prism("DoorTop", [(X(0), band), (X(w), band)] + [(X(d), top(d)) for d in reversed(ds)], "y", -.012, .014)
        for d in (.06, w * .5, w - .06):
            m.box("Door", (X(d - .02), .012, z0 + .05), (X(d + .02), .03, band - .02))
            m.box("DoorTop", (X(d - .02), .014, band), (X(d + .02), .03, top(d) - .05))
        m.prism("Rubber", [(X(w - .015), z0 + .003), (X(w + .008), z0 + .003), (X(w + .008), top(w) - .003), (X(w - .015), top(w - .015) - .003)],
                "y", -.02, .02)
        # Two lock rods, their keepers top and bottom and along, and a handle on each.
        for f in (.42, .86):
            rx, z1 = X(w * f), top(w * f)
            m.cyl("Steel", (rx, .06, z0 - .02), (rx, .06, z1 + .02), .016, 8)
            for z in (z0 + .02, z0 + .9, (z0 + z1) / 2, z1 - .9, z1 - .02):
                m.box("Steel", (rx - .035, .02, z - .03), (rx + .035, .085, z + .03))
            hz = c.deck + 1.05
            m.tube("Steel", [(rx, .075, hz), (rx, .13, hz), (rx + side * .3, .13, hz - .12)], .014, .04, 8)
        # Hinges round the pins.
        for z in self.hinge_heights():
            m.cyl("Steel", (0, 0, z + .05), (0, 0, z + .14), .03, 10)
            m.box("Steel", (X(.02), -.018, z + .06), (X(.3), .038, z + .13))  # proud of the ribs it crosses, both faces
        if side > 0:
            # The warning plate up in the corner, and the marks under it.
            px = X(w * .2)
            m.box("Plate", (px - .22, .03, band + .2), (px + .22, .036, band + .62))
            for k, line in enumerate(("EXCESS", "HEIGHT", "CAR")):
                m.text("PlateText", line, "narrow", (px, .037, band + .52 - .12 * k), (-1, 0, 0), (0, 0, 1), .07, .4)
            for k, line in enumerate((lv["marks"], lv["number"])):
                m.text("Patch", line, "narrow", (px, .031, band - .2 - .13 * k), (-1, 0, 0), (0, 0, 1), .08, .4)
        return m
