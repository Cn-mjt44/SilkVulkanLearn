using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 图像（教程《Images》/《Texture image》章节）的包装 —— **设备内存使用者**之一（继承 VKDeviceMemoryUser）。
/// 图像就是"给 GPU 用的一块数据"：格式、尺寸、层数、怎么排布（tiling）、拿来干什么（usage）…… 全都在创建时定死。
///
/// 图像有两种来源，父对象因此不同（父对象是接口 IVKImageParent，**永远非空**）：
///   ① 我们自己建的（纹理 / 深度缓冲…）—— 必须绑在一块 VKDeviceMemory 上，所以**父对象是那块内存**
///      （VKDeviceMemory 实现 IVKImageParent）；创建入口也在内存上：memory.CreateImage(...)，或
///      VKDeviceMemory.CreateImage(device, ...) 那条一键创建捷径。**没有单独的"绑内存"函数** ——
///      绑定就发生在创建里（同 VKBuffer）。
///   ② 交换链图像（VKSwapchain.Images）—— 驱动给的，存储不由我们管，所以**父对象是那条交换链**
///      （VKSwapchain 实现 IVKImageParent）；Valid 反查它本来就持有的 Images 集合
///（不需要我们登记 —— 生命周期由交换链管）；也**不能单独销毁**
///（Destroy() 会直接抛），跟着交换链一起生灭。
///
/// 划界线：本类只表示"这张图像本身"。VkImageView（"怎么解读这块数据"：二维还是三维、映射到哪个通道）
/// 属于**视图**那一层，因为"视图是这张图像的视图"，入口就在本类：CreateImageView。
/// 视图的登记表也在本类（imageViews），而实际的 vkCreateImageView / vkDestroyImageView 由
/// VKLogicalDevice（Device，它才持着 Device 句柄）执行 —— 见那两个方法的注释。
/// </summary>
public partial class VKImage
{
    /// <summary>图像维度：Type2D 是二维纹理/交换链图像（教程只用这个），另有 Type1D / Type3D。</summary>
    public readonly ImageType ImageType;

    /// <summary>像素格式（教程从文件加载纹理用 R8G8B8A8Srgb；交换链图像一般是 B8G8R8A8Srgb 那类）。</summary>
    public readonly Format Format;

    /// <summary>尺寸：二维图像 depth 写 1（教程原话：二维纹理的第三维没有意义）。</summary>
    public readonly Extent3D Extent;

    /// <summary>mipmap 级数：不生成 mipmap 就是 1。</summary>
    public readonly uint MipLevels;

    /// <summary>层数：普通二维纹理是 1；立方体贴图 / 数组纹理才更多。</summary>
    public readonly uint ArrayLayers;

    /// <summary>采样数：不开 MSAA 就是 Count1Bit。</summary>
    public readonly SampleCountFlags Samples;

    /// <summary>
    /// 排布方式：Optimal（驱动自己排，性能最好，最常用）/ Linear（能直接映射到 CPU 内存，
    /// 但慢，而且能不能用要先问 VKPhysicalDevice.GetImageFormatProperties）。
    /// </summary>
    public readonly ImageTiling Tiling;

    /// <summary>
    /// 用途：**一定要写对**。纹理要 SampledBit（要往里拷数据还得加 TransferDstBit），
    /// 颜色附件要 ColorAttachmentBit，深度/模板附件要 DepthStencilAttachmentBit ——
    /// 它既决定后面这张图像能怎么用，也影响驱动怎么给它排内存。
    /// </summary>
    public readonly ImageUsageFlags Usage;

    /// <summary>共享模式：Exclusive（只被一个队列族用，性能最好）/ Concurrent（跨族，得把族索引都列出来）。</summary>
    public readonly SharingMode ImageSharingMode;

    /// <summary>Concurrent 时列给驱动的那些队列族索引；Exclusive 时是 null。</summary>
    public readonly ReadOnlyCollection<uint>? QueueFamilyIndices;

    /// <summary>
    /// 创建时的初始布局：Undefined 表示"里面没有有效内容"，驱动可以随便挑一个布局 ——
    /// 反正第一次用之前都要自己用 vkCmdPipelineBarrier 转成目标布局。
    /// </summary>
    public readonly ImageLayout InitialLayout;
    public readonly IVKImageParent Parent;

