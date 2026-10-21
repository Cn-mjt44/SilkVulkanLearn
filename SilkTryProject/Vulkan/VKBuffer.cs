using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 缓冲（教程《Vertex buffer creation》/《Index buffer》/《Uniform buffer》/《Texture image》的
/// staging buffer）的包装 —— **设备内存使用者**之一（继承 VKDeviceMemoryUser）。
///
/// VkBuffer 只是"一段 GPU 能读写的线性内存"的描述：多大（Size）、拿来干什么（Usage）、
/// 要不要跨队列族共享（SharingMode）；**它自己不带存储** —— 必须绑在一块 VKDeviceMemory 上，
/// 所以父对象就是那块内存（VKDeviceMemoryUser.Parent），偏移是 Offset。
///
/// 创建入口在**父内存**上：`memory.CreateBuffer(...)`（自己给 offset；一块内存可以放多个资源）或
/// `VKDeviceMemory.CreateBuffer(device, ...)`（一键创建捷径：教程 createBuffer() 的一行版）。
/// **没有单独的"绑内存"函数** —— 绑定就发生在创建里
///（vkCreateBuffer → 问需求 → 校验放得下 → vkBindBufferMemory 一条龙，见 VKDeviceMemory.CreateBuffer）。
///
/// 本类只留：创建时定下来的参数、归属校验、以及销毁。
///（Valid 反查父内存的登记表；Parent 为 null —— 比如静态捷径里先用一次的临时包装 —— 时直接算有效。）
/// </summary>
public partial class VKBuffer
{
    /// <summary>缓冲大小（字节）。教程里就是顶点数据 / 索引数据 / uniform 数据的大小。</summary>
    public readonly ulong Size;

    /// <summary>
    /// 用途：**一定要写对** —— 顶点缓冲 VertexBufferBit、索引缓冲 IndexBufferBit、
    /// uniform 缓冲 UniformBufferBit、还有拷贝用的 TransferSrcBit / TransferDstBit
    ///（教程的 staging buffer 就是 TransferSrcBit）。
    /// </summary>
    public readonly BufferUsageFlags Usage;

    /// <summary>共享模式：Exclusive（只给一个队列族用，性能最好）/ Concurrent（跨族共享）。</summary>
    public readonly SharingMode SharingMode;

    /// <summary>Concurrent 时列给驱动的那些队列族索引；Exclusive 时是 null。</summary>
    public readonly ReadOnlyCollection<uint>? QueueFamilyIndices;

    /// <summary>创建时的 flags。教程用不到（都是 None），留着对应 create-info 的字段。</summary>
    public readonly BufferCreateFlags Flags;
    public readonly VKDeviceMemory Parent;

    /// <summary>在这块内存里的起始偏移（一个资源独占一块内存时就是 0）。</summary>
    public readonly ulong Offset;

    private readonly Silk.NET.Vulkan.Buffer handle;

    internal VKBuffer(
        VKDeviceMemory parent,
        Silk.NET.Vulkan.Buffer handle,
        ulong offset,
        ulong size,
        BufferUsageFlags usage,
        SharingMode sharingMode,
        uint[]? queueFamilyIndices,
        BufferCreateFlags flags)
    {
        Parent = parent;
        Offset = offset;
        this.handle = handle;
        Size = size;
        Usage = usage;
        SharingMode = sharingMode;
        QueueFamilyIndices = queueFamilyIndices != null ? new ReadOnlyCollection<uint>(queueFamilyIndices) : null;
        Flags = flags;
    }

    public bool Valid => Parent.IsBufferOfThis(this);

    /// <summary>
    /// 本资源所属的逻辑设备 —— 设备级调用（同 VKImage.Device）要由它去执行；
    /// 顺着父内存往上找（缓冲的父对象是内存，内存的父对象才是逻辑设备）。
    /// </summary>
    public VKLogicalDevice Device => Parent.Parent;

