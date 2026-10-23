using System.Numerics;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using SilkTry.Vulkan.ManagedStruct;

// 隐式 using 带进来的 System.Threading 里也有一个 Semaphore，和 Vulkan 的同名，必须显式指向后者
//（同 VKSemaphore.cs / VKSwapchain.cs）。
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace SilkTry.Vulkan;

/// <summary>
/// 设备队列（VkQueue）的包装 —— 教程 drawFrame() 里 vkQueueSubmit / vkQueuePresentKHR 的那一端。
///
/// 队列不是 vkCreateXxx 建出来的，而是**从逻辑设备里取出来的**（vkGetDeviceQueue，见 VKLogicalDevice 的构造函数），
/// 所以父对象是 VKLogicalDevice：队列是设备上"某个队列族里的第 i 条通道"，离开设备没有意义。
/// 归属校验走父对象的 EnabledDeviceQueues 登记表（VKLogicalDevice.IsDeviceQueueOfThis）。
///
/// 分工：vkQueueXxx 这些调用只用到队列**自己**的句柄（不需要 Device 句柄），所以真正的调用就在本类里做
///（同 VKCommandBuffer.Begin / End / Reset：自己的句柄自己用）。
/// 参数里出现的其它包装类（命令缓冲 / 信号量 / 栅栏 / 缓冲 / 图像 / 内存）的句柄，按
/// "谁的句柄类型谁在自己的类里读写它"的规矩，由各自的类当参数交下来（下面那些 internal static 的 Queue_* 链路），
/// 本类只负责拼 VkSubmitInfo / VkSubmitInfo2 / VkBindSparseInfo / VkPresentInfoKHR 并调用。
///
/// 本类覆盖的队列函数：
///   · vkQueueSubmit        —— Submit（二进制信号量 + 可选栅栏；一次调用提交一个 VkSubmitInfo）
///   · vkQueueSubmit2       —— Submit2（Vulkan 1.3 / VK_KHR_synchronization2；支持时间线信号量的计数值）
///   · vkQueueWaitIdle      —— WaitIdle（等这条队列上的活全干完）
///   · vkQueueBindSparse    —— BindSparse（稀疏资源的内存绑定，参数见 ManagedBindSparseInfo）
///   · vkQueuePresentKHR    —— Present（转给 VKSwapchain.Present，真正的呈现逻辑在那边）
///   · vkQueueBegin/End/InsertDebugUtilsLabelEXT —— BeginDebugLabel / EndDebugLabel / InsertDebugLabel
///     （需要实例启用了 VK_EXT_debug_utils；没启用就抛 NotSupportedException）
///
/// 划界线：本类不管"提交什么"（命令缓冲怎么录、什么时候重建交换链都归调用方），也不替调用方决定同步策略 ——
/// 原生调用返回的 Result 原样交出去（超时 / ErrorOutOfDateKhr / SuboptimalKhr 都是策略问题）。
/// 队列本身也没有 Destroy：vkGetDeviceQueue 取出来的队列是设备的附属物，随 vkDestroyDevice 一起消失。
/// </summary>
public partial class VKDeviceQueue
{
    public readonly uint FamilyIndex;
    public readonly VKLogicalDevice Parent;
    private readonly Queue handle;

    private readonly static Vk VK = Vk.GetApi();

    internal VKDeviceQueue(VKLogicalDevice parent, Queue handle, uint familyIndex)
    {
        Parent = parent;
        this.handle = handle;
        FamilyIndex = familyIndex;
    }

    /// <summary>
    /// 和 VKSurface.Valid 一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（父对象登记发生在 VKLogicalDevice 的构造函数里：vkGetDeviceQueue 取出来就登记）。
    /// </summary>
    public bool Valid => Parent.IsDeviceQueueOfThis(this);

