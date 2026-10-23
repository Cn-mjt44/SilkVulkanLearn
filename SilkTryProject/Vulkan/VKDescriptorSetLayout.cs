using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Vulkan;
using SilkTry.Vulkan.ManagedStruct;

namespace SilkTry.Vulkan;

/// <summary>
/// 描述符集布局（VkDescriptorSetLayout，教程《Descriptor set layout》章节）的包装。
/// 它回答的是"这套资源怎么摆"：有哪几个绑定（binding）、每个绑定是什么类型
///（uniform 缓冲 / 组合图像采样器 ……）、在哪几个着色器阶段可见、各有多少个描述符。
/// 布局本身**不含任何实际资源** —— 真正的资源在描述符集（VKDescriptorSet）里，
/// 而描述符集（VKDescriptorSet）是按这份布局、从描述符池（VKDescriptorPool）里分配出来的。
/// 本仓库把池直接挂在本布局名下（VKDescriptorPool.Parent 是本类）：一个池只服务一份布局，
/// 于是整条归属链是 设备 → 描述符集布局 → 描述符池 → 描述符集，
/// 正好对应 Vulkan 的硬性寿命要求（布局必须活得比用它的集久）。
///
/// 归属：**父对象是 VKLogicalDevice** —— vkCreateDescriptorSetLayout / vkDestroyDescriptorSetLayout 都是设备级调用，
/// 创建入口就在设备上（VKLogicalDevice.CreateDescriptorSetLayout）、登记表也在它那边
///（Valid 靠 VKLogicalDevice.IsDescriptorSetLayoutOfThis 反查）—— 同 VKShaderPipelineLayout / VKRenderPass 之于 VKLogicalDevice。
/// 描述符池的父对象则是本类（本类登记了名下全部池，池又登记了它分配出的集）。
///
/// 分工：创建 → VKLogicalDevice.CreateDescriptorSetLayout（布局本身）；
///       本布局名下的池 + 集 → VKDescriptorSetLayout.CreateDescriptorPool（建池时一次性把 maxSets 套集分配出来，
///       原生调用仍由设备执行）；
///       销毁 → Destroy() 先销毁名下的池（连带集），再转到 VKLogicalDevice.DestroyDescriptorSetLayout。
/// 绑定里可以带**不可变采样器**（VKSampler，见 <see cref="ManagedDescriptorSetLayoutBinding.ImmutableSamplers"/>）：
/// create-info 的 pImmutableSamplers 要读每个采样器实例的私有句柄，按访问性约定
/// 收口在本文件末尾的 VKSampler.CreateDescriptorSetLayout 里（同 VKImageView.CreateFramebuffer 读附件视图句柄：
/// 谁的句柄类型，谁就在自己的类里读写它）。
/// 管线布局那一路要读**每个布局实例的私有句柄**（create-info 的 pSetLayouts），同样收口在本文件末尾的
/// VKDescriptorSetLayout.CreatePipelineLayout 里。
///
/// 划界线：本类不直接做 vkAllocateDescriptorSets / vkUpdateDescriptorSets / vkCmdBindDescriptorSets ——
/// 分配由 CreateDescriptorPool 发起、真正调用在设备那一环；更新在描述符集（WriteXxx / CopyTo）；
/// 录制绑定在命令缓冲（VKCommandBuffer.BindDescriptorSets）。
/// 本类只做：记住创建时定下来的绑定表、创建 / 销毁名下的描述符池（池里带着集）、校验句柄归属、销毁。
///
/// 生命周期：**布局要活得比用到它的管线布局久，更要活得比它名下的池 / 集久**（Vulkan 要求）。
/// 所以本类的 Destroy() 先销毁登记表里的全部描述符池（池再连带销毁集），最后才销毁布局自己；
/// VKLogicalDevice.Destroy() 则先销毁管线布局（连带管线）、再调本类的 Destroy()；手动销毁时也要按同样的顺序。
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
    /// 这套布局声明了哪些绑定：**key 就是 binding 号**（对着 GLSL 同一个 set 内的 layout(binding = N)），
    /// value 是那条绑定的类型 / 数量 / 可见阶段 / 不可变采样器。
    /// 着色器里按 key 对位，不按遍历顺序；set 号不在 key 里 —— 它由管线布局里这份布局的数组下标决定
    ///（完整对照见仓库根目录 GLSLDescriptorLayoutMapping.md）。
    /// 创建时就复制了一份，之后调用方改自己那份字典不会影响本布局。
    /// </summary>
    public readonly ReadOnlyDictionary<uint, ManagedDescriptorSetLayoutBinding> Bindings;

    private readonly DescriptorSetLayout handle;

    // 本类自己要调原生 API（internal static CreatePipelineLayout）时才需要这一行。
    private readonly static Vk VK = Vk.GetApi();

    internal VKDescriptorSetLayout(
        VKLogicalDevice parent,
        DescriptorSetLayout handle,
        DescriptorSetLayoutCreateFlags flags,
        Dictionary<uint, ManagedDescriptorSetLayoutBinding> bindings)
    {
        Parent = parent;
        this.handle = handle;
        Flags = flags;
        Bindings = new ReadOnlyDictionary<uint, ManagedDescriptorSetLayoutBinding>(bindings);
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
    /// 销毁：先销毁本布局名下的全部描述符池（池会连带回收它分配出的集），
    /// 再把"从父对象集合里摘掉 + 调 vkDestroyDescriptorSetLayout"交给父对象做。
    ///
    /// 顺序由 Vulkan 决定：用到这份布局的管线布局必须先销毁（VKLogicalDevice.Destroy 就这么排），
    /// 而池 / 集必须比布局先死 —— 那一步由本方法自己保证。
    /// </summary>
    public void Destroy()
    {
        foreach (var descriptorPool in descriptorPools.Keys.ToArray())
        {
            try
            {
                descriptorPool.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        Parent.DestroyDescriptorSetLayout(this, CheckedHandle);
    }

    /// <summary>参数是裸句柄（DescriptorSetLayout），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(DescriptorSetLayout handleForCheck)
        => handleForCheck.Handle == handle.Handle;

    
    /// <summary>
    /// 两份布局是不是**可以互换**：同一实例，或者"定义完全相同"。
    ///
    /// 为什么要问这个：Vulkan 在 vkCmdBindDescriptorSets 里要求绑上去的集所用的布局，与管线布局里
    /// 对应 set 的那份布局"matching（same as, or identically defined as）"——
    /// 也就是说两份**内容一模一样**的布局可以互换（不必是同一个对象）。
    /// 定义相同指：flags 相同、绑定条数相同，且每条绑定的 binding 号 / 类型 / 数量 / 可见阶段都相同，
    /// 不可变采样器是同一批对象（采样器本身没有"内容相同"的概念，只能按对象认同）。
    ///
    /// 用途：VKDescriptorSet.BindDescriptorSets 在把集挂到管线布局之前先做这道检查，
    /// 免得把"不匹配的集"送进驱动（校验层会报 VUID-vkCmdBindDescriptorSets-pDescriptorSets-00358）。
    /// </summary>
    public bool IsCompatibleWith(VKDescriptorSetLayout other)
    {
        if (other == null)
        {
            throw new ArgumentNullException(nameof(other));
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Flags != other.Flags || Bindings.Count != other.Bindings.Count)
        {
            return false;
        }

        foreach (var kvp in Bindings)
        {
            ManagedDescriptorSetLayoutBinding candidate = kvp.Value;
            if(!other.Bindings.TryGetValue(kvp.Key, out ManagedDescriptorSetLayoutBinding otherCandidate))
            {
                return false;
            }

            if (candidate.DescriptorType != otherCandidate.DescriptorType ||
                candidate.DescriptorCount != otherCandidate.DescriptorCount ||
                candidate.StageFlags != otherCandidate.StageFlags)
            {
                return false;
            }

            VKSampler[]? samplers = candidate.ImmutableSamplers;
            VKSampler[]? otherSamplers = otherCandidate.ImmutableSamplers;

            if ((samplers == null) != (otherSamplers == null))
            {
                return false;
            }

            if (samplers != null)
            {
                if (samplers.Length != otherSamplers!.Length)
                {
                    return false;
                }

                for (int j = 0; j < samplers.Length; j++)
                {
                    if (!ReferenceEquals(samplers[j], otherSamplers[j]))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

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
    ///   bindings —— 绑定表，**至少一条**（key = binding 号，value = 类型 + 数量 + 可见阶段 + 可选的不可变采样器，
    ///               见 <see cref="ManagedDescriptorSetLayoutBinding"/>）；字典就是把"指针 + count"换成集合，
    ///               bindingCount 由条目数推导，binding 号直接用 key，
    ///               也就是着色器里同一个 set 内的 layout(binding = N)（set 号不在这里，见 GLSLDescriptorLayoutMapping.md）；
    ///   flags    —— create-info 的 flags，教程用 None。
    /// 建出来的布局要活得比用到它的管线布局久（Vulkan 要求）。
    /// 传进来的字典会被**复制**一份：不可变采样器的归一化（非采样器类型一律清空）只作用于副本，
    /// 调用方之后改自己那份也不会影响本布局。
    ///
    /// 绑定里带不可变采样器时要读**每个采样器实例的私有句柄**（create-info 的 pImmutableSamplers），
    /// 所以真正那段原生调用收口在 VKSampler.CreateDescriptorSetLayout 里
    ///（同帧缓冲 → VKImageView.CreateFramebuffer：谁的句柄类型，谁就在自己的类里读写它）：
    ///   VKLogicalDevice.CreateDescriptorSetLayout(…)   ← 本方法：public、零裸句柄
    ///     → VKSampler.CreateDescriptorSetLayout(…)     internal static 收口：组装绑定数组 + 不可变采样器数组
    /// 本方法只把自己的 CheckedHandle 当参数往下传（传入正确参数 = 授权），那边会再复核一次。
    /// </summary>
    public VKDescriptorSetLayout CreateDescriptorSetLayout(
        IReadOnlyDictionary<uint, ManagedDescriptorSetLayoutBinding> bindings,
        DescriptorSetLayoutCreateFlags flags = DescriptorSetLayoutCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        if (bindings == null || bindings.Count == 0)
        {
            throw new ArgumentException(
                "A descriptor set layout needs at least one binding.",
                nameof(bindings));
        }

        // 复制一份：不可变采样器的归一化（VKSampler.CreateDescriptorSetLayout 里做的）只改副本，
        // 而且布局记住的绑定表不会随调用方后续修改而变。
        Dictionary<uint, ManagedDescriptorSetLayoutBinding> normalizedBindings = new(bindings);
        DescriptorSetLayout layoutHandle = VKSampler.CreateDescriptorSetLayout(
            this,
            CheckedHandle,
            normalizedBindings,
            flags,
            allocationCallbacks);

        VKDescriptorSetLayout result = new(
            this,
            layoutHandle,
            flags,
            normalizedBindings);

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

public partial class VKSampler
{
    /// <summary>
    /// vkCreateDescriptorSetLayout 的收口（绑定里可能带**不可变采样器**，要读每个采样器实例的私有句柄，
    /// 所以放在本类而不是 VKLogicalDevice）。
    ///
    /// 调用链：
    ///   VKDescriptorSetLayout.CreateDescriptorSetLayout(…)   ← public、零裸句柄（入口在布局那边）
    ///     → 本方法                                          ← internal static 收口：把绑定表换成指针数组
    /// 之所以是 internal static：入参里有包装类（VKSampler）、局部数组里也有裸句柄，按访问性约定不出包装层。
    /// 之所以放在本类：`Sampler` 这个句柄类型由本类私有持有，同一个类的代码才能读任意实例的 private 字段
    ///（"谁的句柄类型，谁就在自己的类里读写它"）—— 同 VKImageView.CreateFramebuffer 读附件视图句柄。
    ///
    /// 授权方式：调用方把自己持有的设备句柄当参数传进来，传入正确参数 = 授权；
    /// 本方法用 IsHandleOfThis 复核设备句柄，再逐个复核不可变采样器确实登记在这台设备上。
    /// 指针的生命周期：绑定数组和不可变采样器数组都钉在本方法的栈上，
    /// 只在 vkCreateDescriptorSetLayout 调用期间需要，返回前一直有效。
    ///
    /// 入参 bindings 是**调用方给的副本**（VKDescriptorSetLayout.CreateDescriptorSetLayout 已经复制过），
    /// 本方法会就地归一化它（非采样器类型的 ImmutableSamplers 一律清空），这样布局记住的绑定表
    /// 与真正交给驱动的参数一致，调用方自己那份字典不受影响。
    ///
    /// 参数：
    ///   logicalDevice / logicalDeviceHandle —— 发起方与它的设备句柄（授权用）；
    ///   bindings                            —— 绑定表：key = binding 号，value = 该绑定的描述
    ///                                          （见 <see cref="ManagedDescriptorSetLayoutBinding"/>）；
    ///   flags                               —— VkDescriptorSetLayoutCreateInfo::flags；
    ///   allocationCallbacks                 —— 可选的内存分配回调。
    /// 返回值是"操作结果"（新建的 DescriptorSetLayout 句柄），由发起方包装 + 登记。
    /// </summary>
    internal static unsafe DescriptorSetLayout CreateDescriptorSetLayout(
        VKLogicalDevice logicalDevice,
        Device logicalDeviceHandle,
        Dictionary<uint, ManagedDescriptorSetLayoutBinding> bindings,
        DescriptorSetLayoutCreateFlags flags,
        AllocationCallbacks? allocationCallbacks)
    {
        if (!logicalDevice.IsHandleOfThis(logicalDeviceHandle))
        {
            throw new ArgumentException("The provided logical device handle is not from logical device.");
        }

        // 先把所有不可变采样器的句柄集中钉在一段栈内存上，再按绑定切片 ——
        // 这样无论多少条绑定，都只 stackalloc 一次，指针在调用返回前都有效。
        int immutableSamplerCount = 0;
        foreach (var key in bindings.Keys.ToArray())
        {
            ManagedDescriptorSetLayoutBinding existingBinding = bindings[key];

            if(existingBinding.DescriptorType == DescriptorType.Sampler || existingBinding.DescriptorType == DescriptorType.CombinedImageSampler)
            {
                immutableSamplerCount += existingBinding.ImmutableSamplers?.Length ?? 0;
            }
            else
            {
                existingBinding.ImmutableSamplers = null;
            }
            bindings[key] = existingBinding;
        }

        int samplerOffset = 0;
        Sampler* pImmutableSamplers = stackalloc Sampler[immutableSamplerCount];

        int index = 0;
        DescriptorSetLayoutBinding* pBindings = stackalloc DescriptorSetLayoutBinding[bindings.Count];
        foreach (var kvp in bindings)
        {
            ManagedDescriptorSetLayoutBinding binding = kvp.Value;

            if (binding.DescriptorCount == 0)
            {
                throw new ArgumentException(
                    "A descriptor set layout binding needs at least one descriptor.",
                    nameof(bindings));
            }


            Sampler* pBindingSamplers = null;
            if (binding.ImmutableSamplers != null)
            {
                // Vulkan 要求不可变采样器的个数和这个绑定的 descriptorCount 一致。
                if (binding.ImmutableSamplers.Length != binding.DescriptorCount)
                {
                    throw new ArgumentException(
                        "The number of immutable samplers must match the descriptor count of the binding.",
                        nameof(bindings));
                }

                for (int j = 0; j < binding.ImmutableSamplers.Length; j++)
                {
                    VKSampler sampler = binding.ImmutableSamplers[j];
                    if (sampler == null || !logicalDevice.IsSamplerOfThis(sampler))
                    {
                        throw new ArgumentException(
                            "The provided immutable sampler is not associated with logical device.",
                            nameof(bindings));
                    }

                    // 读本类实例的私有句柄 —— 没有"交出句柄"的接口，只是当参数往下传。
                    pImmutableSamplers[samplerOffset + j] = sampler.CheckedHandle;
                }

                pBindingSamplers = pImmutableSamplers + samplerOffset;
                samplerOffset += binding.ImmutableSamplers.Length;
            }
            pBindings[index++] = new DescriptorSetLayoutBinding(
                binding: kvp.Key,
                descriptorType: binding.DescriptorType,
                descriptorCount: binding.DescriptorCount,
                stageFlags: binding.StageFlags,
                pImmutableSamplers: pBindingSamplers
            );
        }

        DescriptorSetLayoutCreateInfo descriptorSetLayoutInfo = new(
            flags: flags,
            bindingCount: (uint)bindings.Count,
            pBindings: pBindings
        );

        DescriptorSetLayout layoutHandle;
        if (allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            if (VK.CreateDescriptorSetLayout(logicalDeviceHandle, in descriptorSetLayoutInfo, &dAllocationCallbacks, out layoutHandle) != Result.Success)
            {
                throw new NotSupportedException("failed to create descriptor set layout!");
            }
        }
        else if (VK.CreateDescriptorSetLayout(logicalDeviceHandle, in descriptorSetLayoutInfo, null, out layoutHandle) != Result.Success)
        {
            throw new NotSupportedException("failed to create descriptor set layout!");
        }

        return layoutHandle;
    }
}

