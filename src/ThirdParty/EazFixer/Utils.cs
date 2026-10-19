using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;

namespace EazFixer
{
    public static class Utils
    {
        public static IEnumerable<MethodDef> GetMethodsRecursive(ModuleDef t) => t.Types.SelectMany(GetMethodsRecursive);
        public static IEnumerable<MethodDef> GetMethodsRecursive(TypeDef type)
        {
            //return all methods in this type
            foreach (MethodDef m in type.Methods)
                yield return m;

            //go through nested types
            foreach (TypeDef t in type.NestedTypes)
            foreach (MethodDef m in GetMethodsRecursive(t))
                yield return m;
        }

        public static MethodInfo FindMethod(Assembly ass, MethodDef meth, Type[] args)
        {
            var flags = BindingFlags.Default;
            flags |= meth.IsPublic ? BindingFlags.Public : BindingFlags.NonPublic;
            flags |= meth.IsStatic ? BindingFlags.Static : BindingFlags.Instance;

            Type type = ass.GetType(meth.DeclaringType.ReflectionFullName);
            var method = type?.GetMethod(meth.Name, flags, null, args, null);
            if (method != null)
                return method;

            // 嵌套/泛型类型的 ReflectionFullName 可能和运行时名字对不上，或者
            // 依赖没加载导致 GetType 返回 null。退化为在全部类型里按名字+参数签名查找。
            try
            {
                foreach (var candidate in ass.GetTypes())
                {
                    method = Match(candidate);
                    if (method != null)
                        return method;
                }
            }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (var candidate in ex.Types)
                {
                    method = Match(candidate);
                    if (method != null)
                        return method;
                }
            }
            return null;

            MethodInfo Match(Type candidate)
            {
                if (candidate == null)
                    return null;
                if (candidate.Name != meth.DeclaringType.Name.String &&
                    candidate.FullName != meth.DeclaringType.ReflectionFullName)
                    return null;
                return candidate.GetMethod(meth.Name, flags, null, args, null);
            }
        }

        public static bool LookForReferences(ModuleDef mod, MethodDef meth) //methoddef can be generalized
        {
            //Why LINQ you may ask? Because I can :)
            return GetMethodsRecursive(mod)
                .Where(m => m.HasBody && m.Body.HasInstructions)
                .SelectMany(m => m.Body.Instructions)
                .Any(i => i.Operand != null && i.Operand == meth);
        }
    }
}
