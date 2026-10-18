# 构建环境与自检说明（开发备忘）

## 1. 工具链

- 本机未安装 Visual Studio，`.NET SDK 8.0.426` 安装在用户目录：`C:\Users\Bass\.dotnet\dotnet.exe`（未写入 PATH，命令需写全路径）。
- 系统默认 `dotnet`（`C:\Program Files\dotnet`）只有 SDK 3.0.101，**不能**用于构建本仓库（要求 .NET 8）。

```bash
DOTNET=C:/Users/Bass/.dotnet/dotnet.exe
cd "E:/Download/DSH/通讯测试工具"
$DOTNET build CommTestTool.sln
$DOTNET run --project src/Comm.App.Wpf
```

## 2. 测试

| 方式 | 命令 | 说明 |
|---|---|---|
| 本地运行器（推荐） | `$DOTNET run --project tests/Comm.TestRunner` | 反射执行同一批 xunit 用例；可加筛选参数，如 `-- Reads_holding` |
| 标准 VSTest | `$DOTNET test CommTestTool.sln` | 在装有 VS/正常桌面环境时使用；本机受限环境下 testhost 会以 `Win32Exception (5)` 中止 |

新增测试项目后需把它加入 `tests/Comm.TestRunner/Program.cs` 的 `assemblies` 数组，本地运行器才会执行。
也可以临时运行未登记的测试程序集（并行开发自检用）：

```
$DOTNET run --project tests/Comm.TestRunner -- --assembly=tests/<项目>/bin/Debug/net8.0/<项目>.dll [--filter=名称片段]
```

M3 交付后登记的程序集：`Comm.Protocols.Modbus.Tests`、`Comm.Session.Tests`、`Comm.Scanning.Tests`、
`Comm.Network.Tests`、`Comm.Infrastructure.Tests`、`Comm.Platform.Windows.Tests`、`Comm.NetConfigHelper.Tests`、
`Comm.Plugins.Host.Tests`、`Comm.Architecture.Tests`、`Comm.Security.Tests`（共 442 个用例，含安全回归 SR-1~SR-7）。

## 2.1 多会话验收探针（M3b）

```
$DOTNET run --project tools/Comm.SessionProbe          # 23 项，全部通过则退出码 0
$DOTNET run --project tools/Comm.SessionProbe -- --verbose
```

探针使用**进程内 Modbus TCP 从站模拟器 + 回环地址**，不打开真实串口、不改动任何系统配置。

## 2.2 性能与稳定性探针（M4）

```
$DOTNET run --project tools/Comm.StressProbe -- --seconds 60 --frames 200000 --report docs/stability-probe-report.md
$DOTNET run --project tools/Comm.StressProbe -- --quick        # 5 s / 2 万条，提交前快速自查
powershell -File tools/Invoke-SoakTest.ps1 -Quick              # 5 分钟稳定性冒烟
powershell -File tools/Invoke-SoakTest.ps1                     # 24 h 长跑（建议在目标机执行）
```

- 退出码 0 = 全部阈值通过；1 = 有阈值未通过（可直接作为发布门禁）。
- `--json <路径>` 输出机器可读摘要（长跑脚本用它取内存与结论，**不解析中文控制台输出**）。
- 注意：不要用系统 TEMP 存放探针产物——本机受限环境下子进程对 TEMP 无写权限，脚本统一写 `artifacts\soak`。

## 3. 界面自检

- `CommTestTool.exe --page=N`：启动时直接打开第 N 个功能页
  （会话页：1=连接中心、2=设备扫描、**3=Unit ID 扫描**、4=数据扫描、5=寄存器定位、6=监视表、7=报文工具、8=报文监视；
  全局页：9=网络工具、10=局域网扫描、11=扩展中心、12=设置与日志。M3b 起列表随会话工作区动态生成）。
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
| `data/netsnap/*.json` | 网卡配置快照（M3a：应用前自动保存，最多 20 份，可一键恢复） |
| `<程序目录>/exports/` | 导出文件默认目录 |
| `<程序目录>/plugins/` | **插件受信目录**（M3b：只加载这里的插件，见 AD-13） |
| `<程序目录>/helper/` | **网卡配置提权助手**（M3a：`Comm.NetConfigHelper.exe`，按需 UAC 拉起） |

