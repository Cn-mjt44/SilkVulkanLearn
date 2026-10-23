using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using SilkTry.Vulkan.ManagedStruct;

namespace SilkTry.Vulkan;

/// <summary>
/// 命令缓冲（教程《Command buffers》/《Rendering and presentation》章节）的包装。
/// 命令缓冲里存的是"要 GPU 做什么"的记录 —— 教程原话是录制一次可以反复提交；
/// 也正因为如此，教程《Command buffers》里那个 SIMULTANEOUS_USE_BIT 之类的用法都落在 Begin 的 flags 上。
///
/// 归属：命令缓冲不是 vkCreateXxx 建出来的，而是从**命令池**里 vkAllocateCommandBuffers 分出来的，
/// 所以父对象是 VKCommandPool（同 VKImageView 的父对象是 VKImage）：
/// 登记表放在池那边（VKCommandBuffer.Valid 就是反查池登记过没有），释放也走池 ——
/// 而且 vkFreeCommandBuffers 本来就必须带池句柄，语义上也只可能由池来管。
///
/// 分工（本仓库的句柄流转约定：**任何对象都不把自己的句柄交出去，谁的句柄类型谁就在自己的类里读写它**）：
///   · 分配：VKCommandPool.AllocateCommandBuffers → VKLogicalDevice.AllocateCommandBuffers（设备执行 Vulkan 调用）
///           → 池把返回的句柄包装成本类并登记（同 VKImage.CreateImageView ↔ VKLogicalDevice.CreateImageView 的分工）；
///   · 释放：Destroy() → VKCommandPool.FreeCommandBuffer（先摘登记）→ VKLogicalDevice.FreeCommandBuffers（设备执行）；
///   · 录制生命周期（Begin / End / Reset）：这三个调用**只用命令缓冲自己的句柄**，所以就在本类里直接做
///     （自己的句柄自己用，本来就不需要交给谁）。
///
/// 划界线：本类做：分配结果的持有、归属校验、Begin / End / Reset、释放，以及教程《Command buffers》
/// 之后各章用到的全部录制命令：
///   · 渲染范围：BeginRenderPass / NextSubpass / EndRenderPass、BeginRendering / EndRendering；
///   · 管线与状态：BindPipeline、SetViewport / SetScissor、SetLineWidth / SetDepthBias / SetBlendConstants、
///     SetDepthTestEnable / SetDepthWriteEnable / SetDepthCompareOp、SetStencilTestEnable / SetStencilOp、
///     SetCullMode / SetFrontFace / SetPrimitiveTopology；
///   · 资源绑定：BindVertexBuffers / BindIndexBuffer、BindDescriptorSets、PushConstants；
///   · 绘制：Draw / DrawIndexed / DrawIndirect / DrawIndexedIndirect；
///   · 传输：CopyBuffer / CopyBufferToImage / CopyImageToBuffer / CopyImage / BlitImage、FillBuffer / UpdateBuffer；
///   · 同步与次级缓冲：PipelineBarrier、ExecuteCommands。
/// 本类的划界线（描述符那一套在哪）：描述符池 / 描述符集由 VKDescriptorPool、VKDescriptorSet 负责
/// （分配、vkUpdateDescriptorSets 的 WriteXxx / CopyTo 都在那边），本类只管录制时的
/// vkCmdBindDescriptorSets（入口是 BindDescriptorSets）。
///
/// 两种"用管线"的路径（对应管线的两个创建入口，见 VKShaderPipeline）：
///   · 固定渲染：管线由 CreateGraphicsRenderPassPipeline 建出来（类型 VKShaderPipeline_RenderPass，带 RenderPass），
///     只能在 BeginRenderPass…EndRenderPass 之间绑，并且那条渲染通道必须就是创建时用的那条；
///   · 动态渲染：管线由 CreateGraphicsDynamicPipeline 建出来（类型 VKShaderPipeline_Dynamic，带 RenderingInfo），
///     只能在 BeginRendering…EndRendering 之间绑 —— 那个入口本身就要求实例 API ≥ 1.3 或设备启用了
///     VK_KHR_dynamic_rendering（Vulkan 另外还要求在创建设备时打开 dynamicRendering 特性，这一步由调用方负责）。
/// 两条路都是同一个 vkCmdBindPipeline（bindPoint 由调用方给），BindPipeline 靠管线类型判断
/// "现在处于哪种渲染范围，管线与它配不配"。
/// </summary>
public partial class VKCommandBuffer
{
    public readonly VKCommandPool Parent;

    /// <summary>
    /// 分配时定下来的级别：
    ///   Primary  —— 可以直接提交到队列（教程用的就是它）；
    ///   Secondary —— 只能被 Primary 命令缓冲用 vkCmdExecuteCommands 调用，录制时还要带上
    ///                CommandBufferBeginInfo::pInheritanceInfo（本类目前没有暴露那个参数）。
    /// </summary>
    public readonly CommandBufferLevel Level;

    /// <summary>
    /// 当前录制的"渲染范围"（两者互斥，都不在时是 null / false）：
    ///   · currentRenderPass —— BeginRenderPass…EndRenderPass 之间，就是那条渲染通道；
    ///   · dynamicRendering  —— BeginRendering…EndRendering 之间。
    /// BindPipeline 靠它们判断"现在能不能绑、绑得对不对"；
    /// Begin / End / Reset 都会把它们清掉（重新录制从头算）。
    /// </summary>
    private VKRenderPass? currentRenderPass;
    private ManagedRenderingInfo? currentDynamicRenderingInfo;

    /// <summary>
    /// 是否处于 Begin…End 之间。录制命令（vkCmdXxx）只有在这段时间里才合法，
    /// 所以所有录制入口都走 CheckedRecordingHandle 做这道检查。
    /// </summary>
    private bool isRecording;

    private readonly CommandBuffer handle;
    private readonly static Vk VK = Vk.GetApi();

