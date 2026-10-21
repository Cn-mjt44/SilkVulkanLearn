# Copilot 项目指令

## API 形态

本仓库所有"托管代码包装原生资源句柄"的 API，必须遵循 [APIFormDesignStandard.md](../APIFormDesignStandard.md)。

写或改这类代码前，先读那份规范；核心六条是：

1. **一个包装类只持有一个原生句柄** —— 出现第二个句柄就拆类。
2. **句柄是 private** —— `public` 签名（参数 / 返回 / 字段）里不出现任何原生句柄；`internal` 可以。
3. **归属靠登记表** —— `Valid` 必须反查父对象的登记表（`Parent.IsXxxOfThis(this)`，并带上父对象自身的 `Valid`），不信任手里的句柄。
4. **创建在父对象、执行在持句柄者** —— `public CreateXxx` 在父包装类上；真正的原生调用在持有底层句柄的设备类里；参数含包装类数组时用 `internal static` 收口。
5. **销毁沿归属链上转、子先于父** —— 包装类只有无参 `Destroy()`，逐级 `internal bool DestroyXxx(...)`。
6. **多条创建路径用子类表达** —— 同一个原生对象有互斥的几套创建参数时，用 `abstract` 基类 + 具体子类
   （路径专属元数据不为可空），父对象上每条路径一个 `public` 工厂；消费方按类型分派时 `switch` 必须有 `default`。

参考实现：[`SilkTryProject/Vulkan/VKBufferView.cs`](../SilkTryProject/Vulkan/VKBufferView.cs)（最简形态）、
[`SilkTryProject/Vulkan/VKShaderPipelineLayout.cs`](../SilkTryProject/Vulkan/VKShaderPipelineLayout.cs)（多级形态）、
[`SilkTryProject/Vulkan/VKShaderPipeline.cs`](../SilkTryProject/Vulkan/VKShaderPipeline.cs)（多路径形态）、
[`SilkTryProject/Vulkan/VKCommandBuffer.cs`](../SilkTryProject/Vulkan/VKCommandBuffer.cs)（有状态录制）。