    /// <summary>在这块内存里的起始偏移（一个资源独占一块内存时就是 0）。</summary>
    public readonly ulong Offset;

    private readonly Image handle;

    internal VKImage(
        IVKImageParent parent,
        Image handle,
        ulong offset,
        ImageType imageType,
        Format format,
        Extent3D extent,
        uint mipLevels,
        uint arrayLayers,
        SampleCountFlags samples,
        ImageTiling tiling,
        ImageUsageFlags usage,
        SharingMode imageSharingMode,
        uint[]? queueFamilyIndices,
        ImageLayout initialLayout)
    {
        Parent = parent;
        Offset = offset;
        this.handle = handle;
        ImageType = imageType;
        Format = format;
        Extent = extent;
        MipLevels = mipLevels;
        ArrayLayers = arrayLayers;
        Samples = samples;
        Tiling = tiling;
        Usage = usage;
        ImageSharingMode = imageSharingMode;
        QueueFamilyIndices = queueFamilyIndices != null ? new ReadOnlyCollection<uint>(queueFamilyIndices) : null;
        InitialLayout = initialLayout;
    }

    /// <summary>
    /// 归属校验：由父对象反查 —— 自己建的图像查那块内存的登记表；交换链图像查交换链本来就持有的 Images 集合。
    /// 两种父对象都实现 IVKImageParent，所以这里始终是同一个表达式（Parent 永远非空）。
    /// </summary>
    public bool Valid => Parent.IsImageOfThis(this);

    /// <summary>
    /// 销毁：**只有"我们自己的内存建的"才能手动销毁**；父对象是交换链（运行时所有）时直接抛。
    /// 我们自己的资源先销毁这张图像上的全部视图，再交给父内存做整件事
    ///（父内存取出这个资源创建时用的 ac、把登记摘掉，再让资源去销毁自己的句柄 —— 见 VKDeviceMemory.DestroyImage）。
    /// **注意：父内存不会跟着自动释放** —— 资源都销毁完之后，还得自己调一次 memory.Destroy()。
    /// </summary>
    public void Destroy()
    {
        // 自己建 / 绑了内存的资源：先销毁视图，再由父内存负责"取出 ac → 摘登记 → 让本对象销毁句柄"。
        if (Parent is VKDeviceMemory memory)
        {
            DestroyImageViews();
            memory.DestroyImage(this, CheckedHandle);
            return;
        }
        throw new InvalidOperationException("This instance is own by vulkan driver or system, cannot destroy manually.");
    }

    /// <summary>
    /// 销毁这张图像上的全部视图。交换链图像自己不归我们销毁，但它们的视图必须先于交换链销毁，
    /// 所以 VKSwapchain.Destroy 会通过这个方法清掉这些视图。
    /// </summary>
    internal void DestroyImageViews()
    {
        foreach (var imageView in imageViews.Keys.ToArray())
        {
            try
            {
                imageView.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }
    }

    private Image CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The image is not valid.");
            }

            return handle;
        }
    }

    /// <summary>参数是裸句柄（Image），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(Image handleForCheck)
        => handleForCheck.Handle == handle.Handle;

}

public interface IVKImageParent
{
    bool IsImageOfThis(VKImage image);
    VKLogicalDevice Parent { get; }

}

public partial class VKDeviceMemory : IVKImageParent
{
    
    private readonly ConcurrentDictionary<VKImage, AllocationCallbacks?> images = new ();

    VKLogicalDevice IVKImageParent.Parent => Parent;

