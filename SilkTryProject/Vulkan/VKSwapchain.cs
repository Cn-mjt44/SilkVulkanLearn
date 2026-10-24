using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;


namespace SilkTry.Vulkan;

/// <summary>
/// 交换链（教程《Swap chain》章节）的包装。
/// 交换链是"一排等着显示到屏幕的图像"：我们渲染其中一张，然后把它交还给呈现队列，下次换另一张，避免屏幕撕裂。
///
/// 归属：vkCreateSwapchainKHR / vkDestroySwapchainKHR 都是设备级调用，所以父对象是 VKLogicalDevice
///（和 VKCommandPool 一样：创建时登记到父对象集合里，Valid 靠反查父对象有没有登记过自己）。
/// 另外交换链和窗口 surface 是绑定的（重建时要复用同一个 surface），所以额外存了一份 VKSurface 引用。
///
/// 划界线：选格式、选呈现模式、算分辨率（教程的 chooseSwapSurfaceFormat / chooseSwapPresentMode /
/// chooseSwapExtent）都是**调用方的策略**，不在这层里 —— 本类只做四件事：
/// 记住创建时定下来的那套参数、校验句柄归属、销毁、以及三条真正属于交换链的操作
///（取图像 / acquire / present）。
/// </summary>
public partial class VKSwapchain
{

    public readonly VKLogicalDevice Parent;
    /// <summary>创建它时用的窗口 surface。教程《Swap chain recreation》重建时要用回同一个。</summary>
    public readonly VKSurface Surface;

    /// <summary>
    /// 创建时定下来的图像格式（Format + ColorSpace，Vulkan 里就是一对 SurfaceFormatKHR）。
    /// 教程特意提醒：渲染通道、ImageView 都要用这个值，不要再回去问当时那个 VkSwapchainCreateInfoKHR。
    /// </summary>
    public readonly SurfaceFormatKHR ImageFormat;

    /// <summary>创建时定下来的呈现模式（教程优先 MAILBOX，没有才退回一定支持的 FIFO）。</summary>
    public readonly PresentModeKHR PresentMode;

    /// <summary>创建时定下来的分辨率（教程 chooseSwapExtent() 算出来的那个）。</summary>
    public readonly Extent2D ImageExtent;

    public readonly uint ImageArrayLayers;
    /// <summary>创建时要求的"最少图像数"。实际拿到几张看 ImageCount —— 驱动可能给得比它多。</summary>
    public readonly uint MinImageCount;

    /// <summary>图像用途（默认 ColorAttachmentBit：交换链图像当颜色附件用）。</summary>
    public readonly ImageUsageFlags ImageUsage;

    /// <summary>
    /// 队列族共享模式：Exclusive（图形族和呈现族是同一个：只有一边访问，性能最好）
    /// 或 Concurrent（两个族都会访问，所以必须把两个族索引都列出来，见 QueueFamilyIndices）。
    /// </summary>
    public readonly SharingMode ImageSharingMode;

    /// <summary>Concurrent 时要一起列给驱动的队列族索引；Exclusive 时是空集合。</summary>
    public readonly ReadOnlyCollection<uint>? QueueFamilyIndices;

    /// <summary>创建时用的 preTransform（不传的话就是 surface 的 currentTransform —— 教程说这是唯一安全的选择）。</summary>
    public readonly SurfaceTransformFlagsKHR PreTransform;

    /// <summary>创建时用的 compositeAlpha（默认 OpaqueBitKhr：让窗口系统忽略被遮住的像素）。</summary>
    public readonly CompositeAlphaFlagsKHR CompositeAlpha;

    /// <summary>创建时用的 clipped（默认 true：不关心被遮住那部分像素的内容）。</summary>
    public readonly bool Clipped;

    public readonly ReadOnlyCollection<VKImage> Images;

    public readonly SwapchainKHR handle;
    private readonly static Vk VK = Vk.GetApi();

