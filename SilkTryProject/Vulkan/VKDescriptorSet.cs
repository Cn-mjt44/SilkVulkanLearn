using Silk.NET.Vulkan;
using SilkTry.Vulkan.ManagedStruct;

namespace SilkTry.Vulkan;

/// <summary>
/// 描述符集（VkDescriptorSet，教程《Descriptor pool and sets》章节）的包装。
/// 描述符集是"一套具体资源"的集合：第 0 号绑定指向哪个 uniform 缓冲、第 1 号指向哪张纹理 +
/// 哪个采样器…… 它按**描述符集布局**切出来，从**描述符池**里分配，赋值靠
/// vkUpdateDescriptorSets（本类的 WriteXxx / CopyTo），使用靠 vkCmdBindDescriptorSets
/// （VKCommandBuffer.BindDescriptorSets）。
///
/// 归属：**父对象是 VKDescriptorPool** —— 描述符集不能比池活得久（池 Destroy 会让它作废），
/// 而且本仓库是"建池即建集"：集在 VKDescriptorSetLayout.CreateDescriptorPool 里一次性分配出来，
/// 没有单独的分配入口、也没有单独归还（池一销毁就全部回收）。
/// "归属证据"就是池创建时切出来的那批（Valid 靠 VKDescriptorPool.IsDescriptorSetOfThis 反查）——
/// 同 VKCommandBuffer 之于 VKCommandPool。
/// 再往上一层：池又归属它服务的 VKDescriptorSetLayout，所以本类**不再单独存布局字段**，
/// <see cref="Layout"/> 直接取 Parent.Parent —— 一条链（设备 → 布局 → 池 → 集）只有一份真相。
///（vkUpdateDescriptorSets 本身是设备级调用，真正的调用由 VKLogicalDevice 执行 —— 它才持着 Device 句柄；
///  池 / 各资源对象负责把自己的句柄当参数往下传。）
///
/// 分工（"发起方包装 + 校验、沿途各环执行自己那一段"）：
///   · 分配：VKDescriptorSetLayout.CreateDescriptorPool → VKLogicalDevice.AllocateDescriptorSets（本类只负责包装结果）；
///   · 写缓冲：WriteBuffers → VKLogicalDevice.UpdateDescriptorSet_Buffers → VKBuffer.WriteDescriptorSets（收口：读缓冲句柄）；
///   · 写图像：WriteImages  → VKLogicalDevice.UpdateDescriptorSet_Images  → VKImageView.WriteDescriptorSets（收口：读视图句柄）
///     → VKSampler.WriteDescriptorSets（收口：读采样器句柄 + 发原生调用）；
///   · 写纹素缓冲视图：WriteTexelBufferViews → 设备 → VKBufferView.WriteDescriptorSets（收口：读视图句柄）；
///   · 集间拷贝：CopyTo     → VKLogicalDevice.UpdateDescriptorSet_Copy（只碰描述符集句柄，本类自己就能读）；
///   · 绑定：VKCommandBuffer.BindDescriptorSets → VKShaderPipelineLayout.BindDescriptorSets → 本类静态收口（读各集句柄）；
///   · 销毁：没有单独入口 —— 随池走（VKDescriptorPool.Destroy() / 布局的 Destroy()）。
///
/// 划界线：
///   · 不做 vkCmdBindDescriptorSets 的录制状态判断（那是命令缓冲的事），也不做 vkCmdPushDescriptorSet（要扩展）；
///   · 一次 WriteXxx 只写**一个绑定**（vkUpdateDescriptorSets 允许一次写多条，但本仓库按"一个绑定一次调用"暴露，
///     语义一样，省掉一个需要跨多个类收口的批量结构）；
///   · 不支持"纯采样器"描述符（DescriptorType.Sampler）与可变描述符（VK_EXT_mutable_descriptor_type）、
///     加速结构、inline uniform block —— 教程用不到，等真要用时再各自补一个 Xxx.WriteDescriptorSets 收口。
///
/// GLSL 对照（完整表见仓库根目录 GLSLDescriptorLayoutMapping.md）：
///   · set 号 = 管线布局里描述符集布局数组的下标；本类只跟"某个 set 内的 binding 号"打交道；
///   · 着色器里的声明决定 DescriptorType（uniform 块 → UniformBuffer、sampler2D → CombinedImageSampler、……）；
///   · firstDescriptor 就是 GLSL 数组声明的下标（uniform sampler2D tex[4] 的 tex[i]）。
///
/// 生命周期：描述符集要活到**用到它的命令缓冲 / 已提交命令**结束；它引用的缓冲 / 图像视图 / 采样器
/// 也都要活得比它久（描述符集布局同样不能先于写入它的那一步被销毁）。
/// </summary>
public partial class VKDescriptorSet
{
    /// <summary>父对象：这套集是从哪个池里分配出来的。归属校验走它；销毁随池一起做。</summary>
    public readonly VKDescriptorPool Parent;

