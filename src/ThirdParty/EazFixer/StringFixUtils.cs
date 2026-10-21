using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace EazFixer
{
    public static class StringFixUtils
    {
        public static MethodDef FindStringDecryptMethod(ModuleDef module)
        {
            var candidates = FindStringDecryptMethods(module).ToArray();
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

        /// <summary>
        /// 找出所有结构上匹配的 Eaz 字符串入口（含嵌套与非 internal 变体）。
        /// 优先使用上游的精确规则（调用同类型的 string(int,bool) 解析重载）；
        /// 当该重载被内联掉时（2026.2 的构建）退回宽松规则，但仍排除百分比/定点数格式化器。
        /// </summary>
        public static IEnumerable<MethodDef> FindStringDecryptMethods(ModuleDef module)
        {
            var candidates = Utils.GetMethodsRecursive(module).Where(HasDecryptorSignature).ToArray();
            var structural = candidates.Where(CallsResolveOverload).ToArray();
            return structural.Length > 0 ? structural : candidates.Where(m => !IsNumericFormatter(m)).ToArray();
        }

        // static string(int): the entry-point signature shared by the real decryptor
        // and by unrelated formatters, so it is only the first filter.
        private static bool HasDecryptorSignature(MethodDef method) =>
            method.IsStatic
            && method.MethodSig != null && method.MethodSig.Params.Count == 1
            && method.MethodSig.RetType.ElementType == ElementType.String
            && method.MethodSig.Params[0].ElementType == ElementType.I4
            && method.HasBody && method.Body.HasInstructions;

        /// <summary>2021–2025 的解密器会在方法体里调用同类型的 string(int,bool) 解析重载。</summary>
        private static bool CallsResolveOverload(MethodDef method) =>
            method.Body.Instructions.Any(a => a.OpCode.Code == Code.Call && a.Operand is IMethod m
                && m.DeclaringType.FullName == method.DeclaringType.FullName
                && m.MethodSig != null && !m.MethodSig.HasThis && m.MethodSig.Params.Count == 2
                && m.MethodSig.RetType.ElementType == ElementType.String
                && m.MethodSig.Params[0].ElementType == ElementType.I4
                && m.MethodSig.Params[1].ElementType == ElementType.Boolean
                && !IsNumericFormatter(m.ResolveMethodDef()));

        /// <summary>2021–2025 的解密器会在方法体里调用同模块的 string(int,bool) 重载（用于候选评分）。</summary>
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

        // Percent and fixed-point unit formatters share the entry-point signature.
        // A state-free floating-point formatter is not an encrypted-string cache.
        private static bool IsNumericFormatter(MethodDef method)
        {
            if (method == null || !method.HasBody) return false;
            var il = method.Body.Instructions;
            if (il.Any(i => i.OpCode.Code == Code.Ldfld || i.OpCode.Code == Code.Ldsfld ||
                i.OpCode.Code == Code.Ldflda || i.OpCode.Code == Code.Ldsflda ||
                i.OpCode.Code == Code.Stfld || i.OpCode.Code == Code.Stsfld || i.OpCode.Code == Code.Newarr ||
                i.OpCode.Code == Code.Newobj || i.OpCode.Code == Code.Xor ||
                i.OpCode.Code == Code.Shl || i.OpCode.Code == Code.Shr || i.OpCode.Code == Code.Shr_Un)) return false;
            bool floating = il.Any(i => i.OpCode.Code == Code.Ldc_R8 || i.OpCode.Code == Code.Conv_R8);
            bool formatting = il.Any(i => i.Operand is IMethod m &&
                ((m.DeclaringType.FullName == "System.String" && m.Name == "Format") ||
                 (m.DeclaringType.FullName == "System.Int32" && m.Name == "ToString")));
            return floating && formatting;
        }
    }
}
