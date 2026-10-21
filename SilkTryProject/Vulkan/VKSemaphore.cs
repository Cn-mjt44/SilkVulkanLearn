using System.Collections.Concurrent;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;


// 隐式 using 带进来的 System.Threading 里也有一个 Semaphore，和 Vulkan 的同名，必须显式指向后者
//（VKSwapchain.cs 里也是这么处理的；Game.cs 那边是因为 Silk.NET.SDL 也有同名类型）。
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace SilkTry.Vulkan;

/// <summary>
/// 信号量（教程《Rendering and presentation》的 createSyncObjects() /《Frames in flight》章节）的包装。
/// 信号量做的是 **GPU ↔ GPU** 的同步：在队列提交时"等它触发"，或者提交执行完后"置位它"，
/// 用来把"交换链图像可用了"（acquire 完成）和"这一帧画完了"（可以呈现了）串成依赖链 ——
/// 教程里那两条 imageAvailableSemaphore / renderFinishedSemaphore 就是干这个的。
///（栅栏 VKFence 是 GPU ↔ CPU 同步，两者不能混用，教程反复强调过。）
///
/// 归属：vkCreateSemaphore / vkDestroySemaphore 都是设备级调用，所以父对象是 VKLogicalDevice
///（和 VKCommandPool / VKSwapchain / VKRenderPass 一样：创建时登记到父对象集合里，Valid 靠反查）。
///
/// 划界线：二进制信号量最常见的用法（vkQueueSubmit 的 pWaitSemaphores / pSignalSemaphores）
/// 属于**队列提交那一层**，还没有做；本类做的是另一件事 —— **时间线信号量**
///（Vulkan 1.2 核心，原 VK_KHR_timeline_semaphore；教程没讲）可以在主机侧直接
/// Signal / Wait / 查计数值，这三条就在这里。二进制信号量的计数值没有意义，
/// 教程那种用法对它就只剩"持有 + 交给提交 + 销毁"，所以那三个方法只对时间线信号量有效。
///
/// 访问性约定：三个方法都要 Device 句柄，而本类读不到（private），所以真正的 Vulkan 调用放在
/// VKLogicalDevice 里（同 VKSwapchain.AcquireNextImage 的分工），本类只把自己的 CheckedHandle 转交出去；
/// 那边用 IsHandleOfThis 复核句柄，本类这边则先检查"是不是时间线信号量"这种使用上的前提。
/// </summary>
public partial class VKSemaphore
{
    public readonly VKLogicalDevice Parent;

    /// <summary>
    /// 信号量类型：Binary（二进制：只能在"未触发 / 已触发"之间来回，教程用的就是它）
    /// 或 Timeline（时间线：带一个只增不减的 64 位计数值，可以在主机侧等 / 置位）。
    /// 创建时定死，之后不能改。
    /// </summary>
    public readonly SemaphoreType Type;

    /// <summary>
    /// 创建时给的初始计数值 —— 只对 Timeline 有意义（Binary 恒为 0）。
    /// 计数值一旦被 Signal 拉高就不会回退（Wait 不会减它）。
    /// </summary>
    public readonly ulong InitialValue;

    private readonly Semaphore handle;

