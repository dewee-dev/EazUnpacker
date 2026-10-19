using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Text;

namespace Unpacker;

internal static class ProcessRunner
{
    internal static int Run(string exe, IEnumerable<string> args, string log, int timeoutSeconds, Action<ProgressEvent>? progress=null, Action<string>? output=null)
    {
        var guard = new object();
        using var writer = new StreamWriter(log, false, new UTF8Encoding(false)) { AutoFlush = true };
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe))!
            }
        };
        if(progress!=null || ProgressEvents.Enabled)ApplyEnvironment(process.StartInfo);
        void Receive(string? line)
        {
            if(line==null)return;
            lock(guard){
                writer.WriteLine(line);
                if(ProgressEvents.TryRead(line,out var item)){
                    if(progress!=null)progress(item!);
                    else ProgressEvents.Send(item!.Stage,item.State,item.Message,item.Current,item.Total);
                }
                else output?.Invoke(line);
            }
        }
        process.OutputDataReceived += (_, e) => Receive(e.Data);
        process.ErrorDataReceived += (_, e) => Receive(e.Data);
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        bool exited = process.WaitForExit(checked(timeoutSeconds * 1000));
        if (!exited && !process.HasExited)
        {
            // .NET Framework lacks Kill(entireProcessTree); use the Windows process-tree terminator.
            using var killer = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/PID " + process.Id + " /T /F")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (killer != null) { killer.BeginOutputReadLine(); killer.BeginErrorReadLine(); killer.WaitForExit(5000); }
            if (!process.WaitForExit(5000)) process.Kill();
        }
        process.WaitForExit();
        lock (guard) { if (!exited) writer.WriteLine("Worker timeout; process tree terminated."); writer.Flush(); }
        return exited ? process.ExitCode : 124;
    }

    // .NET Framework 的 ProcessStartInfo.EnvironmentVariables 是大小写不敏感的 StringDictionary，
    // 首次访问时会把当前进程环境整体复制进去。若环境里同时存在 http_proxy / HTTP_PROXY 这类
    // 仅大小写不同的变量（Git Bash、代理工具、CI 容器、Docker 里很常见），复制到重复键会抛
    // ArgumentException，并留下一个被截断的集合——PATH、TEMP、SystemRoot 等都可能丢在截断点之后。
    // 旧代码把这个异常吞掉了，结果既没给子进程设上进度开关（后端细粒度进度全部丢失），
    // 又让子进程继承了一份残缺的环境。这里在异常后补齐缺失项，再写入进度开关。
    private static void ApplyEnvironment(ProcessStartInfo info)
    {
        StringDictionary variables;
        try { variables = info.EnvironmentVariables; }
        catch(ArgumentException)
        {
            // 字段已被赋成非 null 的半成品，二次访问不再抛异常。
            try { variables = info.EnvironmentVariables; }
            catch(ArgumentException) { return; }
            var known = new HashSet<string>(variables.Keys.Cast<string>(), StringComparer.OrdinalIgnoreCase);
            foreach(DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                string key = (string)entry.Key;
                if(known.Add(key)) variables[key] = (string)entry.Value;
            }
        }
        variables[ProgressEvents.EnvironmentKey] = "1";
    }

    // Windows CommandLineToArgvW quoting, including trailing backslashes and embedded quotes.
    private static string Quote(string arg)
    {
        var text = new StringBuilder("\""); int slashes = 0;
        foreach (char c in arg)
        {
            if (c == '\\') { slashes++; continue; }
            if (c == '\"') { text.Append('\\', slashes * 2 + 1); text.Append(c); slashes = 0; continue; }
            text.Append('\\', slashes); slashes = 0; text.Append(c);
        }
        text.Append('\\', slashes * 2); text.Append('"'); return text.ToString();
    }
}