    internal VKCommandBuffer(VKCommandPool parent, CommandBuffer handle, CommandBufferLevel level)
    {
        Parent = parent;
        this.handle = handle;
        Level = level;
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象（命令池）有没有登记过自己
    ///（登记发生在 VKCommandPool.AllocateCommandBuffers 里）。
    /// </summary>
    public bool Valid => Parent.IsCommandBufferOfThis(this);

    /// <summary>当前是不是处于 Begin…End 之间（只有这时才能录 vkCmdXxx）。</summary>
    public bool IsRecording => isRecording && Valid;

    private CommandBuffer CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The command buffer is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 录制命令专用：在 CheckedHandle 之上再确认"已经开始录制"，
    /// 免得把 vkCmdXxx 录进一个没 Begin 过的缓冲（Vulkan 会直接报校验错误）。
    /// </summary>
    private CommandBuffer CheckedRecordingHandle
    {
        get
        {
            if (!isRecording)
            {
                throw new InvalidOperationException("The command buffer is not recording; call Begin first.");
            }

            return CheckedHandle;
        }
    }

    /// <summary>
    /// 教程《Command buffers》结尾那句"命令缓冲用完要释放"：vkFreeCommandBuffers。
    /// 和别的包装类一样，把"从池的登记表里摘掉 + 调 vkFreeCommandBuffers"交给父对象做
    ///（池再把自己的句柄传给设备那边执行）。
    /// 注意：池里所有命令缓冲都释放完之前，池本身不能销毁。
    /// </summary>
    public void Destroy()
        => Parent.FreeCommandBuffer(this, CheckedHandle);

    /// <summary>参数是裸句柄（CommandBuffer），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(CommandBuffer handleForCheck)
        => handleForCheck.Handle == handle.Handle;

    /// <summary>
    /// 教程《Command buffers》里的 vkBeginCommandBuffer：开始录制（同时把"渲染范围"状态清零）。
    /// 只用到命令缓冲自己的句柄，所以直接在本类里调（不用麻烦池 / 设备）。
    ///
    /// flags 对应教程那个 VkCommandBufferBeginInfo::flags，默认 None：
    ///   SimultaneousUseBit        —— 允许"上一帧还没画完，这一帧又把同一个命令缓冲提交一遍"
    ///                                （教程在《Rendering and presentation》里解释的；本仓库 Game.cs 就是录一次、每帧重复提交）；
    ///   OneTimeSubmitBit          —— 只提交一次（可以让驱动少做点状态保留）；
    ///   RenderPassContinueBit     —— Secondary 用，表示"这个次级命令缓冲在某个渲染通道内部录制"。
    ///
    /// 注意 CommandBufferBeginInfo 带 void* pInheritanceInfo（Secondary 才用得上），构造函数是 unsafe 的，
    /// 而且必须至少传一个参数（这里传 flags）才会走那个"参数全可选"的构造函数把 SType 填上（同 Game.cs 里那个坑）。
    /// 返回的 Result 原样交给调用方（按本仓库惯例，策略留给调用方）。
    /// </summary>
    public unsafe Result Begin(CommandBufferUsageFlags flags = CommandBufferUsageFlags.None)
    {
        if (isRecording)
        {
            throw new InvalidOperationException(
                "The command buffer is already recording; call End or Reset before beginning again.");
        }

        CommandBufferBeginInfo beginInfo = new(flags: flags);
        Result result = VK.BeginCommandBuffer(CheckedHandle, in beginInfo);
        if (result == Result.Success)
        {
            // 新一次录制从头算：上一次录制残留的"渲染范围"状态不能带进来。
            currentRenderPass = null;
            currentDynamicRenderingInfo = null;
            isRecording = true;
        }

        return result;
    }

    /// <summary>
    /// 教程《Command buffers》里的 vkEndCommandBuffer：结束录制，之后这个命令缓冲才能提交。
    /// 同样是只用自己句柄的调用，直接在本类里做。
    /// 还处在渲染通道 / 动态渲染里时会抛异常（Vulkan 不允许那样结束录制）。
    /// </summary>
    public Result End()
    {
        if (!isRecording)
        {
            throw new InvalidOperationException(
                "The command buffer is not recording; call Begin first.");
        }

        // Vulkan 要求 EndCommandBuffer 之前先把渲染通道 / 动态渲染收尾（否则录出来的是坏缓冲）。
        if (currentRenderPass != null || currentDynamicRenderingInfo != null)
        {
            throw new InvalidOperationException(
                "The command buffer cannot end while a render pass or dynamic rendering is being recorded.");
        }

        Result result = VK.EndCommandBuffer(CheckedHandle);
        if (result == Result.Success)
        {
            isRecording = false;
        }

        currentRenderPass = null;
        currentDynamicRenderingInfo = null;
        return result;
    }

    /// <summary>
    /// 教程《Command buffers》里的 vkCmdBeginRenderPass：开始记录"这一趟渲染通道"
    ///（教程那一章里放在 vkCmdEndRenderPass 之前的几行）。
    ///
    /// 录制本身录在本命令缓冲里，所以入口放在本类；但组装 VkRenderPassBeginInfo 还要 RenderPass 和
    /// Framebuffer 两个句柄，而这两个句柄分别属于 VKRenderPass / VKFramebuffer —— 按本仓库的约定
    /// （谁都不把自己的句柄交出去，只把正确参数往下传），于是整条链是：
    ///   VKCommandBuffer（把自己的 CheckedHandle 传下去）
    ///     → VKRenderPass（把自己的 CheckedHandle 再传下去）
    ///       → VKFramebuffer（读自己的 CheckedHandle，组装 create-info 并调 vkCmdBeginRenderPass）。
    ///
    /// 参数对应教程的 VkRenderPassBeginInfo：
    ///   renderPass  —— 要用哪个渲染通道（它的附件描述决定了 clearValues 的个数和顺序）；
    ///   framebuffer —— 这次渲染实际用哪张图像视图（帧缓冲 = 渲染通道 + 具体附件，两者必须相容）；
    ///   renderArea  —— 渲染区域（教程用全屏：Offset (0,0) + swapchainExtent；教程提醒区域外的像素内容未定义）；
    ///   clearValues —— 清屏值，**顺序和数量**对应渲染通道里那些 loadOp = Clear 的附件
    ///                  （教程只有一个颜色附件，所以长度是 1）；
    ///   contents    —— Inline（默认：绘制命令直接写在本命令缓冲里）或 SecondaryCommandBuffers
    ///                  （子通道的命令写在次级命令缓冲里，见 SubpassContents）。
    ///
    /// 注意 vkCmdBeginRenderPass 本身没有返回值，所以这里也返回 void —— 录错了只能靠校验层报。
    /// </summary>
    public void BeginRenderPass(
        VKFramebuffer framebuffer,
        Rect2D renderArea,
        ClearValue[] clearValues,
        SubpassContents contents = SubpassContents.Inline)
    {
        if (framebuffer == null)
        {
            throw new ArgumentNullException(nameof(framebuffer));
        }

        if (currentDynamicRenderingInfo != null)
        {
            throw new InvalidOperationException(
                "A render pass cannot begin while dynamic rendering is being recorded.");
        }

        if (currentRenderPass != null)
        {
            // Vulkan 不允许在一个渲染通道内部再开一个（必须先 EndRenderPass）。
            throw new InvalidOperationException(
                "A render pass is already being recorded; call EndRenderPass first.");
        }

        framebuffer.BeginRenderPass(this, CheckedRecordingHandle, renderArea, clearValues, contents);

        // 记下"现在在录哪条通道"：BindPipeline 要拿管线的 RenderPass 和它对上。
        currentRenderPass = framebuffer.Parent;
    }

    /// <summary>
    /// 教程《Command buffers》里的 vkCmdEndRenderPass：这一趟渲染通道记录结束。
    /// 它只用到命令缓冲自己的句柄，所以直接在本类里做（同 Begin / End / Reset）——
    /// 不需要麻烦渲染通道 / 帧缓冲，也不需要把句柄交给谁。
    /// 它同样没有返回值；BeginRenderPass / EndRenderPass 必须成对出现 —— 没有正在录的渲染通道时
    /// 本方法会抛异常（而不是把 vkCmdEndRenderPass 直接录进缓冲）。
    /// </summary>
    public void EndRenderPass()
    {
        if (currentRenderPass == null)
        {
            throw new InvalidOperationException("No render pass is being recorded.");
        }

        VK.CmdEndRenderPass(CheckedRecordingHandle);
        currentRenderPass = null;
    }

    /// <summary>
    /// 动态渲染版的 vkCmdBeginRendering（Vulkan 1.3 核心 / VK_KHR_dynamic_rendering 扩展）：
    /// 没有渲染通道对象，附件在这一句里直接给（见 ManagedRenderingInfo）。
    /// 之后用 BindPipeline 绑 CreateGraphicsDynamicPipeline 建出来的管线，最后 EndRendering 收尾。
    ///
    /// 为什么入口在本类：这是**记录到命令缓冲里**的调用，录制本来就写在命令缓冲上；
    /// 附件给的还是包装类 VKImageView（渲染目标），它们的私有句柄由 VKImageView 那边读出来并填成
    /// RenderingAttachmentInfo（同 BeginRenderPass 让 VKRenderPass 收口的分工）。
    ///
    /// 版本 / 扩展的检查放在设备那边（VKLogicalDevice.CmdBeginRendering）：实例 API &lt; 1.3 时
    /// 核心入口不可用，需要设备启用 VK_KHR_dynamic_rendering，否则直接抛 NotSupportedException。
    /// 另外 Vulkan 要求在创建设备时打开 dynamicRendering 特性 —— 那一步不归本类，由调用方负责。
    /// </summary>
    public void BeginRendering(ManagedRenderingInfo renderingInfo)
    {
        if (currentRenderPass != null)
        {
            throw new InvalidOperationException(
                "Dynamic rendering cannot begin while a render pass is being recorded.");
        }

        if (currentDynamicRenderingInfo != null)
        {
            throw new InvalidOperationException("Dynamic rendering is already being recorded.");
        }

        // Vulkan 要求 viewMask 为 0 时 layerCount 必须是 1（default(ManagedRenderingInfo) 正好是 0）。
        if (renderingInfo.ViewMask == 0 && renderingInfo.LayerCount == 0)
        {
            throw new ArgumentException(
                "LayerCount must be 1 when ViewMask is 0; start from ManagedRenderingInfo.CreateDefault.",
                nameof(renderingInfo));
        }

        VKImageView.BeginRendering(this, CheckedRecordingHandle, renderingInfo);
        currentDynamicRenderingInfo = renderingInfo;
    }

    /// <summary>
    /// 动态渲染版的 vkCmdEndRendering：和 BeginRendering 成对出现。
    /// 同样只用命令缓冲自己的句柄；版本 / 扩展的路径选择由设备那边按同一套规则再走一遍
    ///（实例 API ≥ 1.3 用核心入口，否则用 VK_KHR_dynamic_rendering）。
    /// </summary>
    public void EndRendering()
    {
        if (currentDynamicRenderingInfo == null)
        {
            throw new InvalidOperationException("No dynamic rendering is being recorded.");
        }

        Parent.Parent.CmdEndRendering(CheckedRecordingHandle);
        currentDynamicRenderingInfo = null;
    }

    /// <summary>
    /// 绑定图形管线：vkCmdBindPipeline（bindPoint 由调用方给 —— 图形管线一般是 Graphics）。
    ///
    /// 固定 / 动态两条路在这里合流，靠**管线的类型**区分（也就是创建时用了哪个入口）：
    ///   · VKShaderPipeline_RenderPass（CreateGraphicsRenderPassPipeline）：只能在 BeginRenderPass…EndRenderPass
    ///     之间绑，且必须是**同一条**渲染通道（附件格式 / 子通道对不上，校验层会报错）；
    ///   · VKShaderPipeline_Dynamic（CreateGraphicsDynamicPipeline）：只能在 BeginRendering…EndRendering 之间绑，
    ///     并且颜色附件数量 / 格式、深度 / 模板格式要和 BeginRendering 时给的那份一致
    ///（驱动按管线创建时声明的格式去写，对不上就是未定义行为）。
    /// 所以这里检查：
    ///   ① 当前处于某种渲染范围里（渲染通道或动态渲染）；
    ///   ② 管线类型和当前渲染范围对得上；
    ///   ③ 固定渲染：管线的渲染通道就是当前这条；动态渲染：附件数量和格式一致；
    ///   ④ 句柄归属（在 VKShaderPipeline.Bind 里复核）。
    ///（次级命令缓冲那种"继承父级渲染通道"的录制还没做，所以这里只认本缓冲自己的 BeginRenderPass。）
    ///
    /// 句柄：命令缓冲句柄是本类自己的（当参数往 VKShaderPipeline 传），管线句柄由 VKShaderPipeline
    /// 自己在那边读 —— 谁都不把句柄交出去（同 BeginRenderPass 那条链）。
    /// </summary>
    public void BindPipeline(VKShaderPipeline pipeline, PipelineBindPoint bindPoint)
    {
        if (pipeline == null)
        {
            throw new ArgumentNullException(nameof(pipeline));
        }

        if (currentRenderPass == null && currentDynamicRenderingInfo == null)
        {
            throw new InvalidOperationException(
                "BindPipeline can only be used between BeginRenderPass and EndRenderPass, or between BeginRendering and EndRendering.");
        }

        switch (pipeline)
        {
            case VKRenderPassShaderPipeline renderPassPipeline:
                if (renderPassPipeline.RenderPass != currentRenderPass)
                {
                    throw new ArgumentException(
                        "The provided pipeline was created for another render pass (or is a dynamic rendering pipeline).",
                        nameof(pipeline));
                }

                break;

            case VKDynamicShaderPipeline dynamicPipeline:
                if (currentDynamicRenderingInfo == null)
                {
                    throw new ArgumentException(
                        "The provided pipeline was created for dynamic rendering and can only be bound between BeginRendering and EndRendering.",
                        nameof(pipeline));
                }

                CheckDynamicRenderingTarget(dynamicPipeline.RenderingInfo, currentDynamicRenderingInfo.Value, nameof(pipeline));
                break;

            default:
                throw new ArgumentException("Unknown shader pipeline type.", nameof(pipeline));
        }

        pipeline.Bind(this, CheckedRecordingHandle, bindPoint);
    }

    /// <summary>
    /// 动态渲染的"管线格式 vs 实际附件"核对：颜色附件数量必须一致（null 按 0 个算），
    /// 并且每个附件视图的格式要和管线声明的一致；深度 / 模板同理（管线声明了才比）。
    /// </summary>
    private static void CheckDynamicRenderingTarget(
        ManagedPipelineRenderingInfo pipelineInfo,
        ManagedRenderingInfo renderingInfo,
        string parameterName)
    {
        ManagedRenderingAttachmentInfo[] colorAttachments = renderingInfo.ColorAttachments ?? Array.Empty<ManagedRenderingAttachmentInfo>();
        Format[] colorAttachmentFormats = pipelineInfo.ColorAttachmentFormats ?? Array.Empty<Format>();

        if (colorAttachments.Length != colorAttachmentFormats.Length)
        {
            throw new ArgumentException(
                "The provided pipeline declares a different number of color attachments than the current dynamic rendering.",
                parameterName);
        }

        for (int i = 0; i < colorAttachments.Length; i++)
        {
            VKImageView? imageView = colorAttachments[i].ImageView;
            if (imageView != null && imageView.Format != colorAttachmentFormats[i])
            {
                throw new ArgumentException(
                    $"The provided pipeline expects color attachment {i} to be {colorAttachmentFormats[i]}, but the current dynamic rendering target is {imageView.Format}.",
                    parameterName);
            }
        }

        CheckAttachmentFormat(renderingInfo.DepthAttachment, pipelineInfo.DepthAttachmentFormat, "depth", parameterName);
        CheckAttachmentFormat(renderingInfo.StencilAttachment, pipelineInfo.StencilAttachmentFormat, "stencil", parameterName);
    }

    private static void CheckAttachmentFormat(
        ManagedRenderingAttachmentInfo? attachment,
        Format? expectedFormat,
        string attachmentName,
        string parameterName)
    {
        // 管线没声明这个附件（null = Undefined）时不比：那种情况管线本来就不用它。
        if (attachment?.ImageView == null || !expectedFormat.HasValue)
        {
            return;
        }

        if (attachment.Value.ImageView!.Format != expectedFormat.Value)
        {
            throw new ArgumentException(
                $"The provided pipeline expects the {attachmentName} attachment to be {expectedFormat.Value}, but the current dynamic rendering target is {attachment.Value.ImageView.Format}.",
                parameterName);
        }
    }

    /// <summary>
    /// vkCmdBindVertexBuffers：在绘制前绑定一个或多个顶点缓冲（教程《Vertex buffer creation》）。
    ///
    /// 参数对应 vkCmdBindVertexBuffers：
    ///   firstBinding —— 从第几个 binding 开始绑（一般 0）；
    ///   buffers      —— 要绑的缓冲，顺序对应 binding；
    ///   offsets      —— 每个缓冲里"顶点数据从哪里开始"的字节偏移；null = 全 0。
    ///
    /// 句柄：命令缓冲句柄是本类自己的；缓冲句柄由 VKBuffer 那边读（参数里是包装类数组，
    /// 按规范用 internal static 收口），本类不碰任何裸句柄。
    /// </summary>
    public void BindVertexBuffers(uint firstBinding, VKBuffer[] buffers, ulong[]? offsets = null)
    {
        if (buffers == null || buffers.Length == 0)
        {
            throw new ArgumentException("At least one vertex buffer must be bound.", nameof(buffers));
        }

        if (offsets != null && offsets.Length != buffers.Length)
        {
            throw new ArgumentException(
                "The number of offsets must match the number of vertex buffers.",
                nameof(offsets));
        }

        VKBuffer.BindVertexBuffers(
            this,
            CheckedRecordingHandle,
            firstBinding,
            buffers,
            offsets ?? new ulong[buffers.Length]);
    }

    /// <summary>
    /// vkCmdBindVertexBuffers 的单缓冲便捷版：等价于 firstBinding = binding、offsets = [offset]。
    /// </summary>
    public void BindVertexBuffer(VKBuffer buffer, ulong offset = 0, uint binding = 0)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        BindVertexBuffers(binding, new[] { buffer }, new[] { offset });
    }

