# API 形态设计规范（API Form Design Standard）

> 适用于"**托管代码包装原生资源句柄**"这一类 API 的设计与实现。
>
> 把本文件放在仓库根目录，并在 `.github/copilot-instructions.md` 中引用它，Copilot 在新代码里就会按同一套形态生成接口。
> 本文使用中性名称（`Widget` / `WidgetView` / `NativeDevice` / `NativeHandle` / `NativeApi`）；把项目里的真实类型代入即可，规则本身不变。
> 与本仓库 Vulkan 实现的对应关系见 [§15](#15-与本仓库-vulkan-实现的映射)。

---

## 0. 一句话总则

> **一个包装类只持有一个原生句柄；句柄只在包装层内部流转；归属关系由父对象的登记表说话；创建自上而下、销毁自下而上。**

---

## 1. 核心原则

| # | 原则 | 具体要求 |
|---|------|----------|
| **P1** | 一个类 = 一个句柄 | 类里出现第二个原生句柄字段，就是"该拆类"的信号（见 [§13](#13-什么时候该拆类)） |
| **P2** | 句柄是私有实现细节 | `public` 签名里不出现原生句柄；`internal` 签名可以出现 |
| **P3** | 归属靠登记表 | 永不信任手里的句柄值，而是反查父对象有没有登记过自己 |
| **P4** | 建在父对象，做在持句柄者 | `public` 创建入口在父对象上；真正的原生调用由持有底层句柄的对象执行 |
| **P5** | 销毁沿归属链上转 | `Destroy()` 先销毁父对象登记表里的全部子对象（子先于父），再沿归属链上转 → … → 执行方 |

**为什么 P3 不能省**：句柄只是一个数值/结构体，它无法证明自己还活着。可能已经重复销毁、父对象已销毁、或压根不是这一族对象的句柄。唯一可信的存活证明是"**父对象的登记表里还有我**"，且父对象自己也有效。

---

## 2. 包装类的标准形态

新写一个包装类时，直接照抄下面的骨架，只替换名称与元数据：

```csharp
/// <summary>
/// Xxx（原生对象）的包装。
/// 它是什么 / 对应原生 API 的哪一步：……
///
/// 归属：父对象是 Yyy —— 为什么是它：……
///（原生创建/销毁本身是设备级调用，真正的调用由 NativeDevice 执行 —— 它才持有底层句柄；
///  Yyy 是发起方 + 登记方。）
///
/// 分工：创建 → Yyy.CreateXxx；销毁 → Yyy.DestroyXxx。
///
/// 划界线：哪些事本类明确不做。
/// </summary>
public partial class WidgetView
{
    /// <summary>父对象：这个对象是"谁的"。销毁、归属校验都走它。</summary>
    public readonly Widget Parent;

    /// <summary>创建时定下来的不可变元数据（语义同原生 create-info 的字段）。</summary>
    public readonly SomeOptions Options;

    private readonly NativeHandle handle;

    // 仅当本类内部需要直接调用原生 API 时才需要这一行
    private readonly static NativeApi Api = NativeApi.GetApi();

    internal WidgetView(Widget parent, NativeHandle handle, SomeOptions options)
    {
        Parent = parent;
        this.handle = handle;
        Options = options;
    }

    /// <summary>
    /// 和别的包装类一个套路：不去信任手里的句柄，而是反查父对象有没有登记过自己
    ///（登记发生在 Widget.CreateWidgetView 里）。
    /// </summary>
    public bool Valid => Parent.IsWidgetViewOfThis(this);

    private NativeHandle CheckedHandle
    {
        get
        {
            if (!Valid)
            {
                throw new InvalidOperationException("The widget view is not valid.");
            }

            return handle;
        }
    }

    /// <summary>
    /// 销毁：把"从父对象集合里摘掉 + 调原生销毁"交给父对象做
    ///（父对象再把实际的设备调用和句柄转给 NativeDevice）。
    /// </summary>
    public void Destroy()
        => Parent.DestroyWidgetView(this, CheckedHandle);

    /// <summary>参数是裸句柄，只在包装层内部做归属校验用，所以是 internal。</summary>
    internal bool IsHandleOfThis(NativeHandle handleForCheck)
        => handleForCheck.Handle == handle.Handle;
}
```

### 2.1 成员逐一说明（都是硬性要求）

| 成员 | 形态 | 理由 |
|------|------|------|
| `Parent` | `public readonly` | 归属关系必须对使用者可见、且构造后不可变（同一子类型有两种所有者时，类型改为 `I<子类型>Parent` 接口，见 [§6.4](#64-例外资源不归我们所有)） |
| 元数据 | `public readonly`；数组 → `ReadOnlyCollection<T>` | 创建时定下来的东西不应被改；对外暴露不可变视图 |
| `handle` | `private readonly`，名字就叫 `handle` | 与类型名 `NativeHandle` 区分；只有本类能读 |
| 原生 API 入口 | `private readonly static NativeApi Api = NativeApi.GetApi();` | 每类一份，避免每个方法 `GetApi()`；只在需要时加 |
| 构造函数 | `internal` | 外部只能通过父对象的 `public` 工厂拿实例，保证一定被登记 |
| `Valid` | `public bool Valid => Parent.IsXxxOfThis(this);` | 现算、不缓存；缓存会掩盖父对象失效 |
| `CheckedHandle` | **属性**（不是方法），`private` | 读法像字段；`Parent.X(this, CheckedHandle)` 自然；迫使调用方走校验 |
| `Destroy()` | `public void`，**无参** | 使用方永远不需要、也不允许指定句柄 |
| `IsHandleOfThis` | `internal bool`，用 `.Handle` 比较 | 原生句柄结构体的 `==` 语义依赖重载，显式比较字段更可靠 |

### 2.2 必须避免的写法

```csharp
// ❌ public 返回/接收裸句柄
public NativeHandle GetHandle();
public static WidgetView Wrap(NativeHandle handle);

// ❌ 公开句柄字段
public readonly NativeHandle Handle;

// ❌ 跳过 Valid 直接用句柄
public void DoSomething() => Api.DoThing(handle);   // 应该用 CheckedHandle

// ❌ public 构造函数
public WidgetView(Widget parent, NativeHandle handle) { ... }

// ❌ 一个类塞两个句柄（见 §13）
private readonly FirstHandle firstHandle;
private readonly SecondHandle secondHandle;
```

---

## 3. 三种角色与分层

| 角色 | 典型类型 | 职责 | 可见性 |
|------|----------|------|--------|
| **发起方**（父包装类） | `Widget` | 校验自己有效 → 交出 `CheckedHandle` → 包装返回的句柄 → 登记 | `public` 工厂 |
| **执行方**（持底层句柄者） | `NativeDevice` | 用自己私有的底层句柄真正调用原生 API，**返回**新建的裸句柄 | `internal` |
| **收口方**（static） | `WidgetChild.CreateXxx(static)` | 参数里带**包装类数组**、必须读其它实例的私有句柄时出现 | `internal static` |

### 3.1 普通实例方法 vs `static` 的判定

- 参数里只有"标量 + 由调用方自己传下来的裸句柄" → **普通实例方法**即可。
- 参数里有**包装类数组**（需要读 `items[i].CheckedHandle`） → **必须 `internal static`**，并且放在"那个句柄类型"所属的类里。

原因：`private` 是**类型级**可见性，同一个类的代码可以访问任意实例的私有成员；这样就**不需要任何"把句柄交出来"的接口**，也不会让对象把句柄外泄。这就是"**谁的句柄类型，谁就在自己的类里读写它**"。

### 3.2 调用链模板

```text
【创建】
Widget.CreateWidgetView(...)                               // public、零裸句柄
  → NativeDevice.CreateWidgetView(this, CheckedHandle, …)   // internal：执行原生调用，返回新句柄
    → （仅当参数含包装类数组）ChildType.CreateWidgetView(…) // internal static：读各实例私有句柄
  → new WidgetView(…) + widgetViews[result] = allocationCallbacks

【销毁】
WidgetView.Destroy()
  → Widget.DestroyWidgetView(this, CheckedHandle)            // 摘登记，取出创建时的 ac
    → NativeDevice.DestroyWidgetView(this, handle, ac)       // 真正的原生销毁
```

**铁律**：任何对象都不"返回"自己的句柄；只允许把 `CheckedHandle` **当参数往下传**（传入正确值 = 授权），下一环再复核一次。

---

## 4. 父对象的登记表

```csharp
// 写法和别的登记表一样：创建时登记进来，之后靠 IsWidgetViewOfThis() 反查归属
//（WidgetView.Valid 就是这么来的）、销毁时摘掉，value 是创建时用的 allocationCallbacks。
private readonly ConcurrentDictionary<WidgetView, AllocationCallbacks?> widgetViews = new ();

/// <summary>本对象身上有没有登记过它 —— WidgetView.Valid 靠它。</summary>
public bool IsWidgetViewOfThis(WidgetView widgetView)
    => widgetViews.ContainsKey(widgetView) && Valid;
```

规则：

1. 表放在**父对象**上，不放设备、不放全局/静态。
2. 命名 = 子类型名转 camelCase 复数（`widgetViews`、`imageViews`、`pipelineLayouts`）。
3. `value` 存创建时传入的 `AllocationCallbacks?`，销毁时 `TryRemove` 顺便取出，不再额外存字段。
4. 归属校验固定写成 `ContainsKey(x) && Valid` —— **必须带父对象自己的 `Valid`**，只查表不够。
5. 销毁时先校验句柄、再摘登记；任一步失败就返回 `false`，**不要**执行原生销毁。
6. 没有分配回调的资源（例如从池里分配的），`value` 用 `byte`，保持"表 = 归属证据"的语义不变。
7. 用 `ConcurrentDictionary`，不用 `Dictionary` + 外部加锁。

---

## 5. 创建入口的规范

`public` 工厂放在**父对象**上。

### 5.1 签名

```csharp
public WidgetView CreateWidgetView(
    SomeOption optionA,                 // 必填项无默认值
    uint count = 1,                     // 有合理默认值的给默认值
    WidgetCreateFlags flags = WidgetCreateFlags.None,
    AllocationCallbacks? allocationCallbacks = null)   // 永远放最后
```

- 参数对应原生 `create-info` 的字段；
- **用 `T[]?` 代替"指针 + count"**（`count` 由数组长度推导，不单独暴露）；
- 必填项不给默认值；标志枚举默认 `None`；
- `allocationCallbacks` 永远最后一个、默认 `null`；
- 不接收裸句柄，也不返回裸句柄。

### 5.2 方法体（固定四步）

```csharp
public WidgetView CreateWidgetView(...)
{
    NativeHandle handle = Widget.CreateWidgetView(this, CheckedHandle, ...);   // ① 执行方
    WidgetView result = new(this, handle, ...);                                // ② 包装
    widgetViews[result] = allocationCallbacks;                                 // ③ 登记
    return result;                                                             // ④ 返回
}
```

- ① 之前如需用到父对象的句柄，直接 `CheckedHandle`（同时完成一次有效性检查）。
- 需要"一键创建"时，可以再加一个 `public static` 便捷工厂（`Widget.CreateWidgetView(device, …)`），但内部必须走同一套流程。
- **多对象创建要回滚**：一次原生流程里建了多个对象，后一步失败必须 try/catch 销毁已建的，再抛出。

### 5.3 多条创建路径：抽象基类 + 具体子类

同一个原生对象，如果创建参数分成**互斥的几套**（典型：图形管线的"配合渲染通道"与"动态渲染"），
不要在签名里堆一堆可空参数、再在运行期判空，而是：

- 基类写成 `abstract`，只放几条路径**共享**的东西（`Parent`、公共元数据、`handle`、`Valid`、`Destroy()`）；
- 每条路径一个子类，把该路径专属的元数据放进去，**字段不设可空** —— 走了这条路径就一定拿得到；
- 父对象上每条路径一个 `public` 工厂，名字带路径后缀，返回**具体子类**；
- 父对象的登记表仍然以**基类**为 key：`Valid` 只反查一处，销毁也只写一遍；
- 需要追加存活条件时在子类里 `override Valid`（例如"配套的渲染通道还有效"）；
- 使用方（录制命令的包装类）用 `is` / `switch` 按类型判断"这个对象是哪条路径建的、当前环境配不配"，
  **`switch` 必须有 `default` 抛异常** —— 将来新增子类时才不会静默放行。

```csharp
public abstract partial class WidgetView { /* Parent / handle / Valid / Destroy */ }

public class WidgetView_OptionA : WidgetView { public readonly AOptions Options; }   // 非空
public class WidgetView_OptionB : WidgetView { public readonly BOptions Options; }   // 非空

public WidgetView_OptionA CreateWidgetViewOptionA(AOptions options, …);
public WidgetView_OptionB CreateWidgetViewOptionB(BOptions options, …);
```

> 好处：类型本身就说明了"创建时给了什么"，不用靠注释和运行期判空；两条路径的原生参数也各自有完整签名。

---

## 6. 销毁入口的规范

### 6.1 对外形态

- 包装类只提供 **无参** `public void Destroy()`；
- 逐级转发的方法是 `internal bool DestroyWidgetView(实例, 裸句柄, [ac])`；
- 返回 `false` 表示"不是我的"或"已摘除"，**静默返回、不抛异常**；
- 原生销毁没有返回值（多数 C API 如此），所以销毁本身不抛；**创建**失败才抛 `NotSupportedException`。

### 6.2 每一级的固定写法

```csharp
// 父对象：校验 + 摘登记（顺便取出 ac）
internal bool DestroyWidgetView(WidgetView widgetView, NativeHandle handle)
{
    if (!widgetView.IsHandleOfThis(handle) || !widgetViews.TryRemove(widgetView, out var allocationCallbacks))
    {
        return false;
    }

    return Parent.DestroyWidgetView(widgetView, handle, allocationCallbacks);
}

// 执行方：再复核一次，然后才是原生销毁
internal unsafe bool DestroyWidgetView(WidgetView widgetView, NativeHandle handle, AllocationCallbacks? allocationCallbacks)
{
    Device device = CheckedHandle;

    if (!widgetView.IsHandleOfThis(handle))
    {
        return false;
    }

    // … 调原生 Destroy …
    return true;
}
```

### 6.3 销毁顺序

- **子先于父**由包装层保证：父对象的 `Destroy()` 先遍历自己的登记表，逐个调用子对象的 `Destroy()`，最后才销毁自己；调用方不需要手动按顺序销毁。
- 子对象的销毁不判断 `Valid`，而是用 `try-catch` 包住：销毁必须是 best-effort 的，单个子对象失败不能阻断其余子对象的销毁。
- 子对象自己的 `Destroy()` 也会先销毁它的子对象，所以整条归属链是递归地"自下而上"销毁的。
- 每个 `Destroy()` / `DestroyXxx` 的文档注释都必须写明它按什么顺序销毁哪些子对象。
- 若资源自身有硬性先后（例如 A 必须比 B 晚死），**用归属链表达它**：把被依赖者升级为父对象（见 §13），这样父对象的 `Destroy()` 会自然先销毁子对象。
- 不归我们销毁的资源（例如交换链图像，见 §6.4）不能放进"自动销毁"的循环里；它们的子对象（如图像视图）仍需在父对象销毁前单独清掉。

父对象一侧的固定写法（先快照登记表，避免子对象在销毁过程中摘登记影响遍历；不判断 `Valid`，每个子对象的销毁都用 `try-catch` 包住，单个失败不阻断其余对象）：

```csharp
public void Destroy()
{
    foreach (var child in children.Keys.ToArray())
    {
        try
        {
            child.Destroy();
        }
        catch
        {
            // 忽略单个子对象的销毁失败，保证其余子对象继续销毁。
        }
    }

    Parent.DestroyWidget(this, CheckedHandle);
}
```

### 6.4 例外：资源不归我们所有

有时同一个子类型会从**两种所有者**下面被创建出来：

- 我们自己创建、由我们销毁的（比如绑在 `OwnerMemory` 上的对象）；
- 运行时/外部容器给的、我们只能使用不能销毁的（比如从 `OwnerRuntime` 里取到的实例）。

**正确做法：把"所有者"抽成接口，让 `Parent` 保持非空。** 不要用可空 `Parent` 来表达这件事。

```csharp
/// <summary>子对象的所有者。谁造出来的谁实现它，各自在自己的登记表上反查。</summary>
public interface IWidgetOwner
{
    bool IsWidgetOfThis(Widget widget);

    /// <summary>所有者所属的设备 —— 子对象顺着它找回执行方。</summary>
    NativeDevice Device { get; }
}

public partial class Widget
{
    /// <summary>父对象：这个对象是"谁的"。销毁、归属校验都走它。**永远非空。**</summary>
    public readonly IWidgetOwner Parent;

    public bool Valid => Parent.IsWidgetOfThis(this);

    public void Destroy()
    {
        // "能不能自己销毁"由所有者类型回答，而不是判空。
        if (Parent is OwnerMemory memory)
        {
            memory.DestroyWidget(this, CheckedHandle);
            return;
        }

        throw new InvalidOperationException(
            "This instance is owned by the runtime, cannot destroy manually.");
    }
}
```

两类所有者各自实现同一个接口，**反查的是自己的那份数据**：

```csharp
// ① 我们自己造的所有者：有登记表（建时登记、销毁时摘除）
public partial class OwnerMemory : IWidgetOwner
{
    private readonly ConcurrentDictionary<Widget, AllocationCallbacks?> widgets = new ();

    NativeDevice IWidgetOwner.Device => Parent;          // 显式实现，避免和本类的 Parent 撞名
    public bool IsWidgetOfThis(Widget widget) => widgets.ContainsKey(widget) && Valid;

    internal bool DestroyWidget(Widget widget, NativeHandle handle) { /* 摘登记 → 交给设备 */ }
}

// ② 运行时所有者：没有"创建登记"这一步，但它本来就持有这些实例
public partial class OwnerRuntime : IWidgetOwner
{
    public readonly ReadOnlyCollection<Widget> Widgets;   // 创建时一次性生成

    NativeDevice IWidgetOwner.Device => Parent;
    public bool IsWidgetOfThis(Widget widget) => Widgets.Contains(widget) && Valid;

    // 没有 DestroyWidget —— 这些实例不归我们销毁
}
```

规则：

1. **`Parent` 不设可空**，于是 `Valid` 永远是单一表达式 `Parent.IsXxxOfThis(this)`，不会因为"漏判空"而崩溃或走错分支。
2. 每个所有者**反查自己那份数据**：我们造的就查登记表，运行时给的就查它本来就持有的集合 —— 两者都不需要"信任手里的句柄"。
3. 反查一律 `&& Valid`，即所有者自己失效时，它名下的所有子对象一并失效。
4. **"能否销毁"用 `is` 类型判断**（`Parent is OwnerMemory`），禁止写 `Parent == null`；不归我们销毁的直接抛 `InvalidOperationException`。
5. 需要设备时由接口暴露 `Device`，并用**显式接口实现**（`NativeDevice IWidgetOwner.Device => Parent;`）避免与具体类已有的 `Parent` 属性冲突。
6. 不要再引入 `IsCreatedXxx` 这类属性来重复表达同一件事：`Valid` 回答"还活着吗"，所有者类型回答"能不能销毁"。确实需要在**调用前**预判时，用 `Parent is OwnerMemory`（或单独提供一个只读属性），但不要把它和 `Valid` 混为一谈。
7. 接口命名为 `I<子类型>Parent`，成员命名为 `Is<子类型>OfThis` —— 与具体父类上的归属方法保持同一套命名。

---

## 7. 命名规范

| 类别 | 规则 | 示例 |
|------|------|------|
| 包装类 | 原生类型名（去掉统一前缀/后缀） | `WidgetView` |
| 父对象字段 | 固定叫 `Parent` | `public readonly Widget Parent;` |
| 句柄字段 | 固定叫 `handle`（小写） | `private readonly NativeHandle handle;` |
| 校验属性 | 固定叫 `CheckedHandle` | |
| 归属校验（父） | `Is<Child>OfThis` | `IsWidgetViewOfThis` |
| 归属校验（子） | `IsHandleOfThis` | |
| 创建 | `Create<Child>` | `CreateWidgetView` |
| 销毁 | `Destroy<Child>` | `DestroyWidgetView` |
| 登记表 | 子类型名 camelCase 复数 | `widgetViews` |
| 参数 | camelCase，与原生字段同名 | `offset`、`range`、`allocationCallbacks` |

**拼写以正确性为准，且全局一致**：API 一律使用标准英文拼写（`Destroy`、`handle`、`extensions`、`logical`……）。发现旧代码里的拼写错误时，一次性全局改名（连同注释与文档），**不要新旧混用、不要为了过渡而保留两套 API**。

---

## 8. 访问性规范

| 访问性 | 允许出现 | 禁止出现 |
|--------|----------|----------|
| `public` | 包装类、`Parent`、元数据字段、`Valid`、`Destroy()`、`Create*` 工厂、`Is*OfThis` | **任何原生句柄**（参数/返回/字段） |
| `internal` | 带裸句柄的创建/销毁方法、`IsHandleOfThis`、构造函数 | — |
| `private` | `handle`、`CheckedHandle`、登记表、原生 API 静态字段、辅助方法 | — |

原则：**`public` = 安全面子集**，可以放心给使用者；`internal` = 包装层内部协议，句柄可以在这一层流动。

> 历史代码里若出现与上表不一致的例外（例如某个 `IsHandleOfThis` 被写成了 `public`），视为历史遗留，**不要照抄到新代码**。

---

## 9. 参数与返回值设计

- 可选参数从右往左排，`allocationCallbacks` 永远最后。
- 集合参数对外用数组（调用方最方便），对象内部存 `ReadOnlyCollection<T>`。
- "可选"用 `T?` + `null` 表达，不用哨兵值（`uint.MaxValue` / `null` 指针语义要在注释里说明）。
- 不用 `out` 把句柄返回给调用方。
- 返回包装类而不是句柄；返回值非空（失败抛异常，不返回 `null`）。
- 原生结构体参数优先用**带名参数的构造函数**，避免一堆位置参数写错。

---

## 10. 异常与校验规范

| 场景 | 异常类型 | 消息风格 | 示例 |
|------|----------|----------|------|
| 包装对象自身失效 | `InvalidOperationException` | `The <thing> is not valid.` | `"The widget view is not valid."` |
| 传入句柄不属于该对象 / 父对象不符 | `ArgumentException` | `The provided <x> is not <y>.` | `"The provided pipeline layout is not associated with this render pass."` |
| 原生调用返回非成功 | `NotSupportedException` | `failed to create <thing>!` | `"failed to create widget view!"` |
| 参数非法（空数组、长度不符） | `ArgumentException` + `nameof(...)` | 说明约束 | `"A widget needs at least one stage."` |

校验位置：

1. **入口**做廉价检查（父对象 `Valid`、参数非空）；
2. **执行方**再复核一次（句柄归属 + 关联对象属于同一设备）；
3. 消息用英文短句，与既有代码保持一致（包括大小写与结尾 `!`）。

---

## 11. 文件组织

一个功能 = 一个文件，文件名取**子对象**的名字。文件内固定三段，顺序不变：

```csharp
using ...;                       // 只在文件顶部出现一次

namespace Xxx.Yyy;               // 文件作用域命名空间

public partial class WidgetView { ... }        // ① 包装类本身

public partial class Widget { ... }            // ② 父对象：登记表 + 工厂 + 销毁转发

public partial class NativeDevice              // ③ 执行方：设备级执行/销毁
{
    #region ...（教程/功能章节名）

    // 创建、销毁

    #endregion
}
```

- 用 `partial` 把"同一个功能"的三层放在一起，比按类型拆文件更好读；
- 该类型已经很大时，才拆成独立文件；
- `#region` 只用于执行方（设备类）并按功能命名，不要给每个方法都套 region。

---

## 12. 文档注释规范

类注释固定四段（顺序也固定）：

1. **是什么**：对应原生流程的哪一步、解决什么问题；
2. **归属**：父对象是谁、为什么是它；
3. **分工**：创建/销毁分别由谁执行、句柄如何流转；
4. **划界线**：明确列出本类**不做**什么（防止后续把职责堆进来）。

方法注释至少包含：

- **调用链**（多级时用缩进箭头画出来）；
- **参数含义**（与原生字段对应关系）；
- **生命周期顺序**（谁必须先销毁）；
- **为什么是 `internal` / `static`**（如果有特殊性）。

抽象基类 + 多条创建路径时，类注释还要写清：**每条路径对应哪个子类 / 哪个 `public` 工厂**，以及两条路径为什么互斥。

风格要求：

- 解释"**为什么**"和"**边界**"，不复述代码在做什么；
- 类型、方法用 `<see cref="..."/>` 或反引号；
- 中文正文 + 英文代码标识符，保持同一文件内风格统一。

---

## 13. 什么时候该拆类

出现以下任一信号，就应当拆：

1. 一个类里有**两个及以上原生句柄字段**；
2. 两个句柄之间有**硬性销毁顺序**；
3. 其中一个句柄可以被别的对象**独立使用**（不是"只为这个对象存在"）。

拆分步骤：

1. 把被依赖的句柄升级为**独立包装类**（`Layout`），完整实现 §2 的成员；
2. 原类改为它的子类：`Parent` 指向新父对象，`Valid` 改查新父对象；
3. 登记表跟着搬到新父对象；
4. 原来的"复合 `public` 入口"拆成**两步**：
   `renderPass.CreateLayout(...)` → `layout.CreatePipeline(...)`；
5. 销毁拆成两次，并在文档里写明"先管线、后布局"；
6. 同步更新所有交叉引用注释（被拆类、父类、执行方、相关 `Managed*` 结构体）。

> 拆分是**形态修正**，不是功能变更：不改变原生调用顺序、不改变参数语义。

---

## 14. 自检清单（提交前逐条打勾）

- [ ] 一个包装类只持有一个原生句柄
- [ ] `public` 签名里没有任何原生句柄（参数、返回、字段）
- [ ] 有 `public readonly Parent`
- [ ] `Valid` 反查父对象登记表，并且带父对象的 `Valid`
- [ ] `CheckedHandle` 是 `private` **属性**
- [ ] 构造函数是 `internal`
- [ ] `Destroy()` 无参，且通过父对象转发
- [ ] 父对象的 `Destroy()` 先销毁登记表里的全部子对象，再销毁自己
- [ ] 每级 `DestroyXxx` 返回 `bool`，先校验再摘登记
- [ ] 登记表在父对象上，`value` 存 `AllocationCallbacks?`
- [ ] 执行方复核了句柄归属
- [ ] 多对象创建失败时已回滚
- [ ] 参数含包装类数组时，用的是 `internal static` 收口
- [ ] 一处资源若有两种所有者，用 `I<子类型>Parent` 接口 + 非空 `Parent`，销毁处用 `is` 判断（不是判空）
- [ ] 类注释写了归属 / 分工 / 划界线
- [ ] 文档写明了生命周期顺序
- [ ] 命名使用标准英文拼写，且全仓库一致（不新旧混用）
- [ ] 同一原生对象有多条互斥创建路径时，用 `abstract` 基类 + 子类表达，而不是"可空参数 + 运行期判空"
- [ ] 子类里的路径专属元数据**不为可空**；类型即语义
- [ ] 消费方按子类类型分派时写了 `default` 分支
- [ ] 有录制 / 状态机语义的包装类（如命令缓冲），状态不匹配时**先抛异常**，不把非法调用送进原生 API

---

## 15. 与本仓库 Vulkan 实现的映射

| 本文中性名称 | 本仓库实现 |
|--------------|------------|
| `Widget`（父包装类） | `VKInstance`、`VKPhysicalDevice`、`VKLogicalDevice`、`VKCommandPool`、`VKDeviceMemory`、`VKBuffer`、`VKImage`、`VKRenderPass`、`VKShaderPipelineLayout` |
| `WidgetView`（子包装类） | `VKSurface`、`VKLogicalDevice`、`VKDeviceQueue`、`VKCommandPool`、`VKCommandBuffer`、`VKDeviceMemory`、`VKBuffer`、`VKBufferView`、`VKImage`、`VKImageView`、`VKRenderPass`、`VKFramebuffer`、`VKShaderPipelineLayout`、`VKShaderPipeline`、`VKShaderModule`、`VKSwapchain`、`VKFence`、`VKSemaphore` |
| `WidgetView` 的多路径子类（§5.3） | `VKShaderPipeline`（`abstract`）→ `VKRenderPassShaderPipeline` / `VKDynamicShaderPipeline` |
| `NativeDevice`（执行方） | `VKLogicalDevice` |
| `NativeHandle` | `Silk.NET.Vulkan.Instance` / `SurfaceKHR` / `PhysicalDevice` / `Device` / `Queue` / `CommandPool` / `CommandBuffer` / `DeviceMemory` / `Buffer` / `BufferView` / `Image` / `ImageView` / `RenderPass` / `Framebuffer` / `PipelineLayout` / `Pipeline` / `ShaderModule` / `SwapchainKHR` / `Fence` / `Semaphore` |
| `NativeApi` | `Silk.NET.Vulkan.Vk`（`Vk.GetApi()`） |
| `AllocationCallbacks` | `Silk.NET.Vulkan.AllocationCallbacks` |

> 同一个类往往同时出现在"父"和"子"两行里 —— 它既在自己的登记表上管着下一级，也被上一级登记。具体以树为准。

**完整的归属树**（每条边 = 父对象登记了子对象：`Valid` 反查它、`Destroy()` 沿它上转）：

```text
VKInstance                                   ← 根：PhysicalDevices 构造时枚举，surfaces 按需登记
 ├─ VKSurface                                ← 父：VKInstance（surfaces）
 └─ VKPhysicalDevice                         ← 父：VKInstance（PhysicalDevices）
     └─ VKLogicalDevice                      ← 父：VKPhysicalDevice（logicalDevices）
         │                                     持 Device 句柄，执行所有设备级调用
         ├─ VKDeviceQueue                    ← 父：VKLogicalDevice（EnabledDeviceQueues）
         │                                     随设备创建一次性取出，无 Destroy
         ├─ VKCommandPool                    ← 父：VKLogicalDevice（commandPools）
         │   └─ VKCommandBuffer              ← 父：VKCommandPool（commandBuffers）
         ├─ VKDeviceMemory                   ← 父：VKLogicalDevice（memories）
         │   ├─ VKBuffer                     ← 父：VKDeviceMemory（buffers）
         │   │   └─ VKBufferView             ← 父：VKBuffer（bufferViews）
         │   └─ VKImage                      ← 父：IVKImageParent
         │       │                              = VKDeviceMemory（images）| VKSwapchain（Images）
         │       └─ VKImageView              ← 父：VKImage（imageViews）
         ├─ VKRenderPass                     ← 父：VKLogicalDevice（renderPasses）
         │   └─ VKFramebuffer                ← 父：VKRenderPass（framebuffers）
         ├─ VKShaderPipelineLayout           ← 父：VKLogicalDevice（pipelineLayouts）
         │   └─ VKShaderPipeline (abstract)  ← 父：VKShaderPipelineLayout（shaderPipelines）
         │       ├─ VKRenderPassShaderPipeline  ← CreateRenderPassShaderPipeline
         │       └─ VKDynamicShaderPipeline     ← CreateDynamicShaderPipeline
         ├─ VKShaderModule                   ← 父：VKLogicalDevice（shaderModules）
         ├─ VKSwapchain                      ← 父：VKLogicalDevice（swapchains）
         │   └─ VKImage                      ← 父：VKSwapchain（Images）
         │       │                             驱动给的，不能单独 Destroy，随交换链一起销毁
         │       └─ VKImageView              ← 父：VKImage（imageViews）
         ├─ VKFence                          ← 父：VKLogicalDevice（fences）
         └─ VKSemaphore                      ← 父：VKLogicalDevice（semaphores）
```

**引用边**（不是归属：只为校验 / 使用，不参与销毁顺序）：

```text
VKSwapchain ──▶ VKSurface                   重建时复用同一个 surface；swapchain.Valid 要求 surface.Valid
VKFramebuffer ──▶ VKImageView[]             附件视图；帧缓冲必须先于这些视图销毁
VKRenderPassShaderPipeline ──▶ VKRenderPass 配合的渲染通道；通道必须先于管线销毁
VKShaderPipeline ──▶ VKShaderModule[]       Stages 引用的模块；模块必须先建、后毁
VKCommandBuffer ──▶ 录制时用到的帧缓冲 / 渲染通道 / 管线 / 顶点缓冲 …
```

**每条归属边的落点**（登记表都在父对象上；`Destroy()` 一律无参、由父对象转发）：

| 子对象 | 父对象（登记表） | 创建入口 | 销毁入口 |
|---|---|---|---|
| `VKSurface` | `VKInstance`（`surfaces`） | `VKInstance.CreateWindowSurface` | `VKSurface.Destroy` → `VKInstance.DestroyWindowSurface` |
| `VKPhysicalDevice` | `VKInstance`（`PhysicalDevices`） | `VKInstance` 构造时 `vkEnumeratePhysicalDevices` | 无（随实例） |
| `VKLogicalDevice` | `VKPhysicalDevice`（`logicalDevices`） | `VKPhysicalDevice.CreateLogicalDevice` | `Destroy` → `VKPhysicalDevice.DestroyLogicalDevice` |
| `VKDeviceQueue` | `VKLogicalDevice`（`EnabledDeviceQueues`） | `VKLogicalDevice` 构造时 `vkGetDeviceQueue` | 无（随设备） |
| `VKCommandPool` | `VKLogicalDevice`（`commandPools`） | `VKLogicalDevice.CreateCommandPool` | `Destroy` → `VKLogicalDevice.DestroyCommandPool` |
| `VKCommandBuffer` | `VKCommandPool`（`commandBuffers`） | `VKCommandPool.AllocateCommandBuffers` | `Destroy` → `VKCommandPool.FreeCommandBuffer` |
| `VKDeviceMemory` | `VKLogicalDevice`（`memories`） | `VKLogicalDevice.AllocateMemory` | `Destroy` → `VKLogicalDevice.DestroyMemory` |
| `VKBuffer` | `VKDeviceMemory`（`buffers`） | `VKDeviceMemory.CreateBuffer` | `Destroy` → `VKDeviceMemory.DestroyBuffer` |
| `VKBufferView` | `VKBuffer`（`bufferViews`） | `VKBuffer.CreateBufferView` | `Destroy` → `VKBuffer.DestroyBufferView` |
| `VKImage`（我们建的） | `VKDeviceMemory`（`images`） | `VKDeviceMemory.CreateImage` | `Destroy` → `VKDeviceMemory.DestroyImage` |
| `VKImage`（交换链的） | `VKSwapchain`（`Images`） | 交换链创建时 `vkGetSwapchainImagesKHR` | 不能单独销毁；`VKSwapchain.Destroy` 先清它的视图 |
| `VKImageView` | `VKImage`（`imageViews`） | `VKImage.CreateImageView` | `Destroy` → `VKImage.DestroyImageView` |
| `VKRenderPass` | `VKLogicalDevice`（`renderPasses`） | `VKLogicalDevice.CreateRenderPass` | `Destroy` → `VKLogicalDevice.DestroyRenderPass` |
| `VKFramebuffer` | `VKRenderPass`（`framebuffers`） | `VKRenderPass.CreateFramebuffer` | `Destroy` → `VKRenderPass.DestroyFramebuffer` |
| `VKShaderPipelineLayout` | `VKLogicalDevice`（`pipelineLayouts`） | `VKLogicalDevice.CreateShaderPipelineLayout` | `Destroy` → `VKLogicalDevice.DestroyShaderPipelineLayout` |
| `VKShaderPipeline` | `VKShaderPipelineLayout`（`shaderPipelines`） | `VKShaderPipelineLayout.CreateRenderPassShaderPipeline` / `CreateDynamicShaderPipeline` | `Destroy` → `VKShaderPipelineLayout.DestroyShaderPipeline` |
| `VKShaderModule` | `VKLogicalDevice`（`shaderModules`） | `VKLogicalDevice.CreateShaderModule` | `Destroy` → `VKLogicalDevice.DestroyShaderModule` |
| `VKSwapchain` | `VKLogicalDevice`（`swapchains`） | `VKLogicalDevice.TryCreateOrUpdateSwapchain` | `Destroy` → `VKLogicalDevice.DestroySwapchain` → `VKSurface.DestroySwapchain` |
| `VKFence` | `VKLogicalDevice`（`fences`） | `VKLogicalDevice.CreateFence` | `Destroy` → `VKLogicalDevice.DestroyFence` |
| `VKSemaphore` | `VKLogicalDevice`（`semaphores`） | `VKLogicalDevice.CreateSemaphore` | `Destroy` → `VKLogicalDevice.DestroySemaphore` |

> `VKShaderPipelineLayout` 的父对象是 `VKLogicalDevice`（`vkCreatePipelineLayout` 是设备级调用），
> 它**不再挂在渲染通道下面**：布局不绑定某条通道，配合哪条通道是 `VKRenderPassShaderPipeline` 的属性。

**参考实现，建议新代码照着它们写**：

- 最简形态（单句柄 + 引用父级）：`Vulkan/VKBufferView.cs`
- 多级形态（一句话柄拆成父子两级）：`Vulkan/VKShaderPipelineLayout.cs` + `Vulkan/VKShaderPipeline.cs`
- 多路径形态（一个原生对象、两条互斥创建路径，看 §5.3）：`Vulkan/VKShaderPipeline.cs`
- 有状态录制（渲染范围状态机 + 按子类类型做兼容性校验，看 §14）：`Vulkan/VKCommandBuffer.cs`
