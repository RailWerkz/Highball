"""One-off migration of Content/Textures into the standard layout (2026-11):

  Content/Materials/<Category>/<Name>/material.json, preview.png, 1K/, 2K/   PBR surfaces, unpacked from ambientCG-style zips
  Content/Terrain/<Layer>/, Content/Terrain/Clutter/, Content/Terrain/Modifiers/
  Content/Sky/Skyboxes/, Content/Sky/Planets/
  Content/Presets/Track.json                                                (was Content/Materials/Track.json)

Maps inside a size folder are named albedo, normal (OpenGL's way up; normal_dx when only DirectX's came), roughness, metalness,
ao and height, keeping their file type. Terrain/Rocks becomes the Ground category. Tracked files are moved with git mv so
their history follows; the zips are removed once unpacked. Run from the repository root:  python Tools/Content/migrate_textures.py
"""
import json
import os
import re
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CONTENT = os.path.join(ROOT, "Content")
TEXTURES = os.path.join(CONTENT, "Textures")
SIZES = ("1K", "2K")

# Source folder under Textures -> category under Materials.
CATEGORIES = {"Asphalt": "Asphalt", "Concrete": "Concrete", "Metal": "Metal", "Wood": "Wood", "Terrain/Rocks": "Ground"}

# The last word of a map's name -> its standard name.
MAPS = {
    "color": "albedo", "albedo": "albedo", "basecolor": "albedo", "diffuse": "albedo",
    "normalgl": "normal", "normaldx": "normal_dx", "normal": "normal",
    "roughness": "roughness", "metalness": "metalness", "metallic": "metalness",
    "ambientocclusion": "ao", "ao": "ao", "occlusion": "ao",
    "displacement": "height", "height": "height",
}

ZIP_NAME = re.compile(r"^(?P<name>.+)_(?P<size>\d+K)-(?P<kind>JPG|PNG)\.zip$", re.IGNORECASE)


def tracked(path):
    rel = os.path.relpath(path, ROOT)
    return subprocess.run(["git", "ls-files", "--error-unmatch", rel], cwd=ROOT, capture_output=True).returncode == 0


def move(src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    if tracked(src):
        subprocess.run(["git", "mv", os.path.relpath(src, ROOT), os.path.relpath(dst, ROOT)], cwd=ROOT, check=True)
    else:
        shutil.move(src, dst)


def remove(path):
    if tracked(path):
        subprocess.run(["git", "rm", "-q", os.path.relpath(path, ROOT)], cwd=ROOT, check=True)
    else:
        os.remove(path)


def unpack_materials():
    materials = {}
    for folder, category in CATEGORIES.items():
        src = os.path.join(TEXTURES, *folder.split("/"))
        if not os.path.isdir(src):
            continue
        for file in sorted(os.listdir(src)):
            m = ZIP_NAME.match(file)
            if not m:
                continue
            name, size = m.group("name"), m.group("size").upper()
            zpath = os.path.join(src, file)
            entry = materials.setdefault((category, name), {"sizes": set(), "zips": [], "ambientcg": False})
            entry["zips"].append(zpath)
            if size not in SIZES:
                continue
            dest = os.path.join(CONTENT, "Materials", category, name)
            with zipfile.ZipFile(zpath) as z:
                for info in z.infolist():
                    base = os.path.basename(info.filename)
                    stem, ext = os.path.splitext(base)
                    ext = ext.lower()
                    if ext in (".usdc", ".mtlx", ".blend", ".tres"):
                        entry["ambientcg"] = True
                        continue
                    if ext not in (".jpg", ".jpeg", ".png"):
                        continue
                    word = re.split(r"[_\-\s.]", stem.lower())[-1]
                    key = MAPS.get(word)
                    if key is None:
                        # ambientCG's preview: the texture's name alone, beside the maps.
                        if stem.lower() == name.lower() and not os.path.exists(os.path.join(dest, "preview.png")):
                            os.makedirs(dest, exist_ok=True)
                            with open(os.path.join(dest, "preview" + ext), "wb") as out:
                                out.write(z.read(info))
                        continue
                    os.makedirs(os.path.join(dest, size), exist_ok=True)
                    with open(os.path.join(dest, size, key + ext), "wb") as out:
                        out.write(z.read(info))
            entry["sizes"].add(size)

    for (category, name), entry in materials.items():
        dest = os.path.join(CONTENT, "Materials", category, name)
        if entry["sizes"]:
            info = {
                "name": name,
                "category": category,
                "sizes": sorted(entry["sizes"]),
                "source": "ambientCG" if entry["ambientcg"] else None,
                "license": "CC0" if entry["ambientcg"] else None,
            }
            with open(os.path.join(dest, "material.json"), "w", encoding="utf-8", newline="\n") as out:
                json.dump(info, out, indent=2)
                out.write("\n")
            for zpath in entry["zips"]:
                remove(zpath)
        else:
            print(f"  {category}/{name}: no 1K or 2K size; its zips are left where they are", file=sys.stderr)
    print(f"Materials: {sum(1 for e in materials.values() if e['sizes'])} unpacked")


def move_tree(src, dst):
    if not os.path.isdir(src):
        return
    for dirpath, _, files in os.walk(src):
        for file in files:
            path = os.path.join(dirpath, file)
            move(path, os.path.join(dst, os.path.relpath(path, src)))


def move_rest():
    terrain = os.path.join(TEXTURES, "Terrain")
    for layer in sorted(os.listdir(terrain)) if os.path.isdir(terrain) else []:
        if layer == "Rocks":
            continue
        target = {"GroundClutter": "Clutter"}.get(layer, layer)
        move_tree(os.path.join(terrain, layer), os.path.join(CONTENT, "Terrain", target))
    move_tree(os.path.join(TEXTURES, "Skyboxes"), os.path.join(CONTENT, "Sky", "Skyboxes"))
    move_tree(os.path.join(TEXTURES, "Planets"), os.path.join(CONTENT, "Sky", "Planets"))
    track = os.path.join(CONTENT, "Materials", "Track.json")
    if os.path.exists(track):
        move(track, os.path.join(CONTENT, "Presets", "Track.json"))


def tidy():
    # Folders left empty, and the old Textures folder if nothing's left in it.
    for dirpath, dirs, files in os.walk(TEXTURES, topdown=False):
        if not os.listdir(dirpath):
            os.rmdir(dirpath)
    if os.path.isdir(TEXTURES):
        left = [os.path.relpath(os.path.join(d, f), TEXTURES) for d, _, fs in os.walk(TEXTURES) for f in fs]
        print(f"Left in Content/Textures: {left}")


if __name__ == "__main__":
    move_rest()
    unpack_materials()
    tidy()