    /// <summary>
    /// vkCmdBindIndexBuffer：绑定索引缓冲（教程《Index buffer》）。
    /// offset 是索引数据在缓冲里的字节偏移；indexType 是索引的宽度（教程用 Uint16 / Uint32）。
    /// </summary>
    public void BindIndexBuffer(VKBuffer buffer, ulong offset, IndexType indexType = IndexType.Uint32)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        buffer.BindIndexBuffer(this, CheckedRecordingHandle, offset, indexType);
    }

    /// <summary>
    /// vkCmdDraw：按当前绑定的顶点缓冲画（教程《Drawing a triangle》里那句）。
    /// instanceCount / firstInstance 是实例化绘制用的，教程用默认的 1 / 0。
    /// </summary>
    public void Draw(
        uint vertexCount,
        uint instanceCount = 1,
        uint firstVertex = 0,
        uint firstInstance = 0)
        => VK.CmdDraw(CheckedRecordingHandle, vertexCount, instanceCount, firstVertex, firstInstance);

    /// <summary>
    /// vkCmdDrawIndexed：用索引缓冲画（教程《Index buffer》最后那句）。
    /// vertexOffset 会加到每个索引上（用来复用同一份索引数据画多块网格）。
    /// </summary>
    public void DrawIndexed(
        uint indexCount,
        uint instanceCount = 1,
        uint firstIndex = 0,
        int vertexOffset = 0,
        uint firstInstance = 0)
        => VK.CmdDrawIndexed(
            CheckedRecordingHandle,
            indexCount,
            instanceCount,
            firstIndex,
            vertexOffset,
            firstInstance);

    /// <summary>
    /// vkCmdDrawIndirect：绘制参数（vertexCount / instanceCount / firstVertex / firstInstance）
    /// 从缓冲里读，每份参数占 stride 字节 —— 适合 GPU 自己生成绘制列表的场合。
    /// 缓冲必须以 IndirectBufferBit 创建。
    /// </summary>
    public void DrawIndirect(VKBuffer buffer, ulong offset, uint drawCount, uint stride)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        buffer.DrawIndirect(this, CheckedRecordingHandle, offset, drawCount, stride);
    }

    /// <summary>vkCmdDrawIndexedIndirect：同上，参数结构换成 DrawIndexedIndirectCommand。</summary>
    public void DrawIndexedIndirect(VKBuffer buffer, ulong offset, uint drawCount, uint stride)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        buffer.DrawIndexedIndirect(this, CheckedRecordingHandle, offset, drawCount, stride);
    }

    /// <summary>
    /// vkCmdSetViewport：设置动态视口（管线创建时把 Viewport 列进 DynamicStates 才用它）。
    /// 数组版对应 viewportCount &gt; 1（多视口/分层渲染）。
    /// </summary>
    public void SetViewport(Viewport viewport, uint firstViewport = 0)
        => SetViewport(new[] { viewport }, firstViewport);

    /// <summary>vkCmdSetViewport 的数组版：一次设置从 firstViewport 开始的多个视口。</summary>
    public void SetViewport(Viewport[] viewports, uint firstViewport = 0)
    {
        if (viewports == null || viewports.Length == 0)
        {
            throw new ArgumentException("At least one viewport must be provided.", nameof(viewports));
        }

        VK.CmdSetViewport(CheckedRecordingHandle, firstViewport, viewports);
    }

    /// <summary>vkCmdSetScissor：设置动态裁剪矩形（同样要管线里声明成动态状态）。</summary>
    public void SetScissor(Rect2D scissor, uint firstScissor = 0)
        => SetScissor(new[] { scissor }, firstScissor);

    /// <summary>vkCmdSetScissor 的数组版：一次设置从 firstScissor 开始的多个裁剪矩形。</summary>
    public void SetScissor(Rect2D[] scissors, uint firstScissor = 0)
    {
        if (scissors == null || scissors.Length == 0)
        {
            throw new ArgumentException("At least one scissor rectangle must be provided.", nameof(scissors));
        }

        VK.CmdSetScissor(CheckedRecordingHandle, firstScissor, scissors);
    }

    /// <summary>vkCmdSetLineWidth：动态线宽（管线里声明 DynamicState.LineWidth 时用；宽线要先开 wideLines 特性）。</summary>
    public void SetLineWidth(float lineWidth)
        => VK.CmdSetLineWidth(CheckedRecordingHandle, lineWidth);

    /// <summary>vkCmdSetDepthBias：动态深度偏移（管线里声明 DynamicState.DepthBias 时用）。</summary>
    public void SetDepthBias(float constantFactor, float clamp = 0f, float slopeFactor = 0f)
        => VK.CmdSetDepthBias(CheckedRecordingHandle, constantFactor, clamp, slopeFactor);

    /// <summary>vkCmdSetBlendConstants：动态混合常量（管线里声明 DynamicState.BlendConstants 时用）。</summary>
    public unsafe void SetBlendConstants(float red, float green, float blue, float alpha)
    {
        float[] constants = { red, green, blue, alpha };
        fixed (float* pConstants = constants)
        {
            VK.CmdSetBlendConstants(CheckedRecordingHandle, pConstants);
        }
    }

    /// <summary>vkCmdSetDepthTestEnable：动态开关深度测试（DynamicState.DepthTestEnable）。</summary>
    public void SetDepthTestEnable(bool depthTestEnable)
        => VK.CmdSetDepthTestEnable(CheckedRecordingHandle, depthTestEnable);

    /// <summary>vkCmdSetDepthWriteEnable：动态开关深度写入（DynamicState.DepthWriteEnable）。</summary>
    public void SetDepthWriteEnable(bool depthWriteEnable)
        => VK.CmdSetDepthWriteEnable(CheckedRecordingHandle, depthWriteEnable);

    /// <summary>vkCmdSetDepthCompareOp：动态深度比较函数（DynamicState.DepthCompareOp）。</summary>
    public void SetDepthCompareOp(CompareOp compareOp)
        => VK.CmdSetDepthCompareOp(CheckedRecordingHandle, compareOp);

    /// <summary>vkCmdSetStencilTestEnable：动态开关模板测试（DynamicState.StencilTestEnable）。</summary>
    public void SetStencilTestEnable(bool stencilTestEnable)
        => VK.CmdSetStencilTestEnable(CheckedRecordingHandle, stencilTestEnable);

    /// <summary>vkCmdSetStencilOp：动态设置模板操作（DynamicState.StencilOp）。</summary>
    public void SetStencilOp(
        StencilFaceFlags faceMask,
        StencilOp failOp,
        StencilOp passOp,
        StencilOp depthFailOp,
        CompareOp compareOp)
        => VK.CmdSetStencilOp(
            CheckedRecordingHandle,
            faceMask,
            failOp,
            passOp,
            depthFailOp,
            compareOp);

    /// <summary>vkCmdSetCullMode：动态设置剔除模式（DynamicState.CullMode）。</summary>
    public void SetCullMode(CullModeFlags cullMode)
        => VK.CmdSetCullMode(CheckedRecordingHandle, cullMode);

    /// <summary>vkCmdSetFrontFace：动态设置正面绕序（DynamicState.FrontFace）。</summary>
    public void SetFrontFace(FrontFace frontFace)
        => VK.CmdSetFrontFace(CheckedRecordingHandle, frontFace);

    /// <summary>vkCmdSetPrimitiveTopology：动态设置图元拓扑（DynamicState.PrimitiveTopology）。</summary>
    public void SetPrimitiveTopology(PrimitiveTopology primitiveTopology)
        => VK.CmdSetPrimitiveTopology(CheckedRecordingHandle, primitiveTopology);

    /// <summary>
    /// vkCmdPushConstants：把一小段数据直接推进管线（教程《Uniform buffer》之后的 push constant 用法）。
    ///
    /// 对应 GLSL 的 `layout(push_constant) uniform Push { ... } pc;`（它不进描述符集 / 描述符池）。
    /// data 的布局必须和布局里声明的 PushConstantRange（stageFlags / offset / size）对得上；
    /// offset 是相对于该范围的起点。Vulkan 要求数据长度是 4 的倍数。
    ///
    /// 句柄：命令缓冲句柄是本类自己的；管线布局句柄由 VKShaderPipelineLayout 那边读
    ///（"谁的句柄类型，谁就在自己的类里读写它"）。
    /// </summary>
    public void PushConstants(
        VKShaderPipelineLayout layout,
        ShaderStageFlags stageFlags,
        uint offset,
        ReadOnlySpan<byte> data)
    {
        if (layout == null)
        {
            throw new ArgumentNullException(nameof(layout));
        }

        if (data.Length % 4 != 0)
        {
            throw new ArgumentException("Push constant data must be a multiple of 4 bytes.", nameof(data));
        }

        layout.PushConstants(this, CheckedRecordingHandle, stageFlags, offset, data);
    }

    /// <summary>
    /// vkCmdBindDescriptorSets：录制时把描述符集挂到当前管线上（教程《Descriptor pool and sets》的最后一步）。
    ///
    /// 参数：
    ///   bindPoint      —— 绑给图形还是计算管线（要和 BindPipeline 时一致）；
    ///   layout         —— 当前管线用的那份**管线布局**（描述符集就是按它的 set 顺序挂上去的）；
    ///   firstSet       —— descriptorSets[0] 对应布局里的第几个 set（教程只有一套，所以是 0；
    ///                     对着 GLSL 的 layout(set = N)，不写 set 时是 0）；
    ///   descriptorSets —— 要绑的集，顺序对应布局里第 firstSet、firstSet+1……个 set；
    ///   dynamicOffsets —— Dynamic 描述符（UniformBufferDynamic / StorageBufferDynamic）的偏移，不传 = 没有
    ///                     （GLSL 里没有 Dynamic 的写法，这种类型是 API 侧选的）。
    ///
    /// 句柄：命令缓冲句柄是本类自己的；布局句柄由 VKShaderPipelineLayout 那边读、各集句柄由
    /// VKDescriptorSet 的收口读（"谁的句柄类型，谁就在自己的类里读写它"）。
    /// 与 BindPipeline 不同，这里不要求处在渲染范围内 —— 计算管线在渲染范围外绑集是合法的。
    /// </summary>
    public void BindDescriptorSets(
        PipelineBindPoint bindPoint,
        VKShaderPipelineLayout layout,
        uint firstSet,
        VKDescriptorSet[] descriptorSets,
        uint[]? dynamicOffsets = null)
    {
        if (layout == null)
        {
            throw new ArgumentNullException(nameof(layout));
        }

        if (descriptorSets == null || descriptorSets.Length == 0)
        {
            throw new ArgumentException("At least one descriptor set must be provided.", nameof(descriptorSets));
        }

        layout.BindDescriptorSets(this, CheckedRecordingHandle, bindPoint, firstSet, descriptorSets, dynamicOffsets);
    }

    /// <summary>
    /// vkCmdCopyBuffer：缓冲到缓冲的拷贝（教程《Vertex buffer creation》/《Texture image》里的
    /// staging → GPU 缓冲）。regions 里每一项描述源 / 目标偏移和长度。
    /// </summary>
    public void CopyBuffer(VKBuffer source, VKBuffer destination, BufferCopy[] regions)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (regions == null || regions.Length == 0)
        {
            throw new ArgumentException("At least one copy region must be provided.", nameof(regions));
        }

        source.CopyBuffer(this, CheckedRecordingHandle, destination, regions);
    }

    /// <summary>
    /// vkCmdCopyBufferToImage：把缓冲里的像素拷进图像（教程《Texture image》里的 staging → image）。
    /// destinationImageLayout 一般是 TransferDstOptimal（调用前要先自己用 PipelineBarrier 转过去）。
    /// </summary>
    public void CopyBufferToImage(
        VKBuffer source,
        VKImage destination,
        ImageLayout destinationImageLayout,
        BufferImageCopy[] regions)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (regions == null || regions.Length == 0)
        {
            throw new ArgumentException("At least one copy region must be provided.", nameof(regions));
        }

        source.CopyToImage(this, CheckedRecordingHandle, destination, destinationImageLayout, regions);
    }

    /// <summary>vkCmdCopyImageToBuffer：图像 → 缓冲（读回像素、截图之类）。</summary>
    public void CopyImageToBuffer(
        VKImage source,
        ImageLayout sourceImageLayout,
        VKBuffer destination,
        BufferImageCopy[] regions)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (regions == null || regions.Length == 0)
        {
            throw new ArgumentException("At least one copy region must be provided.", nameof(regions));
        }

        source.CopyToBuffer(this, CheckedRecordingHandle, sourceImageLayout, destination, regions);
    }

    /// <summary>vkCmdCopyImage：图像 → 图像（格式必须相容，不能顺便转换格式）。</summary>
    public void CopyImage(
        VKImage source,
        ImageLayout sourceImageLayout,
        VKImage destination,
        ImageLayout destinationImageLayout,
        ImageCopy[] regions)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (regions == null || regions.Length == 0)
        {
            throw new ArgumentException("At least one copy region must be provided.", nameof(regions));
        }

        source.CopyTo(
            this,
            CheckedRecordingHandle,
            sourceImageLayout,
            destination,
            destinationImageLayout,
            regions);
    }

    /// <summary>vkCmdBlitImage：图像 → 图像，允许缩放和格式转换（filter 决定放大 / 缩小时怎么取样）。</summary>
    public void BlitImage(
        VKImage source,
        ImageLayout sourceImageLayout,
        VKImage destination,
        ImageLayout destinationImageLayout,
        ImageBlit[] regions,
        Filter filter = Filter.Linear)
    {
        if (source == null)
        {
            throw new ArgumentNullException(nameof(source));
        }

        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (regions == null || regions.Length == 0)
        {
            throw new ArgumentException("At least one blit region must be provided.", nameof(regions));
        }

        source.BlitTo(
            this,
            CheckedRecordingHandle,
            sourceImageLayout,
            destination,
            destinationImageLayout,
            regions,
            filter);
    }

    /// <summary>
    /// vkCmdFillBuffer：用 32 位值填满缓冲的一段（大小必须是 4 的倍数；
    /// Vk.WholeSize 表示"从 offset 一直到末尾"）。常用来清空 / 初始化 GPU 缓冲。
    /// </summary>
    public void FillBuffer(VKBuffer buffer, ulong offset, ulong size, uint data = 0)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        buffer.Fill(this, CheckedRecordingHandle, offset, size, data);
    }

    /// <summary>
    /// vkCmdUpdateBuffer：从主机内存直接更新缓冲的一小段（Vulkan 限制 ≤ 65536 字节，
    /// 且长度必须是 4 的倍数）。适合少量数据；大块数据走 staging + CopyBuffer。
    /// </summary>
    public void UpdateBuffer(VKBuffer buffer, ulong offset, ReadOnlySpan<byte> data)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (data.Length % 4 != 0)
        {
            throw new ArgumentException("Update data must be a multiple of 4 bytes.", nameof(data));
        }

        if (data.Length > 65536)
        {
            throw new ArgumentException("vkCmdUpdateBuffer accepts at most 65536 bytes.", nameof(data));
        }

        buffer.Update(this, CheckedRecordingHandle, offset, data);
    }

    /// <summary>
    /// vkCmdPipelineBarrier（内存屏障版）：只同步"内存可见性 / 执行顺序"，不涉及具体资源。
    /// memoryBarriers 为空时就是一道全局屏障（全局内存依赖）。
    /// </summary>
    public void PipelineBarrier(
        PipelineStageFlags sourceStageMask,
        PipelineStageFlags destinationStageMask,
        DependencyFlags dependencyFlags = DependencyFlags.None,
        MemoryBarrier[]? memoryBarriers = null)
        => VK.CmdPipelineBarrier(
            CheckedRecordingHandle,
            sourceStageMask,
            destinationStageMask,
            dependencyFlags,
            memoryBarriers ?? Array.Empty<MemoryBarrier>(),
            ReadOnlySpan<BufferMemoryBarrier>.Empty,
            ReadOnlySpan<ImageMemoryBarrier>.Empty);

    /// <summary>
    /// vkCmdPipelineBarrier 的**图像布局转换**版（教程《Texture image》里 transitionImageLayout 那句）：
    /// 把图像的某个子资源范围从 oldLayout 转到 newLayout，同时建立
    /// "sourceStageMask → destinationStageMask" 的依赖。
    /// 访问掩码按教程习惯默认 None；需要精细同步时再显式给。
    /// 句柄由 VKImage 那边读，本类只传命令缓冲自己的句柄。
    /// </summary>
    public void PipelineBarrier(
        VKImage image,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        ImageSubresourceRange subresourceRange,
        PipelineStageFlags sourceStageMask,
        PipelineStageFlags destinationStageMask,
        AccessFlags sourceAccessMask = AccessFlags.None,
        AccessFlags destinationAccessMask = AccessFlags.None,
        DependencyFlags dependencyFlags = DependencyFlags.None,
        uint sourceQueueFamilyIndex = Vk.QueueFamilyIgnored,
        uint destinationQueueFamilyIndex = Vk.QueueFamilyIgnored)
    {
        if (image == null)
        {
            throw new ArgumentNullException(nameof(image));
        }

        image.PipelineBarrier(
            this,
            CheckedRecordingHandle,
            oldLayout,
            newLayout,
            subresourceRange,
            sourceStageMask,
            destinationStageMask,
            sourceAccessMask,
            destinationAccessMask,
            dependencyFlags,
            sourceQueueFamilyIndex,
            destinationQueueFamilyIndex);
    }

    /// <summary>
    /// vkCmdPipelineBarrier 的**缓冲内存屏障**版：同步某段缓冲的访问（例如
    /// "主机写入 → 顶点缓冲读取"，或者传输写入 → 着色器读取）。
    /// </summary>
    public void PipelineBarrier(
        VKBuffer buffer,
        PipelineStageFlags sourceStageMask,
        PipelineStageFlags destinationStageMask,
        AccessFlags sourceAccessMask,
        AccessFlags destinationAccessMask,
        ulong offset = 0,
        ulong size = Vk.WholeSize,
        DependencyFlags dependencyFlags = DependencyFlags.None,
        uint sourceQueueFamilyIndex = Vk.QueueFamilyIgnored,
        uint destinationQueueFamilyIndex = Vk.QueueFamilyIgnored)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        buffer.PipelineBarrier(
            this,
            CheckedRecordingHandle,
            sourceStageMask,
            destinationStageMask,
            sourceAccessMask,
            destinationAccessMask,
            offset,
            size,
            dependencyFlags,
            sourceQueueFamilyIndex,
            destinationQueueFamilyIndex);
    }

    /// <summary>
    /// vkCmdExecuteCommands：执行一组 Secondary 命令缓冲（把次级缓冲里录好的命令并进本缓冲）。
    /// 只允许 Primary 缓冲调用；每个次级缓冲必须是 Secondary 级别，且同属一个逻辑设备。
    /// 次级缓冲在创建 / 录制时所需的 pInheritanceInfo 还没有开放，所以这里的次级缓冲
    /// 只能录制"不继承渲染通道"的命令。
    /// </summary>
    public void ExecuteCommands(params VKCommandBuffer[] commandBuffers)
    {
        if (commandBuffers == null || commandBuffers.Length == 0)
        {
            throw new ArgumentException("At least one secondary command buffer must be provided.", nameof(commandBuffers));
        }

        if (Level != CommandBufferLevel.Primary)
        {
            throw new InvalidOperationException("Only a primary command buffer can execute secondary command buffers.");
        }

        CommandBuffer[] handles = new CommandBuffer[commandBuffers.Length];
        for (int i = 0; i < commandBuffers.Length; i++)
        {
            VKCommandBuffer secondary = commandBuffers[i]
                ?? throw new ArgumentException("A secondary command buffer cannot be null.", nameof(commandBuffers));

            if (ReferenceEquals(secondary, this))
            {
                throw new ArgumentException("A command buffer cannot execute itself.", nameof(commandBuffers));
            }

            if (secondary.Level != CommandBufferLevel.Secondary)
            {
                throw new ArgumentException(
                    "Only secondary command buffers can be executed.",
                    nameof(commandBuffers));
            }

            if (secondary.Parent.Parent != Parent.Parent)
            {
                throw new ArgumentException(
                    "The provided command buffer is not from the same logical device.",
                    nameof(commandBuffers));
            }

            // 同一个类里的不同实例可以读彼此的私有句柄，不需要把句柄"交出来"。
            handles[i] = secondary.CheckedHandle;
        }

        VK.CmdExecuteCommands(CheckedRecordingHandle, handles);
    }

    /// <summary>
    /// vkCmdNextSubpass：进入渲染通道的下一个子通道（多子通道 / 延迟渲染时用）。
    /// 必须在 BeginRenderPass…EndRenderPass 之间调用。
    /// </summary>
    public void NextSubpass(SubpassContents contents = SubpassContents.Inline)
    {
        if (currentRenderPass == null)
        {
            throw new InvalidOperationException("No render pass is being recorded.");
        }

        VK.CmdNextSubpass(CheckedRecordingHandle, contents);
    }

    /// <summary>
    /// 教程没细讲的 vkResetCommandBuffer：把命令缓冲退回"可以重新录制"的状态。
    /// 想让它在录制中被重置（也就是释放录制时占用的资源），命令池创建时得开
    /// CommandPoolCreateFlags.ResetCommandBufferBit（见 VKLogicalDevice.CreateCommandPool），
    /// 否则只能等 vkResetCommandPool 或者在释放后重录。
    /// flags 默认 None（只把缓冲退回初始状态，尽量保留内部资源）；
    /// ReleaseResourcesBit 表示"顺手把资源也还回去"。
    /// </summary>
    public Result Reset(CommandBufferResetFlags flags = CommandBufferResetFlags.None)
    {
        Result result = VK.ResetCommandBuffer(CheckedHandle, flags);
        if (result == Result.Success)
        {
            // 重置失败时缓冲还停在原状态，录制状态也得跟着保留（同 Begin）。
            currentRenderPass = null;
            currentDynamicRenderingInfo = null;
            isRecording = false;
        }

        return result;
    }
}

