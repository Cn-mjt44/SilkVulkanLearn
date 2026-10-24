using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

public partial class VKCommandPool
{
    public readonly uint FamilyIndex;
    public readonly CommandPoolCreateFlags commandPoolCreateFlags;
    public readonly VKLogicalDevice Parent;
    private readonly CommandPool handle;
    private readonly int createThreadId;

    internal VKCommandPool(VKLogicalDevice parent, CommandPool handle, uint familyIndex, CommandPoolCreateFlags commandPoolCreateFlags)
    {
        Parent = parent;
        FamilyIndex = familyIndex;
        this.commandPoolCreateFlags = commandPoolCreateFlags;
        this.handle = handle;
        createThreadId = Thread.CurrentThread.ManagedThreadId;
    }
    public bool Valid => Parent.IsCommandPoolOfThis(this);
    
    private CommandPool CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException(
                    "The physical device is not valid: it does not belong to the parent VKInstance.");
            }

            if(Thread.CurrentThread.ManagedThreadId != createThreadId)
            {
                throw new InvalidOperationException(
                    "The command pool is not accessible from the thread that created it.");
            }

            return handle;
        }
    }

    /// <summary>参数是裸句柄（CommandPool），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(CommandPool handleForCheck)
        => handleForCheck.Handle == handle.Handle;

    
    /// <summary>销毁命令池：先释放池里分配出去的全部命令缓冲，再销毁池本身。</summary>
    public void Destroy()
    {
        foreach (var commandBuffer in commandBuffers.Keys.ToArray())
        {
            try
            {
                commandBuffer.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        Parent.DestroyCommandPool(this, CheckedHandle);
    }
}


public partial class VKLogicalDevice
{
    private readonly ConcurrentDictionary<VKCommandPool, AllocationCallbacks?> commandPools = new ();
    
    public unsafe VKCommandPool CreateCommandPool(
        uint familyIndex,
        CommandPoolCreateFlags commandPoolCreateFlags,
        AllocationCallbacks? allocationCallbacks = null
    )
    {
        CommandPoolCreateInfo poolInfo = new(
            flags: commandPoolCreateFlags,
            queueFamilyIndex: familyIndex);

        CommandPool handle;
        if (allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            if (VK.CreateCommandPool(CheckedHandle, ref poolInfo, &dAllocationCallbacks, out handle) != Result.Success)
            {
                throw new NotSupportedException("failed to create command pool!");
            }
        }
        else
        {
            if (VK.CreateCommandPool(CheckedHandle, ref poolInfo, null, out handle) != Result.Success)
            {
                throw new NotSupportedException("failed to create command pool!");
            }
        }

        VKCommandPool result = new VKCommandPool(this, handle, familyIndex, commandPoolCreateFlags);
        commandPools[result] = allocationCallbacks;
        return result;
    }


    internal unsafe bool DestroyCommandPool(VKCommandPool commandPool, CommandPool handle)
    {
        if (
            !commandPool.IsHandleOfThis(handle) ||
            !commandPools.TryRemove(commandPool, out _)
        )
        {
            return false;
        }
        VK.DestroyCommandPool(CheckedHandle, handle, null);
        return true;
    }

    public bool IsCommandPoolOfThis(VKCommandPool commandPool)
        => commandPools.ContainsKey(commandPool);
}