    /// <summary>
    /// 这套集是按哪份布局切出来的（引用，不是拥有 —— 布局就是池的父对象，所以直接取 Parent.Parent）。
    /// 写入时用它校验"绑定号存不存在、类型对不对、数组下标有没有越界"；
    /// 布局一旦销毁，它名下的池和集都会先被销毁，所以 Valid 会变 false，WriteXxx / CopyTo 会先拦下。
    /// </summary>
    public VKDescriptorSetLayout Layout => Parent.Parent;

    /// <summary>执行方：顺着归属链向上 集 → 池 → 布局 → 设备。</summary>
    private VKLogicalDevice Device => Parent.Parent.Parent;

    private readonly DescriptorSet handle;
    private readonly static Vk VK = Vk.GetApi();

    internal VKDescriptorSet(VKDescriptorPool parent, DescriptorSet handle)
    {
        Parent = parent;
        this.handle = handle;
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象（池）名下有没有自己
    ///（池创建时一次性切出全部集；池一销毁就再没有这个对象，所以池 / 布局任一失效都会让它变成无效）。
    /// </summary>
    public bool Valid => Parent.IsDescriptorSetOfThis(this);

    private DescriptorSet CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The descriptor set is not valid.");
            }

            return handle;
        }
    }

    /// <summary>参数是裸句柄（DescriptorSet），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(DescriptorSet handleForCheck)
        => handleForCheck.Handle == handle.Handle;

    /// <summary>
    /// vkUpdateDescriptorSets 的**缓冲**那一路：把 buffers 依次写到从 firstDescriptor 开始的
    /// descriptorType 绑定上（教程 updateUniformBuffer 里的那句 descriptorWrites[0]）。
    ///
    /// 参数：
    ///   descriptorType —— 必须是这条绑定的类型：UniformBuffer / StorageBuffer，或它们的 Dynamic 版本
    ///                     （GLSL 里 uniform 块 → UniformBuffer、buffer 块 → StorageBuffer；
    ///                      Dynamic 没有 GLSL 写法，是 API 侧的选择）；
    ///   binding        —— 写哪条绑定（对着 GLSL 里的 layout(binding = N)）；
    ///   buffers        —— 每条描述符写什么（缓冲 + 偏移 + 长度，见 <see cref="ManagedDescriptorBufferInfo"/>）；
    ///   firstDescriptor —— 从这条绑定的第几个元素开始写（数组式绑定才用得上，默认 0；
    ///                      也就是 GLSL 数组声明的下标，0 对应 tex[0]）。
    /// 校验（都在本类先做掉，免得送进驱动才被校验层挑出来）：
    ///   · 集本身有效、它的布局还有效；
    ///   · 布局里确实有这条绑定，且类型一致；
    ///   · firstDescriptor + buffers.Length 没超出这条绑定的 descriptorCount；
    ///   · 每个缓冲都来自同一个逻辑设备，并且创建时带了对应 Usage（UniformBufferBit / StorageBufferBit）。
    /// 一次调用只写一条绑定；多个绑定就调用多次（Vulkan 语义一样）。
    /// </summary>
    public void WriteBuffers(
        DescriptorType descriptorType,
        uint binding,
        ManagedDescriptorBufferInfo[] buffers,
        uint firstDescriptor = 0)
    {
        if (buffers == null || buffers.Length == 0)
        {
            throw new ArgumentException("At least one buffer must be provided.", nameof(buffers));
        }

        DescriptorSet setHandle = CheckedHandle;
        CheckWrite(descriptorType, binding, firstDescriptor, (uint)buffers.Length);

        Device.UpdateDescriptorSet_Buffers(this, setHandle, descriptorType, binding, firstDescriptor, buffers);
    }

    /// <summary>
    /// vkUpdateDescriptorSets 的**图像**那一路：把 images 依次写到从 firstDescriptor 开始的绑定上
    /// （教程纹理那一段：imageInfo 里是 imageLayout + imageView + sampler）。
    ///
    /// 参数：
    ///   descriptorType —— 必须是这条绑定的类型：CombinedImageSampler（要带采样器）/ SampledImage /
    ///                     StorageImage / InputAttachment（后三种的 Sampler 会被忽略）；
    ///                     GLSL 对应：uniform sampler2D → CombinedImageSampler、uniform texture2D → SampledImage、
    ///                     layout(rgba8) uniform image2D → StorageImage、
    ///                     layout(input_attachment_index = n) uniform subpassInput → InputAttachment；
    ///   binding / firstDescriptor —— 同 WriteBuffers；
    ///   images         —— 每条描述符写哪张视图、按什么布局采样、用哪个采样器
    ///                     （见 <see cref="ManagedDescriptorImageInfo"/>）。
    /// 校验：布局里确实有这条绑定且类型一致、下标没越界、每张视图来自同一逻辑设备且带对应 Usage
    /// （SampledBit / StorageBit / InputAttachmentBit）；CombinedImageSampler 必须给采样器，且采样器也来自同一设备。
    /// 例外：这条绑定在布局里带了**不可变采样器**时，采样器已经烘进布局，Vulkan 会忽略 pImageInfo 里的 sampler，
    /// 所以那种绑定既不必给采样器，给了也不会被写进去。
    /// </summary>
    public void WriteImages(
        DescriptorType descriptorType,
        uint binding,
        ManagedDescriptorImageInfo[] images,
        uint firstDescriptor = 0)
    {
        if (images == null || images.Length == 0)
        {
            throw new ArgumentException("At least one image must be provided.", nameof(images));
        }

        DescriptorSet setHandle = CheckedHandle;
        ManagedDescriptorSetLayoutBinding layoutBinding = CheckWrite(descriptorType, binding, firstDescriptor, (uint)images.Length);

        Device.UpdateDescriptorSet_Images(
            this,
            setHandle,
            descriptorType,
            binding,
            firstDescriptor,
            images,
            layoutBinding.ImmutableSamplers != null);
    }

    /// <summary>
    /// vkUpdateDescriptorSets 的**纹素缓冲视图**那一路：UniformTexelBuffer / StorageTexelBuffer 绑定。
    ///
    /// 参数：
    ///   descriptorType —— UniformTexelBuffer 或 StorageTexelBuffer
    ///                     （GLSL：uniform samplerBuffer → UniformTexelBuffer、
    ///                      layout(rgba32f) uniform imageBuffer → StorageTexelBuffer）；
    ///   binding / firstDescriptor —— 同上；
    ///   bufferViews    —— 每条描述符用哪张缓冲视图（VKBufferView，见 VKBuffer.CreateBufferView）。
    /// 校验：布局里确实有这条绑定且类型一致、下标没越界、每张视图的缓冲来自同一设备且带对应的
    /// Usage（UniformTexelBufferBit / StorageTexelBufferBit）。
    /// </summary>
    public void WriteTexelBufferViews(
        DescriptorType descriptorType,
        uint binding,
        VKBufferView[] bufferViews,
        uint firstDescriptor = 0)
    {
        if (bufferViews == null || bufferViews.Length == 0)
        {
            throw new ArgumentException("At least one buffer view must be provided.", nameof(bufferViews));
        }

        DescriptorSet setHandle = CheckedHandle;
        CheckWrite(descriptorType, binding, firstDescriptor, (uint)bufferViews.Length);

        Device.UpdateDescriptorSet_TexelBufferViews(this, setHandle, descriptorType, binding, firstDescriptor, bufferViews);
    }

    /// <summary>
    /// vkUpdateDescriptorSets 的**拷贝**那一路：把本集里的一段描述符拷到 destination 里
    /// （同一份资源同时出现在两套集里时，比重新写一遍省事）。
    ///
    /// 参数：
    ///   destination              —— 目标集（必须和本集同一个逻辑设备）；
    ///   sourceBinding / destinationBinding —— 源 / 目标绑定号（都对着 GLSL 的 layout(binding = N)）；
    ///   count                    —— 拷几个（数组式绑定拷多个）；
    ///   sourceFirstDescriptor / destinationFirstDescriptor —— 源 / 目标数组的起始下标（GLSL 数组下标）。
    /// 校验：两边绑定都存在、类型一致、各自的 [起始, 起始+count) 都在 descriptorCount 之内。
    /// 之所以不用经过设备那层的"收口"：拷贝只碰描述符集句柄，而本类就是那个句柄类型的持有者
    ///（同类的代码能读任意实例的 private 字段）。
    /// </summary>
    public void CopyTo(
        VKDescriptorSet destination,
        uint sourceBinding,
        uint destinationBinding,
        uint count = 1,
        uint sourceFirstDescriptor = 0,
        uint destinationFirstDescriptor = 0)
    {
        if (destination == null)
        {
            throw new ArgumentNullException(nameof(destination));
        }

        if (count == 0)
        {
            throw new ArgumentException("At least one descriptor must be copied.", nameof(count));
        }

        DescriptorSet sourceHandle = CheckedHandle;
        DescriptorSet destinationHandle = destination.CheckedHandle;
        CheckCopy(destination, sourceBinding, destinationBinding, count, sourceFirstDescriptor, destinationFirstDescriptor);

        Device.UpdateDescriptorSet_Copy(
            this, sourceHandle,
            destination, destinationHandle,
            sourceBinding, sourceFirstDescriptor,
            destinationBinding, destinationFirstDescriptor,
            count);
    }

    /// <summary>
    /// 写入前的统一校验：集 / 布局都还有效，布局里确实声明了这条绑定，类型对得上，数组下标不越界。
    ///
    /// 为什么要看 Layout.Valid：布局的 Destroy() 会先销毁名下的池（连带集），正常情况下集不会"活着但布局没了"；
    /// 这里仍然提前拦下，免得把非法调用送进 vkUpdateDescriptorSets（也防住未来有人绕过归属链手动销毁布局）。
    /// </summary>
    private ManagedDescriptorSetLayoutBinding CheckWrite(DescriptorType descriptorType, uint binding, uint firstDescriptor, uint count)
    {
        if (!Layout.Valid)
        {
            throw new InvalidOperationException(
                "The descriptor set layout is not valid; a descriptor set cannot be updated after its layout was destroyed.");
        }

        if (!Layout.Bindings.TryGetValue(binding, out ManagedDescriptorSetLayoutBinding layoutBinding))
        {
            throw new ArgumentException(
                $"The descriptor set layout does not declare a binding with index {binding}.",
                nameof(binding));
        }

        if (layoutBinding.DescriptorType != descriptorType)
        {
            throw new ArgumentException(
                $"The provided descriptor type ({descriptorType}) does not match the binding ({layoutBinding.DescriptorType}).",
                nameof(descriptorType));
        }

        if (firstDescriptor + count > layoutBinding.DescriptorCount)
        {
            throw new ArgumentException(
                $"The write from descriptor {firstDescriptor} to {firstDescriptor + count - 1} " +
                $"exceeds the binding descriptor count ({layoutBinding.DescriptorCount}).",
                nameof(firstDescriptor));
        }

        return layoutBinding;
    }

    /// <summary>拷贝前的校验：两个集的设备、绑定、类型、范围都得对得上。</summary>
    private void CheckCopy(
        VKDescriptorSet destination,
        uint sourceBinding,
        uint destinationBinding,
        uint count,
        uint sourceFirstDescriptor,
        uint destinationFirstDescriptor)
    {
        if (destination.Device != Device)
        {
            throw new ArgumentException(
                "The provided destination descriptor set is not from the logical device that owns the source descriptor set.",
                nameof(destination));
        }

        if (!Layout.Valid || !destination.Layout.Valid)
        {
            throw new InvalidOperationException(
                "A descriptor set layout is not valid; a descriptor set cannot be updated after its layout was destroyed.");
        }

        if(!Layout.Bindings.TryGetValue(sourceBinding, out ManagedDescriptorSetLayoutBinding source))
        {
            throw new ArgumentException(
                $"The descriptor set layout does not declare a binding with index {sourceBinding}.",
                nameof(sourceBinding));
        }

        if(!destination.Layout.Bindings.TryGetValue(destinationBinding, out ManagedDescriptorSetLayoutBinding destinationLayoutBinding))
        {
            throw new ArgumentException(
                $"The destination descriptor set layout does not declare a binding with index {destinationBinding}.",
                nameof(destinationBinding));
        }

        if (source.DescriptorType != destinationLayoutBinding.DescriptorType)
        {
            throw new ArgumentException(
                $"The descriptor types of the two bindings do not match ({source.DescriptorType} vs {destinationLayoutBinding.DescriptorType}).",
                nameof(destinationBinding));
        }

        if (sourceFirstDescriptor + count > source.DescriptorCount)
        {
            throw new ArgumentException(
                $"The copy from descriptor {sourceFirstDescriptor} to {sourceFirstDescriptor + count - 1} " +
                $"exceeds the source binding descriptor count ({source.DescriptorCount}).",
                nameof(sourceFirstDescriptor));
        }

        if (destinationFirstDescriptor + count > destinationLayoutBinding.DescriptorCount)
        {
            throw new ArgumentException(
                $"The copy from descriptor {destinationFirstDescriptor} to {destinationFirstDescriptor + count - 1} " +
                $"exceeds the destination binding descriptor count ({destinationLayoutBinding.DescriptorCount}).",
                nameof(destinationFirstDescriptor));
        }
    }
}