public partial class VKCommandPool
{
    

    // 从这个池里分出去的命令缓冲。写法和 VKImage 那边的 imageViews 一样：分配时登记进来，
    // 之后靠 IsCommandBufferOfThis() 反查归属（VKCommandBuffer.Valid 就是这么来的）、释放时摘掉。
    // 值用不上 —— vkFreeCommandBuffers 没有 allocationCallbacks 参数（不像 vkDestroyImage 那些），
    // 这里只当"集合"用，写法上保持和别的登记表一致。
    private readonly ConcurrentDictionary<VKCommandBuffer, byte> commandBuffers = new ();

    #region 命令缓冲（教程《Command buffers》章节）

    /// <summary>
    /// 教程《Command buffers》里 createCommandBuffers() 的 vkAllocateCommandBuffers：
    /// 本类处理"从本池里分配命令缓冲"的请求。
    ///
    /// 句柄怎么凑齐（本仓库的访问性约定：**谁都不把自己的句柄交出去，只把正确参数往下传**）：
    ///   · 池句柄是本类自己的，这里当参数传给 VKLogicalDevice（它那边再复核一次）；
    ///   · Device 句柄由 VKLogicalDevice 自己持有，Vulkan 调用在那一层执行；
    ///   · 新建的命令缓冲句柄由那一层**返回**（"操作结果"），本类负责包装 + 登记。
    /// 也就是 VKImage.CreateImageView ↔ VKLogicalDevice.CreateImageView 那套分工。
    ///
    /// 参数：count 至少是 1（教程是一次按交换链图像数量分配）；level 见 VKCommandBuffer.Level。
    /// 调用方拿到的是包装好的 VKCommandBuffer，外面一个裸句柄都不用拿。
    /// </summary>
    public VKCommandBuffer[] AllocateCommandBuffers(
        uint count = 1,
        CommandBufferLevel level = CommandBufferLevel.Primary)
    {
        // 自己的句柄当参数传下去（传入正确参数 = 授权），设备那边会再校验一次。
        CommandBuffer[] handles = Parent.AllocateCommandBuffers(this, CheckedHandle, count, level);

        VKCommandBuffer[] result = new VKCommandBuffer[handles.Length];
        for (int i = 0; i < handles.Length; i++)
        {
            VKCommandBuffer commandBuffer = new(this, handles[i], level);
            commandBuffers[commandBuffer] = 0;
            result[i] = commandBuffer;
        }

        return result;
    }

