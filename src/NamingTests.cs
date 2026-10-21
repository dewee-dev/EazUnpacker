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
        // 接口成员与实现类成员必须同名。混淆器把所有成员名抹成同一个字符串，
        // 重命名后两侧可能各拿一个名字，甚至把 get/set 拆成两个 PropertyDef，
        // 反编译出来就报 CS0535/CS0737。顺带验证同类型里的重复签名会被拆开（CS0111）。
        string contractInput=Path.Combine(directory,"contract-input.dll"),contractOutput=Path.Combine(directory,"contract-output.dll");
        using(var module=new ModuleDefUser("Contract.dll"))
        {
            new AssemblyDefUser("Contract",new Version(1,0)).Modules.Add(module);
            var int32=module.CorLibTypes.Int32;
            var contract=new TypeDefUser("\u0001",module.CorLibTypes.Object.TypeDefOrRef)
                {Attributes=TypeAttributes.Interface|TypeAttributes.Abstract|TypeAttributes.Public};
            module.Types.Add(contract);
            MethodDefUser Abstract(string name,MethodSig signature)
            {
                var method=new MethodDefUser(name,signature,MethodImplAttributes.IL,
                    MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.Abstract|MethodAttributes.NewSlot|MethodAttributes.HideBySig);
                contract.Methods.Add(method);return method;
            }
            var contractGetter=Abstract("get_\u0002",MethodSig.CreateInstance(int32));
            var contractSetter=Abstract("set_\u0002",MethodSig.CreateInstance(module.CorLibTypes.Void,int32));
            Abstract("\u0003",MethodSig.CreateInstance(module.CorLibTypes.Void));
            var contractProperty=new PropertyDefUser("\u0002",new PropertySig(true,int32))
                {GetMethod=contractGetter,SetMethod=contractSetter};
            contract.Properties.Add(contractProperty);

            var implementation=new TypeDefUser("\u0004",module.CorLibTypes.Object.TypeDefOrRef);
            implementation.Interfaces.Add(new InterfaceImplUser(contract));module.Types.Add(implementation);
            MethodDefUser Concrete(string name,MethodSig signature,bool returnsValue)
            {
                var method=new MethodDefUser(name,signature,MethodImplAttributes.IL,
                    MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.Final|MethodAttributes.NewSlot|MethodAttributes.HideBySig);
                method.Body=new CilBody();
                if(returnsValue)method.Body.Instructions.Add(OpCodes.Ldc_I4_0.ToInstruction());
                method.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
                implementation.Methods.Add(method);return method;
            }
            // getter 与接口同名，setter 被混淆成另一个名字：两侧原本会各拿一个名字
            var getter=Concrete("get_\u0002",MethodSig.CreateInstance(int32),true);
            var setter=Concrete("set_\u0005",MethodSig.CreateInstance(module.CorLibTypes.Void,int32),false);
            Concrete("\u0003",MethodSig.CreateInstance(module.CorLibTypes.Void),false);
            // get/set 被拆成两个 PropertyDef，这是 CS0535 的直接来源
            implementation.Properties.Add(new PropertyDefUser("\u0006",new PropertySig(true,int32)){GetMethod=getter});
            implementation.Properties.Add(new PropertyDefUser("\u0007",new PropertySig(true,int32)){SetMethod=setter});

            // 同类型里两个同名同签名的成员：C# 不允许（CS0111）
            var duplicate=new TypeDefUser("\u0009",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(duplicate);
            foreach(var attributes in new[]{MethodAttributes.Public|MethodAttributes.Virtual,
                MethodAttributes.Private|MethodAttributes.Virtual|MethodAttributes.Final})
            {
                var method=new MethodDefUser("\u000A",MethodSig.CreateInstance(module.CorLibTypes.Void),MethodImplAttributes.IL,attributes);
                method.Body=new CilBody();method.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
                duplicate.Methods.Add(method);
            }
            module.Write(contractInput);
        }
        NameRecoveryPass.Process(contractInput,contractOutput,Path.Combine(directory,"contract-map.json"));
        using(var module=ModuleDefMD.Load(contractOutput))
        {
            var contract=module.Types.Single(t=>t.IsInterface);
            var implementation=module.Types.Single(t=>t.Interfaces.Count!=0);
            var property=implementation.Properties.SingleOrDefault()
                ?? throw new Exception("Split interface properties were not merged back into one PropertyDef.");
            if(property.Name!=contract.Properties.Single().Name)
                throw new Exception("Implementation property name does not match the interface contract.");
            if(property.GetMethod==null || property.SetMethod==null)
                throw new Exception("Merged property lost an accessor.");
            if(property.GetMethod.Name!="get_"+property.Name || property.SetMethod.Name!="set_"+property.Name)
                throw new Exception("Accessor names do not follow the property name.");
            var duplicate=module.Types.Single(t=>t.Name=="Class9" || t.Methods.Count(m=>m.MethodSig?.Params.Count==0)==2 && !t.IsInterface && t.Interfaces.Count==0 && t.Properties.Count==0);
            if(duplicate.Methods.Select(m=>m.Name.String).Distinct().Count()!=duplicate.Methods.Count)
                throw new Exception("Duplicate member signatures were not given unique names.");
        }

        // ILSpy 只把"名字带点 + 有 .override"的方法当成显式接口实现，否则当成
        // 普通私有成员，反编译出来就是 CS0737。私有虚方法必须拿到 "接口.成员" 的名字。
        CheckExplicitImplementation(directory);

        // 抽象基类成员的实现靠签名才能认出来；基类方法被重写但丢了 virtual
        // 标志会让 C# 报 CS0506。两种都要在元数据层面修好。
        CheckAbstractAndOverrideRepair(directory);
        CheckExplicitImplementationIsSealed(directory);
        CheckPropertySignatureFallback(directory);

        Console.WriteLine("PASS: interface contract names, split property rows and duplicate signatures are normalized for C# recompilation.");

    }

    /// <summary>显式接口实现必须是"接口.成员"形状，并且保持私有。</summary>
    private static void CheckExplicitImplementation(string directory)
    {
        string input=Path.Combine(directory,"explicit-input.dll"),output=Path.Combine(directory,"explicit-output.dll");
        using(var module=new ModuleDefUser("Explicit.dll"))
        {
            new AssemblyDefUser("Explicit",new Version(1,0)).Modules.Add(module);
            var int32=module.CorLibTypes.Int32;
            var contract=new TypeDefUser("IContract",module.CorLibTypes.Object.TypeDefOrRef)
                {Attributes=TypeAttributes.Interface|TypeAttributes.Abstract|TypeAttributes.Public};
            module.Types.Add(contract);
            var contractMethod=new MethodDefUser("Compute",MethodSig.CreateInstance(int32),MethodImplAttributes.IL,
                MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.Abstract|MethodAttributes.NewSlot|MethodAttributes.HideBySig);
            contract.Methods.Add(contractMethod);

            var implementation=new TypeDefUser("Implementation",module.CorLibTypes.Object.TypeDefOrRef);
            implementation.Interfaces.Add(new InterfaceImplUser(contract));module.Types.Add(implementation);
            // 名字被抹平成 method_1：即使有 .override，ILSpy 也不认它是显式实现。
            var body=new MethodDefUser("method_1",MethodSig.CreateInstance(int32),MethodImplAttributes.IL,
                MethodAttributes.Private|MethodAttributes.Virtual|MethodAttributes.Final|MethodAttributes.NewSlot|MethodAttributes.HideBySig);
            body.Body=new CilBody();body.Body.Instructions.Add(OpCodes.Ldc_I4_0.ToInstruction());
            body.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            implementation.Methods.Add(body);
            // 显式实现这一行必须补上；没有它 ILSpy 完全看不出对应关系。
            body.Overrides.Add(new MethodOverride(body,contractMethod));
            module.Write(input);
        }
        NameRecoveryPass.Process(input,output,Path.Combine(directory,"explicit-map.json"));
        using(var module=ModuleDefMD.Load(output))
        {
            var type=module.Find("Implementation",false)!;
            var body=type.Methods.Single(m=>m.MethodSig.Params.Count==0);
            if(!body.Name.String.StartsWith("IContract."))
                throw new Exception("Explicit interface implementation was not renamed: "+body.Name);
            if(!body.IsPrivate)
                throw new Exception("Explicit interface implementation lost its private modifier.");
        }
    }

    /// <summary>契约属性在实现类里名字对不上时（访问器被改到别的名字），要按签名认出来。</summary>
    private static void CheckPropertySignatureFallback(string directory)
    {
        string input=Path.Combine(directory,"propfallback-input.dll"),output=Path.Combine(directory,"propfallback-output.dll");
        using(var module=new ModuleDefUser("PropFallback.dll"))
        {
            new AssemblyDefUser("PropFallback",new Version(1,0)).Modules.Add(module);
            var contract=new TypeDefUser("IContract",module.CorLibTypes.Object.TypeDefOrRef)
                {Attributes=TypeAttributes.Interface|TypeAttributes.Abstract|TypeAttributes.Public};
            module.Types.Add(contract);
            foreach(var name in new[]{"Alpha","Beta","Gamma"})
            {
                var property=new PropertyDefUser(name,new PropertySig(true,module.CorLibTypes.Int32,System.Array.Empty<TypeSig>()));
                contract.Properties.Add(property);
                var getter=new MethodDefUser("get_"+name,MethodSig.CreateInstance(module.CorLibTypes.Int32),MethodImplAttributes.IL,
                    MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.Abstract|MethodAttributes.NewSlot|MethodAttributes.HideBySig|MethodAttributes.SpecialName);
                getter.SemanticsAttributes=MethodSemanticsAttributes.Getter;
                property.GetMethods.Add(getter);
                contract.Methods.Add(getter);
            }
            var implementation=new TypeDefUser("Implementation",module.CorLibTypes.Object.TypeDefOrRef);
            implementation.Interfaces.Add(new InterfaceImplUser(contract));module.Types.Add(implementation);
            // 混淆后第三个 getter 挂在别的属性名下（名字对不上，只能靠签名认）。
            foreach(var name in new[]{"Alpha","Beta"})
            {
                var getter=MakeGetter(module,"get_"+name);
                implementation.Methods.Add(getter);
                var property=new PropertyDefUser(name,new PropertySig(true,module.CorLibTypes.Int32,System.Array.Empty<TypeSig>()));
                property.GetMethods.Add(getter);implementation.Properties.Add(property);
            }
            var renamed=MakeGetter(module,"get_Other");
            implementation.Methods.Add(renamed);
            var other=new PropertyDefUser("Other",new PropertySig(true,module.CorLibTypes.Int32,System.Array.Empty<TypeSig>()));
            other.GetMethods.Add(renamed);implementation.Properties.Add(other);
            module.Write(input);
        }
        NameRecoveryPass.Process(input,output,Path.Combine(directory,"propfallback-map.json"));
        using(var module=ModuleDefMD.Load(output))
        {
            var type=module.Find("Implementation",false)!;
            if(!type.Methods.Any(m=>m.Name.String=="get_Gamma"))
                throw new Exception("Contract property accessor was not recognized by signature: "+string.Join(",",type.Methods.Select(m=>m.Name)));
            if(!type.Properties.Any(p=>p.Name.String=="Gamma" && p.GetMethods.Any(m=>m.Name.String=="get_Gamma")))
                throw new Exception("Contract property was not reconstructed.");
        }
    }

    private static MethodDefUser MakeGetter(ModuleDef module,string name)=>
        new(name,MethodSig.CreateInstance(module.CorLibTypes.Int32),MethodImplAttributes.IL,
            MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.HideBySig|MethodAttributes.SpecialName)
        { SemanticsAttributes = MethodSemanticsAttributes.Getter, Body = MakeBody(module) };

    private static CilBody MakeBody(ModuleDef module)
    {
        var body=new CilBody();
        body.Instructions.Add(OpCodes.Ldc_I4_0.ToInstruction());
        body.Instructions.Add(OpCodes.Ret.ToInstruction());
        return body;
    }

    /// <summary>显式接口实现编译出来一定是 private final；缺 final 时 ILSpy 会多写 virtual（CS0106）。</summary>
    private static void CheckExplicitImplementationIsSealed(string directory)
    {
        string input=Path.Combine(directory,"sealed-input.dll"),output=Path.Combine(directory,"sealed-output.dll");
        using(var module=new ModuleDefUser("Sealed.dll"))
        {
            new AssemblyDefUser("Sealed",new Version(1,0)).Modules.Add(module);
            var int32=module.CorLibTypes.Int32;
            var contract=new TypeDefUser("IContract",module.CorLibTypes.Object.TypeDefOrRef)
                {Attributes=TypeAttributes.Interface|TypeAttributes.Abstract|TypeAttributes.Public};
            module.Types.Add(contract);
            var contractMethod=new MethodDefUser("Compute",MethodSig.CreateInstance(int32),MethodImplAttributes.IL,
                MethodAttributes.Public|MethodAttributes.Virtual|MethodAttributes.Abstract|MethodAttributes.NewSlot|MethodAttributes.HideBySig);
            contract.Methods.Add(contractMethod);

            var implementation=new TypeDefUser("Implementation",module.CorLibTypes.Object.TypeDefOrRef);
            implementation.Interfaces.Add(new InterfaceImplUser(contract));module.Types.Add(implementation);
            // 混淆后的形状：私有虚方法、没有 final，名字被抹平，只有 .override 说明实现关系。
            var body=new MethodDefUser("method_1",MethodSig.CreateInstance(int32),MethodImplAttributes.IL,
                MethodAttributes.Private|MethodAttributes.Virtual|MethodAttributes.HideBySig);
            body.Body=new CilBody();body.Body.Instructions.Add(OpCodes.Ldc_I4_0.ToInstruction());
            body.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
            implementation.Methods.Add(body);
            body.Overrides.Add(new MethodOverride(body,contractMethod));
            module.Write(input);
        }
        NameRecoveryPass.Process(input,output,Path.Combine(directory,"sealed-map.json"));
        using(var module=ModuleDefMD.Load(output))
        {
            var body=module.Find("Implementation",false)!.Methods.Single(m=>m.MethodSig.Params.Count==0);
            if(!body.Name.String.StartsWith("IContract."))
                throw new Exception("Explicit interface implementation was not renamed: "+body.Name);
            if(!body.IsFinal)
                throw new Exception("Explicit interface implementation was not sealed (CS0106: modifier 'virtual' is not valid).");
        }
    }

    /// <summary>
    /// 抽象基类成员的实现（名字不同）要按签名对齐；被重写的基类方法必须
    /// 保留 virtual 标志，否则 C# 报 CS0534 / CS0506。
    /// </summary>
    private static void CheckAbstractAndOverrideRepair(string directory)    {
        string input=Path.Combine(directory,"abstract-input.dll"),output=Path.Combine(directory,"abstract-output.dll");
        using(var module=new ModuleDefUser("Abstract.dll"))
        {
            new AssemblyDefUser("Abstract",new Version(1,0)).Modules.Add(module);
            var abstractType=new TypeDefUser("Base",module.CorLibTypes.Object.TypeDefOrRef){Attributes=TypeAttributes.Abstract|TypeAttributes.Public};
            module.Types.Add(abstractType);
            var abstractMethod=new MethodDefUser("Describe",MethodSig.CreateInstance(module.CorLibTypes.String),MethodImplAttributes.IL,
                MethodAttributes.Family|MethodAttributes.Virtual|MethodAttributes.Abstract|MethodAttributes.NewSlot|MethodAttributes.HideBySig);
            abstractType.Methods.Add(abstractMethod);
            var concreteMethod=new MethodDefUser("ToText",MethodSig.CreateInstance(module.CorLibTypes.String),MethodImplAttributes.IL,
                MethodAttributes.Family|MethodAttributes.Virtual|MethodAttributes.ReuseSlot|MethodAttributes.HideBySig);
            concreteMethod.Body=new CilBody();concreteMethod.Body.Instructions.Add(OpCodes.Ldstr.ToInstruction("x"));
            concreteMethod.Body.Instructions.Add(OpCodes.Ret.ToInstruction());

            var derived=new TypeDefUser("Derived",abstractType){Attributes=TypeAttributes.Public};
            module.Types.Add(derived);
            derived.Methods.Add(concreteMethod);
            // .override 指向基类，但基类方法的 virtual 标志被抹掉了（混淆器行为）。
            var stripped=new MethodDefUser("Describe",MethodSig.CreateInstance(module.CorLibTypes.String),MethodImplAttributes.IL,
                MethodAttributes.Family|MethodAttributes.HideBySig);
            abstractType.Methods[0]=stripped;
            concreteMethod.Overrides.Add(new MethodOverride(concreteMethod,stripped));
            module.Write(input);
        }
        NameRecoveryPass.Process(input,output,Path.Combine(directory,"abstract-map.json"));
        using(var module=ModuleDefMD.Load(output))
        {
            var baseMethod=module.Find("Base",false)!.Methods.Single(m=>m.Name.String=="Describe");
            if(!baseMethod.IsVirtual)
                throw new Exception("Overridden base method was not restored to virtual (CS0506).");
            if(!module.Find("Derived",false)!.Methods.Any(m=>m.Name.String=="Describe"))
                throw new Exception("Abstract base member implementation was not aligned by signature (CS0534).");
        }
    }
}
