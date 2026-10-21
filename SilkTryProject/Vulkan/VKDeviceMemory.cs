using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 设备内存（教程《Vertex buffer creation》的 findMemoryType / vkAllocateMemory / vkBindXxxMemory，
/// 《Texture image》上传纹理也走它）的包装。
///
/// 它是所有 GPU 资源的"底座"：VkBuffer / VkImage **本身不带存储** ——
/// 教程 createBuffer() 那几步就是标准流程：
///   ① vkCreateBuffer / vkCreateImage  拿到资源描述；
///   ② vkGetBufferMemoryRequirements / vkGetImageMemoryRequirements 问出 size / alignment / memoryTypeBits；
///   ③ vkAllocateMemory 按"满足 memoryTypeBits 又符合想要的性质（DeviceLocal / HostVisible + HostCoherent）"
///      的内存类型分配（就是教程的 findMemoryType()，见 VKPhysicalDevice.FindMemoryType）；
///   ④ vkBindBufferMemory / vkBindImageMemory 把内存绑到资源上，资源才算真正可用。
///
/// 归属：vkAllocateMemory / vkFreeMemory 都是设备级调用 → 父对象是 VKLogicalDevice
///（和别的包装类一样：创建时登记到父对象集合里，Valid 靠反查）。
///
/// ★ 本类同时是**资源的创建入口**（教程那四步全在这里一条龙，见 CreateBuffer / CreateImage）：
///   用法永远是**先有内存、再有资源** —— 先拿到一块设备内存，再在这块内存对象上申请创建资源：
///   · `memory.CreateBuffer(...)` / `memory.CreateImage(...)` —— 内存是你自己 AllocateMemory 来的
///     （想先问需求就用 device.GetBufferMemoryRequirements / GetImageMemoryRequirements：
///      1.3 上不真建资源，1.3 之前它会临时建一个再销毁），生命周期也由你管；
///   · `VKDeviceMemory.CreateBuffer(device, ...)` / `CreateImage(device, ...)` —— 教程 createBuffer()
///     的一行版：建资源 → 问需求 → 分配一块（正好够它用的）内存 → 创建 + 绑定。
///     分配出来的内存通过 `out VKDeviceMemory memory` 交给你 —— **生命周期由你自己管**
///     （调 memory.Destroy() 会先销毁绑在它上面的资源，再释放内存）。
///   两者都**没有单独的"绑内存"函数** —— 绑定就发生在创建里；一块内存**可以服务多个资源**
///   （登记表是字典；共用一块内存时用 offset 各自分区，也可以一个资源独占一块）。
///
/// 分工（本仓库的句柄流转约定：谁都不把自己的句柄交出去，只把正确参数往下传）：
///   · Map / Unmap 要 Device 句柄，而本类读不到 → 真正的调用在 VKLogicalDevice.Memory_Map /
///     Memory_Unmap 里（同 VKFence.Wait / VKSemaphore.Signal 的分工），本类把自己的 CheckedHandle 当参数传下去；
///   · 非一致性内存的同步（Flush / Invalidate）同理走 VKLogicalDevice.Memory_Flush / Memory_Invalidate，
///     范围和"原子对齐"由本类算好再传下去（VkMappedMemoryRange 要求 offset / size 按 nonCoherentAtomSize 对齐）；
///   · 创建 / 绑定里的 vkCreateXxx / vkGetXxxMemoryRequirements / vkBindXxxMemory 都在 VKLogicalDevice 里执行
///（它持着 Device 句柄），本类只做编排 + 校验 + 登记。
/// </summary>
public partial class VKDeviceMemory
{
    public readonly VKLogicalDevice Parent;

    /// <summary>分配的字节数（教程里就是 vkGetBufferMemoryRequirements 问到的 requirements.Size）。</summary>
    public readonly ulong Size;

    /// <summary>用的内存类型下标 —— 由 findMemoryType() 选出（见 VKPhysicalDevice.FindMemoryType）。</summary>
    public readonly uint MemoryTypeIndex;

    /// <summary>
    /// 这块内存的类型性质位（DeviceLocal / HostVisible / HostCoherent / HostCached / LazilyAllocated……）。
    /// 构造时从物理设备的内存类型表里查一次缓存下来（见 VKPhysicalDevice.GetMemoryTypePropertyFlags），
    /// 所以 IsHostVisible / IsHostCoherent / NeedsManualSynchronization 不会每次都去问驱动。
    /// </summary>
    public readonly MemoryPropertyFlags PropertyFlags;