    private Queue CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException(
                    "The device queue is not valid: it does not belong to the parent VKLogicalDevice.");
            }

            return handle;
        }
    }

    /// <summary>参数是裸句柄（Queue），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(Queue handleForCheck)
        => handleForCheck.Handle == handle.Handle;

    #region 提交（vkQueueSubmit / vkQueueSubmit2）

    /// <summary>
    /// 教程 drawFrame() 里的 vkQueueSubmit：把一批命令缓冲交给 GPU 执行。
    ///
    /// 参数对应 VkSubmitInfo 的字段（一次调用只提交**一个** VkSubmitInfo，够本仓库用；
    /// 一个 SubmitInfo 里可以放多个命令缓冲）：
    ///   commandBuffers    —— 要执行的命令缓冲（必须是 Primary 级别，且已经 End 过；至少一个）；
    ///   waitSemaphores    —— 执行前要等的二进制信号量（教程里是 imageAvailableSemaphore）；
    ///   waitStages        —— 与 waitSemaphores 一一对应的等待阶段；不传就统一用 AllCommandsBit，
    ///                        教程给的 ColorAttachmentOutputBit 就是"等到写颜色附件那一步再等"；
    ///   signalSemaphores  —— 执行完要置位的二进制信号量（教程里是 renderFinishedSemaphore）；
    ///   fence             —— 这一批提交整体完成的栅栏（CPU 侧等它）；null = 不需要。
    ///
    /// 时间线信号量不能用在这里：VkSubmitInfo 的 waiting / signaling 值要靠 pNext 上的
    /// VkTimelineSemaphoreSubmitInfo 传，本方法没有暴露它 —— 需要时间线信号量就用 Submit2。
    ///
    /// 调用链：本方法（校验参数形状）→ VKCommandBuffer.Queue_Submit（读命令缓冲句柄）
    ///   → VKSemaphore.Queue_Submit（读信号量句柄）→ VKFence.Queue_Submit（读栅栏句柄）
    ///   → 本类的 Queue_Submit（拼 VkSubmitInfo + vkQueueSubmit）。
    /// </summary>
    public Result Submit(
        VKCommandBuffer[] commandBuffers,
        VKSemaphore[]? waitSemaphores = null,
        PipelineStageFlags[]? waitStages = null,
        VKSemaphore[]? signalSemaphores = null,
        VKFence? fence = null)
    {
        ValidateCommandBuffers(commandBuffers);

        VKSemaphore[] waits = waitSemaphores ?? Array.Empty<VKSemaphore>();
        if (waitStages != null)
        {
            if (waits.Length == 0)
            {
                throw new ArgumentException(
                    "Wait stages are only meaningful together with wait semaphores.",
                    nameof(waitStages));
            }

            if (waitStages.Length != waits.Length)
            {
                throw new ArgumentException(
                    "The number of wait stages must match the number of wait semaphores.",
                    nameof(waitStages));
            }
        }

        PipelineStageFlags[] stages = waitStages ?? CreateDefaultWaitStages(waits.Length);

        return VKCommandBuffer.Queue_Submit(
            this,
            commandBuffers,
            waits,
            stages,
            signalSemaphores ?? Array.Empty<VKSemaphore>(),
            fence);
    }

    /// <summary>
    /// Vulkan 1.3 的 vkQueueSubmit2（实例 API &lt; 1.3 时自动改用 VK_KHR_synchronization2 的同名入口）：
    /// 功能与 Submit 一样，但信号量的**计数值**也是一等参数，所以时间线信号量能直接用。
    ///
    /// 参数对应 VkSubmitInfo2 的字段：
    ///   commandBuffers    —— 要执行的命令缓冲（同 Submit）；
    ///   waitSemaphores    —— 执行前要等的信号量；
    ///   waitStages        —— 与等待信号量一一对应的阶段（这里是 PipelineStageFlags2，1.3 的合并阶段位都在这个枚举里）；
    ///                        不传就统一用 AllCommandsBit；
    ///   waitValues        —— 与等待信号量一一对应的计数值（时间线信号量等"值达到"；二进制信号量填 0 即可，不传就是全 0）；
    ///   signalSemaphores  —— 执行完要置位的信号量；
    ///   signalValues      —— 与置位信号量一一对应的计数值（时间线信号量置位后的值；二进制信号量填 0，不传就是全 0）；
    ///   fence             —— 整批提交完成的栅栏；null = 不需要。
    ///
    /// 调用链同 Submit，只是每一环换成 Queue_Submit2，最后一环按版本 / 扩展分流：
    ///   实例 API ≥ 1.3 → Vk.QueueSubmit2；否则 → KhrSynchronization2.QueueSubmit2（要求创建设备时启用了该扩展）。
    /// </summary>
    public Result Submit2(
        VKCommandBuffer[] commandBuffers,
        VKSemaphore[]? waitSemaphores = null,
        PipelineStageFlags2[]? waitStages = null,
        ulong[]? waitValues = null,
        VKSemaphore[]? signalSemaphores = null,
        ulong[]? signalValues = null,
        VKFence? fence = null)
    {
        ValidateCommandBuffers(commandBuffers);

        VKSemaphore[] waits = waitSemaphores ?? Array.Empty<VKSemaphore>();
        VKSemaphore[] signals = signalSemaphores ?? Array.Empty<VKSemaphore>();

        if (waitStages != null && waitStages.Length != waits.Length)
        {
            throw new ArgumentException(
                "The number of wait stages must match the number of wait semaphores.",
                nameof(waitStages));
        }

        if (waitValues != null && waitValues.Length != waits.Length)
        {
            throw new ArgumentException(
                "The number of wait values must match the number of wait semaphores.",
                nameof(waitValues));
        }

        if (signalValues != null && signalValues.Length != signals.Length)
        {
            throw new ArgumentException(
                "The number of signal values must match the number of signal semaphores.",
                nameof(signalValues));
        }

        PipelineStageFlags2[] stages = waitStages ?? CreateDefaultWaitStages2(waits.Length);

        return VKCommandBuffer.Queue_Submit2(
            this,
            commandBuffers,
            waits,
            stages,
            waitValues ?? new ulong[waits.Length],
            signals,
            signalValues ?? new ulong[signals.Length],
            fence);
    }

    private static void ValidateCommandBuffers(VKCommandBuffer[] commandBuffers)
    {
        if (commandBuffers == null)
        {
            throw new ArgumentNullException(nameof(commandBuffers));
        }

        if (commandBuffers.Length == 0)
        {
            throw new ArgumentException(
                "At least one command buffer must be submitted.",
                nameof(commandBuffers));
        }
    }

    private static PipelineStageFlags[] CreateDefaultWaitStages(int count)
    {
        PipelineStageFlags[] stages = new PipelineStageFlags[count];
        for (int i = 0; i < stages.Length; i++)
        {
            stages[i] = PipelineStageFlags.AllCommandsBit;
        }

        return stages;
    }

    private static PipelineStageFlags2[] CreateDefaultWaitStages2(int count)
    {
        PipelineStageFlags2[] stages = new PipelineStageFlags2[count];
        for (int i = 0; i < stages.Length; i++)
        {
            stages[i] = PipelineStageFlags2.AllCommandsBit;
        }

        return stages;
    }

    /// <summary>
    /// 本类作为"持队列句柄者"的收口：拼 VkSubmitInfo 并调用 vkQueueSubmit。
    /// 入参里的命令缓冲 / 信号量 / 栅栏句柄已经由各自的类读出来，这里只复核队列自己有效。
    /// 之所以是 internal：入参里带的是裸句柄，按访问性约定不进 public 签名。
    /// </summary>
    internal unsafe Result Queue_Submit(
        CommandBuffer[] commandBuffers,
        Semaphore[] waitSemaphores,
        PipelineStageFlags[] waitStages,
        Semaphore[] signalSemaphores,
        Fence fence)
    {
        Queue queueHandle = CheckedHandle;

        // 空数组 fixed 得到空指针，正好对应"这一项没有"（count 也一起给 0）。
        fixed (CommandBuffer* pCommandBuffers = commandBuffers)
        fixed (Semaphore* pWaitSemaphores = waitSemaphores)
        fixed (PipelineStageFlags* pWaitStages = waitStages)
        fixed (Semaphore* pSignalSemaphores = signalSemaphores)
        {
            SubmitInfo submitInfo = new(
                waitSemaphoreCount: (uint)waitSemaphores.Length,
                pWaitSemaphores: pWaitSemaphores,
                pWaitDstStageMask: pWaitStages,
                commandBufferCount: (uint)commandBuffers.Length,
                pCommandBuffers: pCommandBuffers,
                signalSemaphoreCount: (uint)signalSemaphores.Length,
                pSignalSemaphores: pSignalSemaphores);

            return VK.QueueSubmit(queueHandle, 1, &submitInfo, fence);
        }
    }

    /// <summary>
    /// vkQueueSubmit2 的收口（版本 / 扩展分流在下面）。入参句柄同样由各自的类读好。
    /// 之所以是 internal：入参里带的是裸句柄。
    /// </summary>
    internal unsafe Result Queue_Submit2(
        CommandBuffer[] commandBuffers,
        Semaphore[] waitSemaphores,
        PipelineStageFlags2[] waitStages,
        ulong[] waitValues,
        Semaphore[] signalSemaphores,
        ulong[] signalValues,
        Fence fence)
    {
        Queue queueHandle = CheckedHandle;

        CommandBufferSubmitInfo[] commandBufferInfos = new CommandBufferSubmitInfo[commandBuffers.Length];
        for (int i = 0; i < commandBuffers.Length; i++)
        {
            commandBufferInfos[i] = new(commandBuffer: commandBuffers[i]);
        }

        SemaphoreSubmitInfo[] waitInfos = new SemaphoreSubmitInfo[waitSemaphores.Length];
        for (int i = 0; i < waitSemaphores.Length; i++)
        {
            waitInfos[i] = new(
                semaphore: waitSemaphores[i],
                value: waitValues[i],
                stageMask: waitStages[i]);
        }

        SemaphoreSubmitInfo[] signalInfos = new SemaphoreSubmitInfo[signalSemaphores.Length];
        for (int i = 0; i < signalSemaphores.Length; i++)
        {
            // 置位信号量的 stageMask 被忽略（规范要求它必须是 0），值才是时间线信号量要置到的数。
            signalInfos[i] = new(semaphore: signalSemaphores[i], value: signalValues[i]);
        }

        fixed (CommandBufferSubmitInfo* pCommandBufferInfos = commandBufferInfos)
        fixed (SemaphoreSubmitInfo* pWaitInfos = waitInfos)
        fixed (SemaphoreSubmitInfo* pSignalInfos = signalInfos)
        {
            SubmitInfo2 submitInfo = new(
                waitSemaphoreInfoCount: (uint)waitInfos.Length,
                pWaitSemaphoreInfos: pWaitInfos,
                commandBufferInfoCount: (uint)commandBufferInfos.Length,
                pCommandBufferInfos: pCommandBufferInfos,
                signalSemaphoreInfoCount: (uint)signalInfos.Length,
                pSignalSemaphoreInfos: pSignalInfos);

            if (Parent.Parent.Parent.ApiVersion >= Vk.Version13)
            {
                return VK.QueueSubmit2(queueHandle, 1, &submitInfo, fence);
            }

            if (Parent.EnabledDeviceExtensions.Contains(KhrSynchronization2.ExtensionName)
                && Parent.TryGetDeviceExtension(out KhrSynchronization2 synchronization2))
            {
                return synchronization2.QueueSubmit2(queueHandle, 1, &submitInfo, fence);
            }

            throw new NotSupportedException(
                "vkQueueSubmit2 requires Vulkan 1.3 or the VK_KHR_synchronization2 extension.");
        }
    }

    #endregion

    #region 空闲等待（vkQueueWaitIdle）

    /// <summary>
    /// vkQueueWaitIdle：阻塞到这条队列上的活**全部**干完。教程里没用到（它用栅栏做 CPU 端同步），
    /// 但"退出 / 重建资源前先等一等"时是最省事的做法 —— 代价是把整条队列都等空，不适合每帧调用。
    /// 只用队列自己的句柄，所以直接在本类里调；返回值原样交给调用方。
    /// </summary>
    public Result WaitIdle()
        => VK.QueueWaitIdle(CheckedHandle);

    #endregion

    #region 稀疏绑定（vkQueueBindSparse）

    /// <summary>
    /// vkQueueBindSparse：把稀疏资源（创建时带 SparseBindingBit 的缓冲 / 图像）的区间绑到设备内存上，
    /// 或者把某个区间解绑（ManagedSparseMemoryBind.Memory = null）。一次调用提交一个 VkBindSparseInfo。
    ///
    /// 参数：
    ///   bindInfo —— 绑定内容（见 ManagedBindSparseInfo；里面的包装类都是"借用"，不转移所有权）；
    ///   fence    —— 这批绑定完成的栅栏；null = 不需要。
    ///
    /// 注意：VkBindSparseInfo 只接受**二进制**信号量；时间线信号量会直接抛 InvalidOperationException。
    /// 另外本仓库的创建路径（VKBuffer / VKImage）都是一创建就绑内存，所以目前还建不出稀疏资源 ——
    /// 本方法先把队列这一端补齐，等创建路径支持 SparseBindingBit 之后就能直接使用。
    ///
    /// 调用链：本方法 → VKBuffer.Queue_BindSparse（读缓冲句柄）→ VKImage.Queue_BindSparse（读图像句柄）
    ///   → VKSemaphore.Queue_BindSparse（读信号量句柄）→ VKFence.Queue_BindSparse（读栅栏句柄）
    ///   → 本类的 Queue_BindSparse（拼 VkBindSparseInfo + vkQueueBindSparse）。
    /// </summary>
    public Result BindSparse(ManagedBindSparseInfo bindInfo, VKFence? fence = null)
        => VKBuffer.Queue_BindSparse(this, bindInfo, fence);

    /// <summary>
    /// vkQueueBindSparse 的收口：把各层绑定数组钉住，填好每层的 pBinds 游标，再调用。
    ///
    /// 为什么要"游标"：原生结构体里 pBinds 是指针，而托管侧给的是"每个资源一段绑定数组"。
    /// 这里把同一类的绑定先摊平成一维数组（buffer 一类、opaque 一类、image 一类），
    /// 再按每条记录的 BindCount 依次切分，避免为每一层单独 fixed。
    /// 之所以是 internal：入参里带的是裸句柄，按访问性约定不进 public 签名。
    /// </summary>
    internal unsafe Result Queue_BindSparse(
        SparseBufferMemoryBindInfo[] bufferBinds,
        SparseMemoryBind[] bufferMemoryBinds,
        SparseImageOpaqueMemoryBindInfo[] opaqueBinds,
        SparseMemoryBind[] opaqueMemoryBinds,
        SparseImageMemoryBindInfo[] imageBinds,
        SparseImageMemoryBind[] imageMemoryBinds,
        Semaphore[] waitSemaphores,
        Semaphore[] signalSemaphores,
        Fence fence)
    {
        Queue queueHandle = CheckedHandle;

        fixed (SparseBufferMemoryBindInfo* pBufferBinds = bufferBinds)
        fixed (SparseMemoryBind* pBufferMemoryBinds = bufferMemoryBinds)
        fixed (SparseImageOpaqueMemoryBindInfo* pOpaqueBinds = opaqueBinds)
        fixed (SparseMemoryBind* pOpaqueMemoryBinds = opaqueMemoryBinds)
        fixed (SparseImageMemoryBindInfo* pImageBinds = imageBinds)
        fixed (SparseImageMemoryBind* pImageMemoryBinds = imageMemoryBinds)
        fixed (Semaphore* pWaitSemaphores = waitSemaphores)
        fixed (Semaphore* pSignalSemaphores = signalSemaphores)
        {
            SparseMemoryBind* bufferCursor = pBufferMemoryBinds;
            for (int i = 0; i < bufferBinds.Length; i++)
            {
                pBufferBinds[i].PBinds = bufferCursor;
                bufferCursor += (int)pBufferBinds[i].BindCount;
            }

            SparseMemoryBind* opaqueCursor = pOpaqueMemoryBinds;
            for (int i = 0; i < opaqueBinds.Length; i++)
            {
                pOpaqueBinds[i].PBinds = opaqueCursor;
                opaqueCursor += (int)pOpaqueBinds[i].BindCount;
            }

            SparseImageMemoryBind* imageCursor = pImageMemoryBinds;
            for (int i = 0; i < imageBinds.Length; i++)
            {
                pImageBinds[i].PBinds = imageCursor;
                imageCursor += (int)pImageBinds[i].BindCount;
            }

            BindSparseInfo bindSparseInfo = new(
                waitSemaphoreCount: (uint)waitSemaphores.Length,
                pWaitSemaphores: pWaitSemaphores,
                bufferBindCount: (uint)bufferBinds.Length,
                pBufferBinds: pBufferBinds,
                imageOpaqueBindCount: (uint)opaqueBinds.Length,
                pImageOpaqueBinds: pOpaqueBinds,
                imageBindCount: (uint)imageBinds.Length,
                pImageBinds: pImageBinds,
                signalSemaphoreCount: (uint)signalSemaphores.Length,
                pSignalSemaphores: pSignalSemaphores);

            return VK.QueueBindSparse(queueHandle, 1, &bindSparseInfo, fence);
        }
    }

    #endregion

    #region 呈现（vkQueuePresentKHR）

    /// <summary>
    /// vkQueuePresentKHR：把画好的交换链图像交回窗口显示（教程 drawFrame() 的最后一步）。
    /// 真正的实现和维护都在 VKSwapchain 那边（它要读自己的交换链句柄和 KHR_swapchain 扩展），
    /// 这里只是把"从队列这边发起"的入口补上：队列句柄由本类自己交出去，交换链 / 信号量句柄由它们的类交。
    ///
    /// 注意呈现必须用**呈现队列**（可能和图形队列不是同一条）；返回值同样原样交出去
    ///（ErrorOutOfDateKhr / SuboptimalKhr 由调用方决定怎么处理）。
    /// </summary>
    public Result Present(VKSwapchain swapchain, uint imageIndex, VKSemaphore waitSemaphore)
        => swapchain.Present(this, imageIndex, waitSemaphore);

    #endregion

    #region 调试标签（vkQueueXxxDebugUtilsLabelEXT）

    /// <summary>
    /// vkQueueBeginDebugUtilsLabelEXT：给这条队列开一个调试标签区域（GPU 调试器 / RGP / Nsight 会按它分组显示）。
    /// 必须和 EndDebugLabel 配对（可以嵌套，按栈的顺序配对）。
    ///
    /// label —— 显示的名字；color —— 标签颜色（RGBA，0~1），不传用白色。
    /// 这些入口属于 VK_EXT_debug_utils —— 它是**实例**扩展，所以从 VKInstance 那边取；
    /// 实例没启用（甚至没提供）这个扩展时抛 NotSupportedException。
    /// </summary>
    public unsafe void BeginDebugLabel(string label, Vector4? color = null)
    {
        Queue queueHandle = CheckedHandle;
        ExtDebugUtils debugUtils = CheckedDebugUtils;

        InvokeWithDebugLabel(label, color, labelInfo =>
            debugUtils.QueueBeginDebugUtilsLabel(queueHandle, labelInfo));
    }

    /// <summary>
    /// vkQueueInsertDebugUtilsLabelEXT：在队列的时间线上打一个瞬时标记（不开启区域，只插一个点）。
    /// 参数与限制同 BeginDebugLabel。
    /// </summary>
    public unsafe void InsertDebugLabel(string label, Vector4? color = null)
    {
        Queue queueHandle = CheckedHandle;
        ExtDebugUtils debugUtils = CheckedDebugUtils;

        InvokeWithDebugLabel(label, color, labelInfo =>
            debugUtils.QueueInsertDebugUtilsLabel(queueHandle, labelInfo));
    }

    /// <summary>
    /// vkQueueEndDebugUtilsLabelEXT：结束最近一次 BeginDebugLabel 打开的标签区域。
    /// 没有配对的 Begin 时由驱动 / 校验层报错（这里不做栈记录）。
    /// </summary>
    public void EndDebugLabel()
        => CheckedDebugUtils.QueueEndDebugUtilsLabel(CheckedHandle);

    private ExtDebugUtils CheckedDebugUtils
    {
        get
        {
            if (!Parent.Parent.Parent.TryGetInstanceExtension(out ExtDebugUtils debugUtils))
            {
                throw new NotSupportedException("VK_EXT_debug_utils extension not found.");
            }

            return debugUtils;
        }
    }

    /// <summary>把"拼好的标签结构体"交给具体的 EXT 入口（Begin / Insert）。</summary>
    private unsafe delegate void DebugLabelInvoker(DebugUtilsLabelEXT* labelInfo);

    /// <summary>
    /// 把托管字符串 + 可选颜色拼成 VkDebugUtilsLabelEXT，调完原生入口后由 finally 释放标签名内存 ——
    /// 驱动只在调用期间读这个名字，所以 free 必须发生在**回调之后**（不能提前返回结构体）。
    /// </summary>
    private static unsafe void InvokeWithDebugLabel(
        string label,
        Vector4? color,
        DebugLabelInvoker invoke)
    {
        if (label == null)
        {
            throw new ArgumentNullException(nameof(label));
        }

        nint pLabelName = SilkMarshal.StringToPtr(label);
        try
        {
            DebugUtilsLabelEXT labelInfo = new(pLabelName: (byte*)pLabelName);

            Vector4 labelColor = color ?? Vector4.One;
            labelInfo.Color[0] = labelColor.X;
            labelInfo.Color[1] = labelColor.Y;
            labelInfo.Color[2] = labelColor.Z;
            labelInfo.Color[3] = labelColor.W;

            invoke(&labelInfo);
        }
        finally
        {
            SilkMarshal.Free(pLabelName);
        }
    }

    #endregion
}