    /// <summary>
    /// **内存先行入口（图像）**：先有内存，再在这块内存对象上申请创建一张图像并绑定（同理，绑定就在创建里）。
    /// 参数含义同 VKLogicalDevice 那边：格式 / 尺寸 / 用途 / mip / 层 / 采样 / tiling / 共享 / 初始布局……
    /// （教程的纹理图、深度图都从这里建；同样可以多个资源共用一块内存，用 offset 分段。）
    /// </summary>
    public VKImage CreateImage(
        Format format,
        Extent3D extent,
        ImageUsageFlags usage,
        ulong offset = 0,
        ImageType imageType = ImageType.Type2D,
        uint mipLevels = 1,
        uint arrayLayers = 1,
        SampleCountFlags samples = SampleCountFlags.Count1Bit,
        ImageTiling tiling = ImageTiling.Optimal,
        SharingMode imageSharingMode = SharingMode.Exclusive,
        uint[]? queueFamilyIndices = null,
        ImageLayout initialLayout = ImageLayout.Undefined,
        ImageCreateFlags flags = ImageCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        VKLogicalDevice device = Parent;
        DeviceMemory memoryHandle = CheckedHandle;


        // ① 建资源（设备执行 vkCreateImage）
        Image handle = device.Image_Create(
            format, extent, usage, out MemoryRequirements requirements, imageType, mipLevels, arrayLayers, samples, tiling,
            imageSharingMode, queueFamilyIndices, initialLayout, flags, allocationCallbacks);

        // 先包一层（同上：还没登记，Valid 还是 false）——注意交换链图像才用 parent = null 那条路，
        // 这里是我们自己建的图像，Parent 就是本内存。
        VKImage result = new(
            this, handle, offset,
            imageType, format, extent, mipLevels, arrayLayers, samples, tiling, usage,
            imageSharingMode, queueFamilyIndices, initialLayout);

        // ② 问需求 → ③ 校验放得下
        CheckPlacement(requirements, offset);

        // ④ 先登记（Valid 的数据来源）+ 把 ac 存下来留给销毁用：
        //    下一步的归属校验要求"这张图像已经记在本内存名下"。
        images[result] = allocationCallbacks;

        // ⑤ 绑定；失败就把刚建出来的资源销毁并摘掉登记
        if (device.Image_BindMemory(result, handle, this, memoryHandle, offset) != Result.Success)
        {
            images.TryRemove(result, out _);
            device.Image_Destroy(result, handle, allocationCallbacks);
            throw new NotSupportedException("failed to bind image memory!");
        }

        return result;
    }

    /// <summary>**一键创建捷径（图像）** —— 同上面那个，只是换成图像参数（纹理 / 深度图都适合）。</summary>
    public static VKImage CreateImage(
        VKLogicalDevice device,
        Format format,
        Extent3D extent,
        ImageUsageFlags usage,
        MemoryPropertyFlags properties,
        out VKDeviceMemory memory,
        AllocationCallbacks? allocationCallbacks = null,
        ImageType imageType = ImageType.Type2D,
        uint mipLevels = 1,
        uint arrayLayers = 1,
        SampleCountFlags samples = SampleCountFlags.Count1Bit,
        ImageTiling tiling = ImageTiling.Optimal,
        SharingMode imageSharingMode = SharingMode.Exclusive,
        uint[]? queueFamilyIndices = null,
        ImageLayout initialLayout = ImageLayout.Undefined,
        ImageCreateFlags flags = ImageCreateFlags.None)
    {
        Image handle = device.Image_Create(
            format, extent, usage, out MemoryRequirements requirements, imageType, mipLevels, arrayLayers, samples, tiling,
            imageSharingMode, queueFamilyIndices, initialLayout, flags, allocationCallbacks);

        memory = device.AllocateMemory(requirements, properties, allocationCallbacks);

        VKImage result = new(
            memory, handle, 0,
            imageType, format, extent, mipLevels, arrayLayers, samples, tiling, usage,
            imageSharingMode, queueFamilyIndices, initialLayout);

        // 先登记再绑定：Image_BindMemory 的归属校验要看到这张图像已经记在新内存名下。
        memory.images[result] = allocationCallbacks;

        if (device.Image_BindMemory(result, handle, memory, memory.handle, 0) != Result.Success)
        {
            memory.images.TryRemove(result, out _);
            device.Image_Destroy(result, handle, allocationCallbacks);
            throw new NotSupportedException("failed to bind image memory!");
        }

        return result;
    }



