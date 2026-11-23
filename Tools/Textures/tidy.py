"""Turns downloaded PBR textures into materials in Content/Materials, the layout the game reads, and audits what's there.

    python Tools/Textures/tidy.py [--dry-run] [--force] [--content DIR] [--sources DIR] [--blender PATH] [zip ...]

Drop texture downloads (zips from Poly Haven, ambientCG, or anywhere else) into a category's folder under Content/Materials
(Content/Materials/Wood/oak_planks_2k.zip) and run this. Each zip becomes, or adds a size to, a material:

    Content/Materials/<Category>/<Name>/material.json      its name, category, sizes, and where it came from
    Content/Materials/<Category>/<Name>/<Size>/albedo, normal, roughness, ao, height (and metalness if it has any metal)

which is what the Track Materials window lists and TrackMaterials.Decode reads. The original zip is moved to the sources folder
(default: TextureSources beside the Highball folder, same subfolders), so nothing's lost. Then every material is audited: missing
maps, and which sizes it comes in.

What it handles:
  - maps named by any of the usual words (diff/col/albedo/basecolor/color, nor_gl/normal/nrm, nor_dx, rough, ao, disp/height,
    metal), with the size anywhere in the name (_1k, _2K, -4K-JPG...) and in any folder inside the zip;
  - EXR maps (Poly Haven's normal and roughness): read through Blender and written as 8-bit PNG. A normal map's encoding is
    worked out from the map itself: whichever reading leaves the normals pointing straight up on average, as a tiling map's do;
  - packed ARM maps (ambient occlusion, roughness, metalness in red, green, blue) for any of those three that's missing;
  - DirectX normal maps (green flipped) when there's no OpenGL one, kept as normal_dx: the game flips them itself;
  - the size taken from the name, or else from the colour map's width. Materials come in 1K and 2K: anything larger is scaled
    down to 2K (its normal map renormalised).

Given zips, only those are tidied (the audit still covers everything). --dry-run says what it would do and changes nothing;
--force replaces a size a material already has.

Needs numpy and Pillow, and Blender for EXR maps (found under Program Files\\Blender Foundation, or --blender, or $BLENDER).
"""
import glob
import io
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
HIGHBALL = os.path.dirname(os.path.dirname(HERE))
SIZES = ["1K", "2K"]  # what a material keeps (Content/Materials/<Category>/<Name>/<Size>/)
LARGEST = 2048
SIZE_TOKEN = re.compile(r"(?<![a-z0-9])(1|2|4|8|16)k(?:-(?:jpg|png|exr))?(?![a-z0-9])", re.IGNORECASE)
IMAGE_EXT = (".jpg", ".jpeg", ".png", ".tga", ".bmp", ".exr", ".tif", ".tiff")

# The words a map's file name ends with (after its size is taken out), and the map each means.
WORDS = [
    (("nor", "gl"), "normalgl"), (("normal", "gl"), "normalgl"), (("normalgl",), "normalgl"),
    (("nor", "dx"), "normaldx"), (("normal", "dx"), "normaldx"), (("normaldx",), "normaldx"),
    (("diff",), "color"), (("col",), "color"), (("color",), "color"), (("albedo",), "color"), (("basecolor",), "color"),
    (("base", "color"), "color"), (("diffuse",), "color"),
    (("nor",), "normalgl"), (("normal",), "normalgl"), (("nrm",), "normalgl"),
    (("rough",), "roughness"), (("roughness",), "roughness"), (("rgh",), "roughness"),
    (("ao",), "ao"), (("ambientocclusion",), "ao"), (("occlusion",), "ao"),
    (("disp",), "height"), (("displacement",), "height"), (("height",), "height"),
    (("metal",), "metalness"), (("metallic",), "metalness"), (("metalness",), "metalness"),
    (("arm",), "arm"),
]
OUT_NAMES = {"color": "albedo", "normalgl": "normal", "normaldx": "normal_dx", "roughness": "roughness", "ao": "ao",
             "height": "height", "metalness": "metalness"}