    internal VKSwapchain(
        VKLogicalDevice parent,
        VKSurface surface,
        SwapchainKHR handle,
        SurfaceFormatKHR imageFormat,
        PresentModeKHR presentMode,
        Extent2D imageExtent,
        uint imageArrayLayers,
        uint minImageCount,
        ImageUsageFlags imageUsage,
        SharingMode imageSharingMode,
        uint[]? queueFamilyIndices,
        SurfaceTransformFlagsKHR preTransform,
        CompositeAlphaFlagsKHR compositeAlpha,
        bool clipped,
        Image[] images)
    {
        Parent = parent;
        Surface = surface;
        this.handle = handle;
        ImageFormat = imageFormat;
        PresentMode = presentMode;
        ImageExtent = imageExtent;
        ImageArrayLayers = imageArrayLayers;
        MinImageCount = minImageCount;
        ImageUsage = imageUsage;
        ImageSharingMode = imageSharingMode;
        QueueFamilyIndices = queueFamilyIndices != null ? new ReadOnlyCollection<uint>(queueFamilyIndices) : null;
        PreTransform = preTransform;
        CompositeAlpha = compositeAlpha;
        Clipped = clipped;
        VKImage[] imageInstances = new VKImage[images.Length];
        for (int i = 0; i < images.Length; i++)
        {
            // 这些图像不是我们建的，但它们的参数就是交换链创建时定下来的那套（格式/尺寸/用途/共享模式……），
            // 所以照样填进包装类，用起来和 CreateImage 建出来的图像一致 —— 区别只是不能自己销毁。
            // 按规范：交换链图像的 mipLevels 和 samples 都是 1、tiling 是 Optimal、初始布局是 Undefined。
            // 交换链图像：存储是驱动管的，但父对象仍然记成这条交换链（VKImage.Valid 反查它的 Images 集合）；
            // offset 没有意义（给 0）。
            VKImage image = new VKImage(
                this,
                images[i],
                0,
                ImageType.Type2D,
                imageFormat.Format,
                new Extent3D { Width = imageExtent.Width, Height = imageExtent.Height, Depth = 1 },
                mipLevels: 1,
                arrayLayers: imageArrayLayers,
                samples: SampleCountFlags.Count1Bit,
                tiling: ImageTiling.Optimal,
                usage: imageUsage,
                imageSharingMode: imageSharingMode,
                queueFamilyIndices: queueFamilyIndices,
                initialLayout: ImageLayout.Undefined);

            imageInstances[i] = image;
        }
        Images = new ReadOnlyCollection<VKImage>(imageInstances);
    }



    public bool Valid => Parent.IsSwapchainOfThis(this) && Surface.Valid;

