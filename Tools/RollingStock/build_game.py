"""Build the game's rolling stock from the decompressed Trainz assets in _source: one GLB per livery under
<Folder>/LOD0/, laid out the way Highball/World/VehicleModel.cs wants it, and the vehicle.json beside it.

Usage: python -I build_game.py [car id ...]      (all of them with no ids)

The GLB: glTF Y up, metres, front (Trainz's a.limfront end, its -Y) towards +z, the origin on the rail top midway
between the truck centres (where Trainz has it already). Nodes: Body (everything carried on the springs, couplers and
hoses included) and Truck_Front / Truck_Rear (each S-2 truck, its wheels in it), placed at the car's a.bog points.
Left out: Trainz's drop-shadow quads, the running-number plates (arn.im: Trainz letters them at run time) and loads.
Diffuse textures are capped at MAX_TEX on a side.

The vehicle.json sizes are measured off the meshes: length over the coupler knuckles, truck centres, front overhang
(front knuckle to front truck centre). Masses are typical figures for the type (see each car's note)."""
import sys, os, re, json
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np
from PIL import Image
import im2glb

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
HERE = os.path.join(REPO, 'Content', 'Meshes', 'Vehicles', 'Freight')  # where each car's folder goes
SRC = os.path.join(REPO, 'ContentSource', 'Vehicles', 'Freight', '_source (decompressed Trainz files)')
MAX_TEX = 2048
MAX_NORMAL = 1024   # normal maps on a side: they're PNG, and the biggest part of a GLB at 2048
MAX_SKINS = 2   # skin variants per road on top of its own paint (each is a whole GLB: about 8 MB)

# ---- texture lookup: also accept "name.texture" (no .txt), as some packs ship it --------------------------------------
_read_tex = im2glb.read_texture_txt
TEX_USED = set()      # image files the model being built uses
TEX_REFS = set()      # texture base names the model being built asks for (lower case)
TRUCK_TEX = None      # a truck folder whose bogey/wheel textures stand in where a truck's are compiled (unreadable)
def read_texture_txt(folder, ref):
    base = re.split(r'[\\/]', ref)[-1]
    if base.lower().endswith('.texture'): base = base[:-8]
    TEX_REFS.add(base.lower())
    prim, alpha, info = _read_tex(folder, ref)
    if prim: TEX_USED.add(prim); return prim, alpha, info
    if TRUCK_TEX and base.lower() not in im2glb.TEX_OVERRIDES and find(TRUCK_TEX, base + '.texture.txt'):
        return _read_tex(TRUCK_TEX, base + '.texture')
    files = {f.lower(): f for f in os.listdir(folder)}
    t = files.get((base + '.texture').lower())
    if t:
        info = {}
        for line in open(os.path.join(folder, t), encoding='latin1'):
            if '=' in line:
                k, v = line.strip().split('=', 1); info[k.strip().lower()] = v.strip()
        def img(n): return os.path.join(folder, files[n.lower()]) if n and n.lower() in files else None
        return img(info.get('primary')), img(info.get('alpha')), info
    return None, None, {}
im2glb.read_texture_txt = read_texture_txt

# ---- cap texture size ----------------------------------------------------------------------------------------------------
_open = Image.open
def open_capped(path, *a, **k):
    im = _open(path, *a, **k)
    if max(im.size) > MAX_TEX:
        im.load()
        s = MAX_TEX / max(im.size)
        im = im.resize((max(1, round(im.size[0] * s)), max(1, round(im.size[1] * s))), Image.LANCZOS)
    return im
im2glb.Image.open = open_capped

# ---- opaque colour textures as JPEG (a fraction of the PNG's size); alpha and normal maps stay PNG -----------------------
import io
def image(self, path, alpha_path=None, normal=False):
    key = (path, alpha_path, normal)
    if key in self.imgcache: return self.imgcache[key]
    im = Image.open(path)
    has_alpha = False
    if normal:
        im = im.convert('RGB')
        if max(im.size) > MAX_NORMAL:
            k = MAX_NORMAL / max(im.size); im = im.resize((round(im.size[0] * k), round(im.size[1] * k)), Image.LANCZOS)
    else:
        im = im.convert('RGBA')
        if alpha_path and os.path.normcase(alpha_path) != os.path.normcase(path):
            a = Image.open(alpha_path).convert('L').resize(im.size); im.putalpha(a)
        has_alpha = alpha_path is not None and im.getchannel('A').getextrema()[0] < 250
        if not has_alpha: im = im.convert('RGB')
    buf = io.BytesIO()
    if has_alpha or normal:
        im.save(buf, 'PNG', compress_level=9); mime = 'image/png'
    else:
        im.save(buf, 'JPEG', quality=92, subsampling=0); mime = 'image/jpeg'
    self.j['images'].append(dict(bufferView=self.view(buf.getvalue()), mimeType=mime,
                                 name=os.path.splitext(os.path.basename(path))[0]))
    self.j['textures'].append(dict(source=len(self.j['images']) - 1, sampler=0))
    r = (len(self.j['textures']) - 1, has_alpha); self.imgcache[key] = r; return r
im2glb.GLB.image = image

