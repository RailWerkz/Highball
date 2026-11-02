namespace RailWerkz.Vulkan;

/// Textures bound for a fragment shader: a descriptor set (from a pool of its own) holding a combined image sampler per
/// texture, at set 0, bindings 0, 1, 2... Pipelines that sample textures this way take SetLayoutFor(count) in their
/// pipeline layout.
public sealed unsafe class TextureBinding : IDisposable
{
    private static readonly Dictionary<int, VkDescriptorSetLayout> Layouts = [];

    private VkDescriptorPool _pool;

    public VkDescriptorSet Set { get; }

    /// The layout of a set of one texture.
    public static VkDescriptorSetLayout SetLayout => SetLayoutFor(1);

    /// The layout of a set of count textures. Made on first use and kept for the device's life.
    public static VkDescriptorSetLayout SetLayoutFor(int count)
    {
        if (Layouts.TryGetValue(count, out var layout))
            return layout;

        var bindings = stackalloc VkDescriptorSetLayoutBinding[count];
        for (var i = 0; i < count; i++)
        {
            bindings[i] = new VkDescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = VkDescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = VkShaderStageFlags.FragmentBit,
            };
        }

        var info = new VkDescriptorSetLayoutCreateInfo { BindingCount = count, Bindings = bindings };
        layout = Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);
        Layouts[count] = layout;
        return layout;
    }

    public TextureBinding(params Texture2D[] textures)
    {
        var count = textures.Length;
        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = count };
        var pool = new VkDescriptorPoolCreateInfo { MaxSets = 1, PoolSizeCount = 1, PoolSizes = &size };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);

        var layout = SetLayoutFor(count);
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        Set = sets[0];

        var images = new VkDescriptorImageInfo[count];
        var writes = new VkWriteDescriptorSet[count];
        fixed (VkDescriptorImageInfo* infos = images)
        {
            for (var i = 0; i < count; i++)
            {
                images[i] = new VkDescriptorImageInfo { Sampler = textures[i].Sampler, ImageView = textures[i].View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
                writes[i] = new VkWriteDescriptorSet
                {
                    DstSet = Set,
                    DstBinding = i,
                    DescriptorCount = 1,
                    DescriptorType = VkDescriptorType.CombinedImageSampler,
                    ImageInfo = infos + i,
                };
            }

            Gpu.Device.UpdateDescriptorSets(writes, []);
        }
    }

    public void Bind(CommandBuffer cmd, VkPipelineLayout layout) =>
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, 0, [Set], []);

    public void Dispose()
    {
        if (_pool.IsNull())
            return;

        var pool = _pool;
        _pool = default;
        Gpu.Defer(() => Gpu.Device.DestroyDescriptorPool(pool, Gpu.Device.VkAllocator));
    }

    /// Releases the shared set layouts; call once at shutdown, after every binding is gone.
    public static void Shutdown()
    {
        foreach (var layout in Layouts.Values)
            Gpu.Device.DestroyDescriptorSetLayout(layout, Gpu.Device.VkAllocator);
        Layouts.Clear();
    }
}
