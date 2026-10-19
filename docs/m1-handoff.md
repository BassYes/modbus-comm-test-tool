# M1 → M2 交接文档

| 项目 | 内容 |
|---|---|
| 交接方向 | M1（通信核心）→ M2（寄存器定位 / 局域网发现 / 持久化） |
| 交接对象 | `E:\Download\DSH\通讯测试工具`（本仓库） |
| 依据文档 | `E:\Download\DSH\通讯测试工具\通讯测试工具-产品与功能规划.md`（下称“规划”） |
| 交接状态 | M1 已交付并自测通过：构建 0 错误 0 警告，113 个用例全绿，程序可启动且 7 个功能页正常 |
| M2 状态 | **已交付 v0.2**：M2-1～M2-4 全部完成，并完成第 13 章的反馈修复与 R-17/R-18/R-19；构建 **0 错误 0 警告**，测试 **通过 178，失败 0**，10 个功能页均可正常打开与关闭；详见 `docs/m2-交付说明.md` |
| M2 剩余 | 仅 R-16（多会话与可拆分窗口，已改排 M3）与 G-01（私有 Git 远程仓库，待项目负责人确定托管平台） |
| M2 范围 | 规划 11.3：**R-03 寄存器定位**、**R-06 局域网设备扫描**、**监视表 / 扫描方案 / 报文模板持久化** |
| 不在 M2 范围 | 网卡改 IP 与辅助 IP（M3）、插件宿主（M3）、串口参数扫描与 TCP Unit ID 扫描（M4） |

---

## 0. 交接摘要（先读这段）

1. M1 已经交付**可用的 Modbus RTU/TCP 通信核心 + 7 个功能页界面**，M2 是在这个地基上加功能，**不需要重写通信层**。
2. M2 的三个功能都有现成积木可复用：`DataScanService`（取值）+ `DataDecoder.TryDecodeNumber`（多类型多字节序解释）→ 寄存器定位；`SubnetCalculator`（算范围）+ `NetworkAdapters`（取本机网卡）→ 局域网扫描；`SettingsStore`（带 schemaVersion 的原子写）+ 各 ViewModel 骨架 → 持久化。
3. **有 5 个硬约束不能破坏**（第 4 章），其中 3 个已在 M1 里踩过坑修过：待配对响应必须先登记后发送、回显抑制只能用于读功能码、扫描类任务绝不产生写功能码。
4. 本机环境特殊：**只能用 `%USERPROFILE%\.dotnet\dotnet.exe` 构建**，`dotnet test` 会因 testhost 权限问题中止，请用 `tests/Comm.TestRunner`。
5. 新增测试程序集必须注册到 `tests/Comm.TestRunner/Program.cs` 的 `assemblies` 数组，否则本地跑不到。

---

## 1. 交付物与状态快照

### 1.1 项目清单

| 项目 | 状态 | M2 是否需要改动 |
|---|---|---|
| `src/Comm.Core` | 完成：领域模型、错误码、传输/编解码契约、报文记录、暂停门 | 少量（如需新增错误码） |
| `src/Comm.Protocols.Modbus` | 完成：CRC-16、RTU/TCP 成帧、PDU 编解码、解析、地址换算 | 基本不动（新增功能码时扩展 `Fn`/`PduParser`） |
| `src/Comm.Transport` | 完成：串口、TCP、回环（测试/演示） | 不需要 |
| `src/Comm.Session` | 完成：事务层（优先级队列、配对、重试、写守卫）、会话门面 | 不需要 |
| `src/Comm.Scanning` | 完成：设备扫描、数据扫描、数据解码、手动报文 | **主要工作区**（新增寄存器定位、监视表） |
| `src/Comm.Capture` | 完成：报文总线、JSONL 落盘、TXT/CSV 导出 | 可选（补记录文件滚动） |
| `src/Comm.Network` | 完成：IPv4 子网计算、本机网卡只读枚举 | **主要工作区**（新增局域网发现） |
| `src/Comm.Diagnostics` | 完成：日志闸门、滚动写入、内存环形日志、崩溃报告、诊断包 | 不需要 |
| `src/Comm.Settings` | 完成：设置模型与存储（原子写、损坏保留） | **主要工作区**（新增方案/模板持久化 + schemaVersion=2） |
| `src/Comm.Plugins.Abstractions` | 完成：扩展契约（仅契约） | 可选（接口签名如需扩展） |
| `src/Comm.App.Wpf` | 完成：7 个功能页 | **主要工作区**（新增页面/面板） |
| `tests/*` | 113 用例全绿 | **必须扩充** |

### 1.2 功能实现状态（对照需求编号）

| 需求 | 内容 | 状态 |
|---|---|---|
| R-01 | 串口设备扫描 | 完成 |
| R-02 | 设备数据扫描 | 完成 |
| R-03 | **寄存器定位** | **M2 待做** |
| R-04 | 手动报文 + 校验自动填入 | 完成 |
| R-05 | 实时报文监视 | 完成 |
| R-06 | **同网段在线设备扫描** | **M2 待做** |
| R-07 | 快速修改 IP/掩码/网关 | M3 |
| R-08 | 子网掩码计算同网段 IP | 完成（M2 的局域网扫描要复用它） |
| R-09 | 日志开关与同级目录日志 | 完成 |
| R-10 | 扩展接口与位置 | 契约完成，宿主在 M3 |
| R-11/R-12 | 串口参数扫描 / TCP Unit ID 扫描 | M4 |
| R-13 | 辅助 IP | M3 |
| — | **监视表 / 扫描方案 / 报文模板持久化** | **M2 待做**（规划 4.3.5、4.4.5、4.12） |

---

## 2. 环境、构建与自检（先跑通再动手）

```bash
# 本机未装 Visual Studio；系统默认 dotnet 只有 SDK 3.0.101，不能用于本仓库
DOTNET=%USERPROFILE%/.dotnet/dotnet.exe        # SDK 8.0.426（用户目录安装，未注册 PATH）

cd "E:/Download/DSH/通讯测试工具"
$DOTNET build CommTestTool.sln                 # 期望：0 个错误 0 个警告
$DOTNET run --project tests/Comm.TestRunner    # 期望：总计：通过 113，失败 0
$DOTNET run --project src/Comm.App.Wpf         # 运行界面
```

界面自检：

```bash
# 直接打开第 N 个功能页（1=连接中心 … 7=设置与日志），用于人工核对与截图
src/Comm.App.Wpf/bin/Debug/net8.0-windows/CommTestTool.exe --page=3
bash docs/capture-pages.sh                     # 批量打开各页并截图到 docs/
```

注意事项：

