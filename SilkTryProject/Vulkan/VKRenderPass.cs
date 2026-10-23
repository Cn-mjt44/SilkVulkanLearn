using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using SilkTry.Vulkan.ManagedStruct;

namespace SilkTry.Vulkan;

/// <summary>
/// 渲染通道（教程《Render passes》章节）的包装。
/// 渲染通道描述的是"这一趟渲染要用到哪些附件、分成几个子通道、子通道之间怎么依赖" ——
/// 教程那四块（附件描述 / 附件引用 / 子通道 / 子通道依赖）里，除了"附件引用"是子通道内部的东西，
/// 另外三块都原样存在这里。附件具体是哪张图像视图**不在这里**，那是帧缓冲（《Framebuffers》章节）的事。
///
/// 归属：vkCreateRenderPass / vkDestroyRenderPass 都是设备级调用，所以父对象是 VKLogicalDevice
///（和 VKCommandPool / VKSwapchain 一样：创建时登记到父对象集合里，Valid 靠反查父对象登记过没有）。
///
/// 管线布局不归本类：vkCreatePipelineLayout 是设备级调用，布局的父对象是 VKLogicalDevice。
/// 图形管线仍然是"那份布局的管线"（登记表在 VKShaderPipelineLayout 那边）；管线要配合哪个渲染通道
/// 由调用方在 CreateGraphicsRenderPassPipeline 时传进去（见 VKShaderPipeline_RenderPass.RenderPass）。
///
/// 划界线：vkCmdBeginRenderPass / vkCmdEndRenderPass（教程《Command buffers》/《Rendering and
/// presentation》章节）是**记录到命令缓冲里**的调用，属于命令缓冲那一层，不在这里。
/// 本类只做：记住创建时定下来的那套描述、校验句柄归属、销毁，以及建管线布局（并从它建管线）。
/// 句柄照样不往外漏 —— 后面帧缓冲 / 命令缓冲要用到时，本类把自己的 CheckedHandle **当参数**往下传给
/// 对应包装类的 internal 入口（不返回、不外泄；同 VKSurface.CreateSwapchain 的分工）。
/// </summary>
public partial class VKRenderPass
{
    public readonly VKLogicalDevice Parent;

    /// <summary>附件描述（教程 ①）：格式、采样数、load / store 怎么处理、进出通道时是什么布局。</summary>
    public readonly ReadOnlyCollection<AttachmentDescription> Attachments;

    /// <summary>子通道描述（教程 ③）：本仓库的**无指针版**，见 ManagedSubpassDescription.cs。</summary>
    public readonly ReadOnlyCollection<ManagedSubpassDescription> Subpasses;

    /// <summary>子通道依赖（教程 ④）：附件在"通道外 / 子通道之间"什么时候能读、什么时候能写的先后关系。</summary>
    public readonly ReadOnlyCollection<SubpassDependency> Dependencies;

    private readonly RenderPass handle;

    private readonly static Vk VK = Vk.GetApi();

    internal VKRenderPass(
        VKLogicalDevice parent,
        RenderPass handle,
        AttachmentDescription[] attachments,
        ManagedSubpassDescription[] subpasses,
        SubpassDependency[] dependencies)
    {
        Parent = parent;
        this.handle = handle;
        Attachments = new ReadOnlyCollection<AttachmentDescription>(attachments);
        Subpasses = new ReadOnlyCollection<ManagedSubpassDescription>(subpasses);
        Dependencies = new ReadOnlyCollection<SubpassDependency>(dependencies);
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 VKLogicalDevice.CreateRenderPass 里）。
    /// </summary>
    public bool Valid => Parent.IsRenderPassOfThis(this);

    private RenderPass CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The render pass is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：先把用到本通道的帧缓冲全部销毁，
    /// 再把"从父对象集合里摘掉 + 调 vkDestroyRenderPass"交给父对象做
    ///（Vulkan 要求帧缓冲先于渲染通道销毁，否则校验层会报 vkDestroyRenderPass 那条 VUID）。
    /// </summary>
    public void Destroy()
    {
        foreach (var framebuffer in framebuffers.Keys.ToArray())
        {
            try
            {
                framebuffer.Destroy();
            }
            catch
            {
                // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
            }
        }

        Parent.DestroyRenderPass(this, CheckedHandle);
    }

    /// <summary>参数是裸句柄（RenderPass），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(RenderPass handleForCheck)
        => handleForCheck.Handle == handle.Handle;

}


public partial class VKLogicalDevice
{
    
    #region 渲染通道（教程《Render passes》章节）


