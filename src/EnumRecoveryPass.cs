using System.Globalization;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Unpacker;

// Eazfuscator 的枚举保护会把枚举的具名成员整体删掉，只留下 value__，于是代码里
// 全部退化成 (EnumX)1、(EnumX)2 这样的裸强转。成员名本身已经不在元数据里，
// 但"哪些常量被当成该枚举使用"是可以从 IL 里精确读出来的：这里用一个很小的
// 栈求值器跟踪常量与枚举类型，把它们对上，再把字面量成员补回枚举类型。
// 补回之后 dnSpy 会直接显示 EnumX.Value1，ILSpy 也会把强转渲染成成员引用，
// 因此 DLL 与导出的 C# 工程同时受益，不需要再对文本做二次改写。
internal static class EnumRecoveryPass
{
    internal static int Process(ModuleDef module)
    {
        var candidates = module.GetTypes().Where(t => t.IsEnum && !t.Fields.Any(f => f.IsLiteral)).ToArray();
        if (candidates.Length == 0) return 0;
        var values = Collect(module);
        int members = 0;
        foreach (var pair in values)
        {
            var type = pair.Key;
            var underlying = type.GetEnumUnderlyingType()?.ElementType ?? ElementType.I4;
            var taken = new HashSet<string>(type.Fields.Select(f => f.Name.String), StringComparer.Ordinal);
            foreach (long value in pair.Value)
            {
                string name = value < 0
                    ? "ValueNeg" + (-value).ToString(CultureInfo.InvariantCulture)
                    : "Value" + value.ToString(CultureInfo.InvariantCulture);
                if (!taken.Add(name)) continue;
                var field = new FieldDefUser(name, new FieldSig(type.ToTypeSig()),
                    FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault);
                field.Constant = Constant(underlying, value);
                type.Fields.Add(field);
                members++;
            }
        }
        return members;
    }

    // 必须在 VM/CLR 校验之后单独重写：新增字段会改变 Field 表的 RID 顺序，
    // 而管线前面几个阶段用 MetadataFlags.PreserveAll 保 token，两者不能混用
    // （PreserveAll 下追加字段会写出损坏的表，反编译端直接报 Unknown tables）。
    internal static int Rewrite(string input, string output)
    {
        using var module = ModuleDefMD.Load(input);
        int members = Process(module);
        if (members == 0) return 0;
        // 只保留方法体原样（IL 字节与 max stack），其余表按正常顺序重建：
        // 这样新增的字段行不会和 PreserveRids 冲突。
        module.Write(output, new dnlib.DotNet.Writer.ModuleWriterOptions(module)
        {
            MetadataOptions = { Flags = dnlib.DotNet.Writer.MetadataFlags.KeepOldMaxStack }
        });
        return members;
    }

