using Silk.NET.Vulkan;

namespace SilkTry.Vulkan.ManagedStruct;

/// <summary>
/// "往某个描述符写一张图像（+ 可选采样器）"要用的内容（VkDescriptorImageInfo 的无句柄版，见
/// <see cref="VKDescriptorSet.WriteImages"/>）。
///
/// 为什么需要它：VkDescriptorImageInfo 里是裸的 Sampler / ImageView 句柄，按本仓库约定不能出现在
/// public 签名里，所以这里存**包装类** VKSampler / VKImageView；真正的句柄由它们各自在自己类里的收口读出来。
///
/// 教程里的用法（纹理那一段）：
///   imageInfo.imageLayout = VK_IMAGE_LAYOUT_SHADER_READ_ONLY_OPTIMAL;
///   imageInfo.imageView   = textureImageView;
///   imageInfo.sampler     = textureSampler;
/// 对应到这里：
///   new ManagedDescriptorImageInfo(textureImageView, ImageLayout.ShaderReadOnlyOptimal, textureSampler)
///
/// Sampler 只有 **CombinedImageSampler** 这一种描述符要用：SampledImage / StorageImage / InputAttachment
/// 里这个字段会被 Vulkan 忽略（传 null 就行，传了也不报错）。
///
/// GLSL 侧的声明决定用哪个 DescriptorType（完整对照见仓库根目录 GLSLDescriptorLayoutMapping.md）：
///   uniform sampler2D tex;                                           → CombinedImageSampler（Sampler 必填）
///   uniform texture2D tex; + uniform sampler samp;                   → SampledImage（Sampler 被忽略）
///   layout(rgba8) uniform image2D img;                               → StorageImage（Sampler 被忽略）
///   layout(input_attachment_index = 0) uniform subpassInput inColor; → InputAttachment（Sampler 被忽略）
/// </summary>
public struct ManagedDescriptorImageInfo
{
    /// <summary>
    /// 这张描述符指向哪张图像视图（必须是同一个逻辑设备上建的）。
    /// GLSL 的变量类型决定视图类型：sampler2D / image2D → 2D 视图，samplerCube → Cube 视图；
    /// 存储图像的 layout(rgba8) 是**像素格式**限定符，要和这张视图的格式一致。
    /// </summary>
    public VKImageView ImageView;

    /// <summary>
    /// 采样这个描述符时图像要处于的布局：采样用 ShaderReadOnlyOptimal，
    /// 存储图像（着色器里写）用 General，输入附件用 ShaderReadOnlyOptimal / General（视用法而定）。
    /// 它必须与录制命令时那次 PipelineBarrier 转换到的布局一致。
    /// 注意：这是 Vulkan 的 VkImageLayout，和 GLSL 的 layout(...) 限定符不是一回事 ——
    /// GLSL 里的 layout(rgba8) 是像素格式，不是这个字段。
    /// </summary>
    public ImageLayout ImageLayout;

    /// <summary>
    /// 组合图像采样器用的采样器；只有 CombinedImageSampler 需要，其余类型忽略。
    /// GLSL 对应：uniform sampler2D 是"图像 + 采样器合一"，所以要填；
    /// uniform texture2D 是分离式，采样器会占用 Sampler 类型的描述符（本项目不支持纯采样器）；
    /// 布局里带不可变采样器（ManagedDescriptorSetLayoutBinding.ImmutableSamplers）时不用填。
    /// </summary>
    public VKSampler? Sampler;

    public ManagedDescriptorImageInfo(VKImageView imageView, ImageLayout imageLayout, VKSampler? sampler = null)
    {
        ImageView = imageView;
        ImageLayout = imageLayout;
        Sampler = sampler;
    }
}