    private SwapchainKHR CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The swapchain is not valid.");
            }
            return handle;
        }
    }

    /// <summary>
    /// 销毁：先销毁交换链图像上建出来的全部视图，再把"从父对象集合里摘掉 + 调 vkDestroySwapchainKHR"
    /// 交给父对象做。交换链图像本身不用（也不能）自己销毁，它们跟着交换链一起没。
    /// </summary>
    public void Destroy()
    {
        foreach (var image in Images)
        {
            try
            {
                image.DestroyImageViews();
            }
            catch
            {
                // 忽略单张图像的视图销毁失败，保证其余视图继续销毁。
            }
        }

        Parent.DestroySwapchain(this, handle);
    }

    /// <summary>参数是裸句柄（SwapchainKHR），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(SwapchainKHR handleForCheck)
        => handleForCheck.Handle == handle.Handle;

    /// <summary>
    /// 教程 drawFrame() 的 vkAcquireNextImageKHR：问交换链要一张可用的图像。
    /// 拿到的 imageIndex 用来索引 Images 以及那些"按图像分配"的资源。
    ///
    /// Result 原样交给调用方，因为接下来怎么办是策略，本类不替调用方决定：
    ///   ErrorOutOfDateKhr —— 交换链过期（一般是窗口尺寸变了）：教程《Swap chain recreation》要重建交换链再重试；
    ///   SuboptimalKhr     —— 图像能显示但不是最优：教程说可以照常用（当前 Game 里就是当正常情况继续走）。
    /// timeout 用 ulong.MaxValue 就是教程里的 UINT64_MAX（一直等到有图像为止）。
    ///
    /// signalFence 是**可选**的（vkAcquireNextImageKHR 允许"只给信号量、不给栅栏"，教程就是这么用的）：
    /// 传 null 时按 VK_NULL_HANDLE 交给原生调用；要给的话，它也会在图像可用时被置位。
    ///
    /// 访问性约定：设备句柄由调用方传进来（本类读不到 VKLogicalDevice 的私有句柄），
    /// 用父对象的 IsHandleOfThis 校验"确实是这个逻辑设备"。
    /// </summary>
    public Result AcquireNextImage(ulong timeout, VKSemaphore imageAvailableSemaphore, VKFence? signalFence, out uint imageIndex)
        => imageAvailableSemaphore.Swapchain_AcquireNextImage(this, CheckedHandle, timeout, signalFence, out imageIndex);

    /// <summary>
    /// vkAcquireNextImage2KHR：vkAcquireNextImageKHR 的**设备组**版本，语义完全一样，
    /// 只是参数被塞进了 VkAcquireNextImageInfoKHR，并且多了一个 deviceMask ——
    /// "这次拿到的图像归设备组里哪几个设备管"的位掩码；
    /// 没开设备组（单 GPU）时就是 bit 0，也就是 1（默认值就是这么给的）。
    /// 返回值和 AcquireNextImage 一样原样交给调用方（ErrorOutOfDateKhr / SuboptimalKhr 都是策略问题）。
    /// </summary>
    public Result AcquireNextImage2(ulong timeout, VKSemaphore imageAvailableSemaphore, VKFence? signalFence, out uint imageIndex, uint deviceMask = 1)
        => imageAvailableSemaphore.Swapchain_AcquireNextImage2(this, CheckedHandle, timeout, signalFence, deviceMask, out imageIndex);

    /// <summary>
    /// 教程 drawFrame() 的 vkQueuePresentKHR：把画好的图像交回交换链显示。
    /// 这是队列级的调用，而且必须用"呈现队列"（它可能和图形队列不是同一个），所以队列由调用方传进来 ——
    /// 必须是同一个 VKLogicalDevice（Parent）下的队列 —— 队列句柄同样由调用方传进来，这里用 IsHandleOfThis 校验。
    /// 只用一条等待信号量（renderFinished）和一张图像，够本仓库用；教程也说过 pResults 只有多张交换链时才需要。
    /// 返回值同样原样交给调用方：QueuePresent 也可能返回 ErrorOutOfDateKhr / SuboptimalKhr。
    /// </summary>
    public Result Present(VKDeviceQueue presentQueue, uint imageIndex, VKSemaphore waitSemaphore)
        => presentQueue.Swapchain_Present(this, CheckedHandle, imageIndex, waitSemaphore);
}

public partial class VKLogicalDevice
{
    
    #region 交换链（教程《Swap chain》章节）

    // 从这个逻辑设备上建出来的交换链。写法和 commandPools 一样：创建时登记进来，
    // 之后靠 IsSwapchainOfThis() 反查归属（VKSwapchain.Valid 就是这么来的）。
    private readonly ConcurrentDictionary<VKSwapchain, AllocationCallbacks?> swapchains = new ();
    
