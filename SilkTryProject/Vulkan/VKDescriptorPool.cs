using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Vulkan;
using SilkTry.Vulkan.ManagedStruct;

namespace SilkTry.Vulkan;

/// <summary>
/// 描述符池（VkDescriptorPool，教程《Descriptor pool and sets》章节 createDescriptorPool()）的包装。
/// 描述符集不是单独 new 出来的：它们**从池里分配**，池预先声明"各类描述符各有多少个、最多能分几套"，
/// 需求在池的预算内就不会触发真正的内存分配。
/// 本仓库更进一步：**建池即建集** —— 创建时按父布局的绑定表算出预算，并立刻把 maxSets 套集全部分配出来，
/// 结果就是 <see cref="DescriptorSets"/>；之后没有"再分配一套"的调用，要更多集就再建一个池。
/// 所以池是描述符集的**所有者**，也是它们唯一的生命周期边界：
///   · 分配：VKDescriptorSetLayout.CreateDescriptorPool（建池时一次性分配，入口不在本类）；
///   · 归还：只有 Destroy() 一条路 —— vkDestroyDescriptorPool 会把池里的集一起回收，
///     本仓库不提供单独 vkFreeDescriptorSets（所以 FreeDescriptorSetBit 也用不上）。
///
/// 归属：**父对象是 VKDescriptorSetLayout** —— 一个池只服务一份布局，从它里面分配出来的集也只能用这份布局。
/// 整条链因此是 设备 → 描述符集布局 → 描述符池 → 描述符集，正好对应 Vulkan 的硬性寿命要求
///（布局必须活得比用它的集久）。所以：
///   · 创建入口在布局上（VKDescriptorSetLayout.CreateDescriptorPool），登记表也在布局那边
///     （Valid 靠 VKDescriptorSetLayout.IsDescriptorPoolOfThis 反查）；
///   · VKDescriptorSet 不必再单独存一份布局引用 —— 直接取 Parent.Parent（池的父对象）即可。
/// 代价（有意为之）：Vulkan 允许一个池给多份布局分配集、也允许运行时再分配 / 单独归还，本仓库都不做；
/// 确实需要时再给池换一种归属（例如像 IVKImageParent 那样抽接口），而不是在池里塞多份布局。
///
/// 分工：创建 + 分配 → VKDescriptorSetLayout.CreateDescriptorPool → 设备执行 vkCreateDescriptorPool / vkAllocateDescriptorSets；
///       销毁 → Destroy() → 布局摘登记 → 设备执行 vkDestroyDescriptorPool。
///
/// 划界线：本类不做 vkUpdateDescriptorSets（那是描述符集的 WriteXxx / CopyTo），
/// 也不做 vkCmdBindDescriptorSets（那是命令缓冲的 BindDescriptorSets）。
/// 本类只做：池的生命周期与它名下那批集的包装。
/// 可变描述符数量分配（VkDescriptorSetVariableDescriptorCountAllocateInfo，Vulkan 1.2 / 扩展）不支持 ——
/// 那要在分配时挂 pNext 链，教程用不到，等真要用时再在 VKLogicalDevice.AllocateDescriptorSets 里补。
///
/// 生命周期：池要活到**用它分配出来的集全部不用了**为止（集不能比池活得久）。
/// 池又必须活在它的父布局之内 —— VKDescriptorSetLayout.Destroy() 会先销毁登记表里的全部池（连带集），
/// 再销毁布局自己；VKLogicalDevice.Destroy() 则在管线布局之后调用这一步。
/// </summary>
public partial class VKDescriptorPool
{
    /// <summary>
    /// 父对象：这个池服务的描述符集布局（池按它的绑定切出描述符集）。销毁、归属校验都走它。
    /// 布局的 Destroy() 会先销毁本池（连带池里的集），再销毁布局自己。
    /// </summary>
    public readonly VKDescriptorSetLayout Parent;

