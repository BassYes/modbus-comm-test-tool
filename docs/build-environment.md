# 构建环境与自检说明（开发备忘）

## 1. 工具链

- 本机未安装 Visual Studio，`.NET SDK 8.0.426` 安装在用户目录：`C:\Users\Bass\.dotnet\dotnet.exe`（未写入 PATH，命令需写全路径）。
- 系统默认 `dotnet`（`C:\Program Files\dotnet`）只有 SDK 3.0.101，**不能**用于构建本仓库（要求 .NET 8）。

```bash
DOTNET=C:/Users/Bass/.dotnet/dotnet.exe
cd "E:/Download/DSH/通讯测试工具-v0.1"
$DOTNET build CommTestTool.sln
$DOTNET run --project src/Comm.App.Wpf
```

## 2. 测试

| 方式 | 命令 | 说明 |
|---|---|---|
| 本地运行器（推荐） | `$DOTNET run --project tests/Comm.TestRunner` | 反射执行同一批 xunit 用例；可加筛选参数，如 `-- Reads_holding` |
| 标准 VSTest | `$DOTNET test CommTestTool.sln` | 在装有 VS/正常桌面环境时使用；本机受限环境下 testhost 会以 `Win32Exception (5)` 中止 |

新增测试项目后需把它加入 `tests/Comm.TestRunner/Program.cs` 的 `assemblies` 数组，本地运行器才会执行。

## 3. 界面自检

- `CommTestTool.exe --page=N`：启动时直接打开第 N 个功能页（1=连接中心 … 7=设置与日志）。
- `bash docs/capture-pages.sh`：依次打开各页并截图到 `docs/`（脚本会强杀进程，之后自动删除 `data/.running`）。

## 4. 运行时文件

| 路径（相对 exe 目录） | 内容 |
|---|---|
| `data/settings.json` | 用户设置（原子写入；损坏时保留 `.bad-时间戳` 备份并用默认值启动） |
| `data/.running` | 运行标志；存在表示上次未正常退出，下次启动会提示 |
| `data/captures/` | 报文记录（JSONL） |
| `logs/` | Debug 日志（默认关闭时不产生文件） |
| `logs/crash-*.log` | 崩溃报告（不受日志开关控制，可单独关闭） |

强制结束进程后请删除 `data/.running`，否则下次启动会弹出“上次运行异常退出”提示（这是预期行为，不是缺陷）。

## 5. 已知环境相关注意事项

- 若构建时报“文件被 CommTestTool 占用”，先结束正在运行的实例（`taskkill /IM CommTestTool.exe /F`）。
- 高 DPI 缩放下窗口按 DIP 布局：报文工具页与连接中心页使用固定内容宽度 + 整页滚动，窄窗口时横向滚动即可访问第三列。
