using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 帧缓冲（教程《Framebuffers》章节）的包装。
/// 帧缓冲 = 渲染通道 + 它引用到的那些附件的**具体图像视图**：渲染通道里写的是"第 0 号附件用
/// ColorAttachmentOptimal 这个布局"，帧缓冲才把"第 0 号附件到底是哪张视图"定下来 ——
/// 两者必须相容（附件数量、格式、采样数都对得上）。
/// 所以交换链里每张图像都要配一个帧缓冲：同一时刻可能有好几帧在飞，各自画自己的那张。
///
/// 归属：vkCreateFramebuffer / vkDestroyFramebuffer 都是设备级调用，所以父对象是 VKLogicalDevice
///（和 VKCommandPool / VKRenderPass 一样：创建时登记到父对象集合里，Valid 靠反查）。
/// 注意帧缓冲和渲染通道 / 图像视图之间是**引用**关系（它并不拥有它们，也不负责它们的生死），
/// 所以这里额外存了 VKRenderPass 和 Attachments 两份引用；销毁顺序由调用方负责 ——
/// Vulkan 要求帧缓冲先于它的渲染通道和附件视图被销毁。
///
/// 划界线：vkCmdBeginRenderPass 要用帧缓冲作为参数，那是**记录到命令缓冲里**的调用，
/// 属于命令缓冲那一层（入口是 VKCommandBuffer.BeginRenderPass）；本类只做"记住创建时定下来的那套参数、校验句柄归属、销毁"。
/// 句柄照样不往外漏 —— 录制那一层要用到时，由本类做 internal 入口把 CheckedHandle 当参数往下传。
/// </summary>
public partial class VKFramebuffer
{
    public readonly VKRenderPass Parent;

    /// <summary>
    /// 附件视图。**顺序和数量必须和渲染通道里附件描述的顺序、数量一一对应**（教程特意强调，
    /// 对不上会有校验错误）。渲染通道本身没有附件的场合，这里就是空集合 ——
    /// Vulkan 允许 attachmentCount = 0 的帧缓冲，所以不当作错误。
    /// </summary>
    public readonly ReadOnlyCollection<VKImageView> Attachments;

    /// <summary>帧缓冲的宽 —— 必须能盖住渲染区域（教程用的是交换链图像的分辨率）。</summary>
    public readonly uint Width;

    /// <summary>帧缓冲的高（同上）。</summary>
    public readonly uint Height;

    /// <summary>层数：教程说交换链相关的东西都是单层的，除非做立体 3D 才需要更多层。</summary>
    public readonly uint Layers;

    /// <summary>创建时的 flags。目前 Vulkan 里没有用得上的标志位，留着对应 create-info 的字段。</summary>
    public readonly FramebufferCreateFlags Flags;

    private readonly Framebuffer handle;

    private readonly static Vk VK = Vk.GetApi();

    internal VKFramebuffer(
        VKRenderPass parent,
        Framebuffer handle,
        VKImageView[] attachments,
        uint width,
        uint height,
        uint layers,
        FramebufferCreateFlags flags)
    {
        Parent = parent;
        this.handle = handle;
        Attachments = new ReadOnlyCollection<VKImageView>(attachments);
        Width = width;
        Height = height;
        Layers = layers;
        Flags = flags;
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 VKLogicalDevice.CreateFramebuffer 里）。
    /// </summary>
    public bool Valid => Parent.IsFramebufferOfThis(this) && Attachments.All(x => x.Valid);

    private Framebuffer CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The framebuffer is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：和别的包装类一样，把"从父对象集合里摘掉 + 调 vkDestroyFramebuffer"交给父对象做。
    /// 注意顺序：渲染通道和附件图像视图都还被本对象引用着，得先销毁帧缓冲。
    /// </summary>
    public void Destroy()
        => Parent.DestroyFramebuffer(this, CheckedHandle);

    /// <summary>参数是裸句柄（Framebuffer），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(Framebuffer handleForCheck)
        => handleForCheck.Handle == handle.Handle;

}

public partial class VKRenderPass
{
    
    