    /// <summary>
    /// 创建时的 VkDescriptorPoolCreateInfo::flags。
    /// 本仓库不单独归还某一套（集在池创建时一次性分配、池一销毁就全部回收），
    /// 所以 FreeDescriptorSetBit 之类的标志在这里没有对应操作，照原样记录而已。
    /// </summary>
    public readonly DescriptorPoolCreateFlags Flags;

    /// <summary>
    /// 本池名下全部分配好的描述符集（创建时就按父布局切好，数量 = 创建时给的 maxSets）。
    /// 这些集**不单独销毁**：池的 Destroy() / 布局的 Destroy() 会把它们一起带走，
    /// 之后它们的 Valid 都会变成 false。要建更多集就再建一个池。
    /// </summary>
    public readonly ReadOnlyCollection<VKDescriptorSet> DescriptorSets;

    private readonly DescriptorPool handle;

    internal VKDescriptorPool(
        VKDescriptorSetLayout parent,
        DescriptorPool handle,
        DescriptorPoolCreateFlags flags,
        DescriptorSet[] descriptorSets)
    {
        Parent = parent;
        this.handle = handle;
        Flags = flags;
        VKDescriptorSet[] sets = new VKDescriptorSet[descriptorSets.Length];
        for (int i = 0; i < descriptorSets.Length; i++)
        {
            sets[i] = new VKDescriptorSet(this, descriptorSets[i]);
        }
        DescriptorSets = new ReadOnlyCollection<VKDescriptorSet>(sets);
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象（描述符集布局）有没有登记过自己
    ///（登记发生在 VKDescriptorSetLayout.CreateDescriptorPool 里）。
    /// </summary>
    public bool Valid => Parent.IsDescriptorPoolOfThis(this);

    private DescriptorPool CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The descriptor pool is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 本池名下有没有这套描述符集 —— VKDescriptorSet.Valid 靠它。
    ///（描述符集不能比池活得久，所以"归属证据"就是池创建时切出来的那批；
    ///  池的 Valid 又要求布局有效，于是布局 / 池任一失效，这些集都会自动变成无效。）
    /// </summary>
    public bool IsDescriptorSetOfThis(VKDescriptorSet descriptorSet)
        => DescriptorSets.Contains(descriptorSet) && Valid;

    /// <summary>
    /// 销毁池：vkDestroyDescriptorPool 会把池里的描述符集一起回收，所以不需要（也没法）逐套归还。
    /// 把"从布局的登记表里摘掉 + 调原生"交给父对象（布局）做，布局再把设备调用转给设备。
    /// 顺序由调用方负责：用到这些描述符集的命令缓冲 / 已提交命令必须先结束。
    /// </summary>
    public void Destroy()
        => Parent.DestroyDescriptorPool(this, CheckedHandle);

    /// <summary>参数是裸句柄（DescriptorPool），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(DescriptorPool handleForCheck)
        => handleForCheck.Handle == handle.Handle;
}

public partial class VKDescriptorSetLayout
{
    // 从本布局上建出来的描述符池：CreateDescriptorPool 时登记进来，
    // 之后靠 IsDescriptorPoolOfThis() 反查归属（VKDescriptorPool.Valid 就是这么来的）、销毁时摘掉，
    // value 是创建时用的 allocationCallbacks。池只服务本布局 —— 从池里分配的集也只用这份布局。
    private readonly ConcurrentDictionary<VKDescriptorPool, AllocationCallbacks?> descriptorPools = new ();

