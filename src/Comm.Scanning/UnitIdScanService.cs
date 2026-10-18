using Comm.Core.Errors;
using Comm.Core.Tasks;
using Comm.Protocols.Modbus;
using Comm.Session;

namespace Comm.Scanning;

/// <summary>
/// 创建并打开一条到目标 IP:端口的**临时** TCP 会话（R-12 并发扫描用）。
/// 由应用层提供实现（<c>Comm.Scanning</c> 不引用具体传输实现，保持分层，见规划 12.3）。
/// 返回 null 表示本次连接失败（该 worker 会退化为串行并记录原因）。
/// </summary>
public delegate Task<ModbusSession?> TcpSessionFactory(CancellationToken cancellationToken);

/// <summary>
/// TCP Unit ID 扫描参数（规划 4.2.7）。
/// 适用场景：Modbus TCP 网关后面挂 RS-485 总线，需要逐个 Unit ID 找出在线从站。
/// 探测规则与串口设备扫描**完全一致**（复用 <see cref="DeviceScanService"/>/
/// <see cref="DeviceScanOptions.ProbeTemplates"/>：默认 03→04→01→02，手动模式只允许 01/02/03/04），
/// 默认**串行**（并发度 1），并发度可调但需用户明确开启（网关常限制并发连接数）。
/// </summary>
public sealed record UnitIdScanOptions
{
    public byte StartUnitId { get; init; } = 1;
    public byte EndUnitId { get; init; } = 247;

    /// <summary>单次探测超时（默认与串口设备扫描一致：500 ms，见规划 15.5）。</summary>
    public int ResponseTimeoutMs { get; init; } = 500;

    public int RetryCount { get; init; }

    /// <summary>每个 Unit ID 之间的间隔（毫秒）；并发时为每个 worker 各自的间隔。</summary>
    public int IntervalMs { get; init; } = 20;

    /// <summary>发现后的额外确认次数（首探 + N 次确认全部通过才算“在线”，否则“疑似”）。</summary>
    public int ConfirmAttempts { get; init; }

    /// <summary>尝试读取设备标识（43/14），可得到厂商/型号。</summary>
    public bool TryReadDeviceIdentification { get; init; } = true;

    public DeviceProbeMode Mode { get; init; } = DeviceProbeMode.AutoTemplate;

    /// <summary>手动探测功能码；仅允许只读 01/02/03/04（默认 03）。</summary>
    public byte ManualProbeFunction { get; init; } = Fn.ReadHoldingRegisters;

    public ushort ManualProbeAddress { get; init; }
    public ushort ManualProbeQuantity { get; init; } = 1;

    /// <summary>
    /// 并发连接的 worker 数（规划 13.4.2：默认 1 = 严格串行）。
    /// &gt;1 会为额外 worker 建立临时 TCP 连接（要求设备/网关支持多连接）。
    /// </summary>
    public int Concurrency { get; init; } = 1;

    /// <summary>并发上限（超过该值一律按上限处理，避免把网关打爆）。</summary>
    public const int MaxConcurrency = 8;

    public int TotalUnitIds => Math.Max(0, EndUnitId - StartUnitId + 1);

    /// <summary>把 Unit ID 扫描参数转换为设备探测参数（探测规则复用，见规划 4.2.7）。</summary>
    public DeviceScanOptions ToProbeOptions() => new()
    {
        StartAddress = StartUnitId,
        EndAddress = EndUnitId,
        ResponseTimeoutMs = ResponseTimeoutMs,
        RetryCount = RetryCount,
        IntervalMs = 0, // 间隔由本服务在 worker 循环里控制（并发时每个 worker 独立计时）
        ConfirmAttempts = ConfirmAttempts,
        TryReadDeviceIdentification = TryReadDeviceIdentification,
        Mode = Mode,
        ManualProbeFunction = ManualProbeFunction,
        ManualProbeAddress = ManualProbeAddress,
        ManualProbeQuantity = ManualProbeQuantity
    };

    /// <summary>探测模板（与设备扫描一致：自动 03→04→01→02；手动仅一个只读模板）。</summary>
    public IReadOnlyList<DeviceProbeTemplate> ProbeTemplates() => ToProbeOptions().ProbeTemplates();

    public int EffectiveConcurrency() => Math.Clamp(Concurrency, 1, MaxConcurrency);

