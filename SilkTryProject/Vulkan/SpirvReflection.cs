using Silk.NET.Vulkan;

namespace SilkTry.Vulkan;

/// <summary>
/// 从 SPIR-V 字节码里读出"顶点着色器声明了哪些顶点输入属性"的小反射器。
///
/// 为什么需要它：教程《Vertex buffer creation》里那几条 VkVertexInputAttributeDescription 是**手抄**的 ——
/// 对着 `layout(location = 0) in vec2 position;` 写 location = 0 / format = R32G32Sfloat / offset = 0。
/// 手抄容易和着色器里写的对不上（校验层只会报个很含糊的错），而这段信息本来就在 SPIR-V 里：
///   · Location 装饰（Decoration = 30）给了 location；
///   · OpTypePointer → OpTypeVector → OpTypeFloat / OpTypeInt 这条类型链给了"几个分量、什么类型"，
///     换算成 VkFormat 就是 format；
/// 所以让着色器模块自己去读，比手抄可靠。
///
/// 做法（只读模块头 + 扫一遍指令流，不依赖任何第三方 SPIR-V 库）：
///   ① SPIR-V 头 5 个字：魔数 / 版本 / 生成器 / id 上界 / 保留。指令流从第 6 个字开始；
///   ② 每条指令的第 1 个字 = (字数 &lt;&lt; 16) | opcode，字数含它自己，所以能一条条往前跳；
///   ③ 一边跳一边收：OpTypePointer / OpTypeVector / OpTypeFloat / OpTypeInt 建类型表，
///      OpDecorate 收 Location 与 BuiltIn，OpEntryPoint 收"入口点用它哪些变量"，OpVariable 收输入变量；
///   ④ 最后只取**顶点入口点**（ExecutionModel = Vertex）那一份接口列表里的输入变量，
///      按 location 升序，offset 按"紧凑排布"累加（和教程 stride = 各属性大小之和 一致）。
///
/// 这个类不碰任何 Vulkan 句柄（只读 VKShaderModule 存着的那段字节），所以只是个 internal 工具类，
/// 入口挂在 VKShaderModule.GetVertexInputAttributes 上。
/// </summary>
internal static class SpirvReflection
{
    /// <summary>SPIR-V 魔数（小端写下就是字节 03 02 23 07）。</summary>
    private const uint MagicNumber = 0x07230203;

    /// <summary>字节序相反的模块：魔数会读成这个值，此时每个字都要翻一次字节。</summary>
    private const uint MagicNumberByteSwapped = 0x03022307;

    /// <summary>模块头占 5 个字。</summary>
    private const int HeaderWordCount = 5;

    // 只用到这几个 opcode（完整的 opcode 表见 SPIR-V 规范第 3 章）。
    private const ushort OpEntryPoint = 15;
    private const ushort OpTypeInt = 21;
    private const ushort OpTypeFloat = 22;
    private const ushort OpTypeVector = 23;
    private const ushort OpTypePointer = 32;
    private const ushort OpVariable = 59;
    private const ushort OpDecorate = 71;

    private const uint ExecutionModelVertex = 0;
    private const uint StorageClassInput = 1;
    private const uint DecorationBuiltIn = 11;
    private const uint DecorationLocation = 30;

    /// <summary>32 位标量 / 向量的类型信息（OpTypeFloat / OpTypeInt 的结果）。</summary>
    private readonly record struct ScalarInfo(uint Width, bool IsFloat, bool IsSigned);

    /// <summary>向量类型信息（OpTypeVector 的结果）：分量类型 id + 分量个数。</summary>
    private readonly record struct VectorInfo(uint ComponentType, uint ComponentCount);