    /// <summary>命令缓冲的归属校验：本池分配出去的才算 —— VKCommandBuffer.Valid 就是这么来的。</summary>
    public bool IsCommandBufferOfThis(VKCommandBuffer commandBuffer)
        => commandBuffers.ContainsKey(commandBuffer) && Valid;

    /// <summary>
    /// vkFreeCommandBuffers：登记表在本类手里（命令缓冲是"从本池里分出来的"），所以先校验 + 摘登记，
    /// 再把自己的句柄和命令缓冲自己的句柄一起交给设备那边执行（同 VKImage.DestroyImageView 的分工）。
    /// </summary>
    internal bool FreeCommandBuffer(VKCommandBuffer commandBuffer, CommandBuffer handle)
    {
        if (!commandBuffer.IsHandleOfThis(handle) || !commandBuffers.TryRemove(commandBuffer, out _))
        {
            return false;
        }

        Parent.FreeCommandBuffers(this, CheckedHandle, commandBuffer, handle);
        return true;
    }

    #endregion
}


public partial class VKLogicalDevice
{
    #region 命令缓冲（教程《Command buffers》章节）

    /// <summary>
    /// 教程 createCommandBuffers() 里的 vkAllocateCommandBuffers：真正的设备调用在这里（本类持着 Device 句柄）。
    ///
    /// 分工（同 CreateImageView）：池那边（VKCommandPool）把自己的 CheckedHandle 当参数传下来，
    /// 这里用 IsHandleOfThis 复核"确实是它"，然后本类用自己的 Device 句柄执行 Vulkan 调用；
    /// **新建的句柄由本类返回**（"操作结果"），包装成 VKCommandBuffer、以及登记（VKCommandBuffer.Valid
    /// 的数据来源）都由池做，因为命令缓冲的父对象是池。
    /// </summary>
    internal unsafe CommandBuffer[] AllocateCommandBuffers(
        VKCommandPool commandPool,
        CommandPool commandPoolHandle,
        uint count,
        CommandBufferLevel level)
    {
        Device device = CheckedHandle;

        if (!commandPool.IsHandleOfThis(commandPoolHandle))
        {
            throw new ArgumentException(
                "The provided command pool handle is not associated with the given VKCommandPool.");
        }

        if (count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "At least one command buffer must be allocated.");
        }

        CommandBuffer[] handles = new CommandBuffer[(int)count];
        // pCommandBuffers 只在本次调用里被写，所以 fixed 钉住就够（同 CreateImage / CreateFramebuffer）。
        fixed (CommandBuffer* pCommandBuffers = handles)
        {
            CommandBufferAllocateInfo allocateInfo = new(
                commandPool: commandPoolHandle,
                level: level,
                commandBufferCount: count);

            if (VK.AllocateCommandBuffers(device, in allocateInfo, pCommandBuffers) != Result.Success)
            {
                throw new NotSupportedException("failed to allocate command buffers!");
            }
        }

