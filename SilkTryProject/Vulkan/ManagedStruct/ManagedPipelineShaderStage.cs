using Silk.NET.Vulkan;

namespace SilkTry.Vulkan.ManagedStruct;

/// <summary>
/// 管线着色器阶段（教程《Shader modules》/《Fixed functions》章节里 VkPipelineShaderStageCreateInfo 那一段）的
/// **无指针 + 无句柄版**。
///
/// 为什么自己写一个（同 ManagedSubpassDescription 的理由，但这里还多一条）：
///   · Silk.NET 的 VkPipelineShaderStageCreateInfo 里 pName 是 byte*（C 字符串）、pSpecializationInfo 是结构体指针，
///     调用方要么 fixed 要么取局部变量地址；这里换成 string / 不用，长度和指针的配对由包装层负责。
///   · 更要紧的是 module 字段是 **VkShaderModule 裸句柄** —— 按本仓库的约定，裸句柄不能出现在包装层以外的
///     public 签名里（见 VKShaderModule 顶部那段说明）。所以这里存的是**包装类** VKShaderModule，
///     真正的句柄由 VKShaderModule 自己在 CreateGraphicsPipeline 里读出来（"谁的句柄类型，谁就在自己的类里读写它"）。
///
/// 教程里的用法就是一对（顶点 + 片元）：
///   vertShaderStageInfo.stage  = VK_SHADER_STAGE_VERTEX_BIT;
///   vertShaderStageInfo.module = vertShaderModule;
///   vertShaderStageInfo.pName  = "main";
/// 对应到这里：
///   new ManagedPipelineShaderStage(vertShaderModule, ShaderStageFlags.VertexBit)
///（入口点不写就是 "main"，教程里两个着色器都是 main）。
/// </summary>
public struct ManagedPipelineShaderStage
{
    /// <summary>这一段用哪个着色器模块（教程里是 vertShaderModule / fragShaderModule）。</summary>
    public VKShaderModule Module;

    /// <summary>
    /// 这一段是哪个阶段：顶点用 VertexBit、片元用 FragmentBit（还能几何 / 曲面细分 / 计算……
    /// 但图形管线里必须有且仅有一个 VertexBit 和一个 FragmentBit，教程这两章只用这两个）。
    /// </summary>
    public ShaderStageFlags Stage;

    /// <summary>入口函数名（教程固定是 "main"；GLSL 里就是 void main()）。</summary>
    public string EntryPoint;

    public ManagedPipelineShaderStage(VKShaderModule module, ShaderStageFlags stage, string entryPoint = "main")
    {
        Module = module;
        Stage = stage;
        EntryPoint = entryPoint;
    }
}
