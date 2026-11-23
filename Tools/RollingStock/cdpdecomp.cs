// Port of the decompressor from the cdptool crate (MIT) — Trainz CDP LZSS / adaptive Huffman.
// Usage: dotnet run cdpdecomp.cs <assetsDir>   — decompresses every file except config.txt in place.
using System;
using System.IO;
using System.Collections.Generic;

foreach (var path in Directory.GetFiles(args[0], "*", SearchOption.AllDirectories))
{
    if (Path.GetFileName(path) == "config.txt") continue;
    var d = File.ReadAllBytes(path);
    if (d.Length < 5) continue;
    int size = BitConverter.ToInt32(d, 0);
    try
    {
        var o = Lz.Decompress(d.AsSpan(4).ToArray(), size);
        File.WriteAllBytes(path, o);
        Console.WriteLine($"{o.Length,9} {d[4]:X2} {path}");
    }
    catch (Exception e) { Console.WriteLine($"FAIL {path}: {e.Message}"); }
}

class BitReader
{
    byte[] d; long pos; long len;
    public BitReader(byte[] data, int start) { d = data; pos = start * 8L; len = data.Length * 8L; }
    public uint Bit() { if (pos >= len) return 0; uint b = (uint)(d[pos >> 3] >> (int)(pos & 7)) & 1; pos++; return b; }
    public uint Bits(int n) { uint v = 0; for (int i = 0; i < n; i++) { if (pos >= len) break; v |= Bit() << i; } return v; }
}

class Tree
{
    const uint LEAF = 0x8000, THRESH = 0x7FFF, UNP = 0xFFFFFFFE;
    int nLeaves, total, root;
    uint[] freq, parent, child, sym;
    public Tree(int raw)
    {
        int n = (raw + 1) & 0xFFE; total = n * 2 - 1; root = total - 1; nLeaves = n;
        freq = new uint[total]; parent = new uint[total]; child = new uint[total]; sym = new uint[n];
        for (int i = 0; i < n; i++) { freq[i] = 1; child[i] = (uint)i | LEAF; sym[i] = (uint)i; }
        int j = 0;
        for (int k = n; k < total; k++) { freq[k] = freq[j] + freq[j + 1]; child[k] = (uint)j; parent[j] = parent[j + 1] = (uint)k; j += 2; }
        parent[root] = (uint)root;
    }
    public uint Decode(BitReader br)
    {
        uint v = child[root]; int depth = 0;
        while ((v & LEAF) == 0)
        {
            int idx = (int)(v + br.Bit());
            if (idx >= total) throw new Exception("tree index oob");
            v = child[idx];
            if (++depth > 50) throw new Exception("depth");
        }
        uint s = v & ~LEAF; Update(s); return s;
    }
    void Update(uint symbol)
    {
        if (freq[root] > THRESH) Rebuild();
        int i = (int)sym[symbol];
        while (true)
        {
            freq[i]++; uint f = freq[i];
            if (i + 1 < total && freq[i + 1] < f)
            {
                int j = i + 2;
                while (j < total && freq[j] < f) j++;
                j--;
                uint ci = child[i], cj = child[j];
                if ((cj & LEAF) == 0) { parent[cj] = (uint)i; parent[cj + 1] = (uint)i; } else sym[cj & ~LEAF] = (uint)i;
                if ((ci & LEAF) == 0) { parent[ci] = (uint)j; parent[ci + 1] = (uint)j; } else sym[ci & ~LEAF] = (uint)j;
                (freq[i], freq[j]) = (freq[j], freq[i]); freq[j] = f;
                (child[i], child[j]) = (child[j], child[i]);
                i = j;
            }
            int p = (int)parent[i];
            if (p == i || p == root) break;
            i = p;
        }
        freq[root]++;
    }
    void Rebuild()
    {
        int remaining = nLeaves;
        for (int i = 0; i < root; i++) { if ((child[i] & LEAF) != 0) freq[i] = (freq[i] + 1) >> 1; parent[i] = UNP; }
        parent[root] = UNP;
        uint pairScan = 0, nodeScan = 0;
        while (true)
        {
            bool found = false;
            while (pairScan < root) { if (parent[pairScan] == UNP) { found = true; break; } pairScan++; }
            if (!found || pairScan >= root) { parent[root] = 0xFFFFFFFF; return; }
            uint left = pairScan, right = pairScan + 1; pairScan += 2;
            while (nodeScan <= root)
            {
                if ((child[nodeScan] & LEAF) != 0) remaining--;
                else if (parent[nodeScan] == UNP) break;
                nodeScan++;
            }
            int slot = (int)nodeScan; nodeScan++; uint nextScan = nodeScan;
            uint nf = freq[left] + freq[right];
            int insertAt = slot;
            if (slot > 0 && freq[slot - 1] > nf)
            {
                int scan = slot - 1;
                while (scan >= 0 && freq[scan] > nf)
                {
                    uint c = child[scan];
                    if ((c & LEAF) == 0) { parent[c]++; parent[c + 1]++; } else sym[c & ~LEAF]++;
                    scan--;
                }
                int pos = scan + 1, count = slot - pos;
                if (count > 0) { Array.Copy(freq, pos, freq, pos + 1, count); Array.Copy(child, pos, child, pos + 1, count); }
                insertAt = pos;
            }
            else if (remaining > 0)
            {
                int scanPos = (int)nextScan, dst = slot; uint nxt = nextScan;
                while (true)
                {
                    while (scanPos < root) { if ((child[scanPos] & LEAF) != 0) break; scanPos++; }
                    if (scanPos >= root || freq[scanPos] >= nf) break;
                    freq[dst] = freq[scanPos]; child[dst] = child[scanPos]; parent[dst] = UNP;
                    sym[child[scanPos] & ~LEAF] = (uint)dst;
                    dst = (int)nxt; child[scanPos] = 0; nxt++; nodeScan = nxt;
                    remaining--;
                    if (remaining <= 0) break;
                    scanPos++;
                }
                insertAt = dst;
            }
            freq[insertAt] = nf; child[insertAt] = left; parent[left] = (uint)insertAt; parent[right] = (uint)insertAt;
        }
    }
}

