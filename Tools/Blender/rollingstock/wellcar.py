"""A stand-alone 53 ft well car: a double-stack car on two 125-ton trucks, its containers held by inter-box connectors.

The well is open-floored between two deep side girders: cross-bearers low between them carry the bottom container by its
corner castings, at the 53' positions and the 40' ones, 0.27 m over the rail. Over each truck the girders come up shallower
to an end structure: a bolster on the truck, the draft sills round the coupler, a grated platform with corner railings, the
hand brake on the B end. Container guides stand up at the well's corners. The brakes are truck-mounted, as such cars' are.

The sizes are a WellSpec. Loads are the car's own (see Car.loads): empty, a 53' box in the well, and a 40' high cube stacked
on top of that, the containers being the shared models from containers.py.
"""
from dataclasses import dataclass

from . import containers, details
from .car import Car
from .kit import Kit
from .running_gear import TRUCK_125T, TruckSpec


@dataclass(frozen=True)
class WellSpec:
    well_length: float = 16.31  # inside the end bulkheads: room for a 53' box
    floor: float = .27  # top of the cross-bearers, where the bottom box sits
    sill_in: float = 1.335  # inner face of the side girders: a 102" box with room
    sill_out: float = 1.58  # outer face of the top chord: the car's width
    sill_top: float = 1.52
    girder_bottom: float = .12  # the girders' bottom chord along the well
    end_bottom: float = .80  # and over the trucks
    deck: float = 1.24  # the end platforms' grating
    truck_centre: float = 9.6  # clear of a 53' box's bottom corners
    over_strikers: float = 22.7
    over_couplers: float = 23.6  # 77'5"
    truck: TruckSpec = TRUCK_125T
    data: tuple = ("53 FT WELL", "LD LMT 199400", "LT WT 86600", "DOUBLE STACK", "BLT 4-04")


YELLOW, BROWN, WHITE, BLACK = "#d8a51d", "#4c2b1f", "#ece9de", "#141414"


def paint(body, lettering, grab=None):
    return {"Body": body, "Frame": body, "Grab": grab or body, "Lettering": lettering}


LIVERIES = {
    "rwkz": {"paint": paint(YELLOW, BLACK), "marks": "RWKZ", "number": "530112"},
    "hblx": {"paint": paint(BROWN, WHITE, "#d8a51d"), "marks": "HBLX", "number": "722048"},
}