        return handles;
    }

    /// <summary>
    /// vkFreeCommandBuffers（写法同 DestroyImageView）：池句柄由 VKCommandPool 自己当参数传下来、
    /// 命令缓冲句柄由 VKCommandBuffer 自己当参数传下来，这里各用它们自己的 IsHandleOfThis 复核一遍 ——
    /// 两个句柄都是"传进来的正确参数"，不是"谁交出来的"。
    /// 注意本类只是执行释放，顺序（池销毁前先把命令缓冲都释放掉）由调用方负责。
    /// </summary>
    internal unsafe void FreeCommandBuffers(
        VKCommandPool commandPool,
        CommandPool commandPoolHandle,
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle)
    {
        Device device = CheckedHandle;

        if (!commandPool.IsHandleOfThis(commandPoolHandle))
        {
            throw new ArgumentException(
                "The provided command pool handle is not associated with the given VKCommandPool.");
        }

        if (!commandBuffer.IsHandleOfThis(commandBufferHandle))
        {
            throw new ArgumentException(
                "The provided command buffer handle is not associated with the given VKCommandBuffer.");
        }

        VK.FreeCommandBuffers(device, commandPoolHandle, 1, &commandBufferHandle);
    }

    #endregion

    #region 动态渲染（Vulkan 1.3 / VK_KHR_dynamic_rendering）

    /// <summary>
    /// 动态渲染能不能用：实例 API ≥ 1.3（核心入口 vkCmdBeginRendering），或者创建设备时**启用了**
    /// VK_KHR_dynamic_rendering 扩展（同名入口，走扩展对象）。
    /// 这里只看"入口有没有"：能不能调用还要看 VkPhysicalDeviceDynamicRenderingFeatures::dynamicRendering
    /// 这个特性开没开 —— 那要求 pNext 链，本仓库创建设备时还没做，所以由调用方保证。
    /// 现算不缓存：父对象失效时答案也就不作数了（同 Valid 的规矩）。
    /// </summary>
    public bool SupportsDynamicRendering
        => Parent.Parent.ApiVersion >= Vk.Version13
           || EnabledDeviceExtensions.Contains(KhrDynamicRendering.ExtensionName);

    /// <summary>
    /// vkCmdBeginRendering 的"版本分流"：实例 API ≥ 1.3 用核心入口，否则用 VK_KHR_dynamic_rendering 扩展，
    /// 两者都没有就抛 NotSupportedException。
    /// 命令缓冲句柄由 VKCommandBuffer（它自己的句柄）当参数传下来；
    /// RenderingInfo 里的附件句柄已由 VKImageView 读出来填好 —— 本类只做"调用哪一边"的选择。
    /// 之所以是 internal：入参里带的是裸句柄，按访问性约定不进 public 签名。
    /// </summary>
    internal void CmdBeginRendering(CommandBuffer commandBufferHandle, ref RenderingInfo renderingInfo)
    {
        if (Parent.Parent.ApiVersion >= Vk.Version13)
        {
            VK.CmdBeginRendering(commandBufferHandle, ref renderingInfo);
        }
        else if (TryGetDeviceExtension<KhrDynamicRendering>(out var dynamicRendering))
        {
            dynamicRendering.CmdBeginRendering(commandBufferHandle, ref renderingInfo);
        }
        else
        {
            throw new NotSupportedException(
                "Dynamic rendering requires Vulkan 1.3 or the VK_KHR_dynamic_rendering extension.");
        }
    }

    /// <summary>
    /// vkCmdEndRendering 的"版本分流"（规则同 CmdBeginRendering：核心入口优先，其次扩展）。
    /// </summary>
    internal void CmdEndRendering(CommandBuffer commandBufferHandle)
    {
        if (Parent.Parent.ApiVersion >= Vk.Version13)
        {
            VK.CmdEndRendering(commandBufferHandle);
        }
        else if (TryGetDeviceExtension<KhrDynamicRendering>(out var dynamicRendering))
        {
            dynamicRendering.CmdEndRendering(commandBufferHandle);
        }
        else
        {
            throw new NotSupportedException(
                "Dynamic rendering requires Vulkan 1.3 or the VK_KHR_dynamic_rendering extension.");
        }
    }

    #endregion
}

public partial class VKRenderPass
{
    /// <summary>
    /// vkCmdBeginRenderPass 链路的**最后一环**：组装 VkRenderPassBeginInfo 并执行（教程《Command buffers》）。
    /// 本类不把自己的句柄交出去 —— 这里读的是自己实例的 CheckedHandle
    ///（"谁的句柄类型，谁就在自己的类里读写它"）；渲染通道句柄和命令缓冲句柄都是它们各自
    /// **当参数**传下来的正确参数（不是"交出来的"），下面逐个复核。
    ///
    /// 为什么由本类收口：帧缓冲本来就是"渲染通道 + 具体附件"，而 vkCmdBeginRenderPass 要的两样东西里
    /// 帧缓冲是主角（渲染通道必须和它相容），所以放在这里最自然。
    /// 之所以只能是 internal：入参里带的是裸句柄，按访问性约定不能出包装层；
    /// 外面要录这一句就直接调 VKCommandBuffer.BeginRenderPass，那里一个裸句柄都不用拿。
    /// </summary>
    internal unsafe void BeginRenderPass(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        VKFramebuffer framebuffer,
        Framebuffer framebufferHandle,
        Rect2D renderArea,
        ClearValue[] clearValues,
        SubpassContents contents)
    {
        RenderPass renderPassHandle = CheckedHandle;

        if (!commandBuffer.IsHandleOfThis(commandBufferHandle))
        {
            throw new ArgumentException(
                "The provided command buffer handle is not associated with the given VKCommandBuffer.");
        }

        if (!framebuffer.IsHandleOfThis(framebufferHandle))
        {
            throw new ArgumentException(
                "The provided framebuffer handle is not associated with the given VKFramebuffer.");
        }

        if (!Parent.IsCommandPoolOfThis(commandBuffer.Parent))
        {
            throw new ArgumentException(
                "The provided command buffer is not from a command pool of this logical device.");
        }

        // pClearValues 只在本次调用里被读，所以 fixed 钉住就够（同 CreateFramebuffer / AllocateCommandBuffers）。
        fixed (ClearValue* pClearValues = clearValues)
        {
            RenderPassBeginInfo renderPassBeginInfo = new(
                renderPass: renderPassHandle,
                framebuffer: framebufferHandle,
                renderArea: renderArea,
                clearValueCount: (uint)clearValues.Length,
                pClearValues: pClearValues);

            VK.CmdBeginRenderPass(commandBufferHandle, in renderPassBeginInfo, contents);
        }
    }
}

public partial class VKFramebuffer
{
    
    /// <summary>
    /// vkCmdBeginRenderPass 链路里的**中间一环**（和 CreateFramebuffer 一样的分工：
    /// 谁持着句柄谁就在自己的类里把它**当参数**往下传，不返回、不外泄）：
    /// 本类把自己的 RenderPass 句柄交给帧缓冲那边的入口，由它组装 VkRenderPassBeginInfo 并执行。
    ///
    /// 校验：命令缓冲句柄由 VKCommandBuffer 自己当参数传进来，这里用它的 IsHandleOfThis 复核；
    /// 帧缓冲是本类同一个逻辑设备下的对象，用 Parent（VKLogicalDevice）的 IsFramebufferOfThis 确认。
    /// 之所以只能是 internal：入参里带的是裸句柄，按访问性约定不能出包装层。
    /// </summary>
    internal void BeginRenderPass(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        Rect2D renderArea,
        ClearValue[] clearValues,
        SubpassContents contents)
        => Parent.BeginRenderPass(commandBuffer, commandBufferHandle, this, CheckedHandle, renderArea, clearValues, contents);
}

public partial class VKImageView
{
    /// <summary>
    /// 动态渲染里 vkCmdBeginRendering 那一环的收口：把 ManagedRenderingInfo 里的附件（包装类 VKImageView）
    /// 转换成裸 RenderingAttachmentInfo 并执行（同 VKRenderPass.BeginRenderPass 之于固定渲染）。
    ///
    /// 为什么收口在本类：附件是两个图像视图句柄（渲染目标 + 解析目标），而句柄只有 VKImageView 自己读得到
    ///（"谁的句柄类型，谁就在自己的类里读写它"）；命令缓冲句柄则由 VKCommandBuffer 自己当参数传下来。
    /// 版本 / 扩展的分流（核心 1.3 入口 vs VK_KHR_dynamic_rendering）交给设备那边做，
    /// 本类只负责填结构体。
    /// 之所以只能是 internal：入参 / 局部变量里带的是裸句柄，按访问性约定不能出包装层。
    /// </summary>
    internal static unsafe void BeginRendering(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        ManagedRenderingInfo renderingInfo)
    {
        if (!commandBuffer.IsHandleOfThis(commandBufferHandle))
        {
            throw new ArgumentException(
                "The provided command buffer handle is not associated with the given VKCommandBuffer.");
        }

        ManagedRenderingAttachmentInfo[] colorAttachments = renderingInfo.ColorAttachments ?? Array.Empty<ManagedRenderingAttachmentInfo>();

        // 颜色附件数量不定，先转成托管数组再 fixed（空数组 fixed 得到空指针，正好表达"没有颜色附件"）。
        RenderingAttachmentInfo[] colorAttachmentInfos = new RenderingAttachmentInfo[colorAttachments.Length];
        for (int i = 0; i < colorAttachments.Length; i++)
        {
            colorAttachmentInfos[i] = ToRenderingAttachmentInfo(colorAttachments[i]);
        }

        fixed (RenderingAttachmentInfo* pColorAttachments = colorAttachmentInfos)
        {
            // 深度 / 模板附件最多各一个：取局部变量的地址就够，只在本次调用里被读。
            RenderingAttachmentInfo depthAttachment = default;
            RenderingAttachmentInfo* pDepthAttachment = null;
            if (renderingInfo.DepthAttachment.HasValue)
            {
                depthAttachment = ToRenderingAttachmentInfo(renderingInfo.DepthAttachment.Value);
                pDepthAttachment = &depthAttachment;
            }

            RenderingAttachmentInfo stencilAttachment = default;
            RenderingAttachmentInfo* pStencilAttachment = null;
            if (renderingInfo.StencilAttachment.HasValue)
            {
                stencilAttachment = ToRenderingAttachmentInfo(renderingInfo.StencilAttachment.Value);
                pStencilAttachment = &stencilAttachment;
            }

            RenderingInfo rawRenderingInfo = new(
                flags: renderingInfo.Flags,
                renderArea: renderingInfo.RenderArea,
                layerCount: renderingInfo.LayerCount,
                viewMask: renderingInfo.ViewMask,
                colorAttachmentCount: (uint)colorAttachments.Length,
                pColorAttachments: pColorAttachments,
                pDepthAttachment: pDepthAttachment,
                pStencilAttachment: pStencilAttachment);

            commandBuffer.Parent.Parent.CmdBeginRendering(commandBufferHandle, ref rawRenderingInfo);
        }
    }

