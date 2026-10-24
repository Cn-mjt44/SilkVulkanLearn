using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.SDL;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Windowing;
using SilkTry.Vulkan;
using SilkTry.Vulkan.ManagedStruct;

namespace SilkTry;

/// <summary>
/// 跟着 vulkan-tutorial.com（《Vulkan Tutorial》）写的 Vulkan 初始化 + 渲染循环，
/// 所有 Vulkan 资源都通过 <c>SilkTry.Vulkan</c> 下的包装类创建 / 销毁 —— 本文件里不再出现
/// 任何裸 Vulkan 句柄（<c>Vk</c> 只用来做一次实例层可用性检查，见 CheckValidationLayerSupport）。
///
/// 构造函数里 #region 的排列顺序基本就是教程章节的顺序，方便逐段对照：
///   Vulkan Instance    → Instance
///   Vulkan Surface     → Window surface
///   Physical Devices   → Physical devices and queue families
///   Logical Device     → Logical device and queues
///   Command Pool       → Command buffers（命令池部分）
///   Swapchain          → Swap chain / Image views
///   Render Pass        → Render passes（含深度附件，见《Depth buffering》）
///   Framebuffers       → Framebuffers
///   Shader Modules     → Shader modules
///   Graphics Pipeline  → Fixed functions / Graphics pipeline
///   Vertex Buffer      → Vertex buffer creation / Index buffer creation（24 个顶点 + 36 个索引）
///   Command Buffers    → Command buffers
///   Synchronization    → Rendering and presentation / Frames in flight
///   OnWindowUpdate     → 教程的 drawFrame() + updateUniformBuffer()
///
/// 和教程写法上的差异：
///   1. 教程用 GLFW 拿平台句柄，这里交给 Silk.NET 的 IVkSurface 统一处理。
///   2. 教程的 shader 用 gl_VertexIndex 生成三角形；本仓库的 simpleShader.vert 声明了
///      `layout(location = 0) in vec3 position`，所以这里多传一个顶点缓冲（教程《Vertex buffer creation》）。
///   3. 教程的《Swap chain recreation》拆得更细（重建渲染通道 / 管线）；这里把视口 / 裁剪矩形声明成动态状态，
///      所以窗口尺寸变化时只重建交换链、图像视图、深度附件、帧缓冲、命令缓冲和按图像的信号量，
///      不重建渲染通道和管线。
///   4. 教程《Uniform buffer》用「UBO + 描述符集」传 MVP；这里改用 **push constant**
///      （每帧一个矩阵 64 字节，远小于保证支持的 128 字节），省掉描述符集那一路。
///      代价是 push constant 是录进命令缓冲的，所以命令缓冲改成**每帧重录**。
/// </summary>
public class Game_Vulkan
{
    // 教程《Frames in flight》里的 MAX_FRAMES_IN_FLIGHT。
    private const int MaxFramesInFlight = 2;

    public readonly IWindow Window;
    public readonly IInputContext Input;

    public readonly VKInstance Instance;
    public readonly VKSurface Surface;
    public readonly VKPhysicalDevice PhysicalDevice;
    public readonly VKLogicalDevice LogicalDevice;

    /// <summary>图形 / 呈现队列。两个族索引相同时指向同一个 VKDeviceQueue 实例。</summary>
    public readonly VKDeviceQueue GraphicsQueue;
    public readonly VKDeviceQueue PresentQueue;

    public readonly VKCommandPool CommandPool;
    public readonly VKRenderPass RenderPass;

    // 下面这些会和交换链一起重建，所以不能是 readonly：
    // 交换链、图像视图、帧缓冲、命令缓冲、按图像的 renderFinished 信号量。
    public VKSwapchain Swapchain = null!;
    public VKFramebuffer[] Framebuffers = null!;
    public VKCommandBuffer[] CommandBuffers = null!;

    public readonly VKShaderModule VertexShaderModule;
    public readonly VKShaderModule FragmentShaderModule;
    public readonly VKShaderPipelineLayout PipelineLayout;
    public readonly VKRenderPassShaderPipeline Pipeline;

    public readonly VKBuffer VertexBuffer;
    public readonly VKBuffer IndexBuffer;

    private readonly VKDeviceMemory vertexBufferMemory;
    private readonly VKDeviceMemory indexBufferMemory;

    // 索引数量（36 = 6 面 × 2 三角形 × 3 个索引）；顶点只有 24 个（每个面 4 个角，面与面之间共用位置但颜色不同）。
    private readonly int indexCount;

    // 深度附件跟着交换链一起重建（尺寸变了要重新分配）。
    private readonly Format depthFormat;
    private VKImage depthImage = null!;
    private VKDeviceMemory depthImageMemory = null!;
    private VKImageView depthImageView = null!;

    private VKImageView[] swapchainImageViews = null!;

    // 自转用：从窗口创建起累计的秒数（Update 给的 deltaTime 累加）。
    private double elapsedSeconds;

    // imageAvailable 按"帧在途"分配，renderFinished 按"交换链图像"分配（理由见教程）。
    private readonly VKSemaphore[] imageAvailableSemaphores;
    private VKSemaphore[] renderFinishedSemaphores = null!;
    private readonly VKFence[] inFlightFences;

    // 队列族索引在选设备时确定，重建交换链时还要用，所以存成字段。
    private readonly uint graphicsFamilyIndex;
    private readonly uint presentFamilyIndex;

    private int currentFrame;
    private bool resourcesDestroyed;

    // 窗口尺寸变化只置这个标志，真正的交换链重建放到帧循环里做。
    private bool framebufferResized;

    /// <summary>
    /// 顶点数据（simpleShader.vert 里 `layout(location = 0) in vec3 position` + `location = 1 in vec3 color`）。
    /// 布局要和管线里的 VertexInputAttributeDescription 对上：position 在偏移 0、color 在偏移 12。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Vertex
    {
        public Vector3 Position;
        public Vector3 Color;

        public Vertex(Vector3 position, Vector3 color)
        {
            Position = position;
            Color = color;
        }
    }

    private readonly static Vk VK = Vk.GetApi();