    /// <summary>
    /// 本内存的归属校验：这张图像是不是在这块内存上建的（IVKImageParent.IsImageOfThis 的实现）——
    /// VKImage.Valid 靠它。交换链图像不在这里登记，由 VKSwapchain.IsImageOfThis 回答。
    /// </summary>
    public bool IsImageOfThis(VKImage user)
        => images.ContainsKey(user) && Valid;


    internal bool DestroyImage(VKImage image, Image handle)
    {
        if (
            !image.IsHandleOfThis(handle) ||
            !images.TryRemove(image, out var allocationCallbacks)
        )
        {
            return false;
        }
        
        Parent.Image_Destroy(image, handle, allocationCallbacks);

        return true;
    }
}

public partial class VKSwapchain : IVKImageParent
{

    VKLogicalDevice IVKImageParent.Parent => Parent;


    public bool IsImageOfThis(VKImage user)
        => Images.Contains(user) && Valid;
}

public partial class VKLogicalDevice
{

    #region 图像（教程《Images》/《Texture image》章节）

    /// <summary>
    /// 教程 createTextureImage() 开头那几行：vkCreateImage。
    ///
    /// 真实调用在本类（只有它持着 Device 句柄），但**本方法只负责把图像建出来** ——
    /// 不包装、不登记，因为现在图像的父对象是**那块 VKDeviceMemory**：
    /// 创建入口在内存上（VKDeviceMemory.CreateImage），由那边调本方法拿到句柄，
    /// 再问需求 → 校验 → 绑定 → 包装 → 登记（同 VKLogicalDevice.CreateImageView 的分工）。
    ///
    /// 参数对应教程 VkImageCreateInfo 的字段，含义见 VKDeviceMemory.CreateImage 的注释（参数一模一样）。
    /// 之所以是 internal：外面要走 VKDeviceMemory.CreateImage，句柄不在包装层外流转。
    /// </summary>
    internal unsafe Image Image_Create(
        Format format,
        Extent3D extent,
        ImageUsageFlags usage,
        out MemoryRequirements memoryRequirements,
        ImageType imageType = ImageType.Type2D,
        uint mipLevels = 1,
        uint arrayLayers = 1,
        SampleCountFlags samples = SampleCountFlags.Count1Bit,
        ImageTiling tiling = ImageTiling.Optimal,
        SharingMode imageSharingMode = SharingMode.Exclusive,
        uint[]? queueFamilyIndices = null,
        ImageLayout initialLayout = ImageLayout.Undefined,
        ImageCreateFlags flags = ImageCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Device device = CheckedHandle;

        // 只在创建这一次调用里会用到族索引（同创建交换链），所以用 fixed 把数组钉住；
        // 数组为 null 时 fixed 得到的就是空指针，正好对应 Exclusive 的"不给族索引"。
        uint queueFamilyIndexCount = queueFamilyIndices != null ? (uint)queueFamilyIndices.Length : 0;

        Image handle;
        fixed (uint* pQueueFamilyIndices = queueFamilyIndices)
        {
            ImageCreateInfo imageInfo = new(
                flags: flags,
                imageType: imageType,
                format: format,
                extent: extent,
                mipLevels: mipLevels,
                arrayLayers: arrayLayers,
                samples: samples,
                tiling: tiling,
                usage: usage,
                sharingMode: imageSharingMode,
                queueFamilyIndexCount: queueFamilyIndexCount,
                pQueueFamilyIndices: pQueueFamilyIndices,
                initialLayout: initialLayout);

            if (allocationCallbacks != null)
            {
                AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
                if (VK.CreateImage(device, in imageInfo, &dAllocationCallbacks, out handle) != Result.Success)
                {
                    throw new NotSupportedException("failed to create image!");
                }
            }
            else if (VK.CreateImage(device, in imageInfo, null, out handle) != Result.Success)
            {
                throw new NotSupportedException("failed to create image!");
            }
        }
        memoryRequirements = VK.GetImageMemoryRequirements(device, handle);
        return handle;
    }