    /// <summary>
    /// 教程 createDescriptorPool() 里的 vkCreateDescriptorPool —— **建描述符池的入口就在本类**
    ///（public、签名里零裸句柄）。一个池只服务本布局：从它分配出来的集用的就是这份布局。
    ///
    /// 预算不用调用方给：按本布局的绑定表统计"每套里每种 DescriptorType 各有几个"，再乘以 maxSets，
    /// 得到 VkDescriptorPoolSize 数组（同一类型分散在多条绑定上会先累加）。
    ///
    /// 分配也在这里一次性做完：池建好后立刻用 vkAllocateDescriptorSets 把 maxSets 套集全部分配出来，
    /// 结果挂在 <see cref="VKDescriptorPool.DescriptorSets"/> 上（顺序无关，按布局切，每套都完整）。
    /// 也就是说本仓库的池是"建池即建集"，之后不再有分配调用；要更多集就再建一个池。
    /// 两段原生调用都在这里，所以后一步失败要回滚前一步（销毁刚建好的池），不把半成品留给调用方。
    ///
    /// 为什么入口在本类：池的归属在本类（登记表 descriptorPools），而 vkCreateDescriptorPool 要 Device 句柄，
    /// 所以原生调用交给设备那一环（同 CreateDescriptorSetLayout 的分工）：
    ///   VKDescriptorSetLayout.CreateDescriptorPool(maxSets, …)   ← 本方法：public、零裸句柄
    ///     → VKLogicalDevice.CreateDescriptorPool(…)              internal：补 Device 句柄、执行 vkCreateDescriptorPool
    ///     → VKLogicalDevice.AllocateDescriptorSets(…)            internal：执行 vkAllocateDescriptorSets
    ///   → new VKDescriptorPool(Parent: this, …) + descriptorPools[result] = allocationCallbacks
    ///
    /// 参数：
    ///   maxSets —— 一次分配几套描述符集（同时决定预留量 = 每套各类个数 × maxSets）；
    ///   flags   —— VkDescriptorPoolCreateInfo::flags（本仓库不单独归还某套，所以 FreeDescriptorSetBit 用不上，教程也是 None）。
    /// </summary>
    public VKDescriptorPool CreateDescriptorPool(
        uint maxSets,
        DescriptorPoolCreateFlags flags = DescriptorPoolCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        if (maxSets == 0)
        {
            throw new ArgumentException(
                "A descriptor pool needs room for at least one descriptor set.",
                nameof(maxSets));
        }

        // 同一类型可能分散在多条绑定上，先按"每套"累加；DescriptorCount 已经是这条绑定的槽位数。
        Dictionary<DescriptorType, uint> countsPerSet = new();
        foreach (ManagedDescriptorSetLayoutBinding binding in Bindings.Values)
        {
            countsPerSet.TryGetValue(binding.DescriptorType, out uint countPerSet);
            countsPerSet[binding.DescriptorType] = checked(countPerSet + binding.DescriptorCount);
        }

        // 预留量 = 每套个数 × 套数；顺便摊成原生需要的 VkDescriptorPoolSize[]。
        int index = 0;
        DescriptorPoolSize[] poolSizes = new DescriptorPoolSize[countsPerSet.Count];
        foreach (var pair in countsPerSet)
        {
            uint reservedCount = checked(pair.Value * maxSets);
            poolSizes[index++] = new DescriptorPoolSize
            {
                Type = pair.Key,
                DescriptorCount = reservedCount,
            };
        }

        DescriptorPool poolHandle = Parent.CreateDescriptorPool(
            poolSizes,
            maxSets,
            flags,
            allocationCallbacks);

        DescriptorSet[] setHandles;
        try
        {
            setHandles = Parent.AllocateDescriptorSets(poolHandle, this, CheckedHandle, maxSets);
        }
        catch
        {
            // 分配失败（预算不够 / 碎片化）时把刚建好的池销毁，别把半成品留给调用方 ——
            // 池本身已经建出来了，不销毁就会漏。
            Parent.DestroyDescriptorPool(this, CheckedHandle, poolHandle, allocationCallbacks);
            throw;
        }

        VKDescriptorPool result = new(
            this,
            poolHandle,
            flags,
            setHandles);

        descriptorPools[result] = allocationCallbacks;
        return result;
    }

    /// <summary>本布局名下有没有登记过这个池 —— VKDescriptorPool.Valid 靠它。</summary>
    public bool IsDescriptorPoolOfThis(VKDescriptorPool descriptorPool)
        => descriptorPools.ContainsKey(descriptorPool) && Valid;