    /// <summary>
    /// 教程 createSwapchain()：vkCreateSwapchainKHR。
    ///
    /// 和教程一样，这里只管"照给过来的参数把交换链建出来"，不替调用方做那三个选择
    ///（教程的 chooseSwapSurfaceFormat / chooseSwapPresentMode / chooseSwapExtent）——
    /// 怎么选是看 VKPhysicalDevice.QuerySwapchainSupport() 的结果做决策，属于调用方的策略。
    ///
    /// 参数对应教程 VkSwapchainCreateInfoKHR 的字段：
    ///   surface        —— 要往哪个窗口 surface 呈现（得先确认这个物理设备支持它：
    ///                     VKPhysicalDevice.QuerySwapchainSupport().IsAdequate / GetSurfaceSupport()）；
    ///   imageFormat    —— format + colorSpace（Vulkan 里就是一个 SurfaceFormatKHR，成对出现）；
    ///   presentMode    —— 呈现模式（教程优先 MAILBOX，没有就退回一定支持的 FIFO）；
    ///   imageExtent    —— 分辨率（先看 capabilities.CurrentExtent，是 0xFFFFFFFF 才自己算，
    ///                     并且要夹在 min/maxImageExtent 之间）；
    ///   minImageCount  —— 最少几张图像：教程建议 MinImageCount + 1（给驱动留喘息空间），
    ///                     但 MaxImageCount 不为 0 时不能超；
    ///   其余参数都有默认值，含义见下面各处注释。
    ///
    /// 访问性约定：需要别的包装类的句柄时，由调用方把句柄一起传进来，这里用 IsHandleOfThis 验证"确实是它"
    ///（surfaceHandle 是 surface 的、oldSwapchainHandle 是老交换链的）—— 这样句柄始终是 private，
    /// 不需要把 CheckedHandle 放宽成 internal。
    ///
    /// 创建完立刻把交换链图像一起取出来（vkGetSwapchainImagesKHR）—— 教程就是这么排的，
    /// 而且图像数量要等创建完才知道（驱动可能给得比 minImageCount 多），后面建 ImageView / 帧缓冲都要用。
    /// </summary>
    public unsafe VKSwapchain TryCreateOrUpdateSwapchain(
        VKSurface surface,
        SurfaceFormatKHR imageFormat,
        PresentModeKHR presentMode,
        Extent2D imageExtent,
        uint imageArrayLayers,
        uint minImageCount,
        out VKSwapchain? oldSwapchain,
        ImageUsageFlags imageUsage = ImageUsageFlags.ColorAttachmentBit,
        SharingMode imageSharingMode = SharingMode.Exclusive,
        uint[]? queueFamilyIndices = null,
        SurfaceTransformFlagsKHR? preTransform = null,
        CompositeAlphaFlagsKHR compositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
        bool clipped = true,
        AllocationCallbacks? allocationCallbacks = null)
    {
        // 按本仓库约定：先校验有效性，再建对象
        Device device = CheckedHandle;

        if (!TryGetDeviceExtension(out KhrSwapchain khrSwapchain))
        {
            throw new NotSupportedException("KHR_swapchain extension not found.");
        }
        oldSwapchain = null;
        SwapchainKHR handle = surface.CreateSwapchain(
            this, device, imageFormat, presentMode, imageExtent,
            imageArrayLayers, minImageCount, imageUsage, imageSharingMode,
            queueFamilyIndices, preTransform ?? Parent.GetSurfaceCapabilities(surface).CurrentTransform,
            compositeAlpha, clipped, allocationCallbacks, out SwapchainKHR? oldHandle);

        if(oldHandle != null)
        {
            oldSwapchain = swapchains.Keys.First(x => x.IsHandleOfThis(oldHandle.Value));
        }
        // 教程的 getSwapchainImagesKHR：数量要等创建完才知道（驱动可以给得比 minImageCount 多），
        // 所以只能在这一步问 —— 真正的调用见 Swapchain_GetSwapchainImages()。
        uint swapchainImageCount = 0;
        khrSwapchain.GetSwapchainImages(device, handle, ref swapchainImageCount, null);

        if (swapchainImageCount == 0)
        {
            throw new NotSupportedException("the swap chain reports zero images!");
        }

        var swapchainImageHandles = new Image[swapchainImageCount];
        fixed (Image* swapchainImageHandlesPtr = swapchainImageHandles)
        {
            khrSwapchain.GetSwapchainImages(device, handle, ref swapchainImageCount, swapchainImageHandlesPtr);
        }


        VKSwapchain swapchain = new VKSwapchain(
            this,
            surface,
            handle,
            imageFormat,
            presentMode,
            imageExtent,
            imageArrayLayers,
            minImageCount,
            imageUsage,
            imageSharingMode,
            queueFamilyIndices,
            preTransform ?? Parent.GetSurfaceCapabilities(surface).CurrentTransform,
            compositeAlpha,
            clipped,
            swapchainImageHandles
        );

        swapchains[swapchain] = allocationCallbacks;

        return swapchain;
    }

    internal bool DestroySwapchain(VKSwapchain swapchain, SwapchainKHR handle)
    {
        if (!swapchains.TryRemove(swapchain, out var allocationCallbacks))
        {
            return false;
        }
        return swapchain.Surface.DestroySwapchain(this, CheckedHandle, swapchain, handle, allocationCallbacks);
    }

    public bool IsSwapchainOfThis(VKSwapchain swapchain)
        => swapchains.ContainsKey(swapchain) && Valid;


