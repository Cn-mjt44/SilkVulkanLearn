using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 描述符集布局（VkDescriptorSetLayout，教程《Descriptor set layout》章节）的包装。
/// 它回答的是"这套资源怎么摆"：有哪几个绑定（binding）、每个绑定是什么类型
///（uniform 缓冲 / 组合图像采样器 ……）、在哪几个着色器阶段可见、各有多少个描述符。
/// 布局本身**不含任何实际资源** —— 真正的资源在描述符集（VKDescriptorSet）里，
/// 而描述符集是按这份布局分配出来的（描述符池 / 描述符集的包装类还没有，等它们做出来再接）。
///
/// 归属：**父对象是 VKLogicalDevice** —— vkCreateDescriptorSetLayout / vkDestroyDescriptorSetLayout 都是设备级调用，
/// 创建入口就在设备上（VKLogicalDevice.CreateDescriptorSetLayout）、登记表也在它那边
///（Valid 靠 VKLogicalDevice.IsDescriptorSetLayoutOfThis 反查）—— 同 VKShaderPipelineLayout / VKRenderPass 之于 VKLogicalDevice。
///
/// 分工：创建 → VKLogicalDevice.CreateDescriptorSetLayout；
///       销毁 → Destroy() 转到 VKLogicalDevice.DestroyDescriptorSetLayout。
/// 管线布局那一路要读**每个布局实例的私有句柄**（create-info 的 pSetLayouts），按访问性约定
/// 收口在本类的 internal static CreatePipelineLayout 里（同 VKImageView.CreateFramebuffer、
/// VKShaderModule.CreateGraphicsPipeline 的收口：谁的句柄类型，谁就在自己的类里读写它）。
///
/// 划界线：本类不做 vkAllocateDescriptorSets / vkUpdateDescriptorSets / vkCmdBindDescriptorSets ——
/// 那是描述符池 / 描述符集 / 命令缓冲各自的事（VKCommandBuffer.BindDescriptorSets 也等它们做出来再补）。
/// 本类只做：记住创建时定下来的绑定表、校验句柄归属、销毁。
///
/// 生命周期：**布局要活得比用到它的管线布局久**（Vulkan 要求），所以 VKLogicalDevice.Destroy()
/// 先销毁管线布局（连带管线）、再销毁描述符集布局；手动销毁时也要按同样的顺序。
/// </summary>
public partial class VKDescriptorSetLayout
{
    /// <summary>父对象：创建这份布局的逻辑设备。销毁、归属校验都走它。</summary>
    public readonly VKLogicalDevice Parent;

    /// <summary>
    /// 创建时的 VkDescriptorSetLayoutCreateInfo::flags（push descriptor / update-after-bind 之类，多半还带扩展）；
    /// 教程里是 None。
    /// </summary>
    public readonly DescriptorSetLayoutCreateFlags Flags;

    /// <summary>
    /// 这套布局声明了哪些绑定，顺序就是创建时给数组的顺序。
    /// 着色器里按 <see cref="ManagedDescriptorSetLayoutBinding.Binding"/> 号对位，不按这里的顺序。
    /// </summary>
    public readonly ReadOnlyCollection<ManagedDescriptorSetLayoutBinding> Bindings;

    private readonly DescriptorSetLayout handle;

    // 本类自己要调原生 API（internal static CreatePipelineLayout）时才需要这一行。
    private readonly static Vk VK = Vk.GetApi();

    internal VKDescriptorSetLayout(
        VKLogicalDevice parent,
        DescriptorSetLayout handle,
        DescriptorSetLayoutCreateFlags flags,
        ManagedDescriptorSetLayoutBinding[] bindings)
    {
        Parent = parent;
        this.handle = handle;
        Flags = flags;
        Bindings = new ReadOnlyCollection<ManagedDescriptorSetLayoutBinding>(bindings);
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 VKLogicalDevice.CreateDescriptorSetLayout 里）。
    /// </summary>
    public bool Valid => Parent.IsDescriptorSetLayoutOfThis(this);

    private DescriptorSetLayout CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The descriptor set layout is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：把"从父对象集合里摘掉 + 调 vkDestroyDescriptorSetLayout"交给父对象做。
    /// 顺序由调用方负责：用到这份布局的管线布局必须先销毁（VKLogicalDevice.Destroy 就是按这个顺序排的）。
    /// </summary>
    public void Destroy()
        => Parent.DestroyDescriptorSetLayout(this, CheckedHandle);

    /// <summary>参数是裸句柄（DescriptorSetLayout），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(DescriptorSetLayout handleForCheck)
        => handleForCheck.Handle == handle.Handle;
}

public partial class VKLogicalDevice
{
    #region 描述符集布局（教程《Descriptor set layout》章节）

