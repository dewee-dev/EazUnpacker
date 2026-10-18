using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Unpacker.Gui;

/// <summary>
/// 前端外壳：只负责收集路径、驱动 Unpacker 命令行程序、把返回信息可视化。
/// 所有恢复逻辑仍然在 Unpacker-net48.exe 里，GUI 不复制任何业务代码。
/// </summary>
internal sealed class MainForm : Form
{
    private const string Title = "EazUnpacker GUI";

    // [12:34:56] [3/8 VM 还原] [开始] 消息  | 阶段 1.2s / 总耗时 4.5s
    private static readonly Regex ProgressPattern = new(
        @"^\[\d{2}:\d{2}:\d{2}\] \[(\d)/8 ([^\]]*)\] \[([^\]]*)\] (.*)$",
        RegexOptions.Compiled);

    private static readonly Regex RunDirectoryPattern = new(
        @"实时日志/诊断目录：(.*?)(?:\s{2}\||$)",
        RegexOptions.Compiled);

    private static readonly char[] QuoteTriggers = { ' ', '\t', '\n', '\v', '"' };

    private readonly ConcurrentQueue<(string Text, bool IsError)> _pending = new();
    private readonly StringBuilder _analyzerJson = new();

    private readonly Font _logFont = new("Consolas", 9f);
    private readonly Color _logForeColor = Color.FromArgb(28, 28, 28);

    private readonly TextBox _inputBox = new();
    private readonly TextBox _outputBox = new();
    private readonly RichTextBox _logBox = new();
    private readonly TextBox _jsonBox = new();
    private readonly ListView _artifactList = new();
    private readonly RadioButton _unpackRadio = new();
    private readonly RadioButton _analyzerRadio = new();
    private readonly Button _inputBrowse = new();
    private readonly Button _outputBrowse = new();
    private readonly Button _openOutput = new();
    private readonly Button _copyCommand = new();
    private readonly Button _startButton = new();
    private readonly Button _stopButton = new();
    private readonly Button _clearLogButton = new();
    private readonly Label _statusLabel = new();
    private readonly Label _stageLabel = new();
    private readonly ProgressBar _progressBar = new();
    private readonly SplitContainer _split = new();
    private readonly System.Windows.Forms.Timer _pumpTimer = new();

    private GuiSettings _settings = new();
    private Process? _process;
    private DateTime _startedAt;
    private string? _runDirectory;
    private string? _lastOutput;
    private bool _closing;
    private bool _suppressAutoOutput;
    private bool _outputAutoDerived = true;
    private bool _autoStart;
    private readonly bool _exitWhenDone;

    internal MainForm(
        string? initialInput = null,
        string? initialOutput = null,
        bool analyzeMode = false,
        bool autoStart = false,
        bool exitWhenDone = false)
    {
        _settings = GuiSettings.Load();

        Text = Title + " — 解包 / 分析前端";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1000, 660);
        MinimumSize = new Size(860, 560);
        Font = new Font("Microsoft YaHei UI", 9f);
        AllowDrop = true;

        SuspendLayout();

        // Dock 顺序：先加 Fill，再加 Bottom/Top —— 后加的先参与停靠。
        Controls.Add(BuildBody());
        Controls.Add(BuildFooter());
        Controls.Add(BuildHeader());

        ResumeLayout(false);

        WireEvents();
        UpdateModeUi();

        _pumpTimer.Interval = 120;
        _pumpTimer.Tick += (_, _) => Pump();

