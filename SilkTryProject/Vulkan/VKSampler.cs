using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 采样器（VkSampler，教程《Image view and sampler》章节 createTextureSampler() 那一步）的包装。
/// 图像视图只说明"这块数据按什么格式看"，采样器才说明"着色器里读它的时候怎么插值"：
/// 放大 / 缩小时用最近点还是线性（MagFilter / MinFilter）、mip 级之间怎么插值（MipmapMode）、
/// 坐标超出 [0,1] 怎么处理（AddressModeU / V / W）、要不要各向异性过滤（AnisotropyEnable）、
/// 边界取哪个颜色（BorderColor）……
/// 三者（图像 + 视图 + 采样器）在描述符集里合成一个 combined image sampler 供着色器采样。
///
/// 归属：**父对象是 VKLogicalDevice** —— vkCreateSampler / vkDestroySampler 都是设备级调用，
/// 创建入口就在设备上（VKLogicalDevice.CreateSampler）、登记表也在它那边
///（Valid 靠 VKLogicalDevice.IsSamplerOfThis 反查）—— 同 VKDescriptorSetLayout / VKShaderModule 之于 VKLogicalDevice。
/// 采样器不依附任何图像：同一个采样器可以给多张图用，所以它不该挂在 VKImage 下面。
///
/// 分工：创建 → VKLogicalDevice.CreateSampler；
///       销毁 → Destroy() 转到 VKLogicalDevice.DestroySampler。
/// 拿去做**不可变采样器**时（描述符集布局里的 pImmutableSamplers），那条路要读每个采样器实例的私有句柄，
/// 按访问性约定收口在 VKDescriptorSetLayout.cs 里的 VKSampler.CreateDescriptorSetLayout
///（同 VKImageView.CreateFramebuffer 读附件视图句柄：谁的句柄类型，谁就在自己的类里读写它）。
///
/// 划界线：本类不做"把采样器和图像绑在一起"（那是 VkDescriptorImageInfo，属于描述符集，
/// 入口是 VKDescriptorSet.WriteImages —— 采样器句柄由本类的收口读，见 VKDescriptorSet.cs），
/// 也不做 vkCmdBindDescriptorSets（那是命令缓冲的事）。
/// 本类只做：记住创建时定下来的采样参数、校验句柄归属、销毁。
///
/// 生命周期：采样器要活到**引用它的描述符集 / 命令执行**结束，所以本类不能比那些晚死：
/// VKLogicalDevice.Destroy() 把它排在描述符集布局之后、渲染通道之前。
/// 不可变采样器是被烘进布局里的，布局先销毁、采样器后销毁一定安全。
/// </summary>
public partial class VKSampler
{
    /// <summary>父对象：创建这个采样器的逻辑设备。销毁、归属校验都走它。</summary>
    public readonly VKLogicalDevice Parent;

    /// <summary>创建时的 VkSamplerCreateInfo::flags（目前没有用得上的标志位，教程用 None）。</summary>
    public readonly SamplerCreateFlags Flags;

    /// <summary>放大时的过滤方式：Nearest（最近点，像素风）/ Linear（线性插值，教程用的这个）。</summary>
    public readonly Filter MagFilter;

    /// <summary>缩小时的过滤方式：同上；缩小时还有 mip 级可选，见 <see cref="MipmapMode"/>。</summary>
    public readonly Filter MinFilter;

    /// <summary>
    /// mip 级之间的插值方式：Nearest（直接挑最近的一级）/ Linear（在相邻两级之间插值，也就是三线性过滤）。
    /// 只有图像真的带 mip 级时才有意义（教程下一章生成 mip 时用到）。
    /// </summary>
    public readonly SamplerMipmapMode MipmapMode;

    /// <summary>u 方向超出 [0,1] 的处理：Repeat（平铺）/ MirroredRepeat / ClampToEdge / ClampToBorder。</summary>
    public readonly SamplerAddressMode AddressModeU;

    /// <summary>v 方向的寻址方式，含义同 <see cref="AddressModeU"/>。</summary>
    public readonly SamplerAddressMode AddressModeV;

    /// <summary>w 方向（三维纹理 / 数组层）的寻址方式，二维纹理用不到。</summary>
    public readonly SamplerAddressMode AddressModeW;