def args_of(argv):
    opts = {"dry": "--dry-run" in argv, "force": "--force" in argv, "content": os.path.join(HIGHBALL, "Content"),
            "sources": os.path.join(os.path.dirname(HIGHBALL), "TextureSources"), "blender": os.environ.get("BLENDER")}
    rest = list(argv)
    for flag in ("--content", "--sources", "--blender"):
        if flag in rest:
            i = rest.index(flag)
            opts[flag[2:]] = rest[i + 1]
            del rest[i:i + 2]
    opts["only"] = {os.path.normcase(os.path.abspath(a)) for a in rest if not a.startswith("--")}
    return opts


def find_blender(given):
    if given and os.path.exists(given):
        return given
    found = sorted(glob.glob(r"C:\Program Files\Blender Foundation\*\blender.exe"))
    return found[-1] if found else shutil.which("blender")


def key_of(filename):
    """The map a file is ("color", "normalgl", ...), or None."""
    stem, ext = os.path.splitext(os.path.basename(filename))
    if ext.lower() not in IMAGE_EXT:
        return None
    words = [w for w in re.split(r"[_\-\s.]+", SIZE_TOKEN.sub("", stem).lower()) if w and w not in ("jpg", "png", "exr")]
    for tail, key in WORDS:
        if len(words) >= len(tail) and tuple(words[-len(tail):]) == tail:
            return key
    return None


def base_name(zipname):
    stem = os.path.splitext(os.path.basename(zipname))[0]
    stem = SIZE_TOKEN.sub("", stem)
    return re.sub(r"[_\-\s]+$", "", re.sub(r"[_\-\s]{2,}", "_", stem)).strip("_- ")


def size_of(zipname, color_width):
    """The size a download comes in, in pixels: from its name, or else its colour map's width."""
    m = SIZE_TOKEN.search(os.path.basename(zipname))
    if m:
        return int(m.group(1)) * 1024
    return color_width


