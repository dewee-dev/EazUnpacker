using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace Unpacker.Gui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 用法：
        //   UnpackerGui.exe                       只打开界面
        //   UnpackerGui.exe <输入>                预填输入（可再把 .exe/.dll 拖到本程序上）
        //   UnpackerGui.exe <输入> <输出>          预填输入与输出
        //   --analyzer        切到「仅分析」模式
        //   --run             打开后立即开始
        //   --exit-when-done  跑完自动退出，退出码与命令行程序一致（便于脚本/CI）
        bool analyze = args.Any(a => a.Equals("--analyzer", StringComparison.OrdinalIgnoreCase));
        bool autoRun = args.Any(a => a.Equals("--run", StringComparison.OrdinalIgnoreCase));
        bool exitWhenDone = args.Any(a => a.Equals("--exit-when-done", StringComparison.OrdinalIgnoreCase));

        List<string> files = args
            .Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .Where(File.Exists)
            .ToList();

        string? input = files.Count > 0 ? files[0] : null;
        string? output = files.Count > 1 ? files[1] : null;

        Application.Run(new MainForm(input, output, analyze, autoRun, exitWhenDone));
    }
}