    // 从这个逻辑设备上建出来的帧缓冲。写法和上面一样：创建时登记进来，
    // 之后靠 IsFramebufferOfThis() 反查归属（VKFramebuffer.Valid 就是这么来的）。
    private readonly ConcurrentDictionary<VKFramebuffer, AllocationCallbacks?> framebuffers = new ();
    /// <summary>
    /// 教程 createFramebuffers() 里的 vkCreateFramebuffer。
    /// 帧缓冲把"渲染通道里的第 0 号附件"坐实到"具体哪张图像视图"上，所以参数就是：
    ///   renderPass  —— 要配合的渲染通道；
    ///   attachments —— 附件视图，**顺序和数量必须和渲染通道里附件描述的一一对应**
    ///                 （教程特意强调，对不上会有校验错误）；空数组是合法的（渲染通道本来就没附件）；
    ///   width / height / layers —— 帧缓冲尺寸和层数（教程用交换链的 swapchainExtent + 1 层）。
    ///
    /// 句柄怎么凑齐（本仓库的访问性约定）：**任何对象都不把自己的句柄交出去**，也不提供"交出句柄"的入口 ——
    ///   · Device 句柄由本类自己持有，作为"授权参数"主动往下传（请求方传自己的句柄）；
    ///   · RenderPass 句柄由 VKRenderPass 自己拿着往下传，也只是当参数（不返回、不外泄）；
    ///   · ImageView 句柄由 VKImageView 里那个 **static** 方法自己读 —— 同一个类的代码可以访问任意实例的
    ///     private 字段，所以不需要任何转交接口，也不需要登记表（"谁的句柄类型，谁就在自己的类里读写"）。
    /// 整条链最后返回的是"操作结果"（新建的 Framebuffer 句柄），由本类包装 + 登记；
    /// 分工与 VKSurface.CreateSwapchain 一致：请求方传入设备句柄，被请求方执行 Vulkan 调用并返回结果。
    ///
    /// 照本仓库惯例：建出来 → 登记到本设备的集合里（VKFramebuffer.Valid 就是反查这里）→ 返回包装类。
    /// </summary>
    public VKFramebuffer CreateFramebuffer(
        VKImageView[] attachments,
        uint width,
        uint height,
        uint layers = 1,
        FramebufferCreateFlags flags = FramebufferCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Framebuffer resultHandle = Parent.CreateFramebuffer(this, CheckedHandle, attachments, width, height, layers, flags, allocationCallbacks);
        VKFramebuffer result = new(this, resultHandle, attachments, width, height, layers, flags);
        framebuffers[result] = allocationCallbacks;
        return result;
    }

    /// <summary>
    /// vkDestroyFramebuffer（写法同 DestroyRenderPass / DestroyImage）。
    /// 注意本类只是执行销毁，具体顺序（帧缓冲必须先于它的渲染通道 / 附件视图）由调用方负责。
    /// </summary>
    internal bool DestroyFramebuffer(VKFramebuffer framebuffer, Framebuffer handle)
    {

        if (!framebuffer.IsHandleOfThis(handle) || !framebuffers.TryRemove(framebuffer, out var allocationCallbacks))
        {
            return false;
        }
        Parent.DestroyFramebuffer(framebuffer, handle, allocationCallbacks);
        return true;
    }

    /// <summary>帧缓冲的归属校验：本设备建出来的才算 —— VKFramebuffer.Valid 就是这么来的。</summary>
    public bool IsFramebufferOfThis(VKFramebuffer framebuffer)
        => framebuffers.ContainsKey(framebuffer) && Valid;

}

public partial class VKLogicalDevice
{
    #region 帧缓冲（教程《Framebuffers》章节）