public partial class VKCommandBuffer
{
    /// <summary>
    /// vkQueueSubmit 链路的其中一环：命令缓冲句柄只有本类读得到，所以由本类把
    /// VKCommandBuffer[] 转成裸 CommandBuffer[]（顺带校验"同一个逻辑设备 + Primary 级别"），
    /// 再交给信号量那一环。
    /// 之所以是 internal static：入参里有包装类数组，且局部数组带裸句柄，按访问性约定不出包装层。
    /// </summary>
    internal static Result Queue_Submit(
        VKDeviceQueue queue,
        VKCommandBuffer[] commandBuffers,
        VKSemaphore[] waitSemaphores,
        PipelineStageFlags[] waitStages,
        VKSemaphore[] signalSemaphores,
        VKFence? fence)
        => VKSemaphore.Queue_Submit(
            queue,
            ToQueueCommandBufferHandles(queue, commandBuffers),
            waitSemaphores,
            waitStages,
            signalSemaphores,
            fence);

    /// <summary>Submit2 的同名一环（规则同 Queue_Submit）。</summary>
    internal static Result Queue_Submit2(
        VKDeviceQueue queue,
        VKCommandBuffer[] commandBuffers,
        VKSemaphore[] waitSemaphores,
        PipelineStageFlags2[] waitStages,
        ulong[] waitValues,
        VKSemaphore[] signalSemaphores,
        ulong[] signalValues,
        VKFence? fence)
        => VKSemaphore.Queue_Submit2(
            queue,
            ToQueueCommandBufferHandles(queue, commandBuffers),
            waitSemaphores,
            waitStages,
            waitValues,
            signalSemaphores,
            signalValues,
            fence);

