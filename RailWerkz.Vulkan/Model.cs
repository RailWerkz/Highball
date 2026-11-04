using System.Runtime.InteropServices;
using Brutal.GltfApi;
using Brutal.StbApi;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Utils;

namespace RailWerkz.Vulkan;

/// A glTF model flattened into one mesh: every triangle primitive placed by its node's world transform, grouped by
/// material into submeshes. Optionally rescaled to a real-world size and stood on y = 0, since the source files come
/// in all sorts of units and orientations. Each material keeps its base colour, embedded colour texture and alpha cutoff. Loaded
/// skinned, the vertices keep their bind pose and bone weights instead, and the model carries the Skeleton that bends
/// them (its Placement does the turning, centring and scaling).
public sealed class Model : IDisposable
{
    public readonly record struct SubMesh(int Material, int FirstIndex, int IndexCount);

    /// AlphaCutoff is 0 for an opaque material; for a cut-out one (leaves, grass cards) the alpha below which a pixel is
    /// thrown away.
    public readonly record struct Surface(float4 BaseColor, Texture2D? Texture, float AlphaCutoff);

    /// How the file's coordinates were turned, moved and scaled into the model's, so a part loaded on its own (a
    /// rotor, say) can be put in exactly the same place.
    public readonly record struct Frame(float3 Rotation, float3 Origin, float Scale);

    public Mesh Mesh { get; }
    public IReadOnlyList<SubMesh> SubMeshes { get; }
    public IReadOnlyList<Surface> Surfaces { get; }
    public float3 Min { get; }
    public float3 Max { get; }
    public float3 Size => Max - Min;
    public float3 Center => (Min + Max) * 0.5f;
    public Frame Placement { get; }

    /// Where the named node's own origin ended up, for a model loaded by node name: the pivot a turret turns about.
    public float3 Origin { get; private init; }

    /// The bones, for a model loaded skinned; null for a static one.
    public Skeleton? Skeleton { get; }

    private Model(Mesh mesh, List<SubMesh> subMeshes, List<Surface> surfaces, float3 min, float3 max, Frame placement, Skeleton? skeleton = null)
    {
        Placement = placement;
        Skeleton = skeleton;
        Mesh = mesh;
        SubMeshes = subMeshes;
        Surfaces = surfaces;
        Min = min;
        Max = max;
    }

