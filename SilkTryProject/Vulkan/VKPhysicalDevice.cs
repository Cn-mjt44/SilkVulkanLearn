using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SilkTry.Vulkan;

public class VKPhysicalDevice
{
    public readonly VKInstance Parent;
    private readonly PhysicalDevice handle;

    // 从这个物理设备上切出来的逻辑设备。写法和 VKInstance.surfaces 一样：
    // 创建时登记进来，之后靠 IsLogicalDeviceOfThisPhysicalDevice() 反查归属。
    private readonly ConcurrentDictionary<VKLogicalDevice, AllocationCallbacks?> logicalDevices = new ();


    private readonly static Vk VK = Vk.GetApi();

    internal VKPhysicalDevice(VKInstance parent, PhysicalDevice handle)
    {
        Parent = parent;
        this.handle = handle;
    }

    public bool Valid => Parent.PhysicalDevices.Contains(this);

    /// <summary>
    /// 所有查询（属性和方法）都从这里拿句柄：先确认这个设备确实属于创建它的那个 VKInstance（Valid）。
    /// 不合法就抛 InvalidOperationException —— 宁可在这里直接报错，
    /// 也不要拿一个不属于本实例的野句柄去调 Vulkan。
    /// </summary>
    private PhysicalDevice CheckedHandle
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

    public bool IsHandleOfThis(PhysicalDevice handleForCheck)
        => handleForCheck.Handle == handle.Handle;

    #region 单值查询：一次调用直接返回一个结构体

    /// <summary>
    /// 教程 isDeviceSuitable()：vkGetPhysicalDeviceFeatures，查 geometryShader 之类的设备特性。
    /// </summary>
    public PhysicalDeviceFeatures Features => VK.GetPhysicalDeviceFeatures(CheckedHandle);

    /// <summary>
    /// 教程 rateDeviceSuitability()：vkGetPhysicalDeviceProperties，主要看 deviceType 给设备打分。
    /// 名字容易看混，提醒一下：这个是 PhysicalDeviceProperties（原属性名 FormatProperties 是笔误，已改名），
    /// 和 vkGetPhysicalDeviceFormatProperties（要传 Format、返回 FormatProperties）不是一回事。
    /// </summary>
    public PhysicalDeviceProperties Properties => VK.GetPhysicalDeviceProperties(CheckedHandle);

    /// <summary>
    /// 教程《Vertex buffer creation》的 findMemoryType()：vkGetPhysicalDeviceMemoryProperties，
    /// 用 memoryTypes / memoryHeaps 找一块合适的内存类型。
    /// </summary>
    public PhysicalDeviceMemoryProperties MemoryProperties => VK.GetPhysicalDeviceMemoryProperties(CheckedHandle);

    // 下面三个是核心 1.1（或 VK_KHR_get_physical_device_properties2）版的同一个查询，
    // 区别在于它们能靠 pNext 链一次问一堆扩展的特性/属性（这里 pNext 留空）。
    // 必须用 out 重载：单参数的 GetPhysicalDeviceXxx2(handle) 返回的是 default 结构体（SType = 0），
    // 用校验层跑会当场报错；只有 out 重载会帮你把 SType 填好。

    /// <summary>vkGetPhysicalDeviceFeatures2：Features 的 1.1 版（pNext 链版）。</summary>
    public PhysicalDeviceFeatures2 Features2
    {
        get
        {
            VK.GetPhysicalDeviceFeatures2(CheckedHandle, out PhysicalDeviceFeatures2 features2);
            return features2;
        }
    }

    /// <summary>vkGetPhysicalDeviceProperties2：Properties 的 1.1 版（pNext 链版）。</summary>
    public PhysicalDeviceProperties2 Properties2
    {
        get
        {
            VK.GetPhysicalDeviceProperties2(CheckedHandle, out PhysicalDeviceProperties2 properties2);
            return properties2;
        }
    }