    /// <summary>
    /// 把包装类的附件转换成裸结构体：两张图像视图的句柄在这里读出来（null → VK_NULL_HANDLE）。
    /// </summary>
    private static unsafe RenderingAttachmentInfo ToRenderingAttachmentInfo(ManagedRenderingAttachmentInfo attachment)
    {
        ImageView imageViewHandle = attachment.ImageView != null ? attachment.ImageView.CheckedHandle : default;
        ImageView resolveImageViewHandle = attachment.ResolveImageView != null ? attachment.ResolveImageView.CheckedHandle : default;

        return new RenderingAttachmentInfo(
            imageView: imageViewHandle,
            imageLayout: attachment.ImageLayout,
            resolveMode: attachment.ResolveMode,
            resolveImageView: resolveImageViewHandle,
            resolveImageLayout: attachment.ResolveImageLayout,
            loadOp: attachment.LoadOp,
            storeOp: attachment.StoreOp,
            clearValue: attachment.ClearValue);
    }
}

public partial class VKShaderPipeline
{
    private readonly static Vk VK = Vk.GetApi();

    /// <summary>
    /// vkCmdBindPipeline（PipelineBindPoint.Graphics）的**最后一环**：本类读自己的 Pipeline 句柄并执行。
    ///
    /// 只做句柄级的事情 —— "固定 / 动态"的语义检查（管线类型、当前处于哪种渲染范围、附件格式配不配、
    /// 版本够不够）都在 VKCommandBuffer.BindPipeline（以及管线的创建入口）里做，它调用本方法。
    ///
    /// 校验：命令缓冲句柄由 VKCommandBuffer 自己当参数传进来（IsHandleOfThis 复核）；
    /// 命令缓冲所属的逻辑设备必须就是管线所属的那个（同一个设备才能互相用）。
    /// 之所以只能是 internal：入参里带的是裸句柄，按访问性约定不进 public 签名。
    /// </summary>
    internal void Bind(VKCommandBuffer commandBuffer, CommandBuffer commandBufferHandle, PipelineBindPoint bindPoint)
    {
        if (!commandBuffer.IsHandleOfThis(commandBufferHandle))
        {
            throw new ArgumentException(
                "The provided command buffer handle is not associated with the given VKCommandBuffer.");
        }

        if (commandBuffer.Parent.Parent != Parent.Parent)
        {
            throw new ArgumentException(
                "The provided command buffer is not from the logical device that owns the pipeline.");
        }

        VK.CmdBindPipeline(commandBufferHandle, bindPoint, CheckedHandle);
    }
}

public partial class VKBuffer
{
    private readonly static Vk VK = Vk.GetApi();

    /// <summary>
    /// 录制命令的公共前置检查：命令缓冲句柄是它自己的（IsHandleOfThis 复核），
    /// 并且和本资源属于同一个逻辑设备。
    /// </summary>
    private static void CheckCommandBuffer(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        VKLogicalDevice device)
    {
        if (!commandBuffer.IsHandleOfThis(commandBufferHandle))
        {
            throw new ArgumentException(
                "The provided command buffer handle is not associated with the given VKCommandBuffer.");
        }

        if (commandBuffer.Parent.Parent != device)
        {
            throw new ArgumentException(
                "The provided command buffer is not from the logical device that owns the buffer.");
        }
    }

    private static void CheckBufferDevice(VKBuffer buffer, VKLogicalDevice device, string parameterName)
    {
        if (buffer == null)
        {
            throw new ArgumentException("A buffer cannot be null.", parameterName);
        }

        if (buffer.Device != device)
        {
            throw new ArgumentException(
                "The provided buffer is not from the logical device that owns the command buffer.",
                parameterName);
        }
    }

    /// <summary>
    /// vkCmdBindVertexBuffers 的收口（参数里是包装类数组，按规范放在句柄所属的类里做 internal static）：
    /// 逐个读各缓冲自己的 CheckedHandle，本类不把任何句柄交给外面。
    /// </summary>
    internal static void BindVertexBuffers(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        uint firstBinding,
        VKBuffer[] buffers,
        ulong[] offsets)
    {
        VKLogicalDevice device = commandBuffer.Parent.Parent;
        CheckCommandBuffer(commandBuffer, commandBufferHandle, device);

        Silk.NET.Vulkan.Buffer[] handles = new Silk.NET.Vulkan.Buffer[buffers.Length];
        for (int i = 0; i < buffers.Length; i++)
        {
            CheckBufferDevice(buffers[i], device, nameof(buffers));
            handles[i] = buffers[i].CheckedHandle;
        }

        VK.CmdBindVertexBuffers(commandBufferHandle, firstBinding, handles, offsets);
    }

    /// <summary>vkCmdBindIndexBuffer：单缓冲，读自己的句柄即可。</summary>
    internal void BindIndexBuffer(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        ulong offset,
        IndexType indexType)
    {
        Silk.NET.Vulkan.Buffer bufferHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle, Device);

        VK.CmdBindIndexBuffer(commandBufferHandle, bufferHandle, offset, indexType);
    }

    /// <summary>vkCmdDrawIndirect / vkCmdDrawIndexedIndirect 的公共部分。</summary>
    internal void DrawIndirect(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        ulong offset,
        uint drawCount,
        uint stride)
    {
        Silk.NET.Vulkan.Buffer bufferHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle, Device);

        VK.CmdDrawIndirect(commandBufferHandle, bufferHandle, offset, drawCount, stride);
    }

    internal void DrawIndexedIndirect(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        ulong offset,
        uint drawCount,
        uint stride)
    {
        Silk.NET.Vulkan.Buffer bufferHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle, Device);

        VK.CmdDrawIndexedIndirect(commandBufferHandle, bufferHandle, offset, drawCount, stride);
    }

    /// <summary>
    /// vkCmdCopyBuffer：源 / 目标都是本类型，同一个类的不同实例可以读彼此的私有句柄。
    /// </summary>
    internal void CopyBuffer(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        VKBuffer destination,
        ReadOnlySpan<BufferCopy> regions)
    {
        Silk.NET.Vulkan.Buffer sourceHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle, Device);
        CheckBufferDevice(destination, Device, nameof(destination));
        Silk.NET.Vulkan.Buffer destinationHandle = destination.CheckedHandle;

        VK.CmdCopyBuffer(commandBufferHandle, sourceHandle, destinationHandle, regions);
    }

    /// <summary>
    /// vkCmdCopyBufferToImage 链路的第一环：本类读自己的缓冲句柄，
    /// 再交给 VKImage（那边读图像句柄并执行调用）。
    /// </summary>
    internal void CopyToImage(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        VKImage destination,
        ImageLayout destinationImageLayout,
        ReadOnlySpan<BufferImageCopy> regions)
    {
        Silk.NET.Vulkan.Buffer bufferHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle, Device);

        destination.CopyFromBuffer(
            commandBuffer,
            commandBufferHandle,
            bufferHandle,
            destinationImageLayout,
            regions);
    }

    /// <summary>
    /// vkCmdCopyImageToBuffer 的最后一环：图像句柄由 VKImage 当参数传过来，
    /// 本类读自己的缓冲句柄并执行调用。
    /// </summary>
    internal void CopyFromImage(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        Image sourceImageHandle,
        ImageLayout sourceImageLayout,
        ReadOnlySpan<BufferImageCopy> regions)
    {
        Silk.NET.Vulkan.Buffer bufferHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle, Device);

        VK.CmdCopyImageToBuffer(
            commandBufferHandle,
            sourceImageHandle,
            sourceImageLayout,
            bufferHandle,
            regions);
    }

    /// <summary>vkCmdFillBuffer：只用自己句柄的调用，直接在这里做。</summary>
    internal void Fill(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        ulong offset,
        ulong size,
        uint data)
    {
        Silk.NET.Vulkan.Buffer bufferHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle, Device);

        VK.CmdFillBuffer(commandBufferHandle, bufferHandle, offset, size, data);
    }

    /// <summary>vkCmdUpdateBuffer：主机数据用 fixed 钉住，只在这次调用里被读。</summary>
    internal unsafe void Update(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        ulong offset,
        ReadOnlySpan<byte> data)
    {
        Silk.NET.Vulkan.Buffer bufferHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle, Device);

        fixed (byte* pData = data)
        {
            VK.CmdUpdateBuffer(commandBufferHandle, bufferHandle, offset, (ulong)data.Length, pData);
        }
    }

    /// <summary>vkCmdPipelineBarrier 的缓冲内存屏障版：结构体里的 Silk.NET.Vulkan.Buffer 句柄在这里填。</summary>
    internal unsafe void PipelineBarrier(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        PipelineStageFlags sourceStageMask,
        PipelineStageFlags destinationStageMask,
        AccessFlags sourceAccessMask,
        AccessFlags destinationAccessMask,
        ulong offset,
        ulong size,
        DependencyFlags dependencyFlags,
        uint sourceQueueFamilyIndex,
        uint destinationQueueFamilyIndex)
    {
        Silk.NET.Vulkan.Buffer bufferHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle, Device);

        BufferMemoryBarrier barrier = new(
            srcAccessMask: sourceAccessMask,
            dstAccessMask: destinationAccessMask,
            srcQueueFamilyIndex: sourceQueueFamilyIndex,
            dstQueueFamilyIndex: destinationQueueFamilyIndex,
            buffer: bufferHandle,
            offset: offset,
            size: size);

        VK.CmdPipelineBarrier(
            commandBufferHandle,
            sourceStageMask,
            destinationStageMask,
            dependencyFlags,
            ReadOnlySpan<MemoryBarrier>.Empty,
            new ReadOnlySpan<BufferMemoryBarrier>(in barrier),
            ReadOnlySpan<ImageMemoryBarrier>.Empty);
    }
}

public partial class VKImage
{
    private readonly static Vk VK = Vk.GetApi();

    /// <summary>录制命令的公共前置检查（命令缓冲句柄 + 同一逻辑设备）。</summary>
    private void CheckCommandBuffer(VKCommandBuffer commandBuffer, CommandBuffer commandBufferHandle)
    {
        if (!commandBuffer.IsHandleOfThis(commandBufferHandle))
        {
            throw new ArgumentException(
                "The provided command buffer handle is not associated with the given VKCommandBuffer.");
        }

        if (commandBuffer.Parent.Parent != Parent.Parent)
        {
            throw new ArgumentException(
                "The provided command buffer is not from the logical device that owns the image.");
        }
    }

