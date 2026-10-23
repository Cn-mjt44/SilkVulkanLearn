# GLSL `layout` 关键字 ↔ 描述符定义对照

这份文档解决一个具体问题：C# 描述符这一套 API 的参数名（`Binding`、`DescriptorType`、`DescriptorCount`、
`StageFlags`、`firstDescriptor`、`firstSet`、`ImageLayout`……）大多来自 Vulkan 对象模型，而着色器那边只写
`layout(...)`。两边看起来是两套词汇，其实一一对应。

读法：§1 先建立"set → binding → 描述符"的位置关系；§2 按关键字查表；§3 是照着就能抄的完整例子；
§4 是容易混淆的点。C# 侧的每个参数在源码注释里也标了对应的 GLSL 写法。

---

## 0. 先把几个"layout"分开

"layout" 这个词在 Vulkan 里被用在了三个互不相干的地方，混起来最容易出错：

| 名字 | 是什么 | 出现在哪 |
| --- | --- | --- |
| GLSL `layout(...)` | 着色器**接口限定符**：声明变量/块在管线里占哪个槽位（`set` / `binding` / `location` / `push_constant` ……） | `.vert` / `.frag` / `.comp` 源码 |
| 描述符集布局（`VkDescriptorSetLayout`，C# [`VKDescriptorSetLayout`](SilkTryProject/Vulkan/VKDescriptorSetLayout.cs)） | **一个 set 内部**有哪些绑定，每条绑定的类型 / 数量 / 可见阶段 | C# |
| 管线布局（`VkPipelineLayout`，C# [`VKShaderPipelineLayout`](SilkTryProject/Vulkan/VKShaderPipelineLayout.cs)） | 按**顺序**列出这套管线认哪些描述符集布局，外加 push constant 范围 | C# |
| 图像布局（`VkImageLayout`，C# `ImageLayout`） | 图像在**某一时刻**的用途/同步状态：`ShaderReadOnlyOptimal` / `General` / `TransferDstOptimal` …… | C# / `PipelineBarrier` |
| push constant 范围（`VkPushConstantRange`） | push constant 的可见阶段 + 偏移 + 大小 | C# |

要点：GLSL 的 `layout(...)` 和 `ImageLayout` **没有任何关系**；`layout(rgba8)` 是**像素格式**限定符，
也不是 `ImageLayout`（详见 §2.6 与 §4）。

---

## 1. 三层位置关系

```
着色器侧                                        C# 侧
layout(set = 1, binding = 2)  ───────────────►  VKShaderPipelineLayout.DescriptorSetLayouts[1]
                                                          │   （数组下标 = set 号，顺序就是 set 号）
                                                          └─ 该布局的 Bindings 字典里 key == 2 的那条
                                                             （value 是 ManagedDescriptorSetLayoutBinding）
                                                                │
                                                                └─ VKDescriptorSet.WriteXxx(binding: 2, …)
                                                                   把实际资源写进"第 1 个 set 的第 2 号槽"
```

- **`set` 的唯一落点是"管线布局里描述符集布局数组的下标"**（以及 `BindDescriptorSets` 的 `firstSet`）。
  C# 的 `ManagedDescriptorSetLayoutBinding` 里没有 `Set` 字段 —— 一份 `VKDescriptorSetLayout` 只描述一个 set。
- **`binding` 是描述符集布局里的一个 key**，与 GLSL 的 `binding = M` 数值完全相等。
- **`location` 是另一套编号**（顶点属性 / 阶段间接口 / 颜色附件），不能用它定位描述符。
- **本仓库的池归属**：一个 `VKDescriptorPool` 直接挂在它服务的 `VKDescriptorSetLayout` 下
  （设备 → 布局 → 池 → 集），所以池只服务这一份布局；`VKDescriptorSet.Layout` 就是 `Parent.Parent`，
  不再单独存布局字段。建池时就把 `maxSets` 套集全部分配好了（`pool.DescriptorSets`）。

