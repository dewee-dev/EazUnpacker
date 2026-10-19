using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Unpacker;

internal static class EnumRecoveryTests
{
    internal static void Run()
    {
        using var module = new ModuleDefUser("EnumFixture.dll");
        var assembly = new AssemblyDefUser("EnumFixture", new Version(1, 0));
        assembly.Modules.Add(module);
        // 成员被删光的枚举：只有 value__
        var enumType = new TypeDefUser("Fixture", "E", module.CorLibTypes.GetTypeRef("System", "Enum"))
        {
            Attributes = TypeAttributes.Public | TypeAttributes.Sealed
        };
        module.Types.Add(enumType);
        enumType.Fields.Add(new FieldDefUser("value__", new FieldSig(module.CorLibTypes.Int32),
            FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName));

        var holder = new TypeDefUser("Fixture", "Holder", module.CorLibTypes.Object.TypeDefOrRef);
        module.Types.Add(holder);
        MethodDef Method(string name, MethodSig signature, params Instruction[] body)
        {
            var method = new MethodDefUser(name, signature, MethodImplAttributes.IL,
                MethodAttributes.Public | MethodAttributes.Static);
            method.Body = new CilBody();
            foreach (var instruction in body) method.Body.Instructions.Add(instruction);
            holder.Methods.Add(method);
            return method;
        }
        var enumSig = enumType.ToTypeSig();
        // 返回值、调用参数、比较三种常见形态
        Method("Return", MethodSig.CreateStatic(enumSig), OpCodes.Ldc_I4_3.ToInstruction(), OpCodes.Ret.ToInstruction());
        var take = Method("Take", MethodSig.CreateStatic(module.CorLibTypes.Void, enumSig), OpCodes.Ret.ToInstruction());
        Method("Call", MethodSig.CreateStatic(module.CorLibTypes.Void),
            OpCodes.Ldc_I4_5.ToInstruction(), OpCodes.Call.ToInstruction(take), OpCodes.Ret.ToInstruction());
        Method("Compare", MethodSig.CreateStatic(module.CorLibTypes.Boolean, enumSig),
            OpCodes.Ldarg_0.ToInstruction(), OpCodes.Ldc_I4_7.ToInstruction(), OpCodes.Ceq.ToInstruction(),
            OpCodes.Ret.ToInstruction());

        int members = EnumRecoveryPass.Process(module);
        var literals = enumType.Fields.Where(f => f.IsLiteral).OrderBy(f => Convert.ToInt64(f.Constant!.Value)).ToArray();
        if (members != 3 || literals.Length != 3)
            throw new Exception("Enum member recovery produced " + members + " members instead of 3.");
        var expected = new[] { 3L, 5L, 7L };
        for (int index = 0; index < expected.Length; index++)
        {
            if (Convert.ToInt64(literals[index].Constant!.Value) != expected[index]
                || literals[index].Name.String != "Value" + expected[index])
                throw new Exception("Recovered enum member does not match the observed constant: " + literals[index].Name);
            if (!literals[index].IsStatic || !literals[index].IsLiteral || literals[index].FieldSig.Type.RemovePinnedAndModifiers().FullName != "Fixture.E")
                throw new Exception("Recovered enum member lost its literal shape.");
        }
        if (EnumRecoveryPass.Process(module) != 0)
            throw new Exception("Enum member recovery is not idempotent.");

        // 已有成员的枚举不参与恢复，避免覆盖原作者命名
        var named = new TypeDefUser("Fixture", "Named", module.CorLibTypes.GetTypeRef("System", "Enum"))
        {
            Attributes = TypeAttributes.Public | TypeAttributes.Sealed
        };
        module.Types.Add(named);
        named.Fields.Add(new FieldDefUser("value__", new FieldSig(module.CorLibTypes.Int32),
            FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName));
        var kept = new FieldDefUser("Kept", new FieldSig(named.ToTypeSig()),
            FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault)
        { Constant = new ConstantUser(9) };
        named.Fields.Add(kept);
        Method("ReturnNamed", MethodSig.CreateStatic(named.ToTypeSig()),
            OpCodes.Ldc_I4.ToInstruction(9), OpCodes.Ret.ToInstruction());
        if (EnumRecoveryPass.Process(module) != 0 || named.Fields.Count(f => f.IsLiteral) != 1)
            throw new Exception("Enum recovery touched an enum that already carries member names.");

        Console.WriteLine("PASS: member-less enums recover literal members from returns, arguments and comparisons; named enums untouched.");
    }
}