- `dotnet test CommTestTool.sln` 在本机会以 `Win32Exception (5)` 中止（testhost 无法读取父进程句柄），这是**环境限制不是代码缺陷**。用例本身是标准 xunit，正常机器上可直接 `dotnet test`。
- 构建时若报 `CommTestTool.exe 被占用`，先 `taskkill /IM CommTestTool.exe /F`。
- 自动化脚本强杀进程后请删 `src/Comm.App.Wpf/bin/Debug/net8.0-windows/data/.running`，否则下次启动会弹“上次运行异常退出”。
- 便携形态：整个 `bin/Debug/net8.0-windows` 目录可直接复制到现场机器运行，配置与日志都在程序同级目录。

---

## 3. 代码地图（M2 需要知道的位置）

### 3.1 分层与依赖方向（不要反向引用）

```text
Comm.App.Wpf
  → Comm.Scanning / Comm.Capture / Comm.Network / Comm.Settings / Comm.Diagnostics
      → Comm.Session → Comm.Protocols.Modbus / Comm.Transport → Comm.Core
```

- `Comm.Core` 无任何项目引用（只依赖 BCL）。
- **`Comm.Network` 不引用 `Comm.Session`**：局域网扫描里“对 502 端口做 Modbus 识别”这一步，请用**委托或接口注入**，不要让 Network 反向依赖 Session（见 6.2 建议设计）。

### 3.2 关键文件与类

| 位置 | 作用 |
|---|---|
| `src/Comm.Session/SessionContracts.cs` | `ModbusRequest`（含 `Priority`/`Confirmed`/`BroadcastConfirmed`/`TimeoutMs`/`RetryCount`/`RawAdu`/`NoResponseExpected`）、`SessionOptions`、`ExchangeResult`、`SessionStatistics` |
| `src/Comm.Session/ModbusMaster.cs` | 事务层：三档优先级队列、配对、重试、回显抑制、写守卫。**改动前先读第 4 章** |
| `src/Comm.Session/ModbusSession.cs` | 会话门面：`ConnectAsync/DisconnectAsync/ExecuteAsync/ReadAsync/Statistics/Codec` |
| `src/Comm.Scanning/DataScanService.cs` | `DataScanOptions` / `DataScanItem` / `ScanAsync` / `ExportCsv`；分批 + 非法地址二分细化 |
| `src/Comm.Scanning/DataDecoder.cs` | 纯函数：`Decode`、`TryDecodeNumber`、`Encode`、`ToBigEndianBytes`；类型 × 字节序 |
| `src/Comm.Scanning/DeviceScanService.cs` | 地址扫描（探测模板 `03→04→01→02`）+ `ExportCsv` |
| `src/Comm.Scanning/ManualSendService.cs` | 手动报文：构建、追加校验、校验、发送、写确认判断、风险描述 |
| `src/Comm.Scanning/ScanContracts.cs` | `ScanStatus`、`ScanProgress`、`DataType`、`ByteOrder` 及中文显示 |
| `src/Comm.Core/Tasks/PauseGate.cs` | 可等待暂停门（任务边界暂停/恢复） |
| `src/Comm.Network/SubnetCalculator.cs` | `Compute`、`ComputeSubnet`、`CheckSameSubnet`、`EnumerateHosts(info, maxCount)`、`ClassifyAddress`、`EstimatedScanTimeText` |
| `src/Comm.Network/NetworkAdapters.cs` | `Read()` → `LocalAdapterInfo`（IP/掩码/网关/MAC/状态/DNS，只读） |
| `src/Comm.Capture/FrameBus.cs` | 报文总线：显示缓冲（可丢）+ 落盘（不丢）、暂停显示、解析开关、`FramesAppended` 事件 |
| `src/Comm.Capture/FrameExporter.cs` | TXT / CSV 导出模板（M2 导出新结果时照抄格式） |
| `src/Comm.Diagnostics/LogGate.cs` | 日志闸门：开关、目录回退、滚动与保留策略；`Info/Debug/Warning/Error` |
| `src/Comm.Settings/AppSettings.cs` | `AppSettings` / `SettingsStore`（原子写、损坏保留、`SchemaVersion` 校验） |
| `src/Comm.Plugins.Abstractions/PluginContracts.cs` | `PluginManifest`、`PluginCapability`、`IFrameParser`、`IDeviceDiscoveryProvider`、`IDeviceConfigurator`（契约，M3 接宿主） |
| `src/Comm.Core/Errors/ErrorInfo.cs` | `ErrorInfo`、`ErrorCategory`、`Severity`、`ErrorCodes` 常量、`CommException` |

### 3.3 界面层结构（三处注册点，新增页面必改）

| 注册点 | 文件 | 说明 |
|---|---|---|
| DI 注册 | `src/Comm.App.Wpf/App.xaml.cs`（`services.AddSingleton<...>` 块） | 新增 ViewModel 后加一行 |
| VM → View 映射 | `src/Comm.App.Wpf/MainWindow.xaml`（`Window.Resources` 的 `DataTemplate`） | 新增页必须加 `DataTemplate` |
| 导航项 | `src/Comm.App.Wpf/ViewModels/MainViewModel.cs` 的 `NavItems` | 顺序决定 `--page=N` 的编号，**新增页后请同步更新 README 与本文档的编号说明** |

其它界面基础设施：

- `Services/AppServices.cs`：全局共享状态门面（`Settings`、`Update(...)`、`Log`、`Bus`、`Sessions`、`Monitor`、`Dialogs`、`Tasks`、`DataDirectory/CaptureDirectory/ExportDirectory`）。**ViewModel 的统一依赖入口**。
- `Services/SessionManager.cs`：连接管理 + `CreateDeviceScanService()/CreateDataScanService()/CreateManualSendService()`。
- `Services/MonitorService.cs`：监视列表（`Rows`）、暂停显示、解析开关、过滤、导出、记录。
- `Services/TaskStatusService.cs`：底部状态栏任务进度（`Report(ScanProgress)` / `Idle()`）。
- `Services/DialogService.cs`：消息框、文件/目录选择、`ConfirmWrite`（写操作二次确认）。
- `Styles/Theme.xaml`：颜色与控件样式（TX 蓝 `#1B5E9B`、RX 绿 `#1E7B34`、错误红 `#C62828`）。
- `Converters/Converters.cs`：方向/状态着色、布尔可见性等。

### 3.4 测试基建