def find(folder, rel):
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
    """The mesh-table's entries that are part of the car, as (name, mesh path, mesh-asset kuid or None): not those hung
    on an attachment point (EOT devices, air hoses), not those Trainz only makes on request (auto-create 0), not drop
    shadows, number plates or loads. A mesh-asset entry's mesh is in that asset (a shared mesh library)."""
    lines = open(cfg, encoding='utf-8').read().splitlines()
    res, i = [], lines.index('mesh-table {') + 1
    depth, cur = 1, None
    while depth > 0 and i < len(lines):
        s = lines[i].strip(); i += 1
        if s.endswith('{'):
            depth += 1
            if depth == 2: cur = s[:-1].strip(); mesh = None; asset = None; auto = True; att = False
        elif s == '}':
            if depth == 2 and mesh and not att and auto and not skip_entry(cur, mesh): res.append((cur, mesh, asset))
            depth -= 1
        elif depth == 2 and s.startswith('mesh '): mesh = s[5:].strip()
        elif depth == 2 and s.startswith('mesh-asset'): asset = s.split()[1]
        elif depth == 2 and s.startswith('att '): att = True
        elif depth == 2 and s.startswith('auto-create') and '[0]' in s: auto = False
    return res

SKIP_ENTRIES = {'shadow', 'load'}
SKIP_MESH = re.compile(r'^(mesh_)?(arn|shadow|gshadow|load|load-.*|numbers|cardata)\.', re.I)
def skip_entry(name, mesh):
    base = re.split(r'[\\/]', mesh)[-1]
    return name.lower() in SKIP_ENTRIES or name.lower().startswith('load') or SKIP_MESH.search(base) is not None

def keep_part(m, g):
    n = (m or {}).get('name', '').lower()
    return not (n.startswith('shadow') or n.startswith('shade'))

def resolve_mesh(car, mesh):
    p = find(car, mesh) or find(car, mesh + '.txt')
    if p and p.lower().endswith('.lm.txt'):
        p = find(os.path.dirname(p), best_lod(p))
    return p

def build(car, truck, out, skin=None, tex=None, extras=()):
    """extras: (mesh file, attachment point) pairs to hang on the body at its points: a load, as containers in a well."""
    """Returns the measurements in game axes (z forward). tex: texture base name -> .texture.txt standing in for it."""
    im2glb.TEX_OVERRIDES.clear()
    im2glb.TEX_OVERRIDES.update(tex or {})
    if isinstance(skin, str): skin = {'map1': skin}
    im2glb.TEX_OVERRIDES.update(skin or {})
    TEX_REFS.clear(); TEX_USED.clear()
    # filter shadow parts out of everything parse_im hands add_im
    parse = im2glb.parse_im
    def parse_kept(path):
        parts, atch = parse(path)
        return [(m, g) for m, g in parts if keep_part(m, g)], atch
    im2glb.parse_im = parse_kept
    try:
        g = im2glb.GLB(); g.keep_atch = False
        g.j['nodes'].append(dict(name='Car', children=[])); root = 0
        g.j['scenes'][0]['nodes'].append(root)
        g.j['nodes'].append(dict(name='Body', children=[])); body = 1
        g.j['nodes'][root]['children'].append(body)
        bog, couplers, allpos, points = {}, [], [], {}
        for name, mesh, asset in mesh_table(os.path.join(car, 'config.txt')):
            if name in SKIP_ENTRIES: continue
            lib = by_kuid(asset, os.path.dirname(car)) if asset else car
            if lib is None: print('   missing mesh library', asset, 'for', mesh); continue
            p = resolve_mesh(lib, mesh)
            if p is None: print('   missing', mesh); continue
            g.add_im(p, os.path.dirname(p), name, parent=body)
            parts, atch = parse_kept(p)
            for m, gg in parts:
                allpos.append(gg['pos'])
                if 'coupler' in (m or {}).get('name', '').lower(): couplers.append(gg['pos'])
            for n, R, l in atch:
                if n.startswith('a.bog'): bog[n] = (R, l)
                points.setdefault(n, (R, l))
        for k, (mesh, at) in enumerate(extras):
            at = next(p for p in ([at] if isinstance(at, str) else at) if p in points)   # the first of the names it has
            R, l = points[at]
            M = np.eye(4); M[:3, :3] = R; M[:3, 3] = l
            g.add_im(mesh, os.path.dirname(mesh), f'Load{k}', xform=M, parent=body)
        if len(bog) > 2: bog = {k: v for k, v in bog.items() if k in ('a.bog0', 'a.bog1')}   # bog2/3: animated couplers hung as bogeys
        assert len(bog) == 2, (car, list(bog))
        tim = truck_mesh(truck)
        # front: the truck towards Trainz -Y (glTF +z)
        order = sorted(bog.items(), key=lambda kv: kv[1][1][1])
        tpos = []
        # a truck's origin is its axle centre, where a.bog is; a few are modelled standing on the rail instead: lift them down
        tz = np.concatenate([q['pos'] for _, q in parse_kept(tim)[0]])[:, 2]
        axle = (tz.min() + tz.max()) / 2 if abs(tz.min() + tz.max()) / 2 > 0.05 else 0.0
        for label, (n, (R, l)) in zip(('Truck_Front', 'Truck_Rear'), order):
            M = np.eye(4); M[:3, :3] = R; M[:3, 3] = l - R @ np.array([0, 0, axle])
            g.add_im(tim, truck, label, xform=M, parent=root)
            tparts, _ = parse_kept(tim)
            tp = np.concatenate([q['pos'] for _, q in tparts]) @ R.T + M[:3, 3]
            tpos.append(tp)
        g.save(out)
    finally:
        im2glb.parse_im = parse
    allp = np.concatenate(allpos)
    cp = np.concatenate(couplers) if couplers else allp
    if np.ptp(cp[:, 1]) < np.ptp(allp[:, 1]) * 0.8: cp = allp   # 'coupler' parts at one end only, or some small part so named
    front_y, rear_y = order[0][1][1][1], order[1][1][1][1]
    tp = np.concatenate(tpos)
    return dict(
        lengthM=round(float(cp[:, 1].max() - cp[:, 1].min()), 3),
        truckCentresM=round(rear_y - front_y, 3),
        frontOverhangM=round(float(front_y - cp[:, 1].min()), 3),
        wheelDiameterM=round(float(order[0][1][1][2]) * 2, 4),   # axle height above the rail
        railTopMin=round(float(tp[:, 2].min()), 3),
        midTrucks=round((front_y + rear_y) / 2, 3),
        bodyWidth=round(float(allp[:, 0].max() - allp[:, 0].min()), 3),
        height=round(float(allp[:, 2].max()), 3))

