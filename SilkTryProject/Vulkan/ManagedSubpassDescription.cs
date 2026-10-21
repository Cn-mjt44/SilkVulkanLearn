using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 子通道描述（教程《Render passes》章节的 ③）的**无指针版**。
///
/// 为什么自己写一个：Silk.NET 的 VkSubpassDescription 里全是裸指针 ——
///   pInputAttachments / pColorAttachments / pResolveAttachments / pDepthStencilAttachment /
///   pPreserveAttachments，每个还配一个 count 字段。在 C# 里这意味着调用方要 fixed、
///   或者取局部变量的地址（教程里就是 `subpass.pColorAttachments = &colorAttachmentRef;`，
///   那句在 C# 里只能写成固定语句或钉住数组），既要自己保证指针生命周期，又要自己保证
///   "长度字段和指针字段说的是同一件事"。这里把指针换成集合：集合长度就是 count，
///   集合内容就是指针指向的东西，两边不会再写歪。
///
/// 真正的转换（把集合钉住、生成裸 VkSubpassDescription）就是本类的 ToRaw()：
/// 它把上面几个 private 数组交给 <see cref="PinnedArrays"/> 钉住，然后填成 Silk.NET 的结构体，
/// 使用方是 VKLogicalDevice.CreateRenderPass。
///
/// ★ 名字提醒：本类型和 Silk.NET.Vulkan.SubpassDescription **同名**。
///   在 namespace SilkTry.Vulkan 的文件里（本仓库 Vulkan/ 下所有文件），
///   当前命名空间里的类型优先，不会歧义，直接用 SubpassDescription 就是本类型；
///   在别的命名空间里（例如 namespace SilkTry 的 Game.cs）如果两个命名空间都 using 了，
///   就会撞出 CS0104 —— 那时候用 using 别名指定要哪个（同 Game.cs 里 Semaphore 那个坑的处理办法）。
/// </summary>
public struct ManagedSubpassDescription
{
    /// <summary>子通道标志位（保留，目前没有用得上的）。</summary>
    public SubpassDescriptionFlags Flags;

    /// <summary>这个子通道是图形还是计算类型 —— 渲染通道里的常规子通道用 Graphics（教程就是这个）。</summary>
    public PipelineBindPoint PipelineBindPoint;

    /// <summary>
    /// 输入附件：着色器里当"输入附件"读的那些（延迟渲染才用得上，教程没用到）；空集合 = 没有。
    /// </summary>
    public AttachmentReference[]? InputAttachments;

    /// <summary>颜色附件：这个子通道往哪些附件写颜色（教程把第 0 号附件放在这里）。</summary>
    public AttachmentReference[]? ColorAttachments;

    /// <summary>
    /// 解析附件（开了多采样时"顺手把结果解析到哪张图"）。
    /// **null 是有含义的**：表示不做解析 —— 对应教程里的 pResolveAttachments = nullptr。
    ///（不能拿空集合代替：Vulkan 里"指针为 null"和"数组长度为 0"是两回事。）
    /// </summary>
    public AttachmentReference[]? ResolveAttachments;

    /// <summary>深度 / 模板附件；null = 这个子通道不用深度模板（教程这章就没用）。</summary>
    public AttachmentReference? DepthStencilAttachment;

    /// <summary>要保留的附件下标（子通道里用不到、但希望通道结束时别被清掉的那些）；空集合 = 没有。</summary>
    public uint[]? PreserveAttachments;

    private GCHandle inputAttachments, colorAttachments, resolveAttachments, preserveAttachments;

    public (nint InputAttachments, nint ColorAttachments, nint ResolveAttachments, nint PreserveAttachments) Pin()
    {
        inputAttachments = InputAttachments != null ? GCHandle.Alloc(InputAttachments, GCHandleType.Pinned) : default;
        colorAttachments = ColorAttachments != null ? GCHandle.Alloc(ColorAttachments, GCHandleType.Pinned) : default;
        resolveAttachments = ResolveAttachments != null ? GCHandle.Alloc(ResolveAttachments, GCHandleType.Pinned) : default;
        preserveAttachments = PreserveAttachments != null ? GCHandle.Alloc(PreserveAttachments, GCHandleType.Pinned) : default;
        return (
            inputAttachments.IsAllocated ? inputAttachments.AddrOfPinnedObject() : 0,
            colorAttachments.IsAllocated ? colorAttachments.AddrOfPinnedObject() : 0,
            resolveAttachments.IsAllocated ? resolveAttachments.AddrOfPinnedObject() : 0,
            preserveAttachments.IsAllocated ? preserveAttachments.AddrOfPinnedObject() : 0
        );
    }

    public void Unpin()
    {
        if(inputAttachments.IsAllocated)
            inputAttachments.Free();
        if(colorAttachments.IsAllocated)
            colorAttachments.Free();
        if(resolveAttachments.IsAllocated)
            resolveAttachments.Free();
        if(preserveAttachments.IsAllocated)
            preserveAttachments.Free();
        inputAttachments = default;
        colorAttachments = default;
        resolveAttachments = default;
        preserveAttachments = default;
    }
}