---

## 2. 关键字总表

### 2.1 定位描述符：`set` / `binding`

| GLSL | 含义 | C# 落点 | 备注 |
| --- | --- | --- | --- |
| `layout(set = N)` | 第 N 个描述符集 | `VKShaderPipelineLayout.DescriptorSetLayouts[N]`；绑定时的 `BindDescriptorSets(firstSet: N)` | 不写 = 0；set 号由数组**顺序**决定 |
| `layout(binding = M)` | 该 set 内第 M 号绑定 | 绑定表字典的 **key**（`CreateDescriptorSetLayout` 的 `bindings`）；写入时 `WriteBuffers / WriteImages / WriteTexelBufferViews(binding: M)` | 不写 = 0；每个 set 独立编号；同一 set 内不能重号（字典天然保证） |
| `layout(set = N, binding = M)` + 数组 `[K]` | 一个绑定里有 K 个描述符 | 该 key 对应值的 `DescriptorCount = K`；写第 i 个用 `firstDescriptor: i` | 不写数组 = 1 |
| 不写 `set` / `binding` | 默认属于 set 0 / binding 0 | 同上，取 0 | 显式写更不容易错 |

注意：`binding` 是 GLSL 里**唯一**能定位描述符槽位的限定符；顶点输入的 `binding`（C# 侧
`VertexInputBindingDescription.Binding`）是另一码事，见 §2.7。

### 2.2 声明类型 → `DescriptorType`

| GLSL 声明 | `DescriptorType` | C# 写入方法 | 说明 |
| --- | --- | --- | --- |
| `uniform Block { … } name;`（uniform 块） | `UniformBuffer` | `WriteBuffers` | 成员默认按 std140 |
| `buffer Block { … } name;`（SSBO） | `StorageBuffer` | `WriteBuffers` | 成员默认按 std430 |
| ——（没有 GLSL 写法） | `UniformBufferDynamic` / `StorageBufferDynamic` | `WriteBuffers` + `BindDescriptorSets(dynamicOffsets:)` | "是否 dynamic"是 API 侧的选择，着色器看不出来 |
| `uniform sampler2D s;`（`sampler1D/2D/3D/Cube/2DArray/…`、`sampler2DShadow`、`sampler2DMS` ……） | `CombinedImageSampler` | `WriteImages`（要带 `Sampler`） | 图像与采样器合在一起 |
| `uniform texture2D t;` + `uniform sampler s;`（分离式） | `SampledImage`（+ `Sampler`） | `WriteImages`（`Sampler` 字段被忽略） | 本项目**不支持纯采样器描述符**（见 [`VKDescriptorSet`](SilkTryProject/Vulkan/VKDescriptorSet.cs) 划界线） |
| `layout(rgba8) uniform image2D img;` | `StorageImage` | `WriteImages`（`Sampler` 被忽略） | 格式限定符必写；图像创建要带 `StorageBit` |
| `layout(input_attachment_index = I, set = …, binding = …) uniform subpassInput inColor;` | `InputAttachment` | `WriteImages`（`Sampler` 被忽略） | `I` 对应渲染通道子通道的 `InputAttachments[I]`；`subpassInputMS` 同理 |
| `uniform samplerBuffer tb;` | `UniformTexelBuffer` | `WriteTexelBufferViews` | 缓冲视图 + 格式 |
| `layout(rgba32f) uniform imageBuffer ib;` | `StorageTexelBuffer` | `WriteTexelBufferViews` | 格式限定符必写 |
| `uniform sampler2D tex[4];` | `CombinedImageSampler`，`DescriptorCount = 4` | `WriteImages(firstDescriptor: i)` | 数组在 C# 里 = `DescriptorCount` + 下标 |
| `uniform sampler2D tex[];`（不定长） | 需要可变描述符数量分配 | —— | 本项目不支持（见 `VKDescriptorPool` 划界线） |
| `accelerationStructureEXT` | `AccelerationStructureKHR` | —— | 本项目不支持 |
| `layout(binding = M) uniform …` 同一 binding 声明成两种类型 | —— | —— | Vulkan 不允许，创建布局时就会失败 |