    // 用我这块内存的所有资源（VKBuffer / VKImage） → 各自创建时用的分配回调（销毁时要原样还给 vkDestroyBuffer /
    // vkDestroyImage，所以和别的包装类的登记表一样把 ac 存在父对象这边）。
    // 创建时登记进来，之后靠 IsUserOfThis() 反查归属（VKBuffer.Valid / VKImage.Valid 就是这么来的）、销毁时摘掉。
    // 一块内存可以挂多个资源（各自占一段 offset），也可以只挂一个。

    private readonly DeviceMemory handle;

    // 映射状态：Map 成功之后、Unmap 之前。存下来是为了三件事：
    //   ① Flush / Invalidate 要求"这块内存当前正被映射"，而且范围必须落在映射范围之内（VkMappedMemoryRange 的 VUID）；
    //   ② Unmap 要拦住"没映射就解除映射"；
    //   ③ Destroy（vkFreeMemory）要拦住"还映射着就释放"。
    // 同一个时刻只可能有一个映射（vkMapMemory 不允许对同一块内存重复映射），所以记一段范围就够了。
    private bool isMapped = false;
    private ulong mappedOffset;
    private ulong mappedSize;

    internal VKDeviceMemory(VKLogicalDevice parent, DeviceMemory handle, ulong size, uint memoryTypeIndex)
    {
        Parent = parent;
        this.handle = handle;
        Size = size;
        MemoryTypeIndex = memoryTypeIndex;
        PropertyFlags = parent.Parent.GetMemoryTypePropertyFlags(memoryTypeIndex);
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 VKLogicalDevice.AllocateMemory 里）。
    /// </summary>
    public bool Valid => Parent.IsMemoryOfThis(this);

    private DeviceMemory CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The device memory is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：先销毁绑在本内存上的全部资源（缓冲区 / 图像，图像会先销毁自己的视图），
    /// 再把"从父对象集合里摘掉 + 调 vkFreeMemory"交给父对象做。
    /// 前提：还映射着的话先 Unmap（vkFreeMemory 要求这块内存当前没被映射），所以映射状态先检查。
    /// </summary>
    public void Destroy()
    {
        if (isMapped)
        {
            throw new InvalidOperationException(
                "This device memory is still mapped; call Unmap() before destroying it.");
        }

        foreach (var buffer in buffers.Keys.ToArray())
        {
            try
            {
                buffer.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        foreach (var image in images.Keys.ToArray())
        {
            try
            {
                image.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        Parent.DestroyMemory(this, CheckedHandle);
    }

    /// <summary>参数是裸句柄（DeviceMemory），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(DeviceMemory handleForCheck)
        => handleForCheck.Handle == handle.Handle;


    #region 内存同步：映射 / 解除映射，以及非一致性内存的 Flush / Invalidate（vulkan-tutorial 之外的那一半）

    /// <summary>
    /// 这块内存的类型是不是 HostVisible —— 只有 HostVisible 的类型才能映射（vkMapMemory）。
    /// </summary>
    public bool IsHostVisible => PropertyFlags.HasFlag(MemoryPropertyFlags.HostVisibleBit);

    /// <summary>
    /// 是不是"主机一致性"内存：CPU 写完设备直接能看到、设备写完 CPU 直接能读到，
    /// 映射之后不需要 Flush() / Invalidate()（教程 findMemoryType 挑的 HostVisible + HostCoherent 就是这种）。
    /// </summary>
    public bool IsHostCoherent => PropertyFlags.HasFlag(MemoryPropertyFlags.HostCoherentBit);

    /// <summary>
    /// 是不是"非一致性"的 HostVisible 内存 —— 不少独显的 DeviceLocal + HostVisible 内存就属于这种：
    /// 两边的缓存各看各的，CPU 写完要 Flush() 才能让设备看见，设备写完要 Invalidate() 才能让 CPU 读到。
    /// 教程没碰到（它特意要 HostCoherent），但少了这一块，包装层在不一致的机器上会默默读到脏数据。
    /// </summary>
    public bool NeedsManualSynchronization => IsHostVisible && !IsHostCoherent;

    /// <summary>当前是否处于映射状态（Map 成功之后、Unmap 之前）。</summary>
    public bool IsMapped => isMapped;

    /// <summary>
    /// 教程 createBuffer() 里的 vkMapMemory：把这块内存映射到 CPU 地址空间，返回可读写的裸指针。
    /// offset / size 一般给 0 / Size（整块映射；教程就是这么用的），flags 目前只有 None 可用。
    /// **用完必须 Unmap**（教程在 memcpy 之后立刻 vkUnmapMemory）。
    ///
    /// 校验：内存类型必须是 HostVisible（不然映射本来就不合法），并且不能处于"已经映射着"的状态
    ///（vkMapMemory 不允许对同一块内存重复映射）。
    /// 映射之后：HostCoherent 的内存，CPU 写完设备直接就能看到；非一致性内存
    ///（NeedsManualSynchronization）写完要 Flush()、要读回设备写的结果得先 Invalidate()。
    /// 教程挑的是 HostVisible + HostCoherent，所以那边没有这一步。
    /// </summary>
    public Result Map(ulong offset, ulong size, out Span<byte> pData, MemoryMapFlags flags = MemoryMapFlags.None)
    {
        if (isMapped)
        {
            throw new InvalidOperationException(
                "This device memory is already mapped; unmap it before mapping it again.");
        }

        if (!IsHostVisible)
        {
            throw new InvalidOperationException(
                "This device memory is not host visible; only host visible memory types can be mapped.");
        }

        Result result = Parent.Memory_Map(this, CheckedHandle, offset, size, flags, out pData);

        if (result == Result.Success)
        {
            isMapped = true;
            mappedOffset = offset;
            mappedSize = size;
        }

        return result;
    }

    /// <summary>整块映射的便捷版本（offset = 0、size = Size），其余同 Map(offset, size, out pData, flags)。</summary>
    public Result Map(out Span<byte> pData, MemoryMapFlags flags = MemoryMapFlags.None)
        => Map(0, Size, out pData, flags);

    /// <summary>
    /// vkUnmapMemory：解除映射（教程在 memcpy 之后立刻调它）。
    /// 注意：**非一致性内存解除映射不会隐式 flush** —— 没 Flush 过的 CPU 写入可能永远看不到，
    /// 所以上传类用法是"写完 → Flush() → Unmap()"这个顺序。
    /// </summary>
    public void Unmap()
    {
        if (!isMapped)
        {
            throw new InvalidOperationException("This device memory is not mapped.");
        }

        Parent.Memory_Unmap(this, CheckedHandle);

        isMapped = false;
        mappedOffset = 0;
        mappedSize = 0;
    }

    /// <summary>
    /// vkFlushMappedMemoryRanges（整块）：把主机写到映射内存里的字节刷到设备可见的内存域。
    /// 上传类用法（memcpy 之后、提交之前）基本都用这个 —— 它就是 Flush(0, Vk.WholeSize)，
    /// 即"从 0 一直到映射结尾"，一定满足 Vulkan 对 range 的对齐要求。
    /// HostCoherent 的内存上调用是无害的空操作，所以不必先判断 NeedsManualSynchronization。
    /// </summary>
    public Result Flush()
        => Flush(0, Vk.WholeSize);

    /// <summary>
    /// vkFlushMappedMemoryRanges（指定范围）。offset / size 是**相对这块内存**的
    ///（和 Map 的 offset 同一个坐标系，不是相对映射起点）。
    /// 校验与对齐（都来自 VkMappedMemoryRange 的 VUID）：
    ///   · 这块内存必须正被映射，且范围必须落在映射范围内；
    ///   · offset 必须是 nonCoherentAtomSize 的整数倍 → 内部帮你向下取整；
    ///   · size 也要是它的整数倍，或者正好到内存末尾 → 内部向上取整，所以在映射边界上
    ///     可能刷得比你要求的多一点（对 flush 来说多刷是安全的，代价只是同步范围大一点）；
    ///   · 取整后凑不出合法范围时直接抛（提示你整块映射 + 用 Flush()）。
    /// 换句话说：原子块是同步的最小单位，别指望只刷一块原子的前半截。
    /// </summary>
    public Result Flush(ulong offset, ulong size)
        => Synchronize(offset, size, invalidate: false);

    /// <summary>
    /// vkInvalidateMappedMemoryRanges（整块）：反过来 —— 设备刚写进这块内存的内容，
    /// CPU 要 **Invalidate 之后**才保证读到（读回结果 / 调试回读时用）。参数含义同 Flush()。
    /// </summary>
    public Result Invalidate()
        => Invalidate(0, Vk.WholeSize);

    /// <summary>vkInvalidateMappedMemoryRanges（指定范围）：参数与对齐规则同 Flush(offset, size)。</summary>
    public Result Invalidate(ulong offset, ulong size)
        => Synchronize(offset, size, invalidate: true);

    /// <summary>
    /// Flush / Invalidate 的公共实现：先按 VkMappedMemoryRange 的要求把范围算成"合法的原子范围"，再交给设备执行。
    /// 和创建入口一样，这里只做编排 + 校验：真正调 vkFlushMappedMemoryRanges / vkInvalidateMappedMemoryRanges
    /// 的是 VKLogicalDevice（它持着 Device 句柄）。
    /// </summary>
    private Result Synchronize(ulong offset, ulong size, bool invalidate)
    {
        if (!isMapped)
        {
            throw new InvalidOperationException(
                "This device memory must be mapped before flushing or invalidating it" +
                " (VkMappedMemoryRange requires the memory to be currently host mapped).");
        }

        DeviceMemory memoryHandle = CheckedHandle;
        ulong mappedEnd = mappedOffset + mappedSize;
        ulong atomSize = Parent.Parent.NonCoherentAtomSize;

        if (atomSize == 0)
        {
            atomSize = 1;
        }

        // ① 范围必须落在当前映射范围内（VUID-VkMappedMemoryRange-size-00685 / -00686）
        if (size == Vk.WholeSize)
        {
            if (offset < mappedOffset || offset >= mappedEnd)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offset),
                    $"The offset {offset} is outside the currently mapped range [{mappedOffset}, {mappedEnd}).");
            }

            // VUID-VkMappedMemoryRange-size-01389：VK_WHOLE_SIZE 要求映射结尾是原子的整数倍，或者正好是内存末尾
            if (mappedEnd % atomSize != 0 && mappedEnd != Size)
            {
                throw new ArgumentException(
                    $"This memory is mapped up to {mappedEnd}, which is neither a multiple of nonCoherentAtomSize" +
                    $" ({atomSize}) nor the end of the memory ({Size}); map the whole memory (Map()) instead.");
            }
        }
        else if (offset < mappedOffset || offset + size > mappedEnd)
        {
            throw new ArgumentOutOfRangeException(
                nameof(size),
                $"The range [{offset}, {offset + size}) is not contained in the currently mapped range" +
                $" [{mappedOffset}, {mappedEnd}).");
        }

        // ② offset 向下取整到 nonCoherentAtomSize（VUID-VkMappedMemoryRange-offset-00687）
        ulong alignedOffset = offset - (offset % atomSize);

        if (alignedOffset < mappedOffset)
        {
            // 映射起点本身没对齐（比如 Map(64, …) 而原子是 128）时：取整不能越出映射，
            // 只能从映射起点开始 —— 而映射起点本身不是原子的整数倍时就没有合法解了。
            if (mappedOffset % atomSize != 0)
            {
                throw new ArgumentException(
                    $"This memory was mapped at offset {mappedOffset}, which is not a multiple of" +
                    $" nonCoherentAtomSize ({atomSize}); map the whole memory (Map()) or map at an aligned" +
                    " offset if you want to flush / invalidate a sub-range.");
            }

            alignedOffset = mappedOffset;
        }

        ulong alignedSize;

        if (size == Vk.WholeSize)
        {
            // VK_WHOLE_SIZE 本身就是"从 offset 一直到映射结尾"，直接透传（只要求 offset 对齐）
            alignedSize = Vk.WholeSize;
        }
        else
        {
            // ③ size 向上取整到原子大小，并裁到映射范围内（00685）
            ulong end = offset + size;
            ulong alignedEnd = end % atomSize == 0 ? end : end + (atomSize - end % atomSize);

            if (alignedEnd > mappedEnd)
            {
                alignedEnd = mappedEnd;
            }

            alignedSize = alignedEnd - alignedOffset;

            // VUID-VkMappedMemoryRange-size-01390：size 不是原子大小的整数倍时，必须正好到这块内存的末尾
            if (alignedSize % atomSize != 0 && alignedOffset + alignedSize != Size)
            {
                throw new ArgumentException(
                    $"The range cannot be expressed as whole nonCoherentAtomSize ({atomSize}) atoms inside the" +
                    " current mapping; map the whole memory (Map()) and use Flush() / Invalidate() instead.");
            }
        }

        return invalidate
            ? Parent.Memory_Invalidate(this, memoryHandle, alignedOffset, alignedSize)
            : Parent.Memory_Flush(this, memoryHandle, alignedOffset, alignedSize);
    }

    #endregion



    /// <summary>
    /// 校验"这块内存放得下这个资源"（多个资源共用一块内存时，offset 就是在这里把关的）。
    /// 三条都来自 Vulkan 规范对 vkBindXxxMemory 的要求：
    ///   · offset 必须是 requirements.Alignment 的整数倍；
    ///   · offset + requirements.Size 不能超过这块内存的 Size；
    ///   · 本内存的类型必须也在 requirements.MemoryTypeBits 里（不然这个资源根本不能用这种类型）。
    /// </summary>
    private void CheckPlacement(MemoryRequirements requirements, ulong offset)
    {
        if (requirements.Alignment != 0 && offset % requirements.Alignment != 0)
        {
            throw new ArgumentException(
                $"The offset {offset} is not aligned to the resource's memory alignment {requirements.Alignment}.");
        }

        if (offset + requirements.Size > Size)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                $"The resource needs {requirements.Size} bytes at offset {offset}, but this memory is only {Size} bytes.");
        }

        if ((requirements.MemoryTypeBits & (1u << (int)MemoryTypeIndex)) == 0)
        {
            throw new ArgumentException(
                "This memory's type index is not allowed by the resource's memoryTypeBits.");
        }
    }

}

