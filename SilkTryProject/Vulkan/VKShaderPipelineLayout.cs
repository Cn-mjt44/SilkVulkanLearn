using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 管线布局（教程《Graphics pipeline》里 createPipelineLayout() 那一步）的包装。
/// 布局描述的是"这套管线能读写哪些资源"：描述符集布局（每个描述符集里有哪些绑定）和 push constant 范围。
/// 它在 Vulkan 里是**独立的对象**，建它的是 VKLogicalDevice.CreateShaderPipelineLayout；
/// 之后被图形管线拿去用（管线正是**按这份布局**去接资源的，见 VKShaderPipeline）。
///
/// 归属：**父对象是 VKLogicalDevice** —— vkCreatePipelineLayout / vkDestroyPipelineLayout 都是设备级调用，
/// 创建入口就在设备上（VKLogicalDevice.CreateShaderPipelineLayout）、登记表也在它那边
///（Valid 靠 VKLogicalDevice.IsShaderPipelineLayoutOfThis 反查）—— 同 VKRenderPass / VKCommandPool 之于 VKLogicalDevice。
/// 布局本身不绑定某个渲染通道（那是固定渲染管线的属性，见 VKShaderPipeline_RenderPass.RenderPass），它只描述"这套管线能接哪些资源"。
///
/// 本类手里只有**一个**句柄（PipelineLayout）—— 这正是它从原来的 VKShaderPipeline 里拆出来的原因：
/// 那个类原本一个身位塞了"管线 + 布局"两个句柄，和别的包装类"一个类 = 一个句柄"的风格不一致。
/// 现在布局独立成类，并且**成为 VKShaderPipeline 的父级**：管线是"这份布局的管线"。
/// 建管线的代码（登记表 shaderPipelines + CreateGraphicsPipeline）按本仓库惯例写在**子对象那个文件**里，
/// 也就是 VKShaderPipeline.cs 里那份 partial VKShaderPipelineLayout（同 VKBufferView 之于 VKBuffer）。
///
/// 分工（本仓库的句柄流转约定：**任何对象都不把自己的句柄交出去，谁的句柄类型谁就在自己的类里读写它**）：
///   · 创建：VKLogicalDevice.CreateShaderPipelineLayout（入口在这里，但签名里的"描述符集布局数组"
///     需要读各布局实例的私有句柄，所以真正那段原生调用收口在 VKDescriptorSetLayout.CreatePipelineLayout）；
///   · 销毁：Destroy() → VKLogicalDevice.DestroyShaderPipelineLayout（摘登记 + vkDestroyPipelineLayout）。
///
/// 生命周期：**布局要活得比用到它的管线久**（Vulkan 要求），所以本类的 Destroy() 会先销毁按这份布局
/// 建出来的全部管线，再销毁布局自己 —— 调用方不需要手动按顺序销毁。
/// 反过来，本布局引用的 VKDescriptorSetLayout 要活得比本布局久：VKLogicalDevice.Destroy() 按
/// "先管线布局、后描述符集布局"的顺序销毁；手动销毁时也要照这个顺序。
/// </summary>
public partial class VKShaderPipelineLayout
{
    /// <summary>父对象：创建这份布局的逻辑设备。销毁、归属校验都走它。</summary>
    public readonly VKLogicalDevice Parent;

    /// <summary>创建管线布局时的 flags（那块 PipelineLayoutCreateInfo 在 VKLogicalDevice.CreateShaderPipelineLayout 里）。</summary>
    public readonly PipelineLayoutCreateFlags LayoutFlags;

    /// <summary>创建管线布局时声明的 push constant 范围；null = 没有。</summary>
    public readonly ReadOnlyCollection<PushConstantRange>? PushConstantRanges;

    /// <summary>
    /// 这份布局认哪几个描述符集布局（create-info 里的 pSetLayouts）；null = 不管描述符集。
    /// 顺序就是 set 号（着色器里的 set = N）。
    /// 引用，不是拥有 —— 描述符集布局由 VKLogicalDevice 持有，**要活得比本布局久**
    ///（VKLogicalDevice.Destroy 先销毁管线布局、再销毁描述符集布局）。
    /// </summary>
    public readonly ReadOnlyCollection<VKDescriptorSetLayout>? DescriptorSetLayouts;

    private readonly PipelineLayout handle;

