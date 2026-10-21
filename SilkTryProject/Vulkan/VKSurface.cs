
using System.Collections.ObjectModel;
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SilkTry.Vulkan;


public partial class VKSurface
{

    public readonly VKInstance Parent;
    private readonly SurfaceKHR handle;
    private readonly static Vk VK = Vk.GetApi();
    internal VKSurface(VKInstance parent, SurfaceKHR handle)
    {
        Parent = parent;
        this.handle = handle;
    }

    public bool Valid => Parent.IsSurfaceOfThis(this);
    private SurfaceKHR CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException(
                    "The physical device is not valid: it does not belong to the parent VKInstance.");
            }

            return handle;
        }
    }

    public void Destroy()
    {
        Parent.DestroyWindowSurface(this, CheckedHandle);
    }

    /// <summary>参数是裸句柄（SurfaceKHR），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(SurfaceKHR handleForCheck)
        => handleForCheck.Handle == handle.Handle;



    #region surface 能力查询：教程《Swap chain》章节的 querySwapchainSupport()

    // 这四个查询都是"物理设备 + surface"合起来才成立的（vkGetPhysicalDeviceSurfaceXxx），所以放在这里：
    // 调用方是物理设备，surface 由参数给进来。KhrSurface 是实例级扩展，写法同 VKInstance.DestroyWindowSurface。
    // 访问性约定：VKSurface 的裸句柄本类读不到（private），所以 surfaceHandle 也由调用方传进来，
    // 每个方法都先用 VKSurface.IsHandleOfThis 校验"确实是这个 surface"。
    private KhrSurface SurfaceExtension
    {
        get
        {
            if (!Parent.TryGetInstanceExtension(out KhrSurface khrSurface))
            {
                throw new NotSupportedException("KHR_surface extension not found.");
            }

            return khrSurface;
        }
    }

    /// <summary>
    /// vkGetPhysicalDeviceSurfaceSupportKHR：教程 findQueueFamilies() 里的 presentSupport ——
    /// 这个队列族能不能往这个 surface 上呈现。教程特意提醒这一条不能靠队列族的 flags 猜，必须问 surface。
    /// 是否支持放在 supported 里，Result 照常交给调用方（教程也没管它）。
    /// </summary>
    internal Result GetSurfaceSupport(VKPhysicalDevice device, PhysicalDevice deviceHandle, uint queueFamilyIndex, out bool supported)
    {

        if (!device.IsHandleOfThis(deviceHandle))
        {
            throw new ArgumentException("The provided physical device handle is not associated with physical device.");
        }

        Result result = SurfaceExtension.GetPhysicalDeviceSurfaceSupport(deviceHandle, queueFamilyIndex, CheckedHandle, out Bool32 presentSupport);

        // Bool32 有到 bool 的隐式转换（它就是 VkTrue / VkFalse 那套）
        supported = presentSupport;

        return result;
    }

    /// <summary>
    /// vkGetPhysicalDeviceSurfaceCapabilitiesKHR：一个结构体里装着 min/maxImageCount、currentExtent、
    /// min/maxImageExtent、currentTransform、supportedCompositeAlpha 等，
    /// 是算分辨率（chooseSwapExtent）和定 preTransform 的依据。
    /// </summary>
    internal SurfaceCapabilitiesKHR GetSurfaceCapabilities(VKPhysicalDevice device, PhysicalDevice deviceHandle)
    {

        if (!device.IsHandleOfThis(deviceHandle))
        {
            throw new ArgumentException("The provided physical device handle is not associated with physical device.");
        }

        SurfaceExtension.GetPhysicalDeviceSurfaceCapabilities(deviceHandle, CheckedHandle, out SurfaceCapabilitiesKHR capabilities);

        return capabilities;
    }

    /// <summary>
    /// vkGetPhysicalDeviceSurfaceFormatsKHR：这个 surface 支持哪些 (format, colorSpace) 组合。
    /// 同样是"先问数量再拿列表"，同 QueueFamilies；
    /// 教程说结果可能是空的（这个 surface 在这个设备上不可用），空就是空，不算错误 ——
    /// 想知道"够不够用"看 SwapchainSupportDetails.IsAdequate。
    /// </summary>
    internal unsafe ReadOnlyCollection<SurfaceFormatKHR> GetSurfaceFormats(VKPhysicalDevice device, PhysicalDevice deviceHandle)
    {

        if (!device.IsHandleOfThis(deviceHandle))
        {
            throw new ArgumentException("The provided physical device handle is not associated with physical device.");
        }
        KhrSurface khrSurface = SurfaceExtension;

        uint formatCount = 0;
        khrSurface.GetPhysicalDeviceSurfaceFormats(deviceHandle, CheckedHandle, ref formatCount, null);

        if (formatCount == 0)
        {
            return new ReadOnlyCollection<SurfaceFormatKHR>(Array.Empty<SurfaceFormatKHR>());
        }

        var formats = new SurfaceFormatKHR[formatCount];
        fixed (SurfaceFormatKHR* formatsPtr = formats)
        {
            khrSurface.GetPhysicalDeviceSurfaceFormats(deviceHandle, CheckedHandle, ref formatCount, formatsPtr);
        }

        return new ReadOnlyCollection<SurfaceFormatKHR>(formats);
    }

    /// <summary>
    /// vkGetPhysicalDeviceSurfacePresentModesKHR：支持的呈现模式列表（FIFO 一定在，MAILBOX 看驱动）。
    /// 也是"先问数量再拿列表"，同样可能是空的。
    /// </summary>
    internal unsafe ReadOnlyCollection<PresentModeKHR> GetSurfacePresentModes(VKPhysicalDevice device, PhysicalDevice deviceHandle)
    {

        if (!device.IsHandleOfThis(deviceHandle))
        {
            throw new ArgumentException("The provided physical device handle is not associated with physical device.");
        }
        KhrSurface khrSurface = SurfaceExtension;

        uint presentModeCount = 0;
        khrSurface.GetPhysicalDeviceSurfacePresentModes(deviceHandle, CheckedHandle, ref presentModeCount, null);

        if (presentModeCount == 0)
        {
            return new ReadOnlyCollection<PresentModeKHR>(Array.Empty<PresentModeKHR>());
        }

        var presentModes = new PresentModeKHR[presentModeCount];
        fixed (PresentModeKHR* presentModesPtr = presentModes)
        {
            khrSurface.GetPhysicalDeviceSurfacePresentModes(deviceHandle, CheckedHandle, ref presentModeCount, presentModesPtr);
        }

        return new ReadOnlyCollection<PresentModeKHR>(presentModes);
    }

    #endregion

    /// <summary>
    /// vkGetDeviceGroupSurfacePresentModesKHR：这个 surface 在设备组呈现下支持哪些模式。
    /// 它属于 KHR_swapchain（**设备**扩展），所以要一个逻辑设备才能拿到函数指针 ——
    /// 写法同 CreateSwapchain：逻辑设备对象 + 它的 Device 句柄一起由调用方传进来，
    /// 本类先用 IsHandleOfThis 校验"确实是这个设备"，再把自己私有的 surface 句柄交出去。
    /// </summary>
    internal Result GetDeviceGroupSurfacePresentModes(VKLogicalDevice logicalDevice, Device deviceHandle, out DeviceGroupPresentModeFlagsKHR modes)
    {
        if (!logicalDevice.IsHandleOfThis(deviceHandle))
        {
            throw new ArgumentException("The provided logical device handle is not associated with this surface.");
        }

        if (!logicalDevice.TryGetDeviceExtension(out KhrSwapchain khrSwapchain))
        {
            throw new NotSupportedException("KHR_swapchain extension not found.");
        }

        return khrSwapchain.GetDeviceGroupSurfacePresentModes(deviceHandle, CheckedHandle, out modes);
    }

    /// <summary>
    /// vkGetPhysicalDevicePresentRectanglesKHR：设备组里每个设备各自负责呈现的区域列表。
    /// 和上面那几个 surface 能力查询一样是"先问数量再拿列表"，结果也可能是空的（空不算错误）；
    /// 区别是它属于**设备**扩展，所以除物理设备句柄外还要逻辑设备来取函数指针，
    /// 校验方式同 GetDeviceGroupSurfacePresentModes。
    /// </summary>
    internal unsafe ReadOnlyCollection<Rect2D> GetPresentRectangles(VKPhysicalDevice device, PhysicalDevice handle, VKLogicalDevice logicalDevice, Device deviceHandle)
    {
        if (!logicalDevice.IsHandleOfThis(deviceHandle))
        {
            throw new ArgumentException("The provided logical device handle is not associated with logical device.");
        }

        if (!device.IsHandleOfThis(handle))
        {
            throw new ArgumentException("The provided physical device handle is not associated with physical device.");
        }

        if (!logicalDevice.TryGetDeviceExtension(out KhrSwapchain khrSwapchain))
        {
            throw new NotSupportedException("KHR_swapchain extension not found.");
        }

        SurfaceKHR surfaceHandle = CheckedHandle;

        uint rectCount = 0;
        khrSwapchain.GetPhysicalDevicePresentRectangles(handle, surfaceHandle, ref rectCount, null);

        if (rectCount == 0)
        {
            return new ReadOnlyCollection<Rect2D>(Array.Empty<Rect2D>());
        }

        var rects = new Rect2D[rectCount];
        fixed (Rect2D* rectsPtr = rects)
        {
            khrSwapchain.GetPhysicalDevicePresentRectangles(handle, surfaceHandle, ref rectCount, rectsPtr);
        }

        return new ReadOnlyCollection<Rect2D>(rects);
    }

}