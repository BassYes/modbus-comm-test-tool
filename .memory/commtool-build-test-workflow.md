---
name: commtool-build-test-workflow
description: 通讯测试工具（E:\Download\DSH\通讯测试工具）的构建、测试与自检命令
type: reference
---

**Why:** 这台机器上 `dotnet` 的默认实例是 3.0，`dotnet test` 的 testhost 会因权限被中止（Win32Exception 5），
必须用特定路径的 SDK 与仓库自带的反射运行器，否则会误判“测试全挂/构建失败”。

**How to apply:**
- SDK：只用 `C:/Users/Bass/.dotnet/dotnet.exe`（8.0.x）。`dotnet build CommTestTool.sln -v q --nologo` 应 **0 错误 0 警告**。
- 测试：`C:/Users/Bass/.dotnet/dotnet.exe run --project tests/Comm.TestRunner`（反射跑同一批 xunit 用例）。
  新增测试项目要登记到 `tests/Comm.TestRunner/Program.cs` 的 `assemblies` 数组。
- 临时跑未登记的测试程序集：`... -- run --project tests/Comm.TestRunner -- --assembly=<dll 路径> [--filter=名称片段]`
  （TestRunner 已支持 `--assembly=`，并用 `AssemblyLoadContext.Resolving` 探测该 dll 所在目录的依赖）。
- 多会话验收：`dotnet run --project tools/Comm.SessionProbe`（23 项，进程内 Modbus TCP 模拟器，不开真实串口）。
- 发布：`powershell -File tools/Publish-App.ps1 [-Sign -Zip] [-WithSamplePlugin]` → `artifacts\publish`
  （含 `helper\Comm.NetConfigHelper.exe` 与 `plugins\`）。
- 界面自检：`CommTestTool.exe --page=N`（1–7 会话页，8 网络工具、9 局域网扫描、10 扩展中心、11 设置与日志）；
  `bash docs/capture-pages.sh [--min] [--pages 8,9,10,11]`。
- **沙箱限制**：子进程（dotnet 测试、探针）**不能写系统 TEMP**（`C:\Users\Bass\AppData\Local\Temp` 会 UnauthorizedAccessException），
  测试里要写临时文件时用 `Path.Combine(AppContext.BaseDirectory, ...)`（见 FrameFileWriterTests / PluginTestFixtures）。
- 运行程序前若报“文件被占用”，先 `taskkill /IM CommTestTool.exe /F`；强杀后删掉 `data/.running` 再启动。

**Related:** [[commtool-m3-architecture-decisions]]、[[taskflow-model-roles-missing]]