    /// <summary>
    /// 转句柄 + 校验：每个命令缓冲都得属于队列所在的那个逻辑设备，而且必须是 Primary
    ///（Vulkan 只允许把 Primary 命令缓冲直接提交到队列；Secondary 得由 vkCmdExecuteCommands 调用）。
    /// </summary>
    private static CommandBuffer[] ToQueueCommandBufferHandles(
        VKDeviceQueue queue,
        VKCommandBuffer[] commandBuffers)
    {
        CommandBuffer[] handles = new CommandBuffer[commandBuffers.Length];
        for (int i = 0; i < commandBuffers.Length; i++)
        {
            VKCommandBuffer commandBuffer = commandBuffers[i] ?? throw new ArgumentException(
                $"The command buffer at index {i} is null.");

            // 命令缓冲的 Parent 是命令池，池的 Parent 才是逻辑设备。
            if (commandBuffer.Parent.Parent != queue.Parent)
            {
                throw new ArgumentException(
                    $"The command buffer at index {i} is not from the logical device that owns the queue.");
            }

            if (commandBuffer.Level != CommandBufferLevel.Primary)
            {
                throw new ArgumentException(
                    $"The command buffer at index {i} is not a primary command buffer.");
            }

            handles[i] = commandBuffer.CheckedHandle;
        }

        return handles;
    }
}