def d(*parts): return os.path.join(SRC, *parts)
def one(pattern):
    import glob
    r = glob.glob(pattern.replace('[', '[[]'))
    assert len(r) == 1, (pattern, r)
    return r[0]

# ---- assets by kuid, so a car finds its own truck and skins in whichever pack shipped them -------------------------------
def _norm_uid(u):
    u = int(u)
    return u & 0x1FFFFFF if u > 0x1FFFFFF else u     # kuid:33599756:x is kuid2:45324:x:1 (the version in the top bits)

_INDEX = None
def asset_index():
    global _INDEX
    if _INDEX is None:
        _INDEX = {}
        for pack in sorted(os.listdir(SRC)):
            pp = os.path.join(SRC, pack)
            if not os.path.isdir(pp): continue
            for a in sorted(os.listdir(pp)):
                m = re.search(r'_kuid2?_(-?\d+)_(\d+)(?:_\d+)?_$', a)
                if m: _INDEX.setdefault((_norm_uid(m[1]), int(m[2])), []).append(os.path.join(pp, a))
    return _INDEX

def by_kuid(kuid, near=None):
    m = re.match(r'kuid2?:(-?\d+):(\d+)', kuid)
    hits = asset_index().get((_norm_uid(m[1]), int(m[2])), []) if m else []
    if near: hits = sorted(hits, key=lambda h: not h.startswith(near))   # the same pack's copy first
    return hits[0] if hits else None

def config(asset): return open(os.path.join(asset, 'config.txt'), encoding='utf-8', errors='replace').read()

# Stand-ins where a car's truck isn't in any pack (Trainz built-ins, or not shipped), by the car's weight class.
FALLBACK_TRUCK = {
    50: lambda: one(d('greenville_52ft_gondola_pack', 'Bettendorf 50 Ton Truck Dusty*')),
    70: lambda: one(d('dttx_wellcar', 'S-2 Truck 70 ton*')),
    100: lambda: one(d('csx_4750_hopper', 'S-2 Truck 100 ton*')),
}
def truck_mesh(truck):
    """A truck's most detailed mesh: its LOD list (bogey.lm, truck.lm...) or its only .im; None if it's built of other assets."""
    files = sorted(os.listdir(truck))
    lm = next((f for f in files if f.lower() in ('bogey.lm.txt', 'bogey.lm')), None) or next((f for f in files if f.lower().endswith('.lm.txt')), None)
    if lm: return find(truck, best_lod(os.path.join(truck, lm)))
    ims = [f for f in files if f.lower().endswith('.im')]
    return os.path.join(truck, ims[0]) if len(ims) == 1 else None

def truck_of(car, tons):
    pack = os.path.dirname(car)
    for k in re.findall(r'^\s+bogey (kuid\S+)', config(car), re.M):
        t = by_kuid(k, pack)
        # a truck made of meshes from other assets (mesh-asset), not shipped here, is no use: take a stand-in
        if t and re.search(r'^kind bogey', config(t), re.M) and truck_mesh(t): return t
    t = FALLBACK_TRUCK[tons]()
    print(f'   (truck not shipped: using {os.path.basename(t)})')
    return t

def grl_kg(truck, tons):
    """The truck's gross rail load, from its name ('... 263,000 lbs ...'), else its weight class's."""
    m = re.search(r'(\d{3}),(\d{3}) lbs', os.path.basename(truck))
    lb = int(m[1] + m[2]) if m else {50: 169000, 70: 220000, 100: 263000}[tons]
    return round(lb * 0.45359237 / 10) * 10

def skin_groups(car):
    """The texture-groups the car's config points at (its number and weathering variations)."""
    pack, out = os.path.dirname(car), []
    for k in dict.fromkeys(re.findall(r'(kuid2?:-?\d+:\d+)', config(car))):
        a = by_kuid(k, pack)
        if a and a != car and re.search(r'^kind texture-group', config(a), re.M)                 and not re.search(r'^username .*placard', config(a), re.M | re.I): out.append(a)   # not the DOT placard library
    return out

