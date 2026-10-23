using Silk.NET.Vulkan;

namespace SilkTry.Vulkan.ManagedStruct;

/// <summary>
/// "往某个描述符写一段缓冲"要用的内容（VkDescriptorBufferInfo 的无句柄版，见
/// <see cref="VKDescriptorSet.WriteBuffers"/>）。
///
/// 为什么需要它：VkDescriptorBufferInfo 里是裸的 Buffer 句柄，按本仓库约定不能出现在 public 签名里，
/// 所以这里存**包装类** VKBuffer；真正的句柄由 VKBuffer 在自己类里的收口读出来
///（同 ManagedPipelineShaderStage 存 VKShaderModule 的做法）。
///
/// 教程里的用法（uniform 缓冲那一段）：
///   bufferInfo.buffer = uniformBuffer;
///   bufferInfo.offset = 0;
///   bufferInfo.range  = sizeof(UniformBufferObject);
/// 对应到这里：
///   new ManagedDescriptorBufferInfo(uniformBuffer, 0, (ulong)sizeof(UniformBufferObject))
/// 不写 range 就是"从 offset 一直写到缓冲末尾"。
///
/// GLSL 侧的块声明决定用哪个 DescriptorType（完整对照见仓库根目录 GLSLDescriptorLayoutMapping.md）：
///   layout(set = 0, binding = 0, std140) uniform UniformBufferObject { ... } ubo; → UniformBuffer
///   layout(set = 0, binding = 1, std430) buffer StorageBlock { ... } ssbo;       → StorageBuffer
/// 块成员的 std140 / std430 / row_major / offset 只影响"缓冲里的字节排布"，
/// 不是本结构体的 Offset / Range —— 那两个是"这段描述符指向 buffer 的哪一段"。
/// </summary>
public struct ManagedDescriptorBufferInfo
{
    /// <summary>这段描述符指向哪个缓冲（必须是同一个逻辑设备上建的）。</summary>
    public VKBuffer Buffer;

    /// <summary>
    /// 从这个缓冲的哪个字节开始（要满足设备对 uniform / storage 缓冲对齐的要求）。
    /// 不是 GLSL 的 layout(offset = N) —— 那个是块成员在块内的偏移。
    /// </summary>
    public ulong Offset;

    /// <summary>
    /// 写多少字节：null = 一直到缓冲末尾（原生字段里对应 VK_WHOLE_SIZE 的语义）。
    /// 一段 uniform 只取一部分时（一个缓冲里放多份数据）才需要显式给。
    /// </summary>
    public ulong? Range;

    public ManagedDescriptorBufferInfo(VKBuffer buffer, ulong offset = 0, ulong? range = null)
    {
        Buffer = buffer;
        Offset = offset;
        Range = range;
    }
}