public partial class VKSemaphore
{
    /// <summary>
    /// vkQueueSubmit 链路的其中一环：信号量句柄由本类读出来（Submit 只接受二进制信号量），
    /// 再把栅栏那一环接上。
    /// 之所以是 internal static：入参 / 返回值里带裸句柄，按访问性约定不出包装层。
    /// </summary>
    internal static Result Queue_Submit(
        VKDeviceQueue queue,
        CommandBuffer[] commandBuffers,
        VKSemaphore[] waitSemaphores,
        PipelineStageFlags[] waitStages,
        VKSemaphore[] signalSemaphores,
        VKFence? fence)
        => VKFence.Queue_Submit(
            queue,
            commandBuffers,
            Queue_SemaphoreHandles(queue, waitSemaphores, allowTimeline: false, "vkQueueSubmit"),
            waitStages,
            Queue_SemaphoreHandles(queue, signalSemaphores, allowTimeline: false, "vkQueueSubmit"),
            fence);

    /// <summary>vkQueueSubmit2 的同名一环：允许时间线信号量（计数值由 Submit2 的参数给）。</summary>
    internal static Result Queue_Submit2(
        VKDeviceQueue queue,
        CommandBuffer[] commandBuffers,
        VKSemaphore[] waitSemaphores,
        PipelineStageFlags2[] waitStages,
        ulong[] waitValues,
        VKSemaphore[] signalSemaphores,
        ulong[] signalValues,
        VKFence? fence)
        => VKFence.Queue_Submit2(
            queue,
            commandBuffers,
            Queue_SemaphoreHandles(queue, waitSemaphores, allowTimeline: true, "vkQueueSubmit2"),
            waitStages,
            waitValues,
            Queue_SemaphoreHandles(queue, signalSemaphores, allowTimeline: true, "vkQueueSubmit2"),
            signalValues,
            fence);