`StageFlags` 在 GLSL 里没有对应关键字：取**所有访问这个绑定的阶段**的并集 ——
在 `.vert` 里声明 → `VertexBit`，在 `.frag` 里声明 → `FragmentBit`，两个阶段都用 → 两个按位或。

### 2.3 数组、数量与写入下标

- GLSL 的数组长度 = 该绑定的 `ManagedDescriptorSetLayoutBinding.DescriptorCount`（不是 1 时才需要显式给）。
- 写入第 i 个元素 → `firstDescriptor: i`，即 GLSL 里的 `tex[i]`。
- 一次 `WriteXxx` 只写一个绑定（一条 GLSL `layout(binding = M)` 声明）。
- 数组式绑定可以只写其中一段：`firstDescriptor + 写入个数 ≤ DescriptorCount`。
- 描述符池的预算不用手填：创建池时按布局统计"每类 `DescriptorCount` 之和 × `maxSets`"；
  同一批集也在建池时一次性分配出来，就是池的 `DescriptorSets`。

### 2.4 块内布局限定符（只影响着色器/结构体的字节排布，不产生描述符）

| GLSL | 含义 | C# 侧对应 |
| --- | --- | --- |
| `layout(std140)` | UBO 成员按 std140 对齐 | C# 结构体的字段顺序/类型按同一规则排（见 `UniformBufferObject`） |
| `layout(std430)` | SSBO / push constant 成员按 std430 对齐 | 同上 |
| `layout(scalar)` | 标量块布局（要求设备特性 `scalarBlockLayout` / `VK_EXT_scalar_block_layout`） | 本项目未启用 |
| `layout(row_major)` / `layout(column_major)` | 矩阵按行/列主序存 | C# 侧矩阵的内存布局要匹配（默认列主序） |
| `layout(offset = N)`（块成员） | 该成员在**块内**的字节偏移 | 靠 C# 结构体布局（`StructLayout` / 字段顺序）对上 |
| `layout(align = N)`（块成员） | 该成员的对齐 | 同上 |
| `layout(packed)` / `layout(shared)` | 兼容老 GLSL 的块布局 | Vulkan 不支持（只能用 std140 / std430 / scalar） |

这些关键字**不会**变成描述符布局的参数；它们只决定"缓冲里的字节长什么样"。
`ManagedDescriptorBufferInfo.Offset / Range` 是"这段描述符指向缓冲的哪一段"，和 `layout(offset = N)` 无关。

### 2.5 push constant

| GLSL | 含义 | C# 落点 |
| --- | --- | --- |
| `layout(push_constant) uniform Push { … } pc;` | 一个 push constant 块 | `PushConstantRange { StageFlags, Offset, Size }`，传给 `CreateShaderPipelineLayout(pushConstantRanges:)` |
| `layout(push_constant, std430)` | 块成员按 std430 排布（push constant 默认就是 std430） | 同上（只影响字节排布） |
| 块成员的 `layout(offset = N)` | 成员相对块首地址的偏移 | 靠 C# 结构体布局；`PushConstantRange.Offset` 是**整块**相对 push constant 空间的起点 |
| ——（GLSL 无对应） | 运行时推数据 | `VKCommandBuffer.PushConstants(layout, stageFlags, offset, data)`；`offset` 相对该 range 起点，长度必须是 4 的倍数 |

push constant 不进描述符集、不进描述符池，`set` / `binding` 不能和 `push_constant` 一起写。

### 2.6 图像格式与内存访问限定符