    internal VKSemaphore(VKLogicalDevice parent, Semaphore handle, SemaphoreType type, ulong initialValue)
    {
        Parent = parent;
        this.handle = handle;
        Type = type;
        InitialValue = initialValue;
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 VKLogicalDevice.CreateSemaphore 里）。
    /// </summary>
    public bool Valid => Parent.IsSemaphoreOfThis(this);

    /// <summary>是不是时间线信号量 —— Signal / Wait / GetCounterValue 只有它能用。</summary>
    public bool IsTimeline => Type == SemaphoreType.Timeline;

    private Semaphore CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The semaphore is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：和别的包装类一样，把"从父对象集合里摘掉 + 调 vkDestroySemaphore"交给父对象做。
    /// 教程提醒：销毁之前要保证没有在途的提交还等着 / 置位它（一般就在 QueuedSubmit / Present 之后、
    /// 且已经 WaitForFences 过一遍的时候销毁）。
    /// </summary>
    public void Destroy()
        => Parent.DestroySemaphore(this, CheckedHandle);

    /// <summary>参数是裸句柄（Semaphore），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(Semaphore handleForCheck)
        => handleForCheck.Handle == handle.Handle;

    /// <summary>
    /// 教程没讲的 vkSignalSemaphore：把时间线信号量的计数值抬到 value（必须比当前值大，否则是无效用法）。
    /// 它是"主机侧置位"，和提交里的 pSignalSemaphores（GPU 侧置位）是两条路 ——
    /// 教程那两条二进制信号量用不到它，所以这里对 Binary 直接报错而不是发给驱动。
    /// 返回的 Result 原样交给调用方（和 AcquireNextImage / Present 一个态度）。
    /// </summary>
    public Result Signal(ulong value)
    {
        if (!IsTimeline)
        {
            throw new InvalidOperationException(
                "vkSignalSemaphore can only be used on a timeline semaphore.");
        }

        return Parent.Semaphore_Signal(this, CheckedHandle, value);
    }

    /// <summary>
    /// vkWaitSemaphores：等这个时间线信号量的计数值 >= value。
    /// timeout 用 ulong.MaxValue（也就是教程里的 UINT64_MAX）表示一直等；
    /// 超时返回 Result.Timeout，和别的失败一样由调用方决定怎么办。
    /// 注意它等的是"计数值达到"，不会把它减掉 —— 想表达"这一段工作做完了"得自己选一个合适的 value
    ///（典型的做法是每次提交前 +1，用递增值当"批次号"）。
    /// </summary>
    public Result Wait(ulong value, ulong timeout = ulong.MaxValue)
    {
        if (!IsTimeline)
        {
            throw new InvalidOperationException(
                "vkWaitSemaphores can only be used on a timeline semaphore.");
        }

        return Parent.Semaphore_Wait(this, CheckedHandle, value, timeout);
    }

    /// <summary>
    /// vkGetSemaphoreCounterValue：读当前计数值（不会等待，也不会改动它）。
    /// 同样是时间线信号量专属；Result 原样返回，成功时 value 里是计数值。
    /// </summary>
    public Result GetCounterValue(out ulong value)
    {
        if (!IsTimeline)
        {
            throw new InvalidOperationException(
                "vkGetSemaphoreCounterValue can only be used on a timeline semaphore.");
        }

        return Parent.Semaphore_GetCounterValue(this, CheckedHandle, out value);
    }
}

public partial class VKLogicalDevice
{
    
    #region 同步对象（教程《Rendering and presentation》/《Frames in flight》章节）


    // 教程《Rendering and presentation》/《Frames in flight》里的那两套同步对象。
    // 写法和上面一样：创建时登记进来，之后靠 IsSemaphoreOfThis() / IsFenceOfThis() 反查归属
    //（VKSemaphore.Valid / VKFence.Valid 就是这么来的）。
    private readonly ConcurrentDictionary<VKSemaphore, AllocationCallbacks?> semaphores = new ();
    
    /// <summary>
    /// 教程 createSyncObjects() 里的 vkCreateSemaphore。
    ///
    /// 参数：
    ///   semaphoreType —— Binary（二进制，教程里那两条用的就是它：只能在"未触发 ↔ 已触发"之间来回）
    ///                    或 Timeline（时间线，Vulkan 1.2 核心：带一个只增不减的 64 位计数值，
    ///                    可以在主机侧 Signal / Wait / 查值 —— 见 VKSemaphore 那三个方法）；
    ///   initialValue  —— 初始计数值，只对 Timeline 有意义（Binary 恒为 0）；
    ///   其余没得选：SemaphoreCreateInfo 的 flags 现在必须为 0，也没有别的字段。
    ///
    /// 一个容易踩的 Silk.NET 坑（同 Game.cs 里那段注释）：SemaphoreCreateInfo 没有任何"有意义的"
    /// 参数可传，而 struct 的 new X() 会绑定到隐式无参构造函数、拿到全零值、**不会**去调用那个
    /// 「参数全可选」的构造函数 —— 所以 SType 必须靠显式传 sType 才能填上。
    /// Timeline 还要在 pNext 上挂一个 SemaphoreTypeCreateInfo（类型 + 初始值都写在那里）：
    /// 它只活到 vkCreateSemaphore 返回，所以指向栈上的局部变量就够（和 Game.cs 里挂 debugCreateInfo 同一招）。
    ///
    /// 照本仓库惯例：建出来 → 登记到本设备的集合里（VKSemaphore.Valid 就是反查这里）→ 返回包装类。
    /// </summary>
    public unsafe VKSemaphore CreateSemaphore(
        SemaphoreType semaphoreType = SemaphoreType.Binary,
        ulong initialValue = 0,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Device device = CheckedHandle;

        SemaphoreCreateInfo semaphoreInfo = new(sType: StructureType.SemaphoreCreateInfo);

        // 时间线信号量才需要这条（它自己才是 sType = SemaphoreTypeCreateInfo 的那个结构体）；
        // 二进制信号量不用，pNext 保持 null。
        SemaphoreTypeCreateInfo typeInfo = new(
            semaphoreType: semaphoreType,
            initialValue: initialValue);
        if (semaphoreType == SemaphoreType.Timeline)
        {
            semaphoreInfo.PNext = &typeInfo;
        }

        Semaphore handle;
        if (allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            if (VK.CreateSemaphore(device, in semaphoreInfo, &dAllocationCallbacks, out handle) != Result.Success)
            {
                throw new NotSupportedException("failed to create semaphore!");
            }
        }
        else if (VK.CreateSemaphore(device, in semaphoreInfo, null, out handle) != Result.Success)
        {
            throw new NotSupportedException("failed to create semaphore!");
        }

        VKSemaphore result = new(this, handle, semaphoreType, initialValue);
        semaphores[result] = allocationCallbacks;
        return result;
    }

