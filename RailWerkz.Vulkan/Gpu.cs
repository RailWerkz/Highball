using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal;
using Brutal.Pointers;
using Brutal.VulkanApi.Vma;
using RailWerkz.Core.Logging;

namespace RailWerkz.Vulkan;

/// The GPU: one Vulkan 1.3 instance and device, the window's swapchain, and the frames in flight.
///
/// A frame: BeginFrame (wait for the frame slot, acquire a swapchain image, start its command buffer), record into
/// Cmd (offscreen work first, then BeginPresentPass for what lands on screen), EndFrame (submit and present).
/// Anything the GPU may still be reading is released through Defer, which waits until that frame slot comes round.
public static unsafe class Gpu
{
    public const int FramesInFlight = 2;
    private const int Api13 = (1 << 22) | (3 << 12);

    public static Instance Instance { get; private set; } = null!;
    public static PhysicalDevice PhysicalDevice { get; private set; } = null!;
    public static DeviceEx Device { get; private set; } = null!;
    /// Where every buffer and image gets its memory: VMA, which carves them out of a few large blocks instead of
    /// a separate device allocation each. Made with the device and destroyed just before it.
    public static VmaAllocator Allocator { get; private set; } = null!;
    public static Queue Queue { get; private set; } = null!;
    public static int QueueFamily { get; private set; }

    public static string Name { get; private set; } = "UNKNOWN";
    public static string ApiVersion { get; private set; } = "";
    public static float MaxAnisotropy { get; private set; }

    /// The render pass that draws onto the swapchain image and leaves it ready to present.
    public static VkRenderPass PresentPass { get; private set; }
    public static VkFormat SwapchainFormat { get; private set; }
    public static int MinImageCount { get; private set; }
    public static int ImageCount => _images.Length;
    public static int2 Extent { get; private set; }

    /// The command buffer of the frame being recorded (between BeginFrame and EndFrame).
    public static CommandBuffer Cmd => _frames[_frame].Cmd;

    /// Which of the FramesInFlight slots this frame records into: data written per slot is safe from frames still in flight.
    public static int FrameSlot => _frame;

    /// The frame being recorded, counted from the first: changes every BeginFrame.
    public static long FrameNumber => _frameNumber;

    /// Whether one indirect call can make many draws (multiDrawIndirect).
    public static bool MultiDraw { get; private set; }

    /// FIFO when on; mailbox (or immediate) when off. Takes effect on the next frame.
    public static bool VSync
    {
        get => _vsync;
        set
        {
            if (_vsync == value)
                return;

            _vsync = value;
            _swapchainDirty = true;
        }
    }

    private sealed class Frame
    {
        public VkCommandPool Pool;
        public CommandBuffer Cmd;
        public VkSemaphore ImageReady;
        public VkFence Fence;
    }

    private static readonly Frame[] _frames = new Frame[FramesInFlight];

    /// What's been let go of, and the last frame that could still be using it: the one it was let go in, or the one after, whose
    /// commands start with the uploads asked for in the one before.
    private static readonly Queue<(long LastUse, Action Release)> Retiring = new();
    private static long _frameNumber;
    private static VkSurfaceKHR _surface;
    private static VkDebugUtilsMessengerEXT _messenger;
    private static VkSwapchainKHR _swapchain;
    private static VkImage[] _images = [];
    private static VkImageView[] _views = [];
    private static VkFramebuffer[] _framebuffers = [];
    private static VkSemaphore[] _renderFinished = [];
    private static VkCommandPool _oneShotPool;
    private static bool _vsync = true;
    private static bool _swapchainDirty;
    private static int _frame;
    private static int _image;
    private static Action<byte[], int, int>? _capture;