public partial class VKLogicalDevice
{
    

    // 设备内存（教程《Vertex buffer creation》/《Texture image》的底座）：创建时登记进来，
    // 之后靠 IsMemoryOfThis() 反查归属（VKDeviceMemory.Valid 就是这么来的）。
    // 注意：VKBuffer / VKImage 不再登记在设备上 —— 它们的父对象是**那块内存**，
    // 归属校验和登记表都在 VKDeviceMemory 那边（见 VKDeviceMemory.users）。
    private readonly ConcurrentDictionary<VKDeviceMemory, AllocationCallbacks?> memories = new ();

    #region 设备内存（教程《Vertex buffer creation》/《Texture image》章节）

    /// <summary>
    /// 教程 createBuffer() 里那段"分配内存"的 vkAllocateMemory —— 也是最贴近教程的入口：
    /// 直接把 vkGetXxxMemoryRequirements 问到的 requirements 和想要的内存性质丢进来，
    /// 本类用 Parent（物理设备）的 FindMemoryType() 挑出内存类型下标，再调 vkAllocateMemory。
    ///
    /// 参数：
    ///   requirements —— VKBuffer.MemoryRequirements（将来还有 VKImage 那份）问出来的三个值，
    ///                   这里只用 Size 和 MemoryTypeBits；
    ///   properties   —— 想要什么性质：设备本地用 DeviceLocalBit；CPU 要写就用 HostVisibleBit，
    ///                   不想手动 flush 再加 HostCoherentBit（教程的 staging buffer 就是这两个）。
    ///
    /// 照本仓库惯例：建出来 → 登记到本设备的集合里（VKDeviceMemory.Valid 就是反查这里）→ 返回包装类。
    /// </summary>
    public VKDeviceMemory AllocateMemory(
        MemoryRequirements requirements,
        MemoryPropertyFlags properties,
        AllocationCallbacks? allocationCallbacks = null)
        => AllocateMemory(
            requirements.Size,
            Parent.FindMemoryType(requirements.MemoryTypeBits, properties),
            allocationCallbacks);


