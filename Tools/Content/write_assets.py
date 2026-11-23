"""Writes (or refreshes) asset.json in each lineside asset folder under Content/Meshes/Lineside.

    python Tools/Content/write_assets.py

An asset.json says what's in the folder: its id, kind, display name, the models the game loads, and each model's paint slots
(its parts are named after a paint, Fixed_Black, Moving_Red: see TrackMaterials) with the material each slot is drawn with. A
slot's material starts as the shared paint of the same name; give an asset a material of its own by changing it here (the
Inspector will do that too). Values already set in an asset.json are kept when it's refreshed.
"""
import json
import os
import struct

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
LINESIDE = os.path.join(ROOT, "Content", "Meshes", "Lineside")

ASSETS = {
    "Signals": ("signals", "Signal", "Signals and signal bridges: masts, heads and the bridge kit."),
    "HandThrows": ("hand_throw", "Hand throw", "A switch stand worked by hand, in place of a power machine."),
    "Crossings": ("crossing_mast", "Grade crossing", "A grade crossing's masts, flashers and gates."),
    "Poles": ("poles", "Pole", "Utility and light poles."),
    "Ties": ("loose_ties", "Loose ties", "Ties and tie sections laid beside the track."),
    "RoadSigns": ("road_signs", "Road signs", "Road sign packs (not placed by the game yet)."),
}


def node_names(path):
    """The names of a .glb's nodes and meshes, from its JSON chunk."""
    with open(path, "rb") as f:
        magic, _, _ = struct.unpack("<4sII", f.read(12))
        if magic != b"glTF":
            return []
        length, kind = struct.unpack("<I4s", f.read(8))
        doc = json.loads(f.read(length))
    return [n.get("name", "") for n in doc.get("nodes", [])] + [m.get("name", "") for m in doc.get("meshes", [])]


def paints_of(path):
    """A model's paint slots: what follows the first underscore of a painted part's name (Fixed_Black: Black)."""
    slots = set()
    for name in node_names(path):
        if "_" in name:
            paint = name.split("_", 1)[1].split(".")[0]
            if paint and paint[0].isupper() and paint.replace("_", "").isalnum():
                slots.add(paint)
    return sorted(slots)


def main():
    for folder, (asset_id, name, about) in ASSETS.items():
        path = os.path.join(LINESIDE, folder)
        if not os.path.isdir(path):
            continue
        json_path = os.path.join(path, "asset.json")
        old = {}
        if os.path.exists(json_path):
            with open(json_path, encoding="utf-8") as f:
                old = json.load(f)
        old_models = {m["file"]: m for m in old.get("models", [])}
        models = []
        for file in sorted(os.listdir(path)):
            if not file.lower().endswith(".glb"):
                continue
            # (A pack made elsewhere doesn't name its parts after paints: it's drawn with its own materials.)
            slots = [] if folder == "RoadSigns" else paints_of(os.path.join(path, file))
            kept = old_models.get(file, {}).get("materials", {})
            models.append({"file": file, "materials": {s: kept.get(s, s) for s in slots}})
        asset = {
            "id": old.get("id", asset_id),
            "kind": "lineside",
            "name": old.get("name", name),
            "about": old.get("about", about),
            "models": models,
        }
        if os.path.exists(os.path.join(path, "CREDITS.txt")):
            asset["credits"] = "CREDITS.txt"
        with open(json_path, "w", encoding="utf-8", newline="\n") as f:
            json.dump(asset, f, indent=2)
            f.write("\n")
        print(f"{folder}: {len(models)} models, slots {sorted({s for m in models for s in m['materials']})}")


if __name__ == "__main__":
    main()
