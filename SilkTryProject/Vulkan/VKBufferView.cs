using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 缓冲视图（VkBufferView）的包装 —— 和 VKImageView 是同一个套路，只是"看"的对象从图像换成了缓冲。
/// 缓冲（VKBuffer）只是一段 GPU 能读写的**线性字节**，驱动并不知道该**怎么解读**它：
/// 视图就是这份解读 —— 按什么格式看（Format，比如一段 uint 当成 R32_UINT 还是 R32G32B32A32_UINT）、
/// 从哪看起（Offset）、看多长（Range）。
/// 它的用武之地是**纹素缓冲**：着色器里要么当 uniform 采样（UniformTexelBufferBit），
/// 要么当存储缓冲读写（StorageTexelBufferBit）—— 也就是说缓冲创建时的 Usage 必须带上这两个其一，
/// 不然建视图会被校验层拦下。
///
/// 归属：本类的父对象是 VKBuffer —— 视图是"这段缓冲的视图"，离开这段缓冲没有任何意义，
/// 也不能比它活得久（所以销毁缓冲前要先把视图都销毁掉）。
///
/// vkCreateBufferView / vkDestroyBufferView 本身是**设备级**调用，而本类读不到 Device 句柄
///（也不该读：访问性约定是句柄只在包装层内部流转，谁持着句柄谁做实际操作）。
/// 所以真正的 Vulkan 调用在 VKLogicalDevice.CreateBufferView / DestroyBufferView 里：
///   VKBuffer.CreateBufferView(参数) → 把自己的缓冲句柄交给 VKLogicalDevice →
///   那边校验 + vkCreateBufferView + **返回建出来的 BufferView 句柄** →
///   VKBuffer 再包装成本类并登记（VKBufferView.Valid 就是反查这段缓冲登记过没有）。
/// 也就是说：从外面建视图，一个裸句柄都不用拿。
/// </summary>
public partial class VKBufferView
{
    /// <summary>父对象：这张视图是"谁的视图"。销毁、归属校验都走它。</summary>
    public readonly VKBuffer Parent;

    /// <summary>
    /// 视图格式：这段缓冲按什么格式解读（texel 缓冲的格式要落在 VkPhysicalDeviceFeatures 允许的种类里）。
    /// </summary>
    public readonly Format Format;

    /// <summary>从缓冲的哪个字节开始看（必须满足格式要求的对齐，一般都从 0 开始）。</summary>
    public readonly ulong Offset;

    /// <summary>看多少个字节（不传就是"从 offset 一直看到缓冲末尾"，即 create-info 里的 VK_WHOLE_SIZE）。</summary>
    public readonly ulong Range;

    private readonly BufferView handle;
    private readonly static Vk VK = Vk.GetApi();

    internal VKBufferView(
        VKBuffer parent,
        BufferView handle,
        Format format,
        ulong offset,
        ulong range)
    {
        Parent = parent;
        this.handle = handle;
        Format = format;
        Offset = offset;
        Range = range;
    }

    /// <summary>
    /// 和 VKImageView / VKSurface 一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 VKBuffer.CreateBufferView 里）。
    /// </summary>
    public bool Valid => Parent.IsBufferViewOfThis(this);

    private BufferView CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The buffer view is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：和别的包装类一样，把"从父对象集合里摘掉 + 调 vkDestroyBufferView"交给父对象做
    ///（父对象再把实际的设备调用和句柄转给 VKLogicalDevice）。
    /// </summary>
    public void Destroy()
        => Parent.DestroyBufferView(this, CheckedHandle);

    /// <summary>参数是裸句柄（BufferView），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(BufferView handleForCheck)
        => handleForCheck.Handle == handle.Handle;
}

public partial class VKBuffer
{
    // 这段缓冲身上建出来的视图（写法和 VKImage 那边的 imageViews 一样）：CreateBufferView 时登记进来，
    // 之后靠 IsBufferViewOfThis() 反查归属（VKBufferView.Valid 就是这么来的 —— 视图离开了缓冲没有意义，
    // 所以登记表放在本类而不是 VKLogicalDevice 上）；顺便把 allocationCallbacks 记下来留给销毁用。
    private readonly ConcurrentDictionary<VKBufferView, AllocationCallbacks?> bufferViews = new ();

