"""Lay RTXT procedural-track pieces along a straight + curve, the way Trainz bends them, into one .glb.
Usage: python -I rtxt_demo.py <rtxt out dir> <out.glb> [gauge-variant 090|115|136] [ballast set Gr1|Gr2|Gr3|Blk|SP] [CL|RU]"""
import sys, os, glob, math, random
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
import im2glb

src, out = sys.argv[1], sys.argv[2]
wt = sys.argv[3] if len(sys.argv) > 3 else '115'
bset = sys.argv[4] if len(sys.argv) > 4 else 'Gr3'
cond = sys.argv[5] if len(sys.argv) > 5 else 'CL'
def asset(prefix):
    return glob.glob(os.path.join(src, prefix + '*'))[0]

# the ballast meshes of a set live in whichever of its assets carries them
cands = [p for p in glob.glob(os.path.join(src, f'RTXT {bset} *')) if os.path.exists(os.path.join(p, f'{wt}_ballast-a.trainzmesh'))]
BALLAST = cands[0]
TIES = asset('RTXT Library Ties Wood ')
RAILS = asset('RTXT Library Rails Clean ' if cond == 'CL' else 'RTXT Library Rails Rusty ')
PLATES_L = asset(f'RTXT Tieplates L {wt} '); PLATES_R = asset(f'RTXT Tieplates R {wt} ')
OFF = 0.7519

# centreline: 24 m straight along +Y, then an arc of radius R turning left for 60 m
R, STRAIGHT, ARC = 150.0, 24.0, 60.0
def frame(s):
    if s <= STRAIGHT:
        return np.array([0.0, s]), 0.0
    a = (s - STRAIGHT) / R
    return np.array([-R + R * math.cos(a), STRAIGHT + R * math.sin(a)]), a
def bend(pos, nrm, s_end, lat):
    """Mesh space: x across (+right), y along (piece spans y in [-4,0] ending at s_end), z up."""
    P = np.empty_like(pos); N = np.empty_like(nrm)
    for i, (x, y, z) in enumerate(pos):
        c, h = frame(s_end + y)
        fwd = np.array([-math.sin(h), math.cos(h)]); right = np.array([fwd[1], -fwd[0]])
        xy = c + (x + lat) * right
        P[i] = (xy[0], xy[1], z)
        nx, ny, nz = nrm[i]
        N[i] = (*(nx * right + ny * fwd), nz)
    return P, N

random.seed(4)
groups = {}   # (component, material name) -> lists
def lay(component, folder, meshes, lat, length=STRAIGHT + ARC, mirror=False):
    s = 4.0
    while s <= length + 1e-6:
        parts, _ = im2glb.parse_trainzmesh(os.path.join(folder, random.choice(meshes)))
        for m, g in parts:
            pos, nrm, idx = g['pos'], g['nrm'], g['idx']
            if mirror:
                pos = pos * [-1, 1, 1]; nrm = nrm * [-1, 1, 1]; idx = idx.reshape(-1, 3)[:, ::-1].flatten()
            P, N = bend(pos, nrm, s, lat)
            k = (component, m['name'])
            G = groups.setdefault(k, dict(m=m, folder=folder, pos=[], nrm=[], uv=[], idx=[], n=0))
            G['pos'].append(P); G['nrm'].append(N); G['uv'].append(g['uv']); G['idx'].append(idx + G['n']); G['n'] += len(P)
        s += 4.0

lay('ballast', BALLAST, [f'{wt}_ballast-{v}.trainzmesh' for v in 'abc'], 0)
lay('ties', TIES, [f'{wt}_ties-{v}.trainzmesh' for v in 'abc'], 0)
lay('tieplates L', PLATES_L, [f'{wt}_tieplates-{v}.trainzmesh' for v in 'ab'], -OFF)
lay('tieplates R', PLATES_L, [f'{wt}_tieplates-{v}.trainzmesh' for v in 'ab'], OFF, mirror=True)
lay('rail L', RAILS, [f'{wt}_rail-hi.trainzmesh'], -OFF)
lay('rail R', RAILS, [f'{wt}_rail-hi.trainzmesh'], OFF)

glb = im2glb.GLB(); C = im2glb.C
root = dict(name=f'RTXT {bset} {cond} {wt}', children=[]); glb.j['nodes'].append(root)
glb.j['scenes'][0]['nodes'].append(0)
tris = {}
for (comp, mname), G in groups.items():
    pos = (np.concatenate(G['pos']) @ C.T).astype('<f4')
    nrm = np.concatenate(G['nrm']) @ C.T; nrm = (nrm / np.maximum(np.linalg.norm(nrm, axis=1, keepdims=True), 1e-9)).astype('<f4')
    uv = np.concatenate(G['uv']).astype('<f4'); idx = np.concatenate(G['idx'])
    big = idx.max() > 65535
    prim = dict(attributes=dict(POSITION=glb.acc(pos, 5126, 'VEC3', 34962, True), NORMAL=glb.acc(nrm, 5126, 'VEC3', 34962),
                                TEXCOORD_0=glb.acc(uv, 5126, 'VEC2', 34962)),
                indices=glb.acc(idx.astype('<u4' if big else '<u2'), 5125 if big else 5123, 'SCALAR', 34963),
                material=glb.material(G['m'], G['folder']), mode=4)
    glb.j['meshes'].append(dict(name=f'{comp} {mname}', primitives=[prim]))
    glb.j['nodes'].append(dict(name=f'{comp} ({mname})', mesh=len(glb.j['meshes']) - 1))
    root['children'].append(len(glb.j['nodes']) - 1)
    tris[comp] = tris.get(comp, 0) + len(idx) // 3
glb.save(out)
L = STRAIGHT + ARC
print('wrote', out, {k: f'{v} tris ({v / L:.0f}/m)' for k, v in tris.items()}, 'total/m', round(sum(tris.values()) / L))
