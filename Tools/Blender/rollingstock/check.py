"""Checks on a built car: faces of different paints lying in one plane and overlapping, which flicker in the game (z-fighting).

Faces are bucketed by the plane they lie in (facing the same way, at the same offset, to the millimetre); within a bucket,
two of different paints whose bounds overlap by more than a couple of millimetres across the plane are reported. Bounds
are boxes, so two faces that only touch at an edge can be reported too; give such pieces a millimetre or two of clearance.
"""
from collections import defaultdict

import bpy


def coplanar(objects=None):
    """Pairs of paints with overlapping faces in one plane: {(paint, paint): (count, an example point)}."""
    buckets = defaultdict(list)
    for obj in objects or [o for o in bpy.data.objects if o.type == "MESH"]:
        mw = obj.matrix_world
        rot = mw.to_3x3()
        paint = obj.material_slots[0].material.name.split(".")[0] if obj.material_slots and obj.material_slots[0].material else obj.name
        verts = obj.data.vertices
        for p in obj.data.polygons:
            if p.area < 1e-5:
                continue
            n = (rot @ p.normal).normalized()
            pts = [mw @ verts[i].co for i in p.vertices]
            key = (round(n.x, 2), round(n.y, 2), round(n.z, 2), round(n.dot(mw @ p.center), 3))
            buckets[key].append((paint, [min(v[i] for v in pts) for i in range(3)], [max(v[i] for v in pts) for i in range(3)]))
    found = {}
    for faces in buckets.values():
        if len({f[0] for f in faces}) < 2:
            continue
        for i, a in enumerate(faces):
            for b in faces[i + 1:]:
                if a[0] == b[0]:
                    continue
                span = sorted(min(a[2][k], b[2][k]) - max(a[1][k], b[1][k]) for k in range(3))
                if span[0] > -1e-4 and span[1] > .002:
                    pair = tuple(sorted((a[0], b[0])))
                    count, at = found.get(pair, (0, [round(v, 3) for v in a[1]]))
                    found[pair] = (count + 1, at)
    return found


def report(name, objects=None):
    """Prints any overlaps in the scene as warnings, and returns how many faces overlap."""
    found = coplanar(objects)
    for (a, b), (count, at) in sorted(found.items(), key=lambda kv: -kv[1][0]):
        print(f"WARNING {name}: {count} faces of {a} and {b} overlap in one plane (they'll flicker), e.g. at {at}")
    return sum(count for count, _ in found.values())