    /// <summary>mip 级偏移：正值让采样偏模糊、负值偏锐利，一般 0。</summary>
    public readonly float MipLodBias;

    /// <summary>
    /// 是否开启各向异性过滤（斜着看纹理时更清晰）。
    /// 要求创建设备时打开 samplerAnisotropy 特性（见 Game.cs 的 deviceFeatures），
    /// 否则校验层会报错 —— 所以本类默认不开。
    /// </summary>
    public readonly bool AnisotropyEnable;

    /// <summary>各向异性的最大采样次数，必须落在 [1, 物理设备的 maxSamplerAnisotropy]（教程用的就是这个上限）。</summary>
    public readonly float MaxAnisotropy;

    /// <summary>是否开启比较采样（阴影贴图用的那种，采样结果是比较结果而不是颜色）。</summary>
    public readonly bool CompareEnable;

    /// <summary>比较采样用的比较运算（CompareEnable 为 false 时无意义）。</summary>
    public readonly CompareOp CompareOp;

    /// <summary>
    /// 可用 mip 级的下界：小于它就不会再取更清晰的 mip，一般 0。
    ///（对应 create-info 里的 minLod，不是"图像最少要几级 mip"。）
    /// </summary>
    public readonly float MinLod;

    /// <summary>可用 mip 级的上界：默认 1000（VK_LOD_CLAMP_NONE）表示"不设上限"。</summary>
    public readonly float MaxLod;

    /// <summary>越界采样且寻址方式是 ClampToBorder 时取的颜色（自定义颜色要 VK_EXT_custom_border_color）。</summary>
    public readonly BorderColor BorderColor;

    /// <summary>是否不做归一化（坐标直接是 texel 下标）；开启后有一串额外限制，见 CreateSampler 的校验。</summary>
    public readonly bool UnnormalizedCoordinates;

    private readonly Sampler handle;
    private readonly static Vk VK = Vk.GetApi();

    internal VKSampler(
        VKLogicalDevice parent,
        Sampler handle,
        SamplerCreateFlags flags,
        Filter magFilter,
        Filter minFilter,
        SamplerMipmapMode mipmapMode,
        SamplerAddressMode addressModeU,
        SamplerAddressMode addressModeV,
        SamplerAddressMode addressModeW,
        float mipLodBias,
        bool anisotropyEnable,
        float maxAnisotropy,
        bool compareEnable,
        CompareOp compareOp,
        float minLod,
        float maxLod,
        BorderColor borderColor,
        bool unnormalizedCoordinates)
    {
        Parent = parent;
        this.handle = handle;
        Flags = flags;
        MagFilter = magFilter;
        MinFilter = minFilter;
        MipmapMode = mipmapMode;
        AddressModeU = addressModeU;
        AddressModeV = addressModeV;
        AddressModeW = addressModeW;
        MipLodBias = mipLodBias;
        AnisotropyEnable = anisotropyEnable;
        MaxAnisotropy = maxAnisotropy;
        CompareEnable = compareEnable;
        CompareOp = compareOp;
        MinLod = minLod;
        MaxLod = maxLod;
        BorderColor = borderColor;
        UnnormalizedCoordinates = unnormalizedCoordinates;
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 VKLogicalDevice.CreateSampler 里）。
    /// </summary>
    public bool Valid => Parent.IsSamplerOfThis(this);

    private Sampler CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The sampler is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：把"从父对象集合里摘掉 + 调 vkDestroySampler"交给父对象做。
    /// 顺序由调用方负责：引用它的描述符集 / 还在执行的命令必须先结束
    ///（Vulkan 要求 vkDestroySampler 时不能再有命令在用这个采样器）。
    /// </summary>
    public void Destroy()
        => Parent.DestroySampler(this, CheckedHandle);

    /// <summary>参数是裸句柄（Sampler），只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(Sampler handleForCheck)
        => handleForCheck.Handle == handle.Handle;
}

public partial class VKLogicalDevice
{
    #region 采样器（教程《Image view and sampler》章节）

    // 从本设备上建出来的采样器（写法和 descriptorSetLayouts / renderPasses 一样）：CreateSampler 时登记进来，
    // 之后靠 IsSamplerOfThis() 反查归属（VKSampler.Valid 就是这么来的）、销毁时摘掉，
    // value 是创建这个采样器时用的 allocationCallbacks。
    private readonly ConcurrentDictionary<VKSampler, AllocationCallbacks?> samplers = new ();