    // 从本设备上建出来的描述符集布局（写法和 pipelineLayouts / renderPasses 一样）：CreateDescriptorSetLayout 时登记进来，
    // 之后靠 IsDescriptorSetLayoutOfThis() 反查归属（VKDescriptorSetLayout.Valid 就是这么来的）、销毁时摘掉，
    // value 是创建那份布局时用的 allocationCallbacks。
    private readonly ConcurrentDictionary<VKDescriptorSetLayout, AllocationCallbacks?> descriptorSetLayouts = new ();

    /// <summary>
    /// 教程 createDescriptorSetLayout() 里的 vkCreateDescriptorSetLayout —— **建描述符集布局的入口就在本类**
    ///（public、签名里零裸句柄）。
    ///
    /// 为什么入口在本类：vkCreateDescriptorSetLayout 要 Device 句柄，而只有本类持着它
    ///（同 CreateShaderPipelineLayout / CreateRenderPass / CreateCommandPool）。布局本身不绑定管道 / 命令缓冲，
    /// 之后谁用它（目前是管线布局的 pSetLayouts）由那个对象记着。
    ///
    /// 参数对应 VkDescriptorSetLayoutCreateInfo 的字段：
    ///   bindings —— 绑定表，**至少一条**（一条 = binding 号 + 类型 + 数量 + 可见阶段，
    ///               见 <see cref="ManagedDescriptorSetLayoutBinding"/>）；数组就是把"指针 + count"换成集合，
    ///               bindingCount 由数组长度推导；
    ///   flags    —— create-info 的 flags，教程用 None。
    /// 建出来的布局要活得比用到它的管线布局久（Vulkan 要求）。
    /// </summary>
    public unsafe VKDescriptorSetLayout CreateDescriptorSetLayout(
        ManagedDescriptorSetLayoutBinding[] bindings,
        DescriptorSetLayoutCreateFlags flags = DescriptorSetLayoutCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        if (bindings == null || bindings.Length == 0)
        {
            throw new ArgumentException(
                "A descriptor set layout needs at least one binding.",
                nameof(bindings));
        }

        Device device = CheckedHandle;

        // 一条绑定一个 VkDescriptorSetLayoutBinding，数量不定，所以钉在栈上（同 CreateRenderPass 里的附件数组）。
        // pImmutableSamplers 不传 —— 那条路要采样器句柄，本仓库还没有 VKSampler 包装类。
        DescriptorSetLayoutBinding* pBindings = stackalloc DescriptorSetLayoutBinding[bindings.Length];
        for (int i = 0; i < bindings.Length; i++)
        {
            pBindings[i] = new DescriptorSetLayoutBinding(
                binding: bindings[i].Binding,
                descriptorType: bindings[i].DescriptorType,
                descriptorCount: bindings[i].DescriptorCount,
                stageFlags: bindings[i].StageFlags);
        }

        DescriptorSetLayoutCreateInfo descriptorSetLayoutInfo = new(
            flags: flags,
            bindingCount: (uint)bindings.Length,
            pBindings: pBindings);

        DescriptorSetLayout layoutHandle;
        if (allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            if (VK.CreateDescriptorSetLayout(device, in descriptorSetLayoutInfo, &dAllocationCallbacks, out layoutHandle) != Result.Success)
            {
                throw new NotSupportedException("failed to create descriptor set layout!");
            }
        }
        else if (VK.CreateDescriptorSetLayout(device, in descriptorSetLayoutInfo, null, out layoutHandle) != Result.Success)
        {
            throw new NotSupportedException("failed to create descriptor set layout!");
        }

        VKDescriptorSetLayout result = new(
            this,
            layoutHandle,
            flags,
            bindings);

        descriptorSetLayouts[result] = allocationCallbacks;
        return result;
    }

    /// <summary>本设备身上有没有登记过这份布局 —— VKDescriptorSetLayout.Valid 靠它。</summary>
    public bool IsDescriptorSetLayoutOfThis(VKDescriptorSetLayout descriptorSetLayout)
        => descriptorSetLayouts.ContainsKey(descriptorSetLayout) && Valid;

    /// <summary>
    /// vkDestroyDescriptorSetLayout（写法同 DestroyShaderPipelineLayout）：先校验句柄 + 摘登记
    ///（顺便把创建时记下的 ac 取出来），再执行销毁。
    /// 注意顺序：用到这份布局的管线布局必须先销毁 —— 这一步由调用方负责（VKLogicalDevice.Destroy 已按此排序）。
    /// </summary>
    internal unsafe bool DestroyDescriptorSetLayout(VKDescriptorSetLayout descriptorSetLayout, DescriptorSetLayout handle)
    {
        Device device = CheckedHandle;

        if (!descriptorSetLayout.IsHandleOfThis(handle) || !descriptorSetLayouts.TryRemove(descriptorSetLayout, out var allocationCallbacks))
        {
            return false;
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyDescriptorSetLayout(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyDescriptorSetLayout(device, handle, null);
        }

        return true;
    }

    #endregion
}