    /// <summary>
    /// 给这段缓冲建视图（教程没有单独讲这一章，用法和 VKImage.CreateImageView 一样）。
    ///
    /// 参数对应 VkBufferViewCreateInfo 的字段，默认值就是"从头看到尾、按缓冲自己的样子解读"：
    ///   format —— **必填**：这段缓冲按什么格式解读（R32_UINT / R32G32B32A32_UINT……）；
    ///   offset —— 从哪个字节看起，一般从 0 开始（要满足格式的对齐要求）；
    ///   range  —— 看多少个字节，**不传就是"从 offset 一直看到缓冲末尾"**（对应 VK_WHOLE_SIZE）；
    ///   flags  —— create-info 里虽有这个字段，但规范要求必须是 0，所以这里不暴露。
    ///
    /// 提醒：缓冲创建时的 Usage 得带上 UniformTexelBufferBit 或 StorageTexelBufferBit，否则会被校验层拦下。
    ///
    /// 调用方不用拿任何裸句柄：真正的 vkCreateBufferView 在 VKLogicalDevice（Device）里做
    ///（只有它持着 Device 句柄），本类把自己的 CheckedHandle 一起交出去；
    /// 那边把建出来的 BufferView 句柄还回来，本类再包装 + 登记（同 VKImage.CreateImageView 的分工）。
    /// </summary>
    public VKBufferView CreateBufferView(
        Format format,
        ulong offset = 0,
        ulong? range = null,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Silk.NET.Vulkan.Buffer bufferHandle = CheckedHandle;

        // 不传 range 就"从 offset 看到缓冲末尾"（create-info 里的 VK_WHOLE_SIZE 就是这个意思）。
        ulong bufferViewRange = range ?? Size - offset;

        BufferView viewHandle = Device.CreateBufferView(
            this,
            bufferHandle,
            format,
            offset,
            bufferViewRange,
            allocationCallbacks);

        VKBufferView result = new (this, viewHandle, format, offset, bufferViewRange);
        bufferViews[result] = allocationCallbacks;
        return result;
    }

    /// <summary>
    /// 本缓冲身上有没有登记过这个视图 —— VKBufferView.Valid 靠它。
    ///（视图的父对象是 VKBuffer，所以只查这里，同 VKImage.IsImageViewOfThis。）
    /// </summary>
    public bool IsBufferViewOfThis(VKBufferView bufferView)
        => bufferViews.ContainsKey(bufferView) && Valid;

    /// <summary>
    /// vkDestroyBufferView：登记表在本类手里（视图是"这段缓冲的视图"），所以先校验 + 摘登记，
    /// 再把"调 vkDestroyBufferView"转给父对象（它才持着 Device 句柄），
    /// allocationCallbacks 是创建时记下来的那一份（同 VKImage.DestroyImageView）。
    /// </summary>
    internal void DestroyBufferView(VKBufferView bufferView, BufferView handle)
    {
        if (!bufferView.IsHandleOfThis(handle) || !bufferViews.TryRemove(bufferView, out var allocationCallbacks))
        {
            return;
        }

        Device.DestroyBufferView(bufferView, handle, allocationCallbacks);
    }
}

public partial class VKLogicalDevice
{
    #region 缓冲视图

    /// <summary>
    /// vkCreateBufferView。
    ///
    /// 为什么具体操作写在这里：它需要 Device 句柄，而本类才是"持着 Device 的那一方"
    ///（同 CreateImageView 的分工）。视图的父对象是 VKBuffer，所以调用方（VKBuffer.CreateBufferView）
    /// 把自己的**缓冲裸句柄**一起传进来，本类先校验"确实是本设备上登记的这段缓冲"，再做实际创建，
    /// 最后把建出来的 BufferView 句柄原样还回去 —— 包装成 VKBufferView、以及登记
    ///（VKBufferView.Valid 的数据来源）都由 VKBuffer 做，因为只有它知道视图挂在哪段缓冲上。
    /// 参数含义见 VKBuffer.CreateBufferView 的注释。
    /// </summary>
    internal unsafe BufferView CreateBufferView(
        VKBuffer buffer,
        Silk.NET.Vulkan.Buffer bufferHandle,
        Format format,
        ulong offset,
        ulong range,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Device device = CheckedHandle;

        if (!buffer.IsHandleOfThis(bufferHandle) || !buffer.Valid)
        {
            throw new ArgumentException(
                "The buffer view handle is not valid: it is not the buffer of the parent VKLogicalDevice.");
        }

        // 写法同 CreateImageView：用带名参数的构造函数，SType 自动填成 BufferViewCreateInfo；
        // flags 是保留字段（必须为 0），所以不传，走构造函数默认值。
        // 它带 void* pNext，所以构造函数在元数据里是 unsafe 的 —— 本方法也因此要标 unsafe。
        BufferViewCreateInfo bufferViewInfo = new(
            buffer: bufferHandle,
            format: format,
            offset: offset,
            range: range);

        BufferView handle;
        if (allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            if (VK.CreateBufferView(device, in bufferViewInfo, &dAllocationCallbacks, out handle) != Result.Success)
            {
                throw new NotSupportedException("failed to create buffer view!");
            }
        }
        else if (VK.CreateBufferView(device, in bufferViewInfo, null, out handle) != Result.Success)
        {
            throw new NotSupportedException("failed to create buffer view!");
        }

        return handle;
    }

    /// <summary>
    /// vkDestroyBufferView：真正的设备调用在这里（本类持着 Device 句柄）。
    /// 登记表在 VKBuffer 那边，所以走到这一步说明那边已经校验过、并且把创建时记下的
    /// allocationCallbacks 取出来了；这里照惯例再用视图自己的 IsHandleOfThis 复核一次句柄，
    /// 然后才是 VK.DestroyBufferView。
    /// </summary>
    internal unsafe bool DestroyBufferView(VKBufferView bufferView, BufferView handle, AllocationCallbacks? allocationCallbacks)
    {
        Device device = CheckedHandle;

        if (!bufferView.IsHandleOfThis(handle))
        {
            return false;
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyBufferView(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyBufferView(device, handle, null);
        }
        return true;
    }

    #endregion
}