    /// <summary>
    /// 参数校验（与服务层门禁一致）：功能码白名单、数量上限、地址空间、并发范围（CC-6：扫描不产生写功能码）。
    /// </summary>
    public static bool Validate(UnitIdScanOptions options, out ErrorInfo? error)
    {
        error = null;

        if (options.StartUnitId < 1 || options.EndUnitId > 247 || options.EndUnitId < options.StartUnitId)
        {
            error = new ErrorInfo(ErrorCodes.ValidationAddressRange, ErrorCategory.Validation, Severity.Warning,
                $"Unit ID 范围非法：{options.StartUnitId} ~ {options.EndUnitId}（有效范围 1 ~ 247，且起始不得大于结束）");
            return false;
        }

        if (options.ResponseTimeoutMs < 50 || options.ResponseTimeoutMs > 10000)
        {
            error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                "响应超时必须在 50 ~ 10000 ms 之间");
            return false;
        }

        if (options.IntervalMs < 0 || options.IntervalMs > 5000)
        {
            error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                "请求间隔必须在 0 ~ 5000 ms 之间");
            return false;
        }

        if (options.ConfirmAttempts is < 0 or > 5)
        {
            error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                "确认次数必须在 0 ~ 5 之间");
            return false;
        }

        if (options.Concurrency < 1 || options.Concurrency > MaxConcurrency)
        {
            error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                $"并发度必须在 1 ~ {MaxConcurrency} 之间（默认 1 = 串行；网关常限制并发连接数）");
            return false;
        }

        // 只读门禁：与串口设备扫描共用同一套规则（写在服务层，界面被绕过也拦得住）。
        if (options.Mode == DeviceProbeMode.Manual &&
            (Fn.IsWrite(options.ManualProbeFunction) || !Fn.IsRead(options.ManualProbeFunction)))
        {
            error = new ErrorInfo(ErrorCodes.ValidationScanWriteBlocked, ErrorCategory.Validation, Severity.Error,
                $"扫描任务不允许使用功能码 0x{options.ManualProbeFunction:X2}，只能选择只读功能码 01/02/03/04",
                Hint: "写操作请在“报文工具”中手工发送，并完成二次确认");
            return false;
        }

        return DeviceScanOptions.Validate(options.ToProbeOptions(), out error);
    }

    /// <summary>预计最长耗时（无设备时每个 Unit ID 都会走完模板并超时）。</summary>
    public TimeSpan EstimatedDuration()
    {
        double perUnit = ProbeTemplates().Count * (double)ResponseTimeoutMs * (1 + Math.Max(0, RetryCount))
                         + IntervalMs + Math.Max(0, ConfirmAttempts) * ResponseTimeoutMs;
        double total = TotalUnitIds * perUnit / EffectiveConcurrency() / 1000.0;
        return TimeSpan.FromSeconds(total);
    }

    public string EstimatedDurationText()
    {
        var duration = EstimatedDuration();
        var concurrencyText = EffectiveConcurrency() > 1 ? $"，{EffectiveConcurrency()} 并发" : "，串行";
        return duration.TotalMinutes >= 1
            ? $"约 {duration.TotalMinutes:F1} 分钟（{TotalUnitIds} 个 Unit ID{concurrencyText}）"
            : $"约 {duration.TotalSeconds:F0} 秒（{TotalUnitIds} 个 Unit ID{concurrencyText}）";
    }

    /// <summary>生成方案负载用的描述文本。</summary>
    public string SummaryText =>
        $"Unit ID {StartUnitId}~{EndUnitId}，超时 {ResponseTimeoutMs} ms，重试 {RetryCount}，" +
        $"探测 {(Mode == DeviceProbeMode.Manual ? $"0x{ManualProbeFunction:X2} 起始 {ManualProbeAddress} 数量 {ManualProbeQuantity}" : "自动模板 03→04→01→02")}，" +
        $"并发 {EffectiveConcurrency()}";
}