def skin_sets(group, refs):
    """A texture-group's skins as {variant: {texture the model uses: replacement .texture.txt}}. Which of the model's
    textures each one replaces is the longest of the model's texture names it starts with (map1_b -> map1)."""
    sets = {}
    stems = {}   # 'map1' -> 'map1.m.tbumptex_diff': a TRS material's texture is named after its map
    for r in refs: stems.setdefault(r.split('.')[0], r)
    for t in re.findall(r'^\s+\d+ (\S+\.texture)$', config(group), re.M):
        name = os.path.basename(t)[:-8].lower()
        base = max((r for r in refs if name.startswith(r)), key=len, default=None)   # the same name: a whole repaint
        stem = max((s for s in stems if name.startswith(s)), key=len, default=None)
        if not base and stem: base = stem
        path = find(group, t + '.txt')
        if not base or not path: continue
        var = name[len(base):].strip('_-') or '0'
        sets.setdefault(var, {})[stems.get(base, base)] = path
    return dict(sorted(sets.items()))

# ---- loads: containers in a well car's well -------------------------------------------------------------------------------
def container20(i):
    """One of the five 20 ft boxes in the container pack's mesh library: 1-3 MSC (beige, yellow, red), 4 Alianca, 5 APL."""
    lib = one(d('containers_20_1', '20ft Container Mesh Library*'))
    return find(lib, f'{i}/' + best_lod(find(lib, f'{i}/body.lm.txt')))

CONTAINER20_LOADS = [('20ft_msc', 'Two 20 ft containers (MSC yellow, MSC red)', (2, 3)),
                     ('20ft_alianca_apl', 'Two 20 ft containers (Alianca, APL)', (4, 5)),
                     ('20ft_msc_apl', 'Two 20 ft containers (MSC, APL)', (1, 5))]
def well_loads(front, rear):
    """Two 20 ft boxes end to end in the well, at the car's own points for them (only 40 ft and longer go on top)."""
    return lambda: [(i, n, [(container20(a), front), (container20(b), rear)]) for i, n, (a, b) in CONTAINER20_LOADS]

# ---- the cars --------------------------------------------------------------------------------------------------------------
CARS = {}
def car(id, folder, **kw): CARS[id] = dict(folder=folder, **kw)

def hopper_skins():
    base = one(d('csx_4750_hopper', 'CSXT LO 4750 3 bay*'))
    skins = one(d('csx_4750_hopper', 'CSX LO 4750 Skins*'))
    out = [('csx', 'CSX', None)]
    for i in range(5):
        out.append((f'csx_{i + 1}', f'CSX (car {i + 2})', os.path.join(skins, str(i), 'map1.texture.txt')))
    return base, out

car('boxcar_csx_acf50', 'BoxCars/ACF50',
    truck=lambda: one(d('csx_acf50_boxcar', 'S-2 Truck 70 ton*')),
    liveries=lambda: [('csx', 'CSX', one(d('csx_acf50_boxcar', "CSX 50' ACF Boxcar 1*")), None)],
    json=dict(replaces=[], name="50 ft boxcar (CSX)", kind='Boxcar', type="ACF 50 ft plug-door boxcar, 70-ton trucks",
              note="about 75,700 lb empty (the Trainz asset's 34,350 kg) and 220,000 lb loaded on 70-ton trucks",
              massEmptyKg=34350, massLoadedKg=99800, maxSpeedKmh=113))

def _hopper_liveries():
    base, skins = hopper_skins()
    milw = one(d('milw_4710_covered_hopper', 'MILW LO 4750 3 bay*'))   # the same model, in Milwaukee Road paint
    rock = one(d('rock_lo4750', 'ROCK LO 4750 3 bay*'))
    return [(i, n, base, s) for i, n, s in skins] + [('milw', 'MILW', milw, None), ('rock', 'ROCK', rock, None)]
car('covered_hopper_csx_4750', 'CoveredHoppers/LO4750',
    truck=lambda: one(d('csx_4750_hopper', 'S-2 Truck 100 ton*')),
    liveries=_hopper_liveries,
    json=dict(replaces=['open_hopper_witx', 'covered_hopper_witx'], name="4750 cu ft covered hopper", kind='CoveredHopper', type="Three-bay 4750 cu ft covered hopper, 100-ton trucks",
              note="about 64,400 lb empty (the Trainz asset's 29,211 kg) and 286,000 lb loaded (286k gross rail load)",
              massEmptyKg=29211, massLoadedKg=129700, maxSpeedKmh=113))

car('wellcar_dttx_53', 'WellCars/Husky53',
    truck=lambda: one(d('dttx_wellcar', 'S-2 Truck 70 ton*')),
    liveries=lambda: [('dttx', 'DTTX', one(d('dttx_wellcar', '53ft DTTX Wellcar (Single)*')), None),
                      ('dttx_weathered', 'DTTX (weathered)', one(d('dttx_wellcar', '53ft DTTX Wellcar Weathered*')), None),
                      ('bnsf_h2', 'BNSF H2', one(d('53ft_bnsf_h2_wellcar_2_pck', '53ft BNSF H2 Wellcar (Single)*')), None),
                      ('bnsf_h2_weathered', 'BNSF H2 (weathered)', one(d('53ft_bnsf_h2_wellcar_2_pck', '53ft BNSF H2 Wellcar Weathered*')), None),
                      ('bnsf_h3', 'BNSF H3', one(d('53ft_bnsf_h3_wellcar_2_pck', '53ft BNSF H3 Wellcar (Single)*')), None),
                      ('bnsf_h3_weathered', 'BNSF H3 (weathered)', one(d('53ft_bnsf_h3_wellcar_2_pck', '53ft BNSF H3 Wellcar Weathered*')), None)],
    json=dict(replaces=['wellcar_ttx', 'well_53'], name="53 ft Husky Stack well car", kind='Intermodal', type="Stand-alone 53 ft double-stack well car (TTX Husky Stack)",
              note="about 50,500 lb empty (the Trainz asset's 22,906 kg); with two loaded containers, about 150,000 lb",
              massEmptyKg=22906, massLoadedKg=68040, maxSpeedKmh=113),
    loads=well_loads(['a.load_front_SC', 'a.load_front'], ['a.load_back_SC', 'a.load_rear']))

