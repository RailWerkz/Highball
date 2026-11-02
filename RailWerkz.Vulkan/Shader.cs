using RailWerkz.Core.IO;

namespace RailWerkz.Vulkan;

/// A SPIR-V module from Content/Shaders. GLSL sources there are compiled to .spv at build time (glslc).
public sealed class Shader : IDisposable
{
    public VkShaderModule Module { get; private set; }
    public string Name { get; }

    private Shader(VkShaderModule module, string name)
    {
        Module = module;
        Name = name;
    }

    /// name: the source file name, e.g. "terrain.vert" (loads terrain.vert.spv).
    public static Shader Load(string name)
    {
        var path = ContentPath.Get($"Shaders/{name}.spv");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Shader {name}: no {name}.spv (is the Vulkan SDK's glslc installed?)", path);

        var module = ShaderModuleExtensions.FromSpirv(Gpu.Device, File.ReadAllBytes(path), Gpu.Device.VkAllocator);
        return new Shader(module, name);
    }

    public void Dispose()
    {
        if (Module.IsNull())
            return;

        var module = Module;
        Module = default;
        Gpu.Defer(() => Gpu.Device.DestroyShaderModule(module, Gpu.Device.VkAllocator));
    }
}