public partial class VKLogicalDevice
{
    #region 描述符集（教程《Descriptor pool and sets》章节）

    /// <summary>
    /// vkAllocateDescriptorSets 的"设备这一环"：发起方（VKDescriptorSetLayout.CreateDescriptorPool）
    /// 把自己的布局句柄 + 刚建好的池句柄传进来，本类补上 Device 句柄、执行分配。
    /// 池只服务一份布局（布局是池的父对象），所以 count 套集都填同一份布局句柄。
    /// 返回新建的描述符集句柄数组，由发起方包装 + 登记。
    /// 之所以只能是 internal：入参 / 返回值里带裸句柄，按访问性约定不能出包装层。
    /// </summary>
    internal unsafe DescriptorSet[] AllocateDescriptorSets(
        DescriptorPool descriptorPoolHandle,
        VKDescriptorSetLayout layout,
        DescriptorSetLayout layoutHandle,
        uint count)
    {
        if (!layout.IsHandleOfThis(layoutHandle))
        {
            throw new ArgumentException(
                "The provided descriptor set layout handle is not from the descriptor set layout.",
                nameof(layoutHandle));
        }

        if (!IsDescriptorSetLayoutOfThis(layout))
        {
            throw new ArgumentException(
                "The provided descriptor set layout is not associated with logical device.",
                nameof(layout));
        }

        int setCount = checked((int)count);

        // vkAllocateDescriptorSets 要的是"每个 set 一份布局"的数组；本仓库所有集共用父布局，所以每项相同。
        DescriptorSetLayout* pSetLayouts = stackalloc DescriptorSetLayout[setCount];
        for (int i = 0; i < setCount; i++)
        {
            pSetLayouts[i] = layoutHandle;
        }

        DescriptorSetAllocateInfo allocateInfo = new(
            descriptorPool: descriptorPoolHandle,
            descriptorSetCount: count,
            pSetLayouts: pSetLayouts);

        DescriptorSet* pDescriptorSets = stackalloc DescriptorSet[setCount];
        if (VK.AllocateDescriptorSets(CheckedHandle, in allocateInfo, pDescriptorSets) != Result.Success)
        {
            throw new NotSupportedException("failed to allocate descriptor sets!");
        }

        DescriptorSet[] result = new DescriptorSet[setCount];
        for (int i = 0; i < setCount; i++)
        {
            result[i] = pDescriptorSets[i];
        }

        return result;
    }

