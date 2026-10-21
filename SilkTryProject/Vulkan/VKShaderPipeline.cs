using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 图形管线（教程《Graphics pipeline》/《Fixed functions》章节）的包装。
/// 管线 = 教程那一大段 createGraphicsPipeline() 的结果：把**着色器阶段**（哪个阶段用哪个模块）
/// 和**一大串固定功能状态**（顶点输入 / 图元装配 / 视口 / 光栅化 / 多重采样 / 颜色混合 ……）
/// 烘成驱动能直接吃的一个对象。之后录制命令时用 vkCmdBindPipeline 把它挂上去，再 vkCmdDraw。
///
/// 归属：**父对象是 VKShaderPipelineLayout** —— 管线是"那份布局的管线"：布局描述它要接哪些资源
///（描述符集布局 / push constant），管线正是按那份布局去接的。所以创建入口就在布局上
///（VKShaderPipelineLayout.CreateGraphicsPipeline）、登记表也在它那边（Valid 靠
/// VKShaderPipelineLayout.IsShaderPipelineOfThis 反查）—— 同 VKBufferView 之于 VKBuffer、
/// VKImageView 之于 VKImage。
///（vkCreateGraphicsPipelines / vkDestroyPipeline 本身仍是设备级调用，真正的 Vulkan 调用
///  由 VKLogicalDevice 执行 —— 它才持着 Device 句柄；布局是发起方 + 登记方。）
///
/// 本类手里只有**一个**句柄（Pipeline）—— 这一点是特别拆出来的结果：原先本类一个身位塞了
/// "管线 + 布局"两个句柄，和别的包装类"一个类 = 一个句柄"的风格不一致，所以把布局拆成了独立的
/// VKShaderPipelineLayout（并且它就是本类的父级）。教程说布局要活得比管线久，所以布局的 Destroy()
/// 会先销毁本类，再销毁布局自己（同 VKImage 先销毁自己视图的约定）。
///
/// 管线本身是抽象的：Vulkan 的两条路各对应一个子类，由创建入口决定拿到哪一个 ——
///   · VKShaderPipeline_RenderPass —— CreateGraphicsRenderPassPipeline：附件格式写在渲染通道里，
///     渲染通道句柄放进 create-info，子类的 RenderPass 记着它；
///   · VKShaderPipeline_Dynamic    —— CreateGraphicsDynamicPipeline：renderPass = VK_NULL_HANDLE，
///     附件格式改由 VkPipelineRenderingCreateInfo（pNext 链）声明，托管版存在子类的 RenderingInfo 里。
/// 两者天然互斥（类型不同），VKCommandBuffer.BindPipeline 正是靠类型判断当前渲染范围配不配。
///
/// 和着色器模块的关系：**引用，不是拥有**。管线里记着用到的那些 VKShaderModule（同 VKRenderPass 记着
/// 它的 Attachments、VKFramebuffer 记着 Attachments），但模块的生死由调用方负责 ——
/// Vulkan 要求模块先于用到它的管线创建、后于管线销毁。
///
/// 划界线：vkCmdBindPipeline 的**入口在命令缓冲那边**（VKCommandBuffer.BindPipeline，它接受本类任一子类，
/// 按类型做兼容性检查），本类只提供 internal 的
/// Bind（读自己的 CheckedHandle 执行那句调用，同 VKRenderPass.BeginRenderPass 的分工）；vkCmdDraw 那些录制命令见 VKCommandBuffer。
/// 本类只做：记住创建时定下来的那套东西、校验句柄归属、绑定、销毁。
/// </summary>
public abstract partial class VKShaderPipeline
{
    /// <summary>父对象：这条管线是"哪份布局"的管线（它是按那份布局去接资源的）。销毁、归属校验都走它。</summary>
    public readonly VKShaderPipelineLayout Parent;

    /// <summary>
    /// 着色器阶段数组（教程 vertShaderStageInfo / fragShaderStageInfo 那两条）。
    /// 存的是包装类 VKShaderModule 的引用，**不拥有**它们 —— 模块必须先建、后销毁。
    /// </summary>
    public readonly ReadOnlyCollection<ManagedPipelineShaderStage> Stages;

    /// <summary>创建时定下来的固定功能状态（本仓库的无指针版，见 ManagedGraphicsPipelineState）。</summary>
    public readonly ManagedPipelineStateCreateInfo State;

