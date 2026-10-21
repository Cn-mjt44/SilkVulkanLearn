using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 图像视图（教程《Image views》章节）的包装。
/// 图像（VKImage）只是"给 GPU 用的一块数据"，驱动并不知道该**怎么解读**它：
/// 视图就是这份解读 —— 当成二维图还是立方体贴图（ViewType）、按什么格式看（Format）、
/// 看到哪几层 / 哪几级 mip（SubresourceRange）、四个通道要不要重排（Components）。
/// 教程里两处用到它：交换链的每张图像都要建一个（渲染通道才能把它当颜色附件用），
/// 以及从文件加载的纹理图像要建一个（着色器里才能采样）。
///
/// 归属：本类的父对象是 VKImage —— 视图是"这张图像的视图"，离开这张图像没有任何意义，
/// 也不能比它活得久（所以 VKImage.Destroy() 会先把本图像身上的视图都销毁掉）。
///
/// vkCreateImageView / vkDestroyImageView 本身是**设备级**调用，而本类读不到 Device 句柄
///（也不该读：访问性约定是句柄只在包装层内部流转，谁持着句柄谁做实际操作）。
/// 所以真正的 Vulkan 调用在 VKLogicalDevice.CreateImageView / DestroyImageView 里：
///   VKImage.CreateImageView(参数) → 把自己的图像句柄交给 VKLogicalDevice →
///   那边校验 + vkCreateImageView + **返回建出来的 ImageView 句柄** →
///   VKImage 再包装成本类并登记（VKImageView.Valid 就是反查这张图像登记过没有）。
/// 也就是说：从外面建视图，一个裸句柄都不用拿。
/// </summary>
public partial class VKImageView
{
    /// <summary>父对象：这张视图是"谁的视图"。销毁、归属校验都走它。</summary>
    public readonly VKImage Parent;

    /// <summary>
    /// 视图类型：Type2D（二维纹理 / 交换链图像，教程只用这个）/ Type3D / Cube / Type2DArray ……
    /// 必须和图像本身的 ImageType 对得上（二维图像才能建 Type2D 视图）。
    /// </summary>
    public readonly ImageViewType ViewType;

    /// <summary>视图格式：通常就是图像自己的格式（教程里直接用创建图像时那个 textureImageFormat）。</summary>
    public readonly Format Format;

    /// <summary>通道重排：教程用的是 Identity（各通道原样，就是默认的全零值），一般不用改。</summary>
    public readonly ComponentMapping Components;

    /// <summary>看图像的哪一部分：aspect（颜色 / 深度 / 模板）、mip 区间、层区间。</summary>
    public readonly ImageSubresourceRange SubresourceRange;

    private readonly ImageView handle;
    private readonly static Vk VK = Vk.GetApi();

    internal VKImageView(
        VKImage parent,
        ImageView handle,
        ImageViewType viewType,
        Format format,
        ComponentMapping components,
        ImageSubresourceRange subresourceRange)
    {
        Parent = parent;
        this.handle = handle;
        ViewType = viewType;
        Format = format;
        Components = components;
        SubresourceRange = subresourceRange;
    }

    /// <summary>
    /// 和 VKImage / VKSurface 一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 VKImage.CreateImageView 里）。
    /// </summary>
    public bool Valid => Parent.IsImageViewOfThis(this);

    private ImageView CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The image view is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：和别的包装类一样，把"从父对象集合里摘掉 + 调 vkDestroyImageView"交给父对象做
    ///（父对象再把实际的设备调用和句柄转给 VKLogicalDevice）。
    /// </summary>
    public void Destroy()
        => Parent.DestroyImageView(this, CheckedHandle);

    /// <summary>参数是裸句柄（ImageView），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(ImageView handleForCheck)
        => handleForCheck.Handle == handle.Handle;

}

public partial class VKImage
{
    

