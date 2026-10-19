using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace EazFixer
{
    public static class StringFixUtils
    {
        public static MethodDef FindStringDecryptMethod(ModuleDef module)
        {
            var candidates = Utils.GetMethodsRecursive(module).Where(CanBeStringMethod).ToArray();
            if (candidates.Length == 0)
                return null;
            if (candidates.Length == 1)
                return candidates[0];

            // 2021–2025 的典型形状：解密器方法体里调用 string(int,bool) 重载。
            var strict = candidates.Where(CallsIntBoolOverload).ToArray();
            if (strict.Length == 1)
                return strict[0];

            // 2026.2 起有些构建把该重载内联/改写掉，只剩 string(int)。
            // 这时按"调用点数量 + 是否调用重载"挑最像解密器的那个，
            // 避免 SingleOrDefault 在多个候选上直接抛异常。
            return candidates
                .Select(m => (Method: m, Score: CallSiteCount(module, m) + (CallsIntBoolOverload(m) ? 1000 : 0)))
                .OrderByDescending(x => x.Score)
                .Select(x => x.Method)
                .First();
        }

        private static bool CanBeStringMethod(MethodDef method)
        {
            //internal and static
            if (!method.IsStatic || !method.IsAssembly)
                return false;

            //takes int, returns string
            if (method.MethodSig.ToString() != "System.String (System.Int32)")
                return false;

            //actually a proper method, not abstract or from an interface
            if (!method.HasBody || !method.Body.HasInstructions)
                return false;

            //is not private or public
            if (method.IsPrivate || method.IsPublic)
                return false;

            return true;
        }

        /// <summary>2021–2025 的解密器会在方法体里调用同模块的 string(int,bool) 重载。</summary>
        private static bool CallsIntBoolOverload(MethodDef method) =>
            method.HasBody && method.Body.HasInstructions && method.Body.Instructions.Any(a =>
                a.OpCode.Code == Code.Call && a.Operand is MethodDef m &&
                m.MethodSig.ToString() == "System.String (System.Int32,System.Boolean)");

        /// <summary>统计 ldc.i4 &lt;id&gt; 后跟 call 解密器 的调用点数量，用于在多个候选间排序。</summary>
        private static int CallSiteCount(ModuleDef module, MethodDef decrypter)
        {
            int count = 0;
            foreach (var method in Utils.GetMethodsRecursive(module))
            {
                if (!method.HasBody || !method.Body.HasInstructions)
                    continue;
                var instructions = method.Body.Instructions;
                for (int i = 0; i < instructions.Count; i++)
                {
                    if (instructions[i].OpCode.Code != Code.Call && instructions[i].OpCode.Code != Code.Callvirt)
                        continue;
                    if (!(instructions[i].Operand is IMethod called) || called.FullName != decrypter.FullName)
                        continue;
                    if (i > 0 && instructions[i - 1].IsLdcI4())
                        count++;
                }
            }
            return count;
        }
    }
}
