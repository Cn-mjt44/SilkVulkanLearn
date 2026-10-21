using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 描述符集布局里的**一条绑定**（教程《Descriptor set layout》章节的 VkDescriptorSetLayoutBinding）的无指针版。
///
/// 为什么自己写一个：Silk.NET 的 VkDescriptorSetLayoutBinding 带一个裸指针字段
/// pImmutableSamplers（不可变采样器数组），连它的构造函数都是 unsafe 的 —— 调用方只想写
/// "第 0 号绑定是个组合图像采样器"，却得先进 unsafe 上下文、还要自己保证指针活到创建调用返回之后。
/// 本仓库还没有 VKSampler 包装类，immutable sampler 这一路本来就用不上，所以这里只留四个纯值字段：
/// 写绑定不需要 unsafe，指针的生命周期由包装层负责（见 VKLogicalDevice.CreateDescriptorSetLayout）。
///
/// 教程里的用法（片元着色器里那个 sampler2D）：
///   binding.binding         = 0;
///   binding.descriptorType  = VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER;
///   binding.descriptorCount = 1;
///   binding.stageFlags      = VK_SHADER_STAGE_FRAGMENT_BIT;
/// 对应到这里就是一行：
///   new ManagedDescriptorSetLayoutBinding(0, DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit)
/// </summary>
public struct ManagedDescriptorSetLayoutBinding
{
    /// <summary>着色器里的 binding 号 —— 和 GLSL 的 layout(binding = N) 对上。</summary>
    public uint Binding;

    /// <summary>这个位置绑什么：UniformBuffer / StorageBuffer / CombinedImageSampler ……</summary>
    public DescriptorType DescriptorType;

    /// <summary>
    /// 这个绑定里有多少个描述符：普通用法是 1；数组形式的（一次绑一串纹理 / 缓冲）填数组长度，
    /// 着色器里就写成 `binding = 0` 的数组（例如 uniform sampler2D textures[4]）。
    /// </summary>
    public uint DescriptorCount;

    /// <summary>这个绑定在哪几个着色器阶段可见（可组合，例如顶点 + 片元都用就按位或）。</summary>
    public ShaderStageFlags StageFlags;

    public ManagedDescriptorSetLayoutBinding(
        uint binding,
        DescriptorType descriptorType,
        ShaderStageFlags stageFlags,
        uint descriptorCount = 1)
    {
        Binding = binding;
        DescriptorType = descriptorType;
        StageFlags = stageFlags;
        DescriptorCount = descriptorCount;
    }
}