    /// <summary>
    /// 反射出顶点输入属性。调用方是 VKShaderModule.GetVertexInputAttributes（它传的就是模块自己存着的那段字节）。
    /// </summary>
    internal static VertexInputAttributeDescription[] GetVertexInputAttributes(IReadOnlyList<byte> code, uint binding)
    {
        if (code == null || code.Count < HeaderWordCount * 4)
        {
            throw new ArgumentException("The SPIR-V code is too short to be a module.", nameof(code));
        }

        if (code.Count % 4 != 0)
        {
            throw new ArgumentException("The size of the SPIR-V code must be a multiple of 4 bytes.", nameof(code));
        }

        bool byteSwapped = ReadWord(code, 0, false) == MagicNumberByteSwapped;
        if (ReadWord(code, 0, byteSwapped) != MagicNumber)
        {
            throw new NotSupportedException("The given bytes are not a SPIR-V module (bad magic number).");
        }

        int wordCount = code.Count / 4;

        // 类型表：都是"结果 id → 类型信息"，因为 SPIR-V 里用 id 互相引用。
        Dictionary<uint, uint> pointerPointeeTypes = [];        // OpTypePointer: resultId → 指向的类型 id
        Dictionary<uint, ScalarInfo> scalarTypes = [];          // OpTypeFloat / OpTypeInt
        Dictionary<uint, VectorInfo> vectorTypes = [];          // OpTypeVector
        Dictionary<uint, uint> locations = [];                  // 带 Location 装饰的 id → location
        HashSet<uint> builtIns = [];                            // 带 BuiltIn 装饰的 id（gl_VertexIndex 之类）
        Dictionary<uint, HashSet<uint>> entryPointInterfaces = [];  // 入口点 id → 它声明的接口变量
        List<uint> vertexEntryPoints = [];                      // ExecutionModel = Vertex 的入口点
        List<(uint ResultId, uint ResultType)> inputVariables = [];  // StorageClass = Input 的变量

        for (int i = HeaderWordCount; i < wordCount;)
        {
            uint instruction = ReadWord(code, i, byteSwapped);
            int instructionWordCount = (int)(instruction >> 16);
            ushort opCode = (ushort)(instruction & 0xFFFF);

            // 字数含指令自己；为 0 或越过末尾说明模块被截断了，读到哪算哪。
            if (instructionWordCount == 0 || i + instructionWordCount > wordCount)
            {
                break;
            }

            switch (opCode)
            {
                case OpTypeFloat:
                    scalarTypes[ReadWord(code, i + 1, byteSwapped)] =
                        new ScalarInfo(ReadWord(code, i + 2, byteSwapped), true, false);
                    break;

                case OpTypeInt:
                    scalarTypes[ReadWord(code, i + 1, byteSwapped)] =
                        new ScalarInfo(ReadWord(code, i + 2, byteSwapped), false, ReadWord(code, i + 3, byteSwapped) != 0);
                    break;

                case OpTypeVector:
                    vectorTypes[ReadWord(code, i + 1, byteSwapped)] =
                        new VectorInfo(ReadWord(code, i + 2, byteSwapped), ReadWord(code, i + 3, byteSwapped));
                    break;

                case OpTypePointer:
                    pointerPointeeTypes[ReadWord(code, i + 1, byteSwapped)] = ReadWord(code, i + 3, byteSwapped);
                    break;

                case OpDecorate:
                    if (instructionWordCount >= 4)
                    {
                        uint target = ReadWord(code, i + 1, byteSwapped);
                        uint decoration = ReadWord(code, i + 2, byteSwapped);
                        if (decoration == DecorationLocation)
                        {
                            locations[target] = ReadWord(code, i + 3, byteSwapped);
                        }
                        else if (decoration == DecorationBuiltIn)
                        {
                            builtIns.Add(target);
                        }
                    }
                    break;

                case OpEntryPoint:
                    {
                        uint executionModel = ReadWord(code, i + 1, byteSwapped);
                        uint entryPoint = ReadWord(code, i + 2, byteSwapped);
                        int end = i + instructionWordCount;

                        // 入口点名字是字面量字符串：按字打包、补 0 到 4 字节，所以要先跳过它，
                        // 后面剩下的字才是接口变量 id（顶点输入就在这里面）。
                        int j = i + 3;
                        while (j < end)
                        {
                            uint nameWord = ReadWord(code, j, byteSwapped);
                            j++;
                            if ((nameWord & 0x000000FF) == 0 || (nameWord & 0x0000FF00) == 0 ||
                                (nameWord & 0x00FF0000) == 0 || (nameWord & 0xFF000000) == 0)
                            {
                                break;
                            }
                        }

                        HashSet<uint> interfaceVariables =
                            entryPointInterfaces.TryGetValue(entryPoint, out HashSet<uint>? existing) ? existing : [];
                        for (; j < end; j++)
                        {
                            interfaceVariables.Add(ReadWord(code, j, byteSwapped));
                        }
                        entryPointInterfaces[entryPoint] = interfaceVariables;

                        if (executionModel == ExecutionModelVertex)
                        {
                            vertexEntryPoints.Add(entryPoint);
                        }
                    }
                    break;

                case OpVariable:
                    // OpVariable: resultType, resultId, storageClass, [initializer]
                    if (instructionWordCount >= 4 && ReadWord(code, i + 3, byteSwapped) == StorageClassInput)
                    {
                        inputVariables.Add((ReadWord(code, i + 2, byteSwapped), ReadWord(code, i + 1, byteSwapped)));
                    }
                    break;
            }

            i += instructionWordCount;
        }

        if (vertexEntryPoints.Count == 0)
        {
            throw new InvalidOperationException(
                "The shader module has no vertex entry point, so it declares no vertex input attributes.");
        }

        // 只认"顶点入口点"声明的那批接口变量 —— 这样同一个模块里别的入口点（比如片元着色器的 in 变量）
        // 就不会被误当成顶点属性。
        HashSet<uint> vertexInputs = entryPointInterfaces[vertexEntryPoints[0]];

        List<VertexInputAttribute> attributes = [];
        foreach ((uint resultId, uint resultType) in inputVariables)
        {
            if (!vertexInputs.Contains(resultId) || builtIns.Contains(resultId) || !locations.TryGetValue(resultId, out uint location))
            {
                continue;
            }

            if (!pointerPointeeTypes.TryGetValue(resultType, out uint pointeeType))
            {
                throw new NotSupportedException(
                    "The vertex input variable does not point to a type this reader understands.");
            }

            // 变量本身是指针；它指向的要么是标量（如 float），要么是向量（如 vec2）。
            uint componentType = pointeeType;
            uint componentCount = 1;
            if (vectorTypes.TryGetValue(pointeeType, out VectorInfo vector))
            {
                componentType = vector.ComponentType;
                componentCount = vector.ComponentCount;
            }

            if (!scalarTypes.TryGetValue(componentType, out ScalarInfo scalar))
            {
                throw new NotSupportedException(
                    "Only scalar and vector vertex inputs are supported (matrices, arrays and structs are not).");
            }

            attributes.Add(new VertexInputAttribute(location, scalar, componentCount));
        }

        attributes.Sort(static (left, right) => left.Location.CompareTo(right.Location));

        // offset 按"紧凑排布"累加：第 1 个属性的 offset 是 0，后面每一个都接在前一个后面 ——
        // 这正是教程那条"stride = 各属性大小之和、offset 依次相加"的顶点布局。
        // （顶点之间有对齐要求时，调用方拿到数组后自己改 offset / 对应的 stride 即可。）
        VertexInputAttributeDescription[] result = new VertexInputAttributeDescription[attributes.Count];
        uint offset = 0;
        for (int i = 0; i < attributes.Count; i++)
        {
            VertexInputAttribute attribute = attributes[i];
            result[i] = new VertexInputAttributeDescription(
                location: attribute.Location,
                binding: binding,
                format: ToFormat(attribute.Scalar, attribute.ComponentCount),
                offset: offset);
            offset += attribute.ComponentCount * (attribute.Scalar.Width / 8);
        }

        return result;
    }