    /// <summary>创建时的 VkGraphicsPipelineCreateInfo::flags（派生管线 / 禁止优化之类），教程用 None。</summary>
    public readonly PipelineCreateFlags Flags;

    private readonly Pipeline handle;

    internal VKShaderPipeline(
        VKShaderPipelineLayout parent,
        Pipeline handle,
        ManagedPipelineShaderStage[] stages,
        ManagedPipelineStateCreateInfo state)
    {
        Parent = parent;
        this.handle = handle;
        Stages = new ReadOnlyCollection<ManagedPipelineShaderStage>(stages);
        State = state;
        Flags = state.Flags;
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 VKShaderPipelineLayout.CreateGraphicsPipeline 里）。
    /// </summary>
    public virtual bool Valid => Parent.IsShaderPipelineOfThis(this);

    private Pipeline CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The shader pipeline is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：把"从父对象集合里摘掉 + 调 vkDestroyPipeline"交给父对象做（父对象再把实际的设备调用
    /// 和句柄转给 VKLogicalDevice）。注意顺序：用到本管线的命令缓冲、用到这些模块的其它管线，
    /// 都得先销毁（Vulkan 要求）；而**布局要活得比本管线久** —— 布局的 Destroy() 会先销毁本管线。
    /// </summary>
    public void Destroy()
        => Parent.DestroyShaderPipeline(this, CheckedHandle);

    /// <summary>参数是裸句柄（Pipeline），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(Pipeline handleForCheck)
        => handleForCheck.Handle == handle.Handle;

}

public class VKRenderPassShaderPipeline : VKShaderPipeline
{

    /// <summary>
    /// 这条管线配合的渲染通道（固定渲染那一路；动态渲染用的是 VKShaderPipeline_Dynamic）。
    /// 引用，不是拥有 —— 渲染通道要活得比本管线久（同 Stages 与 VKShaderModule 的关系）。
    /// </summary>
    public readonly VKRenderPass RenderPass;

    /// <summary>
    /// 这条管线属于渲染通道的第几个子通道（教程只有一个，所以是 0）。
    /// </summary>
    public readonly uint Subpass;
    internal VKRenderPassShaderPipeline(
        VKShaderPipelineLayout parent,
        Pipeline handle,
        VKRenderPass renderPass,
        uint subpass,
        ManagedPipelineShaderStage[] stages,
        ManagedPipelineStateCreateInfo state)
        : base(
            parent,
            handle,
            stages,
            state
        )
    {
        RenderPass = renderPass;
        Subpass = subpass;
    }

    public override bool Valid => base.Valid && RenderPass.Valid;

}

public class VKDynamicShaderPipeline : VKShaderPipeline
{

    /// <summary>
    /// 动态渲染管线的附件格式声明（创建时那份 VkPipelineRenderingCreateInfo 的托管版）。
    /// VKCommandBuffer.BindPipeline 用它核对 BeginRendering 时给的颜色 / 深度 / 模板附件格式。
    /// </summary>
    public readonly ManagedPipelineRenderingInfo RenderingInfo;
    internal VKDynamicShaderPipeline(
        VKShaderPipelineLayout parent,
        Pipeline handle,
        ManagedPipelineRenderingInfo renderingInfo,
        ManagedPipelineShaderStage[] stages,
        ManagedPipelineStateCreateInfo state)
        : base(
            parent,
            handle,
            stages,
            state
        )
    {
        RenderingInfo = renderingInfo;
    }
}

public partial class VKShaderPipelineLayout
{
    // 从这份布局上建出来的图形管线（写法和 VKBuffer.bufferViews / VKImage.imageViews 一样）：
    // CreateGraphicsPipeline 时登记进来，之后靠 IsShaderPipelineOfThis() 反查归属
    //（VKShaderPipeline.Valid 就是这么来的 —— 管线是"这份布局的管线"，所以登记表在本类而不是设备上）；
    // 顺便把 allocationCallbacks 记下来留给销毁用。
    private readonly ConcurrentDictionary<VKShaderPipeline, AllocationCallbacks?> shaderPipelines = new ();