| 位置 | 作用 |
|---|---|
| `tests/Comm.TestSupport/ModbusSlaveSimulator.cs` | 从站模拟器：寄存器映射、非法地址、不支持功能码、离线、CRC 破坏、RS-485 回显、广播、请求记录 |
| `tests/Comm.TestSupport/CollectingFrameObserver.cs` | 收集报文用于断言（方向计数、标记检查） |
| `src/Comm.Transport/LoopbackChannel.cs` | 把写出的字节交给响应函数并回灌（无需硬件） |
| `tests/Comm.TestRunner/Program.cs` | 本地运行器：反射执行 `[Fact]/[Theory]`；**新增测试程序集要在这里注册** |

---

## 4. 不可破坏的契约（硬约束）

### 4.1 并发与链路

| 编号 | 约束 | 落地位置 |
|---|---|---|
| CC-1 | 同一串口/TCP 连接同一时刻只有一个未完成请求 | `ModbusMaster` 单事务循环 |
| CC-2 | 通信读/写路径不做 UI、磁盘、耗时格式化 | `ModbusMaster`、`FrameBus` |
| CC-3 | 所有队列有上限与满载策略 | `FrameBus`（显示可丢/落盘不丢）、日志通道 |
| CC-4 | 取消后 1 秒内不再发出新请求 | `ModbusMaster` 重试前检查取消 |
| CC-5 | 暂停显示不影响通信与落盘 | `MonitorService` + `FrameBus.DisplayPaused` |
| CC-6 | **扫描类任务不得产生写功能码请求** | `ModbusMaster.GuardReject` + `ModbusRequest.Priority` |
| CC-7 | 退出不遗留后台进程 | `App.OnExit` 依次取消任务、关连接、排空队列 |

### 4.2 三个已踩过的坑（改动相关代码时请保留注释与顺序）

1. **先登记待配对响应，再发送**（`ModbusMaster.ExecuteOneAsync`）：`PrepareResponseWait(...)` 必须在 `WriteFrameAsync(...)` **之前**调用。反过来写在模拟器/快速设备上会丢响应造成假超时。
2. **回显抑制只对读功能码生效**（`WriteFrameAsync` 里 `Fn.IsRead(...)` 白名单）：写单个线圈/寄存器（05/06）的响应与请求逐字节相同，按内容比较会把真响应当回显丢掉。
3. **校验错误不立即结束请求**（`HandleFrames`）：收到 CRC 错误帧时只置 `_pendingChecksumError` 并继续等待有效响应，超时后才按校验错误返回；否则线上噪声或回显会让正常请求失败。

### 4.3 安全与写操作

- 写入必须走 `ModbusRequest.Confirmed = true`；广播（地址 0）写还需 `BroadcastConfirmed = true`。守卫在**事务层**，不要只放在 UI（否则未来脚本入口会绕过）。
- 对应错误码：`VAL-4004`（未确认写）、`VAL-4005`（广播写未确认）、`VAL-4006`（扫描写被阻断）。
- UI 侧二次确认：`DialogService.ConfirmWrite(...)`，文案需显示目标、功能码、地址、数值与风险。

### 4.4 数据与错误约定

- **值 vs 异常**：超时、CRC 错误、Modbus 异常响应都是**预期结果**，通过 `ExchangeResult.Status` 返回，不抛异常；只有传输失效与程序错误才抛 `CommException`。
- **错误码**：前缀 `TRN/FRM/PRO/VAL/PLT/STO/PLG/INT`，已发布错误码不得改含义；M2 需要新码时在 `ErrorCodes` 里追加（局域网相关可先用现成的 `VAL-4007 ValidationNetworkRange`）。
- **地址显示**：协议地址 ↔ 1 基/4xxxx 的换算只走 `AddressMap`，界面与导出都调它，不要各自加减 1。
- **字节序**：`DataDecoder` 中“寄存器线上字节序列”与“数值大端字节序”通过 `ByteOrder`（ABCD/BADC/CDAB/DCBA，四种都是自逆置换）互相转换；解码数值统一转 Little-Endian 再交给 `BitConverter`。**这是 M1 修过的缺陷，改动时务必跑 `DataDecoderTests`**。
- **时间**：显示用 `DateTimeOffset`，间隔/耗时用 `Clock.MonotonicTicks`（`Stopwatch`）。

### 4.5 日志与持久化

- 日志开关关闭时**不创建任何日志文件**（`LogGate` 只在开启时 new `RollingFileWriter`）；内存环形日志始终可用（`LogGate.BuildDiagnosticText`）。不要把新功能写成“关闭开关也落盘”。
- 设置文件：`SettingsStore` 原子写（临时文件 + `File.Move`），损坏时备份为 `.bad-时间戳` 并用默认值启动。
- ⚠️ **`SettingsStore.Load` 目前把 `SchemaVersion > 1` 视为“更高版本”→ 只读使用默认值**。M2 升级到 2 时必须同时改这里：接受 2、对 1 做迁移（新增字段给默认值即可），否则老设置会被静默丢弃。
- 报文记录：`FrameFileWriter` 写 JSONL（表头 + 每行一条），**没有按大小滚动**（已知欠账，见第 9 章）。

### 4.6 UI 线程与刷新

- 界面绑定对象只允许在 UI 线程改（`Application.Current.Dispatcher`）。
- 报文列表按 50 ms 批量投递（`FrameBus.FramesAppended` → `MonitorService.AppendNew`），**不要逐条 `ObservableCollection.Add`**。
- 扫描类 ViewModel 的标准骨架（照抄 `DeviceScanViewModel` / `DataScanViewModel`）：`CancellationTokenSource` + `PauseGate` + `IProgress<ScanProgress>` + `finally { _app.Tasks.Idle(); }`。

---

## 5. M1 遗留的已知欠账（M2 顺手清掉或明确记录）

| 编号 | 问题 | 位置 | 建议 |
|---|---|---|---|
| D-1 | 设备扫描“导出”对话框给了 TXT 选项，但实际只写 CSV | `DeviceScanViewModel.Export` | 按扩展名分支，或去掉 TXT 选项 |
| D-2 | `MonitorSettings.ColorizeDirections` 定义了但未接线（界面始终着色） | `Comm.Settings` + `MonitorView` | 接线到样式，或删除该字段 |
| D-3 | 界面显示行数上限硬编码 20 000（与 `MonitorSettings.DisplayCapacity` 不是一回事） | `MonitorService.UiCapacity` | 改为可配置（设置页显示缓冲项） |
| D-4 | 报文记录 JSONL 无按大小滚动，长时间记录会无限增长 | `Comm.Capture/FrameFileWriter.cs` | 加 `MaxFileSizeMb` 滚动（可参考 `RollingFileWriter`） |
| D-5 | UI 文案内嵌在 XAML/代码中，未走 `.resx`（规划 AD-14 要求） | 全部界面 | 排到 M3 或明确放弃多语言 |
| D-6 | `PduParser.ParseReadRequest` 对位区也返回 `PduKind.ReadRegisters`（仅显示用） | `Comm.Protocols.Modbus/PduParser.cs` | 低优先级，改时补用例 |
| D-7 | 提权助手 `NetConfigHelper`（M3）尚未创建，`Comm.Platform.Windows` 项目不存在 | — | M3 新建 |

