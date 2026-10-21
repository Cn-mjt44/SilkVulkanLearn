using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 栅栏（教程《Rendering and presentation》的 createSyncObjects() /《Frames in flight》章节）的包装。
/// 栅栏做的是 **GPU ↔ CPU** 的同步 —— 教程拿它打的那个比方是"告诉 CPU：这一帧提交的所有命令都执行完了，
/// 你可以安全地复用这套命令缓冲和同步对象了"。所以它和信号量（GPU ↔ GPU）不能混用，
/// 教程反复强调过这一点。
///
/// 归属：vkCreateFence / vkDestroyFence 都是设备级调用，所以父对象是 VKLogicalDevice
///（和 VKCommandPool / VKSwapchain / VKRenderPass 一样：创建时登记到父对象集合里，Valid 靠反查）。
///
/// 教程里栅栏只有两个动作，都发生 "在 CPU 侧"：
///   vkWaitForFences —— 等它触发（提交完成）；
///   vkResetFences  —— 把它退回未触发（准备下一次用）。
/// 本类再加上 vkGetFenceStatus（只问状态、不等待），三个都在下面。
///
/// 访问性约定：这三个操作都要 Device 句柄，而本类读不到（private），所以真正的 Vulkan 调用放在
/// VKLogicalDevice 里（同 VKSwapchain.AcquireNextImage 的分工），本类只把自己的 CheckedHandle 转交出去，
/// 由那边用 IsHandleOfThis 复核句柄。
/// </summary>
public partial class VKFence
{
    public readonly VKLogicalDevice Parent;

    /// <summary>
    /// 创建时的 flags。教程用的是 SignaledBit：第一帧没有任何东西可以等，
    /// 如果栅栏初始是未触发状态，第一次 Wait 会永远卡住，所以创建时就让它处于已触发状态。
    /// </summary>
    public readonly FenceCreateFlags Flags;

    private readonly Fence handle;

    internal VKFence(VKLogicalDevice parent, Fence handle, FenceCreateFlags flags)
    {
        Parent = parent;
        this.handle = handle;
        Flags = flags;
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 VKLogicalDevice.CreateFence 里）。
    /// </summary>
    public bool Valid => Parent.IsFenceOfThis(this);

    private Fence CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The fence is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：和别的包装类一样，把"从父对象集合里摘掉 + 调 vkDestroyFence"交给父对象做。
    /// 教程提醒：销毁前要保证没有在途提交还在用它（先 Wait 过一遍）。
    /// </summary>
    public void Destroy()
        => Parent.DestroyFence(this, CheckedHandle);

    /// <summary>参数是裸句柄（Fence），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(Fence handleForCheck)
        => handleForCheck.Handle == handle.Handle;

    /// <summary>
    /// 教程 drawFrame() 开头的 vkWaitForFences：等这一帧的栅栏触发，也就是等上一轮用这套对象的提交结束。
    /// waitAll 固定传 true（本类只包一个栅栏，和教程一样）。
    /// timeout 用 ulong.MaxValue（教程里的 UINT64_MAX）表示无限等；超时返回 Result.Timeout，
    /// 和其他 Result 一样原样交给调用方（本类不替调用方决定策略）。
    /// </summary>
    public Result Wait(ulong timeout = ulong.MaxValue)
        => Parent.Fence_Wait(this, CheckedHandle, timeout);

    /// <summary>
    /// 教程 drawFrame() 里的 vkResetFences：把栅栏退回未触发，准备给下一次提交用。
    /// 教程特意提醒过顺序 —— 必须在"确认这一帧确实要提交"之后才 Reset：
    /// 如果在更早的地方 Reset，然后中途 return（比如交换链过期那条路），
    /// 栅栏就会永远停在未触发状态，下一帧的 Wait 会直接卡死。
    /// </summary>
    public Result Reset()
        => Parent.Fence_Reset(this, CheckedHandle);

    /// <summary>
    /// vkGetFenceStatus：只问"现在触发了没有"，不等待、也不改动它。
    /// 返回 Success 表示已触发，NotReady 表示还没触发 —— 教程说起步时它俩都不是错误，
    /// 所以 Result 原样交给调用方去分辨。
    /// </summary>
    public Result Status
        => Parent.Fence_GetStatus(this, CheckedHandle);


}

public partial class VKLogicalDevice
{
    