    /// <summary>
    /// 教程 createGraphicsPipeline() 里那句 vkCreateGraphicsPipelines —— **建图形管线的入口就在本类**
    ///（public、签名里零裸句柄）。这是**固定渲染**那一版：附件格式写死在渲染通道里。
    ///
    /// 为什么入口在本类：管线是"在这份布局上建出来的"（布局描述它要接的资源，管线按布局去接），
    /// 所以管线是"这份布局的管线" —— 包装 + 登记都在本类（登记表 shaderPipelines），
    /// 和 VKBuffer.CreateBufferView / VKImage.CreateImageView 一个套路：
    /// 发起方（本类）把自己的 CheckedHandle 当参数往下传，设备侧执行 Vulkan 调用并**返回新建的句柄**。
    ///
    /// 整条链（"发起方包装 + 登记、沿途各环执行自己那一段"）：
    ///   VKShaderPipelineLayout.CreateGraphicsRenderPassPipeline(stages, state, renderPass, …) ← 本方法：public、零裸句柄
    ///     → VKRenderPass.CreateGraphicsPipeline(…)（static）                                 渲染通道侧：把渲染通道换成自己的 CheckedHandle
    ///       → VKLogicalDevice.CreateGraphicsPipeline_RenderPass(…)                          设备侧：过设备这一环
    ///         → VKShaderModule.CreateGraphicsPipeline(…, this, …)                            static 收口：读各模块的私有句柄
    /// 返回的句柄由本方法包装成 VKShaderPipeline_RenderPass（**父对象就是本类**）并登记。
    ///
    /// 参数：
    ///   stages     —— 着色器阶段数组（哪个阶段用哪个模块 + 入口名），至少一项；
    ///   state      —— 固定功能状态，见 ManagedGraphicsPipelineState（从 CreateDefault() 起步最省事）；
    ///   renderPass —— 配合的渲染通道（**必填**：管线要和它的附件格式 / 子通道相容）；
    ///   subpass    —— 属于那个渲染通道的第几个子通道（教程只有一个，所以默认 0）。
    /// 建出来的管线只能在 BeginRenderPass…EndRenderPass 之间用 VKCommandBuffer.BindPipeline 绑定。
    /// 动态渲染（没有渲染通道）用下面那个 <see cref="CreateGraphicsDynamicPipeline"/>。
    /// 注意生命周期：用到它的命令缓冲先销毁、它用的着色器模块后销毁（Vulkan 要求）。
    /// </summary>
    public VKRenderPassShaderPipeline CreateRenderPassShaderPipeline(
        ManagedPipelineShaderStage[] stages,
        ManagedPipelineStateCreateInfo state,
        VKRenderPass renderPass,
        uint subpass = 0,
        AllocationCallbacks? allocationCallbacks = null)
    {
        if (renderPass == null)
        {
            throw new ArgumentNullException(
                nameof(renderPass),
                "A pipeline that uses a render pass needs one; use CreateGraphicsDynamicPipeline for dynamic rendering.");
        }

        // 自己的句柄当参数往下传（传入正确参数 = 授权），渲染通道 / 设备那边会再复核。
        Pipeline pipelineHandle = renderPass.CreateGraphicsPipeline(
            this, CheckedHandle, stages, state, subpass, allocationCallbacks);

        VKRenderPassShaderPipeline result = new(
            this,
            pipelineHandle,
            renderPass,
            subpass,
            stages,
            state);

        shaderPipelines[result] = allocationCallbacks;
        return result;
    }