class WellCar(Car):
    def __init__(self, key="well_53", folder="WellCar_53ft", title="53ft Well Car", spec=WellSpec(), liveries=None):
        self.key, self.folder, self.title, self.c = key, folder, title, spec
        self.truck_spec = spec.truck
        self.truck_centre = spec.truck_centre
        self.over_couplers = spec.over_couplers
        self.liveries = liveries or LIVERIES
        self.WE = spec.well_length / 2  # the well's ends, inside the bulkheads
        self.OS = spec.over_strikers / 2
        self.E = self.OS - .3  # the end sill's inner face

    # ------------------------------------------------------------ loads

    def cargo(self):
        """The containers this car carries, built once into Models/RollingStock/Containers."""
        out = {}
        for spec, paint in ((containers.C53, containers.PAINTS["highball"]), (containers.C40HC, containers.PAINTS["rwkz"])):
            out[containers.model_name(spec, paint)] = (lambda s=spec, p=paint: containers.container(s, p), containers.colours(paint))
        return out

    def loads(self):
        c = self.c
        bottom = containers.model_name(containers.C53, containers.PAINTS["highball"])
        top = containers.model_name(containers.C40HC, containers.PAINTS["rwkz"])
        upper = c.floor + containers.C53.height + .012  # on inter-box connectors
        return {
            "empty": [],
            "single": [{"model": bottom, "position": [0, 0, c.floor], "yaw": 0}],
            "double": [{"model": bottom, "position": [0, 0, c.floor], "yaw": 0}, {"model": top, "position": [0, 0, round(upper, 4)], "yaw": 0}],
        }

    # ------------------------------------------------------------ the body

    def body(self, lv):
        m = Kit()
        self.girders(m)
        self.well(m)
        for e in (-1, 1):
            self.end(m, e, lv)
        for s in (-1, 1):
            self.side(m, s, lv)
        return m

    def girders(self, m):
        """The two side girders: a deep plate web between a box top chord and a bottom chord along the well, shallower over the
        trucks, ramped between, stiffened outside."""
        c, WE, OS = self.c, self.WE, self.OS
        ramp = WE + .45
        for s in (-1, 1):
            X = lambda a, b: (s * a, s * b)
            x0, x1 = X(c.sill_in, c.sill_in + .03)
            # The web along the well, and its ramps up to the end depth.
            chord = c.sill_top - .2  # the web stops under the top chord, so their faces don't overlap
            m.box("Body", (x0, -WE, c.girder_bottom), (x1, WE, chord))
            for e in (-1, 1):
                m.prism("Body", [(e * WE, c.girder_bottom), (e * ramp, c.end_bottom), (e * ramp, chord), (e * WE, chord)][::e], "x", min(x0, x1), max(x0, x1))
                m.box("Body", (x0, e * ramp, c.end_bottom), (x1, e * (OS - .005), chord))  # the girders end just inside the end sill's face
            # The top chord, full length, a box standing out from the web.
            m.box("Body", (s * c.sill_in, -(OS - .005), c.sill_top - .2), (s * c.sill_out, OS - .005, c.sill_top), .006)
            # The bottom chord along the well, and along the ends.
            m.box("Body", (s * c.sill_in, -WE, c.girder_bottom), (s * (c.sill_in + .2), WE, c.girder_bottom + .1))
            for e in (-1, 1):
                m.box("Body", (s * c.sill_in, e * ramp, c.end_bottom), (s * (c.sill_in + .2), e * (OS - .005), c.end_bottom + .08))
                # Along the ramp's lower edge.
                m.prism("Body", [(e * WE, c.girder_bottom), (e * ramp, c.end_bottom), (e * ramp, c.end_bottom + .08), (e * WE, c.girder_bottom + .1)][::e], "x",
                        min(s * c.sill_in, s * (c.sill_in + .2)), max(s * c.sill_in, s * (c.sill_in + .2)))
            # Vertical stiffeners outside, between the chords.
            n = 12
            for k in range(n + 1):
                y = -WE + 2 * WE * k / n
                m.box("Body", (s * (c.sill_in + .03), y - .03, c.girder_bottom + .1), (s * (c.sill_in + .13), y + .03, c.sill_top - .2))
            for e in (-1, 1):
                for y in (ramp + .5, (ramp + OS) / 2 + .3):
                    m.box("Body", (s * (c.sill_in + .03), e * y - .03, c.end_bottom + .08), (s * (c.sill_in + .13), e * y + .03, c.sill_top - .2))

    def well(self, m):
        """Cross-bearers between the girders' bottom chords, a bulkhead at each end, and guides at the corners."""
        c, WE = self.c, self.WE
        x = c.sill_in
        bearers = [-WE + .09, -6.096, -4.0, -2.0, 0, 2.0, 4.0, 6.096, WE - .09]
        for y in bearers:
            m.box("Frame", (-x, y - .09, c.girder_bottom + .02), (x, y + .09, c.floor - .015))
            # A pad under each corner casting's place.
            for s in (-1, 1):
                m.box("Steel", (s * (x - .32), y - .1, c.floor - .015), (s * (x - .02), y + .1, c.floor))
        # Two longitudinal stringers tying the bearers.
        for s in (-1, 1):
            m.box("Frame", (s * .55, -WE, c.girder_bottom + .04), (s * .7, WE, c.floor - .03))
        for e in (-1, 1):
            # The bulkhead: a plate across the well's end, with a top beam and two gussets.
            m.box("Body", (-x, e * WE, c.girder_bottom), (x, e * (WE + .05), c.sill_top - .1))
            m.box("Body", (-x, e * (WE + .05), c.sill_top - .3), (x, e * (WE + .35), c.sill_top - .1))
            for s in (-1, 1):
                m.prism("Body", [(e * (WE + .05), c.girder_bottom + .3), (e * (WE + .45), c.end_bottom), (e * (WE + .45), c.sill_top - .3),
                                 (e * (WE + .05), c.sill_top - .3)][::e], "x", min(s * .4, s * .44), max(s * .4, s * .44))
            # Container guides at the corners: angles standing above the girders, flared at the top.
            for s in (-1, 1):
                gx, gy = s * (x - .005), e * (WE - .005)
                m.box("Frame", (gx, gy, c.sill_top), (gx - s * .1, gy - e * .012, 2.3))
                m.box("Frame", (gx, gy, c.sill_top), (gx - s * .012, gy - e * .1, 2.3))
                m.box("Frame", (gx + s * .06, gy, 2.3), (gx - s * .1, gy - e * .012, 2.42))

    def end(self, m, e, lv):
        c, OS, E, tc = self.c, self.OS, self.E, self.truck_centre
        x = c.sill_out
        cz, p = self.coupler_z, self.coupler_pivot
        low = cz - .17
        # The stub centre sill from the bulkhead to the end sill, down into the draft sills round the coupler beyond the truck.
        for s in (-1, 1):
            m.box("Frame", (s * .22, e * (self.WE + .35), .74), (s * .24, e * OS, c.deck - .04))
            m.box("Frame", (s * .22, e * (self.WE + .35), .72), (s * .34, e * OS, .74))
            m.box("Frame", (s * .22, e * (tc + .45), low), (s * .24, e * OS, .74))
            m.box("Frame", (s * .22, e * (tc + .45), low - .02), (s * .34, e * OS, low))
        m.box("Dark", (-.2, e * (p - .2), cz - .12), (.2, e * (p - .85), cz + .12), .01)
        # The body bolster across the truck, on its centre plate and side bearings.
        bolster = [(-x, c.end_bottom + .1), (-.34, .70), (.34, .70), (x, c.end_bottom + .1), (x, c.deck - .04), (-x, c.deck - .04)]
        y = e * tc
        m.prism("Frame", bolster, "y", y - .2, y + .2)
        m.cyl("Steel", (0, y, c.truck.centre_plate), (0, y, .70), .19, 24)
        for s in (-1, 1):
            sb = s * c.truck.side_bearing
            m.box("Frame", (sb - .08, y - .09, .705), (sb + .08, y + .09, .78))  # shorter than the truck's, whose top it sits into
        # Cross members under the deck, and the deck: grating from the bulkhead to the end sill.
        for yy in (self.WE + .7, tc + 1.0):
            m.box("Frame", (-x, e * yy - .05, c.deck - .2), (x, e * yy + .05, c.deck - .04))
        m.box("Grating", (-(x - .02), e * (self.WE + .35), c.deck - .04), (x - .02, e * E, c.deck))
        n = 14
        for k in range(n + 1):
            xx = -(x - .05) + 2 * (x - .05) * k / n
            m.box("Grating", (xx - .006, e * (self.WE + .35), c.deck), (xx + .006, e * E, c.deck + .012))
        # The end sill, with the coupler's opening, and the striker.
        for s in (-1, 1):
            m.box("Frame", (s * .24, e * E, .805), (s * x, e * OS, c.deck))  # its bottom off the girder's bottom chord
        hi = cz + .17
        m.box("Frame", (-.24, e * E, hi + .02), (.24, e * OS, c.deck))
        m.box("Frame", (-.32, e * OS, hi), (.32, e * (OS + .03), hi + .08))
        m.box("Frame", (-.32, e * (OS - .1), low - .06), (.32, e * (OS + .03), low))
        for s in (-1, 1):
            m.box("Frame", (s * .24, e * OS, low), (s * .32, e * (OS + .03), hi))
        # Railings round the platform's outer corners.
        top = c.deck + 1.0
        for s in (-1, 1):
            px, py = s * (x - .06), e * (OS - .08)
            foot = c.deck + .013  # standing on the grating's bars
            m.tube("Grab", [(px, e * (self.WE + .9), foot), (px, e * (self.WE + .9), top), (px, py, top), (s * .55, py, top), (s * .55, py, foot)], .02, .08, 8)
            m.tube("Grab", [(px, py, foot), (px, py, top)], .02, 0, 8)
            m.tube("Grab", [(px, e * (self.WE + .9), c.deck + .5), (px, py, c.deck + .5), (s * .55, py, c.deck + .5)], .015, .06, 8)
        # A sill step under each corner, and a grab on the end sill beside it.
        for s in (-1, 1):
            y0 = e * (OS - .55)
            details.sill_step(m, "Grab", (s * (x + .01), min(y0, e * (OS - .15)), .82), (0, 1, 0), (s, 0, 0), .4, .45)
            details.grab(m, "Grab", (s * (x - .1), e * OS, .95), (s * (x - .55), e * OS, .95), (0, e, 0), .06)
        # The hand brake on the B end, on a stand at the platform's end.
        if e > 0:
            face = OS - .26
            m.box("Body", (.4, face - .03, c.deck), (1.08, face, c.deck + 1.25))
            details.hand_brake(m, .74, face - .03, -1, c.deck + .85, c.deck + .02, (.42, 1.06))
        # The train line's end, its hose, and the uncoupling lever across the end sill to the top of the coupler's head.
        details.air_hose(m, .4, OS, e, .74)
        details.uncoupling_lever(m, e * 1.42, -e * .05, OS, e, .95, cz + .26, p + .33 - OS)
        # The reporting marks across the end sill.
        m.text("Lettering", f"{lv['marks']} {lv['number']}", "narrow", (-e * .85, e * (OS + .001), 1.08), (-e, 0, 0), (0, 0, 1), .07, .9)

    def side(self, m, s, lv):
        c, WE, OS = self.c, self.WE, self.OS
        x = s * (c.sill_out + .001)
        Y = lambda yv: s * yv

        def put(yv, z, words, h, w, kind="narrow"):
            m.text("Lettering", words, kind, (x, Y(yv), z), (0, s, 0), (0, 0, 1), h, w)

        # Along the top chord's face: marks and number at the A end, the data at the B end.
        put(-(WE - 1.6), c.sill_top - .1, f"{lv['marks']} {lv['number']}", .13, 2.2)
        put(WE - 1.6, c.sill_top - .1, "  ".join(c.data[:3]), .07, 2.6)
        # On the web, between stiffeners: the marks big, and the rest of the data.
        web = s * (c.sill_in + .031)
        # The stiffeners are every twelfth of the well; the lettering goes in the middle of the bays between them.
        bay = 2 * WE / 12
        mid = lambda k: -WE + bay * (k + .5)  # noqa: E731
        m.text("Lettering", lv["marks"], "sans", (web, Y(mid(4)), .78), (0, s, 0), (0, 0, 1), .3, bay - .25)
        m.text("Lettering", lv["number"], "sans", (web, Y(mid(7)), .78), (0, s, 0), (0, 0, 1), .22, bay - .25)
        m.text("Lettering", c.data[3], "narrow", (web, Y(mid(9)), .9), (0, s, 0), (0, 0, 1), .09, bay - .25)
        m.text("Lettering", c.data[4], "narrow", (web, Y(mid(9)), .7), (0, s, 0), (0, 0, 1), .07, bay - .25)
        # Reflective strips along the top chord, and the AEI tag at the A end.
        for yv in (-(WE - .3), -WE / 2, 0, WE / 2, WE - .3):
            details.reflector(m, (x, Y(yv), c.sill_top - .26), (0, s, 0), (0, 0, 1))
        m.box("Dark", (s * c.sill_out, Y(-(OS - 1.4)) - .12, c.sill_top - .17), (s * (c.sill_out + .012), Y(-(OS - 1.4)) + .12, c.sill_top - .11))
