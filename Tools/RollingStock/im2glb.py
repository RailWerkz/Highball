"""Convert decompressed Trainz .im (JIRF/IDXM) meshes to .glb with embedded PNG textures.
Usage: python -I im2glb.py <assetsDir> <outDir>
Layout per rileyzzz/BlenderIMExporter. Trainz axes = Blender (Z up); glTF is Y up."""
import struct, sys, os, io, json, re
import numpy as np
from PIL import Image

def jstr(d, o):
    n, = struct.unpack_from('<I', d, o); n &= 0x00FFFFFF
    return d[o+4:o+4+n].split(b'\0')[0].decode('latin1'), o + 4 + n

def chunks(d, o, end):
    while o + 8 <= end:
        tag = d[o:o+4].decode('latin1'); size, = struct.unpack_from('<I', d, o+4)
        yield tag, o + 8, o + 8 + size
        o += 8 + size

def parse_matl(d, o):
    ver, = struct.unpack_from('<I', d, o); o += 4
    name = ''
    if ver >= 102:
        name, o = jstr(d, o)
        nprops, = struct.unpack_from('<I', d, o); o += 4
        for _ in range(nprops):
            _, o = jstr(d, o); _, o = jstr(d, o)
    two_sided, = struct.unpack_from('<I', d, o); o += 4
    alpha = 1.0
    if ver >= 102:
        alpha, = struct.unpack_from('<f', d, o); o += 4
    amb = struct.unpack_from('<3f', d, o); diff = struct.unpack_from('<3f', d, o+12)
    spec = struct.unpack_from('<3f', d, o+24); emis = struct.unpack_from('<3f', d, o+36)
    shin, = struct.unpack_from('<f', d, o+48); o += 52
    ntex, = struct.unpack_from('<I', d, o); o += 4
    texs = []
    for _ in range(ntex):
        t, = struct.unpack_from('<I', d, o); o += 4
        p, o = jstr(d, o)
        s, = struct.unpack_from('<f', d, o); o += 4
        texs.append((t, p, s))
    return dict(name=name, two_sided=bool(two_sided), alpha=alpha, diffuse=diff, emissive=emis, textures=texs)

def parse_geom(d, o):
    ver, = struct.unpack_from('<I', d, o); o += 4
    prim = 4
    if ver >= 101: prim, = struct.unpack_from('<I', d, o); o += 4
    has_tan = 0
    if ver >= 201: has_tan, = struct.unpack_from('<I', d, o); o += 4
    o += 4  # area
    nv, nprim = struct.unpack_from('<II', d, o); o += 8
    nidx = nprim * 3
    nfn = 0
    if ver >= 101: nidx, nfn = struct.unpack_from('<II', d, o); o += 8
    if ver in (103, 104): o += 4
    if ver >= 102: o += 4  # max influence
    bone = ''
    if ver >= 200: bone, o = jstr(d, o)  # parent bone: vertices are in that bone's space
    v = np.frombuffer(d, '<f4', nv*5, o).reshape(nv, 5); o += nv*20
    idx = np.frombuffer(d, '<u2', nidx, o); o += nidx*2
    nrm = np.frombuffer(d, '<f4', nv*3, o).reshape(nv, 3); o += nv*12
    return dict(prim=prim, pos=v[:, :3].copy(), uv=v[:, 3:5].copy(), idx=idx.copy(), nrm=nrm.copy(), bone=bone)