    /// <summary>
    /// vkCreateGraphicsPipelines 的**动态渲染**那一版：不给渲染通道，附件格式改由
    /// VkPipelineRenderingCreateInfo（本仓库的 <see cref="ManagedPipelineRenderingInfo"/>）声明。
    ///
    /// 和上面固定渲染那版的差别只有"格式从哪来"：
    ///   · 固定：格式写在 VKRenderPass 里，创建管线时把渲染通道句柄放进 create-info；
    ///   · 动态：没有渲染通道，格式通过 pNext 链上的 PipelineRenderingCreateInfo 报给驱动
    ///          （这一步在收口的 VKShaderModule.CreateGraphicsPipeline 里组装）。
    /// 建出来的管线 renderPass = VK_NULL_HANDLE，只能在 BeginRendering…EndRendering 之间用
    /// VKCommandBuffer.BindPipeline 绑定（创建时已经检查过实例版本 / 扩展，见
    /// VKLogicalDevice.SupportsDynamicRendering）。
    ///
    /// 调用链比固定那版短 —— 没有渲染通道，所以不经过 VKRenderPass 那一环：
    ///   VKShaderPipelineLayout.CreateGraphicsDynamicPipeline(…)  ← 本方法：public、零裸句柄
    ///     → VKLogicalDevice.CreateGraphicsPipeline_Dynamic(…, renderingInfo, …)
    ///       → VKShaderModule.CreateGraphicsPipeline(…, this, …)
    ///
    /// 参数：
    ///   stages        —— 着色器阶段数组，至少一项；
    ///   state         —— 固定功能状态，见 ManagedGraphicsPipelineState；
    ///   renderingInfo —— 附件格式声明（颜色 / 深度 / 模板 / viewMask）；
    ///   allocationCallbacks —— 可选的内存分配回调。
    /// </summary>
    public VKDynamicShaderPipeline CreateDynamicShaderPipeline(
        ManagedPipelineShaderStage[] stages,
        ManagedPipelineStateCreateInfo state,
        ManagedPipelineRenderingInfo renderingInfo,
        AllocationCallbacks? allocationCallbacks = null)
    {
        // 动态渲染是 Vulkan 1.3 的核心功能（1.3 之前要 VK_KHR_dynamic_rendering）。
        // 创建时就拦下，免得建出一条根本录不进命令缓冲的管线。
        if (!Parent.SupportsDynamicRendering)
        {
            throw new NotSupportedException(
                "Dynamic rendering requires Vulkan 1.3 or the VK_KHR_dynamic_rendering extension.");
        }

        // 自己的句柄当参数往下传（传入正确参数 = 授权），设备那边会再复核。
        Pipeline pipelineHandle = Parent.CreateShaderPipeline(
            this, CheckedHandle,
            null, default, renderingInfo,
            stages, state, 0, allocationCallbacks);

        VKDynamicShaderPipeline result = new(
            this,
            pipelineHandle,
            renderingInfo,
            stages,
            state);

        shaderPipelines[result] = allocationCallbacks;
        return result;
    }

    /// <summary>
    /// 本布局身上有没有登记过这条管线 —— VKShaderPipeline.Valid 靠它。
    ///（管线的父对象是管线布局，所以只查这里。）
    /// </summary>
    public bool IsShaderPipelineOfThis(VKShaderPipeline shaderPipeline)
        => shaderPipelines.ContainsKey(shaderPipeline) && Valid;

    /// <summary>
    /// vkDestroyPipeline：登记表在本类手里（管线是"这份布局的管线"），所以先校验 + 摘登记
    ///（顺便把创建时记下的 ac 取出来），再把"真正的销毁"转给父对象（布局 → 逻辑设备）——
    /// 同 VKBuffer.DestroyBufferView / VKImage.DestroyImageView 的分工。
    /// </summary>
    internal bool DestroyShaderPipeline(VKShaderPipeline shaderPipeline, Pipeline handle)
    {
        if (!shaderPipeline.IsHandleOfThis(handle) || !shaderPipelines.TryRemove(shaderPipeline, out var allocationCallbacks))
        {
            return false;
        }

        return Parent.DestroyShaderPipeline(shaderPipeline, handle, allocationCallbacks);
    }
}

public partial class VKRenderPass
{
    /// <summary>
    /// 建管线时的"渲染通道这一环"：把渲染通道换成它自己的 CheckedHandle，
    /// 连同布局那一对（实例 + 句柄）一起交给 VKLogicalDevice 往下走
    ///（调用链见 VKShaderPipelineLayout.CreateGraphicsRenderPassPipeline）。
    ///
    /// 只有**固定渲染**那一版要走这里；动态渲染没有渲染通道，直接从布局调设备（见
    /// VKShaderPipelineLayout.CreateGraphicsDynamicPipeline）。
    ///
    /// 之所以是 static：参数里带的是裸句柄（PipelineLayout），而且只有本类的代码能读渲染通道实例的私有
    /// CheckedHandle —— "谁的句柄类型，谁就在自己的类里读写它"（同 VKImageView.CreateFramebuffer）。
    /// 之所以是 internal：签名里带裸句柄，按访问性约定不进 public 签名。
    /// </summary>
    internal Pipeline CreateGraphicsPipeline(
        VKShaderPipelineLayout shaderPipelineLayout,
        PipelineLayout layoutHandle,
        ManagedPipelineShaderStage[] stages,
        ManagedPipelineStateCreateInfo state,
        uint subpass,
        AllocationCallbacks? allocationCallbacks)
        => shaderPipelineLayout.Parent.CreateShaderPipeline(
            shaderPipelineLayout, layoutHandle,
            this, CheckedHandle, default,
            stages, state, subpass, allocationCallbacks);
}

public partial class VKLogicalDevice
{
    #region 图形管线（教程《Graphics pipeline》/《Fixed functions》章节）

