using Silk.NET.Vulkan;

namespace SilkTry.Vulkan.ManagedStruct;

/// <summary>
/// 描述符集布局里的**一条绑定**（教程《Descriptor set layout》章节的 VkDescriptorSetLayoutBinding）的无指针版。
///
/// 为什么自己写一个：Silk.NET 的 VkDescriptorSetLayoutBinding 带一个裸指针字段
/// pImmutableSamplers（不可变采样器数组），连它的构造函数都是 unsafe 的 —— 调用方只想写
/// "第 0 号绑定是个组合图像采样器"，却得先进 unsafe 上下文、还要自己保证指针活到创建调用返回之后。
/// 这里换成集合：写绑定不用 unsafe，指针的组装与生命周期由包装层负责
///（VKDescriptorSetLayout.cs 里的 VKSampler.CreateDescriptorSetLayout 收口）。
///
/// 不可变采样器（ImmutableSamplers）：null = 这个绑定不带不可变采样器（采样器由描述符集自己给）；
/// 非 null 时长度必须等于 DescriptorCount，而且这些采样器的生死就与描述符集无关了 ——
/// 布局建好之后就能销毁（本仓库仍按"布局先死、采样器后死"的顺序排，保守且安全）。
///
/// 教程里的用法（片元着色器里那个 sampler2D）：
///   binding.binding         = 0;
///   binding.descriptorType  = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
///   binding.descriptorCount = 1;
///   binding.stageFlags      = VK_SHADER_STAGE_FRAGMENT_BIT;
/// 对应到这里：binding 号变成字典的 key，本结构体只装这个绑定自己的内容：
///   [0] = new ManagedDescriptorSetLayoutBinding(DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit)
///
/// GLSL 侧对照（完整表见仓库根目录 GLSLDescriptorLayoutMapping.md）：
///   layout(set = 0, binding = 0) uniform UniformBufferObject { ... } ubo;  → [0] = (UniformBuffer, VertexBit)
///   layout(set = 0, binding = 1) uniform sampler2D texSampler;             → [1] = (CombinedImageSampler, FragmentBit)
/// 字典的 key 就是同一个 set 内的 layout(binding = N)；set 号不在这里，而是这份布局被放进
/// VKShaderPipelineLayout.DescriptorSetLayouts 时的数组下标。
/// 同一个 set 内两条绑定不能重号（字典天然保证），正对应 Vulkan 的约束。
/// </summary>
public struct ManagedDescriptorSetLayoutBinding
{

    /// <summary>
    /// 这个位置绑什么：UniformBuffer / StorageBuffer / CombinedImageSampler ……
    /// 由着色器里的声明决定（完整对照见仓库根目录 GLSLDescriptorLayoutMapping.md）：
    ///   uniform Block { ... } x;                                    → UniformBuffer
    ///   buffer  Block { ... } x;                                    → StorageBuffer
    ///   uniform sampler2D x;                                        → CombinedImageSampler（图像 + 采样器合一）
    ///   uniform texture2D x; + uniform sampler s;                   → SampledImage（分离式；本项目不支持纯采样器）
    ///   layout(rgba8) uniform image2D x;                            → StorageImage
    ///   layout(input_attachment_index = n) uniform subpassInput x;  → InputAttachment
    ///   uniform samplerBuffer x;                                    → UniformTexelBuffer
    ///   layout(rgba32f) uniform imageBuffer x;                      → StorageTexelBuffer
    /// Dynamic 版本（UniformBufferDynamic / StorageBufferDynamic）在 GLSL 里没有写法，是 API 侧的选择。
    /// </summary>
    public DescriptorType DescriptorType;

    /// <summary>
    /// 这个绑定里有多少个描述符：普通用法是 1；数组形式的（一次绑一串纹理 / 缓冲）填数组长度，
    /// 着色器里就写成 `binding = 0` 的数组（例如 uniform sampler2D textures[4]）。
    /// 必须与 GLSL 声明的数组长度一致；写入第 i 个元素用 VKDescriptorSet.WriteXxx 的 firstDescriptor。
    /// </summary>
    public uint DescriptorCount;

    /// <summary>
    /// 这个绑定在哪几个着色器阶段可见（可组合，例如顶点 + 片元都用就按位或）。
    /// GLSL 里没有对应关键字：取**所有访问这个绑定的阶段**的并集 ——
    /// 在 .vert 里声明 → VertexBit，在 .frag 里声明 → FragmentBit，两边都用 → 两个按位或。
    /// </summary>
    public ShaderStageFlags StageFlags;

    /// <summary>
    /// 不可变采样器：**不填就是"采样器由描述符集给"**（教程那个 sampler2D 就是这种）。
    /// 填了的话（长度必须等于 <see cref="DescriptorCount"/>），这些位置上的采样器被烘进布局里，
    /// 描述符集里就不用再给采样器了（典型是阴影贴图的比较采样器）。
    /// GLSL 里没有对应语法：着色器照旧写 uniform sampler2D x;，是否不可变只由这里决定。
    /// 只有 Sampler / CombinedImageSampler 类型用得上；其余类型传了也会在创建布局时被清空。
    /// </summary>
    public VKSampler[]? ImmutableSamplers;

    public ManagedDescriptorSetLayoutBinding(
        DescriptorType descriptorType,
        ShaderStageFlags stageFlags,
        uint descriptorCount = 1,
        VKSampler[]? immutableSamplers = null)
    {
        DescriptorType = descriptorType;
        StageFlags = stageFlags;
        DescriptorCount = descriptorCount;
        ImmutableSamplers = immutableSamplers;
    }
}
