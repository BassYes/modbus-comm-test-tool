---
name: commtool-m4-delivery-notes
description: 通讯测试工具 v0.4.0（M4）交付要点：探针/安装包/验收状态与遗留阻塞项
type: reference
---

**Why:** M4 引入了三个新的“门禁型”工具（性能探针、长跑脚本、安全回归）与安装包渠道。它们的用法与已知坑、
以及“哪些验收项尚未真正完成”，如果不在下次接手时明确，很容易把“软件侧通过”误当成“已验收”。

**How to apply（命令）:**
- 性能探针（退出码即门禁）：`dotnet run --project tools/Comm.StressProbe -- --seconds 60 --frames 200000 --report docs/stability-probe-report.md`
  加 `--json <路径>` 输出机器可读摘要（长跑脚本靠它取内存/结论，**不要解析中文控制台输出**）。
- 长跑：`powershell -File tools/Invoke-SoakTest.ps1 [-Quick]`（Quick=5 分钟）→ `docs/stability-report.md`（已 gitignore）。
- 安装包：`powershell -File tools/Build-Installer.ps1 [-UseInno] [-Sign]`；本机无 Inno Setup 时自动回退“便携安装包 zip”。
- 安全回归：`dotnet run --project tests/Comm.TestRunner`（内含 `Comm.Security.Tests`，SR-1~SR-7）。

**已知坑（都踩过）:**
1. PowerShell 5.1 脚本含中文**必须存成 UTF-8 with BOM**，否则乱码导致解析错误。
2. 子进程写系统 TEMP 会 UnauthorizedAccessException；脚本/测试统一用 `artifacts\soak` 或 `AppContext.BaseDirectory`。
3. PowerShell 捕获子进程输出用 OEM 代码页 → 中文匹配失败；改为让探针输出 JSON。
4. `FrameExporter.ExportCsv` 成功时返回 `string.Empty`（不是 null），判空要用 `IsNullOrEmpty`。
5. 性能数字要看“跨轮漂移”而不是单轮绝对值（探针每轮新建 20 万条显示缓冲，单轮 ~260 MB 正常）。

**M4 修复的真实缺陷（不要回退）:** ①事务层写请求必须 P0 交互优先级（否则扫描/周期队列能发写）；
②队列用 SemaphoreSlim 唤醒替代 5 ms 轮询；③`WrittenCount` 语义按条数累加；④LoopbackChannel delay=0 不再抛异常。

**未完成的验收（阻塞 v1.0）:** 正式代码签名证书（Q-1）、24 h 真实硬件长跑、1000 帧/s 下界面可操作性、
Inno 正式安装包 + 干净机安装、网卡提权写入与倒计时回滚（隔离虚拟机）、硬件在环矩阵、DPI 150%/200%。
逐项状态见 `docs/v1.0-验收清单.md`。

**Related:** [[commtool-m3-architecture-decisions]]、[[commtool-build-test-workflow]]
