using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace EazFixer.Processors
{
    internal class StringFixer : ProcessorBase
    {
        private MethodDef _decrypterMethod;

        protected override void InitializeInternal()
        {
            //find method
            if (!Flags.StrDecTok.IsNull)
                _decrypterMethod = Ctx.Module.ResolveToken(Flags.StrDecTok) as MethodDef
                                    ?? throw new Exception("StringDecrypter token set but method not found");
            else
                _decrypterMethod = StringFixUtils.FindStringDecryptMethod(Ctx.Module) ??
                               throw new Exception("Could not find decrypter method");
        }

        protected override void ProcessInternal()
        {
            //a dictionary to cache all strings
            var dictionary = new Dictionary<int, string>();

            //get the decrypter method in a way in which we can invoke it
            var decrypter = Utils.FindMethod(Ctx.Assembly, _decrypterMethod, new[] { typeof(int) }) ?? throw new Exception("Couldn't find decrypter method through reflection");

            //store it so we can use it in the stacktrace patch
            StacktracePatcher.PatchStackTraceGetMethod.MethodToReplace = decrypter;

            //for every method with a body...
            foreach (MethodDef meth in Utils.GetMethodsRecursive(Ctx.Module).Where(a => a.HasBody && a.Body.HasInstructions))
            {
                var instructions = meth.Body.Instructions;

                //.. and every call to the decrypter ...
                for (int i = 0; i < instructions.Count; i++)
                {
                    var curr = instructions[i];
                    if (curr.OpCode.Code != Code.Call && curr.OpCode.Code != Code.Callvirt)
                        continue;

                    //if it invokes the string decrypter method with an int parameter
                    if (!(curr.Operand is IMethod md) || md.FullName != _decrypterMethod.FullName ||
                        md.DeclaringType.DefinitionAssembly?.FullName != Ctx.Module.Assembly.FullName)
                        continue;

                    // 标准形状是 ldc.i4 紧邻 call；2026.2 有些构建会插入 nop/conv.i4，
                    // 所以向前看最多 4 条，找到真正压栈的那条 ldc.i4。
                    int idIndex;
                    int val;
                    if (!TryGetPushedInt32(instructions, i, out val, out idIndex))
                        continue;

                    //get the resulting string from either cache or invoking the decrypter method
                    if (!dictionary.ContainsKey(val))
                    {
                        try
                        {
                            dictionary[val] = (string)decrypter.Invoke(null, new object[] { val });
                        }
                        catch (Exception)
                        {
                            // 单条解密失败（例如反调试对某个 id 抛异常）不影响其它调用点。
                            continue;
                        }
                    }

                    // check if str == .ctor due to eaz using string decryptor to call constructors
                    if (dictionary[val] == ".ctor" && Flags.VirtFix) continue;

                    //replace the instructions with the string
                    instructions[idIndex].OpCode = OpCodes.Nop;
                    instructions[idIndex].Operand = null;
                    curr.OpCode = OpCodes.Ldstr;
                    curr.Operand = dictionary[val];
                }
            }
        }

        /// <summary>
        /// 找 call 之前压栈的 ldc.i4。标准形状是紧邻，但有些版本会插入 nop/conv.i4，
        /// 所以向前看最多 4 条，跳过不影响整数值的指令。
        /// </summary>
        private static bool TryGetPushedInt32(IList<Instruction> instructions, int callIndex, out int value, out int index)
        {
            value = 0;
            index = -1;
            for (int i = callIndex - 1, steps = 0; i >= 0 && steps < 4; i--, steps++)
            {
                var instruction = instructions[i];
                if (instruction.IsLdcI4())
                {
                    value = instruction.GetLdcI4Value();
                    index = i;
                    return true;
                }
                switch (instruction.OpCode.Code)
                {
                    case Code.Nop:
                    case Code.Conv_I4:
                    case Code.Conv_U4:
                    case Code.Conv_I8:
                        continue;
                    default:
                        return false;
                }
            }
            return false;
        }

        protected override void CleanupInternal()
        {
            //check if virtfix is active so ignore cleaning
            if (Flags.VirtFix)
                throw new Exception("VirtFix enabled, Cannot remove method");

            //ensure that the string decryptor isn't called anywhere
            if (Utils.LookForReferences(Ctx.Module, _decrypterMethod))
                throw new Exception("String decrypter is still being called");

            //remove the string decryptor class
            var stringType = _decrypterMethod.DeclaringType;
            Ctx.Module.Types.Remove(stringType);
        }
    }
}