---

## 6. M2 任务清单（可直接排期）

> 每项都给出：规划依据 → 建议落点 → 要点 → 验收与测试。工作量估算仅供排序参考。

### 6.1 M2-1 寄存器定位（规划 4.4，需求 R-03）· 估算 4～6 人日

**目标**：现场“知道数值/看到变化，但不知道寄存器地址”时，在指定数据区与地址范围内反查候选点位。

建议落点：新增 `src/Comm.Scanning/RegisterLocator.cs`（+ `RegisterLocatorOptions`、`RegisterCandidate`、`LocatorMode`），复用 `DataScanService` 取值、`DataDecoder.TryDecodeNumber/Decode` 解释。

建议接口（与现有风格保持一致）：

```csharp
public enum LocatorMode { Exact, Range, Tolerance, Change, Trend, BitChange, Text }

public sealed record RegisterLocatorOptions
{
    public byte UnitId { get; init; } = 1;
    public DataArea Area { get; init; } = DataArea.HoldingRegisters;
    public int StartAddress { get; init; }
    public int EndAddress { get; init; }
    public LocatorMode Mode { get; init; } = LocatorMode.Exact;

    // 候选解释组合（规划 4.4.3）
    public IReadOnlyList<DataType> CandidateTypes { get; init; } = [DataType.UInt16, DataType.Int16, DataType.UInt32, DataType.Int32, DataType.Float32];
    public IReadOnlyList<ByteOrder> CandidateOrders { get; init; } = [ByteOrder.Abcd, ByteOrder.Badc, ByteOrder.Cdab, ByteOrder.Dcba];
    public IReadOnlyList<double> CandidateScales { get; init; } = [1.0, 0.1, 0.01, 10.0];

    // 匹配条件
    public double Target { get; init; }
    public double TargetMin { get; init; }
    public double TargetMax { get; init; }
    public double Tolerance { get; init; }
    public double TolerancePercent { get; init; }
    public string? TextPattern { get; init; }

    // 采样（变化 / 趋势 / 位变化模式）
    public int SampleCount { get; init; } = 2;         // 基准 + 1 次对比
    public int SampleIntervalMs { get; init; } = 1000;
    public int MaxResults { get; init; } = 200;
    public bool ExcludeNonFinite { get; init; } = true; // 过滤 NaN/Inf（规划 4.4.6）

    // 复用数据扫描的参数
    public int ResponseTimeoutMs { get; init; } = 1000;
    public int RetryCount { get; init; }
    public int IntervalMs { get; init; } = 20;
}

public sealed record RegisterCandidate(
    int Address, DataType Type, ByteOrder Order, double Scale,
    string RawHex, string ValueText, double? MatchedValue,
    string[]? BeforeValues, string[]? AfterValues,
    int MatchScore, string Reason);

public sealed class RegisterLocator
{
    public RegisterLocator(ModbusSession session);
    public Task<IReadOnlyList<RegisterCandidate>> LocateAsync(
        RegisterLocatorOptions options, PauseGate? gate,
        IProgress<ScanProgress>? progress, IProgress<RegisterCandidate>? candidateProgress,
        CancellationToken cancellationToken);
}
```

实现要点：

1. **一次采样、多次解释**：先用 `DataScanService`（`RequestPriority.Scan`）把范围内原始寄存器取回来，再在内存里按“类型 × 字节序 × 缩放”组合逐个解释打分——**不要为每种组合重新发请求**（现场总线很慢）。
2. 打分与排序：精确/容差命中优先；变化模式按变化幅度、变化次数排序；位变化模式只报 0→1 / 1→0 的位。排序必须**稳定**（同分按地址升序），便于现场复核。
3. 误判控制（规划 4.4.6）：候选项必须同时展示**协议地址 + 原始字节 + 解释规则（类型/字节序/缩放）**；默认过滤 NaN/Inf 与明显不合理的浮点值；用户确认前不写“已识别”字样。
4. 文本模式：在寄存器字节里找 ASCII/UTF-8 片段（可复用 `HexUtil.ToAscii` 的思路）。
5. 结果动作：加入监视表（见 6.4）、生成读取报文（可用 `ModbusRequests.Read` 生成后交给手动报文页的输入框）、导出 CSV（照抄 `DataScanService.ExportCsv` 的格式）。
6. 界面：在“数据扫描”页旁新增“寄存器定位”页（第 3 步的三处注册点）。向导式三步：范围与目标 → 采样/快照 → 候选列表。

**测试（≥8 个用例）**：用 `ModbusSlaveSimulator` 造合成寄存器映射，覆盖：精确命中（含缩放）、容差命中、范围命中、多组合同时命中时排序稳定、无命中、变化模式（两次快照）、位变化、文本模式、NaN 与非法 BCD 被过滤。
**验收**：现场给一台设备，能在一个 100 寄存器范围内、30 秒内给出候选并说明解释规则。

### 6.2 M2-2 局域网设备扫描（规划 4.7，需求 R-06）· 估算 3～5 人日

**目标**：扫描选定本地网段，区分“已发现 / 端口开放 / Modbus 有响应 / 未确认”。

建议落点：新增 `src/Comm.Network/Discovery/`：`LanScanner.cs`、`LanScanOptions.cs`、`LanHostResult.cs`，主机探测抽象 `IHostProbe`（便于测试）。**不要让 `Comm.Network` 引用 `Comm.Session`**。

```csharp
public enum HostDiscoveryKind { Arp, Icmp, Tcp, Unknown }
public enum LanHostState { Discovered, PortOpen, ModbusResponding, Unconfirmed }

public sealed record LanScanOptions
{
    public string? CidrOrRange { get; init; }              // 也可由 IP + 掩码计算
    public string? StartIp { get; init; }
    public string? EndIp { get; init; }
    public IReadOnlyList<int> TcpPorts { get; init; } = [502];
    public bool UseArp { get; init; } = true;
    public bool UseIcmp { get; init; } = true;
    public bool IdentifyModbus { get; init; }              // 默认 false（规划 4.7.2）
    public int Concurrency { get; init; } = 64;            // 16~256
    public int PerHostTimeoutMs { get; init; } = 800;
    public int MaxAddresses { get; init; } = 4096;         // 超过需用户确认（规划 4.7.1）
    public int SendDelayMs { get; init; }                  // 全局限速
}

public sealed record LanHostResult(
    string Ip, string? Mac, string? HostName, HostDiscoveryKind Kind,
    bool PortOpen, bool ModbusResponding, double LatencyMs,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, string Note);

public interface IHostProbe
{
    Task<HostProbeResult> ProbeAsync(string ip, int timeoutMs, CancellationToken cancellationToken);
}
```

