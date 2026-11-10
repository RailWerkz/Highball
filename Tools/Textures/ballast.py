"""Makes Highball's own track ballast: crushed rock as a seamlessly tiling PBR texture, in ambientCG's layout.

    python Tools/Textures/ballast.py [Content folder] [--seed N] [--preview file.png]

Writes Textures/Terrain/Rocks/RailWerkzBallast_1K-JPG.zip and _2K-JPG.zip under the Content folder, each with
_Color, _NormalGL, _Roughness, _AmbientOcclusion and _Displacement maps. The game's ballast repeats every 1.5 m
(line.frag), so one tile is 1.5 m across: at 2K, 0.73 mm a pixel.

The stones are Voronoi cells on a torus (so the edges meet), which come out as the angular, flat-faced pieces crushed rock is:
three layers of them at different sizes, each stone with a random tilt to its top, its edges chipped by noise, the higher hiding the
lower. Dust settles in the gaps. Nothing is drawn that the eye could pick out as the same thing again a tile on, and line.frag
breaks up the tiling as well, so the repeat doesn't show.

Needs numpy and Pillow.
"""
import io
import os
import sys
import zipfile

import numpy as np
from PIL import Image

args = [a for a in sys.argv[1:]]
SEED = 11
PREVIEW = None
if "--seed" in args:
    i = args.index("--seed")
    SEED = int(args[i + 1])
    del args[i:i + 2]
if "--preview" in args:
    i = args.index("--preview")
    PREVIEW = args[i + 1]
    del args[i:i + 2]
CONTENT = os.path.abspath(args[0] if args else "Content")

N = 2048                 # pixels across one tile
TILE = 1.5               # metres across one tile
RELIEF = 0.045           # metres from the deepest gap to the highest stone
PIXEL = TILE / N
rng = np.random.default_rng(SEED)

ys, xs = np.mgrid[0:N, 0:N].astype(np.float32)
xs = (xs + 0.5) / N      # 0..1 across the tile
ys = (ys + 0.5) / N


def periodic_noise(cells, octaves=4, persistence=0.5):
    """Value noise that wraps at the tile's edges: random values on a grid of cells x cells, smoothly interpolated, in octaves."""
    total = np.zeros((N, N), np.float32)
    amplitude, weight = 1.0, 0.0
    for o in range(octaves):
        c = cells * 2 ** o
        grid = rng.random((c, c), dtype=np.float32)
        gx, gy = xs * c, ys * c
        x0, y0 = np.floor(gx).astype(int), np.floor(gy).astype(int)
        fx, fy = gx - x0, gy - y0
        fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
        x0, y0 = x0 % c, y0 % c
        x1, y1 = (x0 + 1) % c, (y0 + 1) % c
        top = grid[y0, x0] * (1 - fx) + grid[y0, x1] * fx
        bottom = grid[y1, x0] * (1 - fx) + grid[y1, x1] * fx
        total += (top * (1 - fy) + bottom * fy) * amplitude
        weight += amplitude
        amplitude *= persistence
    return total / weight


def voronoi(cells):
    """Each pixel's nearest and second nearest feature points on a torus of cells x cells, one jittered point a cell. Returns the
    nearest one's index, the distances to both (in cells), and the offset from the nearest point to the pixel (in cells)."""
    jitter = rng.random((cells, cells, 2), dtype=np.float32) * 0.8 + 0.1
    gx, gy = xs * cells, ys * cells
    ix, iy = np.floor(gx).astype(int), np.floor(gy).astype(int)
    f1 = np.full((N, N), 1e9, np.float32)
    f2 = np.full((N, N), 1e9, np.float32)
    owner = np.zeros((N, N), np.int32)
    ox = np.zeros((N, N), np.float32)
    oy = np.zeros((N, N), np.float32)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            cx, cy = ix + dx, iy + dy
            wx, wy = cx % cells, cy % cells
            px = cx + jitter[wy, wx, 0]
            py = cy + jitter[wy, wx, 1]
            ddx, ddy = gx - px, gy - py
            d = np.sqrt(ddx * ddx + ddy * ddy)
            nearer = d < f1
            f2 = np.where(nearer, f1, np.minimum(f2, d))
            f1 = np.where(nearer, d, f1)
            owner = np.where(nearer, wy * cells + wx, owner)
            ox = np.where(nearer, ddx, ox)
            oy = np.where(nearer, ddy, oy)
    return owner, f1, f2, ox, oy


