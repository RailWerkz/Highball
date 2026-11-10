"""What freight cars run on: three-piece trucks and wheelsets with roller bearings. The coupler is shared: see coupler.py.

    truck     origin on the railhead at the centre plate, turning about Z; wheelsets go in at (0, +-axle, wheel_r)
    wheelset  origin at the axle's centre, rolling about X; the bearings' end caps turn with it
"""
import math
from dataclasses import dataclass

from .kit import Kit, span


@dataclass(frozen=True)
class TruckSpec:
    name: str
    wheel_r: float  # wheel radius
    axle: float  # half the wheelbase
    journal: float  # half the distance between the journals' centres
    centre_plate: float  # top of the bolster's centre bowl above the railhead
    side_bearing: float  # side bearings out from the centre


# A Barber S-2 type: 36" wheels, 5'10" wheelbase, 6.5 x 12 roller bearings. Under 100-ton cars, 286,000 lb on the rail.
TRUCK_100T = TruckSpec("100-ton", .4572, .889, 1.0, .66, .635)
# The same kind for 70 tons: 33" wheels, 5'6" wheelbase, 6 x 11 bearings.
TRUCK_70T = TruckSpec("70-ton", .4191, .838, .975, .625, .635)
# The 125-ton kind, under well cars and other heavy intermodal cars: 38" wheels, 6' wheelbase, 6.5 x 12 bearings.
TRUCK_125T = TruckSpec("125-ton", .4826, .914, 1.0, .70, .635)

def truck(t: TruckSpec):
    m = Kit()
    r, a, j, cp = t.wheel_r, t.axle, t.journal, t.centre_plate
    ped = r + .145  # top of the pedestal opening, over the bearing's adapter
    jaw = .115  # half the pedestal opening
    top = cp + .16  # top of the side frame over the bolster
    win = .30  # half the bolster opening, between the columns
    col = .09  # column thickness
    seat = r - .125  # the spring seat in the bottom of the opening
    spring_top = cp - .17  # the bolster's ends ride on the springs here
    end = a + jaw + .13
    inner = a - jaw - .07  # inner face of the inner pedestal leg

    for s in (-1, 1):
        x0, x1 = span(s * (j - .06), s * (j + .06))
        # The side frame: a top member arched over the bolster, the pedestals over the bearings, a tension member slung
        # below, and the columns between them either side of the bolster opening.
        top_member = [(-end, ped), (-inner, ped), (-(win + col), top - .13), (win + col, top - .13), (inner, ped), (end, ped),
                      (end, ped + .07), (a, ped + .13), (win + col + .1, top), (-(win + col + .1), top), (-a, ped + .13), (-end, ped + .07)]
        m.prism("Truck", top_member, "x", x0, x1)
        tension = [(-(a - jaw), ped - .16), (-(win + col), seat - .08), (win + col, seat - .08), (a - jaw, ped - .16),
                   (inner, ped - .06), (win + col, seat), (-(win + col), seat), (-inner, ped - .06)]
        m.prism("Truck", tension, "x", x0, x1)
        # A bead along the outer face of each member, the frame's flange.
        f0, f1 = span(s * (j + .06), s * (j + .075))
        m.prism("Truck", [(-a, ped + .1), (-(win + col + .1), top - .03), (win + col + .1, top - .03), (a, ped + .1),
                          (a, ped + .13), (win + col + .1, top), (-(win + col + .1), top), (-a, ped + .13)], "x", f0, f1)
        m.prism("Truck", [(-(a - jaw), ped - .16), (-(win + col), seat - .08), (win + col, seat - .08), (a - jaw, ped - .16),
                          (a - jaw - .02, ped - .12), (win + col, seat - .05), (-(win + col), seat - .05), (-(a - jaw - .02), ped - .12)], "x", f0, f1)
        for e in (-1, 1):
            # Columns, and the friction wedges against them.
            c0, c1 = span(e * win, e * (win + col))
            m.box("Truck", (x0, c0, seat - .02), (x1, c1, top - .12))
            w0, w1 = span(e * (win - .06), e * win)
            m.hexa("Truck", ((s * (j - .05), w0, spring_top), (s * (j + .05), w0, spring_top), (s * (j + .05), w1, spring_top - .02), (s * (j - .05), w1, spring_top - .02),
                             (s * (j - .05), w0, spring_top + .12), (s * (j + .05), w0, spring_top + .12), (s * (j + .05), w1, spring_top + .14), (s * (j - .05), w1, spring_top + .14)))
            # The pedestal legs either side of the bearing, and its adapter.
            y = e * a
            for l0, l1 in ((e * (a + jaw), e * (a + jaw + .09)), (e * inner, e * (a - jaw))):
                m.box("Truck", (x0, l0, ped - .16), (x1, l1, ped + .01))
            m.box("Truck", (s * (j - .1), y - jaw, ped - .05), (s * (j + .1), y + jaw, ped), .008)
            # The brake beam's guide, cast into the frame's inner face.
            g0, g1 = span(s * (j - .06), s * (j - .1))
            m.box("Truck", (g0, e * (a - r - .16), r - .1), (g1, e * (a - r - .02), r + .04))
        # The spring nest: a seat wider than the frame, and two rows of coils.
        m.box("Truck", (s * (j - .15), -win, seat - .03), (s * (j + .15), win, seat))
        for yy in (-.18, 0, .18):
            for xx in (j - .075, j + .075):
                m.helix("Spring", (s * xx, yy, seat), .056, .015, 5, spring_top - seat)
    # The bolster, fish-bellied, its ends out through the side frames on the springs.
    reach = j + .12
    m.prism("Truck", [(-reach, spring_top), (-.55, cp - .26), (.55, cp - .26), (reach, spring_top), (reach, cp - .02), (.35, cp), (-.35, cp), (-reach, cp - .02)],
            "y", -.22, .22)
    for e in (-1, 1):
        m.prism("Truck", [(-.55, cp - .26), (.55, cp - .26), (.35, cp - .03), (-.35, cp - .03)], "y", e * .22, e * .235)
    # The centre bowl and the side bearings.
    m.lathe("Steel", (0, 0, cp - .04), (0, 0, 1), [(0, .26), (.042, .26), (.042, .2), (.025, .2), (.025, .004), (0, .004)], 32)  # rim just proud of the bolster
    for s in (-1, 1):
        x = s * t.side_bearing
        m.box("Steel", (x - .075, -.1, cp - .01), (x + .075, .1, cp + .04), .006)
    # Brake beams ahead of and behind the bolster, their shoes on the treads, and the levers and rod between them.
    for e in (-1, 1):
        y = e * (a - r - .09)
        m.tube("Brake", [(-(j - .08), y, r - .02), (j - .08, y, r - .02)], .032, 0, 10)
        m.tube("Brake", [(-(j - .12), y, r - .02), (0, y + e * .14, r - .03), (j - .12, y, r - .02)], .016, .02, 8)
        m.box("Brake", (-.05, y, r - .07), (.05, y + e * .14, r + .02))
        for s in (-1, 1):
            # Head and shoe: an arc hugging the tread, on the side towards the bolster.
            pts = []
            for k in range(7):
                ang = math.radians(-24 + 48 * k / 6)
                pts.append((e * a - e * (r + .004) * math.cos(ang), r + (r + .004) * math.sin(ang)))
            for k in range(6, -1, -1):
                ang = math.radians(-24 + 48 * k / 6)
                pts.append((e * a - e * (r + .05) * math.cos(ang), r + (r + .05) * math.sin(ang)))
            h0, h1 = span(s * .705, s * .815)  # narrower than the head, and inside the rim's face at .82
            m.prism("Dark", pts, "x", h0, h1)
            m.box("Brake", (s * .70, y - e * .02, r - .1), (s * .82, e * a - e * (r + .04), r + .1))
        m.box("Brake", (.13, y - .02, r - .2), (.18, y + .02, r + .14))
    m.tube("Brake", [(.155, -(a - r - .09), r - .17), (.155, a - r - .09, r - .17)], .018, 0, 8)
    return m


