using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 图形管线的**固定功能状态**（教程《Fixed functions》章节里 VkGraphicsPipelineCreateInfo 那一堆 pXxxState）的
/// **无指针版**。
///
/// 为什么自己写一个：VkGraphicsPipelineCreateInfo 本身带 10 个子状态结构体指针，其中
///   pVertexInputState / pViewportState / pColorBlendState / pDynamicState
/// 里面又各自带裸指针（顶点描述数组、视口数组、附件数组、动态状态数组），
/// 每个还配一个 count 字段。在 C# 里如果直接传这个结构体，调用方要自己 fixed、自己保证
/// "长度字段和指针字段说的是同一件事"。这里和 ManagedSubpassDescription 一个思路：
/// 指针换集合，集合长度就是 count，转换（fixed / 组装）由包装层在 CreateGraphicsPipeline 里做。
///
/// 另外两件事也顺便在这里解决：
///   · 句柄：教程 VkGraphicsPipelineCreateInfo 里的 layout / renderPass 都是裸句柄 —— 不在这份状态里，
///     layout 由 VKShaderPipelineLayout 自己往下传，renderPass 由 VKRenderPass 自己往下传（可选，null → VK_NULL_HANDLE；
///     见 VKLogicalDevice.CreateGraphicsPipeline）；
///   · flags：create-info 自己的 flags 也放在这里（StageCount / pStages 不在此列 —— 那是**着色器阶段数组**，
///     单独作为参数传，处理方式参考帧缓冲的附件数组）。
///
/// ★ 用法提醒：`default(ManagedGraphicsPipelineState)` **不能直接用** —— C# 会把所有字段清零，
/// 而 Vulkan 要求 VkPipelineRasterizationStateCreateInfo::lineWidth 必须 &gt; 0、
/// VkPipelineMultisampleStateCreateInfo::rasterizationSamples 必须是合法的采样数（1/2/4/…），
/// 零值都会被校验层挑出来。请从 <see cref="CreateDefault"/> 起步按需改，或自己把这两项填上。
/// </summary>
public struct ManagedPipelineStateCreateInfo
{
    // ===== ① 顶点输入（教程《Vertex buffer creation》：pVertexInputState）=====
    // 顶点数据在缓冲里怎么排（binding / stride / 每个顶点还是每个实例），以及每个属性在哪（location / format / offset）。
    // 教程早期版本没有顶点缓冲，这一段就是空的。

    /// <summary>顶点绑定描述：第几号 binding、一条顶点数据多大（stride）、逐顶点还是逐实例。</summary>
    public VertexInputBindingDescription[]? VertexBindings;

    /// <summary>顶点属性描述：着色器里的 location 几、从哪个 binding 取、什么格式、在顶点数据里的偏移。</summary>
    public VertexInputAttributeDescription[]? VertexAttributes;

    // ===== ② 图元装配（pInputAssemblyState）=====

    /// <summary>把顶点组装成什么图元：教程用的是 TriangleList。</summary>
    public PrimitiveTopology Topology;

    /// <summary>是否允许"重启图元"（带索引绘制时用一个特殊索引把前一段图元断开）；教程没开。</summary>
    public bool PrimitiveRestartEnable;

    // ===== ③ 视口 / 裁剪（pViewportState）=====
    // 视口决定"裁剪空间坐标 → 屏幕上的哪块矩形"，裁剪矩形决定"哪块区域的片元才保留"。
    // 教程给的是固定值（由交换链分辨率算出来）；另一种常见做法是声明成动态状态（见 DynamicStates），
    // 那样这里就可以不填 —— Vulkan 允许 viewportCount = 1 而 pViewports = null。

    /// <summary>视口数组；不填 = 只声明 1 个视口但不给内容（要配合 DynamicStates 里的 Viewport）。</summary>
    public Viewport[]? Viewports;

    /// <summary>裁剪矩形数组；不填 = 同上（要配合 DynamicStates 里的 Scissor）。</summary>
    public Rect2D[]? Scissors;

    // ===== ④ 光栅化（pRasterizationState）=====

    /// <summary>深度钳制：把深度值钳到 [0,1] 而不是丢弃（教程说开它得先在设备特性里要 depthClamp）。</summary>
    public bool DepthClampEnable;

    /// <summary>true = 不产生任何片元，光栅化阶段之后直接丢掉（教程说是给"只写深度"之类的场合用）。</summary>
    public bool RasterizerDiscardEnable;

    /// <summary>多边形填充方式：Fill（实心，教程用这个）/ Line（线框）/ Point。</summary>
    public PolygonMode PolygonMode;

    /// <summary>剔除哪一边的面：None / FrontBit / BackBit / FrontAndBack（教程用 BackBit）。</summary>
    public CullModeFlags CullMode;