        _exitWhenDone = exitWhenDone;
        if (analyzeMode) _analyzerRadio.Checked = true;
        if (!string.IsNullOrEmpty(initialInput)) ApplyInput(initialInput!);
        if (!string.IsNullOrEmpty(initialOutput)) SetOutputText(initialOutput!);
        _autoStart = autoStart && !string.IsNullOrEmpty(initialInput);
    }

    // ───────────────────────────── 界面构建 ─────────────────────────────

    private Control BuildHeader()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 3,
            Padding = new Padding(12, 12, 12, 4),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        for (int i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        ConfigureTextBox(_inputBox);
        ConfigureTextBox(_outputBox);

        ConfigureButton(_inputBrowse, "浏览…", 84);
        ConfigureButton(_outputBrowse, "浏览…", 84);
        ConfigureButton(_openOutput, "打开目录", 96);
        ConfigureButton(_copyCommand, "复制命令", 96);

        _inputBrowse.Click += (_, _) => PickInput();
        _outputBrowse.Click += (_, _) => PickOutput();
        _openOutput.Click += (_, _) => OpenPath(_unpackRadio.Checked ? _outputBox.Text.Trim() : Path.GetDirectoryName(_inputBox.Text.Trim()) ?? "");
        _copyCommand.Click += (_, _) => CopyCommandLine();

        grid.Controls.Add(CreateLabel("输入文件"), 0, 0);
        grid.Controls.Add(_inputBox, 1, 0);
        grid.Controls.Add(_inputBrowse, 2, 0);
        grid.Controls.Add(_copyCommand, 3, 0);

        grid.Controls.Add(CreateLabel("输出文件"), 0, 1);
        grid.Controls.Add(_outputBox, 1, 1);
        grid.Controls.Add(_outputBrowse, 2, 1);
        grid.Controls.Add(_openOutput, 3, 1);

        grid.Controls.Add(CreateLabel("操作"), 0, 2);

        var modes = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(3, 4, 3, 4),
        };
        _unpackRadio.Text = "解包恢复（unpack）";
        _unpackRadio.AutoSize = true;
        _unpackRadio.Checked = true;
        _unpackRadio.Margin = new Padding(0, 2, 18, 2);
        _analyzerRadio.Text = "仅分析（analyzer，不写文件）";
        _analyzerRadio.AutoSize = true;
        _analyzerRadio.Margin = new Padding(0, 2, 0, 2);
        modes.Controls.Add(_unpackRadio);
        modes.Controls.Add(_analyzerRadio);
        grid.Controls.Add(modes, 1, 2);

        var hint = new Label
        {
            Text = "可直接把 .exe / .dll 拖进窗口",
            AutoSize = true,
            ForeColor = Color.FromArgb(120, 120, 120),
            Anchor = AnchorStyles.Left,
            Margin = new Padding(3, 8, 3, 4),
        };
        grid.Controls.Add(hint, 2, 2);
        grid.SetColumnSpan(hint, 2);

        return grid;
    }

    private Control BuildBody()
    {
        _logBox.Dock = DockStyle.Fill;
        _logBox.ReadOnly = true;
        _logBox.WordWrap = false;
        _logBox.DetectUrls = false;
        _logBox.HideSelection = false;
        _logBox.ScrollBars = RichTextBoxScrollBars.Both;
        _logBox.BackColor = Color.FromArgb(250, 250, 250);
        _logBox.ForeColor = _logForeColor;
        _logBox.BorderStyle = BorderStyle.None;
        _logBox.Font = _logFont;

        var logGroup = new GroupBox
        {
            Text = "运行日志",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 6, 10, 10),
        };
        logGroup.Controls.Add(_logBox);

        _artifactList.Dock = DockStyle.Fill;
        _artifactList.View = View.Details;
        _artifactList.FullRowSelect = true;
        _artifactList.GridLines = false;
        _artifactList.MultiSelect = false;
        _artifactList.HideSelection = false;
        _artifactList.Columns.Add("项目", 150);
        _artifactList.Columns.Add("路径 / 值", 640);
        _artifactList.Columns.Add("状态", 90);
        _artifactList.DoubleClick += (_, _) => OpenSelectedArtifact();

        _jsonBox.Dock = DockStyle.Fill;
        _jsonBox.Multiline = true;
        _jsonBox.ReadOnly = true;
        _jsonBox.WordWrap = false;
        _jsonBox.ScrollBars = ScrollBars.Both;
        _jsonBox.Font = _logFont;
        _jsonBox.BorderStyle = BorderStyle.None;
        _jsonBox.BackColor = Color.FromArgb(250, 250, 250);

        var resultGroup = new GroupBox
        {
            Text = "返回信息",
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 6, 10, 10),
        };
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var artifactPage = new TabPage("产物与路径") { Padding = new Padding(4) };
        artifactPage.Controls.Add(_artifactList);
        var jsonPage = new TabPage("原始 JSON") { Padding = new Padding(4) };
        jsonPage.Controls.Add(_jsonBox);
        tabs.TabPages.Add(artifactPage);
        tabs.TabPages.Add(jsonPage);
        resultGroup.Controls.Add(tabs);

        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Horizontal;
        _split.Panel1MinSize = 140;
        _split.Panel2MinSize = 140;
        _split.Panel1.Controls.Add(logGroup);
        _split.Panel2.Controls.Add(resultGroup);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 4, 12, 4) };
        host.Controls.Add(_split);
        return host;
    }

    private Control BuildFooter()
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(12, 4, 12, 12),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _statusLabel.Text = "就绪";
        _statusLabel.AutoSize = true;
        _statusLabel.Anchor = AnchorStyles.Left;
        _statusLabel.Margin = new Padding(0, 8, 12, 0);

        _stageLabel.Text = "";
        _stageLabel.AutoSize = true;
        _stageLabel.ForeColor = Color.FromArgb(90, 90, 90);
        _stageLabel.Anchor = AnchorStyles.Left;
        _stageLabel.Margin = new Padding(0, 8, 12, 0);

        _progressBar.Minimum = 0;
        _progressBar.Maximum = 8;
        _progressBar.Value = 0;
        _progressBar.Style = ProgressBarStyle.Continuous;
        _progressBar.Size = new Size(200, 16);
        _progressBar.Anchor = AnchorStyles.Left;
        _progressBar.Margin = new Padding(0, 6, 12, 0);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
        };
        ConfigureButton(_startButton, "开始解包", 104);
        ConfigureButton(_stopButton, "停止", 84);
        ConfigureButton(_clearLogButton, "清空日志", 96);
        _stopButton.Enabled = false;
        buttons.Controls.Add(_startButton);
        buttons.Controls.Add(_stopButton);
        buttons.Controls.Add(_clearLogButton);

        grid.Controls.Add(_statusLabel, 0, 0);
        grid.Controls.Add(_progressBar, 1, 0);
        grid.Controls.Add(_stageLabel, 2, 0);
        grid.Controls.Add(buttons, 3, 0);
        return grid;
    }

    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 8, 8, 4),
    };

    private static void ConfigureTextBox(TextBox box)
    {
        box.Dock = DockStyle.Fill;
        box.Margin = new Padding(3, 4, 3, 4);
        box.BorderStyle = BorderStyle.FixedSingle;
    }

    private static void ConfigureButton(Button button, string text, int width)
    {
        button.Text = text;
        button.AutoSize = false;
        button.Size = new Size(width, 28);
        button.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        button.Margin = new Padding(3, 4, 3, 4);
    }

    // ───────────────────────────── 事件绑定 ─────────────────────────────

    private void WireEvents()
    {
        _inputBox.TextChanged += (_, _) =>
        {
            if (_suppressAutoOutput) return;
            if (_outputAutoDerived) ApplySuggestedOutput(_inputBox.Text.Trim());
        };
        _outputBox.TextChanged += (_, _) =>
        {
            if (!_suppressAutoOutput) _outputAutoDerived = false;
        };

        _unpackRadio.CheckedChanged += (_, _) => UpdateModeUi();
        _analyzerRadio.CheckedChanged += (_, _) => UpdateModeUi();

        _startButton.Click += async (_, _) => await RunAsync();
        _stopButton.Click += (_, _) => StopProcess(confirm: true);
        _clearLogButton.Click += (_, _) => _logBox.Clear();

        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (_process != null) return;
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            string? candidate = files.FirstOrDefault(IsAssemblyPath);
            if (candidate == null)
            {
                AppendLog("只能拖入 .exe / .dll 文件。", Color.Firebrick);
                return;
            }
            ApplyInput(candidate);
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ClampToScreen();
        _split.SplitterDistance = Math.Max(_split.Panel1MinSize, (int)(_split.Height * 0.60));
        _inputBox.Focus();
        if (_autoStart)
        {
            _autoStart = false;
            BeginInvoke(new Action(() => _ = RunAsync()));
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _closing = true;
        _pumpTimer.Stop();
        if (_process != null) StopProcess(confirm: false);
        base.OnFormClosing(e);
    }

    /// <summary>小屏幕上不要把按钮挤出可见区域。</summary>
    private void ClampToScreen()
    {
        Rectangle work = Screen.FromControl(this).WorkingArea;
        int maxWidth = Math.Max(720, work.Width - 40);
        int maxHeight = Math.Max(480, work.Height - 40);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, maxWidth), Math.Min(MinimumSize.Height, maxHeight));
        Size = new Size(Math.Min(Width, maxWidth), Math.Min(Height, maxHeight));
        Location = new Point(
            work.Left + Math.Max(0, (work.Width - Width) / 2),
            work.Top + Math.Max(0, (work.Height - Height) / 2));
    }

    // ───────────────────────────── 路径处理 ─────────────────────────────

    private static bool IsAssemblyPath(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyInput(string path)
    {
        _inputBox.Text = path;
        if (_outputAutoDerived) ApplySuggestedOutput(path);
        _settings.LastInputDirectory = Path.GetDirectoryName(path);
        _settings.Save();
    }

    private void ApplySuggestedOutput(string input)
    {
        if (input.Length == 0 || !IsAssemblyPath(input))
        {
            SetOutputText("");
            return;
        }
        string extension = Path.GetExtension(input);
        string suggestion = Path.Combine(
            Path.GetDirectoryName(input) ?? "",
            Path.GetFileNameWithoutExtension(input) + ".unpacked" + extension);
        SetOutputText(suggestion);
        _outputAutoDerived = true;
    }

    private void SetOutputText(string text)
    {
        _suppressAutoOutput = true;
        _outputBox.Text = text;
        _suppressAutoOutput = false;
        _outputAutoDerived = true;
    }

    private void PickInput()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择要处理的程序集",
            Filter = "程序集 (*.exe;*.dll)|*.exe;*.dll|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        string? start = Directory.Exists(_settings.LastInputDirectory) ? _settings.LastInputDirectory : null;
        if (start == null && IsAssemblyPath(_inputBox.Text.Trim()))
            start = Path.GetDirectoryName(_inputBox.Text.Trim());
        if (start != null) dialog.InitialDirectory = start;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ApplyInput(dialog.FileName);
    }

    private void PickOutput()
    {
        string input = _inputBox.Text.Trim();
        string extension = IsAssemblyPath(input) ? Path.GetExtension(input) : ".exe";
        using var dialog = new SaveFileDialog
        {
            Title = "选择输出文件",
            Filter = extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                ? "程序集 (*.dll)|*.dll|可执行文件 (*.exe)|*.exe"
                : "可执行文件 (*.exe)|*.exe|程序集 (*.dll)|*.dll",
            DefaultExt = extension.TrimStart('.'),
            AddExtension = true,
            OverwritePrompt = false,
        };
        string current = _outputBox.Text.Trim();
        if (current.Length > 0)
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
            dialog.FileName = Path.GetFileName(current);
        }
        else if (Directory.Exists(_settings.LastOutputDirectory))
        {
            dialog.InitialDirectory = _settings.LastOutputDirectory;
        }
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetOutputText(dialog.FileName);
        _settings.LastOutputDirectory = Path.GetDirectoryName(dialog.FileName);
        _settings.Save();
    }

    private void UpdateModeUi()
    {
        bool unpack = _unpackRadio.Checked;
        bool idle = _process == null;
        _outputBox.Enabled = unpack && idle;
        _outputBrowse.Enabled = unpack && idle;
        _openOutput.Enabled = idle;
        _startButton.Text = unpack ? "开始解包" : "开始分析";
        if (idle) _statusLabel.Text = unpack ? "就绪：解包恢复" : "就绪：仅分析（不写文件）";
    }

    private List<string> BuildArguments()
    {
        string input = _inputBox.Text.Trim();
        if (_analyzerRadio.Checked) return new List<string> { "analyzer", input };
        return new List<string> { "unpack", input, _outputBox.Text.Trim() };
    }

    private void CopyCommandLine()
    {
        string? cli = ResolveCli(interactive: false);
        List<string> args = BuildArguments();
        string text = (cli == null ? "Unpacker-net48.exe" : Quote(Path.GetFileName(cli)))
            + " " + string.Join(" ", args.Select(Quote));
        try
        {
            Clipboard.SetText(text);
            AppendLog("已复制命令：" + text, Color.FromArgb(90, 90, 90));
        }
        catch (Exception ex)
        {
            AppendLog("复制失败：" + ex.Message, Color.Firebrick);
        }
    }

    // ───────────────────────────── 运行 ─────────────────────────────

    private async Task RunAsync()
    {
        if (_process != null) return;

        string? cli = ResolveCli(interactive: true);
        if (cli == null) return;

        string input = _inputBox.Text.Trim();
        if (input.Length == 0)
        {
            Warn("请先选择输入文件（.exe / .dll）。");
            _inputBox.Focus();
            return;
        }
        if (!File.Exists(input))
        {
            Warn("输入文件不存在：" + input);
            return;
        }
        if (!IsAssemblyPath(input))
        {
            Warn("输入必须是 .exe 或 .dll 文件。");
            return;
        }

        bool analyze = _analyzerRadio.Checked;
        string output = _outputBox.Text.Trim();
        if (!analyze)
        {
            if (output.Length == 0)
            {
                Warn("请先选择输出文件路径。");
                _outputBox.Focus();
                return;
            }
            string extension = Path.GetExtension(output);
            if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                Warn("输出必须是 .exe 或 .dll 文件路径。");
                _outputBox.Focus();
                return;
            }
            if (Directory.Exists(output))
            {
                Warn("输出路径是一个目录，请给出具体的文件名。");
                return;
            }
            if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            {
                Warn("输出不能覆盖输入原件，请换一个文件名。");
                _outputBox.Focus();
                return;
            }
        }

        string worker = Path.Combine(Path.GetDirectoryName(cli) ?? "", "worker", "Unpacker.Worker.exe");
        if (!File.Exists(worker))
        {
            Warn("未找到 worker\\Unpacker.Worker.exe。\r\n\r\n请确认 UnpackerGui.exe 与 Unpacker-net48.exe、worker\\ 目录放在一起（使用完整发布包），或先用「复制命令」确认命令行程序位置。");
        }

        List<string> args = BuildArguments();
        _logBox.Clear();
        _jsonBox.Clear();
        _analyzerJson.Clear();
        _artifactList.Items.Clear();
        _runDirectory = null;
        _lastOutput = analyze ? null : output;
        _progressBar.Value = 0;
        _stageLabel.Text = "";
        _startedAt = DateTime.Now;

        AppendLog("$ " + Quote(cli) + " " + string.Join(" ", args.Select(Quote)), Color.FromArgb(70, 70, 70));
        SetRunning(true);

        var startInfo = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(cli) ?? Environment.CurrentDirectory,
        };
        startInfo.Arguments = string.Join(" ", args.Select(Quote));

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, ev) => { if (ev.Data != null) _pending.Enqueue((ev.Data, false)); };
        process.ErrorDataReceived += (_, ev) => { if (ev.Data != null) _pending.Enqueue((ev.Data, true)); };

        _process = process;
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _process = null;
            SetRunning(false);
            AppendLog("启动失败：" + ex.Message, Color.Firebrick);
            Warn("无法启动命令行程序：\r\n" + ex.Message);
            return;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _pumpTimer.Start();

        int exitCode;
        try
        {
            exitCode = await Task.Run(() =>
            {
                process.WaitForExit();
                return process.ExitCode;
            });
        }
        catch (Exception ex)
        {
            exitCode = -1;
            AppendLog("等待进程结束失败：" + ex.Message, Color.Firebrick);
        }

        if (_closing) return;

        _pumpTimer.Stop();
        Pump();
        process.Dispose();
        _process = null;
        SetRunning(false);

        TimeSpan elapsed = DateTime.Now - _startedAt;
        (string verdict, Color color) = exitCode switch
        {
            0 => ("完成", Color.FromArgb(0, 120, 60)),
            2 => ("部分完成", Color.FromArgb(180, 110, 0)),
            124 => ("超时", Color.FromArgb(180, 110, 0)),
            _ => ("失败", Color.Firebrick),
        };
        AppendLog($"—— 进程退出码 {exitCode}（{verdict}），用时 {elapsed.TotalSeconds:F1}s ——", color);
        _statusLabel.Text = $"{verdict} · 退出码 {exitCode} · 用时 {elapsed.TotalSeconds:F1}s";
        if (exitCode == 0) _progressBar.Value = 8;

        LoadResults(analyze);

        if (_exitWhenDone)
        {
            Environment.ExitCode = exitCode;
            Close();
        }
    }

    private void StopProcess(bool confirm)
    {
        Process? process = _process;
        if (process == null) return;
        if (confirm && MessageBox.Show(
                this,
                "停止会强制结束解包进程及其子进程，当前输出文件可能不完整。\r\n\r\n继续吗？",
                "停止运行",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        int id;
        try { id = process.Id; }
        catch (InvalidOperationException) { return; }

        AppendLog("正在终止进程树（PID " + id + "）…", Color.Firebrick);
        try
        {
            var killer = new ProcessStartInfo("taskkill", "/T /F /PID " + id)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var kill = Process.Start(killer);
            kill?.WaitForExit(8000);
        }
        catch (Exception ex)
        {
            AppendLog("终止失败：" + ex.Message, Color.Firebrick);
        }
    }

    private void SetRunning(bool running)
    {
        _startButton.Enabled = !running;
        _stopButton.Enabled = running;
        _inputBox.Enabled = !running;
        _inputBrowse.Enabled = !running;
        _unpackRadio.Enabled = !running;
        _analyzerRadio.Enabled = !running;
        _copyCommand.Enabled = !running;
        _openOutput.Enabled = !running;
        bool canOutput = !running && _unpackRadio.Checked;
        _outputBox.Enabled = canOutput;
        _outputBrowse.Enabled = canOutput;
        UseWaitCursor = running;
        if (running)
        {
            _statusLabel.Text = "运行中 · 已用 0s";
            _stageLabel.Text = "";
        }
    }

    // ───────────────────────────── 输出泵 ─────────────────────────────

    private void Pump()
    {
        if (_process != null)
        {
            TimeSpan elapsed = DateTime.Now - _startedAt;
            _statusLabel.Text = $"运行中 · 已用 {elapsed.TotalSeconds:F0}s";
        }

        if (_pending.IsEmpty) return;

        bool analyze = _analyzerRadio.Checked;
        var normal = new StringBuilder();
        var errors = new StringBuilder();
        bool sawError = false;

        while (_pending.TryDequeue(out (string Text, bool IsError) item))
        {
            TrackProgress(item.Text);
            if (item.IsError)
            {
                errors.AppendLine(item.Text);
                sawError = true;
            }
            else if (analyze && !ProgressPattern.IsMatch(item.Text))
            {
                // analyzer 的返回体就是缩进 JSON，直接收进结果页，不刷日志。
                _analyzerJson.AppendLine(item.Text);
            }
            else
            {
                normal.AppendLine(item.Text);
            }
        }

        if (normal.Length > 0) AppendLog(normal.ToString().TrimEnd('\r', '\n'), _logForeColor);
        if (sawError) AppendLog(errors.ToString().TrimEnd('\r', '\n'), Color.Firebrick);
    }

    private void TrackProgress(string text)
    {
        Match match = ProgressPattern.Match(text);
        if (!match.Success) return;

        if (int.TryParse(match.Groups[1].Value, out int stage) && stage >= 0 && stage <= 8)
        {
            if (_progressBar.Value != stage) _progressBar.Value = stage;
            string state = match.Groups[3].Value;
            _stageLabel.Text = $"阶段 {stage}/8 {match.Groups[2].Value} · {state}";
        }

        if (_runDirectory == null)
        {
            Match directory = RunDirectoryPattern.Match(match.Groups[4].Value);
            if (directory.Success) _runDirectory = directory.Groups[1].Value.Trim();
        }
    }

    private void AppendLog(string text, Color color)
    {
        if (text.Length == 0) return;
        if (_logBox.TextLength > 1_500_000)
        {
            _logBox.Select(0, _logBox.TextLength / 4);
            _logBox.SelectedText = string.Empty;
        }

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = color;
        _logBox.SelectionFont = _logFont;
        _logBox.AppendText(text + Environment.NewLine);
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    // ───────────────────────────── 结果 ─────────────────────────────

    private void LoadResults(bool analyze)
    {
        JObject? result = null;
        if (analyze)
        {
            string raw = _analyzerJson.ToString().Trim();
            if (raw.StartsWith("{", StringComparison.Ordinal))
            {
                try { result = JObject.Parse(raw); }
                catch (JsonException ex) { AppendLog("返回内容不是合法 JSON：" + ex.Message, Color.DarkOrange); }
            }
            if (result == null && raw.Length > 0) _jsonBox.Text = raw;
        }
        else if (_runDirectory != null)
        {
            string file = Path.Combine(_runDirectory, "result.json");
            if (File.Exists(file))
            {
                try { result = JObject.Parse(File.ReadAllText(file)); }
                catch (Exception ex) { AppendLog("读取 result.json 失败：" + ex.Message, Color.DarkOrange); }
            }
        }

        if (result != null) _jsonBox.Text = result.ToString(Formatting.Indented);

        var rows = new List<(string Name, string Value, bool IsPath)>();
        void AddPath(string name, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) rows.Add((name, path!, true));
        }
        void AddValue(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add((name, value!, false));
        }

        if (result != null)
        {
            AddPath("输入文件", result.Value<string>("Input"));
            AddValue("SHA-256", result.Value<string>("Sha256"));
            AddPath("输出程序", result.Value<string>("Output"));
            AddPath("运行配置", result.Value<string>("RuntimeConfig"));
            AddPath("源码工程", result.Value<string>("SourceProject"));
            AddPath("重编译产物", result.Value<string>("RebuiltAssembly"));
            AddPath("字符串索引", result.Value<string>("StringIndex"));
            AddPath("诊断目录", result.Value<string>("DiagnosticsDirectory"));
        }

        if (!analyze && _lastOutput != null)
        {
            if (!rows.Any(r => r.Name == "输出程序")) AddPath("输出程序", _lastOutput);
            if (File.Exists(_lastOutput + ".config") && !rows.Any(r => r.Name == "运行配置"))
                AddPath("运行配置", _lastOutput + ".config");
        }
        if (_runDirectory != null && !rows.Any(r => r.Name == "诊断目录")) AddPath("诊断目录", _runDirectory);

        _artifactList.BeginUpdate();
        _artifactList.Items.Clear();
        foreach ((string name, string value, bool isPath) in rows)
        {
            bool exists = isPath && (File.Exists(value) || Directory.Exists(value));
            var item = new ListViewItem(name)
            {
                ForeColor = !isPath || exists ? _logForeColor : Color.FromArgb(150, 150, 150),
            };
            item.SubItems.Add(value);
            item.SubItems.Add(isPath ? (exists ? "存在" : "缺失") : "校验值");
            if (exists) item.Tag = value;
            _artifactList.Items.Add(item);
        }
        _artifactList.EndUpdate();

        if (rows.Count == 0)
        {
            AppendLog("没有拿到可展示的结果路径；请看日志里的失败原因。", Color.DarkOrange);
        }
    }

    private void OpenSelectedArtifact()
    {
        if (_artifactList.SelectedItems.Count == 0) return;
        if (_artifactList.SelectedItems[0].Tag is string path) OpenPath(path);
    }

    private static void OpenPath(string path)
    {
        if (path.Length == 0) return;
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", Quote(path)) { UseShellExecute = true });
            }
            else if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "/select," + Quote(path)) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show("路径不存在：" + path, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开失败：" + ex.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void Warn(string message) => MessageBox.Show(this, message, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);

    // ───────────────────────────── 命令行程序定位 ─────────────────────────────

    private IEnumerable<string> CandidateClis()
    {
        string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrEmpty(_settings.CliPath) && File.Exists(_settings.CliPath)) yield return _settings.CliPath!;
        yield return Path.Combine(baseDirectory, "Unpacker-net48.exe");
        yield return Path.Combine(baseDirectory, "Unpacker.exe");
    }

    private string? ResolveCli(bool interactive)
    {
        foreach (string candidate in CandidateClis())
        {
            if (File.Exists(candidate)) return candidate;
        }
        if (!interactive) return null;

        using var dialog = new OpenFileDialog
        {
            Title = "选择 Unpacker 命令行程序",
            Filter = "Unpacker 命令行 (Unpacker*.exe)|Unpacker*.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (!string.IsNullOrEmpty(_settings.CliPath) && File.Exists(_settings.CliPath))
            dialog.InitialDirectory = Path.GetDirectoryName(_settings.CliPath);
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;

        _settings.CliPath = dialog.FileName;
        _settings.Save();
        return dialog.FileName;
    }

    // ───────────────────────────── 工具 ─────────────────────────────

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(QuoteTriggers) < 0) return value;
        var builder = new StringBuilder("\"");
        for (int i = 0; i < value.Length; i++)
        {
            char current = value[i];
            if (current == '\\')
            {
                int slashes = 1;
                while (i + 1 < value.Length && value[i + 1] == '\\')
                {
                    i++;
                    slashes++;
                }
                builder.Append('\\', i == value.Length - 1 ? slashes * 2 : slashes);
            }
            else if (current == '"')
            {
                builder.Append("\\\"");
            }
            else
            {
                builder.Append(current);
            }
        }
        builder.Append('"');
        return builder.ToString();
    }

    private sealed class GuiSettings
    {
        public string? CliPath { get; set; }
        public string? LastInputDirectory { get; set; }
        public string? LastOutputDirectory { get; set; }

        private static string FilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EazUnpackerGui",
            "settings.json");

        public static GuiSettings Load()
        {
            try
            {
                string path = FilePath;
                if (File.Exists(path))
                {
                    GuiSettings? loaded = JsonConvert.DeserializeObject<GuiSettings>(File.ReadAllText(path));
                    if (loaded != null) return loaded;
                }
            }
            catch
            {
                // 设置文件坏了不应该影响启动。
            }
            return new GuiSettings();
        }

        public void Save()
        {
            try
            {
                string path = FilePath;
                string? directory = Path.GetDirectoryName(path);
                if (directory != null) Directory.CreateDirectory(directory);
                File.WriteAllText(path, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch
            {
                // 只读目录下保存失败可以忽略。
            }
        }
    }
}