    /// <summary>
    /// 教程 createFramebuffers() 里的 vkCreateFramebuffer —— 本类处理来自 VKLogicalDevice 的帧缓冲创建请求：
    /// **用自己私有的 RenderPass 句柄执行，并把创建出来的 Framebuffer 句柄返回**
    ///（同 VKSurface.CreateSwapchain 处理交换链创建请求：请求方传入自己持有的设备句柄，被请求方用私有句柄执行 + 返回新建的句柄）。
    /// 本类不把自己的句柄交出去，只交出"操作结果"；外面要建帧缓冲就直接调 VKLogicalDevice.CreateFramebuffer。
    ///
    /// 授权方式同 VKSurface.CreateSwapchain：调用方（VKLogicalDevice）**主动把自己持有的逻辑设备句柄传进来**，
    /// 传入正确参数就等于授权；本类用 IsHandleOfThis 校验，对了才拿它去调 Vulkan。
    ///
    /// 附件句柄（ImageView[]）也由调用方传进来，但**不是**"某个对象交出自己的句柄"：
    /// 句柄本来就是 VKLogicalDevice 建的，它手里有一份登记表（imageViewHandles），自己查出来再传。
    /// 附件顺序 / 数量必须和本渲染通道里附件描述的一一对应（教程特意强调，对不上会有校验错误）；
    /// 长度为 0（渲染通道没有附件）是合法的，此时 pAttachments 为 null。
    ///
    /// 之所以只能是 internal：入参 / 返回值里带的是裸句柄，按访问性约定不能出包装层。
    /// </summary>
    internal Framebuffer CreateFramebuffer(
        VKRenderPass renderPass,
        RenderPass renderPassHandle,
        VKImageView[] attachments,
        uint width,
        uint height,
        uint layers,
        FramebufferCreateFlags flags,
        AllocationCallbacks? allocationCallbacks)
        => VKImageView.CreateFramebuffer(this, CheckedHandle, renderPass, renderPassHandle, attachments, width, height, layers, flags, allocationCallbacks);


    /// <summary>
    /// vkDestroyFramebuffer（写法同 DestroyRenderPass / DestroyImage）。
    /// 注意本类只是执行销毁，具体顺序（帧缓冲必须先于它的渲染通道 / 附件视图）由调用方负责。
    /// </summary>
    internal unsafe bool DestroyFramebuffer(VKFramebuffer framebuffer, Framebuffer handle, AllocationCallbacks? allocationCallbacks)
    {
        Device device = CheckedHandle;
        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyFramebuffer(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyFramebuffer(device, handle, null);
        }
        return true;
    }

    #endregion
}

public partial class VKImageView
{
    internal static unsafe Framebuffer CreateFramebuffer(
        VKLogicalDevice logicalDevice,
        Device logicalDeviceHandle,
        VKRenderPass renderPass,
        RenderPass renderPassHandle,
        VKImageView[] attachments,
        uint width,
        uint height,
        uint layers,
        FramebufferCreateFlags flags,
        AllocationCallbacks? allocationCallbacks)
    {
        if(!logicalDevice.IsHandleOfThis(logicalDeviceHandle))
        {
            throw new ArgumentException("The provided logical device handle is not from logical device.");
        }
        if(!renderPass.IsHandleOfThis(renderPassHandle))
        {
            throw new ArgumentException("The provided render pass handle is not from render pass.");
        }
        if(!logicalDevice.IsRenderPassOfThis(renderPass))
        {
            throw new ArgumentException("The provided render pass is not associated with logical device.");
        }
        if(renderPass.Attachments.Count != attachments.Length)
        {
            throw new ArgumentException("The number of attachments does not match the render pass.");
        }
        var pAttachments = stackalloc ImageView[attachments.Length];
        for(int i = 0; i < attachments.Length; i++)
        {
            VKImageView imageView = attachments[i];
            if(imageView.Parent.Parent.Parent != logicalDevice)
            {
                throw new ArgumentException("The provided image view is not associated with logical device.");
            }
            AttachmentDescription description = renderPass.Attachments[i];
            if(description.Format != imageView.Format)
            {
                throw new ArgumentException("The format of the image view does not match the render pass attachment.");
            }
            pAttachments[i] = imageView.CheckedHandle;
        }

        FramebufferCreateInfo framebufferInfo = new(
            flags: flags,
            renderPass: renderPassHandle,
            attachmentCount: (uint)attachments.Length,
            pAttachments: pAttachments,
            width: width,
            height: height,
            layers: layers);

        Framebuffer handle;
        if (allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            if (VK.CreateFramebuffer(logicalDeviceHandle, in framebufferInfo, &dAllocationCallbacks, out handle) != Result.Success)
            {
                throw new NotSupportedException("failed to create framebuffer!");
            }
        }
        else if (VK.CreateFramebuffer(logicalDeviceHandle, in framebufferInfo, null, out handle) != Result.Success)
        {
            throw new NotSupportedException("failed to create framebuffer!");
        }
        return handle;
    }
}