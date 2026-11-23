"""One-off migration of Content/Meshes into the standard layout (2026-11):

  Content/Meshes/Track/                       track pieces: ties, plates, switch kits, turnouts, the bridge kit
  Content/Meshes/Lineside/<Kind>/             what's placed beside the track: Signals, HandThrows, Crossings, Poles, Ties, RoadSigns
  Content/Meshes/Vehicles/Locomotives/<Model>/<Road>/, Vehicles/Freight/<Type>/, Vehicles/Couplers/, Vehicles/Legacy/
  Content/Meshes/Vegetation/, Content/Meshes/Characters/

An asset's folder holds what the game loads (and its vehicle.json, assembly.json, CREDITS.txt); what it's made from stays beside it
in Source/, which git and the build leave out. Downloads and unpacked third-party packs go to ContentSource/ at the repository root
(ignored). Tracked files are moved with git mv so their history follows. Every path that names a moved model (code, vehicle.json,
assembly.json, the Blender scripts) is rewritten. Run from the repository root:  python Tools/Content/migrate_meshes.py
"""
import os
import re
import shutil
import subprocess

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
MESHES = os.path.join(ROOT, "Content", "Meshes")
SOURCE = os.path.join(ROOT, "ContentSource")

# Old path (under Meshes) -> new path (under Meshes, or absolute for outside it).
MOVES = [
    ("Locomotives", "Vehicles/Locomotives"),
    ("Couplers", "Vehicles/Couplers"),
    ("RollingStockV2/Autoracks", "Vehicles/Freight/Autoracks"),
    ("RollingStockV2/BoxCars", "Vehicles/Freight/BoxCars"),
    ("RollingStockV2/Hoppers", "Vehicles/Freight/Hoppers"),
    ("RollingStockV2/WellCars", "Vehicles/Freight/WellCars"),
    ("RollingStockV2/Downloads", os.path.join(SOURCE, "Vehicles", "Freight", "Downloads")),
    ("RollingStockV2/_source (decompressed Trainz files)", os.path.join(SOURCE, "Vehicles", "Freight", "_source (decompressed Trainz files)")),
    ("RollingStockV2/_tools", os.path.join(ROOT, "Tools", "RollingStock")),
    ("RollingStock", "Vehicles/Legacy"),
    ("Track/Signals", "Lineside/Signals"),
    ("Track/signal.glb", "Lineside/Signals/signal.glb"),
    ("Track/hand_throw.glb", "Lineside/HandThrows/hand_throw.glb"),
    ("Track/crossing_mast.glb", "Lineside/Crossings/crossing_mast.glb"),
    ("Poles/Assembled", "Lineside/Poles/Source"),
    ("RoadSigns", "Lineside/RoadSigns"),
    ("CrossingSignals", os.path.join(SOURCE, "Lineside", "CrossingSignals")),
]
POLES = ["pole_telegraph", "pole_distribution", "pole_telecom", "light_cobra", "light_yard_mast", "light_platform"]
TIES = ["tie_single", "tie_section", "tie_section_ballast"]
MOVES += [(f"Poles/{p}.glb", f"Lineside/Poles/{p}.glb") for p in POLES]
MOVES += [(f"Poles/{t}.glb", f"Lineside/Ties/{t}.glb") for t in TIES]

