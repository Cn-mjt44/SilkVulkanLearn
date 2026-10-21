using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Silk.NET.Core;
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace SilkTry.Vulkan;

public class VKInstance
{
    public readonly Version32 ApiVersion;
    public readonly ReadOnlyCollection<VKPhysicalDevice> PhysicalDevices;


    private readonly Instance handle;
    private readonly AllocationCallbacks? allocationCallbacks;
    private readonly ConcurrentDictionary<VKSurface, AllocationCallbacks?> surfaces = new ();

    
    private readonly static Vk VK = Vk.GetApi();

    public unsafe VKInstance(
        Version32? apiVersion = null,
        Version32? applicationVersion = null,
        Version32? engineVersion = null,
        string? applicationName = null,
        string? engineName = null,
        AllocationCallbacks? allocationCallbacks = null,
        DebugUtilsMessengerCreateInfoEXT? debugMessengerInfo = null,
        HashSet<string>? instanceExtensions = null,
        HashSet<string>? enabledLayers = null
        )
    {
        applicationName ??= "VulkanApplication";
        engineName ??= "Silk.NET";
        apiVersion ??= Vk.Version10;
        applicationVersion ??= new Version32(1, 0, 0);
        engineVersion ??= new Version32(1, 0, 0);
        int applicationNameLength = applicationName.Length + 1;
        int engineNameLength = engineName.Length + 1;
        byte* pApplicationName = stackalloc byte[applicationNameLength];
        byte* pEngineName = stackalloc byte[engineNameLength];
        Encoding.UTF8.GetBytes(applicationName, new Span<byte>(pApplicationName, applicationNameLength));
        Encoding.UTF8.GetBytes(engineName, new Span<byte>(pEngineName, engineNameLength));
        
        ApplicationInfo appInfo = new(
            pApplicationName: pApplicationName,
            applicationVersion: applicationVersion,
            pEngineName: pEngineName,
            engineVersion: engineVersion,
            apiVersion: apiVersion
        );
        InstanceCreateInfo instanceCreateInfo = new(
            pApplicationInfo: &appInfo
        );

        if(instanceExtensions != null && instanceExtensions.Count > 0)
        {
            instanceCreateInfo.EnabledExtensionCount = (uint)instanceExtensions.Count;
            instanceCreateInfo.PpEnabledExtensionNames = (byte**)SilkMarshal.StringArrayToPtr(instanceExtensions.ToArray());
        }

        if(enabledLayers != null && enabledLayers.Count > 0)
        {
            instanceCreateInfo.EnabledLayerCount = (uint)enabledLayers.Count;
            instanceCreateInfo.PpEnabledLayerNames = (byte**)SilkMarshal.StringArrayToPtr(enabledLayers.ToArray());
        }


        DebugUtilsMessengerCreateInfoEXT dDebugMessengerInfo = default;
        if(debugMessengerInfo != null && debugMessengerInfo.Value.SType == StructureType.DebugUtilsMessengerCreateInfoExt)
        {
            dDebugMessengerInfo = debugMessengerInfo.Value;
            instanceCreateInfo.PNext = &dDebugMessengerInfo;
        }

        if(allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            VK.CreateInstance(ref instanceCreateInfo, &dAllocationCallbacks, out handle);
        }
        else
        {
            VK.CreateInstance(ref instanceCreateInfo, null, out handle);
        }
        this.allocationCallbacks = allocationCallbacks;

        ApiVersion = apiVersion.Value;

        uint deviceCount = 0;
        VK.EnumeratePhysicalDevices(handle, ref deviceCount, null);

        if (deviceCount == 0)
        {
            throw new NotSupportedException("failed to find GPUs with Vulkan support!");
        }

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* devicesPtr = devices)
        {
            VK.EnumeratePhysicalDevices(handle, ref deviceCount, devicesPtr);
        }

        var vKPhysicalDevices = new VKPhysicalDevice[deviceCount];
        for (int i = 0; i < deviceCount; i++)
        {
            vKPhysicalDevices[i] = new VKPhysicalDevice(this, devices[i]);
        }
        PhysicalDevices = new ReadOnlyCollection<VKPhysicalDevice>(vKPhysicalDevices);
    }



    public bool TryGetInstanceExtension<T>(out T extensionName) where T : NativeExtension<Vk>
        => VK.TryGetInstanceExtension(handle, out extensionName);

    /// <summary>
    /// 参数里带裸句柄（Device），只在包装层内部用，所以是 internal —— 外面（Game 之类）走包装类的入口。
    /// </summary>
    internal bool TryGetDeviceExtension<T>(Device device, out T extensionName) where T : NativeExtension<Vk>
        => VK.TryGetDeviceExtension(handle, device, out extensionName);

    public unsafe VKSurface CreateWindowSurface(IVkSurface api, AllocationCallbacks? allocationCallbacks = null)
    {
        VKSurface surface;
        if(allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            surface = new VKSurface(this, api.Create(handle.ToHandle(), &dAllocationCallbacks).ToSurface());
        }
        else
        {
            surface = new VKSurface(this, api.Create<AllocationCallbacks>(handle.ToHandle(), null).ToSurface());
        }
        surfaces.TryAdd(surface, allocationCallbacks);
        return surface;
    }

    /// <summary>
    /// 参数里带裸句柄（SurfaceKHR），所以是 internal：VKSurface.Destroy() 是给外面的入口，
    /// 句柄由它自己的私有 CheckedHandle 传进来 —— 保持"CheckedHandle 不放宽"那条约定。
    /// </summary>
    internal unsafe bool DestroyWindowSurface(VKSurface surface, SurfaceKHR handle)
    {
        if(
            !surface.IsHandleOfThis(handle) ||
            !TryGetInstanceExtension(out KhrSurface khrSurface) ||
            !surfaces.TryRemove(surface, out var allocationCallbacks)
        )
        {
            return false;
        }
        if(allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            khrSurface.DestroySurface(this.handle, handle, ref dAllocationCallbacks);
        }
        else
        {
            khrSurface.DestroySurface(this.handle, handle, null);
        }
        return true;
    }

    public bool IsSurfaceOfThis(VKSurface surface)
        => surfaces.ContainsKey(surface);

    unsafe ~VKInstance()
    {
        if(allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyInstance(handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyInstance(handle, null);
        }
    }
}