    /// <summary>反射过程中先攒下来的中间结果（还没换算成 VkFormat）。</summary>
    private readonly record struct VertexInputAttribute(uint Location, ScalarInfo Scalar, uint ComponentCount);

    /// <summary>
    /// 按 SPIR-V 里的类型换算成 VkFormat：32 位浮点 / 有符号 / 无符号的标量与 2/3/4 维向量。
    /// 其它宽度（8 / 16 / 64 位）目前直接抛 —— 需要时再补。
    /// </summary>
    private static Format ToFormat(in ScalarInfo scalar, uint componentCount)
    {
        if (scalar.Width != 32)
        {
            throw new NotSupportedException(
                $"Only 32-bit vertex input components are supported, but the shader declares a {scalar.Width}-bit one.");
        }

        return (scalar.IsFloat, scalar.IsSigned, componentCount) switch
        {
            (true, _, 1) => Format.R32Sfloat,
            (true, _, 2) => Format.R32G32Sfloat,
            (true, _, 3) => Format.R32G32B32Sfloat,
            (true, _, 4) => Format.R32G32B32A32Sfloat,
            (false, true, 1) => Format.R32Sint,
            (false, true, 2) => Format.R32G32Sint,
            (false, true, 3) => Format.R32G32B32Sint,
            (false, true, 4) => Format.R32G32B32A32Sint,
            (false, false, 1) => Format.R32Uint,
            (false, false, 2) => Format.R32G32Uint,
            (false, false, 3) => Format.R32G32B32Uint,
            (false, false, 4) => Format.R32G32B32A32Uint,
            _ => throw new NotSupportedException(
                $"Unsupported vertex input component count: {componentCount}.")
        };
    }

    /// <summary>
    /// 读第 index 个 32 位字（按小端拼字节）。byteSwapped = true 时再翻一次字节
    ///（对付字节序相反的模块；正常模块用不到）。
    /// </summary>
    private static uint ReadWord(IReadOnlyList<byte> code, int index, bool byteSwapped)
    {
        int byteOffset = index * 4;
        uint word = (uint)(code[byteOffset]
            | (code[byteOffset + 1] << 8)
            | (code[byteOffset + 2] << 16)
            | (code[byteOffset + 3] << 24));

        return byteSwapped ? System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(word) : word;
    }
}