/// <summary>
/// 单个 Unit ID 的扫描结果（规划 4.2.7 与 4.2.4 的结果字段一致；<see cref="UnitId"/> 即从站地址）。
/// </summary>
public sealed record UnitIdScanItem(
    byte UnitId,
    DeviceOnlineStatus Status,
    byte ProbeFunction,
    byte? ExceptionCode,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    double ElapsedMs,
    string? DeviceIdentification,
    string Note,
    int WorkerIndex = 0,
    int ConfirmSuccess = 0,
    int ConfirmTotal = 0)
{
    public string StatusText => Status switch
    {
        DeviceOnlineStatus.Online => "在线",
        DeviceOnlineStatus.Suspected => "疑似",
        _ => "未发现"
    };

    public string ProbeFunctionText => $"0x{ProbeFunction:X2} {Fn.Name(ProbeFunction)}";

    public string ExceptionText => ExceptionCode is { } code
        ? $"0x{code:X2} {ModbusExceptions.Describe(code)}"
        : string.Empty;

    public string WorkerText => WorkerIndex > 0 ? $"#{WorkerIndex + 1}" : string.Empty;

    public string ConfirmText => ConfirmTotal > 0 ? $"{ConfirmSuccess}/{ConfirmTotal}" : string.Empty;

    public static UnitIdScanItem FromProbe(byte unitId, DeviceScanItem probe, int workerIndex) => new(
        UnitId: unitId,
        Status: probe.Status,
        ProbeFunction: probe.ProbeFunction,
        ExceptionCode: probe.ExceptionCode,
        FirstSeen: probe.FirstSeen,
        LastSeen: probe.LastSeen,
        ElapsedMs: probe.ElapsedMs,
        DeviceIdentification: probe.DeviceIdentification,
        Note: probe.Note,
        WorkerIndex: workerIndex,
        ConfirmSuccess: probe.ConfirmSuccess,
        ConfirmTotal: probe.ConfirmTotal);

    /// <summary>未完成的探测（worker 连接失败、被取消的边界项等）——不谎报在线，也不谎报离线。</summary>
    public static UnitIdScanItem Unconfirmed(byte unitId, string note, int workerIndex = 0) => new(
        UnitId: unitId,
        Status: DeviceOnlineStatus.Offline,
        ProbeFunction: Fn.ReadHoldingRegisters,
        ExceptionCode: null,
        FirstSeen: DateTimeOffset.Now,
        LastSeen: DateTimeOffset.Now,
        ElapsedMs: 0,
        DeviceIdentification: null,
        Note: note,
        WorkerIndex: workerIndex);
}

/// <summary>
/// TCP Unit ID 扫描服务（R-12，规划 4.2.7、13.4.2）。
/// <para>
/// 复用 <see cref="DeviceScanService.ProbeAddressAsync"/> 的探测规则（默认模板 / 手动只读模板 / 异常响应判在线 /
/// CRC 错误只记“疑似”），因此 TCP 与串口两条路径的行为一致。
/// </para>
/// <para>
/// 并发：默认 1（严格串行，复用当前会话，且满足 CC-1“单链路单事务”）。
/// 并发 &gt; 1 时为额外 worker 通过 <see cref="TcpSessionFactory"/> 建立临时连接，扫描结束后一律释放；
/// 任一 worker 建连失败时自动降级为更小并发并记录原因，不会中断整轮扫描。
/// </para>
/// </summary>
public sealed class UnitIdScanService
{
    private readonly ModbusSession _primary;
    private readonly TcpSessionFactory? _factory;

    public UnitIdScanService(ModbusSession primary, TcpSessionFactory? factory = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _factory = factory;
    }

    /// <summary>实际生效的并发度（没有会话工厂时强制串行）。</summary>
    public int EffectiveConcurrency(UnitIdScanOptions options) =>
        _factory is null ? 1 : options.EffectiveConcurrency();