    public unsafe Game_Vulkan(string name, bool enableValidationLayers = false)
    {
        // ===== 教程《Base code》/《Validation layers》: initWindow() =====
        Sdl SDL = SdlProvider.SDL.Value;
        SDL.SetHint(Sdl.HintWindowsDpiAwareness, "system");
        SDL.VideoInit(null as string);
        Silk.NET.Windowing.Window.PrioritizeSdl();
        Window = Silk.NET.Windowing.Window.Create(WindowOptions.DefaultVulkan with { Title = name });
        Window.Initialize();
        Input = Window.CreateInput();

        IVkSurface vkSurface = Window.VkSurface
            ?? throw new NotSupportedException("not support Vulkan");

        // ===== 教程《Instance》章节 =====
        // 教程用 glfwGetRequiredInstanceExtensions() 拿平台扩展；这里由 IVkSurface 报给我们。
        byte** ppRequiredExtensions = vkSurface.GetRequiredExtensions(out uint requiredExtensionCount);
        HashSet<string> instanceExtensions = new(
            SilkMarshal.PtrToStringArray((nint)ppRequiredExtensions, (int)requiredExtensionCount));

        HashSet<string>? enabledLayers = null;
        DebugUtilsMessengerCreateInfoEXT? debugMessengerInfo = null;

        // ===== 教程《Validation layers》章节 =====
        if (enableValidationLayers)
        {
            if (!CheckValidationLayerSupport())
            {
                throw new NotSupportedException("validation layers requested, but not available!");
            }

            instanceExtensions.Add(ExtDebugUtils.ExtensionName);
            enabledLayers = ["VK_LAYER_KHRONOS_validation"];

            // 教程的小技巧：把 debug messenger 的 create info 挂到实例的 pNext 上，
            // 这样 vkCreateInstance / vkDestroyInstance 内部的错误也能被校验层报出来。
            debugMessengerInfo = new DebugUtilsMessengerCreateInfoEXT(
                messageSeverity: DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt |
                                 DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
                                 DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                messageType: DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
                             DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt |
                             DebugUtilsMessageTypeFlagsEXT.ValidationBitExt,
                pfnUserCallback: (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback);
        }

        Instance = new VKInstance(
            apiVersion: new Version32(1, 3, 0),
            applicationName: name,
            engineName: typeof(Game_Vulkan).FullName,
            debugMessengerInfo: debugMessengerInfo,
            instanceExtensions: instanceExtensions,
            enabledLayers: enabledLayers);

        #region Vulkan Surface

        // ===== 教程《Window surface》章节 =====
        // 平台差异（Win32 / XCB / Wayland…）全部由 IVkSurface 处理，包装层只负责登记与销毁。
        Surface = Instance.CreateWindowSurface(vkSurface);

        #endregion

        #region Physical Devices

        // ===== 教程《Physical devices and queue families》章节 =====
        // 教程拆成 isDeviceSuitable() + findQueueFamilies()；这里同样要求：
        // ① 有图形队列族、② 有能往本 surface 呈现的队列族、③ 设备特性够用，然后按设备类型打分取最高。
        VKPhysicalDevice? selectedDevice = null;
        graphicsFamilyIndex = 0;
        presentFamilyIndex = 0;
        uint bestScore = 0;

        PhysicalDeviceProperties properties;
        foreach (VKPhysicalDevice candidate in Instance.PhysicalDevices)
        {
            properties = candidate.Properties;
            Console.WriteLine($"Evaluating device: {Encoding.UTF8.GetString(properties.DeviceName, 256)}");

            if (!TryFindQueueFamilies(candidate, out uint candidateGraphics, out uint candidatePresent))
            {
                continue;
            }

            uint score = DeviceScore(candidate);
            if (score <= bestScore)
            {
                continue;
            }

            selectedDevice = candidate;
            graphicsFamilyIndex = candidateGraphics;
            presentFamilyIndex = candidatePresent;
            bestScore = score;
        }

        PhysicalDevice = selectedDevice
            ?? throw new NotSupportedException("failed to find a suitable GPU!");

        properties = PhysicalDevice.Properties;
        this.Window.Title += $" - {Encoding.UTF8.GetString(properties.DeviceName, 256)}";

        #endregion

        #region Logical Device

        // ===== 教程《Logical device and queues》章节 =====
        // 图形族和呈现族相同时只建一条队列（同一个 VkDeviceQueueCreateInfo 里出现两次是非法，所以这里通过数据结构让非法申请无法表达）。
        Dictionary<uint, float[]> queueFamilies = new()
        {
            [graphicsFamilyIndex] = [1.0f],
            [presentFamilyIndex] = [1.0f]
        };

        // 教程这章只开空特性；SamplerAnisotropy 是《Image view and sampler》才用，这里顺手开着。
        PhysicalDeviceFeatures deviceFeatures = new() { SamplerAnisotropy = true };

        // 交换链扩展是唯一必需项（动态渲染那套扩展等到用动态管线时再加）。
        HashSet<string> deviceExtensions = [Silk.NET.Vulkan.Extensions.KHR.KhrSwapchain.ExtensionName];

        LogicalDevice = PhysicalDevice.CreateLogicalDevice(queueFamilies, deviceFeatures, deviceExtensions);
        GraphicsQueue = LogicalDevice.EnabledDeviceQueues[graphicsFamilyIndex][0];
        PresentQueue = LogicalDevice.EnabledDeviceQueues[presentFamilyIndex][0];


        #endregion

        #region Command Pool

        // 池子按队列族分类：命令提交到哪个队列，就得用哪个族的池。
        CommandPool = LogicalDevice.CreateCommandPool(
            graphicsFamilyIndex,
            CommandPoolCreateFlags.ResetCommandBufferBit);

        #endregion

        #region Swapchain

        // ===== 教程《Swap chain》/《Image views》章节 =====
        // 交换链和图像视图总是成对重建，所以收在 CreateSwapchain() 里（窗口尺寸变化时也走它）。
        CreateSwapchain();

        #endregion

        #region Render Pass

        // ===== 教程《Render passes》章节 =====
        // ① 附件描述 ② 附件引用 ③ 子通道 ④ 子通道依赖。
        // 和教程《Depth buffering》一样，这里有两个附件：0 = 颜色，1 = 深度。
        // 深度格式现在就定下来（建深度图像时还要用同一个）。
        depthFormat = FindDepthFormat();

        AttachmentDescription colorAttachment = new()
        {
            Format = Swapchain.ImageFormat.Format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr
        };

        // 深度只在渲染通道内部用，最后是什么内容没人关心，所以 StoreOp = DontCare。
        AttachmentDescription depthAttachment = new()
        {
            Format = depthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal
        };

        AttachmentReference colorAttachmentReference = new()
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal
        };

        AttachmentReference depthAttachmentReference = new()
        {
            Attachment = 1,
            Layout = ImageLayout.DepthStencilAttachmentOptimal
        };

        ManagedSubpassDescription subpass = new()
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachments = [colorAttachmentReference],
            DepthStencilAttachment = depthAttachmentReference
        };

