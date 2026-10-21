using System.Reflection;
using EazFixer;
using EazyDevirt;
using Newtonsoft.Json;

namespace Unpacker;

internal static class WorkerEntry
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { Environment.Exit(WorkerDesktop.Run(args, Run)); }
        catch(Exception ex) { Console.Error.WriteLine(ex); Environment.Exit(1); }
    }

    private static int Run(string[] args)
    {
        Console.OutputEncoding=System.Text.Encoding.UTF8;
        try
        {
            switch(args[0])
            {
                case "pipeline": return RecoveryPipeline.Run(args);
                case "--validate-worker": return ClrValidator.Run(args[1],args[2],args[3],args.Length>4?int.Parse(args[4]):5,args.Length>5?args[5]:null);
                case "--vm-worker":
                    ProgressEvents.Send(3,"info","加载 VM 还原后端");
                    var vmProgress=new ProgressEvents.Counter(3);
                    File.WriteAllText(args[3],VmBridge.Process(args[1],args[2],(message,current,total)=>vmProgress.Report(message switch {
                        "ResourceParsing"=>"VM 资源解析", "OpCodeMapping"=>"VM 操作码映射", "MethodDiscovery"=>"VM 方法发现", "MethodDevirtualizer"=>"读取 VM 方法体、校准头部布局", _=>message},current,total)).ToString());return 0;
                case "--index-strings":
                case "--strings-worker":
                    ProgressEvents.Send(3,"info","加载复处理后端");
                    using(var dependencies=new DependencyLoadScope(args.Length>4?args[4]:args[1],args[1]))
                    {
                        var stages=FixerBridge.Process(args[1],args[2],true,(name,state,count)=>ProgressEvents.Backend(3,name,state,count,"复处理："),args[0]!="--index-strings");
                        File.WriteAllText(args[3],JsonConvert.SerializeObject(stages));
                        return stages.Any(s=>s.Status=="failed")?2:0;
                    }
                case "source": ProgressEvents.Send(6,"info","加载待反编译程序集");return SourceCompiler.Run(args[1],args[2],args[3],args[4]);
                case "string-index": return StringSearchIndex.Run(args[1],args[2],args[3]);
                case "--names-worker": NameRecoveryPass.Process(args[1],args[2],args[3]);return 0;
                case "--enum-worker": return EnumRecoveryPass.Rewrite(args[1],args[2]);
                case "--enum-probe":
                    using(var module=dnlib.DotNet.ModuleDefMD.Load(args[1]))
                    {
                        var collected=EnumRecoveryPass.Collect(module);
                        var rows=module.GetTypes().Where(t=>t.IsEnum).Select(t=>new{
                            Name=t.FullName,
                            Declared=t.Fields.Where(f=>f.IsLiteral).Select(f=>Convert.ToInt64(f.Constant?.Value??0)).OrderBy(v=>v).ToArray(),
                            Collected=collected.TryGetValue(t,out var set)?set.ToArray():Array.Empty<long>()});
                        File.WriteAllText(args[2],Newtonsoft.Json.JsonConvert.SerializeObject(rows,Newtonsoft.Json.Formatting.Indented));
                    }
                    return 0;
                case "--dump-types":
                    using(var module=dnlib.DotNet.ModuleDefMD.Load(args[1]))
                    {
                        var rows=module.GetTypes().Select(t=>new{
                            Name=t.FullName,Base=t.BaseType?.FullName,IsEnum=t.IsEnum,Vis=t.Visibility.ToString(),
                            Fields=t.Fields.Select(f=>new{Name=f.Name.String,Static=f.IsStatic,Literal=f.IsLiteral,
                                Const=f.IsLiteral?f.Constant?.Value?.ToString():null,Type=f.FieldSig?.Type?.FullName})}).ToArray();
                        File.WriteAllText(args[2],Newtonsoft.Json.JsonConvert.SerializeObject(rows,Newtonsoft.Json.Formatting.Indented));
                    }
                    return 0;
                case "--dump-members":
                    using(var module=dnlib.DotNet.ModuleDefMD.Load(args[1]))
                    {
                        var type=module.Find(args[2],false) ?? throw new ArgumentException("Type not found: "+args[2]);
                        var sb=new System.Text.StringBuilder();
                        sb.AppendLine("Type: "+type.FullName+"  base="+type.BaseType?.FullName);
                        sb.AppendLine("--- methods ---");
                        foreach(var m in type.Methods)
                            sb.AppendLine(m.Name.String+" | "+m.MethodSig+" | "+m.Attributes+" | impl="+m.ImplAttributes+" | rid="+m.MDToken.Rid);
                        sb.AppendLine("--- properties ---");
                        foreach(var p in type.Properties)
                            sb.AppendLine(p.Name.String+" : "+p.PropertySig+" | "
                                +(p.GetMethod==null?"-":"get="+p.GetMethod.Name.String)+" "
                                +(p.SetMethod==null?"-":"set="+p.SetMethod.Name.String));
                        sb.AppendLine("--- events ---");
                        foreach(var e in type.Events)
                            sb.AppendLine(e.Name.String+" : "+e.EventType+" | add="+(e.AddMethod?.Name.String??"-")+" remove="+(e.RemoveMethod?.Name.String??"-"));
                        sb.AppendLine("--- interfaces ---");
                        foreach(var i in type.Interfaces)sb.AppendLine(i.Interface.FullName);
                        sb.AppendLine("--- method impls ---");
                        foreach(var m in type.Methods)
                            foreach(var ov in m.Overrides)
                                sb.AppendLine(m.Name.String+"  ->  "+ov.MethodDeclaration.FullName);
                        File.WriteAllText(args[3],sb.ToString());
                    }
                    return 0;
                case "--map-worker": RebuiltValidation.MapMethods(args[1],args[2],args[3],args[4]);return 0;
                case "--flow-worker": ControlFlowCleanupPass.Process(args[1],args[2],args[3]);return 0;
                case "--self-test": return SelfTests.Run();
                case "--desktop-probe": Console.WriteLine("Desktop: "+WorkerDesktop.CurrentName);return 0;
                case "--test-sleep": System.Threading.Thread.Sleep(30000);return 0;
                case "--test-progress":
                    Console.WriteLine("raw fixture stdout");Console.Error.WriteLine("raw fixture stderr");
                    Console.WriteLine(ProgressEvents.Prefix+"{malformed}");
                    ProgressEvents.Send(3,"progress","实时测试：开始",0,2);
                    System.Threading.Thread.Sleep(1400);
                    ProgressEvents.Send(3,"progress","实时测试：结束",2,2);return 0;
                case "--test-progress-relay": return ProcessRunner.Run(Assembly.GetExecutingAssembly().Location,new[]{"--test-progress"},args[1],10);
                case "--test-env":
                    Console.WriteLine(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PATH"))?"PATH-MISSING":"PATH-OK");
                    Console.WriteLine(ProgressEvents.Enabled?"PROGRESS-OK":"PROGRESS-MISSING");return 0;
                case "--test-sleep-tree":
                    using(var child=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Assembly.GetExecutingAssembly().Location,"--test-sleep"){UseShellExecute=false,CreateNoWindow=true}))
                    {Console.WriteLine("Fixture child: "+child!.Id);Console.Out.Flush();System.Threading.Thread.Sleep(30000);}return 0;
                default: throw new ArgumentException("Unknown worker operation.");
            }
        }
        catch(Exception ex){ProgressEvents.Send(ProgressEvents.CurrentStage,"error",ex.Message);Console.Error.WriteLine(ex);return 1;}
    }
}