    // 这张图像身上建出来的视图（写法和 VKLogicalDevice 里那些集合一样）：CreateImageView 时登记进来，
    // 之后靠 IsImageViewOfThis() 反查归属（VKImageView.Valid 就是这么来的 —— 视图离开了图像没有意义，
    // 所以登记表放在本类而不是 VKLogicalDevice 上）；顺便把 allocationCallbacks 记下来留给销毁用。
    private readonly ConcurrentDictionary<VKImageView, AllocationCallbacks?> imageViews = new ();
    
    /// <summary>
    /// 教程 createImageViews() 里那条：给图像建视图。交换链每张图像的视图、以及纹理图像的视图，
    /// 都是走这个入口建的（教程分了两处写，其实一模一样的调用，只是参数不同）。
    ///
    /// 参数对应教程 VkImageViewCreateInfo 的字段，默认值就是教程"交换链图像视图"那套
    ///（Type2D / 用图像自己的格式 / 1 级 mip / 1 层 / Identity 通道重排）：
    ///   aspectMask     —— 这是**必填**的：颜色图像用 ColorBit；深度用 DepthBit，模板用 StencilBit，
    ///                     深度+模板就两个按位或（教程的纹理 / 交换链都是 ColorBit）；
    ///   viewType       —— Type2D（二维图像 / 交换链）；立方体贴图用 Cube，数组纹理用 Type2DArray……
    ///   format         —— 不传就是图像自己的格式（教程也是直接用 textureImageFormat，没另选一个）；
    ///   baseMipLevel / baseArrayLayer —— 从哪一级 mip / 哪一层开始看，一般从 0 开始；
    ///   levelCount / layerCount —— 看几级 / 几层，**不传就是"从 base 开始把剩下的都看了"**
    ///                     （教程给纹理建视图时就是 levelCount = mipLevels、layerCount = 1）；
    ///   components     —— 通道重排，默认 Identity（全零值就是不重排）；
    ///   flags          —— 目前没有用得上的标志位，留空。
    ///
    /// 调用方不用拿任何裸句柄：真正的 vkCreateImageView 在 VKLogicalDevice（Device）里做
    ///（只有它持着 Device 句柄），本类把自己的 CheckedHandle 一起交出去；
    /// 那边把建出来的 ImageView 句柄还回来，本类再包装 + 登记（同 VKSurface.CreateSwapchain 的分工）。
    /// </summary>
    public VKImageView CreateImageView(
        ImageAspectFlags aspectMask,
        ImageViewType viewType = ImageViewType.Type2D,
        Format? format = null,
        uint baseMipLevel = 0,
        uint? levelCount = null,
        uint baseArrayLayer = 0,
        uint? layerCount = null,
        ComponentMapping components = default,
        ImageViewCreateFlags flags = ImageViewCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Image imageHandle = CheckedHandle;

        // 不传 format 就跟着图像本身的格式走（教程给纹理建视图时也是直接用 textureImageFormat）。
        Format imageViewFormat = format ?? Format;
        ImageSubresourceRange subresourceRange = new(
            aspectMask: aspectMask,
            baseMipLevel: baseMipLevel,
            levelCount: levelCount ?? MipLevels - baseMipLevel,
            baseArrayLayer: baseArrayLayer,
            layerCount: layerCount ?? ArrayLayers - baseArrayLayer);

        ImageView viewHandle = Parent.Parent.CreateImageView(
            this,
            imageHandle,
            viewType,
            imageViewFormat,
            components,
            subresourceRange,
            flags,
            allocationCallbacks);

        VKImageView result = new (this, viewHandle, viewType, imageViewFormat, components, subresourceRange);
        imageViews[result] = allocationCallbacks;
        return result;
    }

    /// <summary>
    /// 本图像身上有没有登记过这个视图 —— VKImageView.Valid 靠它。
    ///（视图的父对象是 VKImage，所以只查这里；交换链图像也能建视图，走的是同一张表。）
    /// </summary>
    public bool IsImageViewOfThis(VKImageView imageView)
        => imageViews.ContainsKey(imageView) && Valid;

