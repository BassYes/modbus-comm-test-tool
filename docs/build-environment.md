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

- `CommTestTool.exe --page=N`：启动时直接打开第 N 个功能页（1=连接中心 … 10=设置与日志）。
- `bash docs/capture-pages.sh`：依次打开各页并截图到 `docs/`（脚本会强杀进程，之后自动删除 `data/.running`）。
- `bash docs/capture-pages.sh --min`：把窗口改为 **900×480**（最小可用尺寸）后逐页截图，用于 B-01 验收。
- `bash docs/capture-pages.sh --pages 2,8`：只截指定页。
- 100% / 150% / 200% DPI 需手工切换系统缩放后再运行上述命令（脚本内无法切换会话缩放）。

## 4. 运行时文件

M2.5（R-21 X-2）起，**数据目录由 `Comm.Diagnostics.AppPaths` 统一解析**：

1. 程序目录可写 → 程序同级 `data/`（便携形态，下面的表都相对它）；
2. 程序目录不可写（`Program Files`、只读 U 盘）→ `%LOCALAPPDATA%\通讯测试工具\data`；
3. 两者都不可写 → 系统临时目录，并在日志与设置页给出提示。

程序目录存在 `portable.flag` 时表示“明确要求便携”；设置页可一键创建，也可在便携↔用户目录间迁移设置。
**实际使用的目录与回退原因显示在“设置与日志”页与启动日志里。**

| 路径（相对数据目录） | 内容 |
|---|---|
| `settings.json` | 用户设置（原子写入；损坏时保留 `.bad-时间戳` 备份并用默认值启动） |
| `.running` | 运行标志；存在表示上次未正常退出，下次启动会提示 |
| `captures/` | 报文记录（JSONL） |
| `logs/debug-*.log` | Debug 日志（默认关闭时不产生文件） |
| `logs/crash-*.log` | 崩溃报告（不受日志开关控制，可单独关闭） |
| `<程序目录>/exports/` | 导出文件默认目录 |

强制结束进程后请删除 `data/.running`，否则下次启动会弹出“上次运行异常退出”提示（这是预期行为，不是缺陷）。

## 4.1 版本号与构建信息（X-9）

- 版本号只改 `Directory.Build.props` 的 `<Version>`；`AssemblyVersion` / `FileVersion` / `ProductVersion` 与界面标题、关于行都跟着变。
- 构建时间（UTC）与 Git 提交号通过 `AssemblyMetadata` 注入，可在设置页“运行环境”看到。
- 若在非 Git 环境构建，提交号显示为 `unknown`（不影响构建）。

## 4.2 发布（X-1）

```powershell
.	ools\Publish-App.ps1                 # Release + 自包含 win-x64 → artifacts\publish
.	ools\Publish-App.ps1 -Sign -Zip      # 追加签名与便携包
```

**不要把 `bin/Debug` 直接拷到现场**：里面有 pdb 与中间文件，且经网络/U 盘搬运后容易带上 MOTW 标记而触发安全警告。

## 5. 已知环境相关注意事项

- 若构建时报“文件被 CommTestTool 占用”，先结束正在运行的实例（`taskkill /IM CommTestTool.exe /F`）。
- 高 DPI 缩放下窗口按 DIP 布局；M2.5 起主窗口最小尺寸为 **900×480 DIP**，10 个功能页在最小尺寸下均可滚动到底（B-01）。
- 字体回退链在 `Styles/Theme.xaml`：`UiFont`（微软雅黑 UI → Segoe UI → SimSun）、`MonoFont`（Consolas → Cascadia Mono → Courier New）。
- 若需要验证“程序目录不可写”的回退路径，可把发布目录放到 `C:\Program Files\<名称>\` 下运行，然后在设置页确认数据目录已变为 `%LOCALAPPDATA%\通讯测试工具\data`。