    /// <summary>
    /// 教程 createTextureSampler() 里的 vkCreateSampler —— **建采样器的入口就在本类**
    ///（public、签名里零裸句柄）。
    ///
    /// 为什么入口在本类：vkCreateSampler 要 Device 句柄，而只有本类持着它
    ///（同 CreateDescriptorSetLayout / CreateShaderModule）。
    ///
    /// 参数一一对应 VkSamplerCreateInfo 的字段，默认值就是"最省事的二维纹理采样器"，
    /// 也是教程那个 sampler 的默认值（线性过滤 + Repeat 寻址）：
    ///   magFilter / minFilter   —— 放大 / 缩小的过滤方式，默认 Linear；
    ///   mipmapMode              —— mip 级之间怎么插值，默认 Linear（三线性，配合带 mip 的图像）；
    ///   addressModeU/V/W        —— u/v/w 三个方向越界怎么处理，默认 Repeat（平铺）；
    ///   anisotropyEnable / maxAnisotropy
    ///                           —— 各向异性过滤。**创建设备时要打开 samplerAnisotropy 特性**
    ///                              （见 Game.cs 的 deviceFeatures），maxAnisotropy 要落在
    ///                              [1, 设备上限]，否则这里直接抛 ArgumentException；
    ///   compareEnable / compareOp
    ///                           —— 比较采样（阴影贴图），默认关闭 / Always；
    ///   mipLodBias / minLod / maxLod
    ///                           —— mip 偏移与可用范围，默认 0 / 0 / 1000（= VK_LOD_CLAMP_NONE，不设上限）；
    ///   borderColor            —— 寻址方式是 ClampToBorder 时取的颜色；自定义颜色要扩展
    ///                              （VK_EXT_custom_border_color），默认 FloatTransparentBlack；
    ///   unnormalizedCoordinates —— 坐标不归一化。开启时 Vulkan 还要求：min/mag 过滤方式相同、
    ///                              mipmapMode 是 Nearest、minLod = maxLod = 0、三个寻址方式都是
    ///                              ClampToEdge / ClampToBorder —— 这些这里一次性校验掉；
    ///   flags                  —— create-info 的 flags，教程用 None。
    /// 建出来的采样器要活到引用它的描述符集 / 命令结束（同 Vulkan 的常规要求）。
    /// </summary>
    public unsafe VKSampler CreateSampler(
        Filter magFilter = Filter.Linear,
        Filter minFilter = Filter.Linear,
        SamplerMipmapMode mipmapMode = SamplerMipmapMode.Linear,
        SamplerAddressMode addressModeU = SamplerAddressMode.Repeat,
        SamplerAddressMode addressModeV = SamplerAddressMode.Repeat,
        SamplerAddressMode addressModeW = SamplerAddressMode.Repeat,
        bool anisotropyEnable = false,
        float maxAnisotropy = 1.0f,
        bool compareEnable = false,
        CompareOp compareOp = CompareOp.Always,
        float mipLodBias = 0.0f,
        float minLod = 0.0f,
        float maxLod = 1000.0f,
        BorderColor borderColor = BorderColor.FloatTransparentBlack,
        bool unnormalizedCoordinates = false,
        SamplerCreateFlags flags = SamplerCreateFlags.None,
        AllocationCallbacks? allocationCallbacks = null)
    {
        if (minLod > maxLod)
        {
            throw new ArgumentException(
                "The minimum level of detail cannot be greater than the maximum level of detail.",
                nameof(minLod));
        }

        if (anisotropyEnable)
        {
            // 上限来自物理设备（Game.cs 里创建设备时打开了 samplerAnisotropy 特性）；
            // 超出范围校验层会直接拦下，这里提前抛，免得建出一个非法采样器。
            float maxSupportedAnisotropy = Parent.Properties.Limits.MaxSamplerAnisotropy;
            if (maxAnisotropy < 1.0f || maxAnisotropy > maxSupportedAnisotropy)
            {
                throw new ArgumentException(
                    $"The maximum anisotropy must be between 1 and the device limit ({maxSupportedAnisotropy}).",
                    nameof(maxAnisotropy));
            }
        }

        if (unnormalizedCoordinates)
        {
            // VUID-VkSamplerCreateInfo-unnormalizedCoordinates-01076：不归一化时这一整套限制都成立。
            if (magFilter != minFilter)
            {
                throw new ArgumentException(
                    "Unnormalized coordinates require the same minification and magnification filter.",
                    nameof(minFilter));
            }

            if (mipmapMode != SamplerMipmapMode.Nearest)
            {
                throw new ArgumentException(
                    "Unnormalized coordinates require the nearest mipmap mode.",
                    nameof(mipmapMode));
            }

            if (minLod != 0.0f || maxLod != 0.0f)
            {
                throw new ArgumentException(
                    "Unnormalized coordinates require both level-of-detail bounds to be zero.",
                    nameof(maxLod));
            }

            if (!IsClampAddressMode(addressModeU) || !IsClampAddressMode(addressModeV) || !IsClampAddressMode(addressModeW))
            {
                throw new ArgumentException(
                    "Unnormalized coordinates require ClampToEdge or ClampToBorder addressing.",
                    nameof(addressModeU));
            }
        }

        Device device = CheckedHandle;

        SamplerCreateInfo samplerInfo = new(
            flags: flags,
            magFilter: magFilter,
            minFilter: minFilter,
            mipmapMode: mipmapMode,
            addressModeU: addressModeU,
            addressModeV: addressModeV,
            addressModeW: addressModeW,
            mipLodBias: mipLodBias,
            anisotropyEnable: anisotropyEnable,
            maxAnisotropy: maxAnisotropy,
            compareEnable: compareEnable,
            compareOp: compareOp,
            minLod: minLod,
            maxLod: maxLod,
            borderColor: borderColor,
            unnormalizedCoordinates: unnormalizedCoordinates);

        Sampler samplerHandle;
        if (allocationCallbacks != null)
        {
            AllocationCallbacks dAllocationCallbacks = allocationCallbacks.Value;
            if (VK.CreateSampler(device, in samplerInfo, &dAllocationCallbacks, out samplerHandle) != Result.Success)
            {
                throw new NotSupportedException("failed to create sampler!");
            }
        }
        else if (VK.CreateSampler(device, in samplerInfo, null, out samplerHandle) != Result.Success)
        {
            throw new NotSupportedException("failed to create sampler!");
        }

        VKSampler result = new(
            this,
            samplerHandle,
            flags,
            magFilter,
            minFilter,
            mipmapMode,
            addressModeU,
            addressModeV,
            addressModeW,
            mipLodBias,
            anisotropyEnable,
            maxAnisotropy,
            compareEnable,
            compareOp,
            minLod,
            maxLod,
            borderColor,
            unnormalizedCoordinates);

        samplers[result] = allocationCallbacks;
        return result;
    }