实现要点：

1. IP 范围：`SubnetCalculator.Compute(...)` + `EnumerateHosts(info, MaxAddresses)`；超限时**不展开完整列表**，界面提示数量并要求确认（`VAL-4007`）。默认值取 `AppSettings.Scan.MaxLanScanAddresses`（当前 4096）。
2. 探测方法：ARP 用 IP Helper `SendARP`（同一二层网段有效，路由之后无效——要在界面/文档里写明）；ICMP 用 `System.Net.NetworkInformation.Ping`（无需管理员）；TCP 端口探测用 `Socket.ConnectAsync` + `PerHostTimeoutMs`。
3. 并发：`SemaphoreSlim(Concurrency)` 限流 + 每主机独立超时 + 取消支持 + `PauseGate`；**不要**一次创建 4096 个任务后不管。
4. 结果分级（规划 4.7.2）：不能因为 ICMP 无响应就判“离线”，要给出 `Unconfirmed` 并允许复测。
5. Modbus 识别（默认关闭）：只发**一次只读请求**（如 `03` 读 1 个寄存器，或 `43/14` 读设备标识）；这一步通过注入的委托完成，例如：
   ```csharp
   public delegate Task<string?> ModbusIdentifyDelegate(string ip, int port, byte unitId, CancellationToken ct);
   ```
   实现放在 `Comm.Scanning`（那里已经引用 Session），由界面把 `SessionManager`/新建的临时会话接上。
6. 与子网计算联动：网络工具页可直接把计算出的范围一键“送去扫描”；扫描结果里的“创建 TCP 会话”按钮把 IP 回填到连接中心。
7. 导出：CSV（照抄 `DeviceScanService.ExportCsv` 的格式），字段含 IP/MAC/主机名/发现方式/端口状态/Modbus 状态/耗时/首末发现时间/备注。

**测试（≥6 个用例）**：用 `TcpListener` 绑定 `127.0.0.1` 做确定性的端口探测测试（含超时、连接被拒、并发限流生效）；用假 `IHostProbe` 测分类逻辑与“不因 ICMP 失败判离线”；子网范围超限返回错误码；取消与暂停行为。
**验收**：能在 /24 网段 10 秒内列出在线主机并区分端口开放与 Modbus 响应；扫描范围超过上限时会拦截并要求确认。

### 6.3 M2-3 持久化：扫描方案 / 报文模板 / 设备目标（规划 4.12、7.2、7.3）· 估算 2～3 人日

建议落点：`src/Comm.Settings/` 新增 `ScanPlan.cs`、`FrameTemplate.cs`、`DeviceTarget.cs`；`AppSettings` 增加集合字段并把 `SchemaVersion` 升到 **2**。

```csharp
public sealed record DeviceTarget
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = string.Empty;
    public ConnectionKindHint Kind { get; init; }        // Serial / Tcp
    public string Endpoint { get; init; } = string.Empty; // COM3 或 192.168.1.10:502
    public byte UnitId { get; init; } = 1;
    public string? Notes { get; init; }
    public string[] Tags { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record ScanPlan
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = string.Empty;
    public string? DeviceTargetId { get; init; }
    public string PlanKind { get; init; } = "device";   // device / data / locate / lan
    public JsonElement Payload { get; init; }            // 各扫描选项的序列化快照（用 JsonElement 保持向前兼容）
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
}

public sealed record FrameTemplate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = string.Empty;
    public string Transport { get; init; } = "rtu";      // rtu / tcp
    public string Hex { get; init; } = string.Empty;
    public bool IsWrite { get; init; }                  // 写模板必须有风险标签（规划 6.5）
    public string? Notes { get; init; }
}
```

要点：

1. `AppSettings` 加 `SchemaVersion` 迁移：**同步修改 `SettingsStore.Load` 中 `SchemaVersion > 1` 的判断**（见 4.5 ⚠️），并新增迁移函数（v1 → v2：新集合填空数组）。
2. 方案里存的是**选项快照**（`JsonElement`），这样以后给 `DataScanOptions` 加字段不会让旧方案读不出来；读取时用 `JsonSerializer.Deserialize<DataScanOptions>` 反序列化，缺字段走默认值。
3. 写模板保存/载入时必须保留 `IsWrite` 标签，**从模板重发写报文要再次确认**（规划 6.5）。
4. 界面：设备扫描页/数据扫描页加“保存为方案 / 载入方案”；报文工具页加“收藏 / 应用模板”。设置页可列出与删除方案。
5. 服务器/工程文件（多方案打包）留给 M3，不要现在做目录级工程格式。

**测试（≥5 个用例）**：`SettingsStore` 往返（含集合字段）、v1 文件升级到 v2 不丢原字段、更高版本文件仍按只读处理、损坏文件备份 `.bad-*`、写模板标记与风险提示。
**验收**：保存一个数据扫描方案 → 重启程序 → 载入后参数完全一致；老设置文件升级后无告警。

### 6.4 M2-4 监视表（规划 4.3.5、4.4.5）· 估算 2～3 人日

- 服务：`src/Comm.Scanning/WatchTableService.cs`，用 `RequestPriority.Periodic`（优先级介于交互与扫描之间，见规划 13.3），`PeriodicTimer` 定时只入队不做 I/O。
- 数据对象：规划 7.4 的 `RegisterPoint`（地址、数据类型、字节序、缩放、单位、名称、备注、位定义）。
- 功能：加入/移除点位、周期刷新、变化标记（与上一采样比较）、按点导出 CSV、点位详情。
- 与扫描/定位联动：扫描结果与定位候选都能“加入监视表”。
- 注意：周期刷新与扫描任务的交互（规划 13.3）——扫描进行中允许穿插周期请求，但要在界面提示“已穿插请求，可能影响扫描耗时统计”。

**测试**：合成映射上验证周期读取、变化标记、暂停/恢复、与扫描任务并行时不破坏 CC-1（交替请求同一个会话，断言 TX/RX 计数与结果正确）。

---

## 7. 可复用资产（避免重复劳动）