    /// <summary>
    /// 教程 createGraphicsPipeline() 里那句 vkCreateGraphicsPipelines —— 本方法只负责"设备侧"那一半
    ///（要 Device 句柄的调用），**入口在 VKShaderPipelineLayout.CreateGraphicsRenderPassPipeline**
    ///（管线是"那份布局的管线"，包装 + 登记都在那边；同 CreateFramebuffer 都是"发起方包装 + 登记、
    /// 本类执行句柄级调用"的分工）。
    ///
    /// 为什么本类这一环不能省：vkCreateGraphicsPipelines 要 Device 句柄，而 Device 句柄只有本类能读出来
    ///（别的类读不到 private 的 CheckedHandle）—— 就是"谁持着句柄谁就把它当参数往下传"。
    ///
    /// 句柄怎么凑齐（本仓库的访问性约定：**任何对象都不把自己的句柄交出去**）：
    ///   · Device 句柄由本类自己持有，作为"授权参数"主动往下传；
    ///   · RenderPass / PipelineLayout 句柄分别由 VKRenderPass、VKShaderPipelineLayout 自己当参数传下来（实例 + 句柄一对；
    ///     动态渲染那条路没有渲染通道，两者都是 null / VK_NULL_HANDLE），本类只往下转，
    ///     由收口的 VKShaderModule.CreateGraphicsPipeline 复核；
    ///   · 着色器模块句柄由 VKShaderModule 里那个 **static** 方法自己读 —— 同一个类的代码可以访问
    ///     任意实例的 private 字段，所以不需要任何转交接口（"谁的句柄类型，谁就在自己的类里读写"）。
    /// 返回值是"操作结果"（新建的 Pipeline 句柄），由发起方（VKShaderPipelineLayout）包装 + 登记。
    ///
    /// 参数：
    ///   renderPass / renderPassHandle —— 要配合的渲染通道（固定渲染必给；**句柄是它自己传下来的**。
    ///                                    动态渲染时两者都是 null / VK_NULL_HANDLE）；
    ///   renderingInfo —— 动态渲染的附件格式声明（收口时挂到 pNext 链上）；固定渲染时传 null；
    ///   stages   —— 着色器阶段数组（哪个阶段用哪个模块 + 入口名），至少一项；
    ///   state    —— 固定功能状态，见 ManagedGraphicsPipelineState（从 CreateDefault() 起步最省事）；
    ///   subpass  —— 属于渲染通道的第几个子通道（教程只有一个，所以是 0）。
    /// 之所以是 internal：入参 / 返回值里带的是裸句柄，按访问性约定不进 public 签名。
    /// </summary>
    internal Pipeline CreateShaderPipeline(
        VKShaderPipelineLayout shaderPipelineLayout,
        PipelineLayout pipelineLayoutHandle,
        VKRenderPass? renderPass,
        RenderPass renderPassHandle,
        ManagedPipelineRenderingInfo renderingInfo,
        ManagedPipelineShaderStage[] stages,
        ManagedPipelineStateCreateInfo state,
        uint subpass,
        AllocationCallbacks? allocationCallbacks)
        => VKShaderModule.CreateShaderPipeline(
            this, CheckedHandle, shaderPipelineLayout, pipelineLayoutHandle,
            renderPass, renderPassHandle, renderingInfo, stages, state, subpass, allocationCallbacks);