强制结束进程后请删除 `data/.running`，否则下次启动会弹出“上次运行异常退出”提示（这是预期行为，不是缺陷）。

## 4.1 版本号与构建信息（X-9）

- 版本号只改 `Directory.Build.props` 的 `<Version>`；`AssemblyVersion` / `FileVersion` / `ProductVersion` 与界面标题、关于行都跟着变。
- 构建时间（UTC）与 Git 提交号通过 `AssemblyMetadata` 注入，可在设置页“运行环境”看到。
- 若在非 Git 环境构建，提交号显示为 `unknown`（不影响构建）。

## 4.2 发布（X-1）

```powershell
.\tools\Publish-App.ps1                               # Release + 自包含 win-x64 → artifacts\publish
.\tools\Publish-App.ps1 -Sign -Zip                    # 追加签名与便携包
.\tools\Publish-App.ps1 -WithSamplePlugin             # 额外把参考示例插件装进 plugins\Sample
```

M3 起发布脚本会额外产出：

```
artifacts\publish\
  CommTestTool.exe                 主程序（普通权限）
  helper\Comm.NetConfigHelper.exe 网卡配置提权助手（UAC，必须与主程序一起签名）
  plugins\                        插件受信目录（含“插件目录说明.txt”）
```

### 4.3 安装包（M4）

```powershell
.\tools\Build-Installer.ps1                       # 无 Inno Setup 时：回退生成“便携安装包 zip”
.\tools\Build-Installer.ps1 -UseInno -Sign        # 有 Inno Setup 6 时：正式签名安装包
.\tools\Build-Installer.ps1 -InnoCompiler 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

- 产物在 `artifacts\installer\`。安装版把程序装到 `Program Files`、用户数据在 `%APPDATA%\通讯测试工具\`（`AppPaths` 已实现回退）。
- 安装出来的文件**不带“来自网络”标记**，这是消除启动提示最有效的一条渠道（规划 22.5.2 L2）。

**Inno Setup 相关注意**：

- 默认按顺序自动探测 `C:\Program Files (x86)\Inno Setup 6\ISCC.exe` → `C:\Program Files\Inno Setup 6\ISCC.exe` → `tools\_build\inno\ISCC.exe`（便携版），都不在时回退“便携安装包 zip”。
- 简体中文语言包**随仓库提供**（`tools\ChineseSimplified.isl`），`installer.iss` 用 `{#SourcePath}\ChineseSimplified.isl` 引用，因此换机器不需要手动往 Inno 目录里拷语言文件。
- Inno Setup 的安装程序本身在 **系统 TEMP 不可写**时会静默失败（退出码 1、连日志都不写）。如果目标机是受限环境，先把 `TEMP`/`TMP` 指到可写目录再安装 Inno，或用 `/PORTABLE=1 /VERYSILENT /DIR=<可写目录>` 解压出便携版 ISCC。
- 首次真编译时踩到过两处脚本语法问题，已修：文件头注释必须用 `;`（`#` 是 Inno 预处理指令）、`Excludes` 必须与 `Source` 写在同一条 `[Files]` 条目里。

**不要把 `bin/Debug` 直接拷到现场**：里面有 pdb 与中间文件，且经网络/U 盘搬运后容易带上 MOTW 标记而触发安全警告。

## 5. 已知环境相关注意事项

- 若构建时报“文件被 CommTestTool 占用”，先结束正在运行的实例（`taskkill /IM CommTestTool.exe /F`）。
- 高 DPI 缩放下窗口按 DIP 布局；M2.5 起主窗口最小尺寸为 **900×480 DIP**，10 个功能页在最小尺寸下均可滚动到底（B-01）。
- 字体回退链在 `Styles/Theme.xaml`：`UiFont`（微软雅黑 UI → Segoe UI → SimSun）、`MonoFont`（Consolas → Cascadia Mono → Courier New）。
- 若需要验证“程序目录不可写”的回退路径，可把发布目录放到 `C:\Program Files\<名称>\` 下运行，然后在设置页确认数据目录已变为 `%LOCALAPPDATA%\通讯测试工具\data`。