        SubpassDependency dependency = new()
        {
            SrcSubpass = uint.MaxValue, // VK_SUBPASS_EXTERNAL
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit
        };

        RenderPass = LogicalDevice.CreateRenderPass(
            [colorAttachment, depthAttachment],
            [subpass],
            [dependency]);

        #endregion

        #region Depth Resources

        // ===== 教程《Depth buffering》章节：createDepthResources() =====
        // 深度图像是设备本地内存（CPU 不碰它），尺寸跟着交换链走，所以和交换链一起重建。
        CreateDepthResources();

        #endregion

        #region Framebuffers

        // ===== 教程《Framebuffers》章节 =====
        // 帧缓冲跟着交换链一起重建（尺寸 / 图像数量都可能变）。
        CreateFramebuffers();

        #endregion

        #region Shader Modules

        // ===== 教程《Shader modules》章节 =====
        // .spv 由 csproj 的 CompileShaders 目标在构建时用 glslc 生成，再拷到输出目录的 Shaders/ 下。
        string shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Shaders");
        VertexShaderModule = LogicalDevice.CreateShaderModule(
            File.ReadAllBytes(Path.Combine(shaderDirectory, "simpleShader.vert.spv")));
        FragmentShaderModule = LogicalDevice.CreateShaderModule(
            File.ReadAllBytes(Path.Combine(shaderDirectory, "simpleShader.frag.spv")));

        #endregion

        #region Graphics Pipeline