    private Silk.NET.Vulkan.Buffer CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The buffer is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：先销毁这段缓冲上的全部视图，再把缓冲交给父内存做整件事
    ///（父内存取出这个资源创建时用的 ac、把登记摘掉，再让资源去销毁自己的句柄 —— 见 VKDeviceMemory.DestroyBuffer）。
    /// **注意：父内存不会跟着自动释放** —— 资源都销毁完之后，还得自己调一次 memory.Destroy()。
    /// </summary>
    public void Destroy()
    {
        foreach (var bufferView in bufferViews.Keys.ToArray())
        {
            try
            {
                bufferView.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        Parent.DestroyBuffer(this, CheckedHandle);
    }

    /// <summary>参数是裸句柄（Buffer），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(Silk.NET.Vulkan.Buffer handleForCheck)
        => handleForCheck.Handle == handle.Handle;
}

public partial class VKDeviceMemory
{
    private readonly ConcurrentDictionary<VKBuffer, AllocationCallbacks?> buffers = new ();
    
    /// <summary>
    /// **内存先行入口（缓冲）**：先有内存（device.AllocateMemory 或静态捷径分配），再在这块内存对象上
    /// 申请创建一块缓冲并绑定 —— 内部就是教程 createBuffer() 的 ①②③④：
    ///   vkCreateBuffer（设备执行）→ vkGetBufferMemoryRequirements 问需求 → 校验这块内存放得下
    ///   → vkBindBufferMemory → 登记（VKBuffer.Valid 的数据来源）。
    ///
    /// 参数：size / usage / sharingMode / queueFamilyIndices / flags 同 VKLogicalDevice 那边；
    ///       offset 是资源在这块内存里的落点 —— 一个资源独占一块内存时写 0，多个资源共用一块内存时
    ///       自己分段（校验：必须对齐 requirements.Alignment、offset + requirements.Size 不能超过 Size、
    ///       本内存的类型必须也在 requirements.MemoryTypeBits 里，不满足就直接抛）。
    /// </summary>
    public VKBuffer CreateBuffer(
        ulong size,
        BufferUsageFlags usage,
        ulong offset = 0,
        SharingMode sharingMode = SharingMode.Exclusive,
        uint[]? queueFamilyIndices = null,
        BufferCreateFlags flags = BufferCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        VKLogicalDevice device = Parent;
        DeviceMemory memoryHandle = CheckedHandle;

        // ① 建资源（设备执行 vkCreateBuffer）
        Silk.NET.Vulkan.Buffer handle = device.Buffer_Create(
            size, usage, out MemoryRequirements requirements, sharingMode, queueFamilyIndices, flags, allocationCallbacks);

        // 先包一层（这时还没登记，Valid 还是 false；下面几步只用 IsHandleOfThis 复核句柄，不看 Valid）

        // ② 问需求 → ③ 校验这块内存放得下
        CheckPlacement(requirements, offset);

        VKBuffer result = new(
            this, handle, offset, size, usage, sharingMode, queueFamilyIndices, flags);

        // ④ 先登记（Valid 的数据来源）+ 把 ac 存下来留给销毁用：
        //    下一步的归属校验要求"这块缓冲已经记在本内存名下"。
        buffers[result] = allocationCallbacks;

        // ⑤ 绑定；失败就把刚建出来的资源销毁并摘掉登记，别留下泄漏
        if (device.Buffer_BindMemory(result, handle, this, memoryHandle, offset) != Result.Success)
        {
            buffers.TryRemove(result, out _);
            device.Buffer_Destroy(result, handle, allocationCallbacks);
            throw new NotSupportedException("failed to bind buffer memory!");
        }

        return result;
    }


    /// <summary>
    /// **一键创建捷径（缓冲）** —— 教程 createBuffer() 的一行版：
    ///   建资源 → 问需求（vkGetBufferMemoryRequirements）→ 分配一块正好够它用的内存 → 在这块内存上绑定。
    /// 返回的 VKBuffer.Parent 就是那块新内存，同时通过 `out` 交给你 ——
    /// **它的生命周期由你自己管**（资源销毁完之后 memory.Destroy()），不会自动释放。
    /// </summary>
    public static VKBuffer CreateBuffer(
        VKLogicalDevice device,
        ulong size,
        BufferUsageFlags usage,
        MemoryPropertyFlags properties,
        out VKDeviceMemory memory,
        AllocationCallbacks? allocationCallbacks = null,
        SharingMode sharingMode = SharingMode.Exclusive,
        uint[]? queueFamilyIndices = null,
        BufferCreateFlags flags = BufferCreateFlags.None)
    {
        Silk.NET.Vulkan.Buffer handle = device.Buffer_Create(
            size, usage, out MemoryRequirements requirements, sharingMode, queueFamilyIndices, flags, allocationCallbacks);

        memory = device.AllocateMemory(requirements, properties, allocationCallbacks);

        VKBuffer result = new(
            memory, handle, 0, size, usage, sharingMode, queueFamilyIndices, flags);

        // 先登记再绑定：Buffer_BindMemory 的归属校验要看到这块缓冲已经记在新内存名下。
        memory.buffers[result] = allocationCallbacks;

        if (device.Buffer_BindMemory(result, handle, memory, memory.handle, 0) != Result.Success)
        {
            memory.buffers.TryRemove(result, out _);
            device.Buffer_Destroy(result, handle, allocationCallbacks);
            throw new NotSupportedException("failed to bind buffer memory!");
        }

        return result;
    }

    public bool IsBufferOfThis(VKBuffer user)
        => buffers.ContainsKey(user) && Valid;

    internal bool DestroyBuffer(VKBuffer buffer, Silk.NET.Vulkan.Buffer handle)
    {
        if (
            !buffer.IsHandleOfThis(handle) ||
            !buffers.TryRemove(buffer, out var allocationCallbacks)
        )
        {
            return false;
        }
        
        Parent.Buffer_Destroy(buffer, handle, allocationCallbacks);

        return true;
    }
}

public partial class VKLogicalDevice
{
    /// <summary>
    /// 教程 createBuffer() 里的 vkCreateBuffer。
    ///
    /// 真实调用在本类（只有它持着 Device 句柄），但**本方法只负责把缓冲建出来** ——
    /// 不包装、不登记，因为现在缓冲的父对象是**那块 VKDeviceMemory**：
    /// 创建入口在内存上（VKDeviceMemory.CreateBuffer），由那边调本方法拿到句柄，
    /// 再问需求 → 校验 → 绑定 → 包装 → 登记（同 Image_Create 的分工）。
    ///
    /// 参数：
    ///   size  —— 字节数（教程的顶点缓冲就是 vertices.Length * sizeof(Vertex)）；
    ///   usage —— 拿来干什么（顶点 VertexBufferBit / 索引 IndexBufferBit / staging TransferSrcBit……）；
    ///   sharingMode / queueFamilyIndices —— 跨队列族共享时才用得上，写法同 Image_Create。
    /// 之所以是 internal：外面要走 VKDeviceMemory.CreateBuffer，句柄不在包装层外流转。
    /// </summary>
    internal unsafe Silk.NET.Vulkan.Buffer Buffer_Create(
        ulong size,
        BufferUsageFlags usage,
        out MemoryRequirements memoryRequirements,
        SharingMode sharingMode = SharingMode.Exclusive,
        uint[]? queueFamilyIndices = null,
        BufferCreateFlags flags = BufferCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Device device = CheckedHandle;

        // pQueueFamilyIndices 只在创建这次调用里有效，所以用 fixed 钉住（null 时正好对应"不给族索引"）。
        uint queueFamilyIndexCount = queueFamilyIndices != null ? (uint)queueFamilyIndices.Length : 0;

        Silk.NET.Vulkan.Buffer handle;
        fixed (uint* pQueueFamilyIndices = queueFamilyIndices)
        {
            BufferCreateInfo bufferInfo = new(
                flags: flags,
                size: size,
                usage: usage,
                sharingMode: sharingMode,
                queueFamilyIndexCount: queueFamilyIndexCount,
                pQueueFamilyIndices: pQueueFamilyIndices);

            if (allocationCallbacks != null)
            {
                AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
                if (VK.CreateBuffer(device, in bufferInfo, &dAllocationCallbacks, out handle) != Result.Success)
                {
                    throw new NotSupportedException("failed to create buffer!");
                }
            }
            else if (VK.CreateBuffer(device, in bufferInfo, null, out handle) != Result.Success)
            {
                throw new NotSupportedException("failed to create buffer!");
            }
        }
        memoryRequirements = VK.GetBufferMemoryRequirements(device, handle);
        return handle;
    }

    /// <summary>
    /// vkDestroyBuffer：真实调用在本类；"摘登记 + 取出 ac"由 VKDeviceMemory.DestroyUser 负责，
    /// 分配回调在资源自己身上（VKDeviceMemoryUser.AllocationCallbacks）—— 和 Image_Destroy 一个写法。
    /// </summary>
    internal unsafe void Buffer_Destroy(VKBuffer buffer, Silk.NET.Vulkan.Buffer handle, AllocationCallbacks? allocationCallbacks)
    {
        Device device = CheckedHandle;

        if (!buffer.IsHandleOfThis(handle))
        {
            throw new ArgumentException(
                "The provided buffer handle is not associated with the given VKBuffer.");
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyBuffer(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyBuffer(device, handle, null);
        }
    }

    /// <summary>
    /// vkGetBufferMemoryRequirements：教程 createBuffer() 的第 ② 步（要 Device 句柄，所以实际执行在本类）。
    /// 传进来的包装类是**刚建好、还没登记**的那个，所以只用 IsHandleOfThis 复核句柄，不看 Valid。
    /// </summary>
    internal MemoryRequirements Buffer_GetMemoryRequirements(Silk.NET.Vulkan.Buffer handle)
        => VK.GetBufferMemoryRequirements(CheckedHandle, handle);


    /// <summary>
    /// **先问需求，不真建资源**（用内存之前的第一步）—— 按 create-info 问出 size / alignment / memoryTypeBits：
    ///   · 例：需求 = device.GetBufferMemoryRequirements(size, usage, ...)
    ///     → device.AllocateMemory(需求, 性质) → memory.CreateBuffer(size, usage)（创建 + 绑定一步到位）；
    ///   · 实例 API ≥ 1.3：用 vkGetDeviceBufferMemoryRequirements，直接按 create-info 算；
    ///   · 实例 API < 1.3：没有这个入口，所以这里**临时建一个 buffer → 问它的 requirements → 马上销毁**，
    ///     结果完全等价（只是多了一次建/销）。
    /// 注意：VKDeviceMemory.CreateBuffer(device, ...) 那条一键创建捷径不走这里 —— 它是"先真建资源再问"。
    /// </summary>
    public unsafe MemoryRequirements GetBufferMemoryRequirements(
        ulong size,
        BufferUsageFlags usage,
        SharingMode sharingMode = SharingMode.Exclusive,
        uint[]? queueFamilyIndices = null,
        BufferCreateFlags flags = BufferCreateFlags.None)
    {
        Device device = CheckedHandle;

        if(Parent.Parent.ApiVersion < Vk.Version13)
        {
            Silk.NET.Vulkan.Buffer buf = Buffer_Create(
                size,
                usage,
                out MemoryRequirements info,
                sharingMode,
                queueFamilyIndices,
                flags
            );
            VK.DestroyBuffer(device, buf, null);
            return info;
        }

        uint queueFamilyIndexCount = queueFamilyIndices != null ? (uint)queueFamilyIndices.Length : 0;
        fixed (uint* pQueueFamilyIndices = queueFamilyIndices)
        {
            BufferCreateInfo bufferInfo = new(
                flags: flags,
                size: size,
                usage: usage,
                sharingMode: sharingMode,
                queueFamilyIndexCount: queueFamilyIndexCount,
                pQueueFamilyIndices: pQueueFamilyIndices);

            DeviceBufferMemoryRequirements info = new(pCreateInfo: &bufferInfo);

            // MemoryRequirements2 就是 MemoryRequirements 套一层 sType / pNext；没有扩展要问，直接取里面那个。
            return VK.GetDeviceBufferMemoryRequirements(device, in info).MemoryRequirements;
        }
    }


    /// <summary>
    /// 教程 createBuffer() 收尾的 vkBindBufferMemory（由 VKDeviceMemory.CreateBuffer 在创建的最后一步调）：
    /// 缓冲句柄由 VKBuffer 自己当参数传下来、内存句柄由 VKDeviceMemory 自己传下来，
    /// 这里各用它们的 IsHandleOfThis 复核一遍 —— 都是"传进来的正确参数"，不是"谁交出来的"。
    /// </summary>
    internal Result Buffer_BindMemory(
        VKBuffer buffer,
        Silk.NET.Vulkan.Buffer bufferHandle,
        VKDeviceMemory memory,
        DeviceMemory memoryHandle,
        ulong offset)
    {
        Device device = CheckedHandle;

        if (!buffer.IsHandleOfThis(bufferHandle))
        {
            throw new ArgumentException(
                "The provided buffer handle is not associated with the given VKBuffer.");
        }

        if (!memory.IsHandleOfThis(memoryHandle))
        {
            throw new ArgumentException(
                "The provided device memory handle is not associated with the given VKDeviceMemory.");
        }

        if (!memory.IsBufferOfThis(buffer))
        {
            throw new ArgumentException(
                "The provided device memory is not associated with the given VKBuffer.");
        }

        return VK.BindBufferMemory(device, bufferHandle, memoryHandle, offset);
    }

}