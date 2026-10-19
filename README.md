# EazUnpacker

![Supported Eazfuscator.NET versions: 2021.1–2026.2](https://img.shields.io/badge/Eazfuscator.NET-2021.1%E2%80%932026.2-2563eb)

A command-line unpacker and deobfuscator for Eazfuscator.NET assemblies, with IL recovery and C# source export.

面向 Eazfuscator.NET 程序集的命令行解包与去混淆工具，支持 IL 恢复和 C# 源码导出。

[English](#english) · [简体中文](#简体中文) · [Before / After · 效果对比](#before--after--效果对比)

## Before / After · 效果对比

### 1. Entry-point cleanup · 入口方法整理

**Before · 之前（图 1）**

![Entry point before processing: obfuscated identifiers and constant conditional branches / 处理前的入口方法：混淆标识符和恒定条件分支](src/assets/screenshots/entry-point-before.png)

**After · 之后（图 2）**

![Entry point after processing: readable identifiers, decoded strings and simplified branches / 处理后的入口方法：可读标识符、还原字符串和简化分支](src/assets/screenshots/entry-point-after.png)

### 2. String recovery and control-flow cleanup · 字符串恢复与控制流整理

**Before · 之前（图 3）**

![Method before processing: string decoder calls and redundant conditional branches / 处理前的方法：字符串解码调用和冗余条件分支](src/assets/screenshots/method-before.png)

**After · 之后（图 4）**

![Method after processing: visible string literals and simplified control flow / 处理后的方法：可见的字符串字面量和简化的控制流](src/assets/screenshots/method-after.png)

## English

### Supported versions

**Eazfuscator.NET 2021.1–2026.2**, inclusive.

### Features

- Assembly unpacking and recovery of strings, resources and embedded assemblies.
- Control-flow cleanup and devirtualization of supported VM methods.
- Readable identifier generation and C# project export with a rebuild step.
- JSON analysis output and recovery progress in the CLI.

Generated names improve readability; they do not recover the author's original names when original symbols are unavailable. Support depends on the protections and assembly being processed.

### Notes on 2026.2

- The string decrypter of 2026.2 exposes `string(int)` only; the `string(int, bool)` overload is no longer emitted. Both shapes are detected, and the strict shape still wins when present.
- Obfuscated identifiers written as pseudo-Unicode escapes (`u0006_u2004_u200B`) or containing zero-width characters are treated as generated names and renamed.
- Dependencies are probed in subdirectories of the input, so layouts such as `Components\Vendor\Gapotchenko.FX.dll` resolve during the string, post-VM and CLR validation stages.
- Enum types whose members were stripped by the obfuscator regain `Value<n>` literal members, derived from the constants actually used in field stores, arguments, returns and comparisons. Original member names cannot be recovered. Because these members are added, `ToString()` and `Enum.GetNames()` return the generated names for values that previously had no name at all.

### Build

Requires Windows, .NET Framework 4.8, and a .NET SDK with `.slnx` support (9.0.200 or newer). The first build restores NuGet dependencies.

```powershell
dotnet build Unpacker.slnx -c Release
# Also build the optional modern .NET backend
dotnet build src/ModernWorker/ModernWorker.csproj -c Release
```

You can also open `Unpacker.slnx`. Output is written to `src\bin\Release\net48`; distribute the entire output directory, including its worker processes. If using the optional modern backend, copy the contents of `src\ModernWorker\bin\Release\net8.0-windows` into `src\bin\Release\net48\worker\modern`. This backend requires the .NET 8 Desktop Runtime.

### Usage

```powershell
.\src\bin\Release\net48\Unpacker.exe unpack "C:\input.exe" "C:\output.exe"
.\src\bin\Release\net48\Unpacker.exe analyzer "C:\input.exe"
```

For .NET Framework EXE output, deploy the generated `.config` alongside the executable. Exported source, logs and diagnostics are stored in `%LOCALAPPDATA%\Unpacker\work`. The `analyzer` command returns JSON that includes the generated project location when available.

Any available recovered assembly is published to the requested output path, including partial recovery results. Incomplete VM recovery, CLR checks, runtime configuration or source rebuilding do not discard an already generated output; diagnostics retain the incomplete status.

Exit codes: `0` for completion of the identified stages, `2` for incomplete recovery or source rebuilding, and `1` for input, command or processing errors.

### Graphical interface

`UnpackerGui.exe` (keep it in the same folder as `Unpacker-net48.exe`) is a thin WinForms front-end for the same tool. It targets .NET Framework 4.8, so it needs no extra runtime.

- Pick the input/output with file dialogs, or drag a `.exe`/`.dll` onto the window — or onto `UnpackerGui.exe` itself.
- The output path is derived from the input (`name.unpacked.dll`); edit it or choose another file.
- `运行日志` streams the live stage/state progress; `返回信息` lists the published output, `.config`, exported C# project, string index and diagnostics directory, and `原始 JSON` holds the full result. Double-click a row to open it.
- `复制命令` copies the equivalent command line, so anything you set up in the GUI can be replayed in a terminal.

The GUI drives the CLI as a child process, so behaviour and exit codes are identical to the command line. For scripting, it also accepts arguments:

```powershell
.\UnpackerGui.exe --analyzer --run --exit-when-done "C:\input.exe"
.\UnpackerGui.exe --run --exit-when-done "C:\input.exe" "C:\output.exe"
```

`--analyzer` selects analyzer mode, `--run` starts immediately, and `--exit-when-done` closes the window when the run finishes and returns the CLI exit code.

### Source layout

| Path | Purpose |
| --- | --- |
| `src/` | Main program and project file |
| `src/Gui/` | Optional WinForms front-end (`UnpackerGui.exe`) |
| `src/ThirdParty/` | Required EazFixer, EazyDevirt and Echo source dependencies |
| `src/WorkerHost/` | Required .NET Framework worker process |
| `src/ModernWorker/` | Optional .NET 8 worker for modern .NET assemblies |
| `src/tests/` | Regression scripts and behavior validation tools |
| `src/assets/screenshots/` | Before-and-after screenshots used in this README |

### Testing

```powershell
# Built-in tests; no external samples required
.\src\bin\Release\net48\worker\Unpacker.Worker.exe --self-test
# Regression tests using a prepared sample directory
.\src\tests\Regression.ps1 -SampleRoot "C:\samples"
```

### License

The combined project is licensed under GPL-3.0; see [LICENSE](LICENSE). Third-party components retain their own licenses. Sources and attribution are listed in [NOTICE](NOTICE).

## 简体中文

### 支持版本

**Eazfuscator.NET 2021.1 至 2026.2**，包含起止版本。

### 功能

- 程序集解包，恢复字符串、资源及内嵌程序集。
- 整理控制流，对受支持的 VM 方法进行反虚拟化。
- 生成可读标识符，导出 C# 工程并执行重构建。
- 通过命令行显示处理进度，支持 JSON 分析输出。

缺少原始符号时，生成的名称用于改善可读性，不等于恢复了原作者命名。具体支持情况取决于目标程序集及其保护方式。

### 2026.2 说明

- 2026.2 的字符串解密器只保留 `string(int)`，不再生成 `string(int, bool)` 重载。两种形态都能识别；同时存在时仍优先严格形态。
- 以伪 Unicode 转义（`u0006_u2004_u200B`）书写或含零宽字符的标识符按生成名处理，会被改名。
- 依赖会在输入目录的子目录里探测，`Components\Vendor\Gapotchenko.FX.dll` 这类布局在字符串恢复、VM 后复处理与 CLR 校验阶段都能正常解析。
- 成员被混淆器删光的枚举会按 IL 里实际使用的常量补回 `Value<n>` 成员（来源：字段赋值、调用参数、返回值与比较）。原成员名无法恢复；由于补回了成员，`ToString()` 与 `Enum.GetNames()` 对这些原本没有名字的取值会返回生成的名字。

### 构建

需要 Windows、.NET Framework 4.8 和支持 `.slnx` 的 .NET SDK（9.0.200 或更新版本）。首次构建会还原 NuGet 依赖。

```powershell
dotnet build Unpacker.slnx -c Release
# 同时构建可选的现代 .NET 后端
dotnet build src/ModernWorker/ModernWorker.csproj -c Release
```

也可打开 `Unpacker.slnx`。输出位于 `src\bin\Release\net48`，分发时保留整个输出目录及工作进程。使用可选现代后端时，将 `src\ModernWorker\bin\Release\net8.0-windows` 内的文件复制到 `src\bin\Release\net48\worker\modern`；该后端需要 .NET 8 桌面运行时。

### 使用

```powershell
.\src\bin\Release\net48\Unpacker.exe unpack "C:\input.exe" "C:\output.exe"
.\src\bin\Release\net48\Unpacker.exe analyzer "C:\input.exe"
```

传统 .NET Framework EXE 输出包含必需的 `.config`，应与程序一起部署。导出的源码、日志与诊断文件保存在 `%LOCALAPPDATA%\Unpacker\work`。`analyzer` 输出 JSON，在生成工程可用时提供其位置。

后台只要生成了恢复程序集，就会发布到指定输出路径，包括部分恢复结果。VM 还原、CLR 检查、运行配置或源码重建未完成时，仍保留已生成的输出，并在诊断中记录未完成状态。

退出码：`0` 表示已识别阶段完成，`2` 表示恢复或源码重建不完整，`1` 表示输入、命令或处理错误。

### 图形界面

`UnpackerGui.exe`（与 `Unpacker-net48.exe` 放在同一目录）是同一套工具的精简 WinForms 前端，目标框架同为 .NET Framework 4.8，无需额外运行时。

- 用文件对话框选择输入/输出，或直接把 `.exe`/`.dll` 拖进窗口——也可以拖到 `UnpackerGui.exe` 图标上。
- 输出路径会按输入自动推导（`原名.unpacked.dll`），可手动修改或另选文件。
- `运行日志` 实时显示阶段/状态进度；`返回信息` 列出输出程序、`.config`、导出的 C# 工程、字符串索引和诊断目录，`原始 JSON` 保存完整结果，双击某行即可打开对应位置。
- `复制命令` 复制等价的命令行，界面上选好的参数可以直接拿到终端里重放。

界面把命令行程序作为子进程驱动，行为与退出码和命令行完全一致。需要脚本化时也可以带参数启动：

```powershell
.\UnpackerGui.exe --analyzer --run --exit-when-done "C:\input.exe"
.\UnpackerGui.exe --run --exit-when-done "C:\input.exe" "C:\output.exe"
```

`--analyzer` 切到分析模式，`--run` 打开后立即开始，`--exit-when-done` 跑完自动关窗并返回命令行程序的退出码。

### 源码结构

| 路径 | 用途 |
| --- | --- |
| `src/` | 主程序源码和项目文件 |
| `src/Gui/` | 可选的 WinForms 前端（`UnpackerGui.exe`） |
| `src/ThirdParty/` | 编译必需的 EazFixer、EazyDevirt 与 Echo 源码依赖 |
| `src/WorkerHost/` | 主程序必需的 .NET Framework 工作进程 |
| `src/ModernWorker/` | 处理现代 .NET 程序集的可选 .NET 8 工作进程 |
| `src/tests/` | 回归脚本和行为验证工具 |
| `src/assets/screenshots/` | README 使用的处理前后效果图 |

### 测试

```powershell
# 无需外部样本的内置测试
.\src\bin\Release\net48\worker\Unpacker.Worker.exe --self-test
# 使用准备好的样本目录运行回归测试
.\src\tests\Regression.ps1 -SampleRoot "C:\samples"
```

### 许可证

组合项目采用 GPL-3.0，见 [LICENSE](LICENSE)。第三方源码保留各自许可证，来源和署名见 [NOTICE](NOTICE)。