def srgb_to_linear(a):
    return np.where(a <= 0.04045, a / 12.92, ((np.maximum(a, 0) + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(a):
    a = np.maximum(a, 0)
    return np.where(a <= 0.0031308, a * 12.92, 1.055 * a ** (1 / 2.4) - 0.055)


def normal_reading(rgb):
    """Of the ways an EXR normal map might be encoded, the one whose normals point straight up on average and are unit length."""
    best = None
    for label, f in (("as stored", lambda a: a), ("sRGB-encoded", srgb_to_linear), ("linear", linear_to_srgb)):
        c = np.clip(f(rgb), 0.0, 1.0)
        n = c * 2.0 - 1.0
        tilt = np.abs(n[..., :2].reshape(-1, 2).mean(0)).sum()
        length = np.abs(np.sqrt((n ** 2).sum(-1)).mean() - 1.0)
        score = tilt + length
        if best is None or score < best[0]:
            best = (score, label, c)
    return best[1], best[2]


EXR_DUMP = r'''
import sys, bpy, numpy as np
for path in sys.argv[sys.argv.index("--") + 1:]:
    img = bpy.data.images.load(path)
    img.colorspace_settings.name = "Non-Color"
    w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    np.save(path + ".npy", px.reshape(h, w, 4)[::-1])
'''


def read_exrs(paths, blender, work):
    if not paths:
        return {}
    if not blender:
        raise RuntimeError("EXR maps need Blender: pass --blender PATH")
    script = os.path.join(work, "exr_dump.py")
    with open(script, "w") as f:
        f.write(EXR_DUMP)
    subprocess.run([blender, "-b", "--factory-startup", "--python", script, "--", *paths], check=True,
                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return {p: np.load(p + ".npy").astype(np.float64) for p in paths}


def png_bytes(array, mode):
    b = io.BytesIO()
    Image.fromarray((np.clip(array, 0, 1) * 255.0 + 0.5).astype(np.uint8), mode).save(b, "PNG")
    return b.getvalue()


def shrunk(data, suffix):
    """A map scaled down to the game's largest size: a normal map renormalised after, JPEGs kept as JPEGs."""
    img = Image.open(io.BytesIO(data))
    if img.size[0] <= LARGEST:
        return data
    img = img.resize((LARGEST, round(img.size[1] * LARGEST / img.size[0])), Image.LANCZOS)
    if suffix.startswith("normal"):
        n = np.asarray(img.convert("RGB")).astype(np.float64) / 255.0 * 2.0 - 1.0
        n /= np.maximum(np.sqrt((n ** 2).sum(-1, keepdims=True)), 1e-6)
        img = Image.fromarray(((n + 1.0) * 0.5 * 255.0 + 0.5).clip(0, 255).astype(np.uint8), "RGB")
    b = io.BytesIO()
    if suffix.endswith(".jpg"):
        img.convert("RGB" if img.mode not in ("L", "RGB") else img.mode).save(b, "JPEG", quality=95)
    else:
        img.save(b, "PNG")
    return b.getvalue()


def tidy_zip(path, content_textures, opts, blender, report):
    rel_folder = os.path.relpath(os.path.dirname(path), content_textures)
    if rel_folder in (".", ""):
        report.append(f"  SKIPPED  {os.path.basename(path)}: put it in a category's folder (Content/Materials/Wood/...) first")
        return
    z = zipfile.ZipFile(path)
    maps = {}
    for info in z.infolist():
        if info.is_dir():
            continue
        key = key_of(info.filename)
        if key and key not in maps:
            maps[key] = info.filename
    if "color" not in maps:
        report.append(f"  SKIPPED  {os.path.relpath(path, content_textures)}: no colour map (found: {', '.join(sorted(maps)) or 'nothing'})")
        return
    color_width = Image.open(io.BytesIO(z.read(maps["color"]))).size[0] if not maps["color"].lower().endswith(".exr") else None
    pixels = size_of(path, color_width) or LARGEST
    # Larger than a material keeps: scaled down to its largest.
    shrink = pixels > LARGEST
    size = SIZES[-1] if shrink else min(SIZES, key=lambda s: abs(int(s[0]) * 1024 - pixels))
    name = base_name(path)
    material = os.path.join(os.path.dirname(path), name)
    out = os.path.join(material, size)
    if os.path.isdir(out) and not opts["force"]:
        report.append(f"  SKIPPED  {os.path.relpath(path, content_textures)}: {name} already has {size} (--force replaces it)")
        return

    notes = []
    with tempfile.TemporaryDirectory() as work:
        exrs = []
        for key, member in maps.items():
            if member.lower().endswith(".exr"):
                target = os.path.join(work, f"{key}.exr")
                with open(target, "wb") as f:
                    f.write(z.read(member))
                exrs.append(target)
        decoded = read_exrs(exrs, blender, work) if not opts["dry"] else {}

        files = {}
        for key, member in maps.items():
            if key == "arm":
                continue
            ext = os.path.splitext(member)[1].lower()
            if ext in (".jpg", ".jpeg", ".png"):
                files[OUT_NAMES[key] + (".jpg" if ext in (".jpg", ".jpeg") else ".png")] = z.read(member)
                continue
            if ext == ".exr":
                if opts["dry"]:
                    files[OUT_NAMES[key] + ".png"] = b""
                    notes.append(f"{key} from EXR")
                    continue
                a = decoded[os.path.join(work, f"{key}.exr")]
                if key in ("normalgl", "normaldx"):
                    how, rgb = normal_reading(a[..., :3])
                    files[OUT_NAMES[key] + ".png"] = png_bytes(rgb, "RGB")
                    notes.append(f"{key} from EXR ({how})")
                elif key == "color":
                    files["albedo.png"] = png_bytes(linear_to_srgb(a[..., :3]), "RGB")
                    notes.append("color from EXR (linear, to sRGB)")
                else:
                    files[OUT_NAMES[key] + ".png"] = png_bytes(a[..., 0], "L")
                    notes.append(f"{key} from EXR")
                continue
            # TGA, BMP, TIFF: as PNG.
            img = Image.open(io.BytesIO(z.read(member)))
            b = io.BytesIO()
            img.save(b, "PNG")
            files[OUT_NAMES[key] + ".png"] = b.getvalue()
            notes.append(f"{key} from {ext[1:].upper()}")

        # A packed ARM map fills in whatever of occlusion, roughness and metal isn't there on its own.
        if "arm" in maps:
            arm = np.asarray(Image.open(io.BytesIO(z.read(maps["arm"]))).convert("RGB")).astype(np.float64) / 255.0
            have = {k for k in maps}
            if "ao" not in have:
                files["ao.png"] = png_bytes(arm[..., 0], "L")
                notes.append("ao from ARM")
            if "roughness" not in have:
                files["roughness.png"] = png_bytes(arm[..., 1], "L")
                notes.append("roughness from ARM")
            # (Only where there's metal: a JPEG's noise leaves the odd speck in the blue of one that has none.)
            if "metalness" not in have and arm[..., 2].mean() > 0.05:
                files["metalness.png"] = png_bytes(arm[..., 2], "L")
                notes.append("metalness from ARM")

        if shrink:
            notes.append(f"{pixels // 1024}K scaled down to {SIZES[-1]}")
            if not opts["dry"]:
                for suffix in list(files):
                    files[suffix] = shrunk(files[suffix], suffix)

        missing = [m for m in ("normal", "roughness", "ao", "height")
                   if not any(f.split(".")[0] == m for f in files) and not (m == "normal" and any(f.startswith("normal_dx") for f in files))]
        verb = "WOULD MAKE" if opts["dry"] else "MADE"
        line = f"  {verb}  {os.path.join(rel_folder, name, size)}  from {os.path.basename(path)}"
        if notes:
            line += f"  [{'; '.join(notes)}]"
        if missing:
            line += f"  (no {', '.join(missing)})"
        report.append(line)
        if opts["dry"]:
            return

        if os.path.isdir(out):
            shutil.rmtree(out)
        os.makedirs(out)
        for file, data in files.items():
            with open(os.path.join(out, file), "wb") as o:
                o.write(data)
        write_material(material, name, rel_folder.replace(os.sep, "/"), size, os.path.basename(path))
    z.close()

    keep = os.path.join(opts["sources"], rel_folder)
    os.makedirs(keep, exist_ok=True)
    shutil.move(path, os.path.join(keep, os.path.basename(path)))


def write_material(folder, name, category, size, source):
    """Adds a size to a material's material.json (making it if it's new)."""
    path = os.path.join(folder, "material.json")
    info = {"name": name, "category": category, "sizes": [], "source": None, "license": None}
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            info.update(json.load(f))
    info["sizes"] = sorted(set(info.get("sizes") or []) | {size}, key=SIZES.index)
    info.setdefault("from", source)
    with open(path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(info, f, indent=2)
        f.write("\n")


def audit(content_materials, report):
    materials = []
    for root, dirs, files in os.walk(content_materials):
        if "material.json" in files:
            materials.append(root)
            dirs[:] = []
    report.append("")
    report.append(f"Materials: {len(materials)}")
    for root in sorted(materials, key=str.lower):
        rel = os.path.relpath(root, content_materials).replace(os.sep, "/")
        sizes = [s for s in SIZES if os.path.isdir(os.path.join(root, s))]
        problems = []
        if not sizes:
            problems.append("no size folders (1K/, 2K/)")
        for size in sizes:
            keys = {os.path.splitext(f)[0] for f in os.listdir(os.path.join(root, size))}
            for need in ("albedo", "normal", "roughness"):
                if need not in keys and not (need == "normal" and "normal_dx" in keys):
                    problems.append(f"{size} has no {need} map")
        line = f"  {rel:<40} {' '.join(sizes)}"
        if problems:
            line += "   !! " + "; ".join(problems)
        report.append(line)


def main():
    opts = args_of(sys.argv[1:])
    content_textures = os.path.join(opts["content"], "Materials")
    if not os.path.isdir(content_textures):
        sys.exit(f"No Materials folder under {opts['content']}")
    blender = find_blender(opts["blender"])
    report = [f"Material tidy{' (dry run)' if opts['dry'] else ''}: {content_textures}"]
    todo = []
    for root, dirs, files in os.walk(content_textures):
        for f in files:
            path = os.path.join(root, f)
            if f.lower().endswith(".zip") and (not opts["only"] or os.path.normcase(os.path.abspath(path)) in opts["only"]):
                todo.append(path)
    report.append(f"To tidy: {len(todo)}")
    for path in sorted(todo):
        try:
            tidy_zip(path, content_textures, opts, blender, report)
        except Exception as e:  # one bad download shouldn't stop the rest
            report.append(f"  FAILED   {os.path.relpath(path, content_textures)}: {e}")
    audit(content_textures, report)
    print("\n".join(report))


if __name__ == "__main__":
    main()
