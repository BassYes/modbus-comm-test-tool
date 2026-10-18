---
name: commtool-m3-architecture-decisions
description: 通讯测试工具 M3（v0.3.0）的关键架构决策与踩坑（多会话、插件宿主、网卡配置）
type: decision
---

**Why:** M3 引入了提权写操作、多会话重构与插件加载，这些决策如果被下一轮改动无意破坏，会直接触及安全边界或引入跨会话串扰；
同时有三个“看起来能用、实际会坏”的坑已经踩过。

**How to apply（决策与边界）:**
1. **网卡写入只走提权助手**（AD-08）：主程序保持普通权限；`Comm.Platform.Windows.AdapterConfigService` 只读后端 +
   `INetConfigHelper` → `Comm.NetConfigHelper.exe`（`requireAdministrator`）。助手只接受 `HelperCommands` 白名单命令，
   **绝不**执行 shell/子进程；空闲 5 分钟退出，但**回滚倒计时武装期间不退出**（主程序崩溃也要能回滚，规划 12.5）。
2. **DHCP 切换与 DNS 改写用 netsh（只看退出码）**，静态地址/网关/辅助 IP 用 IP Helper P/Invoke——因为 IP Helper 没有稳定的
   DHCP 开关 API，而 CIM 会引入额外程序集（离线环境不引入）。netsh 输出随系统语言变化，**永远不要解析它的文本**。
3. **会话隔离的粒度**（R-16）：`SessionContext` 每会话一份（传输/事务/统计/监视投影/监视表）；`SessionManager` 只是注册表，
   仍保留“等价于活动会话”的兼容转发成员（新代码请直接用 `SessionContext`）。**监视表的“暂停显示”是全局的**
   （共用同一显示缓冲与投递节拍），其余都按 `FrameRecord.SessionId` 隔离。
4. **串口独占在登记表与系统两层**：`SerialPortRegistry`（`serial:COMx`）先给可读提示；**串口打开失败必须释放登记**，
   否则该会话会永久“占用”一个从未打开的端口。
5. **插件共享契约不得随插件复制**：`Comm.Plugins.Host` 的 `PluginLoadContext` 对 `Comm.Core` /
   `Comm.Plugins.Abstractions` 返回 null 回落默认上下文，否则 `ICommPlugin` 类型标识不一致导致注册失败。
   示例插件工程对这两个引用设 `Private="false"`，`plugin.json` 必须 `CopyToOutputDirectory`（否则发布包缺清单、插件静默不加载）。
6. **插件故障隔离**：注册调用 5 s 超时 + 异常边界；连续失败 3 次自动禁用（PLG-7005）并把禁用状态与失败计数写入设置；
   ALC 不是安全沙箱，只加载 `plugins\` 受信目录。
7. **工作区文件 `.ctws`**（`WorkspaceFile`/`WorkspaceStore`）：原子写、损坏文件保留 `.bad-<时间戳>`、
   **高版本文件只读打开且拒绝覆盖**；导入支持替换/合并；运行期设置（日志/监视/扫描默认值）不受影响。
   监视点位是会话作用域的，导入时只保留与提示，不做一键导入。
8. **D-5（.resx 多语言）已关闭**：v1.0 只做中文界面，多语言推迟 v1.x（用户 2026-10-18 确认，记录于 `docs/m3-交付说明.md` 第 7 节）。
9. 版本号唯一来源仍是 `Directory.Build.props`（唯一例外 `Comm.Plugins.Abstractions` 单独版本号）；
   `tests/Comm.Architecture.Tests` 用 15 项断言守护依赖方向、WPF 隔离、sln 登记与版本来源，**改依赖方向必须同时更新白名单**。

**未验证项（不要当作已验证）:** 真实网卡提权写入与倒计时到期回滚、真实 WPF 窗口拆分/移回与多显示器混合 DPI、
干净机冒烟、真实硬件回归——步骤见 `docs/m3-交付说明.md` 第 6 节。

**Related:** [[commtool-build-test-workflow]]