    /// <summary>
    /// vkDestroyPipeline（写法同 DestroyRenderPass / DestroyFramebuffer）。
    /// 句柄是调用方（VKShaderPipelineLayout → VKRenderPass）当参数传下来的，本类只负责执行销毁；
    /// allocationCallbacks 同样由调用方给 —— 登记表在 VKShaderPipelineLayout 那边（管线是"那份布局的管线"），
    /// ac 也就跟着登记一起存在那边。
    /// 注意本类只是执行销毁，具体顺序（用到这条管线的命令缓冲必须先没）由调用方负责。
    /// </summary>
    internal unsafe bool DestroyShaderPipeline(
        VKShaderPipeline shaderPipeline,
        Pipeline handle,
        AllocationCallbacks? allocationCallbacks)
    {
        Device device = CheckedHandle;

        if (!shaderPipeline.IsHandleOfThis(handle))
        {
            return false;
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyPipeline(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyPipeline(device, handle, null);
        }

        return true;
    }

    #endregion
}

public partial class VKShaderModule
{
    /// <summary>
    /// 教程《Graphics pipeline》章节最后那句 vkCreateGraphicsPipelines —— 本类的"收口"入口
    ///（固定渲染：CreateGraphicsRenderPassPipeline → VKRenderPass.CreateGraphicsPipeline（转发）
    ///   → VKLogicalDevice.CreateGraphicsPipeline_RenderPass → 本方法；
    ///  动态渲染：CreateGraphicsDynamicPipeline → VKLogicalDevice.CreateGraphicsPipeline_Dynamic → 本方法）。
    ///
    /// 为什么收口在本类（对照帧缓冲那一链）：
    ///   · 帧缓冲的收口在 VKImageView，因为要读**每个附件视图实例的私有句柄**；
    ///   · 管线的收口就在这里，因为要读**每个着色器模块实例的私有句柄** —— 同一个类的代码可以访问任意
    ///     实例的 private 字段，所以不需要任何转交接口、也不让模块把句柄交出去。
    /// RenderPass 句柄是 VKRenderPass 那一环当参数传下来的（固定渲染；动态渲染时两者都是 null / VK_NULL_HANDLE），
    /// PipelineLayout 句柄是 VKShaderPipelineLayout 当参数传下来的（它才是那个句柄的持有者）。
    /// 这几对"实例 + 句柄"就是授权，本方法照惯例逐一用 IsHandleOfThis 复核（没有渲染通道时跳过那两项）。
    ///
    /// 动态渲染那一版（renderingInfo 有值）在这里多干一件事：把 ManagedPipelineRenderingInfo 转成
    /// VkPipelineRenderingCreateInfo 并挂到 GraphicsPipelineCreateInfo::pNext 上 ——
    /// 没有渲染通道时，附件格式只能靠这个结构告诉驱动（VUID-VkGraphicsPipelineCreateInfo-renderPass-06582）。
    ///
    /// 参数就是教程 createGraphicsPipeline() 里那两半：
    ///   前半段（着色器阶段）—— stages：每项 = 模块 + 阶段 + 入口名（本方法转成 VkPipelineShaderStageCreateInfo[]）；
    ///   后半段（固定功能）—— state：见 ManagedGraphicsPipelineState（视口 / 光栅化 / 多重采样 / 颜色混合 ……）；
    ///   另外还有 layout（管线布局）/ renderPass + subpass（这条管线属于哪个渲染通道的第几个子通道）。
    ///
    /// 之所以只能是 internal：入参里带的是裸句柄（Device / PipelineLayout / RenderPass），按访问性约定不进 public 签名。
    /// 渲染通道是可选对象：null 时 renderPass 传 VK_NULL_HANDLE，且跳过它的两项归属校验。
    /// </summary>
    internal static unsafe Pipeline CreateShaderPipeline(
        VKLogicalDevice logicalDevice,
        Device logicalDeviceHandle,
        VKShaderPipelineLayout shaderPipelineLayout,
        PipelineLayout pipelineLayoutHandle,
        VKRenderPass? renderPass,
        RenderPass renderPassHandle,
        ManagedPipelineRenderingInfo renderingInfo,
        ManagedPipelineShaderStage[] stages,
        ManagedPipelineStateCreateInfo state,
        uint subpass,
        AllocationCallbacks? allocationCallbacks)
    {
        if (stages == null || stages.Length == 0)
        {
            throw new ArgumentException("A graphics pipeline needs at least one shader stage.", nameof(stages));
        }

        if (!logicalDevice.IsHandleOfThis(logicalDeviceHandle))
        {
            throw new ArgumentException("The provided logical device handle is not from logical device.");
        }

        if (renderPass != null)
        {
            if (!renderPass.IsHandleOfThis(renderPassHandle))
            {
                throw new ArgumentException("The provided render pass handle is not from render pass.");
            }

            if (!logicalDevice.IsRenderPassOfThis(renderPass))
            {
                throw new ArgumentException("The provided render pass is not associated with logical device.");
            }
        }
        else
        {
            renderPassHandle = default;
        }

        if (!shaderPipelineLayout.IsHandleOfThis(pipelineLayoutHandle))
        {
            throw new ArgumentException("The provided pipeline layout handle is not from pipeline layout.");
        }

        // ===== 前半段：着色器阶段数组 =====
        // 教程里这段是手写两条 VkPipelineShaderStageCreateInfo（vert/frag）；这里要按传入的数组来，
        // 数量不定，所以钉在栈上（stackalloc）。
        // pName 是 C 字符串指针，必须活到 vkCreateGraphicsPipelines 返回之后 —— 栈上的 byte[] 数组
        // 在循环里 stackalloc 会被立刻回收，所以统一用 SilkMarshal 分配，最后在 finally 里释放。
        PipelineShaderStageCreateInfo* pStages = stackalloc PipelineShaderStageCreateInfo[stages.Length];
        byte** pEntryPoints = stackalloc byte*[stages.Length];
        int allocatedEntryPointCount = 0;
        try
        {
            for (int i = 0; i < stages.Length; i++)
            {
                VKShaderModule module = stages[i].Module;
                if (module == null || !logicalDevice.IsShaderModuleOfThis(module))
                {
                    throw new ArgumentException("The provided shader module is not associated with logical device.", nameof(stages));
                }

                string entryPoint = string.IsNullOrEmpty(stages[i].EntryPoint) ? "main" : stages[i].EntryPoint;
                pEntryPoints[i] = (byte*)SilkMarshal.StringToPtr(entryPoint);
                allocatedEntryPointCount++;

                pStages[i] = new PipelineShaderStageCreateInfo(
                    stage: stages[i].Stage,
                    module: module.CheckedHandle,
                    pName: pEntryPoints[i]);
            }


            // ===== 后半段：固定功能状态 =====
            // 指针型的子状态在下面用 fixed 钉住（数组为 null 时 fixed 得到空指针，正好表达"没有这个数组"）。
            Viewport[] viewports = state.Viewports ?? [default];
            Rect2D[] scissors = state.Scissors ?? [default];
            PipelineColorBlendAttachmentState[] colorBlendAttachments = state.ColorBlendAttachments ?? CreateDefaultColorBlendAttachment();
            PipelineDepthStencilStateCreateInfo depthStencilState = state.DepthStencilState ?? default;

            // 动态渲染的颜色附件格式数组要钉住：它挂在 pNext 链上，必须活到 vkCreateGraphicsPipelines 返回之后。
            // 固定渲染时 renderingInfo 为 null，fixed 得到空指针，正好表达"没有这个数组"。
            Format[]? colorAttachmentFormats = renderingInfo.ColorAttachmentFormats;
            fixed (Format* pColorAttachmentFormats = colorAttachmentFormats)
            fixed (VertexInputBindingDescription* pVertexBindings = state.VertexBindings)
            fixed (VertexInputAttributeDescription* pVertexAttributes = state.VertexAttributes)
            fixed (Viewport* pViewports = viewports)
            fixed (Rect2D* pScissors = scissors)
            fixed (PipelineColorBlendAttachmentState* pColorBlendAttachments = colorBlendAttachments)
            fixed (DynamicState* pDynamicStates = state.DynamicStates)
            {
                PipelineVertexInputStateCreateInfo vertexInputState = new(
                    vertexBindingDescriptionCount: (uint)(state.VertexBindings?.Length ?? 0),
                    pVertexBindingDescriptions: pVertexBindings,
                    vertexAttributeDescriptionCount: (uint)(state.VertexAttributes?.Length ?? 0),
                    pVertexAttributeDescriptions: pVertexAttributes);

                PipelineInputAssemblyStateCreateInfo inputAssemblyState = new(
                    topology: state.Topology,
                    primitiveRestartEnable: state.PrimitiveRestartEnable);

                // Vulkan 要求 viewportCount / scissorCount 必须相等且至少是 1；
                // 声明成动态状态时可以只报数量、不给内容（那正是 pViewports = null 的合法用法）。
                PipelineViewportStateCreateInfo viewportState = new(
                    viewportCount: (uint)Math.Max(viewports.Length, 1),
                    pViewports: pViewports,
                    scissorCount: (uint)Math.Max(scissors.Length, 1),
                    pScissors: pScissors);

                PipelineRasterizationStateCreateInfo rasterizationState = new(
                    depthClampEnable: state.DepthClampEnable,
                    rasterizerDiscardEnable: state.RasterizerDiscardEnable,
                    polygonMode: state.PolygonMode,
                    cullMode: state.CullMode,
                    frontFace: state.FrontFace,
                    depthBiasEnable: state.DepthBiasEnable,
                    depthBiasConstantFactor: state.DepthBiasConstantFactor,
                    depthBiasClamp: state.DepthBiasClamp,
                    depthBiasSlopeFactor: state.DepthBiasSlopeFactor,
                    lineWidth: state.LineWidth);

                PipelineMultisampleStateCreateInfo multisampleState = new(
                    rasterizationSamples: state.RasterizationSamples,
                    sampleShadingEnable: state.SampleShadingEnable,
                    minSampleShading: state.MinSampleShading,
                    alphaToCoverageEnable: state.AlphaToCoverageEnable,
                    alphaToOneEnable: state.AlphaToOneEnable);

                PipelineColorBlendStateCreateInfo colorBlendState = new(
                    logicOpEnable: state.LogicOpEnable,
                    logicOp: state.LogicOp,
                    attachmentCount: (uint)colorBlendAttachments.Length,
                    pAttachments: pColorBlendAttachments);

                PipelineDynamicStateCreateInfo dynamicState = new(
                    dynamicStateCount: (uint)(state.DynamicStates?.Length ?? 0),
                    pDynamicStates: pDynamicStates);

                // basePipelineHandle / basePipelineIndex：做"管线派生"才用，教程里是 VK_NULL_HANDLE / -1。
                GraphicsPipelineCreateInfo pipelineInfo = new(
                    flags: state.Flags,
                    stageCount: (uint)stages.Length,
                    pStages: pStages,
                    pVertexInputState: &vertexInputState,
                    pInputAssemblyState: &inputAssemblyState,
                    pTessellationState: null,
                    pViewportState: &viewportState,
                    pRasterizationState: &rasterizationState,
                    pMultisampleState: &multisampleState,
                    pDepthStencilState: state.DepthStencilState.HasValue ? &depthStencilState : null,
                    pColorBlendState: &colorBlendState,
                    pDynamicState: state.DynamicStates != null ? &dynamicState : null,
                    layout: pipelineLayoutHandle,
                    renderPass: renderPassHandle,
                    subpass: subpass,
                    basePipelineHandle: default,
                    basePipelineIndex: -1);

                // 动态渲染：没有渲染通道，附件格式改由 pNext 链上的 VkPipelineRenderingCreateInfo 声明。
                // 它和 pipelineInfo 都活在同一次调用里，所以取局部变量地址就够（同在栈上的 pStages）。
                PipelineRenderingCreateInfo renderingCreateInfo = default;
                if (renderPass == null)
                {
                    renderingCreateInfo = new PipelineRenderingCreateInfo(
                        viewMask: renderingInfo.ViewMask,
                        colorAttachmentCount: (uint)(colorAttachmentFormats?.Length ?? 0),
                        pColorAttachmentFormats: pColorAttachmentFormats,
                        depthAttachmentFormat: renderingInfo.DepthAttachmentFormat ?? Format.Undefined,
                        stencilAttachmentFormat: renderingInfo.StencilAttachmentFormat ?? Format.Undefined);
                    pipelineInfo.PNext = &renderingCreateInfo;
                }

                // 管线缓存没做（教程也没做）：传 VK_NULL_HANDLE 就是"不缓存"，驱动照建。
                Pipeline handle;
                if (allocationCallbacks != null)
                {
                    AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
                    if (VK.CreateGraphicsPipelines(logicalDeviceHandle, default, 1u, &pipelineInfo, &dAllocationCallbacks, &handle) != Result.Success)
                    {
                        throw new NotSupportedException("failed to create graphics pipeline!");
                    }
                }
                else if (VK.CreateGraphicsPipelines(logicalDeviceHandle, default, 1u, &pipelineInfo, null, &handle) != Result.Success)
                {
                    throw new NotSupportedException("failed to create graphics pipeline!");
                }

                return handle;
            }
        }
        finally
        {
            for (int i = 0; i < allocatedEntryPointCount; i++)
            {
                SilkMarshal.Free((nint)pEntryPoints[i]);
            }
        }
    }
}
