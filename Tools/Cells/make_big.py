"""Tools/Cells/make_big.py N: a synthetic big route for the scale test of docs/cell-routes.md (Streaming everything): copies World to
Content/Routes/WorldBig (its small files, Network/ and Cells/ copied) and hard-links its tiles N times over (World's tiles at its own
place and N - 1 copies of them on a grid of offsets 100 tiles east and 50 north apart), with its ground cache. Nothing's written into
World. Remove with: rm -rf Content/Routes/WorldBig."""
import os, shutil, sys
n = int(sys.argv[1])
src = os.path.join('Content', 'Routes', 'World')
dst = os.path.join('Content', 'Routes', 'WorldBig')
os.makedirs(dst, exist_ok=True)
for f in os.listdir(src):
    p = os.path.join(src, f)
    if os.path.isfile(p):
        shutil.copy2(p, dst)
for d in ('Network', 'Cells'):
    if not os.path.exists(os.path.join(dst, d)):
        shutil.copytree(os.path.join(src, d), os.path.join(dst, d))
tiles = sorted(os.listdir(os.path.join(src, 'Tiles')))
ground = os.path.join(src, 'Cache', 'Ground')
os.makedirs(os.path.join(dst, 'Cache', 'Ground'), exist_ok=True)
for g in os.listdir(ground):
    t = os.path.join(dst, 'Cache', 'Ground', g)
    if g.endswith('.bin.gz') and not os.path.exists(t):
        os.link(os.path.join(ground, g), t)
made = 0
for k in range(n):
    di, dj = 100 * (k % 10), 50 * (k // 10)
    for name in tiles:
        i, j = map(int, name.split('_'))
        to = os.path.join(dst, 'Tiles', f'{i + di}_{j + dj}')
        if os.path.exists(os.path.join(to, 'Tile.json')):
            continue
        os.makedirs(to, exist_ok=True)
        for f in ('Heights.bin.gz', 'Cover.png', 'Tile.json'):
            os.link(os.path.join(src, 'Tiles', name, f), os.path.join(to, f))
        made += 1
print(f'{made} tiles linked, {len(os.listdir(os.path.join(dst, "Tiles")))} in WorldBig')