static class Lz
{
    static readonly (uint b, int e)[] LT = { (0,0),(1,0),(2,0),(3,0),(4,0),(5,1),(7,1),(9,2),(13,2),(17,3),(25,3),(33,4),(49,4),(65,5),(97,5),(129,7) };
    static void Copy(List<byte> o, int dist, int len, int max)
    { for (int k = 0; k < len && o.Count < max; k++) o.Add(dist <= o.Count ? o[o.Count - dist] : (byte)0); }

    public static byte[] Decompress(byte[] c, int size)
    {
        int mode = (c[0] >> 4) & 3, level = c[0] & 0xF;
        if (level == 0) { var r = new byte[Math.Min(size, c.Length - 1)]; Array.Copy(c, 1, r, 0, r.Length); return r; }
        var (distBits, nDist, distExtra) = mode switch { 0 => (8, 16, 4), 1 => (12, 64, 6), 2 => (14, 64, 8), _ => throw new Exception("mode") };
        uint mask = (1u << distBits) - 1;
        var br = new BitReader(c, 1);
        var o = new List<byte>(size);
        if (level >= 7)
        {
            var lit = new Tree(273); var dt = new Tree(nDist);
            while (o.Count < size)
            {
                uint s = lit.Decode(br);
                if (s == 272) break;
                if (s > 272) throw new Exception("bad sym");
                if (s < 256) o.Add((byte)s);
                else
                {
                    var (b, e) = LT[s - 256];
                    int len = (int)(b + (e > 0 ? br.Bits(e) : 0) + 3);
                    uint ds = dt.Decode(br); uint dx = br.Bits(distExtra);
                    uint dist = ((ds << distExtra) | dx) & mask; if (dist == 0) dist = 1;
                    Copy(o, (int)dist, len, size);
                }
            }
        }
        else if (distBits == 8)
        {
            // Mode 0: 1-bit flags; match = 3 inline length bits (7 = escape byte) + 1-byte distance
            // Flag bits form one continuous stream, refilled 32 bits at a time from the byte stream.
            uint flags = 0; int left = 0;
            uint Flag() { if (left == 0) { flags = br.Bits(32); left = 32; } uint b = flags & 1; flags >>= 1; left--; return b; }
            while (o.Count < size)
            {
                if (Flag() == 0) { o.Add((byte)br.Bits(8)); continue; }
                uint lc = Flag() | Flag() << 1 | Flag() << 2;
                if (lc == 7) lc = br.Bits(8);
                uint dist = br.Bits(8); if (dist == 0) dist = 1;
                Copy(o, (int)dist, (int)lc + 3, size);
            }
        }
        else
        {
            uint lenOv = distBits < 16 ? (1u << (16 - distBits)) - 1 : 0;
            int shift = Math.Min(distBits, 15);
            while (o.Count < size)
            {
                uint flags = br.Bits(32); uint m = 1;
                while (m != 0 && o.Count < size)
                {
                    if ((flags & m) == 0)
                    {
                        m <<= 1; if (m == 0) break;
                        if ((flags & m) == 0) o.Add((byte)br.Bits(8));
                        else { int run = (int)br.Bits(8) + 3; for (int k = 0; k < run && o.Count < size; k++) o.Add((byte)br.Bits(8)); }
                        m <<= 1;
                    }
                    else
                    {
                        uint w = br.Bits(16); uint dist = w & mask; uint lc = (w >> shift) & lenOv;
                        if (lc == lenOv) lc = br.Bits(8);
                        if (dist == 0) dist = 1;
                        Copy(o, (int)dist, (int)lc + 3, size);
                        m <<= 1;
                    }
                }
            }
        }
        return o.ToArray();
    }
}
