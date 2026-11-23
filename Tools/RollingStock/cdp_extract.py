"""Unpack a Trainz .cdp (ACS$ chunk soup) into folders: one per asset, config dump + embedded files."""
import struct, sys, os, re

def parse(buf, off, end):
    out = []
    while off < end:
        size, = struct.unpack_from('<I', buf, off); off += 4
        e = off + size
        nlen = buf[off]; off += 1
        name = buf[off:off+nlen].rstrip(b'\0').decode('cp1252', 'replace'); off += nlen
        t = buf[off]; off += 1
        data = buf[off:e]
        if t == 0:
            val = parse(buf, off, e)
        elif t == 1:
            val = list(struct.unpack('<%di' % (len(data)//4), data))
        elif t == 2:
            val = list(struct.unpack('<%df' % (len(data)//4), data))
        elif t == 3:
            val = data.rstrip(b'\0').decode('cp1252', 'replace')
        elif t == 13:
            val = 'kuid:%d:%d' % struct.unpack('<ii', data[:8]) if len(data) >= 8 else data
        else:
            val = bytes(data)
        out.append((name, t, val))
        off = e
    return out

def dump(entries, ind=0):
    s = ''
    for n, t, v in entries:
        if t == 0:
            s += '  '*ind + n + ' {\n' + dump(v, ind+1) + '  '*ind + '}\n'
        elif isinstance(v, bytes):
            s += '  '*ind + f'{n} <{t}: {len(v)} bytes>\n'
        else:
            s += '  '*ind + f'{n} {v}\n'
    return s

def write_files(entries, path):
    os.makedirs(path, exist_ok=True)
    for n, t, v in entries:
        p = os.path.join(path, n)
        if t == 0:
            write_files(v, p)
        else:
            with open(p, 'wb') as f:
                f.write(v if isinstance(v, bytes) else str(v).encode())

def safe(s):
    return re.sub(r'[<>:"/\\|?*]', '_', s).strip() or 'x'

buf = open(sys.argv[1], 'rb').read()
out = sys.argv[2]
assert buf[:4] == b'ACS$'
root = parse(buf, 16, len(buf))
assets = next(v for n, t, v in root if n == 'assets')
for kuid, t, body in assets:
    d = dict((n, v) for n, _, v in body)
    folder = os.path.join(out, safe(f"{d.get('username', '')} {kuid}"))
    os.makedirs(folder, exist_ok=True)
    cfg = [(n, t, v) for n, t, v in body if n != 'files']
    open(os.path.join(folder, 'config.txt'), 'w', encoding='utf-8', errors='replace').write(dump(cfg))
    files = d.get('files')
    if files:
        write_files(files, folder)
    print(folder)