    /// <summary>
    /// vkUpdateDescriptorSets 的"设备这一环"（缓冲）：发起方（VKDescriptorSet）把自己的集句柄传进来，
    /// 本类补上 Device 句柄，再交给 VKBuffer 的收口（那边要读各缓冲的私有句柄）。
    /// </summary>
    internal void UpdateDescriptorSet_Buffers(
        VKDescriptorSet descriptorSet,
        DescriptorSet descriptorSetHandle,
        DescriptorType descriptorType,
        uint binding,
        uint firstDescriptor,
        ManagedDescriptorBufferInfo[] buffers)
        => VKBuffer.WriteDescriptorSets(
            this,
            CheckedHandle,
            descriptorSet,
            descriptorSetHandle,
            descriptorType,
            binding,
            firstDescriptor,
            buffers);

    /// <summary>
    /// vkUpdateDescriptorSets 的"设备这一环"（图像）：视图 / 采样器句柄由 VKImageView 的收口读，
    /// 本类只把自己持有的 Device 句柄补上。
    /// </summary>
    internal void UpdateDescriptorSet_Images(
        VKDescriptorSet descriptorSet,
        DescriptorSet descriptorSetHandle,
        DescriptorType descriptorType,
        uint binding,
        uint firstDescriptor,
        ManagedDescriptorImageInfo[] images,
        bool bindingHasImmutableSamplers)
        => VKImageView.WriteDescriptorSets(
            this,
            CheckedHandle,
            descriptorSet,
            descriptorSetHandle,
            descriptorType,
            binding,
            firstDescriptor,
            images,
            bindingHasImmutableSamplers);