def quat_mat(x, y, z, w):
    return np.array([[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
                     [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
                     [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]])

def parse_bones(d, o, end, parent, out):
    for tag, b, e in chunks(d, o, end):
        if tag != 'BONE': continue
        name, p = jstr(d, b + 4)
        pos = struct.unpack_from('<3f', d, p); q = struct.unpack_from('<4f', d, p + 12); p += 28 + 4
        M = np.eye(4); M[:3, :3] = quat_mat(*q); M[:3, 3] = pos
        W = parent @ M; out[name] = W
        parse_bones(d, p, e, W, out)

def parse_im(path):
    if path.lower().endswith('.trainzmesh'): return parse_trainzmesh(path)
    d = open(path, 'rb').read()
    assert d[:4] == b'JIRF' and d[8:12] == b'IDXM', path
    parts, atch, bones = [], [], {}
    for tag, o, e in chunks(d, 12, len(d)):
        if tag == 'SKEL':
            parse_bones(d, o + 4, e, np.eye(4), bones)
    for tag, o, e in chunks(d, 12, len(d)):
        if tag == 'CHNK':
            o += 8  # version, chunk index
            m = g = None
            for t2, o2, e2 in chunks(d, o, e):
                if t2 == 'MATL': m = parse_matl(d, o2)
                elif t2 == 'GEOM': g = parse_geom(d, o2)
            if g is not None: parts.append((m, g))
        elif tag == 'ATCH':
            n, = struct.unpack_from('<I', d, o + 4); p = o + 8
            for _ in range(n):
                name, p = jstr(d, p)
                cols = np.array(struct.unpack_from('<9f', d, p)).reshape(3, 3); p += 36
                loc = np.array(struct.unpack_from('<3f', d, p)); p += 12
                atch.append((name, cols.T, loc))  # rows written are R^T rows = R columns
    for _, g in parts:
        if g['bone']:
            if g['bone'] not in bones: print('   unknown bone', g['bone'], path); continue
            W = bones[g['bone']]
            g['pos'] = g['pos'] @ W[:3, :3].T + W[:3, 3]
            g['nrm'] = g['nrm'] @ W[:3, :3].T
    return parts, atch

LAYOUT_SIZES = {'f': 4, 'B': 1, 'b': 1, 'h': 2, 'H': 2}
TEX_SLOTS = {'txAlbedo': 1, 'txNormal': 8, 'txParameters': 99}

def parse_trainzmesh(path):
    """tzMF (.trainzmesh, Trainz TRS/TANE). Reverse-engineered: big-endian header and counts,
    little-endian vertex/index data and u32-length-prefixed strings. Each part:
    [u32le len]['S0_<layout>\\0'][u32be nverts][u32be nidx][u32be stride][verts][u16le idx][material]."""
    d = open(path, 'rb').read()
    assert d[:4] == b'tzMF', path
    hits = [m for m in re.finditer(rb'S\d_[A-Za-z0-9_]+\x00', d)]
    parts = []
    for k, m in enumerate(hits):
        layout = m.group()[3:-1].decode()
        o = m.end()
        nv, ni, stride = struct.unpack_from('>III', d, o); o += 12
        offs, cur = {}, 0
        for name, n, t in re.findall(r'([A-Z][a-z]?\d?)(\d)([fBbhH])', layout):
            offs[name] = (cur, int(n), t); cur += int(n) * LAYOUT_SIZES[t]
        assert cur == stride, (path, layout, cur, stride)
        raw = np.frombuffer(d, np.uint8, nv * stride, o).reshape(nv, stride); o += nv * stride
        def attr(name):
            a, n, _ = offs[name]
            return raw[:, a:a + 4 * n].copy().view('<f4').reshape(nv, n).astype(float)
        idx = np.frombuffer(d, '<u2', ni, o).copy(); o += ni * 2
        g = dict(prim=4, pos=attr('P'), nrm=attr('N'), uv=attr('T0')[:, :2], idx=idx, bone='')
        # material: strings between here and the next part
        end = hits[k + 1].start() if k + 1 < len(hits) else len(d)
        strs, p = [], o
        while p + 4 < end:
            n, = struct.unpack_from('<I', d, p)
            if 1 < n < 200 and p + 4 + n <= end and d[p + 4 + n - 1] == 0 and all(32 <= c < 127 for c in d[p + 4:p + 3 + n]):
                strs.append(d[p + 4:p + 3 + n].decode()); p += 4 + n
            else:
                p += 1
        mname = next((s for s in strs if '.m.' in s), 'material')
        texs = [(TEX_SLOTS[s], strs[i + 1], 1.0) for i, s in enumerate(strs[:-1]) if s in TEX_SLOTS]
        if not texs and mname != 'material':
            # TRS names a material's textures after it: map1.m.tbumptex -> map1.m.tbumptex_diff / _nrm (.texture.txt)
            folder = os.path.dirname(path)
            have = {f.lower() for f in os.listdir(folder)}
            for slot, suffix in ((1, '_diff'), (1, ''), (8, '_nrm')):
                if (mname + suffix + '.texture.txt').lower() in have and not any(t[0] == slot for t in texs):
                    texs.append((slot, mname + suffix + '.texture', 1.0))
        mat = dict(name=mname, two_sided=False, alpha=1.0, diffuse=(1, 1, 1), emissive=(0, 0, 0),
                   textures=[t for t in texs if t[0] != 99])
        parts.append((mat, g))
    # attachment points, after the last part: [u8 name length][name][3 f32be position][4 f32be quaternion x y z w]
    atch, tail = [], (hits[-1].end() if hits else 0)
    for m in re.finditer(rb'a\.', d[tail:]):
        s = tail + m.start(); n = d[s - 1]; name = d[s:s + n]   # the length byte says where the name ends
        if not re.fullmatch(rb'a\.[A-Za-z0-9_.]+', name) or s + n + 28 > len(d): continue
        v = struct.unpack_from('>7f', d, s + n)
        if not all(np.isfinite(v)) or abs(np.linalg.norm(v[3:]) - 1) > 1e-3: continue
        atch.append((name.decode(), quat_mat(*v[3:]), np.array(v[:3])))
    return parts, atch

# Trainz/Blender (x, y, z) -> glTF (x, z, -y)
C = np.array([[1, 0, 0], [0, 0, 1], [0, -1, 0]], float)

TEX_OVERRIDES = {}  # lower-case texture base name -> path of a replacement .texture.txt

def read_texture_txt(folder, ref):
    base = re.split(r'[\\/]', ref)[-1]
    if base.lower().endswith('.texture'): base = base[:-8]
    ov = TEX_OVERRIDES.get(base.lower())
    if ov:
        folder = os.path.dirname(ov); base = os.path.basename(ov)[:-12]
    files = {f.lower(): f for f in os.listdir(folder)}
    txt = files.get((base + '.texture.txt').lower())
    info = {}
    if txt:
        for line in open(os.path.join(folder, txt), encoding='latin1'):
            if '=' in line:
                k, v = line.strip().split('=', 1); info[k.strip().lower()] = v.strip()
    def img(name):
        return os.path.join(folder, files[name.lower()]) if name and name.lower() in files else None
    prim = img(info.get('primary')) or img(base + '.tga') or img(base + '.jpg') or img(base + '.png')
    return prim, img(info.get('alpha')), info

class GLB:
    def __init__(self):
        self.bin = bytearray(); self.j = dict(asset=dict(version='2.0', generator='RailWerkz im2glb'),
            scenes=[dict(nodes=[])], scene=0, nodes=[], meshes=[], materials=[], accessors=[],
            bufferViews=[], images=[], textures=[], samplers=[dict(wrapS=10497, wrapT=10497)])
        self.imgcache = {}
    def view(self, data, target=None):
        while len(self.bin) % 4: self.bin.append(0)
        bv = dict(buffer=0, byteOffset=len(self.bin), byteLength=len(data))
        if target: bv['target'] = target
        self.bin += data; self.j['bufferViews'].append(bv); return len(self.j['bufferViews']) - 1
    def acc(self, arr, ctype, typ, target, minmax=False):
        a = dict(bufferView=self.view(arr.tobytes(), target), componentType=ctype, count=len(arr), type=typ)
        if minmax: a['min'] = arr.min(0).tolist(); a['max'] = arr.max(0).tolist()
        self.j['accessors'].append(a); return len(self.j['accessors']) - 1
    def image(self, path, alpha_path=None, normal=False):
        key = (path, alpha_path)
        if key in self.imgcache: return self.imgcache[key]
        im = Image.open(path)
        has_alpha = False
        if normal:
            im = im.convert('RGB')
        else:
            im = im.convert('RGBA')
            if alpha_path and os.path.normcase(alpha_path) != os.path.normcase(path):
                a = Image.open(alpha_path).convert('L').resize(im.size); im.putalpha(a)
            has_alpha = alpha_path is not None and im.getchannel('A').getextrema()[0] < 250
            if not has_alpha: im = im.convert('RGB')
        buf = io.BytesIO(); im.save(buf, 'PNG', optimize=False, compress_level=6)
        self.j['images'].append(dict(bufferView=self.view(buf.getvalue()), mimeType='image/png',
                                     name=os.path.splitext(os.path.basename(path))[0]))
        self.j['textures'].append(dict(source=len(self.j['images']) - 1, sampler=0))
        r = (len(self.j['textures']) - 1, has_alpha); self.imgcache[key] = r; return r
    def material(self, m, folder):
        mat = dict(name=m['name'] or 'material', pbrMetallicRoughness=dict(metallicFactor=0.0, roughnessFactor=0.8,
                   baseColorFactor=[*m['diffuse'], m['alpha']]), doubleSided=m['two_sided'])
        for t, ref, s in m['textures']:
            prim, alpha, info = read_texture_txt(folder, ref)
            if not prim: print('   missing texture', ref); continue
            if t == 1:
                # env-mapped materials (tbumpenv, reflect...) keep their reflectivity in the alpha channel: opaque
                if 'env' in m['name'].lower() or 'reflect' in m['name'].lower(): alpha = None
                ti, has_a = self.image(prim, alpha)
                mat['pbrMetallicRoughness']['baseColorTexture'] = dict(index=ti)
                mat['pbrMetallicRoughness']['baseColorFactor'] = [1, 1, 1, m['alpha']]
                if has_a:
                    if 'transparent' in m['name'].lower(): mat['alphaMode'] = 'BLEND'
                    else: mat['alphaMode'] = 'MASK'; mat['alphaCutoff'] = 0.5
            elif t == 8:
                ti, _ = self.image(prim, normal=True)
                mat['normalTexture'] = dict(index=ti)
        if m['alpha'] < 0.999 and 'alphaMode' not in mat: mat['alphaMode'] = 'BLEND'
        self.j['materials'].append(mat); return len(self.j['materials']) - 1
    def add_im(self, path, folder, name, translation=None, xform=None, parent=None):
        """xform: optional 4x4 placement in Trainz space; parent: node index to attach under (else scene root)."""
        parts, atch = parse_im(path)
        prims = []
        for m, g in parts:
            if g['prim'] != 4 or len(g['idx']) == 0: continue
            pos = (g['pos'] @ C.T).astype('<f4'); nrm = (g['nrm'] @ C.T)
            ln = np.linalg.norm(nrm, axis=1, keepdims=True); nrm = (nrm / np.where(ln > 0, ln, 1)).astype('<f4')
            p = dict(attributes=dict(POSITION=self.acc(pos, 5126, 'VEC3', 34962, True),
                                     NORMAL=self.acc(nrm, 5126, 'VEC3', 34962),
                                     TEXCOORD_0=self.acc(g['uv'].astype('<f4'), 5126, 'VEC2', 34962)),
                     indices=(self.acc(g['idx'].astype('<u2'), 5123, 'SCALAR', 34963) if g['idx'].max() < 65536 else self.acc(g['idx'].astype('<u4'), 5125, 'SCALAR', 34963)), mode=4)
            if m: p['material'] = self.material(m, folder)
            prims.append(p)
        self.j['meshes'].append(dict(name=name, primitives=prims))
        node = dict(name=name, mesh=len(self.j['meshes']) - 1, children=[])
        if translation is not None: node['translation'] = translation
        if xform is not None:
            C4 = np.eye(4); C4[:3, :3] = C
            node['matrix'] = (C4 @ xform @ C4.T).T.flatten().tolist()
        self.j['nodes'].append(node); ni = len(self.j['nodes']) - 1
        for an, R, loc in (atch if getattr(self, 'keep_atch', True) else []):
            M = np.eye(4); M[:3, :3] = C @ R @ C.T; M[:3, 3] = C @ loc
            self.j['nodes'].append(dict(name=an, matrix=M.T.flatten().tolist()))
            node['children'].append(len(self.j['nodes']) - 1)
        if not node['children']: del node['children']
        if parent is None: self.j['scenes'][0]['nodes'].append(ni)
        else: self.j['nodes'][parent].setdefault('children', []).append(ni)
        self.last_node = ni
        return len(parts), sum(len(g['idx']) // 3 for _, g in parts), len(atch)
    def save(self, path):
        while len(self.bin) % 4: self.bin.append(0)
        self.j['buffers'] = [dict(byteLength=len(self.bin))]
        for k in ('images', 'textures', 'materials'):
            if not self.j[k]: del self.j[k]
        if 'textures' not in self.j: del self.j['samplers']
        js = json.dumps(self.j, separators=(',', ':')).encode()
        js += b' ' * (-len(js) % 4)
        with open(path, 'wb') as f:
            f.write(struct.pack('<III', 0x46546C67, 2, 12 + 8 + len(js) + 8 + len(self.bin)))
            f.write(struct.pack('<II', len(js), 0x4E4F534A)); f.write(js)
            f.write(struct.pack('<II', len(self.bin), 0x004E4942)); f.write(self.bin)

def main():
  src, dst = sys.argv[1], sys.argv[2]
  os.makedirs(dst, exist_ok=True)
  allglb = GLB(); x = 0.0
  for folder in sorted(os.listdir(src)):
    fp = os.path.join(src, folder)
    ims = sorted(f for f in os.listdir(fp) if f.lower().endswith('.im'))
    if not ims: print('-- no mesh:', folder); continue
    label = re.sub(r'\s*_kuid_.*$', '', folder)
    g = GLB()
    for im in ims:
        nm = label if len(ims) == 1 else f'{label} - {os.path.splitext(im)[0]}'
        info = g.add_im(os.path.join(fp, im), fp, nm)
        # combined scene: line the assets up along X with a gap
        parts, _ = parse_im(os.path.join(fp, im))
        allpos = np.concatenate([gg['pos'] for _, gg in parts])
        w = float(allpos[:, 0].max() - allpos[:, 0].min())
        allglb.add_im(os.path.join(fp, im), fp, nm, [x - float(allpos[:, 0].min()), 0.0, 0.0])
        x += w + 1.0
        print(f'{nm}: {info[0]} parts, {info[1]} tris, {info[2]} attachments')
    g.save(os.path.join(dst, label + '.glb'))
  allglb.save(os.path.join(dst, '_ALL.glb'))

if __name__ == '__main__':
    main()