    /// <summary>
    /// vkQueueBindSparse 的一环：只接受二进制信号量（VkBindSparseInfo 没有计数值的位置）。
    /// </summary>
    internal static Result Queue_BindSparse(
        VKDeviceQueue queue,
        SparseBufferMemoryBindInfo[] bufferBinds,
        SparseMemoryBind[] bufferMemoryBinds,
        SparseImageOpaqueMemoryBindInfo[] opaqueBinds,
        SparseMemoryBind[] opaqueMemoryBinds,
        SparseImageMemoryBindInfo[] imageBinds,
        SparseImageMemoryBind[] imageMemoryBinds,
        ManagedBindSparseInfo bindInfo,
        VKFence? fence)
        => VKFence.Queue_BindSparse(
            queue,
            bufferBinds,
            bufferMemoryBinds,
            opaqueBinds,
            opaqueMemoryBinds,
            imageBinds,
            imageMemoryBinds,
            Queue_SemaphoreHandles(
                queue,
                bindInfo.WaitSemaphores ?? Array.Empty<VKSemaphore>(),
                allowTimeline: false,
                "vkQueueBindSparse"),
            Queue_SemaphoreHandles(
                queue,
                bindInfo.SignalSemaphores ?? Array.Empty<VKSemaphore>(),
                allowTimeline: false,
                "vkQueueBindSparse"),
            fence);

    /// <summary>
    /// 转句柄 + 校验：同一逻辑设备、非 null、按需拒绝时间线信号量。
    /// operation 只用来拼错误消息（让调用方知道是哪条队列函数拒了它）。
    /// </summary>
    private static Semaphore[] Queue_SemaphoreHandles(
        VKDeviceQueue queue,
        VKSemaphore[] semaphores,
        bool allowTimeline,
        string operation)
    {
        if (semaphores.Length == 0)
        {
            return Array.Empty<Semaphore>();
        }

        Semaphore[] handles = new Semaphore[semaphores.Length];
        for (int i = 0; i < semaphores.Length; i++)
        {
            VKSemaphore semaphore = semaphores[i] ?? throw new ArgumentException(
                $"{operation} received a null semaphore at index {i}.");

            if (semaphore.Parent != queue.Parent)
            {
                throw new ArgumentException(
                    $"{operation} received a semaphore at index {i} that is not from the logical device that owns the queue.");
            }

            if (!allowTimeline && semaphore.IsTimeline)
            {
                throw new InvalidOperationException(
                    $"{operation} does not support timeline semaphores.");
            }

            handles[i] = semaphore.CheckedHandle;
        }

        return handles;
    }
}