def layer(cells, lift, chip, gap):
    """One layer of stones: each stone's height and which stone it is. lift raises the layer; chip is noise to break the edges; gap
    is how far in from its cell's edge a stone starts (in cells), the wider the more of the layer below shows between them."""
    owner, f1, f2, ox, oy = voronoi(cells)
    count = cells * cells
    # How far in from the stone's edge (in cells): the cells' edges are straight, so the stones have flat faces and sharp corners.
    edge = (f2 - f1) * 0.5 + (chip - 0.5) * 0.1 - gap
    # Rising to a ridge along the middle rather than a flat top: the faces of a broken stone meeting.
    size = np.clip(edge / 0.32, 0.0, 1.0)
    profile = size ** 0.55
    # Each stone broken into a few faces at different angles: a finer set of cells, each with a tilt of its own.
    facet, _, _, fx, fy = voronoi(int(cells * 2.6))
    facets = facet.max() + 1
    face_tilt = (rng.random((facets, 2), dtype=np.float32) - 0.5) * 0.5
    tilt = (rng.random((count, 2), dtype=np.float32) - 0.5) * 0.8
    height = rng.random(count, dtype=np.float32) * 0.35 + 0.65
    top = height[owner] + tilt[owner, 0] * ox + tilt[owner, 1] * oy + (face_tilt[facet, 0] * fx + face_tilt[facet, 1] * fy) * 0.22
    h = profile * top * 0.6 + lift
    h = np.where(edge > 0.0, h, 0.0)
    return h, None, owner