    /// <summary>
    /// vkAcquireNextImageKHR / vkAcquireNextImage2KHR 共用的那套归属校验（本仓库的访问性约定）：
    /// 交换链 / 信号量 / 栅栏的裸句柄必须分别属于传进来的包装类（谁的句柄谁自己管），
    /// 而且这三样都得挂在同一个逻辑设备（this）上。
    /// </summary>
    private void CheckSwapchainAcquireArguments(
        VKSwapchain swapchain,
        SwapchainKHR handle,
        VKSemaphore imageAvailableSemaphore,
        Silk.NET.Vulkan.Semaphore semaphore,
        VKFence? signalFence,
        Fence fence)
    {
        if (!swapchain.IsHandleOfThis(handle))
        {
            throw new InvalidOperationException(
                "The swapchain handle is not valid: it is not the handle of the parent VKSwapchain.");
        }

        if (!imageAvailableSemaphore.IsHandleOfThis(semaphore))
        {
            throw new InvalidOperationException(
                "The semaphore handle is not valid: it is not the handle of the parent VKSemaphore.");
        }

        // 栅栏是可选的（vkAcquireNextImageKHR 允许只给信号量）：null 表示这次不需要栅栏，
        // 句柄按 VK_NULL_HANDLE 传下去。
        if (signalFence != null && !signalFence.IsHandleOfThis(fence))
        {
            throw new InvalidOperationException(
                "The fence handle is not valid: it is not the handle of the parent VKFence.");
        }

        if (swapchain.Parent != this || imageAvailableSemaphore.Parent != this ||
            (signalFence != null && signalFence.Parent != this))
        {
            throw new InvalidOperationException(
                "One or more handles are not valid: they are not the handles of the parent VKLogicalDevice.");
        }
    }

    internal Result Swapchain_AcquireNextImage(
        VKSwapchain swapchain,
        SwapchainKHR handle,
        ulong timeout,
        VKSemaphore imageAvailableSemaphore,
        Silk.NET.Vulkan.Semaphore semaphore,
        VKFence? signalFence,
        Fence fence,
        out uint imageIndex)
    {
        Device deviceHandle = CheckedHandle;

        CheckSwapchainAcquireArguments(swapchain, handle, imageAvailableSemaphore, semaphore, signalFence, fence);

        if (!TryGetDeviceExtension(out KhrSwapchain khrSwapchain))
        {
            throw new NotSupportedException("KHR_swapchain extension not found.");
        }
        
        uint index = 0;
        Result result = khrSwapchain.AcquireNextImage(deviceHandle, handle, timeout, semaphore, fence, ref index);
        imageIndex = index;

        return result;
    }

    /// <summary>
    /// vkAcquireNextImage2KHR：vkAcquireNextImageKHR 的**设备组（device group）**版本。
    /// 语义和 AcquireNextImage 一模一样（要一张可用图像，并把 semaphore / fence 的置位交给驱动），
    /// 区别只是参数被塞进了 VkAcquireNextImageInfoKHR，里面多一个 deviceMask ——
    /// "这次拿到的图像归设备组里哪几个设备管"的位掩码；没开设备组（本仓库这种单 GPU）时就是 bit 0，也就是 1。
    ///
    /// 分工和它的兄弟完全一致（本类做最后的 Vulkan 调用，其余句柄由各包装类当参数传下来），所以并排放。
    /// </summary>
    internal unsafe Result Swapchain_AcquireNextImage2(
        VKSwapchain swapchain,
        SwapchainKHR handle,
        ulong timeout,
        VKSemaphore imageAvailableSemaphore,
        Silk.NET.Vulkan.Semaphore semaphore,
        VKFence? signalFence,
        Fence fence,
        uint deviceMask,
        out uint imageIndex)
    {
        Device deviceHandle = CheckedHandle;

        CheckSwapchainAcquireArguments(swapchain, handle, imageAvailableSemaphore, semaphore, signalFence, fence);

        if (!TryGetDeviceExtension(out KhrSwapchain khrSwapchain))
        {
            throw new NotSupportedException("KHR_swapchain extension not found.");
        }

        // 和 vkAcquireNextImageKHR 的区别全在这一个结构体里：swapchain / timeout / semaphore / fence
        // 原样搬进来，外加 deviceMask。struct 的 new S() 只会拿到零值，所以必须用带名构造函数把 SType 也一起填上。
        AcquireNextImageInfoKHR acquireInfo = new(
            swapchain: handle,
            timeout: timeout,
            semaphore: semaphore,
            fence: fence,
            deviceMask: deviceMask);

        uint index = 0;
        Result result = khrSwapchain.AcquireNextImage2(deviceHandle, in acquireInfo, ref index);
        imageIndex = index;

        return result;
    }

