using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Unpacker;

internal static class NamingTests
{
    internal static void Run(string directory)
    {
        string input=Path.Combine(directory,"naming-input.dll"),output=Path.Combine(directory,"naming-output.dll");
        using(var module=new ModuleDefUser("Naming.dll"))
        {
            new AssemblyDefUser("Naming",new Version(1,0)).Modules.Add(module);
            var forms=new AssemblyRefUser("System.Windows.Forms",new Version(4,0,0,0));
            var form=new TypeRefUser(module,"System.Windows.Forms","Form",forms);
            var control=new TypeRefUser(module,"System.Windows.Forms","Control",forms);
            var textBox=new TypeRefUser(module,"System.Windows.Forms","TextBox",forms);
            var type=new TypeDefUser("Fixture","#=form",form);module.Types.Add(type);
            var field=new FieldDefUser("#=field",new FieldSig(textBox.ToTypeSig()),FieldAttributes.Private);type.Fields.Add(field);
            var handler=new MethodDefUser("#=handler",MethodSig.CreateInstance(module.CorLibTypes.Void,module.CorLibTypes.Object,
                module.CorLibTypes.GetTypeRef("System","EventArgs").ToTypeSig()),MethodImplAttributes.IL,MethodAttributes.Private);
            handler.Body=new CilBody();handler.Body.Variables.Add(new Local(new SZArraySig(module.CorLibTypes.Int32)));
            handler.Body.Instructions.Add(OpCodes.Ret.ToInstruction());type.Methods.Add(handler);
            var init=new MethodDefUser("#=init",MethodSig.CreateInstance(module.CorLibTypes.Void),MethodImplAttributes.IL,MethodAttributes.Private);
            init.Body=new CilBody();type.Methods.Add(init);
            var setName=new MemberRefUser(module,"set_Name",MethodSig.CreateInstance(module.CorLibTypes.Void,module.CorLibTypes.String),control);
            var eventHandler=module.CorLibTypes.GetTypeRef("System","EventHandler");
            var instructions=new[]{OpCodes.Ldarg_0.ToInstruction(),OpCodes.Newobj.ToInstruction(new MemberRefUser(module,".ctor",MethodSig.CreateInstance(module.CorLibTypes.Void),textBox)),
                OpCodes.Stfld.ToInstruction(field),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldfld.ToInstruction(field),OpCodes.Ldstr.ToInstruction("inputTextBox"),OpCodes.Callvirt.ToInstruction(setName),
                OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldfld.ToInstruction(field),OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldftn.ToInstruction(handler),
                OpCodes.Newobj.ToInstruction(new MemberRefUser(module,".ctor",MethodSig.CreateInstance(module.CorLibTypes.Void,module.CorLibTypes.Object,module.CorLibTypes.IntPtr),eventHandler)),
                OpCodes.Callvirt.ToInstruction(new MemberRefUser(module,"add_TextChanged",MethodSig.CreateInstance(module.CorLibTypes.Void,eventHandler.ToTypeSig()),control)),
                OpCodes.Ldarg_0.ToInstruction(),OpCodes.Ldstr.ToInstruction("SampleForm"),OpCodes.Callvirt.ToInstruction(setName),
                OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(new MemberRefUser(module,"SuspendLayout",MethodSig.CreateInstance(module.CorLibTypes.Void),control)),
                OpCodes.Ldarg_0.ToInstruction(),OpCodes.Call.ToInstruction(new MemberRefUser(module,"ResumeLayout",MethodSig.CreateInstance(module.CorLibTypes.Void),control)),OpCodes.Ret.ToInstruction()};
            foreach(var instruction in instructions)init.Body.Instructions.Add(instruction);
            module.Write(input);
        }
        string map=Path.Combine(directory,"naming-map.json");
        NameRecoveryPass.Process(input,output,map);
        using(var module=ModuleDefMD.Load(output))
        {
            var type=module.Types.Single(t=>!t.IsGlobalModuleType);
            var handler=type.Methods.Single(m=>m.Name=="inputTextBox_TextChanged");
            if(type.Name!="SampleForm" || type.Fields.Single().Name!="inputTextBox" || !type.Methods.Any(m=>m.Name=="InitializeComponent"))
                throw new Exception("Evidence-based role names were not recovered.");
            if(handler.Parameters.Where(p=>!p.IsHiddenThisParameter).Select(p=>p.Name).SequenceEqual(new[]{"sender","e"})!=true
                || handler.Body.Variables.Single().Type.FullName!="System.Int32[]" || type.Fields.Single().FieldType.FullName!="System.Windows.Forms.TextBox")
                throw new Exception("Callback role names or actual type shapes changed.");
            if(!File.ReadAllText(map).Contains("control.Name literal"))throw new Exception("Naming evidence was not recorded.");
        }

        // Eazfuscator 2021+ 的伪 Unicode 转义名（全是 ASCII）必须被改名，
        // 否则会原样留在反编译结果里。
        string lookalikeInput=Path.Combine(directory,"lookalike-input.dll"),lookalikeOutput=Path.Combine(directory,"lookalike-output.dll");
        using(var module=new ModuleDefUser("Lookalike.dll"))
        {
            new AssemblyDefUser("Lookalike",new Version(1,0)).Modules.Add(module);
            var type=new TypeDefUser("u0006_u2004_u200B",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(type);
            var method=new MethodDefUser("u0001_u0002",MethodSig.CreateStatic(module.CorLibTypes.Void),
                MethodImplAttributes.IL,MethodAttributes.Public|MethodAttributes.Static);
            method.Body=new CilBody();method.Body.Instructions.Add(OpCodes.Ret.ToInstruction());type.Methods.Add(method);
            module.Write(lookalikeInput);
        }
        NameRecoveryPass.Process(lookalikeInput,lookalikeOutput,Path.Combine(directory,"lookalike-map.json"));
        using(var module=ModuleDefMD.Load(lookalikeOutput))
        {
            var type=module.Types.Single(t=>!t.IsGlobalModuleType);
            if(type.Name=="u0006_u2004_u200B")throw new Exception("Eazfuscator lookalike type name was not renamed.");
            if(type.Methods.Single().Name=="u0001_u0002")throw new Exception("Eazfuscator lookalike method name was not renamed.");
        }
        Console.WriteLine("PASS: control/type/event/initializer names carry evidence; parameter and array type shapes are preserved.");
    }
}