    private readonly ConcurrentDictionary<VKFence, AllocationCallbacks?> fences = new ();
    /// <summary>
    /// 教程 createSyncObjects() 里的 vkCreateFence。
    /// flags 不传就是 None；教程那里用的是 FenceCreateFlags.SignaledBit（第一帧没东西可等，
    /// 初始必须是已触发状态，否则第一次 Wait 永远卡住）。
    /// 注意 FenceCreateInfo 至少要传一个参数才会走「参数全可选」的构造函数把 SType 填上，
    /// 所以写成 new(flags: flags)（同上面 SemaphoreCreateInfo 那个坑）。
    ///
    /// 照本仓库惯例：建出来 → 登记到本设备的集合里（VKFence.Valid 就是反查这里）→ 返回包装类。
    /// </summary>
    public unsafe VKFence CreateFence(
        FenceCreateFlags flags = FenceCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Device device = CheckedHandle;

        FenceCreateInfo fenceInfo = new(flags: flags);

        Fence handle;
        if (allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            if (VK.CreateFence(device, in fenceInfo, &dAllocationCallbacks, out handle) != Result.Success)
            {
                throw new NotSupportedException("failed to create fence!");
            }
        }
        else if (VK.CreateFence(device, in fenceInfo, null, out handle) != Result.Success)
        {
            throw new NotSupportedException("failed to create fence!");
        }

        VKFence result = new(this, handle, flags);
        fences[result] = allocationCallbacks;
        return result;
    }

    /// <summary>vkDestroyFence（写法同 DestroySemaphore / DestroyImage）。</summary>
    internal unsafe bool DestroyFence(VKFence fence, Fence handle)
    {
        Device device = CheckedHandle;

        if (!fence.IsHandleOfThis(handle) || !fences.TryRemove(fence, out var allocationCallbacks))
        {
            return false;
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyFence(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyFence(device, handle, null);
        }
        return true;
    }

    /// <summary>栅栏的归属校验：本设备建出来的才算 —— VKFence.Valid 就是这么来的。</summary>
    public bool IsFenceOfThis(VKFence fence)
        => fences.ContainsKey(fence) && Valid;

    /// <summary>
    /// 教程 drawFrame() 的 vkWaitForFences：waitAll 固定 true（这里只等一个栅栏），
    /// timeout 用调用方给的（ulong.MaxValue = UINT64_MAX = 无限等）。
    /// </summary>
    internal unsafe Result Fence_Wait(VKFence fence, Fence handle, ulong timeout)
    {
        Device device = CheckedHandle;

        if (!fence.IsHandleOfThis(handle))
        {
            throw new InvalidOperationException(
                "The fence handle is not valid: it is not the handle of the given VKFence.");
        }

        return VK.WaitForFences(device, 1, &handle, true, timeout);
    }

    /// <summary>教程 drawFrame() 的 vkResetFences：把栅栏退回未触发（注意必须确认要提交之后再 Reset）。</summary>
    internal unsafe Result Fence_Reset(VKFence fence, Fence handle)
    {
        Device device = CheckedHandle;

        if (!fence.IsHandleOfThis(handle))
        {
            throw new InvalidOperationException(
                "The fence handle is not valid: it is not the handle of the given VKFence.");
        }

        return VK.ResetFences(device, 1, &handle);
    }

    /// <summary>
    /// vkGetFenceStatus：Success = 已触发，NotReady = 还没触发（两个都不算错误，交给调用方分辨）。
    /// </summary>
    internal Result Fence_GetStatus(VKFence fence, Fence handle)
    {
        Device device = CheckedHandle;

        if (!fence.IsHandleOfThis(handle))
        {
            throw new InvalidOperationException(
                "The fence handle is not valid: it is not the handle of the given VKFence.");
        }

        return VK.GetFenceStatus(device, handle);
    }
}