    // 从这个逻辑设备上建出来的渲染通道。写法和 commandPools 一样：创建时登记进来，
    // 之后靠 IsRenderPassOfThis() 反查归属（VKRenderPass.Valid 就是这么来的）。
    private readonly ConcurrentDictionary<VKRenderPass, AllocationCallbacks?> renderPasses = new ();
    /// <summary>
    /// 教程 createRenderPass() 里的 vkCreateRenderPass。
    /// 和 CreateImage / CreateCommandPool 一个套路：照参数建出来 → 登记到本设备的集合里
    ///（VKRenderPass.Valid 就是靠反查这里）→ 返回包装类 —— 句柄不往外漏。
    ///
    /// 参数就是教程那四块里的三块：
    ///   attachments  —— 附件描述（①）：格式 / 采样数 / load·store / 布局；
    ///   subpasses    —— 子通道（③），用的是本仓库的**无指针版 SubpassDescription**
    ///                    （集合代替指针，count 与指针的配对在库这边自动处理，见 SubpassDescription.cs）；
    ///                    Vulkan 规范要求至少一个子通道，所以空数组直接抛 ArgumentException。
    ///   dependencies —— 子通道依赖（④），可以不传。
    ///
    /// 这里的钉钉子（pin）分两处，因为 Vulkan 只在本次调用里读这些结构体，建完它自己留有拷贝：
    ///   attachments / dependencies 是普通结构体数组 → 用 fixed；
    ///   子通道内部那一堆集合（每个子通道的每类附件）数量不定，fixed 写不出来，
    ///   交给 PinnedArrays 用 GCHandle 钉（SubpassDescription.ToRaw 里用的就是它）。
    /// </summary>
    public unsafe VKRenderPass CreateRenderPass(
        AttachmentDescription[] attachments,
        ManagedSubpassDescription[] subpasses,
        SubpassDependency[]? dependencies = null,
        AllocationCallbacks? allocationCallbacks = null)
    {
        Device device = CheckedHandle;

        if (subpasses == null || subpasses.Length == 0)
        {
            throw new ArgumentException("A render pass needs at least one subpass.", nameof(subpasses));
        }

        // 数组为 null / 为空时 fixed 得到的指针虽然能传，但没必要 —— 按惯例整块给 null 更清楚。

        uint attachmentCount = attachments != null ? (uint)attachments.Length : 0;
        uint dependencyCount = dependencies != null ? (uint)dependencies.Length : 0;
        SubpassDescription* pSubpasses = stackalloc SubpassDescription[subpasses.Length];
        AttachmentReference* pSubpasses_DepthStencilAttachment = stackalloc AttachmentReference[subpasses.Length];
        try
        {
            for (int i = 0; i < subpasses.Length; i++)
            {
                ref ManagedSubpassDescription subpass = ref subpasses[i];
                if (subpass.ResolveAttachments != null && subpass.ResolveAttachments.Length != subpass.ColorAttachments?.Length)
                {
                    throw new ArgumentException("The number of resolve attachments does not match the number of color attachments.");
                }
                (nint InputAttachments, nint ColorAttachments, nint ResolveAttachments, nint PreserveAttachments) = subpass.Pin();
                pSubpasses_DepthStencilAttachment[i] = subpass.DepthStencilAttachment ?? default;
                pSubpasses[i] = new SubpassDescription(
                    flags: subpass.Flags,
                    pipelineBindPoint: subpass.PipelineBindPoint,
                    inputAttachmentCount: (uint)(subpass.InputAttachments?.Length ?? 0),
                    pInputAttachments: (AttachmentReference*)InputAttachments,
                    colorAttachmentCount: (uint)(subpass.ColorAttachments?.Length ?? 0),
                    pColorAttachments: (AttachmentReference*)ColorAttachments,
                    pResolveAttachments: (AttachmentReference*)ResolveAttachments,
                    pDepthStencilAttachment: subpass.DepthStencilAttachment.HasValue ? &pSubpasses_DepthStencilAttachment[i] : null,
                    preserveAttachmentCount: (uint)(subpass.PreserveAttachments?.Length ?? 0),
                    pPreserveAttachments: (uint*)PreserveAttachments
                );
            }

            RenderPass handle;
            fixed (AttachmentDescription* pAttachments = attachments)
            fixed (SubpassDependency* pDependencies = dependencies)
            {
                RenderPassCreateInfo renderPassInfo = new(
                    attachmentCount: attachmentCount,
                    pAttachments: pAttachments,
                    subpassCount: (uint)subpasses.Length,
                    pSubpasses: pSubpasses,
                    dependencyCount: dependencyCount,
                    pDependencies: pDependencies);

                if (allocationCallbacks != null)
                {
                    AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
                    if (VK.CreateRenderPass(device, in renderPassInfo, &dAllocationCallbacks, out handle) != Result.Success)
                    {
                        throw new NotSupportedException("failed to create render pass!");
                    }
                }
                else if (VK.CreateRenderPass(device, in renderPassInfo, null, out handle) != Result.Success)
                {
                    throw new NotSupportedException("failed to create render pass!");
                }
            }

            VKRenderPass result = new (
                this,
                handle,
                attachments ?? Array.Empty<AttachmentDescription>(),
                subpasses,
                dependencies ?? Array.Empty<SubpassDependency>());

            renderPasses[result] = allocationCallbacks;
            return result;
        }
        finally
        {
            for (int i = 0; i < subpasses.Length; i++)
            {
                subpasses[i].Unpin();
            }
        }
    }

    /// <summary>
    /// vkDestroyRenderPass（写法同 DestroyImage / DestroySwapchain）。
    /// 注意本类只是执行销毁，具体顺序（先帧缓冲、后通道）由调用方负责。
    /// </summary>
    internal unsafe bool DestroyRenderPass(VKRenderPass renderPass, RenderPass handle)
    {
        Device device = CheckedHandle;

        if (!renderPass.IsHandleOfThis(handle) || !renderPasses.TryRemove(renderPass, out var allocationCallbacks))
        {
            return false;
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroyRenderPass(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroyRenderPass(device, handle, null);
        }
        return true;
    }

    /// <summary>
    /// 渲染通道的归属校验：本设备建出来的才算 —— VKRenderPass.Valid 就是这么来的。
    /// </summary>
    public bool IsRenderPassOfThis(VKRenderPass renderPass)
        => renderPasses.ContainsKey(renderPass) && Valid;

    #endregion

}