using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 一次稀疏绑定（VkBindSparseInfo）的**无指针版**。
///
/// 为什么要自己写一个：VkBindSparseInfo 里全都是指针 + count（等待信号量、缓冲绑定、不透明图像绑定、
/// 图像绑定、信号量），而且每一层绑定（VkSparseBufferMemoryBindInfo 之类）里又嵌着属于
/// VKBuffer / VKImage / VKDeviceMemory 的裸句柄。直接用它的话，调用方既要自己 fixed 好几层数组，
/// 又得自己去拿那些包装类的私有句柄。
///
/// 这里把：
///   · 指针 + count 换成数组（长度就是 count，空 / null 就是"这一类没有"）；
///   · VKBuffer / VKImage / VKDeviceMemory 换成包装类（转换在各自的类里做 —— "谁的句柄类型，
///     谁就在自己的类里读写它"）；
///   · VkSemaphore 换成 VKSemaphore（只能是二进制信号量：VkBindSparseInfo 不支持时间线信号量）。
///
/// 和 VKDeviceQueue.BindSparse 的分工：本类型只负责"把绑定内容描述出来"，
/// 真正的 VkBindSparseInfo 由 VKDeviceQueue.BindSparse 那条链路拼出来并调用。
/// </summary>
public struct ManagedBindSparseInfo
{
    /// <summary>绑定开始前要等哪些信号量（二进制；null / 空 = 不等）。</summary>
    public VKSemaphore[]? WaitSemaphores;

    /// <summary>缓冲的稀疏内存绑定；null / 空 = 这次不绑缓冲。</summary>
    public ManagedSparseBufferMemoryBindInfo[]? BufferBinds;

    /// <summary>图像**整层**（opaque）的稀疏内存绑定；null / 空 = 没有。</summary>
    public ManagedSparseImageOpaqueMemoryBindInfo[]? ImageOpaqueBinds;

    /// <summary>图像**按子资源区域**的稀疏内存绑定；null / 空 = 没有。</summary>
    public ManagedSparseImageMemoryBindInfo[]? ImageBinds;

    /// <summary>绑定完成后要置位哪些信号量（二进制；null / 空 = 不置位）。</summary>
    public VKSemaphore[]? SignalSemaphores;
}

/// <summary>
/// VkSparseMemoryBind 的包装类版：把一段资源区间绑到（或解绑自）一块设备内存。
/// Memory 为 null 表示**解绑**（对应原生结构体的 memory = VK_NULL_HANDLE）。
/// </summary>
public struct ManagedSparseMemoryBind
{
    /// <summary>资源内偏移（缓冲是字节，图像按 opaque 绑定时是"整个 mip / 层的起始"）。</summary>
    public ulong ResourceOffset;

    /// <summary>资源区间的大小（字节）。</summary>
    public ulong Size;

    /// <summary>绑到哪块内存；null = 解绑这段区间。</summary>
    public VKDeviceMemory? Memory;

    /// <summary>在内存里的起始偏移。</summary>
    public ulong MemoryOffset;

    /// <summary>绑定标志（目前只有 None）。</summary>
    public SparseMemoryBindFlags Flags;
}

/// <summary>VkSparseBufferMemoryBindInfo 的包装类版：一段缓冲 + 它的若干条区间绑定。</summary>
public struct ManagedSparseBufferMemoryBindInfo
{
    /// <summary>要绑的缓冲（必填）。</summary>
    public VKBuffer? Buffer;

    /// <summary>这个缓冲上的绑定列表；null / 空 = 没有绑定。</summary>
    public ManagedSparseMemoryBind[]? Binds;
}

/// <summary>VkSparseImageOpaqueMemoryBindInfo 的包装类版：一张图像 + 它的若干条整层绑定。</summary>
public struct ManagedSparseImageOpaqueMemoryBindInfo
{
    /// <summary>要绑的图像（必填）。</summary>
    public VKImage? Image;

    /// <summary>这张图像上的绑定列表；null / 空 = 没有绑定。</summary>
    public ManagedSparseMemoryBind[]? Binds;
}

/// <summary>
/// VkSparseImageMemoryBind 的包装类版：把图像某个子资源的某个 3D 区域绑到一块内存。
/// Memory 为 null 表示解绑这个区域。
/// </summary>
public struct ManagedSparseImageMemoryBind
{
    /// <summary>子资源（aspect / mip / array layer）。</summary>
    public ImageSubresource Subresource;

    /// <summary>区域在图像里的起点（对稀疏图像按 tile 对齐）。</summary>
    public Offset3D Offset;

    /// <summary>区域大小。</summary>
    public Extent3D Extent;

    /// <summary>绑到哪块内存；null = 解绑这个区域。</summary>
    public VKDeviceMemory? Memory;

    /// <summary>在内存里的起始偏移。</summary>
    public ulong MemoryOffset;

    /// <summary>绑定标志（目前只有 None）。</summary>
    public SparseMemoryBindFlags Flags;
}

/// <summary>VkSparseImageMemoryBindInfo 的包装类版：一张图像 + 它的若干条区域绑定。</summary>
public struct ManagedSparseImageMemoryBindInfo
{
    /// <summary>要绑的图像（必填）。</summary>
    public VKImage? Image;

    /// <summary>这张图像上的区域绑定列表；null / 空 = 没有绑定。</summary>
    public ManagedSparseImageMemoryBind[]? Binds;
}