| GLSL | 含义 | C# 落点 |
| --- | --- | --- |
| `layout(rgba8)`、`layout(rgba32f)`、`layout(r32ui)` …（写在 image 变量上） | 存储图像 / 存储纹素缓冲的**像素格式** | 创建 `VKImage` / `VKImageView` 时的 `Format`；必须与着色器里写的格式一致（**不是** `ImageLayout`） |
| `layout(readonly)` / `layout(writeonly)` | 只读 / 只写 | 限制着色器访问；不改变描述符类型（存储图像仍需 `StorageBit`，SSBO 仍需 `StorageBufferBit`） |
| `layout(coherent)` / `layout(volatile)` / `layout(restrict)` | 内存一致性 / 别名限定 | 影响同步方式；不改变描述符类型 |
| `layout(nonuniformEXT)` | 非一致索引（bindless / 数组采样） | 本项目未用 |

### 2.7 `location`：另一套编号

| GLSL | 含义 | C# 落点 |
| --- | --- | --- |
| 顶点着色器 `layout(location = L) in vec2 position;` | 第 L 号顶点属性 | `VertexInputAttributeDescription.Location = L`（[`ManagedPipelineStateCreateInfo.VertexAttributes`](SilkTryProject/Vulkan/ManagedStruct/ManagedPipelineStateCreateInfo.cs)）；它的 `binding` 字段指向 `VertexInputBindingDescription`，**不是** GLSL 的 `layout(binding = …)` |
| 顶点着色器 `layout(location = L, component = C) in …` | 同一 location 内从第 C 个分量开始 | 用更细的 `Format` + `Offset` 表达 |
| 片元着色器 `layout(location = L) out vec4 color;` | 第 L 个颜色附件 | 渲染通道子通道的 `ColorAttachments[L]`（及管线的颜色混合状态），不是描述符 |
| 阶段间 `layout(location = L) out/in …` | 顶点→片元等阶段间接口 | 只由着色器之间匹配，C# 不出现；变量名无所谓，**location 必须一致** |
| `layout(flat)` / `noperspective` / `centroid` / `sample` / `patch` | 插值/补丁限定 | 阶段间必须一致，C# 无参数 |
| `layout(location = L, index = I) out …` | 双源混合的第 I 个输出 | 本项目未用 |

### 2.8 其他 layout 关键字速查（和描述符无关）

| GLSL | 含义 | C# 落点 |
| --- | --- | --- |
| `layout(constant_id = N) const T x = v;` | 特化常量 | `VkSpecializationInfo`（本项目未封装） |
| `layout(local_size_x = X, local_size_y = Y, local_size_z = Z) in;` | compute 工作组大小 | `vkCmdDispatch` 参数（项目暂无 compute） |
| `layout(local_size_x_id = N)` | 工作组大小的特化常量 | 同上 |
| `layout(early_fragment_tests) in;` | 提前深度/模板测试 | 着色器执行模式，C# 无参数 |
| `layout(origin_upper_left)` / `layout(pixel_center_integer)` | `gl_FragCoord` 约定 | C# 无参数 |
| `layout(depth_less)` / `depth_greater` / `depth_any` / `depth_unchanged` | 深度输出约定 | 管线深度状态，C# 无参数 |
| `layout(vertices = N) out;` | 细分控制着色器输出顶点数 | 管线 tessellation 状态 |
| `layout(triangles, equal_spacing, cw) in;` | 细分图元参数 | 管线 tessellation 状态 |
| `layout(points/lines/triangles, …) in;`、`layout(triangle_strip, max_vertices = N) out;` | 几何着色器输入/输出 | 管线几何状态 |
| `layout(stream = N)` | 几何着色器输出流 | 管线状态 |
| `layout(xfb_buffer = B, xfb_offset = O, xfb_stride = S)` | transform feedback | 管线状态 |
| `layout(buffer_reference, std430)` | 缓冲设备地址 | 设备特性 + `VKBufferDeviceAddress`，本项目未用 |

