"""Builds Highball's rolling stock in Blender and exports it for the game.

    blender -b --factory-startup --python Tools/Blender/rolling_stock.py -- <Content folder> [options]

    --car KEY        build only this car (again for more); all of them without it
    --preview DIR    render previews of each car into DIR
    --no-assembled   skip Assembled/ (the whole car as .glb and .blend, for looking at)
    --lod N          build a lower level of detail (1, 2) of the cars that have it, into <folder>/LODs/LOD<N>
    --list           print the cars and stop

Each car goes under Meshes/Vehicles/Legacy/<folder>: its body in each livery, its moving parts, truck, wheelset, coupler and
assembly.json. The car types live in Tools/Blender/rollingstock; catalog.py lists them.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from rollingstock import car as pipeline  # noqa: E402
from rollingstock.catalog import CARS  # noqa: E402

args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
content, picked, preview, assembled, lod = None, [], None, True, 0
i = 0
while i < len(args):
    a = args[i]
    if a == "--car":
        picked.append(args[i + 1])
        i += 1
    elif a == "--preview":
        preview = os.path.abspath(args[i + 1])
        i += 1
    elif a == "--lod":
        lod = int(args[i + 1])
        i += 1
    elif a == "--no-assembled":
        assembled = False
    elif a == "--list":
        for key, c in CARS.items():
            print(f"{key:16} {c.title:24} {', '.join(c.liveries)}")
        sys.exit(0)
    else:
        content = a
    i += 1
content = os.path.abspath(content or "Content")

unknown = [k for k in picked if k not in CARS]
if unknown:
    sys.exit(f"no car {', '.join(unknown)}; there are {', '.join(CARS)}")
for key in picked or [k for k, c in CARS.items() if lod in c.lods]:
    report = pipeline.build(CARS[key], content, preview, assembled, lod)
    print(key, sum(report.values()), "triangles over", len(report), "files")