    /// <summary>执行扫描。参数非法时抛 <see cref="CommException"/>（与设备扫描一致）。</summary>
    public async Task<IReadOnlyList<UnitIdScanItem>> ScanAsync(UnitIdScanOptions options, PauseGate? gate,
        IProgress<ScanProgress>? progress, IProgress<UnitIdScanItem>? itemProgress,
        CancellationToken cancellationToken)
    {
        if (!UnitIdScanOptions.Validate(options, out var error)) throw new CommException(error!);

        int concurrency = EffectiveConcurrency(options);
        string taskName = concurrency > 1 ? $"Unit ID 扫描（{concurrency} 并发）" : "Unit ID 扫描";
        var probeOptions = options.ToProbeOptions();
        int total = options.TotalUnitIds;
        int completed = 0;

        var results = new List<UnitIdScanItem>(total);
        var sync = new object();

        progress?.Report(new ScanProgress(taskName, 0, total, "开始", ScanStatus.Running));

        // 建立 worker：worker 0 复用当前会话；其余通过工厂建临时会话（失败则减少并发）。
        var workers = new List<Worker> { new(0, new DeviceScanService(_primary), null) };
        var startupNotes = new List<string>();

        for (int i = 1; i < concurrency; i++)
        {
            ModbusSession? extra = null;
            try
            {
                extra = await _factory!(cancellationToken).ConfigureAwait(false);

                // 工厂契约：返回**已打开**的会话。未打开的会话会让探测直接失败，
                // 因此这里明确降级并给出原因，而不是静默把 Unit ID 记成“未发现”（不谎报结果）。
                if (extra is { IsOpen: false })
                {
                    startupNotes.Add($"并发 worker {i + 1} 返回的会话尚未打开，本轮实际并发 {workers.Count}");
                    await extra.DisposeAsync().ConfigureAwait(false);
                    extra = null;
                }
            }
            catch (Exception ex)
            {
                startupNotes.Add($"并发 worker {i + 1} 建立连接失败：{ex.Message}");
            }

            if (extra is null)
            {
                if (startupNotes.Count == 0 || !startupNotes[^1].Contains("实际并发", StringComparison.Ordinal))
                {
                    startupNotes.Add($"并发 worker {i + 1} 未建立连接，本轮实际并发 {workers.Count}");
                }

                break;
            }

            workers.Add(new Worker(i, new DeviceScanService(extra), extra));
        }

        if (startupNotes.Count > 0)
        {
            progress?.Report(new ScanProgress(taskName, 0, total, string.Join("；", startupNotes), ScanStatus.Running));
        }

        int next = options.StartUnitId;

        try
        {
            var tasks = workers.Select(worker => Task.Run(async () =>
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (gate is not null)
                    {
                        if (gate.IsPaused)
                        {
                            lock (sync) progress?.Report(new ScanProgress(taskName, completed, total, "已暂停", ScanStatus.Paused));
                        }

                        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }

                    int unitId = Interlocked.Increment(ref next) - 1;
                    if (unitId > options.EndUnitId) return;

                    UnitIdScanItem item;
                    try
                    {
                        var probe = await worker.Service
                            .ProbeAddressAsync((byte)unitId, probeOptions, cancellationToken)
                            .ConfigureAwait(false);
                        item = UnitIdScanItem.FromProbe((byte)unitId, probe, worker.Index);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 单个 Unit ID 的异常（含链路中断）不能打断整轮扫描：记为未确认并继续。
                        item = UnitIdScanItem.Unconfirmed((byte)unitId, $"探测异常：{ex.Message}", worker.Index);
                    }

                    int done;
                    lock (sync)
                    {
                        results.Add(item);
                        done = ++completed;
                    }

                    itemProgress?.Report(item);
                    progress?.Report(new ScanProgress(taskName, done, total, $"Unit ID {unitId}", ScanStatus.Running));

                    if (options.IntervalMs > 0)
                    {
                        await Task.Delay(options.IntervalMs, cancellationToken).ConfigureAwait(false);
                    }
                }
            }, cancellationToken)).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            // 临时会话一律释放（保留当前会话，它属于用户）。
            foreach (var worker in workers)
            {
                if (worker.Owned is null) continue;
                try
                {
                    await worker.Owned.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // 释放失败不影响扫描结果
                }
            }
        }

        progress?.Report(new ScanProgress(taskName, total, total, "完成", ScanStatus.Completed));

        return results.OrderBy(r => r.UnitId).ToArray();
    }

    /// <summary>把结果导出为 CSV（规划 4.2.5 的“导出扫描结果”）。</summary>
    public static string ExportCsv(IReadOnlyList<UnitIdScanItem> items)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("UnitID,状态,探测功能码,异常码,首次发现,最近响应,耗时ms,确认,设备标识,备注");
        foreach (var item in items)
        {
            builder.Append(item.UnitId).Append(',')
                .Append(item.StatusText).Append(',')
                .Append(item.ProbeFunctionText.Replace(',', ' ')).Append(',')
                .Append(item.ExceptionCode is { } code ? $"0x{code:X2}" : string.Empty).Append(',')
                .Append(item.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(item.LastSeen.ToString("yyyy-MM-dd HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(item.ElapsedMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                .Append(item.ConfirmText).Append(',')
                .Append((item.DeviceIdentification ?? string.Empty).Replace(',', ' ')).Append(',')
                .Append((item.Note ?? string.Empty).Replace(',', ' ').Replace('\n', ' '))
                .AppendLine();
        }

        return builder.ToString();
    }

    private sealed record Worker(int Index, DeviceScanService Service, ModbusSession? Owned);
}