    /// <summary>
    /// vkAllocateMemory 的真身：内存类型下标自己给（不经过 FindMemoryType）。
    /// 上面那个"requirements + properties"的重载就是拿它实现的；
    /// 之所以是 internal：外面要那一版就够了，这一版得调用方自己算好类型下标。
    /// </summary>
    internal unsafe VKDeviceMemory AllocateMemory(
        ulong size,
        uint memoryTypeIndex,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Device device = CheckedHandle;

        MemoryAllocateInfo allocateInfo = new(
            allocationSize: size,
            memoryTypeIndex: memoryTypeIndex);

        DeviceMemory handle;
        if (allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            if (VK.AllocateMemory(device, in allocateInfo, &dAllocationCallbacks, out handle) != Result.Success)
            {
                throw new NotSupportedException("failed to allocate memory!");
            }
        }
        else if (VK.AllocateMemory(device, in allocateInfo, null, out handle) != Result.Success)
        {
            throw new NotSupportedException("failed to allocate memory!");
        }

        VKDeviceMemory result = new(this, handle, size, memoryTypeIndex);
        memories[result] = allocationCallbacks;
        return result;
    }

    /// <summary>vkFreeMemory（写法同 DestroyImage / DestroyBuffer）。</summary>
    internal unsafe bool DestroyMemory(VKDeviceMemory memory, DeviceMemory handle)
    {
        Device device = CheckedHandle;

        if (!memory.IsHandleOfThis(handle) || !memories.TryRemove(memory, out var allocationCallbacks))
        {
            return false;
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.FreeMemory(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.FreeMemory(device, handle, null);
        }
        return true;
    }

    /// <summary>设备内存的归属校验：本设备建出来的才算 —— VKDeviceMemory.Valid 就是这么来的。</summary>
    public bool IsMemoryOfThis(VKDeviceMemory memory)
        => memories.ContainsKey(memory) && Valid;


    /// <summary>教程 createBuffer() 里 vkMapMemory 的那一步：把内存映射到 CPU 地址空间。</summary>
    internal unsafe Result Memory_Map(
        VKDeviceMemory memory,
        DeviceMemory handle,
        ulong offset,
        ulong size,
        MemoryMapFlags flags,
        out Span<byte> pData)
    {
        Device device = CheckedHandle;

        if (!memory.IsHandleOfThis(handle))
        {
            throw new ArgumentException(
                "The provided device memory handle is not associated with the given VKDeviceMemory.");
        }

        // Silk.NET 只有"指针版"的 MapMemory，所以先映射到局部变量再交出去（不能对 out 参数取地址）。
        void* pMapped = null;
        Result result = VK.MapMemory(device, handle, offset, size, flags, &pMapped);
        pData = new Span<byte>(pMapped, (int)size);
        return result;
    }

    /// <summary>vkUnmapMemory：解除映射（教程在 memcpy 之后立刻调它）。</summary>
    internal void Memory_Unmap(VKDeviceMemory memory, DeviceMemory handle)
    {
        Device device = CheckedHandle;

        if (!memory.IsHandleOfThis(handle))
        {
            throw new ArgumentException(
                "The provided device memory handle is not associated with the given VKDeviceMemory.");
        }

        VK.UnmapMemory(device, handle);
    }

    /// <summary>
    /// vkFlushMappedMemoryRanges：主机写完映射内存之后，把这段字节刷成"设备可见"
    ///（只有非一致性内存需要；HostCoherent 的内存上调用是无害的空操作）。
    /// 和 Memory_Map / Memory_Unmap 一样：句柄由 VKDeviceMemory 当参数传下来，这里复核一遍再动手；
    /// 范围和原子对齐是 VKDeviceMemory.Flush 算好之后传下来的（见 VkMappedMemoryRange 的 VUID）。
    /// </summary>
    internal unsafe Result Memory_Flush(
        VKDeviceMemory memory,
        DeviceMemory handle,
        ulong offset,
        ulong size)
    {
        Device device = CheckedHandle;

        if (!memory.IsHandleOfThis(handle))
        {
            throw new ArgumentException(
                "The provided device memory handle is not associated with the given VKDeviceMemory.");
        }

        // 和别的 info 结构一样：SType 交给构造函数填（必须至少传一个参数，
        // 否则 new S() 只会拿到零值）。
        MappedMemoryRange range = new(
            sType: StructureType.MappedMemoryRange,
            memory: handle,
            offset: offset,
            size: size);

        return VK.FlushMappedMemoryRanges(device, 1, &range);
    }

    /// <summary>
    /// vkInvalidateMappedMemoryRanges：反过来 —— 设备写到这块内存的内容，要 Invalidate 之后 CPU 才保证读到
    ///（读回结果时用；同样只对非一致性内存有意义）。其余同 Memory_Flush。
    /// </summary>
    internal unsafe Result Memory_Invalidate(
        VKDeviceMemory memory,
        DeviceMemory handle,
        ulong offset,
        ulong size)
    {
        Device device = CheckedHandle;

        if (!memory.IsHandleOfThis(handle))
        {
            throw new ArgumentException(
                "The provided device memory handle is not associated with the given VKDeviceMemory.");
        }

        MappedMemoryRange range = new(
            sType: StructureType.MappedMemoryRange,
            memory: handle,
            offset: offset,
            size: size);

        return VK.InvalidateMappedMemoryRanges(device, 1, &range);
    }
#endregion
}