    /// <summary>
    /// vkUpdateDescriptorSets 的"设备这一环"（纹素缓冲视图）：视图句柄由 VKBufferView 的收口读。
    /// </summary>
    internal void UpdateDescriptorSet_TexelBufferViews(
        VKDescriptorSet descriptorSet,
        DescriptorSet descriptorSetHandle,
        DescriptorType descriptorType,
        uint binding,
        uint firstDescriptor,
        VKBufferView[] bufferViews)
        => VKBufferView.WriteDescriptorSets(
            this,
            CheckedHandle,
            descriptorSet,
            descriptorSetHandle,
            descriptorType,
            binding,
            firstDescriptor,
            bufferViews);

    /// <summary>
    /// vkUpdateDescriptorSets 的"设备这一环"（集间拷贝）：只碰描述符集句柄，而那种句柄本类读不到，
    /// 所以两个集都由发起方（VKDescriptorSet）当参数传进来，这里复核后执行。
    /// </summary>
    internal unsafe void UpdateDescriptorSet_Copy(
        VKDescriptorSet source,
        DescriptorSet sourceHandle,
        VKDescriptorSet destination,
        DescriptorSet destinationHandle,
        uint sourceBinding,
        uint sourceFirstDescriptor,
        uint destinationBinding,
        uint destinationFirstDescriptor,
        uint count)
    {
        Device device = CheckedHandle;

        if (!source.IsHandleOfThis(sourceHandle))
        {
            throw new ArgumentException("The provided source descriptor set handle is not from descriptor set.");
        }

        if (!destination.IsHandleOfThis(destinationHandle))
        {
            throw new ArgumentException("The provided destination descriptor set handle is not from descriptor set.");
        }

        CopyDescriptorSet copy = new(
            srcSet: sourceHandle,
            srcBinding: sourceBinding,
            srcArrayElement: sourceFirstDescriptor,
            dstSet: destinationHandle,
            dstBinding: destinationBinding,
            dstArrayElement: destinationFirstDescriptor,
            descriptorCount: count);

        VK.UpdateDescriptorSets(device, [], [copy]);
    }

    #endregion
}

