using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using SilkTry.Vulkan.ManagedStruct;

namespace SilkTry.Vulkan;

/// <summary>
/// 着色器模块（教程《Shader modules》章节）的包装。
/// 着色器模块就是"一段已经编译成 SPIR-V 的字节码"在 Vulkan 里的载体：创建时把字节数组交给驱动，
/// 之后它就被图形管线里的"着色器阶段"引用（见 ManagedPipelineShaderStage）。
/// 教程特意说过：**模块不是着色器本身**，同一个模块可以被多个管线 / 多个阶段复用；
/// 反过来，模块必须活得比用到它的管线久（所以 VKShaderPipeline 里存着模块的引用，
/// 销毁顺序是"先管线、后模块"）。
///
/// 归属：vkCreateShaderModule / vkDestroyShaderModule 都是设备级调用，所以父对象是 VKLogicalDevice
///（和 VKRenderPass / VKFramebuffer 一样：创建时登记到父对象集合里，Valid 靠反查父对象登记过没有）。
///
/// 加载来源：按用户要求，创建入口直接收 **byte[]**（教程里是 readFile() 读 .spv 再塞进 codeSize / pCode）。
/// 本类不负责"从文件读字节"这种事 —— 那是调用方的策略（Game.cs 里就是 File.ReadAllBytes），
/// 包装层只做"把这段字节交给驱动"。
///
/// 分工（本仓库的句柄流转约定：**任何对象都不把自己的句柄交出去，谁的句柄类型谁就在自己的类里读写它**）：
///   · 创建：就在 VKLogicalDevice.CreateShaderModule 里做 —— 它的参数只有 Device 句柄和一段 byte[]，
///     没有"包装类数组"，不需要谁来读别的实例的私有句柄，所以普通实例方法就够
///     （同 CreateCommandPool / CreateImage；**static 只在参数里带包装类数组时才用**，见下面那句 CreateGraphicsPipeline）；
///   · 销毁：Destroy() → VKLogicalDevice.DestroyShaderModule（摘登记 + vkDestroyShaderModule）。
///
/// 划界线：vkCreateGraphicsPipelines 虽然产出的不是 ShaderModule 句柄，但它的 pStages 里每个阶段都握着
/// **本类实例的私有句柄**（参数里带的正是"着色器模块数组"），按"谁的句柄类型，谁就在自己的类里读写它"，
/// 那句调用只能由本类来做（而且要 static —— 同 VKImageView.CreateFramebuffer）——
/// 所以本类里还收了 CreateGraphicsPipeline 这个收口（详见方法上的注释）。
///
/// 另外本类还提供"从自己的 SPIR-V 里读出顶点输入属性"的入口（GetVertexInputAttributes）：
/// 模块存着那段字节，由它来说"我声明了哪些 location、什么格式"最自然。
/// </summary>
public partial class VKShaderModule
{
    /// <summary>父对象：这个模块是"哪个逻辑设备"建的。销毁、归属校验都走它。</summary>
    public readonly VKLogicalDevice Parent;

    /// <summary>
    /// 创建时喂进去的那段 SPIR-V 字节码（原样留一份，方便调试 / 以后拿去做管线缓存）。
    /// 注意：驱动在建模块时会**自己留一份拷贝**，所以这里留不留都不影响 Vulkan 那边的行为。
    /// </summary>
    public readonly ReadOnlyCollection<byte> Code;

    /// <summary>创建时的 flags（目前 Vulkan 里没有用得上的标志位，留着对应 create-info 的字段）。</summary>
    public readonly ShaderModuleCreateFlags Flags;

    private readonly ShaderModule handle;
    private readonly static Vk VK = Vk.GetApi();