def wheelset(t: TruckSpec):
    """Two wheels pressed on an axle 1.36 m back to back, with the bearings on the journals."""
    m = Kit()
    r, j = t.wheel_r, t.journal
    m.cyl("Steel", (-(j + .098), 0, 0), (j + .098, 0, 0), .085, 24)  # its ends inside the bearings', not level with them
    m.cyl("Steel", (-.62, 0, 0), (.62, 0, 0), .095, 24)
    for s in (-1, 1):
        web = [(.62, .095), (.62, .15), (.67, .2), (.695, r - .1), (.69, r - .055), (.80, r - .055), (.785, r - .1), (.745, .2), (.79, .15), (.79, .095)]
        m.lathe("Truck", (0, 0, 0), (s, 0, 0), web, 44)
        rim = [(.68, r - .06), (.68, r + .026), (.686, r + .03), (.697, r + .029), (.71, r + .008), (.725, r + .002), (.82, r - .006), (.82, r - .06)]
        m.lathe("Tread", (0, 0, 0), (s, 0, 0), rim, 64)
        # The bearing: cup, seals and the end cap with its three bolts, so that it's seen to turn.
        m.lathe("Bearing", (0, 0, 0), (s, 0, 0), [(j - .11, .09), (j - .11, .1), (j - .09, .112), (j + .08, .112), (j + .1, .1), (j + .1, .09)], 28)
        m.lathe("Steel", (0, 0, 0), (s, 0, 0), [(j + .102, .004), (j + .102, .09), (j + .125, .085), (j + .13, .004)], 24)  # off the cup's end
        for k in range(3):
            ang = 2 * math.pi * k / 3
            y, z = .05 * math.cos(ang), .05 * math.sin(ang)
            m.cyl("Dark", (s * (j + .125), y, z), (s * (j + .145), y, z), .016, 6)
        for k in range(4):
            ang = 2 * math.pi * k / 4 + .4
            y, z = .25 * math.cos(ang), .25 * math.sin(ang)
            m.cyl("Dark", (s * .74, y, z), (s * .765, y, z), .022, 8)
    return m