| 需要的能力 | 直接复用 |
|---|---|
| 取值（按数据区分批读取、非法地址细化） | `DataScanService.ScanAsync` |
| 多类型/多字节序解释与数值提取 | `DataDecoder.Decode` / `TryDecodeNumber` / `Encode` |
| 子网范围计算与地址分类 | `SubnetCalculator.Compute` / `EnumerateHosts` / `ClassifyAddress` / `EstimatedScanTimeText` |
| 本机网卡信息（含掩码，用于默认扫描范围） | `NetworkAdapters.Read()` |
| 暂停/恢复/取消 | `PauseGate` + `CancellationToken` |
| 进度与状态栏 | `ScanProgress` + `TaskStatusService.Report(...)` |
| 结果导出 CSV（含表头与过滤条件行） | `DeviceScanService.ExportCsv` / `DataScanService.ExportCsv` / `FrameExporter` |
| 扫描页 ViewModel 骨架（含 finally 收尾、日志、错误处理） | `DeviceScanViewModel` / `DataScanViewModel` |
| 界面样式与配色 | `Styles/Theme.xaml` |
| 无硬件测试 | `ModbusSlaveSimulator` + `LoopbackChannel` + `CollectingFrameObserver` |
| 协议层新功能码 | `Fn` 常量与 `Fn.MaxQuantity`、`PduParser`、`ModbusRequests` |

---

## 8. 测试与验收要求（M2 的 Definition of Done）

1. **全绿**：`$DOTNET run --project tests/Comm.TestRunner` 必须包含 M1 的 113 个用例 + M2 新增用例，**通过率 100%**；`$DOTNET build CommTestTool.sln` 保持 **0 错误 0 警告**。
2. **新增测试程序集**（如 `Comm.Network.Discovery.Tests`）要：
   - 建 `.csproj`（照抄现有测试项目，含 `Microsoft.NET.Test.Sdk` / `xunit` / `xunit.runner.visualstudio`）；
   - `$DOTNET sln CommTestTool.sln add <path>`；
   - **在 `tests/Comm.TestRunner/Program.cs` 的 `assemblies` 数组里注册**（否则本地跑不到）。
3. **不破坏 M1 行为**：任何涉及 `ModbusMaster`、`DataDecoder`、`SettingsStore` 的改动，必须跑通对应的既有用例；不要为了让新功能通过而放宽既有断言。
4. **可测试性优先**：网络与时间相关逻辑通过接口/委托注入（`IHostProbe`、`ModbusIdentifyDelegate`、`TimeProvider`），保证 CI 无需真实网络与硬件。
5. **文档同步**：更新 `README.md`（功能清单、页面截图、命令）与新增 `docs/m2-交付说明.md`；并在规划文档 `11.3 M2` 小节补“实现状态”一行（M1 已按此格式补过）。
6. **截图**：`bash docs/capture-pages.sh`（新增页面后同步更新脚本与 `--page=N` 说明）。
7. **安全回归**：至少覆盖“扫描类任务不产生写功能码”“从模板重发写报文需二次确认”两条。

---

## 9. 已知陷阱清单（都会真实发生）

| # | 现象 | 原因 | 对策 |
|---|---|---|---|
| 1 | `dotnet test` 中止 | 本机 testhost 无法 OpenProcess 父进程 | 用 `Comm.TestRunner` |
| 2 | 请求“假超时”，日志里有 TX 无 RX | 待配对响应登记晚于发送 | 保持 `PrepareResponseWait` 在 `WriteFrameAsync` 之前 |
| 3 | 写 05/06 请求超时 | 响应与请求逐字节相同，被当成回显丢弃 | 回显抑制只对 `Fn.IsRead` 的功能码启用 |
| 4 | 偶发校验错误后正常响应被忽略 | 收到 CRC 错误帧就结束请求 | 只置标志继续等有效响应（M1 已修） |
| 5 | RTU 分帧错位/半帧 | Windows + USB 串口静默计时不准 | 保持“长度预测为主、静默为辅”；阈值 `SilenceThresholdMs` 可调 |
| 6 | 扫描耗时远超预期 | 空地址必然超时 × 探测模板数 | 界面先显示最坏情况预计耗时；地址范围别默认 1~247 |
| 7 | 界面卡死 | 在 UI 线程做同步 I/O 或逐条 `Add` | 后台任务 + `IProgress` + 50 ms 批量投递 |
| 8 | 新页面打不开/不显示 | 三处注册点漏改 | DI + `DataTemplate` + `NavItems`（见 3.3） |
| 9 | 老设置文件被丢 | `SettingsStore` 对 `SchemaVersion > 1` 直接回退默认值 | 升到 2 时同步改判断并写迁移 |
| 10 | 大数据量界面变卡 | `MonitorService.UiCapacity` 硬编码 20 000 | 需要更大时改设置项而不是改常量 |
| 11 | 记录文件无限增长 | `FrameFileWriter` 无滚动 | M2 顺手补滚动（D-4） |
| 12 | 强杀后启动弹“上次异常退出” | `data/.running` 未清 | 自动化脚本里删除该文件 |
| 13 | 局域网扫描把内网打爆 | 无限制并发 / 无上限范围 | `SemaphoreSlim` + `MaxAddresses` + 用户确认 |
| 14 | 路由网段 ARP 拿不到 MAC | ARP 仅二层有效 | 结果标 `Unconfirmed` 并说明，不谎报在线 |
| 15 | 新增测试“跑不到” | 运行器未注册程序集 | 见 8.2 |

---

## 10. 待确认事项（M2 开工前建议先问）

沿用规划 17.2 中原样未决的项：

1. 是否存在 32 位 Windows / Windows 7 的老旧工控机（影响发布形态）。
2. 报文记录的最大持续时长与峰值吞吐（校准缓冲与滚动默认值）。
3. 是否需要项目级设备点表与 Excel 导入导出。
4. 是否需要命令行模式用于批量扫描/自动验收（`IAutomationProvider` 已留位）。
5. 是否有必须支持的厂商设备型号（加入兼容性矩阵）。
6. 代码签名证书/分发流程。
7. 是否需要 v1.0 同时提供英文界面。

M2 新增待确认：

8. 寄存器定位的**候选数量上限**与是否需要“按置信度自动确认”？（建议：不自动确认，只排序）
9. 局域网扫描是否需要 MAC 厂商（OUI）查询（需要离线数据文件，约 2 MB）？
10. 监视表最小刷新周期（建议不低于 200 ms，避免打爆现场总线）？
11. 扫描方案是否需要“导出/导入单个方案文件”给同事复用（不依赖工程文件）？

---

## 11. 交接验收清单（接手方执行）