    internal VKShaderModule(
        VKLogicalDevice parent,
        ShaderModule handle,
        byte[] code,
        ShaderModuleCreateFlags flags)
    {
        Parent = parent;
        this.handle = handle;
        Code = new ReadOnlyCollection<byte>(code);
        Flags = flags;
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 VKLogicalDevice.CreateShaderModule 里）。
    /// </summary>
    public bool Valid => Parent.IsShaderModuleOfThis(this);

    private ShaderModule CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The shader module is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：和别的包装类一样，把"从父对象集合里摘掉 + 调 vkDestroyShaderModule"交给父对象做。
    /// 注意顺序：用到本模块的管线必须先销毁（Vulkan 要求）；销毁 VKLogicalDevice 时会先销毁管线。
    /// </summary>
    public void Destroy()
        => Parent.DestroyShaderModule(this, CheckedHandle);

    /// <summary>参数是裸句柄（ShaderModule），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(ShaderModule handleForCheck)
        => handleForCheck.Handle == handle.Handle;

    /// <summary>
    /// 从本模块的 SPIR-V 里**反射出它声明的顶点输入属性** —— 也就是教程《Vertex buffer creation》里
    /// 手抄的那几条 VkVertexInputAttributeDescription，做完可以直接塞进
    /// ManagedGraphicsPipelineState.VertexAttributes。
    ///
    /// 读出来的三样东西：
    ///   location —— SPIR-V 里那个变量的 Location 装饰（就是 `layout(location = 0) in ...` 那个 0）；
    ///   format   —— 从变量的类型推出来：float / vec2 / vec3 / vec4 → R32Sfloat 系列，
    ///               有符号 / 无符号整型 → R32Sint / R32Uint 系列（目前只支持 32 位）；
    ///   offset   —— 按"紧凑排布"累加（第 1 个是 0，后面依次接上）—— 正是教程里
    ///               "stride = 各属性大小之和、offset 依次相加"的写法，返回值按 location 升序排好。
    ///
    /// binding 由调用方给（顶点属性本身不知道自己来自哪条顶点缓冲，教程只有一条，所以默认 0）。
    /// 顶点之间有对齐要求（比如 vec3 后面补 4 字节）时，拿到数组后自己改 offset，stride 也要相应改
    ///   —— stride 不在属性里，它在 VertexInputBindingDescription（ManagedGraphicsPipelineState.VertexBindings）。
    ///
    /// 只认**顶点入口点**声明的输入变量（OpEntryPoint 里 ExecutionModel = Vertex 的那个入口点），
    /// 所以：片元着色器的 in 变量不会被算进来；gl_VertexIndex / gl_InstanceIndex 这种带 BuiltIn 装饰的
    /// 内建输入会被跳过；不是顶点着色器的模块会直接抛 InvalidOperationException。
    ///
    /// 这个方法只读本对象存着的那段字节，不碰 Vulkan 句柄，所以也不要求 Valid。
    /// </summary>
    public VertexInputAttributeDescription[] GetVertexInputAttributes(uint binding = 0)
        => SpirvReflection.GetVertexInputAttributes(Code, binding);

    /// <summary>
    /// 没显式给颜色混合附件时用的那一条：不做混合、RGBA 四个通道都写
    ///（教程 createGraphicsPipeline() 里 colorBlendAttachment 的默认写法）。
    /// </summary>
    private static PipelineColorBlendAttachmentState[] CreateDefaultColorBlendAttachment()
        =>
        [
            new PipelineColorBlendAttachmentState(
                blendEnable: false,
                colorWriteMask: ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit)
        ];
}

public partial class VKLogicalDevice
{
    
    #region 着色器模块（教程《Shader modules》章节）


    // 着色器模块（教程《Shader modules》章节）：写法和上面一样 —— 创建时登记进来，
    // 之后靠 IsShaderModuleOfThis() 反查归属（VKShaderModule.Valid 就是这么来的）。
    // 注意：**图形管线不登记在这里** —— 管线是"那份布局的管线"，
    // 所以登记表在 VKShaderPipelineLayout 那边（VKShaderPipeline.Valid 靠 VKShaderPipelineLayout.IsShaderPipelineOfThis）。
    private readonly ConcurrentDictionary<VKShaderModule, AllocationCallbacks?> shaderModules = new ();
    
    /// <summary>
    /// 教程 createShaderModule() 里的 vkCreateShaderModule。
    /// 和 CreateImage / CreateCommandPool 一个套路：照参数建出来 → 登记到本设备的集合里
    ///（VKShaderModule.Valid 就是靠反查这里）→ 返回包装类 —— 句柄不往外漏。
    ///
    /// 加载来源按用户要求就是**直接给一段 byte[]**（教程里是 readFile() 读 .spv 的结果）：
    /// 本方法不管这段字节从哪来，只管把它交给驱动；长度必须是 4 的倍数（SPIR-V 指令按 4 字节对齐），
    /// 这一条在句柄级入口 VKShaderModule.CreateShaderModule 里检查。
    ///
    /// Vulkan 调用在 VKShaderModule 里执行（那句产出的就是它的句柄类型），本类只包装 + 登记。
    /// 注意生命周期：**模块要活得比用到它的管线久**（先建模块、后建管线；先销毁管线、后销毁模块）。
    /// </summary>
    public unsafe VKShaderModule CreateShaderModule(
        byte[] bin,
        ShaderModuleCreateFlags flags = ShaderModuleCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Device device = CheckedHandle;
        ShaderModule handle;
        fixed (byte* pBin = bin)
        {
            ShaderModuleCreateInfo shaderModuleInfo = new(
                flags: flags,
                codeSize: (nuint)bin.Length,
                pCode: (uint*)pBin);

            if (allocationCallbacks != null)
            {
                AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
                if (VK.CreateShaderModule(device, in shaderModuleInfo, &dAllocationCallbacks, out handle) != Result.Success)
                {
                    throw new NotSupportedException("failed to create shader module!");
                }
            }
            else if (VK.CreateShaderModule(device, in shaderModuleInfo, null, out handle) != Result.Success)
            {
                throw new NotSupportedException("failed to create shader module!");
            }
        }
        VKShaderModule result = new(this, handle, bin, flags);
        shaderModules[result] = allocationCallbacks;
        return result;
    }

    /// <summary>
    /// vkDestroyShaderModule（写法同 DestroyRenderPass / DestroyFramebuffer）。
    /// 注意本类只是执行销毁，具体顺序（用到它的管线必须先没）由调用方负责。
    /// </summary>
    internal unsafe bool DestroyShaderModule(VKShaderModule shaderModule, ShaderModule handle)
    {
        Device device = CheckedHandle;

        if (!shaderModule.IsHandleOfThis(handle) || !shaderModules.TryRemove(shaderModule, out var allocationCallbacks))
        {
            return false;
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyShaderModule(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyShaderModule(device, handle, null);
        }
        return true;
    }

    /// <summary>着色器模块的归属校验：本设备建出来的才算 —— VKShaderModule.Valid 就是这么来的。</summary>
    public bool IsShaderModuleOfThis(VKShaderModule shaderModule)
        => shaderModules.ContainsKey(shaderModule) && Valid;

    #endregion

}