    /// <summary>
    /// 教程 vkCreateImage() 收尾的 vkBindImageMemory（和缓冲那条一模一样的分工）：
    /// 图像句柄由 VKImage 自己当参数传下来、内存句柄由 VKDeviceMemory 自己传下来，
    /// 这里各用它们的 IsHandleOfThis 复核一遍 —— 都是"传进来的正确参数"，不是"谁交出来的"。
    /// </summary>
    internal Result Image_BindMemory(
        VKImage image,
        Image imageHandle,
        VKDeviceMemory memory,
        DeviceMemory memoryHandle,
        ulong offset)
    {
        Device device = CheckedHandle;

        if (!image.IsHandleOfThis(imageHandle))
        {
            throw new ArgumentException(
                "The provided image handle is not associated with the given VKImage.");
        }

        if (!memory.IsHandleOfThis(memoryHandle))
        {
            throw new ArgumentException(
                "The provided device memory handle is not associated with the given VKDeviceMemory.");
        }

        if (!memory.IsImageOfThis(image))
        {
            throw new ArgumentException(
                "The provided device memory is not associated with the given VKImage.");
        }

        return VK.BindImageMemory(device, imageHandle, memoryHandle, offset);
    }

    /// <summary>
    /// vkDestroyImage：只管我们自己建的图像（真实调用在本类；"摘登记 + 取出 ac"由
    /// VKDeviceMemory.DestroyUser 负责）。分配回调不在设备这边存 —— 它和登记一起放在**父内存**的
    /// users 字典里，由那边取出来当参数传进来。
    /// </summary>
    internal unsafe void Image_Destroy(VKImage image, Image handle, AllocationCallbacks? allocationCallbacks)
    {
        Device device = CheckedHandle;

        if (!image.IsHandleOfThis(handle))
        {
            throw new ArgumentException(
                "The provided image handle is not associated with the given VKImage.");
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyImage(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyImage(device, handle, null);
        }
    }


    /// <summary>
    /// 图像版的"先问需求，不真建资源"（同上）：1.3 上用 vkGetDeviceImageMemoryRequirements，
    /// 1.3 之前临时建一张图像 → 问 requirements → 马上销毁。
    /// 注意里面的 ImageCreateInfo 字段要和 Image_Create 保持一致（两处是同一次创建的两个阶段）。
    /// </summary>
    public unsafe MemoryRequirements GetImageMemoryRequirements(
        Format format,
        Extent3D extent,
        ImageUsageFlags usage,
        ImageType imageType = ImageType.Type2D,
        uint mipLevels = 1,
        uint arrayLayers = 1,
        SampleCountFlags samples = SampleCountFlags.Count1Bit,
        ImageTiling tiling = ImageTiling.Optimal,
        SharingMode imageSharingMode = SharingMode.Exclusive,
        uint[]? queueFamilyIndices = null,
        ImageLayout initialLayout = ImageLayout.Undefined,
        ImageCreateFlags flags = ImageCreateFlags.None)
    {
        Device device = CheckedHandle;

        if(Parent.Parent.ApiVersion < Vk.Version13)
        {
            Image img = Image_Create(
                format,
                extent,
                usage,
                out MemoryRequirements info,
                imageType,
                mipLevels,
                arrayLayers,
                samples,
                tiling,
                imageSharingMode,
                queueFamilyIndices,
                initialLayout,
                flags,
                null
            );
            VK.DestroyImage(device, img, null);
            return info;
        }

        uint queueFamilyIndexCount = queueFamilyIndices != null ? (uint)queueFamilyIndices.Length : 0;
        fixed (uint* pQueueFamilyIndices = queueFamilyIndices)
        {
            ImageCreateInfo imageInfo = new(
                flags: flags,
                imageType: imageType,
                format: format,
                extent: extent,
                mipLevels: mipLevels,
                arrayLayers: arrayLayers,
                samples: samples,
                tiling: tiling,
                usage: usage,
                sharingMode: imageSharingMode,
                queueFamilyIndexCount: queueFamilyIndexCount,
                pQueueFamilyIndices: pQueueFamilyIndices,
                initialLayout: initialLayout);

            DeviceImageMemoryRequirements info = new(pCreateInfo: &imageInfo);

            return VK.GetDeviceImageMemoryRequirements(device, in info).MemoryRequirements;
        }
    }
    #endregion
}