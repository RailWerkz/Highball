using System.Security.Cryptography;
using System.Text;
using Brutal;
using Brutal.MeshOptimizer;
using Brutal.Pointers;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;

namespace RailWerkz.Vulkan;

/// A loaded model's triangles made cheaper to draw (meshoptimizer), as it's loaded:
/// - each material's triangles reordered for the GPU's vertex cache and then against overdraw, and the vertices reordered to the
///   order they're first used in (vertex fetch);
/// - lower-detail versions (Levels) of a dense model, each a share of its triangles chosen to keep its shape and its seams
///   (positions, normals and uvs where they part), as more index ranges into the same vertices: they cost no vertices or textures.
/// What's worked out is kept in UserData/MeshCache, keyed to the file (its path, size and time) and how it was loaded, so it's only
/// done once per change of the file.
public static unsafe class MeshOptimize
{
    /// A level: its share of the full model's triangles, and the most it may be off (as a share of the model's size) to get there.
    private static readonly (float Share, float Error)[] Levels = [(0.5f, 0.004f), (0.2f, 0.012f), (0.06f, 0.035f)];

    /// Models with fewer triangles get no lower-detail versions: too few to be worth it.
    public const int LodFloor = 4000;

    private const int CacheVersion = 1;

    /// One lower-detail version: its submeshes (into the shared index buffer), and how far off it is, as a share of the model's size.
    public sealed record Level(List<Model.SubMesh> SubMeshes, float Error);