AUTORACK_ROADS = [('csx', 'CSX'), ('bnsf', 'BNSF'), ('bn', 'BN'), ('up', 'UP'), ('ns', 'NS'), ('cn', 'CN'),
                  ('cn_2', 'CN 2'), ('sp', 'SP'), ('gt', 'GT'), ('cr', 'CR')]
AUTORACK_ROADS_2 = [('cnw', 'CNW'), ('drgw', 'DRGW'), ('drgw_2', 'DRGW 2'), ('icg', 'ICG'), ('ns_2', 'NS 2'), ('up_2', 'UP 2'), ('up_3', 'UP 3')]
car('autorack_89_trilevel', 'Autoracks/TriLevel89',
    truck=lambda: one(d('autoracks_89ft', 'S-2 Truck 50 ton*')),
    # The 28 in truck's textures are compiled .texture files (TRS binaries) we can't read; the 33 in S-2 from the well car
    # pack is the same author's, on the same atlas.
    tex=lambda: {n: os.path.join(one(d('dttx_wellcar', 'S-2 Truck 70 ton*')), n + '.texture.txt') for n in ('bogey', 'wheel')},
    liveries=lambda: [(i, n.replace(' 2', ' (2)'), one(d('autoracks_89ft', f'89Ft Autorack {n} _kuid*')), None) for i, n in AUTORACK_ROADS]
                   + [(i, re.sub(r' (\d)$', r' (\1)', n), one(d('89ftautorack7pack', f'89Ft Autorack {n} _kuid*')), None) for i, n in AUTORACK_ROADS_2],
    json=dict(replaces=['autorack_csx', 'autorack_89'], name="89 ft tri-level autorack", kind='Autorack', type="Enclosed 89 ft tri-level autorack",
              note="about 105,800 lb empty (the Trainz asset's 47,990 kg); loaded with fifteen or so automobiles, about 165,000 lb",
              massEmptyKg=47990, massLoadedKg=74800, maxSpeedKmh=113))

# Cars built straight from their packs: every traincar in the pack whose name matches is a livery, named by the pattern's
# groups; its own truck (by kuid) and its texture-group skins come along. Masses: the Trainz asset's empty mass, and the
# truck's gross rail load unless the car fills up by volume first.
def auto(id, folder, kind, name, type, cars, tons=100, loaded=None, skins=True, replaces=(), speed=113, skins_only=(), truck=None):
    car(id, folder, auto=dict(cars=cars, tons=tons, loaded=loaded, skins=skins, skins_only=skins_only, truck=truck),
        json=dict(replaces=list(replaces), name=name, kind=kind, type=type, maxSpeedKmh=speed))

def roads(pack, pattern, names=None):
    """(livery id, livery name, asset folder) for each traincar in the pack whose username matches."""
    out = []
    for a in sorted(os.listdir(d(pack))):
        cfg = os.path.join(d(pack), a, 'config.txt')
        if not os.path.exists(cfg) or not re.search(r'^kind traincar', config(os.path.join(d(pack), a)), re.M): continue
        user = re.search(r'^username (.*)$', config(os.path.join(d(pack), a)), re.M)[1].strip()
        m = re.fullmatch(pattern, user)
        if not m: continue
        label = ' '.join(g for g in m.groups() if g).strip() if m.groups() else user
        label = (names or {}).get(label, label)
        lid = re.sub(r'[^a-z0-9]+', '_', label.lower().replace('&', '')).strip('_')
        out.append((lid, label, os.path.join(d(pack), a)))
    assert out, (pack, pattern)
    return out

# Box cars
auto('boxcar_60_hc', 'BoxCars/HighCube60', 'Boxcar', "60 ft high-cube boxcar", "60 ft high-cube double-plug-door boxcar",
     lambda: roads('60ft_HC_Box_CSXv1', r'60ft HC Boxcar (CSX) (Ver1)') + roads('60ft_HC_Box_CSXv2', r'60ft HC Boxcar (CSX) (Ver2)')
           + roads('60ft_HC_Box_TTXv1', r'60ft HC Boxcar (TTX) (Ver1)') + roads('60ft_HC_Box_TTXv2', r'60ft HC Boxcar (TTX) (Ver2)')
           + roads('60ft_hc_box_cn_1_', r'60ft HC Boxcar (CN)') + roads('60ft_hc_box_ns1_1_', r'60ft HC Boxcar (NS) (Ver1)')
           + roads('60ft_hc_box_ns2_1_', r'60ft HC Boxcar (NS) (Ver2)'), tons=100)
