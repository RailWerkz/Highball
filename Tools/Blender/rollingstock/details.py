"""Safety appliances and end gear, shared by the car bodies: ladders, sill steps, grab irons, the hand brake, hoses, levers.

Each is given on an axis-aligned wall: a point on the wall, the unit direction across it, and the unit direction out of it.
"""
import math

from mathutils import Vector

from .kit import Kit


def _v(p):
    return Vector(p)


def bar(m: Kit, role, a, b, z0, z1):
    """A box between two points in plan, from z0 up to z1."""
    m.box(role, (a[0], a[1], z0), (b[0], b[1], z1))


def ladder(m: Kit, role, foot, across, out, width, rungs, stand=.13, top=None):
    """Two flat-bar stiles standing off a wall, with round rungs at the heights given.

    foot is on the wall below the first stile, at the lowest rung's height less a little."""
    foot, across, out = _v(foot), _v(across), _v(out)
    z0 = rungs[0] - .08
    z1 = (top if top is not None else rungs[-1] + .1)
    for k in (0, 1):
        p = foot + across * (width * k)
        lo, hi = p - across * .006 + out * (stand - .05), p + across * .006 + out * stand
        bar(m, role, lo, hi, z0, z1)
        for z in (z0 + .02, z1 - .02):
            lo, hi = p - across * .006, p + across * .006 + out * stand
            bar(m, role, lo, hi, z - .025, z + .025)
    for z in rungs:
        a = foot + out * (stand - .025)
        b = a + across * width
        m.cyl(role, (a.x, a.y, z), (b.x, b.y, z), .0125, 8)


def grab(m: Kit, role, a, b, out, stand=.065, r=.0125):
    """A grab iron: a rod from a to b on a wall, bent out to stand off it."""
    a, b, out = _v(a), _v(b), _v(out)
    m.tube(role, [a, a + out * stand, b + out * stand, b], r, .03, 8)


def sill_step(m: Kit, role, top, across, out, width, drop):
    """A stirrup hanging from the side sill, its tread drop below the sill."""
    top, across, out = _v(top), _v(across), _v(out)
    down = Vector((0, 0, -drop))
    a, b = top, top + across * width
    m.tube(role, [a, a + out * .07 + down, b + out * .07 + down, b], .013, .04, 8)
    lo = a + out * .0 + down
    hi = b + out * .14 + down + Vector((0, 0, .02))
    m.box(role, (lo.x, lo.y, lo.z - .004), (hi.x, hi.y, hi.z))


def hand_brake(m: Kit, x, face, e, wheel_z, step_z, step_x, frame_role="Frame"):
    """A geared hand brake on an end: housing, wheel facing out, release lever, chain down to the sill, and the brake step.

    face is the y of the wall it's bolted to, e the end (+1 or -1)."""
    y = lambda d: face + e * d
    m.box(frame_role, (x - .27, y(0), wheel_z - .32), (x + .28, y(.12), wheel_z + .24), .015)
    hub = (x, y(.24), wheel_z)
    m.cyl("Dark", (x, y(.12), wheel_z), hub, .03, 10)
    m.cyl("Dark", (x, y(.2), wheel_z), (x, y(.26), wheel_z), .05, 14)
    m.ring("Dark", hub, (0, e, 0), .27, .017, 6, 36)
    for k in range(3):
        t = 2 * math.pi * k / 3 + .3
        bend = t + .35
        m.tube("Dark", [(x + .04 * math.cos(t), y(.24), wheel_z + .04 * math.sin(t)),
                        (x + .16 * math.cos(bend - .15), y(.235), wheel_z + .16 * math.sin(bend - .15)),
                        (x + .265 * math.cos(bend), y(.23), wheel_z + .265 * math.sin(bend))], .012, .04, 6)
    m.tube("Steel", [(x + .24, y(.12), wheel_z - .22), (x + .24, y(.16), wheel_z - .26), (x + .42, y(.17), wheel_z - .3)], .011, .03, 6)
    # The chain down to the bell crank in the end sill.
    cx = x + .18
    m.tube("Dark", [(cx, y(.03), wheel_z - .32), (cx, y(.03), step_z - .2)], .01, 0, 6)
    # The step: a grating on two brackets.
    x0, x1 = step_x
    m.box("Grating", (x0, y(.04), step_z - .04), (x1, y(.07), step_z))
    m.box("Grating", (x0, y(.32), step_z - .04), (x1, y(.35), step_z))
    for xx in (x0, x1 - .03):
        m.box("Grating", (xx, y(.04), step_z - .04), (xx + .03, y(.35), step_z))
    n = int((x1 - x0) / .05)
    for k in range(1, n):
        xx = x0 + (x1 - x0) * k / n
        m.box("Grating", (xx - .005, y(.07), step_z - .03), (xx + .005, y(.32), step_z - .002))
    for xx in (x0 + .06, x1 - .06):
        m.tube("Grating", [(xx, y(.33), step_z - .04), (xx, y(.02), step_z - .3)], .014, 0, 6)


def air_hose(m: Kit, x, end, e, z):
    """The angle cock at the end of the train line and the hose hanging from it, with its glad hand."""
    y = lambda d: end + e * d
    m.cyl("Brake", (x, y(-.02), z), (x, y(.08), z), .03, 10)
    m.tube("Steel", [(x, y(.04), z + .03), (x + .02, y(.06), z + .1), (x + .1, y(.06), z + .12)], .009, .02, 6)
    m.tube("Rubber", [(x, y(.08), z), (x, y(.18), z - .06), (x, y(.28), z - .22), (x, y(.3), z - .3)], .022, .06, 10)
    m.box("Steel", (x - .03, y(.26), z - .36), (x + .05, y(.33), z - .3), .006)


def uncoupling_lever(m: Kit, handle_x, rod_x, end, e, z, rod_z, reach):
    """The lever across the end sill, from its handle at the corner to the crank under the coupler's lift rod."""
    y = lambda d: end + e * d
    s = 1 if rod_x > handle_x else -1
    m.tube("Brake", [(handle_x, y(.02), z + .06), (handle_x, y(.1), z), (rod_x - s * .14, y(.1), z), (rod_x - s * .04, y(reach - .04), (z + rod_z) / 2),
                     (rod_x, y(reach), rod_z)], .012, .05, 8)
    for f in (.2, .65):
        bx = handle_x + (rod_x - handle_x) * f
        m.box("Brake", (bx - .025, y(.003), z - .02), (bx + .025, y(.11), z + .02))  # just off the sill's face


def reflector(m: Kit, origin, u, v, w=.45, h=.1):
    """A strip of reflective sheeting: 4" by 18"."""
    m.panel("Reflector", origin, u, v, [(-w / 2, -h / 2), (w / 2, -h / 2), (w / 2, h / 2), (-w / 2, h / 2)], .0015)
