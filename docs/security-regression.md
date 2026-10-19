# 安全与高风险回归清单（SR-1 ~ SR-7）

> 规划依据：16.3.9（每次发布必跑）、15.7（安全类错误）、AD-08（权限隔离）、AD-13（插件受信目录）。
> 自动化部分：`tests/Comm.Security.Tests`（`SecurityRegressionTests`，每条用例的方法名前缀标注 SR 编号）。
> 本文件是同一张表的**文档版本**，用于发布前逐项签字，并记录无法自动验证、需要人工执行的步骤。

运行自动化部分：

```bash
DOTNET=%USERPROFILE%/.dotnet/dotnet.exe
$DOTNET run --project tests/Comm.TestRunner -- --assembly=tests/Comm.Security.Tests/bin/Debug/net8.0/Comm.Security.Tests.dll
```

---

## SR-1 全部扫描功能不得产生写功能码

| 扫描功能 | 自动化用例 | 断言语义 |
|---|---|---|
| 串口设备扫描（自动模板） | `SR1_Device_scan_uses_only_read_function_codes` | 抓取模拟器收到的全部请求，逐条断言 `Fn.IsRead` 为真、无任何写功能码 |
| 串口设备扫描（手动模式选写功能码） | `SR1_Device_scan_in_manual_mode_refuses_write_templates` | 参数校验返回 `VAL-4006`；绕过界面直接调服务抛 `CommException`；**模拟器一条请求都收不到** |
| TCP Unit ID 扫描（R-12） | `SR1_Unit_id_scan_uses_only_read_function_codes_and_never_broadcasts` | 只读功能码；请求中不出现 Unit ID 0；`StartUnitId=0` 被参数校验拒绝 |
| 数据区扫描 | `SR1_Data_scan_uses_only_read_function_codes` | 只读功能码 |
| 寄存器定位 | `SR1_Register_locator_uses_only_read_function_codes` | 只读功能码（一次采样、多次解释） |
| 监视表周期刷新 | `SR1_Watch_table_refresh_uses_only_read_function_codes` | `Periodic` 优先级请求仍为只读 |
| 参数扫描阶段 A（R-11） | `SR1_Parameter_scan_stage_a_uses_only_read_function_codes` | 只读 03 |

**人工补充**：现场用串口监听/抓包工具对每种扫描各抓一次报文，确认没有 `05/06/0F/10`。适用于真实适配器（硬件在环，见 `docs/hardware-compatibility-matrix-v1.md`）。

## SR-2 写请求发送前必须确认；周期/循环写再次确认

| 检查点 | 自动化用例 | 说明 |
|---|---|---|
| 未确认的写请求被拒且不发出 | `SR2_Write_request_without_confirmation_is_rejected_and_nothing_is_sent` | 返回 `VAL-4004`，模拟器无请求，TX 计数为 0 |
| 扫描/周期队列不允许写（M4 新增守卫） | `SR2_Write_request_from_periodic_or_scan_priority_is_rejected` | 返回 `VAL-4006`；**该用例在 M4 发现并修复了一个真实缺陷**：原先只校验“已确认”，未校验优先级，导致扫描/周期队列可以发出已确认的写请求 |
| 手动发送页面拒绝未确认写帧 | `SR2_Manual_send_refuses_write_frames_without_confirmation` | 手动报文页的守卫 |
| 读帧不要求写确认 | `SR2_Read_frames_do_not_require_write_confirmation` | 避免误报，保证正常调试不被无关确认打断 |

**人工补充**：界面手工发送一次写线圈与一次写寄存器，确认弹窗显示目标/地址/原值/新值；循环发送写请求时再弹一次（界面已实现，需人工点一遍）。

## SR-3 广播写专项确认

| 检查点 | 自动化用例 |
|---|---|
| 广播写只做普通确认仍被拒（`VAL-4005`），专项确认后才发出 | `SR3_Broadcast_write_requires_dedicated_confirmation` |

**人工补充**：确认扫描与自动任务**不会**把广播地址 0 作为探测地址（已在 SR-1 的 Unit ID 用例中断言）。

## SR-4 网卡配置不确认保留时可回滚；回滚后与快照一致

