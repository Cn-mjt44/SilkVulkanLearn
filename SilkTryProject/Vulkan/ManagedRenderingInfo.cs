using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 动态渲染（Vulkan 1.3 核心 / VK_KHR_dynamic_rendering 扩展）里 VkRenderingInfo 的**无指针版**。
///
/// 为什么要自己写一个：VkRenderingInfo 里带裸指针 —— pColorAttachments / pDepthAttachment / pStencilAttachment，
/// 颜色附件还配一个 count。直接用它的话调用方要自己 fixed、自己保证"长度字段和指针字段说的是同一件事"。
/// 这里把指针换成集合（同 ManagedSubpassDescription / ManagedPipelineStateCreateInfo 的思路），
/// 集合长度就是 count；真正的转换（填裸结构体 + 钉住）由 VKCommandBuffer.BeginRendering 那条链里的
/// VKImageView 做 —— 因为附件句柄属于 VKImageView（"谁的句柄类型，谁就在自己的类里读写它"）。
///
/// 和渲染通道的区别（这也是"固定 / 动态"两种用法的分界）：
///   · 渲染通道（固定）：附件格式 / load·store 都在创建渲染通道时烘死，配合 CreateGraphicsPipeline 时
///     传了 RenderPass 的管线使用（VKCommandBuffer.BindPipeline）；
///   · 动态渲染（本类型）：没有渲染通道对象，附件（就是这里的 RenderingAttachmentInfo[]）在**每次录制**时给，
///     配合 CreateGraphicsDynamicPipeline 建出来的管线使用（VKCommandBuffer.BindPipeline）。
///
/// ★ 用法提醒：`default(ManagedRenderingInfo)` 的 LayerCount 是 0 —— 而 Vulkan 要求 viewMask 为 0 时
///   layerCount 必须是 1。所以从 <see cref="CreateDefault"/> 起步，再改需要的字段。
/// </summary>
public struct ManagedRenderingInfo
{
    /// <summary>VkRenderingInfo::flags（保留，目前没有用得上的）。</summary>
    public RenderingFlags Flags;

    /// <summary>渲染区域（同渲染通道的 renderArea：区域外的像素内容未定义）。</summary>
    public Rect2D RenderArea;

    /// <summary>层数；viewMask 为 0 时必须是 1（CreateDefault 已经填好）。</summary>
    public uint LayerCount;

    /// <summary>多视图渲染的视图掩码（VR 之类才用得上）；0 = 不用多视图。</summary>
    public uint ViewMask;

    /// <summary>颜色附件：下标就是着色器里的 output location；空集合 = 没有颜色附件。</summary>
    public ManagedRenderingAttachmentInfo[]? ColorAttachments;

    /// <summary>深度附件；null = 这次渲染没有深度附件。</summary>
    public ManagedRenderingAttachmentInfo? DepthAttachment;

    /// <summary>模板附件；null = 这次渲染没有模板附件。</summary>
    public ManagedRenderingAttachmentInfo? StencilAttachment;

    /// <summary>
    /// 给一份能直接用的初值：只有 renderArea 是必填的，其余按"单层 / 无多视图 / 没有附件"起步
    ///（同 ManagedPipelineStateCreateInfo.CreateDefault 的用意）。
    /// </summary>
    public static ManagedRenderingInfo CreateDefault(Rect2D renderArea) => new()
    {
        RenderArea = renderArea,
        LayerCount = 1,
    };
}

/// <summary>
/// 动态渲染管线的附件格式声明（VkPipelineRenderingCreateInfo 的**无指针版**）。
///
/// 固定渲染的管线从渲染通道里"读出"附件格式；动态渲染没有渲染通道，所以**建管线时**必须自己把
/// 那些格式报给驱动 —— 就是这个结构（通过 pNext 挂到 VkGraphicsPipelineCreateInfo 上）。
/// 它和录制时的 <see cref="ManagedRenderingInfo"/> 是两回事：这里只说"格式"，具体是哪张图、
/// load / store 怎么做，是 BeginRendering 时的事。
///
/// 对应关系：
///   ColorAttachmentFormats     → colorAttachmentCount + pColorAttachmentFormats（下标 = 着色器 output location）
///   DepthAttachmentFormat      → depthAttachmentFormat（null = Format.Undefined，没有深度附件）
///   StencilAttachmentFormat    → stencilAttachmentFormat（null = Format.Undefined，没有模板附件）
///   ViewMask                   → 多视图渲染（VR 之类）；0 = 不用
/// </summary>
public struct ManagedPipelineRenderingInfo
{
    /// <summary>多视图渲染的视图掩码；0 = 不用多视图（普通渲染）。</summary>
    public uint ViewMask;

    /// <summary>颜色附件格式，下标对应着色器里的 output location；null / 空 = 没有颜色附件。</summary>
    public Format[]? ColorAttachmentFormats;

    /// <summary>深度附件格式；null = 没有深度附件。</summary>
    public Format? DepthAttachmentFormat;

    /// <summary>模板附件格式；null = 没有模板附件。</summary>
    public Format? StencilAttachmentFormat;
}

/// <summary>
/// 动态渲染的单个附件（VkRenderingAttachmentInfo 的**包装类版**）。
///
/// 和裸结构体唯一的区别是那两张图像视图：这里存 VKImageView 包装类，**不拥有**它们
///（视图要活得比录制出来的命令久）—— 裸句柄只有 VKImageView 自己能读，转换在它那边做。
///
/// 默认值（default）就是可用的：LoadOp = Load、StoreOp = Store（枚举的 0 值正好是这两个），
/// ImageLayout 记得按实际用法填（通常 ColorAttachmentOptimal / DepthStencilAttachmentOptimal）。
/// </summary>
public struct ManagedRenderingAttachmentInfo
{
    /// <summary>要渲染到的图像视图；null = VK_NULL_HANDLE（没有附件）。</summary>
    public VKImageView? ImageView;

    /// <summary>渲染期间这张图要处于的布局。</summary>
    public ImageLayout ImageLayout;

    /// <summary>多采样解析方式；None（0）= 不解析。</summary>
    public ResolveModeFlags ResolveMode;

    /// <summary>解析目标图像视图；null = VK_NULL_HANDLE（不解析）。</summary>
    public VKImageView? ResolveImageView;

    /// <summary>解析目标在渲染期间的布局。</summary>
    public ImageLayout ResolveImageLayout;

    /// <summary>进入这次渲染时怎么处理已有内容：Load（默认）/ Clear / DontCare。</summary>
    public AttachmentLoadOp LoadOp;

    /// <summary>渲染结束时怎么处理内容：Store（默认）/ DontCare。</summary>
    public AttachmentStoreOp StoreOp;

    /// <summary>LoadOp = Clear 时用的清屏值。</summary>
    public ClearValue ClearValue;
}