    /// <summary>vkGetPhysicalDeviceMemoryProperties2：MemoryProperties 的 1.1 版（pNext 链版）。</summary>
    public PhysicalDeviceMemoryProperties2 MemoryProperties2
    {
        get
        {
            VK.GetPhysicalDeviceMemoryProperties2(CheckedHandle, out PhysicalDeviceMemoryProperties2 memoryProperties2);
            return memoryProperties2;
        }
    }

    #endregion

    #region 列表查询：「先问数量、再拿列表」，写法同 VKInstance.PhysicalDevices

    private ReadOnlyCollection<QueueFamilyProperties>? queueFamilies = null;

    /// <summary>
    /// 教程 findQueueFamilies()：vkGetPhysicalDeviceQueueFamilyProperties。
    /// 「先问数量、再拿列表」在 Vulkan 里到处都是，所以这里照 VKInstance.PhysicalDevices 的写法实现
    /// （懒加载 + 缓存 + 读写锁），而不是每次访问都重新枚举一遍。队列族信息不会变，缓存是安全的。
    /// </summary>
    public unsafe ReadOnlyCollection<QueueFamilyProperties> QueueFamilies
    {
        get
        {
            PhysicalDevice device = CheckedHandle;

            if (queueFamilies == null)
            {
                uint queueFamilyCount = 0;
                VK.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, null);

                if (queueFamilyCount == 0)
                {
                    throw new NotSupportedException("physical device reports zero queue families!");
                }

                var families = new QueueFamilyProperties[queueFamilyCount];
                fixed (QueueFamilyProperties* familiesPtr = families)
                {
                    VK.GetPhysicalDeviceQueueFamilyProperties(device, ref queueFamilyCount, familiesPtr);
                }
                queueFamilies = new ReadOnlyCollection<QueueFamilyProperties>(families);
            }

            return queueFamilies;
        }
    }

    private ReadOnlyCollection<QueueFamilyProperties2>? queueFamilies2 = null;

    /// <summary>
    /// 核心 1.1 版的队列族列表（vkGetPhysicalDeviceQueueFamilyProperties2），
    /// 和 QueueFamilies 的唯一区别是每个元素都能挂 pNext 链去问扩展信息。
    /// 注意：用裸指针枚举时，每个元素的 SType 必须由调用方填 —— struct 的 new S() 只会拿到零值，
    /// 不会去调用那个「参数全可选」的构造函数，所以这里显式 new QueueFamilyProperties2(StructureType.QueueFamilyProperties2)。
    /// </summary>
    public unsafe ReadOnlyCollection<QueueFamilyProperties2> QueueFamilies2
    {
        get
        {
            PhysicalDevice device = CheckedHandle;

            if (queueFamilies2 == null)
            {
                uint queueFamilyCount = 0;
                VK.GetPhysicalDeviceQueueFamilyProperties2(device, ref queueFamilyCount, null);

                if (queueFamilyCount == 0)
                {
                    throw new NotSupportedException("physical device reports zero queue families!");
                }

                var families = new QueueFamilyProperties2[queueFamilyCount];
                for (int i = 0; i < families.Length; i++)
                {
                    families[i] = new QueueFamilyProperties2(StructureType.QueueFamilyProperties2);
                }

                fixed (QueueFamilyProperties2* familiesPtr = families)
                {
                    VK.GetPhysicalDeviceQueueFamilyProperties2(device, ref queueFamilyCount, familiesPtr);
                }

                queueFamilies2 = new ReadOnlyCollection<QueueFamilyProperties2>(families);
            }

            return queueFamilies2;
        }
    }

    #endregion

    #region 带参数的查询：参数会变，没法做属性，做成方法

    /// <summary>
    /// vkGetPhysicalDeviceFormatProperties：问「某个 Format 支持哪些特性」，
    /// 返回的 FormatProperties 里是 linearTilingFeatures / optimalTilingFeatures / bufferFeatures 三组。
    /// </summary>
    public FormatProperties GetFormatProperties(Format format)
        => VK.GetPhysicalDeviceFormatProperties(CheckedHandle, format);

    /// <summary>
    /// vkGetPhysicalDeviceImageFormatProperties：比上面问得更细，
    /// 连 imageType / tiling / usage / flags 一起给，返回 Result。
    /// 返回 ErrorFormatNotSupported 就说明这个组合不被支持（教程里判断「能不能当深度/模板附件」常用）。
    /// </summary>
    public Result GetImageFormatProperties(
        Format format,
        ImageType imageType,
        ImageTiling tiling,
        ImageUsageFlags usage,
        ImageCreateFlags flags,
        out ImageFormatProperties properties)
        => VK.GetPhysicalDeviceImageFormatProperties(CheckedHandle, format, imageType, tiling, usage, flags, out properties);

    /// <summary>
    /// vkGetPhysicalDeviceSparseImageFormatProperties：又是「先问数量、再拿列表」，
    /// 所以照样用 VKInstance.PhysicalDevices 那套写法；区别是参数会变、结果不能缓存，只能每次现问。
    /// 注意：属性数量为 0 在这里是正常结果（这个组合就是不支持稀疏），不像枚举 GPU 那样算错误。
    /// </summary>
    public unsafe ReadOnlyCollection<SparseImageFormatProperties> GetSparseImageFormatProperties(
        Format format,
        ImageType imageType,
        SampleCountFlags samples,
        ImageUsageFlags usage,
        ImageTiling tiling)
    {
        PhysicalDevice device = CheckedHandle;

        uint propertyCount = 0;
        VK.GetPhysicalDeviceSparseImageFormatProperties(device, format, imageType, samples, usage, tiling, ref propertyCount, null);

        if (propertyCount == 0)
        {
            return new ReadOnlyCollection<SparseImageFormatProperties>(Array.Empty<SparseImageFormatProperties>());
        }

        var properties = new SparseImageFormatProperties[propertyCount];
        fixed (SparseImageFormatProperties* propertiesPtr = properties)
        {
            VK.GetPhysicalDeviceSparseImageFormatProperties(device, format, imageType, samples, usage, tiling, ref propertyCount, propertiesPtr);
        }

        return new ReadOnlyCollection<SparseImageFormatProperties>(properties);
    }

    /// <summary>
    /// 教程《Vertex buffer creation》里的 findMemoryType()：在物理设备的内存类型表里找一个
    /// **同时满足 typeFilter（资源要求的那些位：vkGetXxxMemoryRequirements 问到的 memoryTypeBits）
    /// 和 properties（想要的性质，比如 DeviceLocal，或者 HostVisible + HostCoherent）** 的类型下标。
    /// 教程就是"线性扫描 + 位与判断"，找不到直接抛 —— 这里也一样。
    /// （它只读缓存过的 MemoryProperties，所以不需要 CheckedHandle，只靠属性自己校验。）
    /// </summary>
    public uint FindMemoryType(uint typeFilter, MemoryPropertyFlags properties)
    {
        PhysicalDeviceMemoryProperties memoryProperties = MemoryProperties;

        for (uint i = 0; i < memoryProperties.MemoryTypeCount; i++)
        {
            // MemoryTypes 是 Silk.NET 生成的内联数组（MemoryTypesBuffer），带索引器；
            // typeFilter 的第 i 位为 1 表示"这种类型可用"，PropertyFlags 是位标志（HasFlag 检查是否全含）。
            if ((typeFilter & (1u << (int)i)) != 0 &&
                memoryProperties.MemoryTypes[(int)i].PropertyFlags.HasFlag(properties))
            {
                return i;
            }
        }

        throw new NotSupportedException("failed to find suitable memory type!");
    }

    /// <summary>
    /// 某个内存类型下标（就是 findMemoryType 选出来的那个）的性质位：
    /// DeviceLocal / HostVisible / HostCoherent / HostCached / LazilyAllocated……
    /// 用来回答"这块内存能不能映射（HostVisible）、映射后要不要手动 flush（非 HostCoherent）"，
    /// 见 VKDeviceMemory.IsHostVisible / IsHostCoherent / NeedsManualSynchronization。
    /// 参数越界就直接抛，不要拿一个不存在的内存类型去索引驱动给的表。
    /// </summary>
    public MemoryPropertyFlags GetMemoryTypePropertyFlags(uint memoryTypeIndex)
    {
        PhysicalDeviceMemoryProperties memoryProperties = MemoryProperties;

        if (memoryTypeIndex >= memoryProperties.MemoryTypeCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(memoryTypeIndex),
                $"This physical device only reports {memoryProperties.MemoryTypeCount} memory types.");
        }

        // MemoryTypes 是 Silk.NET 生成的内联数组（MemoryTypesBuffer），要和 FindMemoryType 一样用 int 索引。
        return memoryProperties.MemoryTypes[(int)memoryTypeIndex].PropertyFlags;
    }

    /// <summary>
    /// VkPhysicalDeviceLimits::nonCoherentAtomSize —— 非一致性内存的**最小同步粒度**（字节）。
    /// vkFlushMappedMemoryRanges / vkInvalidateMappedMemoryRanges 的 offset（以及 size）必须按它对齐
    ///（VUID-VkMappedMemoryRange-offset-00687），所以 VKDeviceMemory.Flush / Invalidate 会用它来取整。
    /// </summary>
    public ulong NonCoherentAtomSize => Properties.Limits.NonCoherentAtomSize;

    // 下面三个是外部句柄（dma_buf / win32 句柄 / 同步 fd 之类的）能力查询，
    // 核心 1.1 的功能（对应 VK_KHR_external_memory/fence/semaphore_capabilities 系列扩展）。
    // 入参是「额外参数」：buffer 那个要 flags + usage，fence/semaphore 只要 handleType。
    // PhysicalDeviceExternalXxxInfo 的 SType 交给构造函数填（注意必须至少传一个参数，
    // 因为 struct 的 new S() 只会拿到零值、不会调用那个「参数全可选」的构造函数）。

    /// <summary>vkGetPhysicalDeviceExternalBufferProperties：这个 buffer 用法 + 外部句柄类型能不能共享出去。</summary>
    public unsafe ExternalBufferProperties GetExternalBufferProperties(
        ExternalMemoryHandleTypeFlags handleType,
        BufferUsageFlags usage = BufferUsageFlags.None,
        BufferCreateFlags flags = BufferCreateFlags.None)
    {
        var info = new PhysicalDeviceExternalBufferInfo(
            flags: flags,
            usage: usage,
            handleType: handleType);
        return VK.GetPhysicalDeviceExternalBufferProperties(CheckedHandle, in info);
    }

    /// <summary>vkGetPhysicalDeviceExternalFenceProperties：这种外部 fence 句柄类型能不能导入/导出。</summary>
    public unsafe ExternalFenceProperties GetExternalFenceProperties(ExternalFenceHandleTypeFlags handleType)
    {
        var info = new PhysicalDeviceExternalFenceInfo(handleType: handleType);
        return VK.GetPhysicalDeviceExternalFenceProperties(CheckedHandle, in info);
    }

    /// <summary>vkGetPhysicalDeviceExternalSemaphoreProperties：这种外部信号量句柄类型能不能导入/导出。</summary>
    public unsafe ExternalSemaphoreProperties GetExternalSemaphoreProperties(ExternalSemaphoreHandleTypeFlags handleType)
    {
        var info = new PhysicalDeviceExternalSemaphoreInfo(handleType: handleType);
        return VK.GetPhysicalDeviceExternalSemaphoreProperties(CheckedHandle, in info);
    }

    #endregion

    #region surface 能力查询：教程《Swap chain》章节的 querySwapchainSupport()

    /// <summary>
    /// vkGetPhysicalDeviceSurfaceSupportKHR：教程 findQueueFamilies() 里的 presentSupport ——
    /// 这个队列族能不能往这个 surface 上呈现。教程特意提醒这一条不能靠队列族的 flags 猜，必须问 surface。
    /// 是否支持放在 supported 里，Result 照常交给调用方（教程也没管它）。
    /// </summary>
    public Result GetSurfaceSupport(uint queueFamilyIndex, VKSurface surface, out bool supported)
        => surface.GetSurfaceSupport(this, CheckedHandle, queueFamilyIndex, out supported);

    /// <summary>
    /// vkGetPhysicalDeviceSurfaceCapabilitiesKHR：一个结构体里装着 min/maxImageCount、currentExtent、
    /// min/maxImageExtent、currentTransform、supportedCompositeAlpha 等，
    /// 是算分辨率（chooseSwapExtent）和定 preTransform 的依据。
    /// </summary>
    public SurfaceCapabilitiesKHR GetSurfaceCapabilities(VKSurface surface)
        => surface.GetSurfaceCapabilities(this, CheckedHandle);

    /// <summary>
    /// vkGetPhysicalDeviceSurfaceFormatsKHR：这个 surface 支持哪些 (format, colorSpace) 组合。
    /// 同样是"先问数量再拿列表"，同 QueueFamilies；
    /// 教程说结果可能是空的（这个 surface 在这个设备上不可用），空就是空，不算错误 ——
    /// 想知道"够不够用"看 SwapchainSupportDetails.IsAdequate。
    /// </summary>
    public ReadOnlyCollection<SurfaceFormatKHR> GetSurfaceFormats(VKSurface surface)
        => surface.GetSurfaceFormats(this, CheckedHandle);

    /// <summary>
    /// vkGetPhysicalDeviceSurfacePresentModesKHR：支持的呈现模式列表（FIFO 一定在，MAILBOX 看驱动）。
    /// 也是"先问数量再拿列表"，同样可能是空的。
    /// </summary>
    public ReadOnlyCollection<PresentModeKHR> GetSurfacePresentModes(VKSurface surface)
        => surface.GetSurfacePresentModes(this, CheckedHandle);

    /// <summary>
    /// vkGetPhysicalDevicePresentRectanglesKHR 的这一环：本方法只做"把自己的物理设备句柄交出去"，
    /// 真正调用在 VKSurface.GetPresentRectangles（它读自己的 CheckedHandle）。
    /// 参数里带裸句柄（Device），所以是 internal。
    /// </summary>
    internal ReadOnlyCollection<Rect2D> GetPresentRectangles(VKSurface surface, VKLogicalDevice logicalDevice, Device logicalDeviceHandle)
        => surface.GetPresentRectangles(this, CheckedHandle, logicalDevice, logicalDeviceHandle);
    #endregion

    #region 创建设备：逻辑设备（教程《Logical device and queues》）

    /// <summary>
    /// 教程 createLogicalDevice()：vkCreateDevice。
    /// 逻辑设备才是真正干活的接口，创建时要说明四件事：
    ///   ① queueFamilyIndices —— 用哪些队列族（内部会去重：图形族和呈现族常常是同一个索引，
    ///      教程里用 std::set 干这件事；同一个队列族也不能出现在两条 VkDeviceQueueCreateInfo 里）；
    ///   ② enabledFeatures    —— 开哪些设备特性（不传就是全关，等价于教程的 VkPhysicalDeviceFeatures{}）；
    ///   ③ deviceExtensions   —— 开哪些设备扩展（比如 KHR_swapchain）；
    ///   ④ allocationCallbacks—— 可选的内存分配回调，写法同 CreateWindowSurface。
    ///
    /// 创建成功后会把这个逻辑设备登记到本物理设备的集合里（同 VKInstance.CreateWindowSurface 登记 surface
    /// 的做法），这样 VKLogicalDevice.Valid 才能校验「它确实属于我这个物理设备」。
    /// 注意：教程那段把实例层名复制给逻辑设备的代码这里依然不写 —— 设备层自 Vulkan 1.1 废弃，
    /// DeviceCreateInfo 的 enabledLayerCount 必须保持 0。
    /// </summary>
    public unsafe VKLogicalDevice CreateLogicalDevice(
        Dictionary<uint, float[]> queueFamilyIndicesWithCount,
        PhysicalDeviceFeatures? enabledFeatures = null,
        HashSet<string>? deviceExtensions = null,
        AllocationCallbacks? allocationCallbacks = null
    )
    {
        // 按本文件约定：先校验物理设备有效性，再拿锁 / 建对象
        PhysicalDevice device = CheckedHandle;


        var gcHandles = stackalloc GCHandle[queueFamilyIndicesWithCount.Count];
        var queueCreateInfos = stackalloc DeviceQueueCreateInfo[queueFamilyIndicesWithCount.Count];

        // 扩展名数组是 SilkMarshal 在堆上分配的，vkCreateDevice 只在本次调用期间读它，用完就释放。
        byte** ppEnabledExtensionNames = null;
        try
        {
            int i = 0;
            foreach(var kvp in queueFamilyIndicesWithCount)
            {
                GCHandle handle = GCHandle.Alloc(kvp.Value, GCHandleType.Pinned);
                gcHandles[i] = handle;
                queueCreateInfos[i] = new DeviceQueueCreateInfo(
                    queueFamilyIndex: kvp.Key,
                    queueCount: (uint)kvp.Value.Length,
                    pQueuePriorities: (float*)handle.AddrOfPinnedObject());
                i++;
            }

            PhysicalDeviceFeatures features = enabledFeatures ?? default;
            uint enabledExtensionCount = 0;
            if (deviceExtensions != null && deviceExtensions.Count > 0)
            {
                enabledExtensionCount = (uint)deviceExtensions.Count;
                ppEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(deviceExtensions.ToArray());
            }

            Device logicalDeviceHandle;
            DeviceCreateInfo deviceCreateInfo = new(
                queueCreateInfoCount: (uint)queueFamilyIndicesWithCount.Count,
                pQueueCreateInfos: queueCreateInfos,
                enabledExtensionCount: enabledExtensionCount,
                ppEnabledExtensionNames: ppEnabledExtensionNames,
                pEnabledFeatures: &features);

            if (allocationCallbacks != null)
            {
                AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
                if (VK.CreateDevice(device, in deviceCreateInfo, &dAllocationCallbacks, out logicalDeviceHandle) != Result.Success)
                {
                    throw new NotSupportedException("failed to create logical device!");
                }
            }
            else if (VK.CreateDevice(device, in deviceCreateInfo, null, out logicalDeviceHandle) != Result.Success)
            {
                throw new NotSupportedException("failed to create logical device!");
            }

            VKLogicalDevice logicalDevice = new VKLogicalDevice(
                this, logicalDeviceHandle, queueFamilyIndicesWithCount,
                deviceExtensions?.ToArray());
            logicalDevices[logicalDevice] = allocationCallbacks;

            return logicalDevice;
        }
        finally
        {
            if (ppEnabledExtensionNames != null)
            {
                SilkMarshal.Free((nint)ppEnabledExtensionNames);
            }

            for(int i = 0; i < queueFamilyIndicesWithCount.Count; i++)
            {
                if(gcHandles[i].IsAllocated)
                {
                    gcHandles[i].Free();
                }
            }
        }
    }

    internal unsafe bool DestroyLogicalDevice(VKLogicalDevice logicalDevice, Device handle)
    {
        if(
            !logicalDevice.IsHandleOfThis(handle) ||
            !logicalDevices.TryRemove(logicalDevice, out var allocationCallbacks)
        )
        {
            return false;
        }
        if(allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyDevice(handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyDevice(handle, null);
        }
        return true;
    }

    public bool IsLogicalDeviceOfThis(VKLogicalDevice logicalDevice)
        =>  logicalDevices.ContainsKey(logicalDevice) && Valid;

    #endregion
}