| # | 动作 | 期望结果 | 完成 |
|---|---|---|---|
| 1 | `$DOTNET build CommTestTool.sln` | 0 错误 0 警告 | ✅ |
| 2 | `$DOTNET run --project tests/Comm.TestRunner` | 通过 178，失败 0 | ✅ |
| 3 | 运行 `CommTestTool.exe` | 界面出现 10 个功能页，底部状态栏正常 | ✅ |
| 4 | `--page=4` 寄存器定位、`--page=5` 监视表、`--page=9` 局域网扫描 | 页面正常，不崩溃 | ✅ |
| 5 | 打开“Debug 日志”，检查程序同级 `logs/` 出现 `debug-*.log`；关闭后不再新建 | 符合规划 4.10 | ✅ |
| 6 | 通读第 4 章（硬约束）与第 9 章（陷阱） | 已确认不会踩已知坑 | ✅ |
| 7 | 与交接方/用户确认第 10 章待定项 | 已确认或明确按建议默认执行 | ✅ |
| 8 | 从第 6 章挑选第一项任务（建议 M2-1 寄存器定位） | 开工 | ✅ |

> M2 实际交付内容与偏差见 `docs/m2-交付说明.md`；第 13 章的反馈修复均已实施并在该文档第 4 节记录人工验证结果。

---

## 12. 参考文档

| 文档 | 位置 |
|---|---|
| 产品与功能规划（需求源头） | `E:\Download\DSH\通讯测试工具\通讯测试工具-产品与功能规划.md` |
| M1 使用说明 | `README.md` |
| M1 交付说明（范围对照、偏差、风险） | `docs/v0.1-交付说明.md` |
| 构建环境备忘 | `docs/build-environment.md` |
| 界面截图 | `docs/screenshot-*.png` |
| 截图脚本 | `docs/capture-pages.sh` |

---

**交接确认**（M1 完成，M2 接手）：

- M1 交付方：______________  日期：__________
- M2 接手方：______________  日期：__________
- 备注：

---

## 13. 2026-10-17 现场反馈：可修复问题与实施思路

> 反馈来源：`E:\Download\DSH\通讯测试工具\现存bug.docx`。以下结论已对照 v0.1 源码与反馈截图核查；这里只记录修复设计，不表示代码已经修改。新增功能想法另见《通讯测试工具-产品与功能规划》v0.3 第 21 章。

### 13.1 结论与优先级

| 反馈项 | 结论 | 建议优先级 | 主要落点 |
|---|---|---|---|
| 1. 网络工具的 IP/掩码二进制显示不全 | 可修，属于响应式布局缺陷 | P3；并入最近一次 UI 修复 | `Views/NetworkToolsView.xaml` |
| 3. “尝试读取设备标识”文字显示不全 | 可修，属于控件内容未换行 | P3；并入最近一次 UI 修复 | `Views/DeviceScanView.xaml` |
| 5、10. 关闭窗口或双击左上角图标退出时报 `IAsyncDisposable` 错误 | 可修，两个现象是同一根因；正常退出路径存在确定性异常 | **P0，下一次构建前先修** | `App.xaml.cs:90-110`、`Services/SessionManager.cs` |
| 8. 新一轮设备扫描仍显示上一轮停止提示 | 可修，属于状态未在任务启动时重置 | P2；与扫描页修复一并完成 | `ViewModels/DeviceScanViewModel.cs` |

### 13.2 问题 1：网络工具二进制文本被截断

**根因**：`NetworkToolsView.xaml` 把左侧面板固定为 `380`，结果表第一列又固定为 `110`；`BinaryText` 与 `MaskBinaryText` 使用 Consolas 显示 32 位点分二进制，但未设置换行、横向滚动或跨列布局。在窗口较窄、DPI 较高时，第二列可用宽度不足，因此尾部被裁切。计算结果本身没有丢失，`CopyDetailsCommand` 仍能复制完整文本。

**建议修复**：

1. 不用单纯扩大主窗口最小宽度掩盖问题；结果区改为可自适应布局。
2. “IP 二进制”“掩码二进制”优先改为标签占一行、值在下一行跨两列；值使用只读 `TextBox` 或带水平滚动的 `ScrollViewer`，并保留 Consolas。
3. 为值增加完整内容 `ToolTip` 和复制能力；在最小窗口宽度与 100%/125%/150%/200% DPI 下均应可读。
4. 顺带检查 `HostRangeText`、`EstimatedScanText` 等长文本，统一启用 `TextWrapping` 或裁切提示。

**验收**：在 `192.168.1.10/24`、`/0`、`/32` 三组输入下，两个二进制值完整可见或可无损横向滚动/复制；调整窗口大小不覆盖其他结果行。

### 13.3 问题 3：设备标识选项文字被截断

**根因**：设备扫描参数栏固定宽度 `330`，`CheckBox Content="尝试读取设备标识（43/14，可获得厂商与型号）"` 是单行字符串，默认不会按参数栏宽度换行。

**建议修复**：

1. 将 `CheckBox.Content` 改为 `TextBlock`，设置 `TextWrapping="Wrap"`；不要依赖扩大固定列宽。
2. 可将简短标题改为“尝试读取设备标识（43/14）”，把“可获得厂商与型号”放入第二行说明或 `ToolTip`。
3. 检查 125%～200% DPI、最小窗口尺寸及中文字体回退。

**验收**：完整文字可见，复选框点击区域正常，下面的预计耗时与按钮不被覆盖。

### 13.4 问题 5/10：退出时错误处置了异步服务容器

**已确认根因**：

- `SessionManager` 只实现 `IAsyncDisposable`。
- `App.OnExit` 虽然先手工调用了 `SessionManager.DisposeAsync()`，但 `finally` 中仍执行 `_services?.Dispose()`。
- Microsoft DI 的同步 `ServiceProvider.Dispose()` 发现容器内存在仅支持异步释放的 `SessionManager` 后抛出 `InvalidOperationException`，与日志堆栈 `App.xaml.cs:109` 完全一致。
- 点击右上角 X 和双击窗口左上角图标最终都进入同一关闭流程，所以两条反馈是同一个缺陷。

**建议修复**：