public partial class VKBuffer
{
    /// <summary>
    /// vkUpdateDescriptorSets 的缓冲收口（要读每个缓冲实例的私有句柄，所以放在本类）。
    /// 调用链：VKDescriptorSet.WriteBuffers → VKLogicalDevice.UpdateDescriptorSet_Buffers → 本方法。
    ///
    /// 本方法负责把 ManagedDescriptorBufferInfo[] 变成 VkDescriptorBufferInfo[]（指针钉在栈上，
    /// 只在 vkUpdateDescriptorSets 期间需要），并逐个校验：
    ///   · 缓冲非空、来自同一逻辑设备、创建时带了这种描述符要求的 Usage；
    ///   · range 不传就是"从 offset 到缓冲末尾"（对应原生的 VK_WHOLE_SIZE 语义）。
    /// </summary>
    internal static unsafe void WriteDescriptorSets(
        VKLogicalDevice logicalDevice,
        Device logicalDeviceHandle,
        VKDescriptorSet descriptorSet,
        DescriptorSet descriptorSetHandle,
        DescriptorType descriptorType,
        uint binding,
        uint firstDescriptor,
        ManagedDescriptorBufferInfo[] buffers)
    {
        if (!logicalDevice.IsHandleOfThis(logicalDeviceHandle))
        {
            throw new ArgumentException("The provided logical device handle is not from logical device.");
        }

        if (!descriptorSet.IsHandleOfThis(descriptorSetHandle))
        {
            throw new ArgumentException("The provided descriptor set handle is not from descriptor set.");
        }

        BufferUsageFlags requiredUsage = GetRequiredUsage(descriptorType, nameof(descriptorType));

        DescriptorBufferInfo[] bufferInfos = new DescriptorBufferInfo[buffers.Length];
        for (int i = 0; i < buffers.Length; i++)
        {
            VKBuffer buffer = buffers[i].Buffer;
            if (buffer == null)
            {
                throw new ArgumentException("A descriptor cannot be written from a null buffer.", nameof(buffers));
            }

            if (buffer.Device != logicalDevice)
            {
                throw new ArgumentException(
                    "The provided buffer is not from the logical device that owns the descriptor set.",
                    nameof(buffers));
            }

            if ((buffer.Usage & requiredUsage) == 0)
            {
                throw new ArgumentException(
                    $"The buffer was not created with {requiredUsage} usage, which the descriptor type requires.",
                    nameof(buffers));
            }

            if (buffers[i].Offset > buffer.Size)
            {
                throw new ArgumentException(
                    "The buffer offset is beyond the end of the buffer.",
                    nameof(buffers));
            }

            ulong range = buffers[i].Range ?? (buffer.Size - buffers[i].Offset);
            bufferInfos[i] = new DescriptorBufferInfo(
                buffer: buffer.CheckedHandle,
                offset: buffers[i].Offset,
                range: range);
        }

        fixed (DescriptorBufferInfo* pBufferInfos = bufferInfos)
        {
            WriteDescriptorSet write = new(
                dstSet: descriptorSetHandle,
                dstBinding: binding,
                dstArrayElement: firstDescriptor,
                descriptorCount: (uint)bufferInfos.Length,
                descriptorType: descriptorType,
                pBufferInfo: pBufferInfos);

            VK.UpdateDescriptorSets(logicalDeviceHandle, [write], []);
        }
    }

    /// <summary>这种描述符类型要求缓冲创建时带哪个 Usage；不是缓冲类描述符就直接拒绝。</summary>
    private static BufferUsageFlags GetRequiredUsage(DescriptorType descriptorType, string parameterName)
        => descriptorType switch
        {
            DescriptorType.UniformBuffer or DescriptorType.UniformBufferDynamic => BufferUsageFlags.UniformBufferBit,
            DescriptorType.StorageBuffer or DescriptorType.StorageBufferDynamic => BufferUsageFlags.StorageBufferBit,
            _ => throw new ArgumentException(
                $"The descriptor type {descriptorType} is not a buffer descriptor type.",
                parameterName),
        };
}