    /// <summary>本设备身上有没有登记过这个采样器 —— VKSampler.Valid 靠它。</summary>
    public bool IsSamplerOfThis(VKSampler sampler)
        => samplers.ContainsKey(sampler) && Valid;

    /// <summary>
    /// vkDestroySampler（写法同 DestroyDescriptorSetLayout）：先校验句柄 + 摘登记
    ///（顺便把创建时记下的 ac 取出来），再执行销毁。
    /// 注意顺序：引用它的描述符集 / 还在执行的命令必须先结束 —— 这一步由调用方负责。
    /// </summary>
    internal unsafe bool DestroySampler(VKSampler sampler, Sampler handle)
    {
        Device device = CheckedHandle;

        if (!sampler.IsHandleOfThis(handle) || !samplers.TryRemove(sampler, out var allocationCallbacks))
        {
            return false;
        }

        if (allocationCallbacks != null)
        {
            var dAllocationCallbacks = allocationCallbacks.Value;
            VK.DestroySampler(device, handle, &dAllocationCallbacks);
        }
        else
        {
            VK.DestroySampler(device, handle, null);
        }

        return true;
    }

    /// <summary>不归一化坐标允许的寻址方式（ClampToEdge / ClampToBorder）。</summary>
    private static bool IsClampAddressMode(SamplerAddressMode addressMode)
        => addressMode == SamplerAddressMode.ClampToEdge || addressMode == SamplerAddressMode.ClampToBorder;

    #endregion
}