---

## 3. 完整例子（教程的 UBO + 纹理 + push constant）

着色器：

```glsl
#version 450

layout(set = 0, binding = 0) uniform UniformBufferObject {
    mat4 model;
    mat4 view;
    mat4 proj;
} ubo;

layout(set = 0, binding = 1) uniform sampler2D texSampler;

layout(push_constant) uniform Push {
    vec4  tint;
    layout(offset = 32) float exposure;
} pc;
```

C#（按调用顺序）：

```csharp
// 1) 描述符集布局：字典的 key 就是 GLSL 的 binding 号（set 号不在这里）
VKDescriptorSetLayout setLayout = LogicalDevice.CreateDescriptorSetLayout(
    new Dictionary<uint, ManagedDescriptorSetLayoutBinding>
    {
        [0] = new(DescriptorType.UniformBuffer, ShaderStageFlags.VertexBit),
        [1] = new(DescriptorType.CombinedImageSampler, ShaderStageFlags.FragmentBit),
    });

// 2) 描述符池：挂在布局上（一个池只服务这一份布局），预算按绑定表 × 套数自动统计；
//    建池时就把 maxSets 套集全部分配好，直接取用
VKDescriptorPool pool = setLayout.CreateDescriptorPool(maxSets: 1);
VKDescriptorSet set = pool.DescriptorSets[0];

// 3) 管线布局：数组下标 = set 号（这里只用到 set 0）；pushConstantRanges 对应 layout(push_constant)
VKShaderPipelineLayout pipelineLayout = LogicalDevice.CreateShaderPipelineLayout(
    pushConstantRanges:
    [
        new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
            Offset = 0,
            Size = 36, // vec4 占 0..15，float 在 32..35
        }
    ],
    descriptorSetLayouts: [setLayout]);

// 4) 写资源：binding 号与 GLSL 完全一致；池只服务这一份布局，所以不用再传布局
set.WriteBuffers(
    DescriptorType.UniformBuffer,
    binding: 0,
    [new ManagedDescriptorBufferInfo(uniformBuffer, 0, (ulong)sizeof(UniformBufferObject))]);
set.WriteImages(
    DescriptorType.CombinedImageSampler,
    binding: 1,
    [new ManagedDescriptorImageInfo(textureImageView, ImageLayout.ShaderReadOnlyOptimal, textureSampler)]);

// 5) 录制时绑定：firstSet = 0 对应 layout(set = 0)
commandBuffer.BindDescriptorSets(PipelineBindPoint.Graphics, pipelineLayout, firstSet: 0, [set]);
commandBuffer.PushConstants(
    pipelineLayout,
    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
    offset: 0,
    data); // 按 Push 结构体的字节排好（这里有 C# 结构体布局与 GLSL 对齐规则的对应问题）
```

---

## 4. 容易踩的坑

1. **`set` 号不是布局对象里的字段**。一份 `VKDescriptorSetLayout` 只描述"一个 set 内的绑定"；
   set 号是你在 `CreateShaderPipelineLayout(descriptorSetLayouts: [...])` 里放的位置。
   不写 `layout(set = …)` 时 GLSL 默认 set 0，那也必须在数组第 0 位放一份布局。
   同理，一个 `VKDescriptorPool` 只服务一份布局：需要给多份布局分配集时，各建各的池。
2. **不同 set 的 binding 编号互相独立**：set 0 的 binding 0 与 set 1 的 binding 0 是两条不同的绑定。
3. **顶点输入的 `binding` ≠ GLSL `layout(binding = …)`**。顶点输入用 `layout(location = L) in …`，
   C# 侧对应 `VertexInputAttributeDescription.Location`；`binding` 只是"从第几个顶点缓冲取"。
4. **`layout(rgba8)` 不是 `ImageLayout`**。前者是像素格式（对着 `VKImage` / `VKImageView` 的 `Format`），
   后者是图像在某个时刻的用途/同步状态（由 `PipelineBarrier` 决定）。
