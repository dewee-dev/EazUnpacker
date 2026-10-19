using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace EazFixer
{
    public static class StacktracePatcher
    {
        private const string HarmonyId = "holly.eazfixer.stacktrace";
        private static Harmony _harmony;

        public static void Patch()
        {
            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());
        }

        public static void UnPatch()
        {
            _harmony?.UnpatchAll(_harmony.Id);
            _harmony = null;
        }

        [HarmonyPatch(typeof(StackFrame), "GetMethod")]
        public class PatchStackTraceGetMethod
        {
            public static MethodInfo MethodToReplace;

            public static void Postfix(ref MethodBase __result)
            {
                if (IsReflectionInvokeFrame(__result))
                {
                    //just replace it with a method
                    __result = MethodToReplace ?? MethodBase.GetCurrentMethod();
                    Debug.WriteLine("[D] Patched stacktrace entry");
                }
            }

            /// <summary>
            /// 反射调用时，解密器看到的调用者会落在反射的 invoke 辅助方法上：
            ///   .NET Framework 4.x: System.RuntimeMethodHandle
            ///   .NET 5+ / 8:       System.Reflection.MethodBaseInvoker（或 RuntimeMethodInfo）
            /// 只替换这些帧，普通调用栈不受影响。
            /// </summary>
            private static bool IsReflectionInvokeFrame(MethodBase method)
            {
                var name = method?.DeclaringType?.FullName;
                if (name == null)
                    return false;
                return name == "System.RuntimeMethodHandle"
                    || name == "System.Reflection.MethodBaseInvoker"
                    || name == "System.Reflection.RuntimeMethodInfo"
                    || name.Contains("MethodBaseInvoker")
                    || name.Contains("RuntimeMethodHandle");
            }
        }
    }
}