public partial class VKImageView
{
    /// <summary>
    /// vkUpdateDescriptorSets 的图像这一环（要读每个视图实例的私有句柄，所以放在本类）。
    /// 调用链：VKDescriptorSet.WriteImages → VKLogicalDevice.UpdateDescriptorSet_Images → 本方法
    ///   → VKSampler.WriteDescriptorSets（采样器句柄只有 VKSampler 自己能读，最后那一步交给它）。
    ///
    /// 本方法负责校验并把视图换成句柄：
    ///   · 视图非空、来自同一逻辑设备；
    ///   · 图像创建时带了这种描述符要求的 Usage（SampledBit / StorageBit / InputAttachmentBit）；
    ///   · CombinedImageSampler 必须给采样器，且采样器来自同一设备（其余类型的采样器被 Vulkan 忽略）。
    /// 为什么还要多走一环：`VKImageView` 读不到 `VKSampler` 的私有句柄（private 是类型级的），
    /// 而采样器句柄只能由 `VKSampler` 自己的代码读 —— 同"谁的句柄类型，谁就在自己的类里读写它"。
    /// </summary>
    internal static void WriteDescriptorSets(
        VKLogicalDevice logicalDevice,
        Device logicalDeviceHandle,
        VKDescriptorSet descriptorSet,
        DescriptorSet descriptorSetHandle,
        DescriptorType descriptorType,
        uint binding,
        uint firstDescriptor,
        ManagedDescriptorImageInfo[] images,
        bool bindingHasImmutableSamplers)
    {
        if (!logicalDevice.IsHandleOfThis(logicalDeviceHandle))
        {
            throw new ArgumentException("The provided logical device handle is not from logical device.");
        }

        if (!descriptorSet.IsHandleOfThis(descriptorSetHandle))
        {
            throw new ArgumentException("The provided descriptor set handle is not from descriptor set.");
        }

        ImageUsageFlags requiredUsage = GetRequiredUsage(descriptorType, nameof(descriptorType));

        // 绑定了不可变采样器时，采样器已经烘进布局里（Vulkan 规定这种情况 pImageInfo 的 sampler 被忽略），
        // 所以既不需要调用方给采样器，也不会把它写进去。
        bool needsSampler = descriptorType == DescriptorType.CombinedImageSampler && !bindingHasImmutableSamplers;

        ImageView[] imageViewHandles = new ImageView[images.Length];
        ImageLayout[] imageLayouts = new ImageLayout[images.Length];
        VKSampler?[] samplers = new VKSampler?[images.Length];
        for (int i = 0; i < images.Length; i++)
        {
            VKImageView imageView = images[i].ImageView;
            if (imageView == null)
            {
                throw new ArgumentException("A descriptor cannot be written from a null image view.", nameof(images));
            }

            if (imageView.Parent.Parent.Parent != logicalDevice)
            {
                throw new ArgumentException(
                    "The provided image view is not from the logical device that owns the descriptor set.",
                    nameof(images));
            }

            if ((imageView.Parent.Usage & requiredUsage) == 0)
            {
                throw new ArgumentException(
                    $"The image was not created with {requiredUsage} usage, which the descriptor type requires.",
                    nameof(images));
            }

            imageViewHandles[i] = imageView.CheckedHandle;
            imageLayouts[i] = images[i].ImageLayout;

            if (needsSampler)
            {
                VKSampler? sampler = images[i].Sampler;
                if (sampler == null)
                {
                    throw new ArgumentException(
                        "A combined image sampler descriptor needs a sampler.",
                        nameof(images));
                }

                if (!logicalDevice.IsSamplerOfThis(sampler))
                {
                    throw new ArgumentException(
                        "The provided sampler is not associated with the logical device that owns the descriptor set.",
                        nameof(images));
                }
            }

            samplers[i] = images[i].Sampler;
        }

        VKSampler.WriteDescriptorSets(
            logicalDevice,
            logicalDeviceHandle,
            descriptorSet,
            descriptorSetHandle,
            descriptorType,
            binding,
            firstDescriptor,
            imageViewHandles,
            imageLayouts,
            samplers);
    }

    /// <summary>这种描述符类型要求图像创建时带哪个 Usage；不是图像类描述符就直接拒绝。</summary>
    private static ImageUsageFlags GetRequiredUsage(DescriptorType descriptorType, string parameterName)
        => descriptorType switch
        {
            DescriptorType.SampledImage or DescriptorType.CombinedImageSampler => ImageUsageFlags.SampledBit,
            DescriptorType.StorageImage => ImageUsageFlags.StorageBit,
            DescriptorType.InputAttachment => ImageUsageFlags.InputAttachmentBit,
            _ => throw new ArgumentException(
                $"The descriptor type {descriptorType} is not an image descriptor type.",
                parameterName),
        };
}