public partial class VKFence
{
    /// <summary>
    /// vkQueueSubmit 链路的最后一环：栅栏句柄由本类读出来（null 就是"不要栅栏"），
    /// 然后交给队列去拼结构体并调用。之所以是 internal static：入参里带裸句柄。
    /// </summary>
    internal static Result Queue_Submit(
        VKDeviceQueue queue,
        CommandBuffer[] commandBuffers,
        Semaphore[] waitSemaphores,
        PipelineStageFlags[] waitStages,
        Semaphore[] signalSemaphores,
        VKFence? fence)
        => queue.Queue_Submit(
            commandBuffers,
            waitSemaphores,
            waitStages,
            signalSemaphores,
            Queue_FenceHandle(queue, fence));

    /// <summary>vkQueueSubmit2 的同名最后一环。</summary>
    internal static Result Queue_Submit2(
        VKDeviceQueue queue,
        CommandBuffer[] commandBuffers,
        Semaphore[] waitSemaphores,
        PipelineStageFlags2[] waitStages,
        ulong[] waitValues,
        Semaphore[] signalSemaphores,
        ulong[] signalValues,
        VKFence? fence)
        => queue.Queue_Submit2(
            commandBuffers,
            waitSemaphores,
            waitStages,
            waitValues,
            signalSemaphores,
            signalValues,
            Queue_FenceHandle(queue, fence));

    /// <summary>vkQueueBindSparse 的同名最后一环。</summary>
    internal static Result Queue_BindSparse(
        VKDeviceQueue queue,
        SparseBufferMemoryBindInfo[] bufferBinds,
        SparseMemoryBind[] bufferMemoryBinds,
        SparseImageOpaqueMemoryBindInfo[] opaqueBinds,
        SparseMemoryBind[] opaqueMemoryBinds,
        SparseImageMemoryBindInfo[] imageBinds,
        SparseImageMemoryBind[] imageMemoryBinds,
        Semaphore[] waitSemaphores,
        Semaphore[] signalSemaphores,
        VKFence? fence)
        => queue.Queue_BindSparse(
            bufferBinds,
            bufferMemoryBinds,
            opaqueBinds,
            opaqueMemoryBinds,
            imageBinds,
            imageMemoryBinds,
            waitSemaphores,
            signalSemaphores,
            Queue_FenceHandle(queue, fence));

    /// <summary>
    /// 把可空栅栏换成裸句柄：null → default（VK_NULL_HANDLE，表示不要栅栏）；
    /// 非 null 时校验它和队列属于同一个逻辑设备，再去读它的 CheckedHandle。
    /// </summary>
    private static Fence Queue_FenceHandle(VKDeviceQueue queue, VKFence? fence)
    {
        if (fence == null)
        {
            return default;
        }

        if (fence.Parent != queue.Parent)
        {
            throw new ArgumentException(
                "The provided fence is not from the logical device that owns the queue.");
        }

        return fence.CheckedHandle;
    }
}

public partial class VKBuffer
{
    /// <summary>
    /// vkQueueBindSparse 链路的其中一环：缓冲句柄由本类读出来，每条绑定里的内存句柄
    /// 交给 VKDeviceMemory 转（内存句柄只有它读得到）。转完把"缓冲 → 绑定数组"的对应关系
    /// 交给图像那一环。
    /// 之所以是 internal static：入参 / 局部数组里带裸句柄，按访问性约定不出包装层。
    /// </summary>
    internal static unsafe Result Queue_BindSparse(
        VKDeviceQueue queue,
        ManagedBindSparseInfo bindInfo,
        VKFence? fence)
    {
        ManagedSparseBufferMemoryBindInfo[] managedBinds =
            bindInfo.BufferBinds ?? Array.Empty<ManagedSparseBufferMemoryBindInfo>();

        SparseBufferMemoryBindInfo[] bufferBinds = new SparseBufferMemoryBindInfo[managedBinds.Length];
        List<SparseMemoryBind> memoryBinds = new();

        for (int i = 0; i < managedBinds.Length; i++)
        {
            VKBuffer buffer = managedBinds[i].Buffer ?? throw new ArgumentException(
                $"A sparse buffer bind at index {i} has no buffer.",
                nameof(bindInfo));

            // 缓冲的 Parent 是内存，内存的 Parent 才是逻辑设备。
            if (buffer.Parent.Parent != queue.Parent)
            {
                throw new ArgumentException(
                    $"The buffer at index {i} is not from the logical device that owns the queue.",
                    nameof(bindInfo));
            }

            SparseMemoryBind[] binds = VKDeviceMemory.Sparse_MemoryBinds(
                managedBinds[i].Binds ?? Array.Empty<ManagedSparseMemoryBind>());

            bufferBinds[i] = new(buffer: buffer.CheckedHandle, bindCount: (uint)binds.Length);
            memoryBinds.AddRange(binds);
        }

        return VKImage.Queue_BindSparse(queue, bufferBinds, memoryBinds.ToArray(), bindInfo, fence);
    }
}