    /// Loads Content/Models/relativePath. targetLength, if set, scales the model so its longest side is that many metres.
    /// preRotation (radians XYZ) turns the model before sizing, for files that aren't Y-up. nodeName picks one model
    /// out of a pack: only that node and its children are loaded. skipNode leaves a node and its children out (or several,
    /// comma-separated). frame,
    /// if given, places the model the way another part of the same file was placed instead of sizing it. skinned keeps
    /// the file's skins for animating (see Skeleton) rather than freezing them in their bind pose.
    public static Model Load(string relativePath, float targetLength = 0f, float3 preRotation = default, string? nodeName = null,
        string? skipNode = null, Frame? frame = null, bool skinned = false)
    {
        var path = ContentPath.Model(relativePath);
        using var loader = new GltfLoader(path);
        var gltf = loader.GltfJson;

        if (skinned && gltf.Skins is { Length: > 0 })
            return LoadSkinned(loader, relativePath, targetLength, preRotation, skipNode);

        var perMaterial = new SortedDictionary<int, (List<Vertex> Vertices, List<uint> Indices)>();

        var found = nodeName is null;
        var skip = SkipSet(skipNode);
        var origin = default(float3);

        void AddNode(int nodeIndex, float4x4 parent, bool include)
        {
            var node = gltf.Nodes[nodeIndex];
            var trs = float4x4.CreateScale(node.Scale) * float4x4.CreateFromQuaternion(node.Rotation) * float4x4.CreateTranslation(node.Translation);
            var world = node.Matrix * trs * parent;

            if (node.Name is { } name && skip.Contains(name))
                return;

            if (!include && node.Name == nodeName)
            {
                include = found = true;
                origin = new float3(world.M41, world.M42, world.M43);
            }

            if (include && node.Mesh is { } meshIndex)
            {
                // Skinned meshes are already in model space at their bind pose; their node transform doesn't apply.
                var placement = node.Skin is null ? world : float4x4.Identity;
                AddMesh(meshIndex, placement);
            }

            foreach (var child in node.Children ?? [])
                AddNode(child, world, include);
        }

        void AddMesh(int meshIndex, float4x4 world)
        {
            foreach (var primitive in gltf.Meshes![meshIndex].Primitives)
            {
                if (primitive.Mode != MeshPrimitive.ModeEnum.Triangles)
                    continue;

                var positions = ReadVec(loader, primitive.Attributes["POSITION"], 3);
                var count = positions.Length / 3;
                var normals = primitive.Attributes.TryGetValue("NORMAL", out var n) ? ReadVec(loader, n, 3) : null;
                var uvs = primitive.Attributes.TryGetValue("TEXCOORD_0", out var t) ? ReadVec(loader, t, 2) : null;
                var ids = primitive.Indices is { } i ? ReadIndices(loader, i) : [.. Enumerable.Range(0, count).Select(x => (uint)x)];

                var material = primitive.Material ?? 0;
                if (!perMaterial.TryGetValue(material, out var bucket))
                    perMaterial[material] = bucket = ([], []);

                var baseVertex = (uint)bucket.Vertices.Count;

                for (var v = 0; v < count; v++)
                {
                    var p = MathEx.TransformPoint(new float3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]), world);
                    var nn = normals is null
                        ? new float3(0f, 1f, 0f)
                        : MathEx.Normalize(MathEx.TransformDirection(new float3(normals[v * 3], normals[v * 3 + 1], normals[v * 3 + 2]), world));
                    var uv = uvs is null ? default : new float2(uvs[v * 2], uvs[v * 2 + 1]);
                    bucket.Vertices.Add(new Vertex(p, nn, uv));
                }

                foreach (var id in ids)
                    bucket.Indices.Add(baseVertex + id);
            }
        }

        if ((gltf.Scene ?? (gltf.Scenes?.Length > 0 ? 0 : null)) is { } scene)
        {
            foreach (var root in gltf.Scenes![scene].Nodes ?? [])
                AddNode(root, float4x4.Identity, nodeName is null);
        }
        else
        {
            for (var m = 0; m < (gltf.Meshes?.Length ?? 0); m++)
                AddMesh(m, float4x4.Identity);
        }

        // Merge buckets into one buffer, one submesh per material.
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        var subMeshes = new List<SubMesh>();

        foreach (var (material, bucket) in perMaterial)
        {
            var offset = (uint)vertices.Count;
            subMeshes.Add(new SubMesh(material, indices.Count, bucket.Indices.Count));
            vertices.AddRange(bucket.Vertices);
            indices.AddRange(bucket.Indices.Select(x => x + offset));
        }

        if (!found)
            throw new InvalidDataException($"{relativePath} has no node named {nodeName}");

        if (vertices.Count == 0)
            throw new InvalidDataException($"{relativePath} has no triangles");

        var surfaces = LoadSurfaces(loader, perMaterial.Keys.DefaultIfEmpty().Max() + 1);

        if (frame is { } f)
            preRotation = f.Rotation;

        var rotate = MathEx.RotationZ(preRotation.Z) * MathEx.RotationX(preRotation.X) * MathEx.RotationY(preRotation.Y);
        var array = CollectionsMarshal.AsSpan(vertices);

        for (var v = 0; v < array.Length; v++)
        {
            array[v].Position = MathEx.TransformPoint(array[v].Position, rotate);
            array[v].Normal = MathEx.Normalize(MathEx.TransformDirection(array[v].Normal, rotate));
        }

        var min = new float3(float.MaxValue);
        var max = new float3(float.MinValue);

        foreach (var v in array)
        {
            min = float3.Min(min, v.Position);
            max = float3.Max(max, v.Position);
        }

        var placement = new Frame(preRotation, default, 1f);

        if (targetLength > 0f || frame is not null)
        {
            // Centre on x/z, stand on the ground, scale to size.
            var size = max - min;
            var scale = frame?.Scale ?? targetLength / MathF.Max(size.X, MathF.Max(size.Y, size.Z));
            var centre = frame?.Origin ?? new float3((min.X + max.X) * 0.5f, min.Y, (min.Z + max.Z) * 0.5f);
            placement = new Frame(preRotation, centre, scale);

            for (var v = 0; v < array.Length; v++)
                array[v].Position = (array[v].Position - centre) * scale;

            min = (min - centre) * scale;
            max = (max - centre) * scale;
        }

        var label = nodeName is null ? relativePath : $"{relativePath}#{nodeName}";
        Log.Info($"Model {label}: {vertices.Count} vertices, {indices.Count / 3} triangles, {subMeshes.Count} materials, size {max - min}");

        var mesh = Mesh.Create(array, CollectionsMarshal.AsSpan(indices));
        mesh.BoundsMin = min;
        mesh.BoundsMax = max;
        var pivot = MathEx.TransformPoint(origin, rotate);
        if (targetLength > 0f || frame is not null)
            pivot = (pivot - placement.Origin) * placement.Scale;

        return new Model(mesh, subMeshes, surfaces, min, max, placement) { Origin = pivot };
    }

    private static HashSet<string> SkipSet(string? skipNode) =>
        skipNode is null ? [] : [.. skipNode.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

    // The skinned path: bones from the skins' joints and the nodes above them, vertices kept at bind pose with up to
    // four weights each into one shared palette. Each mesh's own node transform is folded into its vertices (and out
    // of its inverse bind matrices again), so files that scale a mesh unevenly still get true normals. Meshes that
    // aren't skinned ride the bone above them, or the model itself.
    private static Model LoadSkinned(GltfLoader loader, string relativePath, float targetLength, float3 preRotation, string? skipNode)
    {
        var gltf = loader.GltfJson;
        var nodes = gltf.Nodes;
        var skip = SkipSet(skipNode);

        var parents = new int[nodes.Length];
        Array.Fill(parents, -1);
        for (var n = 0; n < nodes.Length; n++)
        {
            foreach (var child in nodes[n].Children ?? [])
                parents[child] = n;
        }

        var needed = new bool[nodes.Length];
        var isJoint = new bool[nodes.Length];
        foreach (var skin in gltf.Skins!)
        {
            foreach (var joint in skin.Joints)
            {
                isJoint[joint] = true;
                for (var j = joint; j >= 0 && !needed[j]; j = parents[j])
                    needed[j] = true;
            }
        }

        var boneOf = new Dictionary<int, int>();
        var bones = new List<Bone>();

        void AddBones(int nodeIndex, int parentBone)
        {
            if (!needed[nodeIndex])
                return;

            var node = nodes[nodeIndex];
            var local = node.Matrix * float4x4.CreateScale(node.Scale) * float4x4.CreateFromQuaternion(node.Rotation) * float4x4.CreateTranslation(node.Translation);
            float4x4.Decompose(local, out var scale, out var rotation, out var translation);
            boneOf[nodeIndex] = bones.Count;
            bones.Add(new Bone(node.Name ?? $"node{nodeIndex}", parentBone, new BoneTransform(translation, Quat.Normalize(rotation), scale), isJoint[nodeIndex]));

            foreach (var child in node.Children ?? [])
                AddBones(child, boneOf[nodeIndex]);
        }

        var roots = gltf.Scenes is { Length: > 0 } scenes ? scenes[gltf.Scene ?? 0].Nodes ?? [] : [];
        foreach (var root in roots)
            AddBones(root, -1);

        var joints = new List<SkinJoint>();
        var skeleton = new Skeleton(bones, joints, LoadClips(loader, boneOf));
        var restGlobals = new float4x4[bones.Count];
        skeleton.Globals(skeleton.Rest, restGlobals);

        int JointFor(int bone, in float4x4 inverseBind)
        {
            for (var i = 0; i < joints.Count; i++)
            {
                if (joints[i].Bone == bone && Close(joints[i].InverseBind, inverseBind))
                    return i;
            }

            joints.Add(new SkinJoint(bone, inverseBind));
            return joints.Count - 1;
        }

        var perMaterial = new SortedDictionary<int, (List<SkinnedVertex> Vertices, List<uint> Indices)>();

        void AddNode(int nodeIndex, float4x4 parent)
        {
            var node = nodes[nodeIndex];
            if (node.Name is { } name && skip.Contains(name))
                return;

            var world = node.Matrix * float4x4.CreateScale(node.Scale) * float4x4.CreateFromQuaternion(node.Rotation) * float4x4.CreateTranslation(node.Translation) * parent;

            if (node.Mesh is { } meshIndex)
            {
                var inverse = Invert(world);
                var normalMatrix = float4x4.Transpose(inverse);
                var map = new Dictionary<int, int>();
                Func<int, int> joint;

                if (node.Skin is { } skinIndex)
                {
                    var skin = gltf.Skins![skinIndex];
                    var inverseBinds = skin.InverseBindMatrices is { } ibm ? ReadMatrices(loader, ibm) : null;
                    joint = k =>
                    {
                        if (!map.TryGetValue(k, out var j))
                            map[k] = j = JointFor(boneOf[skin.Joints[k]], inverse * (inverseBinds?[k] ?? float4x4.Identity));
                        return j;
                    };
                }
                else
                {
                    // Not skinned: carried by the nearest bone above it, or by the model as a whole.
                    var above = parents[nodeIndex];
                    while (above >= 0 && !boneOf.ContainsKey(above))
                        above = parents[above];

                    var fixedJoint = above < 0 ? JointFor(-1, float4x4.Identity) : JointFor(boneOf[above], Invert(restGlobals[boneOf[above]]));
                    joint = _ => fixedJoint;
                }

                AddMesh(meshIndex, world, normalMatrix, joint, node.Skin is not null);
            }

            foreach (var child in node.Children ?? [])
                AddNode(child, world);
        }

        void AddMesh(int meshIndex, float4x4 world, float4x4 normalMatrix, Func<int, int> joint, bool weighted)
        {
            foreach (var primitive in gltf.Meshes![meshIndex].Primitives)
            {
                if (primitive.Mode != MeshPrimitive.ModeEnum.Triangles)
                    continue;

                var positions = ReadVec(loader, primitive.Attributes["POSITION"], 3);
                var count = positions.Length / 3;
                var normals = primitive.Attributes.TryGetValue("NORMAL", out var n) ? ReadVec(loader, n, 3) : null;
                var uvs = primitive.Attributes.TryGetValue("TEXCOORD_0", out var t) ? ReadVec(loader, t, 2) : null;
                var jointIds = weighted && primitive.Attributes.TryGetValue("JOINTS_0", out var ja) ? ReadVec(loader, ja, 4) : null;
                var weights = weighted && primitive.Attributes.TryGetValue("WEIGHTS_0", out var wa) ? ReadVec(loader, wa, 4) : null;
                var ids = primitive.Indices is { } i ? ReadIndices(loader, i) : [.. Enumerable.Range(0, count).Select(x => (uint)x)];

                var material = primitive.Material ?? 0;
                if (!perMaterial.TryGetValue(material, out var bucket))
                    perMaterial[material] = bucket = ([], []);

                var baseVertex = (uint)bucket.Vertices.Count;

                for (var v = 0; v < count; v++)
                {
                    var p = MathEx.TransformPoint(new float3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]), world);
                    var nn = normals is null
                        ? new float3(0f, 1f, 0f)
                        : MathEx.Normalize(MathEx.TransformDirection(new float3(normals[v * 3], normals[v * 3 + 1], normals[v * 3 + 2]), normalMatrix));
                    var uv = uvs is null ? default : new float2(uvs[v * 2], uvs[v * 2 + 1]);

                    var ji = new float4(joint(0), 0f, 0f, 0f);
                    var wi = new float4(1f, 0f, 0f, 0f);
                    if (jointIds is not null && weights is not null)
                    {
                        var w = new float4(MathF.Max(0f, weights[v * 4]), MathF.Max(0f, weights[v * 4 + 1]), MathF.Max(0f, weights[v * 4 + 2]), MathF.Max(0f, weights[v * 4 + 3]));
                        var sum = w.X + w.Y + w.Z + w.W;
                        float Id(int k) => w[k] > 0f ? joint((int)jointIds[v * 4 + k]) : 0f;
                        ji = new float4(Id(0), Id(1), Id(2), Id(3));
                        wi = sum > 0f ? w / sum : wi;
                    }

                    bucket.Vertices.Add(new SkinnedVertex(p, nn, uv, ji, wi));
                }

                foreach (var id in ids)
                    bucket.Indices.Add(baseVertex + id);
            }
        }

        foreach (var root in roots)
            AddNode(root, float4x4.Identity);

        if (joints.Count > Skeleton.MaxJoints)
            throw new InvalidDataException($"{relativePath} needs {joints.Count} joints, more than the {Skeleton.MaxJoints} the shader takes");

        var vertices = new List<SkinnedVertex>();
        var indices = new List<uint>();
        var subMeshes = new List<SubMesh>();

        foreach (var (material, bucket) in perMaterial)
        {
            var offset = (uint)vertices.Count;
            subMeshes.Add(new SubMesh(material, indices.Count, bucket.Indices.Count));
            vertices.AddRange(bucket.Vertices);
            indices.AddRange(bucket.Indices.Select(x => x + offset));
        }

        if (vertices.Count == 0)
            throw new InvalidDataException($"{relativePath} has no triangles");

        // Size and centre it the way the static path would, from the rest pose rather than the bind pose.
        var palette = new float4x4[joints.Count];
        skeleton.Palette(restGlobals, palette);
        var rotate = MathEx.RotationZ(preRotation.Z) * MathEx.RotationX(preRotation.X) * MathEx.RotationY(preRotation.Y);
        var min = new float3(float.MaxValue);
        var max = new float3(float.MinValue);

        foreach (var v in vertices)
        {
            var p = default(float3);
            for (var k = 0; k < 4; k++)
            {
                if (v.Weights[k] > 0f)
                    p += MathEx.TransformPoint(v.Position, palette[(int)v.Joints[k]]) * v.Weights[k];
            }

            p = MathEx.TransformPoint(p, rotate);
            min = float3.Min(min, p);
            max = float3.Max(max, p);
        }

        var placement = new Frame(preRotation, default, 1f);
        var placementMatrix = rotate;

        if (targetLength > 0f)
        {
            var size = max - min;
            var scale = targetLength / MathF.Max(size.X, MathF.Max(size.Y, size.Z));
            var centre = new float3((min.X + max.X) * 0.5f, min.Y, (min.Z + max.Z) * 0.5f);
            placement = new Frame(preRotation, centre, scale);
            placementMatrix = rotate * MathEx.Translation(-centre) * MathEx.Scale(new float3(scale));
            min = (min - centre) * scale;
            max = (max - centre) * scale;
        }

        skeleton.Placement = placementMatrix;

        var surfaces = LoadSurfaces(loader, perMaterial.Keys.DefaultIfEmpty().Max() + 1);
        Log.Info($"Model {relativePath} (skinned): {vertices.Count} vertices, {indices.Count / 3} triangles, {bones.Count} bones, {joints.Count} joints, {skeleton.Clips.Count} clips, size {max - min}");

        var mesh = Mesh.Create<SkinnedVertex>(CollectionsMarshal.AsSpan(vertices), Mesh.SkinnedLayout, CollectionsMarshal.AsSpan(indices));
        mesh.BoundsMin = min;
        mesh.BoundsMax = max;
        return new Model(mesh, subMeshes, surfaces, min, max, placement, skeleton);
    }

    // The file's animations, for the bones this model kept.
    private static List<EmbeddedClip> LoadClips(GltfLoader loader, Dictionary<int, int> boneOf)
    {
        var clips = new List<EmbeddedClip>();
        var animations = loader.GltfJson.Animations ?? [];

        for (var a = 0; a < animations.Length; a++)
        {
            var animation = animations[a];
            var channels = new List<EmbeddedClip.Channel>();
            var duration = 0f;

            foreach (var channel in animation.Channels)
            {
                if (channel.Target.Node is not { } node || !boneOf.TryGetValue(node, out var bone))
                    continue;

                EmbeddedClip.Path? target = channel.Target.Path.ToString().ToLowerInvariant() switch
                {
                    "translation" => EmbeddedClip.Path.Translation,
                    "rotation" => EmbeddedClip.Path.Rotation,
                    "scale" => EmbeddedClip.Path.Scale,
                    _ => null,
                };

                if (target is not { } path)
                    continue;

                var sampler = animation.Samplers[channel.Sampler];
                var times = ReadVec(loader, sampler.Input, 1);
                var width = path == EmbeddedClip.Path.Rotation ? 4 : 3;
                var values = ReadVec(loader, sampler.Output, width);

                // Cubic splines key an in-tangent, the value and an out-tangent: keep the values.
                if (sampler.Interpolation.ToString().ToUpperInvariant() == "CUBICSPLINE")
                    values = [.. Enumerable.Range(0, times.Length).SelectMany(k => values.AsSpan((k * 3 + 1) * width, width).ToArray())];

                channels.Add(new EmbeddedClip.Channel(bone, path, times, values));
                duration = MathF.Max(duration, times.Length > 0 ? times[^1] : 0f);
            }

            if (channels.Count > 0)
                clips.Add(new EmbeddedClip { Name = animation.Name ?? $"clip{a}", Duration = duration, Channels = channels });
        }

        return clips;
    }

    private static float4x4 Invert(in float4x4 m)
    {
        float4x4.Invert(m, out var inverse);
        return inverse;
    }

    private static bool Close(in float4x4 a, in float4x4 b)
    {
        var x = a.AsReadOnlySpan();
        var y = b.AsReadOnlySpan();
        for (var i = 0; i < 16; i++)
        {
            if (MathF.Abs(x[i] - y[i]) > 1e-4f * MathF.Max(1f, MathF.Abs(x[i])))
                return false;
        }

        return true;
    }

    private static float4x4[] ReadMatrices(GltfLoader loader, int accessorIndex)
    {
        var values = ReadVec(loader, accessorIndex, 16);
        return [.. Enumerable.Range(0, values.Length / 16).Select(i => new float4x4(values.AsSpan(i * 16, 16)))];
    }


    // Base colour factor, texture and alpha cutoff per material; textures are embedded images decoded with stb. Blended
    // materials are cut out too: nothing in the game sorts its triangles, and they're nearly all leaves and grass.
    private static List<Surface> LoadSurfaces(GltfLoader loader, int count)
    {
        var gltf = loader.GltfJson;
        var surfaces = new List<Surface>(count);
        var images = new Dictionary<int, Texture2D?>();

        for (var m = 0; m < count; m++)
        {
            var material = gltf.Materials is { } materials && m < materials.Length ? materials[m] : null;
            var pbr = material?.PbrMetallicRoughness;
            float4 factor = pbr?.BaseColorFactor ?? new float4(1f);
            Texture2D? texture = null;

            if (pbr?.BaseColorTexture is { } info && gltf.Textures?[info.Index].Source is { } source)
            {
                if (!images.TryGetValue(source, out texture))
                    images[source] = texture = LoadImage(loader, source);
            }

            var cutoff = material is null || material.AlphaMode == Material.AlphaModeEnum.Opaque ? 0f : material.AlphaCutoff;
            surfaces.Add(new Surface(factor, texture, cutoff));
        }

        return surfaces;
    }

    private static Texture2D? LoadImage(GltfLoader loader, int imageIndex)
    {
        try
        {
            if (loader.GltfJson.Images![imageIndex].BufferView is not { } view)
                return null;

            using var image = Stb.LoadFromMemory(loader.GetBufferView(view), 4);
            return Texture2D.FromPixels(image.Width, image.Height, MemoryMarshal.AsBytes(image.AsSpan<byte4>()), true, true, srgb: true, name: $"gltf.image{imageIndex}");
        }
        catch (Exception e)
        {
            Log.Warn($"Model image {imageIndex}: {e.Message}");
            return null;
        }
    }

    // Floats, or integers (as their values, or scaled to 0-1 when the accessor is normalised).
    /// One named piece of a model kept on the CPU: its triangles in model space (node transforms applied) and where its
    /// node's origin is, for a part that's moved about it.
    public sealed record Part(string Name, Vertex[] Vertices, uint[] Indices, float3 Origin);

    /// Every node with a mesh in a glTF file, by name, as CPU-side triangles: for models taken apart and built into other
    /// meshes (bent along track, posed piece by piece) rather than drawn whole.
    public static Dictionary<string, Part> LoadParts(string relativePath)
    {
        using var loader = new GltfLoader(ContentPath.Model(relativePath));
        var gltf = loader.GltfJson;
        var parts = new Dictionary<string, Part>();

        void AddNode(int nodeIndex, float4x4 parent)
        {
            var node = gltf.Nodes[nodeIndex];
            var trs = float4x4.CreateScale(node.Scale) * float4x4.CreateFromQuaternion(node.Rotation) * float4x4.CreateTranslation(node.Translation);
            var world = node.Matrix * trs * parent;

            if (node.Mesh is { } meshIndex)
            {
                var vertices = new List<Vertex>();
                var indices = new List<uint>();
                foreach (var primitive in gltf.Meshes![meshIndex].Primitives)
                {
                    if (primitive.Mode != MeshPrimitive.ModeEnum.Triangles)
                        continue;
                    var positions = ReadVec(loader, primitive.Attributes["POSITION"], 3);
                    var count = positions.Length / 3;
                    var normals = primitive.Attributes.TryGetValue("NORMAL", out var n) ? ReadVec(loader, n, 3) : null;
                    var uvs = primitive.Attributes.TryGetValue("TEXCOORD_0", out var t) ? ReadVec(loader, t, 2) : null;
                    var ids = primitive.Indices is { } i ? ReadIndices(loader, i) : [.. Enumerable.Range(0, count).Select(x => (uint)x)];
                    var first = (uint)vertices.Count;
                    for (var v = 0; v < count; v++)
                    {
                        var p = MathEx.TransformPoint(new float3(positions[v * 3], positions[v * 3 + 1], positions[v * 3 + 2]), world);
                        var nn = normals is null
                            ? new float3(0f, 1f, 0f)
                            : MathEx.Normalize(MathEx.TransformDirection(new float3(normals[v * 3], normals[v * 3 + 1], normals[v * 3 + 2]), world));
                        vertices.Add(new Vertex(p, nn, uvs is null ? default : new float2(uvs[v * 2], uvs[v * 2 + 1])));
                    }

                    indices.AddRange(ids.Select(id => first + id));
                }

                var name = node.Name ?? $"node{nodeIndex}";
                parts[name] = new Part(name, [.. vertices], [.. indices], new float3(world.M41, world.M42, world.M43));
            }

            foreach (var child in node.Children ?? [])
                AddNode(child, world);
        }

        if ((gltf.Scene ?? (gltf.Scenes?.Length > 0 ? 0 : null)) is { } scene)
            foreach (var root in gltf.Scenes![scene].Nodes ?? [])
                AddNode(root, float4x4.Identity);

        Log.Info($"Model parts {relativePath}: {string.Join(", ", parts.Keys)}");
        return parts;
    }

    private static float[] ReadVec(GltfLoader loader, int accessorIndex, int components)
    {
        var accessor = loader.GltfJson.Accessors[accessorIndex];
        var view = loader.GltfJson.BufferViews[accessor.BufferView!.Value];
        var bytes = loader.GetBufferView(accessor.BufferView.Value)[accessor.ByteOffset..];
        var normalized = accessor.Normalized == true;
        var (size, scale) = accessor.ComponentType switch
        {
            Accessor.ComponentTypeEnum.Float => (4, 1f),
            Accessor.ComponentTypeEnum.UnsignedByte => (1, normalized ? 1f / 255f : 1f),
            Accessor.ComponentTypeEnum.UnsignedShort => (2, normalized ? 1f / 65535f : 1f),
            _ => throw new NotSupportedException($"Vertex data of {accessor.ComponentType} isn't supported"),
        };
        var stride = view.ByteStride ?? components * size;
        var result = new float[accessor.Count * components];

        for (var i = 0; i < accessor.Count; i++)
        {
            for (var c = 0; c < components; c++)
            {
                var at = bytes[(i * stride + c * size)..];
                result[i * components + c] = size switch
                {
                    4 => MemoryMarshal.Read<float>(at),
                    2 => MemoryMarshal.Read<ushort>(at) * scale,
                    _ => at[0] * scale,
                };
            }
        }

        return result;
    }

    private static uint[] ReadIndices(GltfLoader loader, int accessorIndex)
    {
        var accessor = loader.GltfJson.Accessors[accessorIndex];
        var bytes = loader.GetBufferView(accessor.BufferView!.Value)[accessor.ByteOffset..];

        return accessor.ComponentType switch
        {
            Accessor.ComponentTypeEnum.UnsignedByte => [.. bytes[..accessor.Count].ToArray().Select(b => (uint)b)],
            Accessor.ComponentTypeEnum.UnsignedShort => [.. MemoryMarshal.Cast<byte, ushort>(bytes)[..accessor.Count].ToArray().Select(s => (uint)s)],
            _ => MemoryMarshal.Cast<byte, uint>(bytes)[..accessor.Count].ToArray(),
        };
    }

    public void Dispose()
    {
        Mesh.Dispose();
        foreach (var texture in Surfaces.Select(s => s.Texture).OfType<Texture2D>().Distinct())
            texture.Dispose();
    }
}