    /// <summary>
    /// vkDestroyDescriptorPool 的归属这一环（写法同 DestroyDescriptorSetLayout）：先校验句柄 + 从登记表摘掉
    ///（顺便取出创建时记下的 ac），再把原生销毁交给设备那一环。
    /// 池里的描述符集由这次销毁一起回收 —— 本仓库不单独归还集，所以这里没有"逐套 Destroy"的分支。
    /// </summary>
    internal bool DestroyDescriptorPool(VKDescriptorPool descriptorPool, DescriptorPool handle)
    {
        if (!descriptorPool.IsHandleOfThis(handle) || !descriptorPools.TryRemove(descriptorPool, out var allocationCallbacks))
        {
            return false;
        }

        return Parent.DestroyDescriptorPool(this, CheckedHandle, handle, allocationCallbacks);
    }
}

public partial class VKLogicalDevice
{
    #region 描述符池（教程《Descriptor pool and sets》章节）

    /// <summary>
    /// vkCreateDescriptorPool 的"设备这一环"（分工同 CreateDescriptorSetLayout）：
    /// 发起方是描述符集布局（VKDescriptorSetLayout.CreateDescriptorPool，池的归属在那边），
    /// 本类补上只有它才持有的 Device 句柄、执行原生调用，返回新建的裸句柄。
    ///
    /// 参数：poolSizes / maxSets / flags / allocationCallbacks —— 原样对应 VkDescriptorPoolCreateInfo
    ///（poolSizes 由布局按绑定表统计后传下来：每套每类个数 × 套数）。
    /// </summary>
    internal unsafe DescriptorPool CreateDescriptorPool(
        DescriptorPoolSize[] poolSizes,
        uint maxSets,
        DescriptorPoolCreateFlags flags,
        AllocationCallbacks? allocationCallbacks)
    {
        Device device = CheckedHandle;

        DescriptorPool poolHandle;
        fixed (DescriptorPoolSize* pPoolSizes = poolSizes)
        {
            DescriptorPoolCreateInfo poolInfo = new(
                flags: flags,
                maxSets: maxSets,
                poolSizeCount: (uint)poolSizes.Length,
                pPoolSizes: pPoolSizes);

            if (allocationCallbacks != null)
            {
                AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
                if (VK.CreateDescriptorPool(device, in poolInfo, &dAllocationCallbacks, out poolHandle) != Result.Success)
                {
                    throw new NotSupportedException("failed to create descriptor pool!");
                }
            }
            else if (VK.CreateDescriptorPool(device, in poolInfo, null, out poolHandle) != Result.Success)
            {
                throw new NotSupportedException("failed to create descriptor pool!");
            }
        }

        return poolHandle;
    }

    /// <summary>
    /// vkDestroyDescriptorPool 的执行方：布局那边已经校验 + 摘了登记（顺便取出创建时的 ac），
    /// 本类复核布局句柄的归属后执行销毁。池里的描述符集由这次销毁一起回收 ——
    /// 本仓库的集是池创建时一次性分配的，从不单独归还，所以这里不需要逐套 vkFreeDescriptorSets。
    ///
    /// 参数里带布局 + 它的句柄，是为了在这一层再复核一次"这个池确实属于这台设备上登记的布局"
    ///（同别的执行方：不因为上游校验过就跳过）。布局创建池时分配失败的回滚也走这里。
    /// </summary>
    internal unsafe bool DestroyDescriptorPool(
        VKDescriptorSetLayout descriptorSetLayout,
        DescriptorSetLayout descriptorSetLayoutHandle,
        DescriptorPool handle,
        AllocationCallbacks? allocationCallbacks)
    {
        Device device = CheckedHandle;

        if (!descriptorSetLayout.IsHandleOfThis(descriptorSetLayoutHandle) ||
            !IsDescriptorSetLayoutOfThis(descriptorSetLayout))
        {
            return false;
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyDescriptorPool(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyDescriptorPool(device, handle, null);
        }

        return true;
    }

    #endregion
}