    /// <summary>
    /// vkDestroySemaphore（写法同 DestroyImage / DestroySwapchain）：先校验句柄归属、摘登记，
    /// 再按创建时记下的 allocationCallbacks 调真正的销毁。
    /// </summary>
    internal unsafe bool DestroySemaphore(VKSemaphore semaphore, Semaphore handle)
    {
        Device device = CheckedHandle;

        if (!semaphore.IsHandleOfThis(handle) || !semaphores.TryRemove(semaphore, out var allocationCallbacks))
        {
            return false;
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroySemaphore(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroySemaphore(device, handle, null);
        }
        return true;
    }

    /// <summary>信号量的归属校验：本设备建出来的才算 —— VKSemaphore.Valid 就是这么来的。</summary>
    public bool IsSemaphoreOfThis(VKSemaphore semaphore)
        => semaphores.ContainsKey(semaphore) && Valid;

    /// <summary>
    /// vkSignalSemaphore（Vulkan 1.2 核心 / 原 VK_KHR_timeline_semaphore）：把时间线信号量的计数值抬到 value。
    /// "是不是时间线信号量"这个前提由 VKSemaphore.Signal 先检查，这里只负责句柄归属 + 发调用。
    /// </summary>
    internal unsafe Result Semaphore_Signal(VKSemaphore semaphore, Semaphore handle, ulong value)
    {
        Device device = CheckedHandle;

        if (!semaphore.IsHandleOfThis(handle))
        {
            throw new InvalidOperationException(
                "The semaphore handle is not valid: it is not the handle of the given VKSemaphore.");
        }

        SemaphoreSignalInfo signalInfo = new(semaphore: handle, value: value);
        return VK.SignalSemaphore(device, in signalInfo);
    }

    /// <summary>
    /// vkWaitSemaphores（Vulkan 1.2 核心）：等时间线信号量的计数值 >= value。
    /// pSemaphores / pValues 只在**这一次**调用里被读，所以指向栈上的局部变量就够
    ///（同 Game.cs 里"临时指针指向栈上的局部变量、不额外分配堆内存"的写法）。
    /// timeout 用 ulong.MaxValue 就是教程里的 UINT64_MAX（无限等），超时返回 Result.Timeout。
    /// </summary>
    internal unsafe Result Semaphore_Wait(VKSemaphore semaphore, Semaphore handle, ulong value, ulong timeout)
    {
        Device device = CheckedHandle;

        if (!semaphore.IsHandleOfThis(handle))
        {
            throw new InvalidOperationException(
                "The semaphore handle is not valid: it is not the handle of the given VKSemaphore.");
        }

        Semaphore semaphoreHandle = handle;
        ulong waitValue = value;
        SemaphoreWaitInfo waitInfo = new(
            semaphoreCount: 1,
            pSemaphores: &semaphoreHandle,
            pValues: &waitValue);

        return VK.WaitSemaphores(device, in waitInfo, timeout);
    }

    /// <summary>vkGetSemaphoreCounterValue（Vulkan 1.2 核心）：读时间线信号量的当前计数值，不等待、也不改动它。</summary>
    internal Result Semaphore_GetCounterValue(VKSemaphore semaphore, Semaphore handle, out ulong value)
    {
        Device device = CheckedHandle;

        if (!semaphore.IsHandleOfThis(handle))
        {
            throw new InvalidOperationException(
                "The semaphore handle is not valid: it is not the handle of the given VKSemaphore.");
        }

        return VK.GetSemaphoreCounterValue(device, handle, out value);
    }


    #endregion
}