    /// <summary>哪个绕序算正面：CounterClockwise（Vulkan 默认，也是 OpenGL 的习惯）或 Clockwise（教程选了它）。</summary>
    public FrontFace FrontFace;

    /// <summary>深度偏移开关：做阴影贴图时用来躲开 z-fighting，教程没开。</summary>
    public bool DepthBiasEnable;

    public float DepthBiasConstantFactor;

    public float DepthBiasClamp;

    public float DepthBiasSlopeFactor;

    /// <summary>线宽：**必须 &gt; 0**（不开 wideLines 特性时只能是 1.0f）。</summary>
    public float LineWidth;

    // ===== ⑤ 多重采样（pMultisampleState）=====
    // 教程这章没开多重采样，所以是 1 个采样点、其余全关（深度/模板章之后才会用到 MSAA）。

    /// <summary>采样点数：不开 MSAA 就是 Count1Bit（**不能是 0**）。</summary>
    public SampleCountFlags RasterizationSamples;

    /// <summary>着色器内采样着色（让片元着色器按采样点跑，而不是按片元）；教程没开。</summary>
    public bool SampleShadingEnable;

    public float MinSampleShading;

    public bool AlphaToCoverageEnable;

    public bool AlphaToOneEnable;

    // ===== ⑥ 颜色混合（pColorBlendState）=====
    // 每个颜色附件一条"混合附件状态"；教程只用一个颜色附件，所以数组长度 1。

    /// <summary>逻辑运算开关（老式的位运算混合，和下面的按位混合二选一）；教程没开。</summary>
    public bool LogicOpEnable;

    /// <summary>逻辑运算操作类型：Copy 表示直接覆盖、Clear 表示清成 0 ……（LogicOpEnable 为 false 时无意义）。</summary>
    public LogicOp LogicOp;

    /// <summary>
    /// 每个颜色附件的混合配置（BlendEnable / 源与目标的混合因子 / 运算 / 写入哪些通道）。
    /// **不填 = 一个"不混合、RGBA 全写"的附件**（就是教程里 colorWriteMask = RGB|A、blendEnable = FALSE 那套）。
    /// </summary>
    public PipelineColorBlendAttachmentState[]? ColorBlendAttachments;

    // ===== ⑦ 深度 / 模板（pDepthStencilState）=====
    // 教程《Depth buffering》章之后才用得上；null = 不传这个结构体（Vulkan 允许 pDepthStencilState = null）。

    /// <summary>
    /// 深度 / 模板状态。**null 与"全零的结构体"含义不同**：null 表示 create-info 里那个指针就是 null。
    /// 传结构体时请用带名参数的构造函数（如
    /// `new PipelineDepthStencilStateCreateInfo(depthTestEnable: true, depthWriteEnable: true, depthCompareOp: CompareOp.Less)`），
    /// 否则 SType 会是 0。
    /// </summary>
    public PipelineDepthStencilStateCreateInfo? DepthStencilState;

    // ===== ⑧ 动态状态（pDynamicState）=====
    // 列在里面的状态"创建管线时不用定死，可以每帧用命令缓冲里的 vkCmdSetXxx 改"。
    // 教程用它来省掉"窗口尺寸变了要重建管线"的麻烦（视口 / 裁剪矩形挪到录制时设）；null = 全静态。

    /// <summary>要声明成动态的状态（DynamicState.Viewport / Scissor / LineWidth …）；null = 没有。</summary>
    public DynamicState[]? DynamicStates;

    // ===== ⑨ create-info 自己的 flags =====

    /// <summary>VkGraphicsPipelineCreateInfo::flags（派生管线 / 禁止优化之类），教程用 None。</summary>
    public PipelineCreateFlags Flags;

    /// <summary>
    /// 教程《Fixed functions》里那一套固定功能状态的默认值（除了视口 / 裁剪矩形 ——
    /// 它们要按交换链分辨率算，只有调用方知道，所以留空由调用方填，或者声明成动态状态）。
    /// 也就是：三角形列表 + BackBit 剔除 + Clockwise 正面 + 线宽 1.0 + 1 个采样点 + 单个"不混合、RGBA 全写"的颜色附件。
    /// </summary>
    public static ManagedPipelineStateCreateInfo CreateDefault() => new()
    {
        Topology = PrimitiveTopology.TriangleList,
        PrimitiveRestartEnable = false,
        PolygonMode = PolygonMode.Fill,
        CullMode = CullModeFlags.BackBit,
        FrontFace = FrontFace.Clockwise,
        LineWidth = 1.0f,
        RasterizationSamples = SampleCountFlags.Count1Bit,
        ColorBlendAttachments =
        [
            new PipelineColorBlendAttachmentState(
                blendEnable: false,
                colorWriteMask: ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit)
        ],
        Flags = PipelineCreateFlags.None,
    };
}
