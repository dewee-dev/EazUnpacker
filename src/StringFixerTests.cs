using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Unpacker;

/// <summary>
/// 2026.2 兼容性自测：解密器不再调用 string(int,bool) 时仍应被识别；
/// 同时不能破坏 2021–2025 的典型形状（应优先选调用重载的那个）。
/// </summary>
internal static class StringFixerTests
{
    internal static void Run()
    {
        // 2026.2：只有 internal static string(int)，方法体不调用 string(int,bool)。
        using (var module = new ModuleDefUser("Relaxed.dll"))
        {
            new AssemblyDefUser("Relaxed", new Version(1, 0)).Modules.Add(module);
            var type = new TypeDefUser("Decrypter", module.CorLibTypes.Object.TypeDefOrRef);
            module.Types.Add(type);

            var decrypter = new MethodDefUser("Get",
                MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.Int32),
                MethodImplAttributes.IL, MethodAttributes.Assembly | MethodAttributes.Static);
            decrypter.Body = new CilBody();
            decrypter.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("RELAXED"));
            decrypter.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            type.Methods.Add(decrypter);

            var caller = new MethodDefUser("Alpha",
                MethodSig.CreateStatic(module.CorLibTypes.String),
                MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static);
            caller.Body = new CilBody();
            caller.Body.Instructions.Add(OpCodes.Ldc_I4.ToInstruction(3001));
            caller.Body.Instructions.Add(OpCodes.Call.ToInstruction(decrypter));
            caller.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            type.Methods.Add(caller);

            var found = EazFixer.StringFixUtils.FindStringDecryptMethod(module);
            if (found == null || found.Name != "Get")
                throw new Exception("2026.2-style string(int) decrypter was not detected.");
        }

        // 2021–2025：典型形状（方法体调用 string(int,bool)）仍应优先命中。
        using (var module = new ModuleDefUser("Strict.dll"))
        {
            new AssemblyDefUser("Strict", new Version(1, 0)).Modules.Add(module);
            var type = new TypeDefUser("Decrypter", module.CorLibTypes.Object.TypeDefOrRef);
            module.Types.Add(type);

            var inner = new MethodDefUser("Resolve",
                MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.Int32, module.CorLibTypes.Boolean),
                MethodImplAttributes.IL, MethodAttributes.Assembly | MethodAttributes.Static);
            inner.Body = new CilBody();
            inner.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("x"));
            inner.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            type.Methods.Add(inner);

            var outer = new MethodDefUser("Get",
                MethodSig.CreateStatic(module.CorLibTypes.String, module.CorLibTypes.Int32),
                MethodImplAttributes.IL, MethodAttributes.Assembly | MethodAttributes.Static);
            outer.Body = new CilBody();
            outer.Body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
            outer.Body.Instructions.Add(OpCodes.Ldc_I4_0.ToInstruction());
            outer.Body.Instructions.Add(OpCodes.Call.ToInstruction(inner));
            outer.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            type.Methods.Add(outer);

            var found = EazFixer.StringFixUtils.FindStringDecryptMethod(module);
            if (found == null || found.Name != "Get")
                throw new Exception("Strict string(int) decrypter was not preferred.");
        }

        Console.WriteLine("PASS: 2026.2 plain string(int) decrypter detected; strict string(int,bool) shape still preferred.");
    }
}