# Text rewrites: (files, [(old, new), ...]). Paths relative to the repository root; globs allowed.
REWRITES = [
    (["Content/Meshes/Vehicles/Freight/*/vehicle.json", "Highball/Screens/LightingScreen.cs", "Tools/RollingStock/*.py"],
     [("RollingStockV2/_source", "ContentSource/Vehicles/Freight/_source"), ("RollingStockV2/Downloads", "ContentSource/Vehicles/Freight/Downloads"),
      ("RollingStockV2/_tools", "Tools/RollingStock"), ("RollingStockV2/", "Vehicles/Freight/")]),
    (["Content/Meshes/Vehicles/Locomotives/*/*/vehicle.json", "Highball/Screens/LightingScreen.cs"],
     [('"Locomotives/', '"Vehicles/Locomotives/')]),
    (["Content/Meshes/Vehicles/*/*/*/assembly.json", "Content/Meshes/Vehicles/*/*/assembly.json", "Tools/Blender/*.py", "Tools/Blender/rollingstock/*.py",
      "Highball/World/VehicleModel.cs", "RailWerkz.Sim/Trains/*.cs"],
     [('"Couplers/coupler.glb"', '"Vehicles/Couplers/coupler.glb"'), ("Meshes/Couplers/", "Meshes/Vehicles/Couplers/"),
      ("(Couplers/coupler.json", "(Vehicles/Couplers/coupler.json"), ("Vehicles: Couplers/coupler.json", "Vehicles: Vehicles/Couplers/coupler.json"),
      ('Path.Combine(root, "Couplers", "coupler.json")', 'Path.Combine(root, "Vehicles", "Couplers", "coupler.json")'),
      ('"Meshes", "Couplers"', '"Meshes", "Vehicles", "Couplers"'), ('"Meshes", "Locomotives"', '"Meshes", "Vehicles", "Locomotives"'),
      ('"Meshes", "RollingStock"', '"Meshes", "Vehicles", "Legacy"'), ("Meshes/RollingStock/", "Meshes/Vehicles/Legacy/")]),
    (["Tools/Blender/lods.py"], [('for kind in ("Locomotives", "RollingStock"):', 'for kind in ("Vehicles/Locomotives", "Vehicles/Freight", "Vehicles/Legacy"):')]),
    (["Tools/Blender/poles.py"],
     [('folder = os.path.join(CONTENT, "Meshes", "Poles")\nfor name, builder in UTILITY + LIGHTS + TIES:\n    model, _ = builder()\n    model.export(os.path.join(folder, f"{name}.glb"))',
       'for name, builder in UTILITY + LIGHTS + TIES:\n    model, _ = builder()\n    kind = "Ties" if name.startswith("tie_") else "Poles"\n'
       '    model.export(os.path.join(CONTENT, "Meshes", "Lineside", kind, f"{name}.glb"))')]),
    (["Tools/Blender/signals.py"], [('"Meshes", "Track", "Signals"', '"Meshes", "Lineside", "Signals"')]),
    (["Tools/Blender/lineside.py"],
     [("Writes Meshes/Track/signal.glb, hand_throw.glb and crossing_mast.glb under the Content folder.",
       "Writes Meshes/Lineside/Signals/signal.glb, HandThrows/hand_throw.glb and Crossings/crossing_mast.glb under the Content folder."),
      ('model.export(os.path.join(CONTENT, "Meshes", "Track", f"{name}.glb"))',
       'model.export(os.path.join(CONTENT, "Meshes", "Lineside", {"signal": "Signals", "hand_throw": "HandThrows", "crossing_mast": "Crossings"}[name], f"{name}.glb"))')]),
    (["Highball/World/NetworkMeshes.cs"],
     [('"Track/hand_throw.glb"', '"Lineside/HandThrows/hand_throw.glb"'), ('"Track/crossing_mast.glb"', '"Lineside/Crossings/crossing_mast.glb"'),
      ('Model.LoadParts($"Poles/{kind.PoleModel()}.glb")', 'Model.LoadParts($"Lineside/{(kind.IsTie() ? "Ties" : "Poles")}/{kind.PoleModel()}.glb")')]),
    (["Highball/World/NetworkMeshes.Signals.cs"], [('"Track/Signals/', '"Lineside/Signals/')]),
]


def tracked_files(path):
    rel = os.path.relpath(path, ROOT)
    out = subprocess.run(["git", "ls-files", "-z", "--", rel], cwd=ROOT, capture_output=True, text=True).stdout
    return {os.path.normcase(os.path.join(ROOT, f)) for f in out.split("\0") if f}


def move(src, dst):
    if not os.path.exists(src):
        print(f"  (no {os.path.relpath(src, ROOT)})")
        return
    tracked = tracked_files(src)
    files = [src] if os.path.isfile(src) else [os.path.join(d, f) for d, _, fs in os.walk(src) for f in fs]
    for f in files:
        if "__pycache__" in f:
            continue
        target = dst if os.path.isfile(src) else os.path.join(dst, os.path.relpath(f, src))
        os.makedirs(os.path.dirname(target), exist_ok=True)
        if os.path.normcase(f) in tracked:
            subprocess.run(["git", "mv", os.path.relpath(f, ROOT), os.path.relpath(target, ROOT)], cwd=ROOT, check=True)
        else:
            shutil.move(f, target)
    if os.path.isdir(src):
        shutil.rmtree(src, ignore_errors=True)
    print(f"  {os.path.relpath(src, ROOT)} -> {os.path.relpath(dst, ROOT)}")


def rewrite():
    import glob
    for patterns, pairs in REWRITES:
        files = sorted({f for p in patterns for f in glob.glob(os.path.join(ROOT, p))})
        for f in files:
            with open(f, encoding="utf-8") as h:
                text = h.read()
            new = text
            for old, rep in pairs:
                new = new.replace(old, rep)
            if new != text:
                with open(f, "w", encoding="utf-8", newline="") as h:
                    h.write(new)
                print(f"  rewrote {os.path.relpath(f, ROOT)}")


def tidy():
    for name in ("RollingStockV2", "Poles", "Track/Signals"):
        path = os.path.join(MESHES, name)
        if os.path.isdir(path) and not any(fs for _, _, fs in os.walk(path)):
            shutil.rmtree(path)
    left = os.path.join(MESHES, "RollingStockV2")
    if os.path.isdir(left):
        print("Left in RollingStockV2:", [os.path.relpath(os.path.join(d, f), left) for d, _, fs in os.walk(left) for f in fs][:20])


if __name__ == "__main__":
    for old, new in MOVES:
        move(os.path.join(MESHES, *old.split("/")), new if os.path.isabs(new) else os.path.join(MESHES, *new.split("/")))
    rewrite()
    tidy()