1. 提取一个幂等的关闭协调器，保证关闭流程最多执行一次；顺序为：停止循环发送/扫描任务 → 断开会话 → 刷新并关闭报文写入 → 记录“正常退出” → 关闭日志 → 清除 `.running` 标志 → 异步释放 DI 容器。
2. 不再调用 `_services.Dispose()`；使用 `_services.DisposeAsync()`。由于 WPF 的 `OnExit` 是同步入口，可在该处对**单个统一关闭任务**使用 `GetAwaiter().GetResult()`，或在窗口 `Closing` 阶段先异步完成关闭后再允许退出。
3. 明确容器创建对象与外部传入单例（`FrameBus`、`LogGate`）的所有权，避免重复释放；每个 `DisposeAsync` 应保持幂等。
4. 不用空 `catch` 掩盖退出失败；至少写入内存/应急崩溃文件，但退出阶段不得再次弹出“内部错误”形成递归提示。
5. 不使用带超时的 `.Wait(TimeSpan)` 后忽略布尔返回值；若确需总超时，应统一取消并记录哪个组件未能结束。

**回归测试与验收**：

- 未连接、串口已连接、TCP 已连接、扫描中、循环发送中分别执行：右上角 X、`Alt+F4`、双击左上角图标关闭。
- 所有场景均无 `INT-9001` 对话框、无未处理异常、无遗留 `CommTestTool.exe` 进程；报文文件可读，`.running` 被清除。
- 增加可单测的关闭协调器，使用只实现 `IAsyncDisposable` 的假服务验证“异步释放一次且顺序正确”。

### 13.5 问题 8：新扫描沿用上一轮状态提示

**根因**：`DeviceScanViewModel.StartAsync()` 在开始新任务时会清空结果并重置进度，但没有重置 `StatusMessage`。增量回调又只在发现“在线/疑似”设备时更新它；若新任务开始阶段尚未发现设备，界面会继续显示上一轮的“扫描已停止/已完成”文字，造成任务状态冲突。

**建议修复**：

1. 新任务通过参数校验后立即设置 `StatusMessage = "正在扫描…"`，并在首个请求前清除上一轮摘要。
2. 让进度回调统一维护“扫描中：已完成 x/y，当前地址 n”；“发现地址”作为独立的最近事件或通知，不与任务终态共用同一字符串。
3. 完成、取消、异常三种终态只在对应分支写入，`finally` 仅负责资源收尾；开始按钮在旧任务 `finally` 完成前保持禁用。
4. 如后续允许多会话并行，状态必须按 `TaskId/SessionId` 归属，避免迟到回调覆盖新任务。

**验收**：停止一次扫描后立即开始另一范围，新任务首帧发出前旧提示即消失；全程无在线设备时也持续显示新任务进度，最终摘要与实际扫描数一致。

### 13.6 与新增需求的衔接

反馈中的“可拆分多会话窗口、手动探测参数、串口关闭按钮、显式生成 Modbus TCP 报文、底栏显示串口参数、私有 Git 版本管理”均可实施，但属于功能或工程治理，不应混入缺陷补丁。产品范围、阶段划分、安全约束和验收标准已补入规划文档 v0.3 第 21 章。建议实施顺序：

1. 先修退出异常（问题 5/10）及扫描状态（问题 8）。
2. 同批完成两个低风险显示修复（问题 1/3）。
3. 再做串口开关、底栏参数与离线 TCP 报文生成。
4. 多会话窗口必须在 `SessionManager` 从单 `_session` 重构为会话注册表后实施，不能只复制窗口和复用同一个单例 ViewModel。

---

## 14. M2 交付记录（2026-10-17 完成）

本节记录第 13 章修复与 M2-1～M2-4 的实际落地结果，详细内容见 `docs/m2-交付说明.md`。

### 14.1 第 13 章修复实施情况

| 反馈项 | 实施结果 | 落地位置 |
|---|---|---|
| 1 网络工具二进制截断 | 改用只读 `TextBox`（自动换行 + 悬停完整内容） | `Views/NetworkToolsView.xaml` |
| 3 设备标识选项文字截断 | `CheckBox.Content` 改为可换行的 `TextBlock`，副说明放 ToolTip | `Views/DeviceScanView.xaml` |
| 5/10 退出报 `IAsyncDisposable` | 新增幂等关闭协调器（最多执行一次、按序、逐个隔离异常），改用 `_services.DisposeAsync()`，不再 `Dispose()` | `Comm.Core/Tasks/ShutdownCoordinator.cs`、`App.xaml.cs` |
| 8 扫描提示不刷新 | 任务启动立即重置为“正在扫描”，最近发现事件单独维护 | `DeviceScanViewModel`、`DataScanViewModel` |

### 14.2 M2 任务清单完成情况

| 任务 | 状态 | 关键设计 |
|---|---|---|
| M2-1 寄存器定位 | 完成 | `RegisterLocator`：一次采样、多次解释；`MatchSnapshots` 为纯函数（无硬件可测）；七种模式；稳定排序 + 候选上限；不自动确认 |
| M2-2 局域网扫描 | 完成 | 固定数量工作协程避免 4096 个任务；单主机超时隔离；`LanScanRange` 超限返回 `VAL-4007`；未确认 ≠ 离线；Modbus 识别走委托注入 |
| M2-3 持久化 | 完成 | schemaVersion 2 + v1→v2 迁移（并立即落盘）；方案存 `JsonElement` 选项快照；写模板保留 `IsWrite` 风险标签 |
| M2-4 监视表 | 完成 | `WatchTableService` 用 `Periodic` 优先级；连续地址合并读取；变化标记与上次值；应用级单例 `WatchListService` 供多页面“加入监视表” |

### 14.3 M2 新增的额外欠账清理

| 欠账 | 状态 |
|---|---|
| D-1 导出 TXT 实际只写 CSV | 已修（`DeviceScanService.ExportText`） |
| D-2 `ColorizeDirections` 未接线 | 已修（多值转换器 + 监视页开关，关闭后仍保留 TX/RX 文字） |
| D-3 界面行数硬编码 20 000 | 已修（`MonitorSettings.UiCapacity` + 设置页） |
| D-4 记录文件无滚动 | 已修（`FrameFileWriter` 按体积分卷） |
| D-5 / D-6 / D-7 | 未做，顺延 M3（本地化、位区 PduKind 显示、提权助手） |

### 14.4 下一步建议

1. **M3 优先做 R-16（多会话与可拆分窗口）**：先重构 `SessionManager` 为会话注册表（每个会话独立 `ModbusSession`、统计、任务与监视上下文），再引入 `SessionWorkspaceViewModel` 与“拆分 / 移回”窗口；串口仍按 `serial:COMx` 全局独占。
2. **M3 继续**：提权助手与网卡配置（R-07、R-13）、插件宿主（R-10）。
3. **M4**：串口参数扫描 / TCP Unit ID 扫描（R-11、R-12）、性能与 24 h 稳定性、硬件在环矩阵、签名与打包。
4. **工程治理**：尽早建立私有 Git 仓库与基线标签（G-01），见规划第 21.6 节的流程建议。