public partial class VKImage
{
    /// <summary>
    /// vkQueueBindSparse 链路的其中一环：图像句柄由本类读出来。
    /// 两类图像绑定都转成原生结构体（opaque 用 VkSparseMemoryBind，区域绑定用 VkSparseImageMemoryBind），
    /// 内存句柄同样交给 VKDeviceMemory 转。转完接上信号量那一环。
    /// 之所以是 internal static：入参 / 局部数组里带裸句柄，按访问性约定不出包装层。
    /// </summary>
    internal static unsafe Result Queue_BindSparse(
        VKDeviceQueue queue,
        SparseBufferMemoryBindInfo[] bufferBinds,
        SparseMemoryBind[] bufferMemoryBinds,
        ManagedBindSparseInfo bindInfo,
        VKFence? fence)
    {
        ManagedSparseImageOpaqueMemoryBindInfo[] managedOpaqueBinds =
            bindInfo.ImageOpaqueBinds ?? Array.Empty<ManagedSparseImageOpaqueMemoryBindInfo>();

        SparseImageOpaqueMemoryBindInfo[] opaqueBinds = new SparseImageOpaqueMemoryBindInfo[managedOpaqueBinds.Length];
        List<SparseMemoryBind> opaqueMemoryBinds = new();

        for (int i = 0; i < managedOpaqueBinds.Length; i++)
        {
            VKImage image = managedOpaqueBinds[i].Image ?? throw new ArgumentException(
                $"A sparse image opaque bind at index {i} has no image.",
                nameof(bindInfo));

            if (image.Parent.Parent != queue.Parent)
            {
                throw new ArgumentException(
                    $"The image at index {i} is not from the logical device that owns the queue.",
                    nameof(bindInfo));
            }

            SparseMemoryBind[] binds = VKDeviceMemory.Sparse_MemoryBinds(
                managedOpaqueBinds[i].Binds ?? Array.Empty<ManagedSparseMemoryBind>());

            opaqueBinds[i] = new(image: image.CheckedHandle, bindCount: (uint)binds.Length);
            opaqueMemoryBinds.AddRange(binds);
        }

        ManagedSparseImageMemoryBindInfo[] managedImageBinds =
            bindInfo.ImageBinds ?? Array.Empty<ManagedSparseImageMemoryBindInfo>();

        SparseImageMemoryBindInfo[] imageBinds = new SparseImageMemoryBindInfo[managedImageBinds.Length];
        List<SparseImageMemoryBind> imageMemoryBinds = new();

        for (int i = 0; i < managedImageBinds.Length; i++)
        {
            VKImage image = managedImageBinds[i].Image ?? throw new ArgumentException(
                $"A sparse image bind at index {i} has no image.",
                nameof(bindInfo));

            if (image.Parent.Parent != queue.Parent)
            {
                throw new ArgumentException(
                    $"The image at index {i} is not from the logical device that owns the queue.",
                    nameof(bindInfo));
            }

            SparseImageMemoryBind[] binds = VKDeviceMemory.Sparse_ImageMemoryBinds(
                managedImageBinds[i].Binds ?? Array.Empty<ManagedSparseImageMemoryBind>());

            imageBinds[i] = new(image: image.CheckedHandle, bindCount: (uint)binds.Length);
            imageMemoryBinds.AddRange(binds);
        }

        return VKSemaphore.Queue_BindSparse(
            queue,
            bufferBinds,
            bufferMemoryBinds,
            opaqueBinds,
            opaqueMemoryBinds.ToArray(),
            imageBinds,
            imageMemoryBinds.ToArray(),
            bindInfo,
            fence);
    }
}

public partial class VKDeviceMemory
{
    /// <summary>
    /// 把托管版的 VkSparseMemoryBind 数组转成原生结构体数组：内存句柄由本类读出来
    ///（null 就是 VK_NULL_HANDLE，表示解绑）。缓冲绑定和图像 opaque 绑定共用这个转换。
    /// 之所以是 internal：返回的数组里带裸句柄，按访问性约定不出包装层。
    /// </summary>
    internal static SparseMemoryBind[] Sparse_MemoryBinds(ManagedSparseMemoryBind[] binds)
    {
        SparseMemoryBind[] result = new SparseMemoryBind[binds.Length];
        for (int i = 0; i < binds.Length; i++)
        {
            ManagedSparseMemoryBind bind = binds[i];
            result[i] = new(
                resourceOffset: bind.ResourceOffset,
                size: bind.Size,
                memory: bind.Memory != null ? bind.Memory.CheckedHandle : default,
                memoryOffset: bind.MemoryOffset,
                flags: bind.Flags);
        }

        return result;
    }

    /// <summary>
    /// 把托管版的 VkSparseImageMemoryBind 数组转成原生结构体数组（规则同 Sparse_MemoryBinds）。
    /// </summary>
    internal static SparseImageMemoryBind[] Sparse_ImageMemoryBinds(ManagedSparseImageMemoryBind[] binds)
    {
        SparseImageMemoryBind[] result = new SparseImageMemoryBind[binds.Length];
        for (int i = 0; i < binds.Length; i++)
        {
            ManagedSparseImageMemoryBind bind = binds[i];
            result[i] = new(
                subresource: bind.Subresource,
                offset: bind.Offset,
                extent: bind.Extent,
                memory: bind.Memory != null ? bind.Memory.CheckedHandle : default,
                memoryOffset: bind.MemoryOffset,
                flags: bind.Flags);
        }

        return result;
    }
}
