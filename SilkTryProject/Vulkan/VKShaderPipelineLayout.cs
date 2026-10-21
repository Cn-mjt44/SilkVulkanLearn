using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 管线布局（教程《Graphics pipeline》里 createPipelineLayout() 那一步）的包装。
/// 布局描述的是"这套管线能读写哪些资源"：push constant 范围（本类目前就这些）、以后还有描述符集布局。
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
///   · 创建：VKLogicalDevice.CreateShaderPipelineLayout（普通实例方法就够，
///     因为 vkCreatePipelineLayout 的参数里没有"包装类数组"，不需要谁来读别的实例的私有句柄）；
///   · 销毁：Destroy() → VKLogicalDevice.DestroyShaderPipelineLayout（摘登记 + vkDestroyPipelineLayout）。
///
/// 生命周期：**布局要活得比用到它的管线久**（Vulkan 要求），所以本类的 Destroy() 会先销毁按这份布局
/// 建出来的全部管线，再销毁布局自己 —— 调用方不需要手动按顺序销毁。
/// </summary>
public partial class VKShaderPipelineLayout
{
    /// <summary>父对象：创建这份布局的逻辑设备。销毁、归属校验都走它。</summary>
    public readonly VKLogicalDevice Parent;

    /// <summary>创建管线布局时的 flags（那块 PipelineLayoutCreateInfo 在 VKLogicalDevice.CreateShaderPipelineLayout 里）。</summary>
    public readonly PipelineLayoutCreateFlags LayoutFlags;

    /// <summary>
    /// 创建管线布局时声明的 push constant 范围；null = 没有。
    ///（管线布局还要"描述符集布局"，那个包装类还没有，所以目前管线布局里没有描述符集，
    ///  等 VKDescriptorSetLayout 做出来了再补一路参数。）
    /// </summary>
    public readonly ReadOnlyCollection<PushConstantRange>? PushConstantRanges;

    private readonly PipelineLayout handle;

    internal VKShaderPipelineLayout(
        VKLogicalDevice parent,
        PipelineLayout handle,
        PipelineLayoutCreateFlags layoutFlags,
        PushConstantRange[]? pushConstantRanges)
    {
        Parent = parent;
        this.handle = handle;
        LayoutFlags = layoutFlags;
        PushConstantRanges = pushConstantRanges != null ? new ReadOnlyCollection<PushConstantRange>(pushConstantRanges) : null;
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
    /// 参数：
    ///   layoutFlags / pushConstantRanges —— 建管线布局用的参数（描述符集布局还没有包装类，暂不支持）。
    /// </summary>
    public unsafe VKShaderPipelineLayout CreateShaderPipelineLayout(
        PipelineLayoutCreateFlags layoutFlags = PipelineLayoutCreateFlags.None,
        PushConstantRange[]? pushConstantRanges = null,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Device device = CheckedHandle;

        PipelineLayout layoutHandle;
        fixed (PushConstantRange* pPushConstantRanges = pushConstantRanges)
        {
            PipelineLayoutCreateInfo pipelineLayoutInfo = new(
                flags: layoutFlags,
                setLayoutCount: 0,
                pSetLayouts: null,
                pushConstantRangeCount: (uint)(pushConstantRanges?.Length ?? 0),
                pPushConstantRanges: pPushConstantRanges);

            if (allocationCallbacks != null)
            {
                AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
                if (VK.CreatePipelineLayout(device, in pipelineLayoutInfo, &dAllocationCallbacks, out layoutHandle) != Result.Success)
                {
                    throw new NotSupportedException("failed to create pipeline layout!");
                }
            }
            else if (VK.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out layoutHandle) != Result.Success)
            {
                throw new NotSupportedException("failed to create pipeline layout!");
            }
        }

        VKShaderPipelineLayout result = new(
            this,
            layoutHandle,
            layoutFlags,
            pushConstantRanges);

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