    internal static Dictionary<TypeDef, SortedSet<long>> Collect(ModuleDef module)
    {
        var values = new Dictionary<TypeDef, SortedSet<long>>();
        foreach (var type in module.GetTypes())
        foreach (var method in type.Methods)
        {
            if (!method.HasBody || !method.Body.HasInstructions) continue;
            // 单个方法的异常不应让整次恢复失败：只影响该方法贡献的取值。
            try { Scan(method, values); }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is NullReferenceException) { }
        }
        return values;
    }

    private static Constant Constant(ElementType elementType, long value) => elementType switch
    {
        ElementType.I1 => new ConstantUser((sbyte)value),
        ElementType.U1 => new ConstantUser((byte)value),
        ElementType.I2 => new ConstantUser((short)value),
        ElementType.U2 => new ConstantUser((ushort)value),
        ElementType.U4 => new ConstantUser(unchecked((uint)value)),
        ElementType.I8 => new ConstantUser(value),
        ElementType.U8 => new ConstantUser(unchecked((ulong)value)),
        _ => new ConstantUser(unchecked((int)value))
    };

    private static void Scan(MethodDef method, Dictionary<TypeDef, SortedSet<long>> values)
    {
        var body = method.Body;
        var instructions = body.Instructions;
        var targets = new HashSet<Instruction>();
        foreach (var instruction in instructions)
        {
            if (instruction.Operand is Instruction target) targets.Add(target);
            else if (instruction.Operand is IList<Instruction> list) foreach (var item in list) targets.Add(item);
        }
        var stack = new List<object?>();
        foreach (var instruction in instructions)
        {
            // 分支汇合点的栈状态无法线性推断，直接清空，宁可少记录也不误判。
            if (targets.Contains(instruction)) stack.Clear();
            var code = instruction.OpCode.Code;
            if (TryLiteral(instruction, out long literal)) { stack.Add(literal); continue; }
            switch (code)
            {
                case Code.Nop: case Code.Break: case Code.Constrained: case Code.Readonly:
                case Code.Tailcall: case Code.Volatile: case Code.Unaligned:
                    continue;
                case Code.Dup: stack.Add(Peek(stack)); continue;
                case Code.Pop: Pop(stack); continue;
                case Code.Ldarg_0: case Code.Ldarg_1: case Code.Ldarg_2: case Code.Ldarg_3:
                case Code.Ldarg: case Code.Ldarg_S:
                    stack.Add(EnumType(ArgumentType(method, instruction, code))); continue;
                case Code.Ldloc_0: case Code.Ldloc_1: case Code.Ldloc_2: case Code.Ldloc_3:
                case Code.Ldloc: case Code.Ldloc_S:
                    stack.Add(EnumType(LocalType(body, instruction, code))); continue;
                case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
                case Code.Stloc: case Code.Stloc_S:
                    Pop(stack); continue;
                case Code.Ldfld: case Code.Ldsfld:
                    Pop(stack);
                    stack.Add(EnumType(FieldType(instruction)));
                    continue;
                case Code.Ldflda: case Code.Ldsflda:
                    Pop(stack); stack.Add(null); continue;
                case Code.Stfld:
                    Record(EnumType(FieldType(instruction)), Pop(stack), values);
                    Pop(stack);
                    continue;
                case Code.Stsfld:
                    Record(EnumType(FieldType(instruction)), Pop(stack), values);
                    continue;
                case Code.Starg:
                    Record(EnumType((instruction.Operand as Parameter)?.Type), Pop(stack), values);
                    continue;
                case Code.Ret:
                    Record(EnumType(method.MethodSig?.RetType), Pop(stack), values);
                    continue;
                case Code.Call: case Code.Callvirt: case Code.Newobj: case Code.Calli:
                {
                    var callee = instruction.Operand as IMethod;
                    var signature = callee?.MethodSig;
                    if (signature == null) { stack.Clear(); continue; }
                    int count = signature.Params.Count;
                    var arguments = new object?[count];
                    for (int index = count - 1; index >= 0; index--) arguments[index] = Pop(stack);
                    bool instance = code == Code.Newobj ? false
                        : callee is MethodDef definition ? !definition.IsStatic : signature.HasThis;
                    if (instance) Pop(stack);
                    for (int index = 0; index < count; index++) Record(EnumType(signature.Params[index]), arguments[index], values);
                    stack.Add(EnumType(signature.RetType));
                    continue;
                }
                case Code.Ceq: case Code.Cgt: case Code.Cgt_Un: case Code.Clt: case Code.Clt_Un:
                case Code.Beq: case Code.Bne_Un: case Code.Bgt: case Code.Blt: case Code.Bge: case Code.Ble:
                case Code.Bgt_Un: case Code.Blt_Un: case Code.Bge_Un: case Code.Ble_Un:
                {
                    var right = Pop(stack);
                    var left = Pop(stack);
                    Record(EnumTypeOf(left), right, values);
                    Record(EnumTypeOf(right), left, values);
                    if (code is Code.Ceq or Code.Cgt or Code.Cgt_Un or Code.Clt or Code.Clt_Un) stack.Add(null);
                    continue;
                }
                case Code.Box: case Code.Unbox_Any: case Code.Castclass: case Code.Isinst:
                {
                    var value = Pop(stack);
                    stack.Add(EnumType(instruction.Operand as ITypeDefOrRef) ?? value);
                    continue;
                }
                default:
                    ApplyGeneric(instruction, stack);
                    continue;
            }
        }
    }

    private static void ApplyGeneric(Instruction instruction, List<object?> stack)
    {
        int pops = instruction.OpCode.StackBehaviourPop switch
        {
            StackBehaviour.Pop0 => 0,
            StackBehaviour.Pop1 or StackBehaviour.Popi or StackBehaviour.Popref => 1,
            StackBehaviour.Pop1_pop1 or StackBehaviour.Popi_pop1 or StackBehaviour.Popi_popi
                or StackBehaviour.Popi_popi8 or StackBehaviour.Popi_popr4 or StackBehaviour.Popi_popr8
                or StackBehaviour.Popref_pop1 or StackBehaviour.Popref_popi => 2,
            StackBehaviour.Popi_popi_popi or StackBehaviour.Popref_popi_popi or StackBehaviour.Popref_popi_popi8
                or StackBehaviour.Popref_popi_popr4 or StackBehaviour.Popref_popi_popr8
                or StackBehaviour.Popref_popi_popref => 3,
            _ => -1
        };
        if (pops < 0) { stack.Clear(); return; }
        for (int index = 0; index < pops; index++) Pop(stack);
        int pushes = instruction.OpCode.StackBehaviourPush switch
        {
            StackBehaviour.Push1 or StackBehaviour.Pushi or StackBehaviour.Pushi8 or StackBehaviour.Pushr4
                or StackBehaviour.Pushr8 or StackBehaviour.Pushref => 1,
            StackBehaviour.Push1_push1 => 2,
            _ => 0
        };
        for (int index = 0; index < pushes; index++) stack.Add(null);
    }

    private static void Record(TypeDef? type, object? value, Dictionary<TypeDef, SortedSet<long>> values)
    {
        if (type == null || value is not long literal) return;
        if (!values.TryGetValue(type, out var set)) values[type] = set = new SortedSet<long>();
        set.Add(literal);
    }

    private static object? Peek(List<object?> stack) => stack.Count == 0 ? null : stack[stack.Count - 1];

    private static object? Pop(List<object?> stack)
    {
        if (stack.Count == 0) return null;
        var value = stack[stack.Count - 1];
        stack.RemoveAt(stack.Count - 1);
        return value;
    }

    private static bool TryLiteral(Instruction instruction, out long value)
    {
        value = 0;
        switch (instruction.OpCode.Code)
        {
            case Code.Ldc_I4_M1: value = -1; return true;
            case Code.Ldc_I4_0: value = 0; return true;
            case Code.Ldc_I4_1: value = 1; return true;
            case Code.Ldc_I4_2: value = 2; return true;
            case Code.Ldc_I4_3: value = 3; return true;
            case Code.Ldc_I4_4: value = 4; return true;
            case Code.Ldc_I4_5: value = 5; return true;
            case Code.Ldc_I4_6: value = 6; return true;
            case Code.Ldc_I4_7: value = 7; return true;
            case Code.Ldc_I4_8: value = 8; return true;
            case Code.Ldc_I4_S: value = Convert.ToInt64(instruction.Operand, CultureInfo.InvariantCulture); return true;
            case Code.Ldc_I4: value = Convert.ToInt64(instruction.Operand, CultureInfo.InvariantCulture); return true;
            case Code.Ldc_I8: value = Convert.ToInt64(instruction.Operand, CultureInfo.InvariantCulture); return true;
            default: return false;
        }
    }

    private static TypeSig? FieldType(Instruction instruction) => (instruction.Operand as IField)?.FieldSig?.Type;

    private static TypeSig? ArgumentType(MethodDef method, Instruction instruction, Code code)
    {
        if (instruction.Operand is Parameter parameter) return parameter.Type;
        int index = code switch
        {
            Code.Ldarg_0 => 0, Code.Ldarg_1 => 1, Code.Ldarg_2 => 2, Code.Ldarg_3 => 3, _ => -1
        };
        if (index < 0) return null;
        if (!method.IsStatic) index--;
        return index >= 0 && index < method.Parameters.Count ? method.Parameters[index].Type : null;
    }

    private static TypeSig? LocalType(dnlib.DotNet.Emit.MethodBody body, Instruction instruction, Code code)
    {
        if (instruction.Operand is Local local) return local.Type;
        int index = code switch
        {
            Code.Ldloc_0 => 0, Code.Ldloc_1 => 1, Code.Ldloc_2 => 2, Code.Ldloc_3 => 3, _ => -1
        };
        var variables = (body as CilBody)?.Variables;
        return variables != null && index >= 0 && index < variables.Count ? variables[index].Type : null;
    }

    private static TypeDef? EnumType(TypeSig? signature) => EnumType(signature?.ToTypeDefOrRef());

    private static TypeDef? EnumTypeOf(object? value) => value switch
    {
        TypeDef type => type,
        TypeSig signature => EnumType(signature),
        ITypeDefOrRef reference => EnumType(reference),
        _ => null
    };

    private static TypeDef? EnumType(ITypeDefOrRef? reference)
    {
        var type = reference?.ResolveTypeDef();
        return type is { IsEnum: true } ? type : null;
    }
}
