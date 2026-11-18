namespace RailWerkz.Vulkan;

/// Every model texture in one descriptor array, so a model's parts, whatever their materials, are drawn from one bound set in
/// one indirect call (each draw reads its own material's slots from a table: WorldRenderer.DrawModels). A texture takes a slot
/// the first time it's asked for and gives it back once nothing uses it and the frames that might still read it are done.
/// Built on descriptor indexing (Vulkan 1.2): the array's partially bound and written while bound, slots not in use by any
/// frame in flight only.
public static unsafe class MaterialTable
{
    /// How many textures it holds at once.
    public const int Capacity = 16384;

    private static readonly Lock Guard = new();
    private static readonly Dictionary<Texture2D, (int Slot, int Users)> Slots = new(ReferenceEqualityComparer.Instance);
    private static readonly Stack<int> Free = new();
    private static int _next;
    private static VkDescriptorSetLayout _layout;
    private static VkDescriptorPool _pool;
    private static VkDescriptorSet _set;

    /// The array's layout: binding 0, Capacity combined image samplers, read by fragment shaders.
    public static VkDescriptorSetLayout SetLayout
    {
        get
        {
            lock (Guard)
            {
                Init();
                return _layout;
            }
        }
    }

    private static void Init()
    {
        if (!_layout.IsNull())
            return;

        var flags = VkDescriptorBindingFlags.PartiallyBoundBit | VkDescriptorBindingFlags.UpdateAfterBindBit | VkDescriptorBindingFlags.UpdateUnusedWhilePendingBit;
        var bindingFlags = new VkDescriptorSetLayoutBindingFlagsCreateInfo { BindingCount = 1, BindingFlags = &flags };
        var binding = new VkDescriptorSetLayoutBinding
        {
            Binding = 0, DescriptorType = VkDescriptorType.CombinedImageSampler, DescriptorCount = Capacity, StageFlags = VkShaderStageFlags.FragmentBit,
        };
        var info = new VkDescriptorSetLayoutCreateInfo
        {
            Next = &bindingFlags, Flags = VkDescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit, BindingCount = 1, Bindings = &binding,
        };
        _layout = Gpu.Device.CreateDescriptorSetLayout(in info, Gpu.Device.VkAllocator);

        var size = new VkDescriptorPoolSize { Type = VkDescriptorType.CombinedImageSampler, DescriptorCount = Capacity };
        var pool = new VkDescriptorPoolCreateInfo { Flags = VkDescriptorPoolCreateFlags.UpdateAfterBindBit, MaxSets = 1, PoolSizeCount = 1, PoolSizes = &size };
        _pool = Gpu.Device.CreateDescriptorPool(in pool, Gpu.Device.VkAllocator);

        var layout = _layout;
        var allocate = new VkDescriptorSetAllocateInfo { DescriptorPool = _pool, DescriptorSetCount = 1, SetLayouts = &layout };
        var sets = new VkDescriptorSet[1];
        Gpu.Device.AllocateDescriptorSets(in allocate, sets);
        _set = sets[0];
    }

    /// The slot a texture's read from, taking one (and writing it) if it hasn't got one. Pair each with Release.
    public static int Acquire(Texture2D texture)
    {
        lock (Guard)
        {
            Init();
            if (Slots.TryGetValue(texture, out var held))
            {
                Slots[texture] = (held.Slot, held.Users + 1);
                return held.Slot;
            }

            var slot = Free.Count > 0 ? Free.Pop() : _next < Capacity ? _next++ : throw new InvalidOperationException($"more than {Capacity} model textures");
            var image = new VkDescriptorImageInfo { Sampler = texture.Sampler, ImageView = texture.View, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
            var write = new VkWriteDescriptorSet
            {
                DstSet = _set, DstBinding = 0, DstArrayElement = slot, DescriptorCount = 1, DescriptorType = VkDescriptorType.CombinedImageSampler, ImageInfo = &image,
            };
            Gpu.Device.UpdateDescriptorSets([write], []);
            Slots[texture] = (slot, 1);
            return slot;
        }
    }

    /// One use of a texture's slot given up; the slot's free again once the frames that might read it are done.
    public static void Release(Texture2D texture)
    {
        (int Slot, int Users) held;
        lock (Guard)
        {
            if (!Slots.TryGetValue(texture, out held))
                return;
            if (held.Users > 1)
            {
                Slots[texture] = (held.Slot, held.Users - 1);
                return;
            }

            Slots.Remove(texture);
        }

        Gpu.Defer(() =>
        {
            lock (Guard)
                Free.Push(held.Slot);
        });
    }

    public static void Bind(CommandBuffer cmd, VkPipelineLayout layout, int set = 0)
    {
        lock (Guard)
            Init();
        cmd.BindDescriptorSets(VkPipelineBindPoint.Graphics, layout, set, [_set], []);
    }

    public static void Shutdown()
    {
        if (_layout.IsNull())
            return;
        Gpu.Device.DestroyDescriptorPool(_pool, Gpu.Device.VkAllocator);
        Gpu.Device.DestroyDescriptorSetLayout(_layout, Gpu.Device.VkAllocator);
        (_layout, _pool, _set) = (default, default, default);
        Slots.Clear();
        Free.Clear();
        _next = 0;
    }
}