        // ===== 教程《Fixed functions》/《Graphics pipeline》章节 =====
        // MVP 走 push constant（教程这里用的是描述符集里的 uniform buffer）：
        // 顶点着色器声明了 `layout(push_constant) uniform Push { mat4 mvp; }`，
        // 所以管线布局里要登记一段同样大小的范围（mat4 = 64 字节，一个阶段，偏移 0）。
        PushConstantRange mvpPushConstantRange = new()
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)sizeof(Matrix4x4)
        };

        PipelineLayout = LogicalDevice.CreateShaderPipelineLayout(
            pushConstantRanges: [mvpPushConstantRange]);

        ManagedPipelineStateCreateInfo pipelineState = ManagedPipelineStateCreateInfo.CreateDefault();

        // 顶点输入：一条 binding、步长 = sizeof(Vertex)；
        // location 0 是 position(vec3)，location 1 是 color(vec3)，对应 simpleShader.vert 里的两个 in 变量。
        pipelineState.VertexBindings =
        [
            new VertexInputBindingDescription(
                binding: 0,
                stride: (uint)sizeof(Vertex),
                inputRate: VertexInputRate.Vertex)
        ];
        pipelineState.VertexAttributes =
        [
            new VertexInputAttributeDescription(
                location: 0,
                binding: 0,
                format: Format.R32G32B32Sfloat,
                offset: 0),
            new VertexInputAttributeDescription(
                location: 1,
                binding: 0,
                format: Format.R32G32B32Sfloat,
                offset: (uint)Marshal.OffsetOf<Vertex>(nameof(Vertex.Color)))
        ];

        // 深度测试：立方体的背面 / 内部面会互相重叠，光靠剔除不够稳，必须让深度缓冲决定前后关系。
        //（CreateDefault 已经把剔除设成 Back、正面设成 Clockwise —— 和投影矩阵里的“Y 翻转”是配套的。）
        pipelineState.DepthStencilState = new PipelineDepthStencilStateCreateInfo(
            flags: PipelineDepthStencilStateCreateFlags.None,
            depthTestEnable: true,
            depthWriteEnable: true,
            depthCompareOp: CompareOp.Less,
            depthBoundsTestEnable: false,
            stencilTestEnable: false,
            front: default,
            back: default,
            minDepthBounds: 0.0f,
            maxDepthBounds: 1.0f);

        // 视口 / 裁剪矩形声明为**动态状态**：窗口尺寸变化只需要在录制命令缓冲时重新设值，
        // 不用重建管线（教程默认是静态视口，重建交换链时要连管线一起重建）。
        pipelineState.DynamicStates = [DynamicState.Viewport, DynamicState.Scissor];

        ManagedPipelineShaderStage[] shaderStages =
        [
            new(VertexShaderModule, ShaderStageFlags.VertexBit),
            new(FragmentShaderModule, ShaderStageFlags.FragmentBit)
        ];

        Pipeline = PipelineLayout.CreateRenderPassShaderPipeline(shaderStages, pipelineState, RenderPass);

        #endregion

        #region Vertex Buffer

        // ===== 教程《Vertex buffer creation》/《Index buffer creation》章节 =====
        // 教程用 staging buffer 拷到设备本地内存；示例数据很小，
        // 直接用 HostVisible|HostCoherent 内存（不经过 staging）就够了。
        (Vertex[] vertices, ushort[] indices) = CreateCubeMesh();
        indexCount = indices.Length;

        VertexBuffer = CreateHostVisibleBuffer(
            vertices,
            BufferUsageFlags.VertexBufferBit,
            out vertexBufferMemory);

        IndexBuffer = CreateHostVisibleBuffer(
            indices,
            BufferUsageFlags.IndexBufferBit,
            out indexBufferMemory);

        #endregion

        #region Command Buffers

        // ===== 教程《Command buffers》章节 =====
        // 命令缓冲里录着具体的帧缓冲和视口尺寸，所以交换链重建后要重新分配（见 AllocateCommandBuffers）。
        // 每帧还要把新的 MVP push 进去，所以内容是每帧重录的（见 OnWindowUpdate）。
        AllocateCommandBuffers();

        #endregion

        #region Synchronization

        // ===== 教程《Rendering and presentation》/《Frames in flight》章节 =====
        imageAvailableSemaphores = new VKSemaphore[MaxFramesInFlight];
        inFlightFences = new VKFence[MaxFramesInFlight];

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            imageAvailableSemaphores[i] = LogicalDevice.CreateSemaphore();
            // 第一帧没东西可等，栅栏必须从"已触发"开始，否则第一次 Wait 会永远卡住。
            inFlightFences[i] = LogicalDevice.CreateFence(FenceCreateFlags.SignaledBit);
        }

        // renderFinished 按交换链图像分配（数量随交换链图像数量变化，重建交换链时一起重建）。
        CreateRenderFinishedSemaphores();

        #endregion

        Window.Update += OnWindowUpdate;
        Window.Closing += OnWindowClosing;
        Window.Resize += OnWindowResize;
    }

    /// <summary>
    /// 窗口尺寸变化：这里只置标志，真正的重建放到帧循环里做 ——
    /// 事件里既不该等 GPU，也不该销毁可能还在使用中的对象。
    /// </summary>
    protected virtual void OnWindowResize(Vector2D<int> size)
    {
        framebufferResized = true;
    }

    /// <summary>
    /// 教程的 drawFrame()：等栅栏 → acquire 图像 → 重置栅栏 → 重录命令缓冲（推入新的 MVP）→ 提交 → 呈现 → currentFrame++。
    /// 窗口尺寸变化 / 交换链过期时，在这一帧开始前（或 present 之后）重建交换链。
    /// </summary>
    protected virtual void OnWindowUpdate(double deltaTime)
    {
        // 自转的时间源：累计经过的秒数（角度回绕放到 CreateMvpMatrix 里按各自周期做）。
        elapsedSeconds += deltaTime;

        // 最小化时 framebuffer 尺寸是 0：Vulkan 不允许建 0 尺寸的交换链，先不出图，
        // 等窗口恢复（恢复时还会再来一次 Resize）。
        if (Window.FramebufferSize.X == 0 || Window.FramebufferSize.Y == 0)
        {
            return;
        }

        // 在开始新一帧之前处理尺寸变化：此时上一帧的提交由 RecreateSwapchain 里的
        // WaitIdle 保证已经结束，销毁旧资源是安全的。
        if (framebufferResized)
        {
            framebufferResized = false;
            RecreateSwapchain();
        }

        VKFence fence = inFlightFences[currentFrame];
        VKSemaphore imageAvailable = imageAvailableSemaphores[currentFrame];

        // ① 等这一帧的栅栏：保证上一轮用到这套对象的提交已经结束。
        fence.Wait();

        // ② 问交换链要一张可用图像。这里不需要栅栏，只要信号量（教程也是这么用的）。
        Result acquireResult = Swapchain.AcquireNextImage(
            ulong.MaxValue,
            imageAvailable,
            signalFence: null,
            out uint imageIndex);

        if (acquireResult == Result.ErrorOutOfDateKhr)
        {
            // ③ 交换链过期：重建后这一帧不再画（重建后拿到的新链状态和这一帧无关）。
            RecreateSwapchain();
            return;
        }

        if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
        {
            throw new InvalidOperationException("failed to acquire swap chain image!");
        }

        // ④ 重置栅栏：马上就要用它标记"这一次提交还没完成"。
        fence.Reset();

        // ⑤ 重新录制这张图像的命令缓冲：push constant 里的 MVP 每帧都不一样（方块在自转）。
        //    安全依据：这张图像刚被 acquire 回来，说明上一次画它的提交已经结束（present 等过 renderFinished 信号量）。
        RecordCommandBuffer((int)imageIndex, CreateMvpMatrix(elapsedSeconds));

        VKSemaphore renderFinished = renderFinishedSemaphores[imageIndex];

        // ⑥ 提交绘制命令：等 imageAvailable → 执行 → 置位 renderFinished，栅栏标记整批完成。
        Result submitResult = GraphicsQueue.Submit(
            [CommandBuffers[imageIndex]],
            waitSemaphores: [imageAvailable],
            waitStages: [PipelineStageFlags.ColorAttachmentOutputBit],
            signalSemaphores: [renderFinished],
            fence: fence);

        if (submitResult != Result.Success)
        {
            throw new InvalidOperationException("failed to submit draw command buffer!");
        }

        // ⑦ 呈现：等 renderFinished，把画好的图像交回交换链。
        Result presentResult = Swapchain.Present(PresentQueue, imageIndex, renderFinished);

        currentFrame = (currentFrame + 1) % MaxFramesInFlight;

        // ⑧ 呈现时发现交换链过期 / 不是最优，或者这一帧期间窗口又变了尺寸 —— 下一帧开始前重建。
        if (presentResult == Result.ErrorOutOfDateKhr ||
            presentResult == Result.SuboptimalKhr ||
            framebufferResized)
        {
            framebufferResized = false;
            RecreateSwapchain();
        }
        else if (presentResult != Result.Success)
        {
            throw new InvalidOperationException("failed to present swap chain image!");
        }
    }

    /// <summary>
    /// 教程《Swap chain recreation》章节：窗口尺寸变化后重建与交换链相关的全部对象。
    ///
    /// 顺序很重要（每一步都在前面的对象不再被使用之后）：
    ///   等 GPU 空闲 → 释放命令缓冲（录着旧帧缓冲）→ 销毁旧帧缓冲（引用旧图像视图）
    ///   → 销毁旧深度附件（尺寸跟着交换链走）→ 建新交换链（旧链交给驱动复用，新链建好后销毁旧链及其图像视图）
    ///   → 建新图像视图 / 深度附件 / 帧缓冲 → 重新分配命令缓冲 → 重建按图像的 renderFinished 信号量。
    ///
    /// 渲染通道和管线不用重建：附件格式没变，视口 / 裁剪矩形已经是动态状态。
    /// </summary>
    private void RecreateSwapchain()
    {
        // 最小化时 framebuffer 尺寸是 0：等窗口恢复后再来（Resize 会再次置标志）。
        if (Window.FramebufferSize.X == 0 || Window.FramebufferSize.Y == 0)
        {
            return;
        }

        // 等两条队列都空下来：旧帧缓冲 / 命令缓冲 / 信号量都可能还在被 GPU 使用。
        // （包装层还没暴露 vkDeviceWaitIdle，这里按队列等；两族相同时会多等一次，无害。）
        GraphicsQueue.WaitIdle();
        PresentQueue.WaitIdle();

        // 命令缓冲录着旧帧缓冲，先释放。
        foreach (VKCommandBuffer commandBuffer in CommandBuffers)
        {
            commandBuffer.Destroy();
        }

        // 帧缓冲引用旧图像视图和旧深度附件，必须在它们销毁前清掉。
        foreach (VKFramebuffer framebuffer in Framebuffers)
        {
            framebuffer.Destroy();
        }

        // 深度附件的尺寸绑定旧交换链，一起换掉（image.Destroy 会先销毁它的视图）。
        depthImage.Destroy();
        depthImageMemory.Destroy();

        // 新交换链 + 新图像视图（旧交换链在内部销毁）。
        CreateSwapchain();

        CreateDepthResources();
        CreateFramebuffers();
        AllocateCommandBuffers();
        CreateRenderFinishedSemaphores();
    }

    /// <summary>
    /// 教程 createDepthResources()：建深度图像 → 建视图。
    ///
    /// 深度图像只在 GPU 上用（CPU 不读也不写），所以是 DeviceLocal 内存、Optimal tiling、
    /// 用途 DepthStencilAttachment；布局的转换由渲染通道负责（InitialLayout = Undefined → 附件布局）。
    /// 这里不额外插 barrier：每帧渲染通道都以 Undefined 开始，内容和布局都不用跨帧保留。
    /// </summary>
    private void CreateDepthResources()
    {
        Extent2D extent = Swapchain.ImageExtent;

        depthImage = VKDeviceMemory.CreateImage(
            LogicalDevice,
            depthFormat,
            new Extent3D(extent.Width, extent.Height, 1),
            ImageUsageFlags.DepthStencilAttachmentBit,
            MemoryPropertyFlags.DeviceLocalBit,
            out depthImageMemory);

        ImageAspectFlags aspectMask = depthFormat is Format.D16Unorm or Format.D32Sfloat
            ? ImageAspectFlags.DepthBit
            : ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit;

        depthImageView = depthImage.CreateImageView(aspectMask);
    }

    /// <summary>
    /// 教程 findDepthFormat()：挑一个"能当深度附件用"的格式，精度高的优先。
    /// </summary>
    private Format FindDepthFormat()
    {
        Format[] candidates =
        [
            Format.D32Sfloat,
            Format.D32SfloatS8Uint,
            Format.D24UnormS8Uint,
            Format.D16Unorm
        ];

        foreach (Format candidate in candidates)
        {
            FormatProperties formatProperties = PhysicalDevice.GetFormatProperties(candidate);
            if (formatProperties.OptimalTilingFeatures.HasFlag(FormatFeatureFlags.DepthStencilAttachmentBit))
            {
                return candidate;
            }
        }

        throw new NotSupportedException("failed to find a supported depth format!");
    }

    /// <summary>
    /// 每帧的 MVP（row-vector 约定，和 System.Numerics 一致：clip = v * model * view * projection）。
    ///
    /// 投影矩阵最后乘一个 Y 轴翻转：Vulkan 的 clip space 是 y 轴向下的（OpenGL 向上）。
    /// 翻转会连带把三角形的屏幕绕序翻过来，所以管线里的正面是 Clockwise —— 和
    /// <see cref="ManagedPipelineStateCreateInfo.CreateDefault"/> 的默认值一致。
    ///
    /// 矩阵按 System.Numerics 的行主序内存布局直接推给着色器：GLSL 的 mat4 默认按列主序解释，
    /// 正好把它读成转置，于是着色器里的 `pc.mvp * vec4(position, 1.0)` 等价于这边的 row-vector 乘法。
    /// </summary>
    private Matrix4x4 CreateMvpMatrix(double seconds)
    {
        Extent2D extent = Swapchain.ImageExtent;
        float aspect = extent.Height == 0 ? 1.0f : extent.Width / (float)extent.Height;

        // 两个轴各自按自己的周期回绕（共用同一个回绕点的话，转速不同就会每隔几秒"跳"一下）。
        float yaw = (float)(seconds % (2.0 * Math.PI));
        float pitch = (float)(seconds * 0.6 % (2.0 * Math.PI));

        // 绕 Y 轴匀速转，同时绕 X 轴以慢一点的速度翻，这样三个方向的面会轮流出现。
        Matrix4x4 model = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch);
        Matrix4x4 view = Matrix4x4.CreateLookAt(new Vector3(1.6f, 1.6f, 1.6f), Vector3.Zero, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 4.0f,
            aspect,
            0.1f,
            100.0f);

        projection *= Matrix4x4.CreateScale(1.0f, -1.0f, 1.0f);

        return model * view * projection;
    }

    /// <summary>
    /// 立方体的顶点和索引。
    ///
    /// 顶点 24 个（每个面 4 个角）：立方体一共只有 8 个位置，但每个面要单独上色，
    /// 所以位置会在相邻面之间重复一份 —— 这正是教程《Vertex buffer creation》里说的
    /// "同一个位置在不同面上有不同属性时，只能拆成不同顶点"。
    /// 索引 36 个（6 面 × 2 三角形 × 3）：两个三角形共用那条对角线上的两个顶点。
    ///
    /// 顶点的绕序统一按"从外面看是逆时针"给出。
    /// </summary>
    private static (Vertex[] Vertices, ushort[] Indices) CreateCubeMesh()
    {
        const float half = 0.5f;

        // (法线, 面内向右, 面内向上)：三者构成右手系（cross(右, 上) = 法线），
        // 于是按 左下 → 右下 → 右上 → 左上 连起来，从外面看一定是逆时针。
        (Vector3 Normal, Vector3 Right, Vector3 Up)[] faces =
        [
            (Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY),
            (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ),
            (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
            (-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY)
        ];

        Vector3[] faceColors =
        [
            new(0.90f, 0.20f, 0.20f), // +X 红
            new(0.20f, 0.55f, 0.90f), // -X 蓝
            new(0.25f, 0.80f, 0.30f), // +Y 绿
            new(0.85f, 0.75f, 0.20f), // -Y 黄
            new(0.85f, 0.35f, 0.85f), // +Z 品红
            new(0.20f, 0.80f, 0.80f)  // -Z 青
        ];

        Vertex[] vertices = new Vertex[faces.Length * 4];
        ushort[] indices = new ushort[faces.Length * 6];

        for (int i = 0; i < faces.Length; i++)
        {
            (Vector3 normal, Vector3 right, Vector3 up) = faces[i];
            Vector3 center = normal * half;
            Vector3 bottomLeft = center - right * half - up * half;
            Vector3 bottomRight = center + right * half - up * half;
            Vector3 topRight = center + right * half + up * half;
            Vector3 topLeft = center - right * half + up * half;
            Vector3 color = faceColors[i];

            int v = i * 4;
            vertices[v + 0] = new Vertex(bottomRight, color);
            vertices[v + 1] = new Vertex(bottomLeft, color);
            vertices[v + 2] = new Vertex(topLeft, color);
            vertices[v + 3] = new Vertex(topRight, color);

            // 每个面两个三角形：左下-右下-右上 和 左下-右上-左上。
            int t = i * 6;
            indices[t + 0] = (ushort)(v + 0);
            indices[t + 1] = (ushort)(v + 1);
            indices[t + 2] = (ushort)(v + 2);
            indices[t + 3] = (ushort)(v + 0);
            indices[t + 4] = (ushort)(v + 2);
            indices[t + 5] = (ushort)(v + 3);
        }

        return (vertices, indices);
    }

    /// <summary>
    /// 建一个 HostVisible|HostCoherent 的数据缓冲并把数组拷进去。
    ///
    /// 教程《Vertex buffer creation》末尾会提醒"真正的应用应该先写 staging buffer 再拷到 DeviceLocal 内存"；
    /// 这里的顶点 / 索引只有几百字节，直接映射写入最省事，也让上下文更短。
    /// </summary>
    private VKBuffer CreateHostVisibleBuffer<T>(
        T[] data,
        BufferUsageFlags usage,
        out VKDeviceMemory memory)
        where T : struct
    {
        VKBuffer buffer = VKDeviceMemory.CreateBuffer(
            LogicalDevice,
            (ulong)(data.Length * Marshal.SizeOf<T>()),
            usage,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out memory);

        if (memory.Map(out Span<byte> mapped) != Result.Success)
        {
            throw new NotSupportedException("failed to map buffer memory!");
        }

        data.AsSpan().CopyTo(MemoryMarshal.Cast<byte, T>(mapped));
        memory.Flush();
        memory.Unmap();

        return buffer;
    }

    /// <summary>
    /// 教程 createSwapchain() + createImageViews()：建交换链，再给每张图像建视图。
    /// 重建时也会走到这里 —— 包装层会把当前交换链作为 oldSwapchain 交给驱动，
    /// 并把旧的 VKSwapchain 通过 out 参数还回来，由这里在新链建好后销毁
    ///（销毁旧链会连带销毁它的图像视图，所以调用方必须先销毁旧帧缓冲）。
    /// </summary>
    private void CreateSwapchain()
    {
        SurfaceCapabilitiesKHR capabilities = PhysicalDevice.GetSurfaceCapabilities(Surface);
        IReadOnlyList<SurfaceFormatKHR> availableFormats = PhysicalDevice.GetSurfaceFormats(Surface);
        IReadOnlyList<PresentModeKHR> availablePresentModes = PhysicalDevice.GetSurfacePresentModes(Surface);

        if (availableFormats.Count == 0 || availablePresentModes.Count == 0)
        {
            throw new NotSupportedException("the surface does not support this physical device!");
        }

        SurfaceFormatKHR surfaceFormat = ChooseSwapSurfaceFormat(availableFormats);
        PresentModeKHR presentMode = ChooseSwapPresentMode(availablePresentModes);
        Extent2D extent = ChooseSwapExtent(capabilities);

        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
        {
            imageCount = capabilities.MaxImageCount;
        }

        // 图形族和呈现族不是同一个时，图像会被两边同时访问，只能用 CONCURRENT 并列出两个族。
        bool separateQueueFamilies = graphicsFamilyIndex != presentFamilyIndex;

        Swapchain = LogicalDevice.TryCreateOrUpdateSwapchain(
            Surface,
            surfaceFormat,
            presentMode,
            extent,
            imageArrayLayers: 1,
            minImageCount: imageCount,
            out VKSwapchain? oldSwapchain,
            imageUsage: ImageUsageFlags.ColorAttachmentBit,
            imageSharingMode: separateQueueFamilies ? SharingMode.Concurrent : SharingMode.Exclusive,
            queueFamilyIndices: separateQueueFamilies ? [graphicsFamilyIndex, presentFamilyIndex] : null);

        // 新链已经建好，旧链现在可以销毁了（它的图像视图也在这里一并销毁）。
        oldSwapchain?.Destroy();

        // 交换链图像（VKSwapchain.Images）由驱动持有，我们不销毁它们，只给每张建一个视图。
        Console.WriteLine($"surfaceFormat: {surfaceFormat}");
        Console.WriteLine($"presentMode: {presentMode}");
        Console.WriteLine($"extent: ({extent.Width}, {extent.Height})");
        Console.WriteLine($"swapchainImageViewsCount: {Swapchain.Images.Count}");
        swapchainImageViews = new VKImageView[Swapchain.Images.Count];
        for (int i = 0; i < swapchainImageViews.Length; i++)
        {
            swapchainImageViews[i] = Swapchain.Images[i].CreateImageView(ImageAspectFlags.ColorBit);
        }
    }

    /// <summary>
    /// 教程 createFramebuffers()：附件顺序 / 数量必须和渲染通道里的附件描述一一对应。
    /// 尺寸用 Swapchain.ImageExtent（驱动给的**实际**尺寸，不一定等于我们请求的那个值）。
    /// </summary>
    private void CreateFramebuffers()
    {
        Extent2D extent = Swapchain.ImageExtent;

        Framebuffers = new VKFramebuffer[swapchainImageViews.Length];
        for (int i = 0; i < Framebuffers.Length; i++)
        {
            Framebuffers[i] = RenderPass.CreateFramebuffer(
                [swapchainImageViews[i], depthImageView],
                extent.Width,
                extent.Height);
        }
    }

    /// <summary>
    /// 教程 createCommandBuffers()：按交换链图像数量分配命令缓冲（一张图像一个，录着它自己的帧缓冲）。
    /// 内容每帧重录（MVP 在变），分配本身只在初始化和交换链重建时做。
    /// </summary>
    private void AllocateCommandBuffers()
    {
        CommandBuffers = CommandPool.AllocateCommandBuffers((uint)Framebuffers.Length);
    }

    /// <summary>
    /// 教程 recordCommandBuffer()：录一张交换链图像的命令缓冲。
    /// 视口 / 裁剪矩形是动态状态，所以每次录制都按当前交换链尺寸重新设置；
    /// MVP 用 push constant 推进去（教程这一步用的是 UBO + vkCmdBindDescriptorSets）。
    /// </summary>
    private void RecordCommandBuffer(int imageIndex, Matrix4x4 mvp)
    {
        Extent2D extent = Swapchain.ImageExtent;

        // 清屏色：暗青色，方便确认渲染通道真的在跑。
        ClearValue colorClearValue = new()
        {
            Color = new ClearColorValue
            {
                Float32_0 = 0.0f,
                Float32_1 = 0.05f,
                Float32_2 = 0.08f,
                Float32_3 = 1.0f
            }
        };

        // 深度附件也要清：1.0 是最远，深度比较用 Less 就是"越小越靠前"。
        ClearValue depthClearValue = new()
        {
            DepthStencil = new ClearDepthStencilValue
            {
                Depth = 1.0f,
                Stencil = 0
            }
        };

        Rect2D renderArea = new()
        {
            Offset = new Offset2D { X = 0, Y = 0 },
            Extent = extent
        };

        // 管线里把 Viewport / Scissor 声明成了动态状态，这里必须补上（否则用的是未定义的状态）。
        Viewport viewport = new()
        {
            X = 0,
            Y = 0,
            Width = extent.Width,
            Height = extent.Height,
            MinDepth = 0,
            MaxDepth = 1
        };
        Rect2D scissor = renderArea;

        VKCommandBuffer commandBuffer = CommandBuffers[imageIndex];

        // SimultaneousUseBit：允许上一帧还没画完就再次提交同一个命令缓冲。
        if (commandBuffer.Begin(CommandBufferUsageFlags.SimultaneousUseBit) != Result.Success)
        {
            throw new NotSupportedException("failed to begin recording command buffer!");
        }

        // clearValues 的顺序和渲染通道里的附件顺序一致：0 = 颜色，1 = 深度。
        commandBuffer.BeginRenderPass(Framebuffers[imageIndex], renderArea, [colorClearValue, depthClearValue]);
        commandBuffer.BindPipeline(Pipeline, PipelineBindPoint.Graphics);
        commandBuffer.SetViewport(viewport);
        commandBuffer.SetScissor(scissor);
        commandBuffer.PushConstants(
            PipelineLayout,
            ShaderStageFlags.VertexBit,
            0,
            MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref mvp, 1)));
        commandBuffer.BindVertexBuffer(VertexBuffer);
        // 索引是 ushort（24 个顶点用 Uint16 足够，教程也是先 Uint16 再演示 Uint32）。
        commandBuffer.BindIndexBuffer(IndexBuffer, 0, IndexType.Uint16);
        commandBuffer.DrawIndexed((uint)indexCount);
        commandBuffer.EndRenderPass();

        if (commandBuffer.End() != Result.Success)
        {
            throw new NotSupportedException("failed to record command buffer!");
        }
    }

    /// <summary>
    /// 教程里和交换链一起创建的那组 renderFinished 信号量：按**交换链图像**分配，
    /// 图像数量会随重建变化，所以重建时要先销毁旧的、再按新数量创建。
    /// （imageAvailable 是按帧在途分配的，和交换链无关，不用重建。）
    /// </summary>
    private void CreateRenderFinishedSemaphores()
    {
        if (renderFinishedSemaphores != null)
        {
            foreach (VKSemaphore semaphore in renderFinishedSemaphores)
            {
                semaphore.Destroy();
            }
        }

        renderFinishedSemaphores = new VKSemaphore[Swapchain.Images.Count];
        for (int i = 0; i < renderFinishedSemaphores.Length; i++)
        {
            renderFinishedSemaphores[i] = LogicalDevice.CreateSemaphore();
        }
    }

    /// <summary>
    /// 关闭窗口时销毁资源：先等 GPU 把在途工作做完，再交给包装层的归属链自下而上销毁。
    /// <see cref="VKLogicalDevice.Destroy"/> 会先销毁名下的全部子对象
    ///（命令池 → 管线 → 渲染通道 → 交换链 → 内存 → 着色器模块 → 同步对象），最后才销毁设备；
    /// <see cref="VKInstance"/> 目前由终结器负责销毁，所以这里只销毁到逻辑设备和 surface。
    /// </summary>
    private void OnWindowClosing()
    {
        if (resourcesDestroyed)
        {
            return;
        }

        resourcesDestroyed = true;

        // 等 GPU 把已提交的命令全部执行完，避免销毁正在使用的对象。
        // （包装层还没暴露 vkDeviceWaitIdle，这里按队列等；两族相同时会多等一次，无害。）
        GraphicsQueue.WaitIdle();
        PresentQueue.WaitIdle();

        LogicalDevice.Destroy();
        Surface.Destroy();
    }

    /// <summary>
    /// 教程的 chooseSwapSurfaceFormat()：优先 B8G8R8A8_SRGB + SRGB_NONLINEAR，否则取第一个。
    /// 只返回一个 Undefined 格式表示"随便选"（部分驱动会这样），这里退到最常见的组合。
    /// </summary>
    private static SurfaceFormatKHR ChooseSwapSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> availableFormats)
    {
        if (availableFormats.Count == 1 && availableFormats[0].Format == Format.Undefined)
        {
            return new SurfaceFormatKHR
            {
                Format = Format.B8G8R8A8Srgb,
                ColorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr
            };
        }

        foreach (SurfaceFormatKHR availableFormat in availableFormats)
        {
            if (availableFormat.Format == Format.B8G8R8A8Srgb &&
                availableFormat.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
            {
                return availableFormat;
            }
        }

        return availableFormats[0];
    }

    /// <summary>
    /// 教程的 chooseSwapPresentMode()：优先 MAILBOX（低延迟），没有就用一定支持的 FIFO。
    /// </summary>
    private static PresentModeKHR ChooseSwapPresentMode(IReadOnlyList<PresentModeKHR> availablePresentModes)
    {
        foreach (PresentModeKHR presentMode in availablePresentModes)
        {
            if (presentMode == PresentModeKHR.MailboxKhr)
            {
                return presentMode;
            }
        }

        return PresentModeKHR.FifoKhr;
    }

    /// <summary>
    /// 教程的 chooseSwapExtent()：currentExtent 不是 0xFFFFFFFF 就必须照用，
    /// 否则按窗口尺寸算并夹到 surface 支持的区间里。
    /// </summary>
    private Extent2D ChooseSwapExtent(SurfaceCapabilitiesKHR capabilities)
    {
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            return capabilities.CurrentExtent;
        }

        // 用 FramebufferSize 而不是 Size：HiDPI 下两者不同，交换链要的是像素尺寸。
        return new Extent2D
        {
            Width = Math.Clamp((uint)Window.FramebufferSize.X, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Height = Math.Clamp((uint)Window.FramebufferSize.Y, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height)
        };
    }

    /// <summary>
    /// 教程 findQueueFamilies()：找一个能画图的队列族，和一个能给本 surface 呈现的队列族。
    /// "能不能呈现"必须问 surface（vkGetPhysicalDeviceSurfaceSupportKHR），不能只看队列族 flags。
    /// </summary>
    private bool TryFindQueueFamilies(VKPhysicalDevice device, out uint graphicsFamily, out uint presentFamily)
    {
        graphicsFamily = 0;
        presentFamily = 0;
        bool hasGraphicsFamily = false;
        bool hasPresentFamily = false;

        IReadOnlyList<QueueFamilyProperties> queueFamilies = device.QueueFamilies;
        for (uint i = 0; i < queueFamilies.Count; i++)
        {
            if (queueFamilies[(int)i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                graphicsFamily = i;
                hasGraphicsFamily = true;
            }

            if (device.GetSurfaceSupport(i, Surface, out bool supported) == Result.Success && supported)
            {
                presentFamily = i;
                hasPresentFamily = true;
            }

            if (hasGraphicsFamily && hasPresentFamily)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 教程 isDeviceSuitable() + rateDeviceSuitability()：先筛掉功能不够的，再按设备类型打分
    /// （独显 4 &gt; 虚拟 GPU 3 &gt; 集显 2 &gt; 其它 1）。
    /// </summary>
    private static uint DeviceScore(VKPhysicalDevice device)
    {
        PhysicalDeviceFeatures features = device.Features;
        if (!features.GeometryShader || !features.TessellationShader)
        {
            return 0;
        }

        return device.Properties.DeviceType switch
        {
            PhysicalDeviceType.DiscreteGpu => 4,
            PhysicalDeviceType.VirtualGpu => 3,
            PhysicalDeviceType.IntegratedGpu => 2,
            _ => 1
        };
    }

    /// <summary>
    /// 教程 checkValidationLayerSupport()。
    /// 说明：包装层还没有暴露实例层枚举（vkEnumerateInstanceLayerProperties），
    /// 所以这里直接问一次 Vk —— 它不碰任何资源句柄，只是一次只读查询。
    /// </summary>
    private static unsafe bool CheckValidationLayerSupport()
    {
        uint layerCount = 0;
        VK.EnumerateInstanceLayerProperties(ref layerCount, null);

        LayerProperties[] availableLayers = new LayerProperties[layerCount];
        fixed (LayerProperties* pAvailableLayers = availableLayers)
        {
            VK.EnumerateInstanceLayerProperties(ref layerCount, pAvailableLayers);
        }

        foreach (LayerProperties layerProperties in availableLayers)
        {
            string? layerName = SilkMarshal.PtrToString((nint)layerProperties.LayerName);
            if (layerName == "VK_LAYER_KHRONOS_validation")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 教程的 debugCallback：只打印，不参与后续处理。
    /// 必须是 static —— 它会以函数指针的形式交给驱动，实例方法的目标对象不会被自动保活，
    /// 而驱动的生命周期比本类长。
    /// </summary>
    private static unsafe uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT messageSeverity,
        DebugUtilsMessageTypeFlagsEXT messageTypes,
        DebugUtilsMessengerCallbackDataEXT* pCallbackData,
        void* pUserData)
    {
        if (messageSeverity == DebugUtilsMessageSeverityFlagsEXT.VerboseBitExt)
        {
            return Vk.False;
        }

        string message = SilkMarshal.PtrToString((nint)pCallbackData->PMessage) ?? string.Empty;
        Console.WriteLine($"{messageSeverity} | validation layer: {message}");

        return Vk.False;
    }
}