    internal VKShaderPipelineLayout(
        VKLogicalDevice parent,
        PipelineLayout handle,
        PipelineLayoutCreateFlags layoutFlags,
        PushConstantRange[]? pushConstantRanges,
        VKDescriptorSetLayout[]? descriptorSetLayouts)
    {
        Parent = parent;
        this.handle = handle;
        LayoutFlags = layoutFlags;
        PushConstantRanges = pushConstantRanges != null ? new ReadOnlyCollection<PushConstantRange>(pushConstantRanges) : null;
        DescriptorSetLayouts = descriptorSetLayouts != null ? new ReadOnlyCollection<VKDescriptorSetLayout>(descriptorSetLayouts) : null;
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 VKLogicalDevice.CreateShaderPipelineLayout 里）。
    /// </summary>
    public bool Valid => Parent.IsShaderPipelineLayoutOfThis(this);

    private PipelineLayout CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The shader pipeline layout is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：先销毁按这份布局建出来的全部图形管线，再把"从父对象集合里摘掉 + 调 vkDestroyPipelineLayout"
    /// 交给父对象（VKLogicalDevice）做 —— 同 VKRenderPass.Destroy 的分工。
    ///（Vulkan 要求用到这份布局的管线必须先销毁。）
    /// </summary>
    public void Destroy()
    {
        foreach (var shaderPipeline in shaderPipelines.Keys.ToArray())
        {
            try
            {
                shaderPipeline.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        Parent.DestroyShaderPipelineLayout(this, CheckedHandle);
    }

    /// <summary>参数是裸句柄（PipelineLayout），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(PipelineLayout handleForCheck)
        => handleForCheck.Handle == handle.Handle;

}

public partial class VKLogicalDevice
{
    #region 图形管线（教程《Graphics pipeline》/《Fixed functions》章节）

    // 从本设备上建出来的管线布局（写法和 renderPasses / commandPools 一样）：CreateShaderPipelineLayout 时登记进来，
    // 之后靠 IsShaderPipelineLayoutOfThis() 反查归属（VKShaderPipelineLayout.Valid 就是这么来的）、销毁时摘掉，
    // value 是创建那份布局时用的 allocationCallbacks。
    private readonly ConcurrentDictionary<VKShaderPipelineLayout, AllocationCallbacks?> pipelineLayouts = new ();

    /// <summary>
    /// 教程 createPipelineLayout() 里的 vkCreatePipelineLayout —— **建管线布局的入口就在本类**
    ///（public、签名里零裸句柄）。
    ///
    /// 为什么入口在本类：vkCreatePipelineLayout 要 Device 句柄，而只有本类持着它
    ///（同 CreateRenderPass / CreateCommandPool）。布局本身不绑定渲染通道 ——
    /// 配合哪个渲染通道是管线那边的事（见 VKShaderPipeline_RenderPass.RenderPass）。
    ///
    /// 描述符集布局那一路要读**每个布局实例的私有句柄**（create-info 的 pSetLayouts），
    /// 所以真正的那段原生调用收口在 VKDescriptorSetLayout.CreatePipelineLayout 里
    ///（同帧缓冲 → VKImageView.CreateFramebuffer、管线 → VKShaderModule.CreateGraphicsPipeline）：
    ///   VKLogicalDevice.CreateShaderPipelineLayout(…)        ← 本方法：public、零裸句柄
    ///     → VKDescriptorSetLayout.CreatePipelineLayout(…)     internal static 收口：读各布局的私有句柄
    /// 本类只把自己的 CheckedHandle 当参数往下传（传入正确参数 = 授权），那边会再复核一次。
    ///
    /// 参数：
    ///   layoutFlags          —— VkPipelineLayoutCreateInfo::flags（一般 None）；
    ///   pushConstantRanges   —— push constant 范围，null = 没有；
    ///   descriptorSetLayouts —— 这份布局认哪几个描述符集（顺序 = 着色器里的 set 号），null = 不管描述符集。
    /// 生命周期：descriptorSetLayouts 里的布局要活得比本布局久
    ///（VKLogicalDevice.Destroy 就是先销毁管线布局、再销毁描述符集布局）。
    /// </summary>
    public VKShaderPipelineLayout CreateShaderPipelineLayout(
        PipelineLayoutCreateFlags layoutFlags = PipelineLayoutCreateFlags.None,
        PushConstantRange[]? pushConstantRanges = null,
        VKDescriptorSetLayout[]? descriptorSetLayouts = null,
        AllocationCallbacks? allocationCallbacks = null)
    {
        // 自己的句柄当参数往下传（传入正确参数 = 授权）；收口那边会复核设备句柄和每个描述符集布局的归属。
        PipelineLayout layoutHandle = VKDescriptorSetLayout.CreatePipelineLayout(
            this,
            CheckedHandle,
            layoutFlags,
            descriptorSetLayouts,
            pushConstantRanges,
            allocationCallbacks);

        VKShaderPipelineLayout result = new(
            this,
            layoutHandle,
            layoutFlags,
            pushConstantRanges,
            descriptorSetLayouts);

        pipelineLayouts[result] = allocationCallbacks;
        return result;
    }

    /// <summary>
    /// 本设备身上有没有登记过这份布局 —— VKShaderPipelineLayout.Valid 靠它。
    /// </summary>
    public bool IsShaderPipelineLayoutOfThis(VKShaderPipelineLayout shaderPipelineLayout)
        => pipelineLayouts.ContainsKey(shaderPipelineLayout) && Valid;

    /// <summary>
    /// vkDestroyPipelineLayout（写法同 DestroyRenderPass / DestroyCommandPool）：先校验句柄 + 摘登记
    ///（顺便把创建时记下的 ac 取出来），再执行销毁。
    ///（布局的 Destroy() 已经先销毁了按它建出来的全部图形管线 —— Vulkan 要求。）
    /// </summary>
    internal unsafe bool DestroyShaderPipelineLayout(VKShaderPipelineLayout shaderPipelineLayout, PipelineLayout handle)
    {
        Device device = CheckedHandle;

        if (!shaderPipelineLayout.IsHandleOfThis(handle) || !pipelineLayouts.TryRemove(shaderPipelineLayout, out var allocationCallbacks))
        {
            return false;
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyPipelineLayout(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyPipelineLayout(device, handle, null);
        }

        return true;
    }

    #endregion
}

public partial class VKDescriptorSetLayout
{
    /// <summary>
    /// vkCreatePipelineLayout 的收口（管线布局那一路要读**每个描述符集布局实例的私有句柄**，所以放在本类）。
    ///
    /// 调用链：
    ///   VKLogicalDevice.CreateShaderPipelineLayout(…)      ← public、零裸句柄（入口在管线布局那边）
    ///     → 本方法                                         ← internal static 收口：把包装类数组换成句柄数组
    /// 之所以是 internal static：入参里有包装类数组、局部数组里也有裸句柄，按访问性约定不出包装层。
    /// 之所以不放在 VKShaderPipelineLayout 里：`DescriptorSetLayout` 这个句柄类型由本类私有持有，
    /// 同一个类的代码才能读任意实例的 private 字段（"谁的句柄类型，谁就在自己的类里读写它"）——
    /// 同 VKImageView.CreateFramebuffer 读附件视图句柄、VKShaderModule.CreateGraphicsPipeline 读模块句柄。
    ///
    /// 授权方式：调用方（VKLogicalDevice.CreateShaderPipelineLayout）把自己持有的设备句柄当参数传进来，
    /// 传入正确参数 = 授权；本方法用 IsHandleOfThis 复核设备句柄，再逐个复核描述符集布局确实登记在这台设备上。
    ///
    /// 参数：
    ///   logicalDevice / logicalDeviceHandle —— 发起方与它的设备句柄（授权用）；
    ///   layoutFlags                         —— VkPipelineLayoutCreateInfo::flags；
    ///   descriptorSetLayouts                —— 这份布局认哪几个描述符集（顺序 = 着色器里的 set 号），null = 不管描述符集；
    ///   pushConstantRanges                  —— push constant 范围，null = 没有；
    ///   allocationCallbacks                 —— 可选的内存分配回调。
    /// 返回值是"操作结果"（新建的 PipelineLayout 句柄），由发起方包装 + 登记（登记表在 VKLogicalDevice 上）。
    /// </summary>
    internal static unsafe PipelineLayout CreatePipelineLayout(
        VKLogicalDevice logicalDevice,
        Device logicalDeviceHandle,
        PipelineLayoutCreateFlags layoutFlags,
        VKDescriptorSetLayout[]? descriptorSetLayouts,
        PushConstantRange[]? pushConstantRanges,
        AllocationCallbacks? allocationCallbacks)
    {
        if (!logicalDevice.IsHandleOfThis(logicalDeviceHandle))
        {
            throw new ArgumentException("The provided logical device handle is not from logical device.");
        }

        int setLayoutCount = descriptorSetLayouts?.Length ?? 0;
        DescriptorSetLayout* pSetLayouts = stackalloc DescriptorSetLayout[setLayoutCount];
        for (int i = 0; i < setLayoutCount; i++)
        {
            VKDescriptorSetLayout descriptorSetLayout = descriptorSetLayouts![i];
            if (descriptorSetLayout == null || !logicalDevice.IsDescriptorSetLayoutOfThis(descriptorSetLayout))
            {
                throw new ArgumentException(
                    "The provided descriptor set layout is not associated with logical device.",
                    nameof(descriptorSetLayouts));
            }

            // 读本类实例的私有句柄 —— 没有"交出句柄"的接口，只是当参数往下传。
            pSetLayouts[i] = descriptorSetLayout.CheckedHandle;
        }

        PipelineLayout layoutHandle;
        fixed (PushConstantRange* pPushConstantRanges = pushConstantRanges)
        {
            PipelineLayoutCreateInfo pipelineLayoutInfo = new(
                flags: layoutFlags,
                setLayoutCount: (uint)setLayoutCount,
                pSetLayouts: pSetLayouts,
                pushConstantRangeCount: (uint)(pushConstantRanges?.Length ?? 0),
                pPushConstantRanges: pPushConstantRanges);

            if (allocationCallbacks != null)
            {
                AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
                if (VK.CreatePipelineLayout(logicalDeviceHandle, in pipelineLayoutInfo, &dAllocationCallbacks, out layoutHandle) != Result.Success)
                {
                    throw new NotSupportedException("failed to create pipeline layout!");
                }
            }
            else if (VK.CreatePipelineLayout(logicalDeviceHandle, in pipelineLayoutInfo, null, out layoutHandle) != Result.Success)
            {
                throw new NotSupportedException("failed to create pipeline layout!");
            }
        }

        return layoutHandle;
    }
}