    /// <summary>
    /// vkDestroyImageView：登记表在本类手里（视图是"这张图像的视图"），所以先校验 + 摘登记，
    /// 再把"调 vkDestroyImageView"转给父对象（它才持着 Device 句柄），
    /// allocationCallbacks 是创建时记下来的那一份（同 VKLogicalDevice.DestroyImage）。
    /// </summary>
    internal void DestroyImageView(VKImageView imageView, ImageView handle)
    {
        if (!imageView.IsHandleOfThis(handle) || !imageViews.TryRemove(imageView, out var allocationCallbacks))
        {
            return;
        }

        Parent.Parent.DestroyImageView(imageView, handle, allocationCallbacks);
    }
}

public partial class VKLogicalDevice
{
    
    #region 图像视图（教程《Image views》章节）

    /// <summary>
    /// 教程 createImageViews() 里的 vkCreateImageView。
    ///
    /// 为什么具体操作写在这里：它需要 Device 句柄，而本类才是"持着 Device 的那一方"
    ///（同 DestroyImage / Swapchain_AcquireNextImage 的分工）。视图的父对象是 VKImage，
    /// 所以调用方（VKImage.CreateImageView）把自己的**图像裸句柄**一起传进来，
    /// 本类先校验"确实是本设备上登记的这张图像"，再做实际创建，
    /// 最后把建出来的 ImageView 句柄原样还回去 —— 包装成 VKImageView、以及登记
    ///（VKImageView.Valid 的数据来源）都由 VKImage 做，因为只有它知道视图挂在哪张图像上。
    /// 参数含义见 VKImage.CreateImageView 的注释。
    /// </summary>
    internal unsafe ImageView CreateImageView(
        VKImage image,
        Image imageHandle,
        ImageViewType viewType,
        Format format,
        ComponentMapping components,
        ImageSubresourceRange subresourceRange,
        ImageViewCreateFlags flags = ImageViewCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Device device = CheckedHandle;

        if (!image.IsHandleOfThis(imageHandle) || !image.Valid)
        {
            throw new ArgumentException(
                "The image view handle is not valid: it is not the image of the parent VKLogicalDevice.");
        }

        // 写法同 CreateImage：用带名参数的构造函数，SType 自动填成 ImageViewCreateInfo。
        // 它带 void* pNext，所以构造函数在元数据里是 unsafe 的 —— 本方法也因此要标 unsafe。
        ImageViewCreateInfo imageViewInfo = new(
            flags: flags,
            image: imageHandle,
            viewType: viewType,
            format: format,
            components: components,
            subresourceRange: subresourceRange);

        ImageView handle;
        if (allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            if (VK.CreateImageView(device, in imageViewInfo, &dAllocationCallbacks, out handle) != Result.Success)
            {
                throw new NotSupportedException("failed to create image view!");
            }
        }
        else if (VK.CreateImageView(device, in imageViewInfo, null, out handle) != Result.Success)
        {
            throw new NotSupportedException("failed to create image view!");
        }

        return handle;
    }

    /// <summary>
    /// vkDestroyImageView：真正的设备调用在这里（本类持着 Device 句柄）。
    /// 登记表在 VKImage 那边，所以走到这一步说明那边已经校验过、并且把创建时记下的
    /// allocationCallbacks 取出来了；这里照惯例再用视图自己的 IsHandleOfThis 复核一次句柄，
    /// 然后才是 VK.DestroyImageView。
    /// </summary>
    internal unsafe bool DestroyImageView(VKImageView imageView, ImageView handle, AllocationCallbacks? allocationCallbacks)
    {
        Device device = CheckedHandle;

        if (!imageView.IsHandleOfThis(handle))
        {
            return false;
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyImageView(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyImageView(device, handle, null);
        }
        return true;
    }

    #endregion

}