auto('boxcar_ps1_50', 'BoxCars/PS1_50', 'Boxcar', "50 ft PS-1 boxcar", "Pullman-Standard PS-1 50 ft boxcar",
     lambda: roads('ps-1_50ft_pack', r"(.+) 50' PS-1( 2)?"), tons=70, speed=105)
auto('boxcar_fmc_50', 'BoxCars/FMC50', 'Boxcar', "50 ft FMC boxcar (GVSR)", "FMC 50 ft 5258 cu ft boxcar",
     lambda: roads('GVSR_FMC_Boxcar', r'(GVSR) 50ft Boxcar FMC'), tons=70)

# Covered hoppers
auto('covered_hopper_acf_5000', 'CoveredHoppers/ACF5000', 'CoveredHopper', "5000 cu ft Pressure-Aide covered hopper", "ACF 5000 cu ft covered hopper, Pressure-Aide unloading",
     lambda: roads('acfx_5000_pressureaide', r'(ACFX) P-Dif Hopper ACF 5000(?: \((OMYA|Repaint)\))?'))
auto('covered_hopper_5188', 'CoveredHoppers/Gunderson5188', 'CoveredHopper', "5188 cu ft covered hopper", "Gunderson/Greenbrier three-bay 5188 cu ft covered hopper",
     lambda: roads('bnsf_5188_3bay_covered_hopper', r'(BNSF) 5188 3 bay'))
auto('covered_hopper_acf_2971', 'CoveredHoppers/ACF2971', 'CoveredHopper', "2971 cu ft two-bay covered hopper", "ACF two-bay 2971 cu ft covered hopper, 70-ton trucks",
     # CSXT first: the BN cars ship its Chessie texture as a placeholder, their own paint only in their skins
     lambda: roads('csxt_acf_2bay_covered_hopper', r'(CSXT) 2 Bay Hopper ACF 2971')
           + roads('bn_acf_2bay_covered_hopper', r'(BN Green V1|BN Green V2|BN Grey) 2 Bay Hopper ACF 2971',
                   {'BN Green V1': 'BN green', 'BN Green V2': 'BN green (2)', 'BN Grey': 'BN grey'}), tons=70,
     skins_only={'bn_green', 'bn_green_2', 'bn_grey'})
auto('covered_hopper_lo_3510', 'CoveredHoppers/LO3510', 'CoveredHopper', "3510 cu ft covered hopper", "Three-bay 3510 cu ft covered hopper",
     lambda: roads('nyc_ex_rdg_3510', r'(NYC) ex-RDG LO 3510'), tons=70, speed=105)

auto('covered_hopper_trinity_2bay', 'CoveredHoppers/Trinity2Bay', 'CoveredHopper', "Trinity two-bay covered hopper", "Trinity two-bay covered hopper, 100-ton trucks",
     lambda: roads('trinity2bay4roadpack', r'(\S+) 2 bay Trinity hopper (.+)',
                   {'CRDX CRDX clean': 'CRDX clean', 'CRDX wheathered 1': 'CRDX weathered 1', 'CRDX wheathered 2': 'CRDX weathered 2'}))

auto('covered_hopper_evans', 'CoveredHoppers/Evans', 'CoveredHopper', "Evans covered hopper", "Evans three-bay covered hopper, 100-ton trucks",
     lambda: roads('evanshoppers6pack', r'JR Evans Hopper (\S+)(?: #(\d))?'))

# Open hoppers
auto('open_hopper_100_4bay', 'OpenHoppers/Quad100', 'OpenHopper', "100-ton four-bay open hopper", "100-ton four-bay open coal hopper",
     lambda: roads('bn_100t_4bay_open_hopper-ex-CB_Q', r'(BN) ex-CB&Q 100 Ton Hopper'))
auto('open_hopper_100_3bay', 'OpenHoppers/Triple100', 'OpenHopper', "100-ton three-bay open hopper", "100-ton three-bay open coal hopper",
     lambda: roads('cr_100t_3bay_open_hopper', r'(CR) 100 Ton Hopper') + roads('ns_100t_gondola', r'(NS) 100 Ton Hopper')
           + roads('penn_100t_open_hopper', r'(PC|PRR) 100 Ton Hopper'))
# Each road's paint is only in its texture-group: the mesh library's own texture is a blank. 1950s cars, on the A-3 trucks.
auto('open_hopper_ps3_2bay', 'OpenHoppers/PS3_2Bay', 'OpenHopper', "PS-3 two-bay hopper", "Pullman-Standard PS-3 two-bay open hopper, 70-ton trucks",
     lambda: roads('ps_3_4_pack', r'PS-3 2 Bay (.+)'), tons=70, speed=89, skins_only='all',
     truck=lambda: one(d('ps-1_50ft_pack', 'A-3 Truck 70 ton 220,000 lbs 6in*33in wheels _kuid*')))
auto('open_hopper_55ton', 'OpenHoppers/Hopper55Ton', 'OpenHopper', "55-ton two-bay hopper", "55-ton two-bay open hopper, 70-ton trucks",
     lambda: roads('55ton', r'(.+?) 55 Ton (Ballast |Sand )?Hopper ?(.*)', {'Norfolk Southern': 'NS', 'B&O Chessie': 'B&O (Chessie)', 'B&O Original': 'B&O (original)', 'B&O Roman': 'B&O (Roman)',
                   'B&O ex-C&O': 'B&O (ex-C&O)', 'RF&P Ballast': 'RF&P (ballast)', 'Reading Roman Lettering': 'Reading (Roman)',
                   'Reading Speed Lettering': 'Reading (speed lettering)', 'Reading Speed Lettering 2': 'Reading (speed lettering 2)',
                   'Reading Sand': 'Reading (sand)'}), tons=70, speed=89)
