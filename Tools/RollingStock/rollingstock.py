"""Assemble a Trainz traincar (body LOD 1 + extra meshes + trucks at a.bog*) into one .glb.
Usage: python -I rollingstock.py <carDir> <truckDir> <out.glb> [skin map1.texture.txt]"""
import sys, os, re
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import im2glb

car, truck, out = sys.argv[1:4]
if len(sys.argv) > 4:
    im2glb.TEX_OVERRIDES['map1'] = sys.argv[4]

def find(folder, rel):
    """Case-insensitive path lookup."""
    cur = folder
    for part in re.split(r'[\\/]', rel):
        if not part: continue
        m = {f.lower(): f for f in os.listdir(cur)}
        if part.lower() not in m: return None
        cur = os.path.join(cur, m[part.lower()])
    return cur

def best_lod(lm_path):
    txt = open(lm_path, encoding='latin1').read()
    entries = re.findall(r'mesh\("([\d.]+)"\)\s*\{\s*name="([^"]+)"', txt)
    return max(entries, key=lambda e: float(e[0]))[1]

def mesh_table(cfg):
    """Parse mesh-table entries -> list of (name, mesh path)."""
    lines = open(cfg, encoding='utf-8').read().splitlines()
    res, i = [], lines.index('mesh-table {') + 1
    depth, cur = 1, None
    while depth > 0:
        s = lines[i].strip(); i += 1
        if s.endswith('{'):
            depth += 1
            if depth == 2: cur = s[:-1].strip(); mesh = None; asset = False
        elif s == '}':
            if depth == 2 and mesh and not asset: res.append((cur, mesh))
            depth -= 1
        elif depth == 2 and s.startswith('mesh '): mesh = s[5:].strip()
        elif depth == 2 and s.startswith('mesh-asset'): asset = True
    return res

SKIP = {'shadow', 'load'}
g = im2glb.GLB(); g.keep_atch = False
label = re.sub(r'\s*_kuid.*$', '', os.path.basename(car.rstrip('/\\')))
root = dict(name=label, children=[]); g.j['nodes'].append(root); ri = len(g.j['nodes']) - 1
g.j['scenes'][0]['nodes'].append(ri)
bog = []
for name, mesh in mesh_table(os.path.join(car, 'config.txt')):
    if name in SKIP: continue
    p = find(car, mesh) or find(car, mesh + '.txt')
    if p is None: print('missing', mesh); continue
    if p.lower().endswith('.lm.txt'):
        p = find(os.path.dirname(p), best_lod(p))
    info = g.add_im(p, os.path.dirname(p), name, parent=ri)
    print(f'{name}: {os.path.basename(p)} {info}')
    _, atch = im2glb.parse_im(p)
    bog += [(n, R, l) for n, R, l in atch if n.startswith('a.bog')]

tlm = find(truck, 'bogey.lm.txt')
tim = find(truck, best_lod(tlm))
for n, R, l in sorted(bog, key=lambda a: a[0]):
    M = np.eye(4); M[:3, :3] = R; M[:3, 3] = l
    info = g.add_im(tim, truck, f'truck {n[5:]}', xform=M, parent=ri)
    print(f'truck at {n}: {info}')
g.save(out)
print('wrote', out)