    /// <summary>
    /// vkGetDeviceGroupPresentCapabilitiesKHR：问这个逻辑设备在"设备组呈现"里的能力 ——
    /// presentMask 是**每个设备**的一组位（presentMask[i] 的 bit j 置位 = 设备 j 能呈现到设备 i 上，
    /// 数组固定 32 项），modes 是支持的模式（LOCAL / REMOTE / SUM / LOCAL_MULTI_DEVICE）。
    /// 没开设备组（本仓库这种单 GPU）照样能调：presentMask[0] 只有 bit 0 是 1，也就是"只有自己"。
    ///
    /// 签名里零裸句柄（device 就是本类自己的 CheckedHandle），所以是 public；
    /// 用 out 重载是为了让它顺手把 SType 填好（struct 的 new S() 只给零值，这个坑踩过好几次）。
    /// </summary>
    public Result GetDeviceGroupPresentCapabilities(out DeviceGroupPresentCapabilitiesKHR capabilities)
    {
        Device device = CheckedHandle;

        if (!TryGetDeviceExtension(out KhrSwapchain khrSwapchain))
        {
            throw new NotSupportedException("KHR_swapchain extension not found.");
        }

        return khrSwapchain.GetDeviceGroupPresentCapabilities(device, out capabilities);
    }

    /// <summary>
    /// vkGetDeviceGroupSurfacePresentModesKHR：这个 surface 在设备组呈现下支持哪些模式
    ///（同样只在开了设备组时才有意义；单 GPU 一般只报 LOCAL）。
    /// 分工同 TryCreateOrUpdateSwapchain：本方法 public、签名零裸句柄，
    /// 真正的调用在 VKSurface 那边（它读自己的 CheckedHandle，把 surface 句柄交出去）。
    /// </summary>
    public Result GetDeviceGroupSurfacePresentModes(VKSurface surface, out DeviceGroupPresentModeFlagsKHR modes)
        => surface.GetDeviceGroupSurfacePresentModes(this, CheckedHandle, out modes);

    /// <summary>
    /// vkGetPhysicalDevicePresentRectanglesKHR：这个 surface 上，设备组里**各个设备各自负责呈现的区域**列表。
    /// 名字里带 PhysicalDevice，但它其实属于 KHR_swapchain（**设备**扩展）——
    /// 得先有逻辑设备才能从 vkGetDeviceProcAddr 拿到这个函数，所以入口挂在本类上；
    /// 物理设备句柄由 VKPhysicalDevice 那一环交出来（同 GetSurfaceCapabilities 的分工），
    /// 最后真正调用在 VKSurface.GetPresentRectangles（它读自己的 CheckedHandle）。
    /// </summary>
    public ReadOnlyCollection<Rect2D> GetPresentRectangles(VKSurface surface)
        => Parent.GetPresentRectangles(surface, this, CheckedHandle);



    #endregion

}

public partial class VKDeviceQueue
{

    internal Result Swapchain_Present(VKSwapchain swapchain, SwapchainKHR handle, uint imageIndex, VKSemaphore waitSemaphore)
        => waitSemaphore.Swapchain_Present(swapchain, handle, this, CheckedHandle, imageIndex);
}