    /// <summary>
    /// vkCmdCopyBufferToImage 的最后一环：缓冲句柄由 VKBuffer 当参数传下来，本类读自己的图像句柄。
    /// </summary>
    internal void CopyFromBuffer(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        Silk.NET.Vulkan.Buffer sourceBufferHandle,
        ImageLayout destinationImageLayout,
        ReadOnlySpan<BufferImageCopy> regions)
    {
        Image imageHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle);

        VK.CmdCopyBufferToImage(
            commandBufferHandle,
            sourceBufferHandle,
            imageHandle,
            destinationImageLayout,
            regions);
    }

    /// <summary>
    /// vkCmdCopyImageToBuffer 链路的第一环：本类读自己的图像句柄，再交给 VKBuffer 收口。
    /// </summary>
    internal void CopyToBuffer(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        ImageLayout sourceImageLayout,
        VKBuffer destination,
        ReadOnlySpan<BufferImageCopy> regions)
    {
        Image imageHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle);

        destination.CopyFromImage(
            commandBuffer,
            commandBufferHandle,
            imageHandle,
            sourceImageLayout,
            regions);
    }

    /// <summary>
    /// vkCmdCopyImage：源 / 目标都是本类型，同一个类的不同实例可以读彼此的私有句柄。
    /// </summary>
    internal void CopyTo(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        ImageLayout sourceImageLayout,
        VKImage destination,
        ImageLayout destinationImageLayout,
        ReadOnlySpan<ImageCopy> regions)
    {
        Image sourceHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle);

        if (destination.Parent.Parent != Parent.Parent)
        {
            throw new ArgumentException(
                "The provided destination image is not from the same logical device.",
                nameof(destination));
        }

        Image destinationHandle = destination.CheckedHandle;

        VK.CmdCopyImage(
            commandBufferHandle,
            sourceHandle,
            sourceImageLayout,
            destinationHandle,
            destinationImageLayout,
            regions);
    }

    /// <summary>vkCmdBlitImage：同 CopyTo，只是允许缩放 / 格式转换。</summary>
    internal void BlitTo(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        ImageLayout sourceImageLayout,
        VKImage destination,
        ImageLayout destinationImageLayout,
        ReadOnlySpan<ImageBlit> regions,
        Filter filter)
    {
        Image sourceHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle);

        if (destination.Parent.Parent != Parent.Parent)
        {
            throw new ArgumentException(
                "The provided destination image is not from the same logical device.",
                nameof(destination));
        }

        Image destinationHandle = destination.CheckedHandle;

        VK.CmdBlitImage(
            commandBufferHandle,
            sourceHandle,
            sourceImageLayout,
            destinationHandle,
            destinationImageLayout,
            regions,
            filter);
    }

    /// <summary>vkCmdPipelineBarrier 的图像布局转换版：结构体里的 Image 句柄在这里填。</summary>
    internal unsafe void PipelineBarrier(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        ImageLayout oldLayout,
        ImageLayout newLayout,
        ImageSubresourceRange subresourceRange,
        PipelineStageFlags sourceStageMask,
        PipelineStageFlags destinationStageMask,
        AccessFlags sourceAccessMask,
        AccessFlags destinationAccessMask,
        DependencyFlags dependencyFlags,
        uint sourceQueueFamilyIndex,
        uint destinationQueueFamilyIndex)
    {
        Image imageHandle = CheckedHandle;
        CheckCommandBuffer(commandBuffer, commandBufferHandle);

        ImageMemoryBarrier barrier = new(
            srcAccessMask: sourceAccessMask,
            dstAccessMask: destinationAccessMask,
            oldLayout: oldLayout,
            newLayout: newLayout,
            srcQueueFamilyIndex: sourceQueueFamilyIndex,
            dstQueueFamilyIndex: destinationQueueFamilyIndex,
            image: imageHandle,
            subresourceRange: subresourceRange);

        VK.CmdPipelineBarrier(
            commandBufferHandle,
            sourceStageMask,
            destinationStageMask,
            dependencyFlags,
            ReadOnlySpan<MemoryBarrier>.Empty,
            ReadOnlySpan<BufferMemoryBarrier>.Empty,
            new ReadOnlySpan<ImageMemoryBarrier>(in barrier));
    }
}

public partial class VKShaderPipelineLayout
{

    /// <summary>
    /// vkCmdBindDescriptorSets 的"管线布局这一环"：本类读自己的 PipelineLayout 句柄，
    /// 命令缓冲句柄由 VKCommandBuffer 当参数传下来，各描述符集的句柄再交给 VKDescriptorSet 的收口读。
    /// 调用链：
    ///   VKCommandBuffer.BindDescriptorSets(…)   ← public、零裸句柄
    ///     → 本方法                             读自己的布局句柄（这就是"授权"）
    ///       → VKDescriptorSet.BindDescriptorSets(…)   internal static 收口：读各集的私有句柄 + 执行
    /// 参数（bindPoint / firstSet / dynamicOffsets）原样对应 vulkan 的那几个：
    ///   bindPoint      —— 绑给图形还是计算管线；
    ///   firstSet       —— descriptorSets[0] 对应管线布局里的第几个 set（对着 GLSL 的 layout(set = N)）；
    ///   dynamicOffsets —— Dynamic 描述符（UniformBufferDynamic / StorageBufferDynamic）用的偏移数组，
    ///                     不传 = 没有动态描述符（GLSL 没有 Dynamic 写法，是 API 侧选的类型）。
    /// </summary>
    internal void BindDescriptorSets(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        PipelineBindPoint bindPoint,
        uint firstSet,
        VKDescriptorSet[] descriptorSets,
        uint[]? dynamicOffsets)
    {
        PipelineLayout layoutHandle = CheckedHandle;

        if (!commandBuffer.IsHandleOfThis(commandBufferHandle))
        {
            throw new ArgumentException(
                "The provided command buffer handle is not associated with the given VKCommandBuffer.");
        }

        if (commandBuffer.Parent.Parent != Parent)
        {
            throw new ArgumentException(
                "The provided command buffer is not from the logical device that owns the pipeline layout.");
        }

        VKDescriptorSet.BindDescriptorSets(
            commandBuffer,
            commandBufferHandle,
            this,
            layoutHandle,
            bindPoint,
            firstSet,
            descriptorSets,
            dynamicOffsets);
    }
    /// <summary>
    /// vkCmdPushConstants 的最后一环：本类读自己的 PipelineLayout 句柄，
    /// 命令缓冲句柄由 VKCommandBuffer 当参数传下来。
    /// </summary>
    internal unsafe void PushConstants(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        ShaderStageFlags stageFlags,
        uint offset,
        ReadOnlySpan<byte> data)
    {
        PipelineLayout layoutHandle = CheckedHandle;

        if (!commandBuffer.IsHandleOfThis(commandBufferHandle))
        {
            throw new ArgumentException(
                "The provided command buffer handle is not associated with the given VKCommandBuffer.");
        }

        if (commandBuffer.Parent.Parent != Parent)
        {
            throw new ArgumentException(
                "The provided command buffer is not from the logical device that owns the pipeline layout.");
        }

        fixed (byte* pData = data)
        {
            VK.CmdPushConstants(
                commandBufferHandle,
                layoutHandle,
                stageFlags,
                offset,
                (uint)data.Length,
                pData);
        }
    }
}

public partial class VKDescriptorSet
{
    /// <summary>
    /// vkCmdBindDescriptorSets 的收口（参数里是包装类数组，按规范放在句柄所属的类里做 internal static）：
    /// 逐个读各描述符集自己的 CheckedHandle，本类不把任何句柄交给外面。
    /// PipelineLayout 句柄由 VKShaderPipelineLayout 当参数传下来（它才是那个句柄的持有者）。
    /// firstSet 对应 GLSL 的 layout(set = N)：descriptorSets[0] 挂到第 firstSet 个 set 上。
    /// </summary>
    internal static void BindDescriptorSets(
        VKCommandBuffer commandBuffer,
        CommandBuffer commandBufferHandle,
        VKShaderPipelineLayout pipelineLayout,
        PipelineLayout layoutHandle,
        PipelineBindPoint bindPoint,
        uint firstSet,
        VKDescriptorSet[] descriptorSets,
        uint[]? dynamicOffsets)
    {
        VKLogicalDevice device = commandBuffer.Parent.Parent;

        if (!commandBuffer.IsHandleOfThis(commandBufferHandle))
        {
            throw new ArgumentException(
                "The provided command buffer handle is not associated with the given VKCommandBuffer.");
        }

        ReadOnlyCollection<VKDescriptorSetLayout>? pipelineSetLayouts = pipelineLayout.DescriptorSetLayouts;
        if (pipelineSetLayouts == null)
        {
            throw new ArgumentException(
                "The provided pipeline layout was created without descriptor set layouts.",
                nameof(pipelineLayout));
        }

        DescriptorSet[] setHandles = new DescriptorSet[descriptorSets.Length];
        for (int i = 0; i < descriptorSets.Length; i++)
        {
            VKDescriptorSet descriptorSet = descriptorSets[i];
            if (descriptorSet == null)
            {
                throw new ArgumentException("A descriptor set cannot be null.", nameof(descriptorSets));
            }

            if (descriptorSet.Device != device)
            {
                throw new ArgumentException(
                    "The provided descriptor set is not from the logical device that owns the command buffer.",
                    nameof(descriptorSets));
            }

            // Vulkan 要求绑上去的集，其布局与管线布局里第 firstSet + i 个 set 的布局"matching"
            //（同一份，或定义完全相同的另一份）—— 详见 VKDescriptorSetLayout.IsCompatibleWith。
            uint setIndex = firstSet + (uint)i;
            if (setIndex >= pipelineSetLayouts.Count)
            {
                throw new ArgumentException(
                    $"The pipeline layout only declares {pipelineSetLayouts.Count} descriptor set(s); " +
                    $"the descriptor set at index {setIndex} (firstSet + {i}) does not exist in it.",
                    nameof(firstSet));
            }

            if (!descriptorSet.Layout.IsCompatibleWith(pipelineSetLayouts[(int)setIndex]))
            {
                throw new ArgumentException(
                    "The provided descriptor set was allocated from a layout that is not compatible with the pipeline layout.",
                    nameof(descriptorSets));
            }

            setHandles[i] = descriptorSet.CheckedHandle;
        }

        VK.CmdBindDescriptorSets(
            commandBufferHandle,
            bindPoint,
            layoutHandle,
            firstSet,
            setHandles,
            dynamicOffsets ?? Array.Empty<uint>());
    }
}