def main():
    print(f"Ballast: seed {SEED}, {N} px for {TILE} m")
    chip = periodic_noise(64, octaves=3)
    fine = periodic_noise(160, octaves=3)
    grain = periodic_noise(512, octaves=2)

    # Three sizes of stone, each a little higher than the one under it: what shows between the top stones is more stone, deeper down.
    layers = [layer(30, 0.0, chip, 0.0), layer(26, 0.14, np.roll(chip, 300, axis=0), 0.05), layer(22, 0.28, np.roll(chip, 700, axis=1), 0.09)]
    height = np.zeros((N, N), np.float32)
    stone = np.full((N, N), -1, np.int64)
    depth = np.zeros((N, N), np.int32)
    for k, (h, _, owner) in enumerate(layers):
        above = h > height
        height = np.where(above, h, height)
        stone = np.where(above, owner + k * 100000, stone)
        depth = np.where(above, k, depth)
    in_gap = stone < 0

    # The broken faces aren't flat: fine noise over every stone, coarser chips off some.
    height = height + (fine - 0.5) * 0.06 + (grain - 0.5) * 0.015
    height = np.where(in_gap, (fine * 0.08), height)
    height = (height - height.min()) / (height.max() - height.min())

    # Each stone's own colour: mostly grey granite and trap rock, some brown, rust stained or pale.
    ids = np.unique(stone[~in_gap])
    lookup = {}
    palette = [((0.50, 0.49, 0.47), 0.34), ((0.38, 0.38, 0.40), 0.24), ((0.66, 0.64, 0.61), 0.12), ((0.47, 0.41, 0.35), 0.12),
               ((0.27, 0.28, 0.30), 0.12), ((0.58, 0.46, 0.35), 0.06)]
    weights = np.array([w for _, w in palette])
    weights /= weights.sum()
    colors = np.zeros((len(ids), 3), np.float32)
    rough = np.zeros(len(ids), np.float32)
    for i in range(len(ids)):
        base = np.array(palette[rng.choice(len(palette), p=weights)][0], np.float32)
        colors[i] = base * (0.75 + 0.5 * rng.random()) + (rng.random(3) - 0.5) * 0.03
        rough[i] = 0.68 + 0.22 * rng.random()
    index = np.searchsorted(ids, np.where(in_gap, ids[0], stone))
    color = colors[index]
    roughness = rough[index]

    # Granite's grain: light and dark crystals, and the faces mottled.
    speck = periodic_noise(700, octaves=1)
    crystals = np.clip((speck - 0.62) * 6.0, 0.0, 1.0)[..., None] * 0.18 - np.clip((0.3 - speck) * 6.0, 0.0, 1.0)[..., None] * 0.15
    color = color * (0.9 + 0.2 * fine[..., None]) * (1.0 + crystals)

    # Ambient occlusion from how far each pixel is under what's round it (the FFT's blur wraps, as the tile does).
    def blur(img, sigma):
        f = np.fft.fftfreq(N)
        g = np.exp(-2.0 * (np.pi * sigma) ** 2 * (f[:, None] ** 2 + f[None, :] ** 2))
        return np.real(np.fft.ifft2(np.fft.fft2(img) * g)).astype(np.float32)

    cover = blur(height, 10) * 0.5 + blur(height, 28) * 0.5
    occlusion = np.clip(1.0 - np.maximum(cover - height, 0.0) * 3.4, 0.0, 1.0) ** 1.3

    # Dust in the gaps and low down: paler and browner, matt.
    low = np.clip((0.42 - height) / 0.3, 0.0, 1.0)
    dust = np.array([0.50, 0.46, 0.40], np.float32) * (0.85 + 0.3 * fine[..., None])
    settle = np.clip(low * 0.5 + in_gap * 0.4, 0.0, 1.0)[..., None]
    color = color * (1 - settle) + dust * settle
    color *= (0.3 + 0.7 * occlusion)[..., None]
    roughness = np.clip(roughness * (1 - settle[..., 0]) + 0.95 * settle[..., 0] + (fine - 0.5) * 0.08, 0.0, 1.0)

    # The normal from the height's slope, in OpenGL's way up (green up the image).
    scale = RELIEF / PIXEL
    dhx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * 0.5 * scale
    dhy = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * 0.5 * scale
    normal = np.stack([-dhx, dhy, np.ones_like(height)], axis=-1)
    normal /= np.linalg.norm(normal, axis=-1, keepdims=True)

    maps = {
        "Color": to_srgb(np.clip(color, 0.0, 1.0)),
        "NormalGL": normal * 0.5 + 0.5,
        "Roughness": roughness,
        "AmbientOcclusion": occlusion,
        "Displacement": height,
    }
    out = os.path.join(CONTENT, "Textures", "Terrain", "Rocks")
    os.makedirs(out, exist_ok=True)
    for label, size in (("2K", 2048), ("1K", 1024)):
        name = f"RailWerkzBallast_{label}-JPG"
        path = os.path.join(out, name + ".zip")
        with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as z:
            for key, data in maps.items():
                image = Image.fromarray((np.clip(data, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8))
                if size != N:
                    image = image.resize((size, size), Image.LANCZOS)
                buffer = io.BytesIO()
                image.save(buffer, "JPEG", quality=94)
                z.writestr(f"{name}_{key}.jpg", buffer.getvalue())
        print("wrote", path)

    if PREVIEW:
        # Lit by a low sun from the upper left, three tiles by two, to judge the stones and the seams.
        light = np.array([-0.5, 0.5, 0.7], np.float32)
        light /= np.linalg.norm(light)
        shade = np.clip((normal * light).sum(-1), 0.0, 1.0) * 0.85 + 0.15
        lit = to_srgb(np.clip(color * shade[..., None] * occlusion[..., None] * 1.2, 0.0, 1.0))
        small = Image.fromarray((lit * 255).astype(np.uint8)).resize((768, 768), Image.LANCZOS)
        sheet = Image.new("RGB", (768 * 3, 768 * 2))
        for tx in range(3):
            for ty in range(2):
                sheet.paste(small, (tx * 768, ty * 768))
        sheet.save(PREVIEW)
        Image.fromarray((lit * 255).astype(np.uint8)).crop((0, 0, 900, 600)).save(PREVIEW.replace(".png", "_close.png"))
        print("preview", PREVIEW)


def to_srgb(linear):
    return np.where(linear <= 0.0031308, linear * 12.92, 1.055 * np.power(linear, 1 / 2.4) - 0.055)


main()