public partial class VKSampler
{
    /// <summary>
    /// vkUpdateDescriptorSets 的图像收口**最后一环**：本类读每个采样器实例的私有句柄，
    /// 视图句柄 / 布局 / 采样器包装类都由 VKImageView 那一环当参数传下来（它已经校验过了）。
    ///
    /// 之所以放在本类：`VKImageView` 读不到 `VKSampler` 的私有句柄
    ///（private 是类型级可见性，"谁的句柄类型，谁就在自己的类里读写它"）。
    /// 非 CombinedImageSampler 的描述符用不到采样器，samplers 里对应项是 null → 填 VK_NULL_HANDLE
    ///（Vulkan 对这些类型的采样器字段就是忽略的）。
    ///
    /// 之所以是 internal static：入参里有裸句柄（视图 / 布局），按访问性约定不出包装层。
    /// </summary>
    internal static unsafe void WriteDescriptorSets(
        VKLogicalDevice logicalDevice,
        Device logicalDeviceHandle,
        VKDescriptorSet descriptorSet,
        DescriptorSet descriptorSetHandle,
        DescriptorType descriptorType,
        uint binding,
        uint firstDescriptor,
        ImageView[] imageViewHandles,
        ImageLayout[] imageLayouts,
        VKSampler?[] samplers)
    {
        // 照惯例再复核一次（上游已经校验过设备 / 集的归属）。
        if (!logicalDevice.IsHandleOfThis(logicalDeviceHandle))
        {
            throw new ArgumentException("The provided logical device handle is not from logical device.");
        }

        if (!descriptorSet.IsHandleOfThis(descriptorSetHandle))
        {
            throw new ArgumentException("The provided descriptor set handle is not from descriptor set.");
        }

        DescriptorImageInfo[] imageInfos = new DescriptorImageInfo[imageViewHandles.Length];
        for (int i = 0; i < imageViewHandles.Length; i++)
        {
            Sampler samplerHandle = samplers[i] != null ? samplers[i]!.CheckedHandle : default;

            imageInfos[i] = new DescriptorImageInfo(
                sampler: samplerHandle,
                imageView: imageViewHandles[i],
                imageLayout: imageLayouts[i]);
        }

        fixed (DescriptorImageInfo* pImageInfos = imageInfos)
        {
            WriteDescriptorSet write = new(
                dstSet: descriptorSetHandle,
                dstBinding: binding,
                dstArrayElement: firstDescriptor,
                descriptorCount: (uint)imageInfos.Length,
                descriptorType: descriptorType,
                pImageInfo: pImageInfos);

            VK.UpdateDescriptorSets(logicalDeviceHandle, [write], []);
        }
    }
}

public partial class VKBufferView
{
    /// <summary>
    /// vkUpdateDescriptorSets 的纹素缓冲视图收口（要读每个视图实例的私有句柄，所以放在本类）。
    /// 调用链：VKDescriptorSet.WriteTexelBufferViews → VKLogicalDevice.UpdateDescriptorSet_TexelBufferViews → 本方法。
    ///
    /// 校验：视图非空、它的缓冲来自同一逻辑设备，且缓冲创建时带了 UniformTexelBufferBit / StorageTexelBufferBit。
    /// </summary>
    internal static unsafe void WriteDescriptorSets(
        VKLogicalDevice logicalDevice,
        Device logicalDeviceHandle,
        VKDescriptorSet descriptorSet,
        DescriptorSet descriptorSetHandle,
        DescriptorType descriptorType,
        uint binding,
        uint firstDescriptor,
        VKBufferView[] bufferViews)
    {
        if (!logicalDevice.IsHandleOfThis(logicalDeviceHandle))
        {
            throw new ArgumentException("The provided logical device handle is not from logical device.");
        }

        if (!descriptorSet.IsHandleOfThis(descriptorSetHandle))
        {
            throw new ArgumentException("The provided descriptor set handle is not from descriptor set.");
        }

        BufferUsageFlags requiredUsage = GetRequiredUsage(descriptorType, nameof(descriptorType));

        BufferView[] viewHandles = new BufferView[bufferViews.Length];
        for (int i = 0; i < bufferViews.Length; i++)
        {
            VKBufferView bufferView = bufferViews[i];
            if (bufferView == null)
            {
                throw new ArgumentException("A descriptor cannot be written from a null buffer view.", nameof(bufferViews));
            }

            if (bufferView.Parent.Device != logicalDevice)
            {
                throw new ArgumentException(
                    "The provided buffer view is not from the logical device that owns the descriptor set.",
                    nameof(bufferViews));
            }

            if ((bufferView.Parent.Usage & requiredUsage) == 0)
            {
                throw new ArgumentException(
                    $"The buffer was not created with {requiredUsage} usage, which the descriptor type requires.",
                    nameof(bufferViews));
            }

            viewHandles[i] = bufferView.CheckedHandle;
        }

        fixed (BufferView* pBufferViews = viewHandles)
        {
            WriteDescriptorSet write = new(
                dstSet: descriptorSetHandle,
                dstBinding: binding,
                dstArrayElement: firstDescriptor,
                descriptorCount: (uint)viewHandles.Length,
                descriptorType: descriptorType,
                pTexelBufferView: pBufferViews);

            VK.UpdateDescriptorSets(logicalDeviceHandle, [write], []);
        }
    }

    /// <summary>这种描述符类型要求缓冲创建时带哪个 Usage；不是纹素缓冲描述符就直接拒绝。</summary>
    private static BufferUsageFlags GetRequiredUsage(DescriptorType descriptorType, string parameterName)
        => descriptorType switch
        {
            DescriptorType.UniformTexelBuffer => BufferUsageFlags.UniformTexelBufferBit,
            DescriptorType.StorageTexelBuffer => BufferUsageFlags.StorageTexelBufferBit,
            _ => throw new ArgumentException(
                $"The descriptor type {descriptorType} is not a texel buffer descriptor type.",
                parameterName),
        };
}