# (The USRA 55-ton CN hopper pack is left out: its body meshes are in an asset the download doesn't include.)
auto('open_hopper_woodchip_60', 'OpenHoppers/Woodchip60', 'OpenHopper', "60 ft woodchip hopper", "60 ft 9 in woodchip car",
     lambda: roads('bftx_60ft_woodchip_open_hopper', r'(BFTX) 60Ft\.9In\. woodchip gondola'), loaded=None)

# Gondolas
auto('gondola_greenville_52', 'Gondolas/Greenville52', 'Gondola', "52 ft Greenville gondola", "Greenville 52 ft mill gondola",
     lambda: roads('greenville_52ft_gondola_pack', r'(.+) Greenville Gondola'), tons=70, speed=89)

# Flat cars
auto('flatcar_ttx_64', 'Flatcars/TTX64', 'Flatcar', "64 ft flatcar (TTX)", "64 ft general-service flatcar",
     lambda: roads('ttx_flatcar', r'(TTX) Flatcar'), tons=70)
auto('flatcar_bulkhead_62', 'Flatcars/Bulkhead62', 'Flatcar', "62 ft bulkhead flatcar", "62 ft bulkhead flatcar",
     lambda: roads('ttpx_62ft_bulkhead_flatcar', r'(TTPX) 62Ft\. Bulkhead'))
auto('flatcar_centerbeam_73', 'Flatcars/Centerbeam73', 'Flatcar', "73 ft centerbeam flatcar", "73 ft centerbeam lumber flatcar",
     lambda: roads('73ftcenterbeam5pack', r'73ft Centerbeam (\S+)( \d)?'))

auto('flatcar_aar_50ton', 'Flatcars/AAR50Ton', 'Flatcar', "50-ton AAR flatcar", "AAR 50-ton general-service flatcar",
     lambda: roads('cn_cp_flatcars', r'50ton AAR Flatcar (CN|CP)'), tons=50, speed=89,
     truck=lambda: one(d('greenville_52ft_gondola_pack', 'Bettendorf 50 Ton Truck Dusty*')))

auto('flatcar_aar_70ton', 'Flatcars/AAR70Ton', 'Flatcar', "70-ton AAR flatcar", "AAR 70-ton general-service flatcar",
     lambda: roads('70ton_flats', r'70ton AAR Flatcar (.+)'), tons=70, speed=89,
     truck=lambda: one(d('55ton', 'Bettendorf 70 Ton Truck Dusty*')))

# Reefers
auto('reefer_pcf_57', 'Reefers/PCF57', 'Reefer', "57 ft mechanical reefer", "PCF 57 ft mechanical refrigerator car",
     lambda: roads('reefer3pack', r"(.+) PCF 57' Reefer", {'BNFE Green': 'BNFE (green)'}))

# Tank cars
auto('tank_23000_funnelflow', 'TankCars/FunnelFlow23000', 'TankCar', "23,000 gal Funnel-Flow tank car", "23,000 gal Funnel-Flow general-service tank car",
     lambda: roads('utlx_2300gal_tank', r'(UTLX) 23000 gal Funnel-Flow tank car') + roads('23000_gal_tanker_pack', r'(\S+) 23000 gal Funnel-Flow tank car ?(.*)', {'GATX Richardson': 'GATX (Richardson)', 'GATX white': 'GATX (white)', 'PROX black': 'PROX (black)', 'UTLX Terra': 'UTLX (Terra)'}))
auto('tank_20540_latex', 'TankCars/Latex20540', 'TankCar', "20,540 gal latex tank car (DOWX)", "20,540 gal latex tank car",
     lambda: roads('dowx_20540_latex_tank_car', r'(DOWX) 20540 gal Tank Car \(Latex\)'))
auto('tank_33000_lpg', 'TankCars/LPG33000', 'TankCar', "33,000 gal LPG tank car (UTLX)", "33,000 gal pressurised LPG tank car",
     lambda: roads('jr_utlx_33k_lpg_tank_car', r'(UTLX) LPG Tank \(Modern\)'))

# Well cars
auto('wellcar_ap53', 'WellCars/AP53', 'Intermodal', "53 ft AP53 well car", "Stand-alone 53 ft All-Purpose double-stack well car",
     lambda: roads('ap53_wellcar', r'AP53 (TTX) Wellcar ?([ABC])?') + roads('ap53_wellcar_fec_2_pck', r'AP53 Wellcar (FEC)( Weathered)?')
           + roads('ap53_wellcar_fec_2', r'AP53 Wellcar (FEC 2)( Weathered)?'), tons=70, loaded=68040)
CARS['wellcar_ap53']['loads'] = well_loads(['a.load_front', 'a.load_front_SC'], ['a.load_rear', 'a.load_back_SC'])

def livery_list(c):
    """[(livery id, name, car asset, skin overrides or None)], and the truck."""
    if 'auto' not in c:
        return c['liveries'](), c['truck'](), None
    a = c['auto']
    cars = a['cars']()
    truck = a['truck']() if a['truck'] else truck_of(cars[0][2], a['tons'])
    out = [(lid, lname, src, None) for lid, lname, src in cars]
    return out, truck, a