5. **块布局限定符不产生参数**。`std140` / `std430` / `row_major` / `offset` / `align` 只影响缓冲里的字节排布，
   不会变成 `ManagedDescriptorBufferInfo.Offset / Range`。
6. **`DescriptorCount` 与 GLSL 数组长度一一对应**：标量声明 = 1，`tex[4]` = 4；
   写入第 i 个元素用 `firstDescriptor: i`。
7. **GLSL 里没有写法的东西**：Dynamic 描述符（`UniformBufferDynamic` / `StorageBufferDynamic`）、
   不可变采样器、纯采样器描述符（`DescriptorType.Sampler`）都是 API 侧的决定，着色器该写什么还写什么
   （不可变采样器下 GLSL 照旧是 `uniform sampler2D tex;`）。
8. **`StageFlags` 取并集**：一个绑定在顶点和片元两个阶段都访问，就 `VertexBit | FragmentBit`；
   只在片元用 → `FragmentBit`；漏写会让着色器读不到该绑定。
9. **`firstDescriptor` 从 0 起**，就是 GLSL 数组下标；`firstSet` 是"第一个要绑的 set 号"，
   后续集按 `firstSet + i` 对应管线布局里的 set。
10. **生命周期**：描述符集布局要活得比管线布局久；描述符集要活得比用到它的命令缓冲/已提交命令久；
    它引用的缓冲/视图/采样器也要活得比它久。
11. **建池即建集**：本仓库没有"运行时再分配一套"的入口 —— `maxSets` 套集在
    `CreateDescriptorPool` 里就全部分配好了（`pool.DescriptorSets`），也没有单独归还；
    要更多集就再建一个池（同一个布局可以挂多个池）。

---

## 5. 相关源码

- [`ManagedStruct/ManagedDescriptorSetLayoutBinding`](SilkTryProject/Vulkan/ManagedStruct/ManagedDescriptorSetLayoutBinding.cs) —— 一条 binding 的内容（类型 / 数量 / 阶段 / 不可变采样器；binding 号是字典 key）
- [`VKDescriptorSetLayout`](SilkTryProject/Vulkan/VKDescriptorSetLayout.cs) —— 一个 set 的布局；创建/销毁名下的池与集、兼容性校验
- [`VKShaderPipelineLayout`](SilkTryProject/Vulkan/VKShaderPipelineLayout.cs) —— set 的顺序 + push constant 范围
- [`VKDescriptorPool`](SilkTryProject/Vulkan/VKDescriptorPool.cs) —— 挂在布局下、只服务这一份布局；预算自动统计 + 建池即建集
- [`VKDescriptorSet`](SilkTryProject/Vulkan/VKDescriptorSet.cs) —— `WriteBuffers` / `WriteImages` / `WriteTexelBufferViews` / `CopyTo` / 绑定
- [`ManagedStruct/ManagedDescriptorBufferInfo`](SilkTryProject/Vulkan/ManagedStruct/ManagedDescriptorBufferInfo.cs) / [`ManagedStruct/ManagedDescriptorImageInfo`](SilkTryProject/Vulkan/ManagedStruct/ManagedDescriptorImageInfo.cs) —— 写入内容
- [`ManagedStruct/ManagedPipelineStateCreateInfo`](SilkTryProject/Vulkan/ManagedStruct/ManagedPipelineStateCreateInfo.cs) —— 顶点输入 `location` → `VertexInputAttributeDescription`
- [`VKCommandBuffer`](SilkTryProject/Vulkan/VKCommandBuffer.cs) —— `BindDescriptorSets` / `PushConstants`
- 着色器示例：[`simpleShader.vert`](SilkTryProject/Shaders/simpleShader.vert) / [`simpleShader.frag`](SilkTryProject/Shaders/simpleShader.frag)