    /// Reorders vertices and indices in place (vertices may come out fewer: any no triangle uses are dropped), and appends each
    /// lower-detail version's indices after the full model's. key: what decides the result besides the file (node names), for the cache.
    public static List<Level> Process(string file, string key, List<Vertex> vertices, List<uint> indices, List<Model.SubMesh> subMeshes, bool lods)
    {
        var path = CachePath(file, key, vertices.Count, indices.Count, lods);
        if (path is not null && TryRead(path, vertices, indices, subMeshes, out var cached))
            return cached;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var levels = new List<Level>();
        var vertexCount = vertices.Count;
        var vertexArray = vertices.ToArray();
        var indexArray = indices.ToArray();
        fixed (Vertex* v = vertexArray)
        fixed (uint* all = indexArray)
        {
            var positions = (float*)v;
            var stride = new ByteSize64((ulong)sizeof(Vertex));

            // Each material's run: for the vertex cache, then against overdraw (within 5% of the cache's best).
            foreach (var sub in subMeshes)
            {
                if (sub.IndexCount < 3)
                    continue;
                var run = (int*)(all + sub.FirstIndex);
                MeshOptimizer.OptimizeVertexCache(run, run, sub.IndexCount, vertexCount);
                MeshOptimizer.OptimizeOverdraw(run, run, sub.IndexCount, positions, vertexCount, stride, 1.05f);
            }

            var extra = new List<uint>();
            if (lods && indexArray.Length / 3 >= LodFloor)
            {
                foreach (var (share, error) in Levels)
                {
                    var subs = new List<Model.SubMesh>();
                    var worst = 0f;
                    foreach (var sub in subMeshes)
                    {
                        var target = Math.Max(3, (long)(sub.IndexCount * share) / 3 * 3);
                        var output = new uint[Math.Max(sub.IndexCount, 3)];
                        long count;
                        var result = 0f;
                        fixed (uint* o = output)
                        {
                            count = sub.IndexCount < 3 ? 0 : (long)MeshOptimizer.Simplify((int*)o, (int*)(all + sub.FirstIndex), sub.IndexCount, positions, vertexCount, stride, target, error, 0, &result);
                            if (count >= 3)
                                MeshOptimizer.OptimizeVertexCache((int*)o, (int*)o, count, vertexCount);
                        }

                        worst = MathF.Max(worst, count >= 3 ? result : 0f);
                        subs.Add(new Model.SubMesh(sub.Material, indexArray.Length + extra.Count, (int)count));
                        for (var i = 0; i < count; i++)
                            extra.Add(output[i]);
                    }

                    levels.Add(new Level(subs, worst));
                }
            }

            // Every index (the full model's, then the levels'), and the vertices in the order they're first used.
            var combined = new uint[indexArray.Length + extra.Count];
            indexArray.CopyTo(combined, 0);
            extra.CopyTo(combined, indexArray.Length);
            var remap = new uint[vertexCount];
            long unique;
            fixed (uint* c = combined)
            fixed (uint* r = remap)
            {
                unique = (long)MeshOptimizer.OptimizeVertexFetchRemap((int*)r, (int*)c, combined.Length, vertexCount);
                MeshOptimizer.RemapIndexBuffer((int*)c, (int*)c, combined.Length, (int*)r);
            }

            Apply(vertices, remap, (int)unique);
            indices.Clear();
            indices.AddRange(combined);
            if (path is not null)
                Write(path, remap, (int)unique, combined, subMeshes, levels);
        }

        var shares = string.Join(", ", levels.Select(l => $"{l.SubMeshes.Sum(s => s.IndexCount) / 3}"));
        Log.Info($"Mesh {file}{(key.Length > 0 ? "#" + key : "")}: optimized{(levels.Count > 0 ? $", detail levels of {shares} triangles" : "")} in {clock.ElapsedMilliseconds} ms");
        return levels;
    }

    /// The vertices moved to where remap says (old to new; ~0 for one no triangle uses).
    private static void Apply(List<Vertex> vertices, uint[] remap, int unique)
    {
        var moved = new Vertex[unique];
        for (var i = 0; i < remap.Length && i < vertices.Count; i++)
            if (remap[i] != uint.MaxValue)
                moved[remap[i]] = vertices[i];
        vertices.Clear();
        vertices.AddRange(moved);
    }

    // --- The cache. ---

    private static string? CachePath(string file, string key, int vertexCount, int indexCount, bool lods)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
                return null;
            var text = $"{CacheVersion}|{Path.GetFullPath(file).ToLowerInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{key}|{vertexCount}|{indexCount}|{lods}";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
            return Path.Combine(ContentPath.UserData, "MeshCache", hash + ".bin");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void Write(string path, uint[] remap, int unique, uint[] indices, List<Model.SubMesh> subMeshes, List<Level> levels)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            using (var w = new BinaryWriter(File.Create(temp)))
            {
                w.Write(CacheVersion);
                w.Write(remap.Length);
                w.Write(unique);
                foreach (var r in remap)
                    w.Write(r);
                w.Write(indices.Length);
                foreach (var i in indices)
                    w.Write(i);
                void Subs(List<Model.SubMesh> subs)
                {
                    w.Write(subs.Count);
                    foreach (var s in subs)
                    {
                        w.Write(s.Material);
                        w.Write(s.FirstIndex);
                        w.Write(s.IndexCount);
                    }
                }

                Subs(subMeshes);
                w.Write(levels.Count);
                foreach (var level in levels)
                {
                    w.Write(level.Error);
                    Subs(level.SubMeshes);
                }
            }

            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Mesh cache: couldn't write {path}: {e.Message}");
        }
    }

    private static bool TryRead(string path, List<Vertex> vertices, List<uint> indices, List<Model.SubMesh> subMeshes, out List<Level> levels)
    {
        levels = [];
        if (!File.Exists(path))
            return false;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != CacheVersion || r.ReadInt32() != vertices.Count)
                return false;
            var unique = r.ReadInt32();
            var remap = new uint[vertices.Count];
            for (var i = 0; i < remap.Length; i++)
                remap[i] = r.ReadUInt32();
            var count = r.ReadInt32();
            var read = new uint[count];
            for (var i = 0; i < count; i++)
                read[i] = r.ReadUInt32();
            List<Model.SubMesh> Subs()
            {
                var n = r.ReadInt32();
                var subs = new List<Model.SubMesh>(n);
                for (var i = 0; i < n; i++)
                    subs.Add(new Model.SubMesh(r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
                return subs;
            }

            var subsRead = Subs();
            if (subsRead.Count != subMeshes.Count || subsRead.Where((s, i) => s.Material != subMeshes[i].Material).Any())
                return false;
            var levelCount = r.ReadInt32();
            for (var k = 0; k < levelCount; k++)
            {
                var error = r.ReadSingle();
                levels.Add(new Level(Subs(), error));
            }

            Apply(vertices, remap, unique);
            indices.Clear();
            indices.AddRange(read);
            subMeshes.Clear();
            subMeshes.AddRange(subsRead);
            return true;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            Log.Warn($"Mesh cache: {path} unreadable ({e.Message}); worked out again");
            levels = [];
            return false;
        }
    }
}