    /// instanceExtensions: what the window system needs (GLFW's list). createSurface: makes the window's surface from
    /// the VkInstance handle.
    public static void Init(IEnumerable<string> instanceExtensions, Func<nint, ulong> createSurface, int2 size, bool vsync, bool validation)
    {
        _vsync = vsync;
        CreateInstance(instanceExtensions, validation);
        _surface = new VkSurfaceKHR((nint)createSurface(Instance.VkHandle));
        PickPhysicalDevice();
        CreateDevice();

        var poolInfo = new VkCommandPoolCreateInfo { Flags = VkCommandPoolCreateFlags.TransientBit | VkCommandPoolCreateFlags.ResetCommandBufferBit, QueueFamilyIndex = QueueFamily };
        _oneShotPool = Device.CreateCommandPool(in poolInfo, Device.VkAllocator);

        for (var i = 0; i < FramesInFlight; i++)
        {
            var frame = new Frame();
            var info = new VkCommandPoolCreateInfo { Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit, QueueFamilyIndex = QueueFamily };
            frame.Pool = Device.CreateCommandPool(in info, Device.VkAllocator);
            frame.Cmd = Device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = frame.Pool, Level = VkCommandBufferLevel.Primary });
            var semaphore = new VkSemaphoreCreateInfo();
            frame.ImageReady = Device.CreateSemaphore(in semaphore, Device.VkAllocator);
            frame.Fence = Device.CreateFence(VkFenceCreateFlags.SignaledBit, Device.VkAllocator);
            _frames[i] = frame;
        }

        CreateSwapchain(size);
        GpuTimers.Init();
    }

    private static void CreateInstance(IEnumerable<string> instanceExtensions, bool validation)
    {
        var extensions = new HashSet<string>(instanceExtensions);
        var layers = new HashSet<string>();
        VulkanHelpers.AddPlatformRequiredExtensions(extensions);

        validation &= VulkanHelpers.Validation.IsSupported;
        if (validation)
        {
            VulkanHelpers.AddDebugExtensions(extensions);
            VulkanHelpers.AddValidationLayers(layers);
        }

        fixed (byte* app = "Highball\0"u8)
        fixed (byte* engine = "RailWerkz\0"u8)
        {
            Instance = Brutal.VulkanApi.Vulkan.CreateInstance(new Instance.CreateInfo
            {
                AppInfo = new VkApplicationInfo { ApplicationName = app, EngineName = engine, ApiVersion = Api13 },
                Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
                EnabledExtensions = extensions,
                EnabledLayers = layers,
            });
        }

        if (validation)
        {
            var info = VulkanHelpers.BuildDefaultDebugMessengerCreateInfo(new PfnVkDebugUtilsMessengerCallbackEXT(&OnValidation));
            _messenger = Instance.CreateDebugUtilsMessengerEXT(in info, null);
            Log.Info("Vulkan validation on");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Bool32 OnValidation(VkDebugUtilsMessageSeverityFlagsEXT severity, VkDebugUtilsMessageTypeFlagsEXT types, VkDebugUtilsMessengerCallbackDataEXT* data, void* user)
    {
        var message = Marshal.PtrToStringUTF8((nint)data->Message) ?? "";
        if ((severity & VkDebugUtilsMessageSeverityFlagsEXT.ErrorBitEXT) != 0)
            Log.Error($"Vulkan: {message}");
        else if ((severity & VkDebugUtilsMessageSeverityFlagsEXT.WarningBitEXT) != 0)
            Log.Warn($"Vulkan: {message}");
        return new Bool32(false);
    }

    /// A discrete GPU if there is one, with Vulkan 1.3, swapchains, and a graphics queue that can present.
    private static void PickPhysicalDevice()
    {
        PhysicalDevice? best = null;
        var bestScore = -1;
        var bestFamily = -1;

        foreach (var device in Instance.PhysicalDevices)
        {
            var properties = device.GetProperties();
            var name = VulkanDeviceInfo.GetDeviceName(device);
            if (properties.ApiVersion < Api13 || !device.SupportsExtension("VK_KHR_swapchain"u8))
            {
                Log.Info($"GPU {name}: skipped (needs Vulkan 1.3 and swapchains)");
                continue;
            }

            var family = device.FindQueueFamily(VkQueueFlags.GraphicsBit, VkQueueFlags.None);
            if (family < 0 || !(bool)device.GetSurfaceSupportKHR(family, _surface))
                continue;

            var score = properties.DeviceType switch
            {
                VkPhysicalDeviceType.DiscreteGpu => 3,
                VkPhysicalDeviceType.IntegratedGpu => 2,
                VkPhysicalDeviceType.VirtualGpu => 1,
                _ => 0,
            };

            if (score > bestScore)
            {
                best = device;
                bestScore = score;
                bestFamily = family;
            }
        }

        PhysicalDevice = best ?? throw new InvalidOperationException("No GPU with Vulkan 1.3 found. Update the graphics driver.");
        QueueFamily = bestFamily;

        var chosen = PhysicalDevice.GetProperties();
        Name = VulkanDeviceInfo.GetDeviceName(PhysicalDevice);
        ApiVersion = VulkanDeviceInfo.FormatApiVersion(chosen.ApiVersion);
        Log.Info($"GPU: {Name} // Vulkan {ApiVersion} // driver {VulkanDeviceInfo.FormatDriverVersion(in chosen)}");
    }

    /// Vulkan 1.3 core (dynamic rendering, synchronization2) plus the 1.2 pieces bindless rendering is built on.
    private static void CreateDevice()
    {
        var extensions = new HashSet<string>();
        VulkanHelpers.AddSwapchainExtensions(extensions);
        VulkanHelpers.AddMandatoryIfSupportedDeviceExtensions(extensions, PhysicalDevice);

        var supported = PhysicalDevice.GetFeatures();
        var v13 = new VkPhysicalDeviceVulkan13Features
        {
            DynamicRendering = new Bool32(true),
            Synchronization2 = new Bool32(true),
            Maintenance4 = new Bool32(true),
            ShaderDemoteToHelperInvocation = new Bool32(true), // discard in shaders built for 1.3
        };
        var v12 = new VkPhysicalDeviceVulkan12Features
        {
            Next = &v13,
            TimelineSemaphore = new Bool32(true),
            BufferDeviceAddress = new Bool32(true),
            ScalarBlockLayout = new Bool32(true),
            DescriptorIndexing = new Bool32(true),
            RuntimeDescriptorArray = new Bool32(true),
            DescriptorBindingPartiallyBound = new Bool32(true),
            DescriptorBindingVariableDescriptorCount = new Bool32(true),
            DescriptorBindingSampledImageUpdateAfterBind = new Bool32(true),
            ShaderSampledImageArrayNonUniformIndexing = new Bool32(true),
        };
        // gl_DrawID, for draws that read what each of a multi-draw's commands is for (Vegetation).
        var v11 = new VkPhysicalDeviceVulkan11Features { Next = &v12, ShaderDrawParameters = new Bool32(true) };
        var features = new VkPhysicalDeviceFeatures2 { Next = &v11 };
        // Many draws from one call, each with its own first instance (Vegetation); without them each is drawn on its own.
        features.Features.MultiDrawIndirect = supported.MultiDrawIndirect;
        features.Features.DrawIndirectFirstInstance = supported.DrawIndirectFirstInstance;
        MultiDraw = (bool)supported.MultiDrawIndirect;
        if (!(bool)supported.DrawIndirectFirstInstance)
            throw new InvalidOperationException("Vulkan: the GPU can't start indirect draws at an instance (drawIndirectFirstInstance)");
        features.Features.SamplerAnisotropy = supported.SamplerAnisotropy;
        features.Features.FillModeNonSolid = supported.FillModeNonSolid; // wireframe views
        features.Features.ShaderClipDistance = supported.ShaderClipDistance; // track cut off at a distance, exactly, without a discard

        var priority = 1f;
        var queue = new VkDeviceQueueCreateInfo { QueueFamilyIndex = QueueFamily, QueueCount = 1, QueuePriorities = &priority };

        Device = PhysicalDevice.CreateDevice(new DeviceEx.CreateInfo
        {
            Extensions = extensions,
            QueueCreateInfos = new ReadOnlySpan<VkDeviceQueueCreateInfo>(&queue, 1),
            Next = new Ptr(&features),
        });

        Allocator = Device.CreateVmaAllocator(new VmaAllocatorCreateInfo
        {
            Flags = VmaAllocatorCreateFlags.BufferDeviceAddressBit, // the device enables bufferDeviceAddress above
            PhysicalDevice = PhysicalDevice.Handle,
            Device = Device.Handle,
            VulkanApiVersion = Api13,
        });

        Queue = Device.GetQueue(QueueFamily, 0);
        MaxAnisotropy = (bool)supported.SamplerAnisotropy ? MathF.Min(8f, PhysicalDevice.GetProperties().Limits.MaxSamplerAnisotropy) : 0f;
    }

    private static VkSurfaceFormatKHR ChooseFormat()
    {
        var count = PhysicalDevice.GetSurfaceFormatsKHR_Count(_surface);
        var formats = new VkSurfaceFormatKHR[count];
        PhysicalDevice.GetSurfaceFormatsKHR_Write(_surface, formats);

        // UNORM: ImGui's colours are already in display space, and the world will tonemap into it itself.
        foreach (var f in formats)
            if (f.Format is VkFormat.B8G8R8A8UNorm or VkFormat.R8G8B8A8UNorm && f.ColorSpace == VkColorSpaceKHR.SRGBNonlinearKHR)
                return f;

        return formats[0];
    }

    private static VkPresentModeKHR ChoosePresentMode()
    {
        if (_vsync)
            return VkPresentModeKHR.FifoKHR;

        var count = PhysicalDevice.GetSurfacePresentModesKHR_Count(_surface);
        var modes = new VkPresentModeKHR[count];
        PhysicalDevice.GetSurfacePresentModesKHR_Write(_surface, modes);
        if (modes.Contains(VkPresentModeKHR.MailboxKHR))
            return VkPresentModeKHR.MailboxKHR;
        return modes.Contains(VkPresentModeKHR.ImmediateKHR) ? VkPresentModeKHR.ImmediateKHR : VkPresentModeKHR.FifoKHR;
    }

    private static void CreateSwapchain(int2 size)
    {
        var caps = PhysicalDevice.GetSurfaceCapabilitiesKHR(_surface);
        var format = ChooseFormat();

        // The surface says how big it is unless it leaves that to us (0xFFFFFFFF).
        var extent = caps.CurrentExtent.Width != -1
            ? caps.CurrentExtent
            : new VkExtent2D(Math.Clamp(size.X, caps.MinImageExtent.Width, caps.MaxImageExtent.Width), Math.Clamp(size.Y, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));

        var imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0)
            imageCount = Math.Min(imageCount, caps.MaxImageCount);

        var old = _swapchain;
        var info = new VkSwapchainCreateInfoKHR
        {
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = format.Format,
            ImageColorSpace = format.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            // TransferSrc: screenshots copy straight out of the swapchain image.
            ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit,
            ImageSharingMode = VkSharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR,
            PresentMode = ChoosePresentMode(),
            Clipped = new Bool32(true),
            OldSwapchain = old,
        };
        _swapchain = Device.CreateSwapchainKHR(in info, Device.VkAllocator);

        if (!old.IsNull())
            Device.DestroySwapchainKHR(old, Device.VkAllocator);

        var count = Device.GetSwapchainImagesKHR_Count(_swapchain);
        _images = new VkImage[count];
        Device.GetSwapchainImagesKHR_Write(_swapchain, _images);

        if (PresentPass.IsNull())
        {
            SwapchainFormat = format.Format;
            MinImageCount = imageCount;
            PresentPass = CreatePresentPass(format.Format);
        }

        Extent = new int2(extent.Width, extent.Height);
        _views = new VkImageView[count];
        _framebuffers = new VkFramebuffer[count];
        _renderFinished = new VkSemaphore[count];

        for (var i = 0; i < count; i++)
        {
            var view = new VkImageViewCreateInfo
            {
                Image = _images[i],
                ViewType = VkImageViewType._2D,
                Format = format.Format,
                SubresourceRange = Subresources.Full(VkImageAspectFlags.ColorBit),
            };
            _views[i] = Device.CreateImageView(in view, Device.VkAllocator);

            var attachment = _views[i];
            var framebuffer = new VkFramebufferCreateInfo
            {
                RenderPass = PresentPass,
                AttachmentCount = 1,
                Attachments = &attachment,
                Width = extent.Width,
                Height = extent.Height,
                Layers = 1,
            };
            _framebuffers[i] = Device.CreateFramebuffer(in framebuffer, Device.VkAllocator);

            var semaphore = new VkSemaphoreCreateInfo();
            _renderFinished[i] = Device.CreateSemaphore(in semaphore, Device.VkAllocator);
        }

        _swapchainDirty = false;
    }

    /// One colour attachment, cleared, finishing ready to present. ImGui's renderer needs a render pass object.
    private static VkRenderPass CreatePresentPass(VkFormat format)
    {
        var attachment = new VkAttachmentDescription
        {
            Format = format,
            Samples = VkSampleCountFlags._1Bit,
            LoadOp = VkAttachmentLoadOp.Clear,
            StoreOp = VkAttachmentStoreOp.Store,
            StencilLoadOp = VkAttachmentLoadOp.DontCare,
            StencilStoreOp = VkAttachmentStoreOp.DontCare,
            InitialLayout = VkImageLayout.Undefined,
            FinalLayout = VkImageLayout.PresentSrcKHR,
        };
        var reference = new VkAttachmentReference { Attachment = 0, Layout = VkImageLayout.ColorAttachmentOptimal };
        var subpass = new VkSubpassDescription { PipelineBindPoint = VkPipelineBindPoint.Graphics, ColorAttachmentCount = 1, ColorAttachments = &reference };
        var dependency = new VkSubpassDependency
        {
            SrcSubpass = -1, // VK_SUBPASS_EXTERNAL
            DstSubpass = 0,
            SrcStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = VkPipelineStageFlags.ColorAttachmentOutputBit,
            DstAccessMask = VkAccessFlags.ColorAttachmentWriteBit,
        };
        var info = new VkRenderPassCreateInfo
        {
            AttachmentCount = 1,
            Attachments = &attachment,
            SubpassCount = 1,
            Subpasses = &subpass,
            DependencyCount = 1,
            Dependencies = &dependency,
        };
        return Device.CreateRenderPass(in info, Device.VkAllocator);
    }

    private static void DestroySwapchainViews()
    {
        for (var i = 0; i < _views.Length; i++)
        {
            Device.DestroyFramebuffer(_framebuffers[i], Device.VkAllocator);
            Device.DestroyImageView(_views[i], Device.VkAllocator);
            Device.DestroySemaphore(_renderFinished[i], Device.VkAllocator);
        }
    }

    private static void RecreateSwapchain(int2 size)
    {
        WaitIdle();
        DestroySwapchainViews();
        CreateSwapchain(size);
    }

    /// Waits for this frame slot, frees what it held, and acquires a swapchain image. False when there is nothing to
    /// draw on this frame (minimised, or the swapchain was just remade); skip the frame then.
    public static bool BeginFrame(int2 size)
    {
        if (size.X <= 0 || size.Y <= 0)
            return false;

        if (_swapchainDirty || size != Extent)
            RecreateSwapchain(size);

        var frame = _frames[_frame];
        Device.WaitForFence(frame.Fence, -1);

        // This slot's fence has passed, so every frame up to the one it last held has finished.
        var finished = _frameNumber - FramesInFlight;
        while (Retiring.TryPeek(out var next) && next.LastUse <= finished)
            Retiring.Dequeue().Release();

        VkResult result;
        try
        {
            result = Device.AcquireNextImageKHR(_swapchain, -1, frame.ImageReady, default, out _image);
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR) // the wrapper throws this rather than returning it
        {
            result = e.Result;
        }

        if (result == VkResult.ErrorOutOfDateKHR)
        {
            RecreateSwapchain(size);
            return false;
        }

        if (result is not (VkResult.Success or VkResult.SuboptimalKHR))
            throw new InvalidOperationException($"Vulkan: acquire failed ({result})");

        Device.ResetFences([frame.Fence]);
        frame.Cmd.Reset(VkCommandBufferResetFlags.None);
        frame.Cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        GpuTimers.BeginFrame(frame.Cmd, _frame);
        Buffers.FlushUploads(frame.Cmd);
        return true;
    }

    /// Starts the pass onto the swapchain image, cleared to colour.
    public static void BeginPresentPass(float4 clear)
    {
        var value = new VkClearValue();
        value.Color.Float32 = clear;
        var info = new VkRenderPassBeginInfo
        {
            RenderPass = PresentPass,
            Framebuffer = _framebuffers[_image],
            RenderArea = new VkRect2D(Extent.X, Extent.Y),
            ClearValueCount = 1,
            ClearValues = &value,
        };
        Cmd.BeginRenderPass(in info, VkSubpassContents.Inline);
    }

    public static void EndPresentPass() => Cmd.EndRenderPass();

    /// Copies the next presented frame to memory (RGBA, top-down) and hands it over once the GPU is done.
    public static void CaptureNextFrame(Action<byte[], int, int> done) => _capture = done;

    /// Submits the frame and presents it.
    public static void EndFrame()
    {
        var frame = _frames[_frame];
        var capture = _capture is not null ? RecordCapture(frame.Cmd) : default;
        frame.Cmd.End();

        var wait = frame.ImageReady;
        var stage = VkPipelineStageFlags.ColorAttachmentOutputBit;
        var signal = _renderFinished[_image];
        var cmd = frame.Cmd.Handle;
        var submit = new VkSubmitInfo
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &wait,
            WaitDstStageMask = &stage,
            CommandBufferCount = 1,
            CommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            SignalSemaphores = &signal,
        };
        Queue.Submit([submit], frame.Fence);

        if (!capture.IsNull())
            FinishCapture(capture, frame.Fence);

        var swapchain = _swapchain;
        var index = _image;
        var present = new VkPresentInfoKHR
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &signal,
            SwapchainCount = 1,
            Swapchains = &swapchain,
            ImageIndices = &index,
        };
        VkResult result;
        try
        {
            result = Queue.PresentKHR(present);
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorOutOfDateKHR)
        {
            result = e.Result;
        }

        if (result is VkResult.ErrorOutOfDateKHR or VkResult.SuboptimalKHR)
            _swapchainDirty = true;
        else if (result != VkResult.Success)
            throw new InvalidOperationException($"Vulkan: present failed ({result})");

        foreach (var release in _deferred)
            Retiring.Enqueue((_frameNumber + 1, release));
        _deferred.Clear();
        _frameNumber++;
        _frame = (_frame + 1) % FramesInFlight;
    }

    /// After the present pass the image is in PRESENT_SRC: copy it into a host buffer and put it back.
    private static BufferEx RecordCapture(CommandBuffer cmd)
    {
        var buffer = Buffers.Create(Extent.X * Extent.Y * 4, VkBufferUsageFlags.TransferDstBit, MemoryPreference.PreferCpu, "capture");
        var range = Subresources.Full(VkImageAspectFlags.ColorBit);
        Barrier(cmd, _images[_image], range, VkImageLayout.PresentSrcKHR, VkImageLayout.TransferSrcOptimal,
            VkPipelineStageFlags.ColorAttachmentOutputBit, VkPipelineStageFlags.TransferBit, VkAccessFlags.ColorAttachmentWriteBit, VkAccessFlags.TransferReadBit);

        var region = new VkBufferImageCopy
        {
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
            ImageExtent = new VkExtent3D { Width = Extent.X, Height = Extent.Y, Depth = 1 },
        };
        cmd.CopyImageToBuffer(_images[_image], VkImageLayout.TransferSrcOptimal, buffer.VkBuffer, [region]);

        Barrier(cmd, _images[_image], range, VkImageLayout.TransferSrcOptimal, VkImageLayout.PresentSrcKHR,
            VkPipelineStageFlags.TransferBit, VkPipelineStageFlags.BottomOfPipeBit, VkAccessFlags.TransferReadBit, VkAccessFlags.None);
        return buffer;
    }

    private static void FinishCapture(BufferEx buffer, VkFence fence)
    {
        Device.WaitForFence(fence, -1);
        var pixels = new byte[Extent.X * Extent.Y * 4];
        Buffers.Invalidate(buffer);
        using (var mapped = buffer.Map())
            mapped.AsSpan()[..pixels.Length].CopyTo(pixels);

        buffer.Dispose();

        if (SwapchainFormat is VkFormat.B8G8R8A8UNorm or VkFormat.B8G8R8A8SRGB)
            for (var i = 0; i < pixels.Length; i += 4)
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);

        var done = _capture!;
        _capture = null;
        done(pixels, Extent.X, Extent.Y);
    }

    /// What's been let go since the last frame was submitted: handed to that next frame's slot when it is, since the
    /// frames already in flight may still be using it.
    private static readonly List<Action> _deferred = [];

    /// Releases something once the GPU can no longer be using it: once the next frame submitted (and so every frame
    /// before it) has finished.
    public static void Defer(Action release) => _deferred.Add(release);

    /// Runs every deferred release now, and any those defer in turn (disposing one thing can let go of others).
    private static void RunDeferred()
    {
        while (_deferred.Count > 0)
        {
            var batch = _deferred.ToArray();
            _deferred.Clear();
            foreach (var release in batch)
                release();
        }
    }

    /// Records commands, runs them, and waits: for uploads at load time.
    public static void Submit(Action<CommandBuffer> record)
    {
        var cmd = Device.AllocateCommandBuffer(new VkCommandBufferAllocateInfo { CommandPool = _oneShotPool, Level = VkCommandBufferLevel.Primary });
        cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        record(cmd);
        cmd.End();

        var fence = Device.CreateFence(VkFenceCreateFlags.None, Device.VkAllocator);
        var handle = cmd.Handle;
        var submit = new VkSubmitInfo { CommandBufferCount = 1, CommandBuffers = &handle };
        Queue.Submit([submit], fence);
        Device.WaitForFence(fence, -1);
        Device.DestroyFence(fence, Device.VkAllocator);
        Device.FreeCommandBuffers(_oneShotPool, [cmd]);
    }

    /// An image layout transition.
    public static void Barrier(CommandBuffer cmd, VkImage image, VkImageSubresourceRange range, VkImageLayout from, VkImageLayout to,
        VkPipelineStageFlags srcStage, VkPipelineStageFlags dstStage, VkAccessFlags srcAccess, VkAccessFlags dstAccess)
    {
        var barrier = new VkImageMemoryBarrier
        {
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = -1, // VK_QUEUE_FAMILY_IGNORED
            DstQueueFamilyIndex = -1,
            Image = image,
            SubresourceRange = range,
        };
        cmd.PipelineBarrier(srcStage, dstStage, VkDependencyFlags.None, [], [], [barrier]);
    }

    public static void WaitIdle()
    {
        if (Device is null || Device.IsNull())
            return;

        // A lost GPU has nothing left to wait for. Throwing here would also replace, on the way out, the error that says what lost it.
        try
        {
            Device.WaitIdle();
        }
        catch (VkResultError e) when (e.Result == VkResult.ErrorDeviceLost)
        {
            Log.Error($"Vulkan: {e.Message}");
        }
    }

    /// Waits for the GPU and runs every deferred release now (before shutting down what they release into).
    public static void Flush()
    {
        WaitIdle();
        // (Each can let go of more of the other: run both until neither has anything left.)
        do
        {
            RunDeferred();
            while (Retiring.TryDequeue(out var retired))
                retired.Release();
        } while (_deferred.Count > 0);
    }

    /// Last thing before the device goes. Anything still allocated here was never disposed, so it gets named in the
    /// log rather than freed behind its owner's back.
    private static void DestroyAllocator()
    {
        if (Allocator is null)
            return;

        Allocator.CalculateStatistics(out var stats);
        var leaked = stats.Total.Statistics;
        if (leaked.AllocationCount > 0)
            Log.Warn($"Vulkan: {leaked.AllocationCount} allocations ({leaked.AllocationBytes.Bytes} bytes) still alive at shutdown");

        Device.DestroyVmaAllocator(Allocator);
        Allocator = null!;
    }

    public static void Shutdown()
    {
        if (Device is null)
            return;

        WaitIdle();
        // (Each can let go of more of the other: run both until neither has anything left.)
        do
        {
            RunDeferred();
            while (Retiring.TryDequeue(out var retired))
                retired.Release();
        } while (_deferred.Count > 0);

        foreach (var frame in _frames)
        {
            if (frame is null)
                continue;

            Device.DestroyFence(frame.Fence, Device.VkAllocator);
            Device.DestroySemaphore(frame.ImageReady, Device.VkAllocator);
            Device.DestroyCommandPool(frame.Pool, Device.VkAllocator);
        }

        GpuTimers.Shutdown();
        TextureBinding.Shutdown();
        DestroySwapchainViews();
        Device.DestroySwapchainKHR(_swapchain, Device.VkAllocator);
        Device.DestroyRenderPass(PresentPass, Device.VkAllocator);
        Device.DestroyCommandPool(_oneShotPool, Device.VkAllocator);
        DestroyAllocator();
        Device.Dispose();

        Instance.DestroySurfaceKHR(_surface, null);
        if (!_messenger.IsNull())
            Instance.DestroyDebugUtilsMessengerEXT(_messenger, null);
        Instance.Dispose();
    }
}