def main(ids):
    global TRUCK_TEX
    TRUCK_TEX = one(d('dttx_wellcar', 'S-2 Truck 70 ton*'))
    failed = []
    for cid in ids or CARS:
      try:
        c = CARS[cid]
        folder = os.path.join(HERE, *c['folder'].split('/'))
        os.makedirs(os.path.join(folder, 'LOD0'), exist_ok=True)
        cars, truck, a = livery_list(c)
        print(f'== {cid}: truck {os.path.basename(truck)}')
        liveries, meas, sizes, seen = [], None, [], set()
        for lid, lname, src, skin in cars:
            jobs = [(lid, lname, skin)]
            for lid_, lname_, skin_ in jobs:
                rel = f'LOD0/{cid}_{lid_}.glb'
                car_truck = (a['truck']() if a['truck'] else truck_of(src, a['tons'])) if a else truck
                m = build(src, car_truck, os.path.join(folder, rel), skin_, c['tex']() if 'tex' in c else None)
                size = os.path.getsize(os.path.join(folder, rel)) / 1e6
                import hashlib
                look = hashlib.md5(b''.join(sorted(hashlib.md5(open(f, 'rb').read()).digest() for f in TEX_USED))).hexdigest()
                skins_ok = a and a['skins'] and lid_ == lid
                refs = set(TEX_REFS)
                if look in seen or (a and lid_ == lid and (a['skins_only'] == 'all' or lid_ in a['skins_only'])):
                    # the same paint as a livery already built (a pack shipping another car's texture as a placeholder)
                    os.remove(os.path.join(folder, rel)); print(f'   {lid_:<22} placeholder or repeated paint: left out')
                else:
                    seen.add(look)
                    print(f'   {lid_:<22} {m["lengthM"]:6.2f} m  trucks {m["truckCentresM"]:6.2f}  {size:5.1f} MB  {os.path.basename(src)[:50]}')
                    meas = meas or m
                    sizes.append(size)
                    liveries.append(dict(id=lid_, name=lname_, model=f'Vehicles/Freight/{c["folder"]}/{rel}'))
                    # the same livery carrying each of the car's loads: a whole model apiece, as the game swaps models
                    for load_id, _, parts in (c['loads']() if 'loads' in c else []):
                        lrel = f'LOD0/{cid}_{lid_}_{load_id}.glb'
                        build(src, car_truck, os.path.join(folder, lrel), skin_, c['tex']() if 'tex' in c else None, parts)
                        sizes.append(os.path.getsize(os.path.join(folder, lrel)) / 1e6)
                        liveries[-1].setdefault('loads', {})[load_id] = f'Vehicles/Freight/{c["folder"]}/{lrel}'
                # skins from the car's texture-groups, now that the model's texture names are known
                if skins_ok:
                    for g in skin_groups(src):
                        for var, ov in list(skin_sets(g, refs).items())[:MAX_SKINS]:
                            jobs.append((f'{lid}_{var}', lname if var == '0' else f'{lname} ({var})', ov))
        j = c['json']
        if a:
            empty = float(re.search(r'^mass \[([\d.]+)\]', config(cars[0][2]), re.M)[1])
            j = dict(j, massEmptyKg=round(empty), massLoadedKg=a['loaded'] or grl_kg(truck, a['tons']),
                     note=f"the Trainz asset's empty mass ({round(empty):,} kg); loaded, "
                          + ('as full as it fills by volume' if a['loaded'] else "the gross rail load its trucks are rated for"))
        vj = {'id': cid, **({'replaces': j['replaces']} if j['replaces'] else {}), 'name': j['name'], 'kind': j['kind'], 'type': j['type'],
              'lengthM': float(meas['lengthM']), 'truckCentresM': float(meas['truckCentresM']), 'frontOverhangM': meas['frontOverhangM'],
              'axles': 4, 'wheelDiameterM': meas['wheelDiameterM'],
              'massEmptyKg': j['massEmptyKg'], 'massLoadedKg': j['massLoadedKg'], 'maxSpeedKmh': j['maxSpeedKmh'],
              **({'loads': [dict(id=i, name=n) for i, n, _ in c['loads']()]} if 'loads' in c else {}),
              'liveries': liveries}
        if j['replaces']:
            note = '// Consists saved with the old RollingStock car (' + ', '.join(j['replaces']) + ') get this one.\n'
        else: note = ''
        head = ('// Built by Tools/RollingStock/build_game.py from the Trainz assets in ContentSource: rerun it rather than edit by hand.\n'
                '// Sizes are measured off the model (knuckle nose to knuckle nose; front overhang to the front truck centre).\n'
                f'// Masses: {j["note"]}.\n' + note)
        with open(os.path.join(folder, 'vehicle.json'), 'w', encoding='utf-8') as f:
            f.write(head + json.dumps(vj, indent=2) + '\n')
        print(f'   wrote {c["folder"]}/vehicle.json: {len(liveries)} liveries, {sum(sizes):.0f} MB')
      except Exception as e:
        import traceback; traceback.print_exc(); failed.append(cid)
    if failed: print('FAILED:', ' '.join(failed)); sys.exit(1)

if __name__ == '__main__':
    args = sys.argv[1:]
    if args == ['--list']:
        for k, v in CARS.items(): print(k, v['folder'])
    else:
        main(args)
