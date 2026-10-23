using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

// 隐式 using 带进来的 System.Threading 里也有一个 Semaphore，和 Vulkan 的同名，必须显式指向后者
//（同 VKSwapchain.cs）。
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace SilkTry.Vulkan;

public partial class VKLogicalDevice
{
    public readonly VKPhysicalDevice Parent;
    public readonly ReadOnlyDictionary<uint, ReadOnlyCollection<VKDeviceQueue>> EnabledDeviceQueues;

    /// <summary>
    /// 创建设备时启用的设备扩展名（vkCreateDevice 的 ppEnabledExtensionNames）。
    /// 记录下来是为了回答"某个扩展到底开没开"——驱动可能支持某个扩展，但没启用就不能用它的入口
    ///（例如 <see cref="SupportsDynamicRendering"/> 要区分 1.3 核心入口和 VK_KHR_dynamic_rendering）。
    /// </summary>
    public readonly ReadOnlyCollection<string> EnabledDeviceExtensions;

    private readonly Device handle;


    
    private readonly static Vk VK = Vk.GetApi();

    internal VKLogicalDevice(
        VKPhysicalDevice parent,
        Device handle,
        Dictionary<uint, float[]> queueFamilyIndicesWithCount,
        string[]? enabledDeviceExtensions = null)
    {
        Parent = parent;
        this.handle = handle;
        EnabledDeviceExtensions = new ReadOnlyCollection<string>(enabledDeviceExtensions ?? Array.Empty<string>());
        Dictionary<uint, ReadOnlyCollection<VKDeviceQueue>> deviceQueues = new (queueFamilyIndicesWithCount.Count);
        foreach(var kvp in queueFamilyIndicesWithCount)
        {
            var subDeviceQueues = new VKDeviceQueue[kvp.Value.Length];
            for(uint i = 0; i < subDeviceQueues.Length; i++)
            {
                VK.GetDeviceQueue(handle, kvp.Key, i, out Queue deviceQueueHandle);
                subDeviceQueues[i] = new (this, deviceQueueHandle, kvp.Key);
            }
            deviceQueues.Add(kvp.Key, new (subDeviceQueues));
        }
        EnabledDeviceQueues = new (deviceQueues);
    }

    private Device CheckedHandle
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

    public bool TryGetDeviceExtension<T>(out T extensionName) where T : NativeExtension<Vk>
        => Parent.Parent.TryGetDeviceExtension(CheckedHandle, out extensionName);



    public bool IsDeviceQueueOfThis(VKDeviceQueue deviceQueue)
    {
        if(EnabledDeviceQueues.TryGetValue(deviceQueue.FamilyIndex, out var queues))
        {
            return queues.Contains(deviceQueue) && Valid;
        }
        return false;
    }

    /// <summary>
    /// 和 VKSurface.Valid 一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（父对象登记发生在 VKPhysicalDevice.CreateLogicalDevice 里）。
    /// </summary>
    public bool Valid => Parent.IsLogicalDeviceOfThis(this);



    /// <summary>
    /// 销毁设备：先销毁本设备名下的全部子对象，最后才销毁设备本身。
    /// 顺序按 Vulkan 的依赖关系排：命令池（连带命令缓冲）
    /// → 管线布局（连带图形管线）→ 描述符集布局（连带描述符池与描述符集 —— 布局是池的父对象）
    /// → 采样器（描述符集布局的不可变采样器烘进了布局里）→ 渲染通道（连带帧缓冲）
    /// → 交换链（连带图像视图）→ 内存（连带缓冲 / 图像 / 视图）→ 着色器模块 → 栅栏 → 信号量。
    /// 单个子对象销毁失败不会中断流程，剩余的仍会继续销毁（best-effort）。
    /// </summary>
    public void Destroy()
    {
        // 命令池最先：命令缓冲里录着对帧缓冲 / 渲染通道 / 管线 / 描述符集 / 顶点缓冲的引用，
        // 它们必须先被释放，后面那些对象才不是"仍在使用中"。
        foreach (var commandPool in commandPools.Keys.ToArray())
        {
            try
            {
                commandPool.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        // 管线布局会连带销毁按它建出来的图形管线；管线的销毁先于它用的渲染通道。
        foreach (var pipelineLayout in pipelineLayouts.Keys.ToArray())
        {
            try
            {
                pipelineLayout.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        // 描述符集布局被管线布局的 pSetLayouts 引用着（Vulkan 要求引用者先死），
        // 所以排在管线布局全部销毁之后。
        // 描述符池排在描述符集布局**里面**：布局的 Destroy() 先销毁名下的池（连带集），再销毁布局自己 ——
        // 命令缓冲已经先删了，所以这里不用担心"集还被录制中的命令引用"。
        foreach (var descriptorSetLayout in descriptorSetLayouts.Keys.ToArray())
        {
            try
            {
                descriptorSetLayout.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        // 采样器排在描述符集布局之后：布局里的不可变采样器虽然一建好就烘进去了，
        // 但"布局先死、采样器后死"是保守且一定安全的顺序（描述符集引用的采样器也都在它之前销毁 ——
        // 集挂在池上，池在布局的 Destroy() 里先被销毁）。
        foreach (var sampler in samplers.Keys.ToArray())
        {
            try
            {
                sampler.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        // 渲染通道会连带销毁自己的帧缓冲。
        foreach (var renderPass in renderPasses.Keys.ToArray())
        {
            try
            {
                renderPass.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        // 交换链在渲染通道之后：帧缓冲引用着交换链图像视图，必须先随渲染通道销毁。
        foreach (var swapchain in swapchains.Keys.ToArray())
        {
            try
            {
                swapchain.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        // 内存会连带销毁绑在它上面的缓冲 / 图像，图像再销毁自己的视图。
        foreach (var memory in memories.Keys.ToArray())
        {
            try
            {
                memory.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        // 着色器模块要活到用到它的图形管线销毁之后，所以排在管线布局 / 渲染通道后面。
        foreach (var shaderModule in shaderModules.Keys.ToArray())
        {
            try
            {
                shaderModule.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        foreach (var fence in fences.Keys.ToArray())
        {
            try
            {
                fence.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        foreach (var semaphore in semaphores.Keys.ToArray())
        {
            try
            {
                semaphore.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        Parent.DestroyLogicalDevice(this, CheckedHandle);
    }

    internal bool IsHandleOfThis(Device handleForCheck)
        => handleForCheck.Handle == handle.Handle;

}