public partial class VKSurface
{
    private SwapchainKHR currentSwapChain;
    private VKLogicalDevice? currentLinkedDevice;
    /// <summary>
    /// 教程 createSwapchain() 的另一个入口：交换链是"surface + 逻辑设备"绑在一起才成立的，所以从 surface 这边也能建，
    /// 好处是调用方不必自己去拿 surface 句柄——本类直接把 CheckedHandle 交给逻辑设备。
    ///
    /// 交换链的父对象依然是 VKLogicalDevice（vkCreate/DestroySwapchainKHR 都是设备级调用），
    /// 真正的实现和登记都在 VKLogicalDevice.CreateSwapchain 里，这里只负责转发。
    ///
    /// 按访问性约定（需要句柄就由调用方传进来、再用 IsHandleOfThis 校验），这里只多出一个 logicalDevice 参数：
    /// 本类把自己的 CheckedHandle 交给它，它用自己的句柄去调 Vulkan —— 所以从这边走的话，
    /// 调用方一个裸句柄都不用拿。其余参数的含义见 VKLogicalDevice.CreateSwapchain 的注释。
    /// </summary>
    internal unsafe SwapchainKHR CreateSwapchain(
        VKLogicalDevice logicalDevice,
        Device logicalDeviceHandle,
        SurfaceFormatKHR imageFormat,
        PresentModeKHR presentMode,
        Extent2D imageExtent,
        uint imageArrayLayers,
        uint minImageCount,
        ImageUsageFlags imageUsage,
        SharingMode imageSharingMode,
        uint[]? queueFamilyIndices,
        SurfaceTransformFlagsKHR preTransform,
        CompositeAlphaFlagsKHR compositeAlpha,
        bool clipped,
        AllocationCallbacks? allocationCallbacks,
        out SwapchainKHR? oldHandle)
    {
        
        lock(this)
        {

            SurfaceKHR surfaceHandle = CheckedHandle;
            if (!logicalDevice.IsHandleOfThis(logicalDeviceHandle))
            {
                throw new ArgumentException("The provided logical device handle is not associated with this surface.");
            }

            // KHR_swapchain 也是"要主动打开的扩展"，而且要在逻辑设备上打开（同教程那句注释）。
            if (!logicalDevice.TryGetDeviceExtension(out KhrSwapchain khrSwapchain))
            {
                throw new NotSupportedException("KHR_swapchain extension not found.");
            }

            if (currentLinkedDevice != null)
            {
                if(currentLinkedDevice != logicalDevice)
                {
                    throw new ArgumentException("This surface is already linked to a different logical device.");
                }
                oldHandle = currentSwapChain;
            }
            else
            {
                oldHandle = null;
                currentSwapChain = default;
            }


            // 教程说：图形族和呈现族是同一个时用 EXCLUSIVE（只有一边访问图像，性能最好）；
            // 是两个族时图像会被两边同时访问，只能用 CONCURRENT，并且要把这两个族索引都列出来。
            // pQueueFamilyIndices 只在创建这一次调用期间有效，所以用 fixed 把数组钉住，出了 fixed 块就作废
            //（数组是 null 时 fixed 得到的就是空指针，正好对应"不给队列族索引"）。
            uint queueFamilyIndexCount = queueFamilyIndices != null ? (uint)queueFamilyIndices.Length : 0;

            // 重建交换链时把老交换链交给驱动，让它复用内部资源（教程《Swap chain recreation》）。
            // 老交换链只在这一次调用里用一下，本类不保存它的引用。

            SwapchainKHR handle;
            fixed (uint* pQueueFamilyIndices = queueFamilyIndices)
            {
                SwapchainCreateInfoKHR swapchainCreateInfo = new(
                    surface: surfaceHandle,
                    minImageCount: minImageCount,
                    imageFormat: imageFormat.Format,
                    imageColorSpace: imageFormat.ColorSpace,
                    imageExtent: imageExtent,
                    imageArrayLayers: imageArrayLayers,
                    imageUsage: imageUsage,
                    imageSharingMode: imageSharingMode,
                    queueFamilyIndexCount: queueFamilyIndexCount,
                    pQueueFamilyIndices: pQueueFamilyIndices,
                    preTransform: preTransform,
                    compositeAlpha: compositeAlpha,
                    presentMode: presentMode,
                    oldSwapchain : currentSwapChain,
                    clipped: clipped);

                if (allocationCallbacks != null)
                {
                    AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
                    if (khrSwapchain.CreateSwapchain(logicalDeviceHandle, in swapchainCreateInfo, &dAllocationCallbacks, out handle) != Result.Success)
                    {
                        throw new NotSupportedException("failed to create swap chain!");
                    }
                }
                else if (khrSwapchain.CreateSwapchain(logicalDeviceHandle, in swapchainCreateInfo, null, out handle) != Result.Success)
                {
                    throw new NotSupportedException("failed to create swap chain!");
                }
            }

            currentSwapChain = handle;
            currentLinkedDevice = logicalDevice;
            return handle;
        }
    }

    internal unsafe bool DestroySwapchain(
        VKLogicalDevice logicalDevice,
        Device deviceHandle,
        VKSwapchain swapchain,
        SwapchainKHR handle,
        AllocationCallbacks? allocationCallbacks
    )
    {
        if(
            !logicalDevice.IsHandleOfThis(deviceHandle) ||
            !swapchain.IsHandleOfThis(handle) ||
            !logicalDevice.TryGetDeviceExtension(out KhrSwapchain khrSwapchain)
        )
        {
            return false;
        }

        if(currentSwapChain.Handle == handle.Handle)
        {
            currentSwapChain = default;
            if(currentLinkedDevice == logicalDevice)
            {
                currentLinkedDevice = null;
            }
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            khrSwapchain.DestroySwapchain(deviceHandle, handle, &dAllocationCallbacks);
        }
        else
        {
            khrSwapchain.DestroySwapchain(deviceHandle, handle, null);
        }
        return true;
    }
}
public partial class VKSemaphore
{


