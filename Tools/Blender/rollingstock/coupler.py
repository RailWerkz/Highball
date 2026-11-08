"""The one coupler every engine and car uses: the SD40's, written to Models/Couplers by Tools/Blender/sd40.py.

Its origin is the draft pivot, swinging about Z, pulling face towards -Y. coupler.json gives its height above the railhead,
which is every car's coupler height, and its reach from the pivot to the pulling face.
"""
import json
import os
from dataclasses import dataclass

MODEL = "Couplers/coupler.glb"  # under Content/Models, as the assemblies name it


@dataclass(frozen=True)
class Coupler:
    path: str  # the .glb on disk
    height: float
    reach: float


def load(content):
    folder = os.path.join(content, "Models", "Couplers")
    try:
        with open(os.path.join(folder, "coupler.json")) as f:
            info = json.load(f)
    except FileNotFoundError:
        raise SystemExit(f"no shared coupler in {folder}: build the SD40 first (Tools/Blender/sd40.py), which writes it")
    return Coupler(os.path.join(folder, info["model"]), info["height"], info["reach"])
