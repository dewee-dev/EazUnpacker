using ICSharpCode.Decompiler.CSharp.Syntax;

namespace Unpacker;

internal static class SourceCompatibilityTests
{
    internal static void Run(string root)
    {
        var type=new TypeDeclaration {Name="ConstructorFixture"};
        var field=new FieldDeclaration {ReturnType=new PrimitiveType("string")};
        field.Variables.Add(new VariableInitializer("text"));type.Members.Add(field);
        var constructor=new ConstructorDeclaration {Name=type.Name,Body=new BlockStatement()};type.Members.Add(constructor);
        constructor.Body.Statements.Add(new BlockStatement(new ExpressionStatement(new AssignmentExpression(new IdentifierExpression("text"),new PrimitiveExpression("initial")))));
        constructor.Body.Statements.Add(BaseCall());
        type.AcceptVisitor(new MetadataCompatibility());
        if(field.Variables.Single().Initializer is not PrimitiveExpression {Value:"initial"}
            || constructor.Body.Statements.Count!=0 || constructor.Initializer?.ConstructorInitializerType!=ConstructorInitializerType.Base)
            throw new Exception("Pre-base constant field block was not preserved.");

        var temporaryType=new TypeDeclaration {Name="TemporaryFixture"};
        var temporaryField=new FieldDeclaration {ReturnType=new SimpleType("Payload")};
        temporaryField.Variables.Add(new VariableInitializer("payload"));temporaryType.Members.Add(temporaryField);
        var temporaryConstructor=new ConstructorDeclaration {Name=temporaryType.Name,Body=new BlockStatement()};temporaryType.Members.Add(temporaryConstructor);
        temporaryConstructor.Body.Statements.Add(new VariableDeclarationStatement(new SimpleType("Payload"),"local",new ObjectCreateExpression(new SimpleType("Payload"))));
        temporaryConstructor.Body.Statements.Add(new BlockStatement(new ExpressionStatement(new AssignmentExpression(new IdentifierExpression("payload"),new IdentifierExpression("local")))));
        temporaryConstructor.Body.Statements.Add(BaseCall());
        temporaryType.AcceptVisitor(new MetadataCompatibility());
        if(temporaryField.Variables.Single().Initializer is not ObjectCreateExpression || temporaryConstructor.Body.Statements.Count!=0
            || temporaryType.Members.OfType<MethodDeclaration>().Any())throw new Exception("Pre-base temporary store became invalid static instance access.");

        string fixture=Path.Combine(root,"probing");Directory.CreateDirectory(fixture);
        Directory.CreateDirectory(Path.Combine(fixture,"Components","Vendor"));
        string original=Path.Combine(fixture,"app.exe");
        File.WriteAllText(original+".config","<configuration><runtime><assemblyBinding xmlns='urn:schemas-microsoft-com:asm.v1'><probing privatePath='Components;Components\\Vendor;..;C:\\Windows'/></assemblyBinding></runtime></configuration>");
        string[] paths=SourceCompiler.DependencyDirectories(original,original);
        if(paths.Length!=3 || !paths.Contains(Path.Combine(fixture,"Components","Vendor")) || paths.Contains(root))
            throw new Exception("Config probing paths were not resolved within the application directory.");
        Console.WriteLine("PASS: pre-base constant blocks/temporary field stores and contained config probing paths.");
        CheckModuleInitializerHelper(root);
    }

    private static void CheckModuleInitializerHelper(string root)
    {
        // <Module> 里除 .cctor 之外的静态无参 void 方法（混淆器拆出来的模块初始化辅助方法）
        // 只被 .cctor 调用，导出时必须就地展开，否则整个源码导出会失败。
        string path=Path.Combine(root,"global-init.dll"),output=Path.Combine(root,"global-init-src");
        Directory.CreateDirectory(output);
        using(var module=new dnlib.DotNet.ModuleDefUser("GlobalInit.dll"))
        {
            var assembly=new dnlib.DotNet.AssemblyDefUser("GlobalInit",new Version(1,0));assembly.Modules.Add(module);
            var target=new dnlib.DotNet.TypeDefUser("GlobalInit","Target",module.CorLibTypes.Object.TypeDefOrRef);module.Types.Add(target);
            var init=new dnlib.DotNet.MethodDefUser("Init",dnlib.DotNet.MethodSig.CreateStatic(module.CorLibTypes.Void),
                dnlib.DotNet.MethodImplAttributes.IL,dnlib.DotNet.MethodAttributes.Public|dnlib.DotNet.MethodAttributes.Static);
            init.Body=new dnlib.DotNet.Emit.CilBody();init.Body.Instructions.Add(dnlib.DotNet.Emit.OpCodes.Ret.ToInstruction());target.Methods.Add(init);
            var global=module.GlobalType;
            dnlib.DotNet.MethodDef Global(string name,dnlib.DotNet.IMethod operand,dnlib.DotNet.MethodAttributes attributes)
            {
                var method=new dnlib.DotNet.MethodDefUser(name,dnlib.DotNet.MethodSig.CreateStatic(module.CorLibTypes.Void),
                    dnlib.DotNet.MethodImplAttributes.IL,attributes);
                method.Body=new dnlib.DotNet.Emit.CilBody();
                if(operand!=null)method.Body.Instructions.Add(dnlib.DotNet.Emit.OpCodes.Call.ToInstruction(operand));
                method.Body.Instructions.Add(dnlib.DotNet.Emit.OpCodes.Ret.ToInstruction());
                global.Methods.Add(method);return method;
            }
            var helper=Global("f0659e5905454a5e99b9752afc78b700",init,dnlib.DotNet.MethodAttributes.Private|dnlib.DotNet.MethodAttributes.Static);
            Global(".cctor",helper,dnlib.DotNet.MethodAttributes.Private|dnlib.DotNet.MethodAttributes.Static
                |dnlib.DotNet.MethodAttributes.SpecialName|dnlib.DotNet.MethodAttributes.RTSpecialName);
            module.Write(path);
        }
        if(SourceCompiler.ExportModuleInitializer(path,output,false)!=1)
            throw new Exception("Module initializer helper was not inlined.");
        string source=File.ReadAllText(Path.Combine(output,"UnpackModuleInitializer.cs"));
        if(!source.Contains("GlobalInit.Target.Init()") || source.Contains("<Module>"))
            throw new Exception("Global helper export produced an invalid initializer: "+source);
        Console.WriteLine("PASS: <Module> helper methods are inlined into the exported module initializer.");
    }

    private static ExpressionStatement BaseCall()=>new(new InvocationExpression(new MemberReferenceExpression(new BaseReferenceExpression(),".ctor")));
}