    internal Result Swapchain_AcquireNextImage(VKSwapchain swapchain, SwapchainKHR handle, ulong timeout, VKFence? signalFence, out uint imageIndex)
        => signalFence != null
            ? signalFence.Swapchain_AcquireNextImage(swapchain, handle, timeout, this, CheckedHandle, out imageIndex)
            : swapchain.Parent.Swapchain_AcquireNextImage(swapchain, handle, timeout, this, CheckedHandle, null, default, out imageIndex);

    /// <summary>
    /// vkAcquireNextImage2KHR 这一环（链路和上面那条一模一样，只是多带一个 deviceMask）：
    /// 本类只把自己的 CheckedHandle 当参数交给栅栏，下一环（VKFence）再交给逻辑设备去调。
    /// </summary>
    internal Result Swapchain_AcquireNextImage2(VKSwapchain swapchain, SwapchainKHR handle, ulong timeout, VKFence? signalFence, uint deviceMask, out uint imageIndex)
        => signalFence != null
            ? signalFence.Swapchain_AcquireNextImage2(swapchain, handle, timeout, this, CheckedHandle, deviceMask, out imageIndex)
            : swapchain.Parent.Swapchain_AcquireNextImage2(swapchain, handle, timeout, this, CheckedHandle, null, default, deviceMask, out imageIndex);


    internal unsafe Result Swapchain_Present(VKSwapchain swapchain, SwapchainKHR handle, VKDeviceQueue presentQueue, Queue queue, uint imageIndex)
    {
        Silk.NET.Vulkan.Semaphore semaphore = CheckedHandle;

        // 访问性约定：队列句柄由调用方传进来，用它自己的 IsHandleOfThis 校验"确实是这个队列"。
        if (!swapchain.IsHandleOfThis(handle))
        {
            throw new InvalidOperationException(
                "The swapchain handle is not valid: it is not the handle of the given VKSwapchain.");
        }
        if (!presentQueue.IsHandleOfThis(queue))
        {
            throw new InvalidOperationException(
                "The queue handle is not valid: it is not the handle of the given VKDeviceQueue.");
        }

        if (presentQueue.Parent != swapchain.Parent || swapchain.Parent != Parent)
        {
            throw new InvalidOperationException(
                "The queue and swapchain do not belong to the same logical device.");
        }

        if (!Parent.TryGetDeviceExtension(out KhrSwapchain khrSwapchain))
        {
            throw new NotSupportedException("KHR_swapchain extension not found.");
        }

        PresentInfoKHR presentInfo = new(
            waitSemaphoreCount: 1,
            pWaitSemaphores: &semaphore,

            swapchainCount: 1,
            pSwapchains: &handle,

            pImageIndices: &imageIndex);
        lock (presentQueue)
        {
            return khrSwapchain.QueuePresent(queue, &presentInfo);
        }
    }
}
public partial class VKFence
{
    internal Result Swapchain_AcquireNextImage(VKSwapchain swapchain, SwapchainKHR handle, ulong timeout, VKSemaphore imageAvailableSemaphore, Silk.NET.Vulkan.Semaphore semaphore, out uint imageIndex)
        => swapchain.Parent.Swapchain_AcquireNextImage(swapchain, handle, timeout, imageAvailableSemaphore, semaphore, this, CheckedHandle, out imageIndex);

    /// <summary>
    /// vkAcquireNextImage2KHR 这一环：同上面那条链路，只是多带一个 deviceMask，
    /// 最后交给逻辑设备去拼 VkAcquireNextImageInfoKHR 并调用。
    /// </summary>
    internal Result Swapchain_AcquireNextImage2(VKSwapchain swapchain, SwapchainKHR handle, ulong timeout, VKSemaphore imageAvailableSemaphore, Silk.NET.Vulkan.Semaphore semaphore, uint deviceMask, out uint imageIndex)
        => swapchain.Parent.Swapchain_AcquireNextImage2(swapchain, handle, timeout, imageAvailableSemaphore, semaphore, this, CheckedHandle, deviceMask, out imageIndex);
}