| 检查点 | 自动化用例 | 说明 |
|---|---|---|
| 应用失败/回读不一致 → 快照保留、可回滚 | `SR4_Adapter_apply_failure_rolls_back_to_the_snapshot` | 助手报 `PLT-5005`；快照 `Kept=false`；`Restore` 走 `restore` 命令；回滚后网卡地址与快照一致 |
| 助手缺失时阻止写入且不留快照 | `SR4_Adapter_write_without_helper_is_blocked_and_leaves_no_snapshot` | `PLT-5007`；助手 0 条命令；无快照残留；网卡未被改动 |
| 非法输入在任何副作用之前被拒 | `SR4_Invalid_input_is_rejected_before_any_snapshot_or_helper_call` | 网络地址（`192.168.1.0/24`）→ `VAL-4009`；无快照、无命令 |

**人工补充（必须在隔离虚拟机执行，不能用开发机网卡）**：
1. 应用静态地址 → 勾选倒计时 60 s → 不点“保留” → 等 60 s，确认自动恢复到修改前配置。
2. 重复上一次并点“保留”，确认不再回滚。
3. 应用配置后直接结束主程序进程，确认助手仍按倒计时回滚（由助手持有计时，规划 12.5）。
4. 在拒绝 UAC 的情况下点“应用配置”，确认给出可读提示、不重复弹窗、网卡未变。

## SR-5 日志默认关闭；关闭时不落盘；诊断包不含未勾选数据

| 检查点 | 自动化用例 |
|---|---|
| 关闭时不创建任何文件、内存环形缓冲仍可用；打开后立即创建 `debug-*.log` | `SR5_Log_switch_off_creates_no_file_and_on_creates_one` |

**人工补充**：确认“原始报文写入日志”默认关闭；生成诊断包时核对只含日志与自检信息，不含报文内容（除非用户显式开启）。

## SR-6 软件不会自动联网上传任何数据

| 检查点 | 自动化用例 | 说明 |
|---|---|---|
| 源码中不存在上传类 API | `SR6_Source_code_contains_no_outbound_upload_apis` | 扫描 `HttpClient` / `WebClient` / `SmtpClient` / `TelemetryClient` / `ApplicationInsights` 等 |
| 出站连接只出现在允许清单内 | `SR6_Outbound_sockets_only_appear_in_the_expected_transport_and_probe_files` | 允许：`TcpChannel`、局域网探测、提权助手客户端/服务端、IP Helper 探测。任何新增外联文件必须先评审并登记 |

**人工补充**：现场用抓包工具（Wireshark）跑一次完整流程（连接/扫描/导出），确认除目标设备地址外无其他外联。

## SR-7 插件只从受信目录加载；写能力必须声明

| 检查点 | 自动化用例 |
|---|---|
| 受信目录之外（父目录）的 `plugin.json` 不会被加载 | `SR7_Plugin_outside_the_trusted_directory_is_never_loaded` |
| `mayWrite=true` 与所需权限能在界面上暴露 | `SR7_Manifest_write_capability_is_surfaced_for_ui_confirmation` |

**人工补充**：把一个未知来源插件目录放进 `plugins\`，确认扩展中心显示其能力/风险，且在启用前不会注册任何扩展点。

---

## 发布前签字表

| 编号 | 自动化 | 人工 | 结论 | 签字/日期 |
|---|---|---|---|---|
| SR-1 扫描只读 | 通过（7 用例） | 待硬件抓包 | 软件侧通过 | — |
| SR-2 写确认 | 通过（4 用例） | 待界面点验 | 软件侧通过 | — |
| SR-3 广播写 | 通过（1 用例） | — | 通过 | — |
| SR-4 网卡回滚 | 通过（3 用例） | **待隔离虚拟机** | 软件侧通过 | — |
| SR-5 日志开关 | 通过（1 用例） | 待诊断包核对 | 软件侧通过 | — |
| SR-6 无外联上传 | 通过（2 用例） | 待抓包 | 软件侧通过 | — |
| SR-7 插件受信 | 通过（2 用例） | 待界面点验 | 软件侧通过 | — |

> 说明：M4 内“软件侧通过”指自动化用例全部通过；**待办项与现场机/隔离虚拟机相关**，必须在正式发布（v1.0）前补齐并签字。
