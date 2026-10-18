using System.Globalization;
using System.Text;
using Comm.Core.Errors;
using Comm.Core.Tasks;
using Comm.Protocols.Modbus;
using Comm.Session;

namespace Comm.Scanning;

/// <summary>寄存器定位模式（见规划 4.4）。</summary>
public enum LocatorMode
{
    /// <summary>精确值（可叠加容差）。</summary>
    Exact,

    /// <summary>数值区间。</summary>
    Range,

    /// <summary>容差区间（按绝对值或百分比）。</summary>
    Tolerance,

    /// <summary>前后两次采样值发生变化。</summary>
    Change,

    /// <summary>多次采样持续增大或持续减小。</summary>
    Trend,

    /// <summary>寄存器内某一位发生 0→1 / 1→0。</summary>
    BitChange,

    /// <summary>寄存器字节中的 ASCII 文本片段。</summary>
    Text
}

/// <summary>寄存器定位参数（见规划 4.4.2）。</summary>
public sealed record RegisterLocatorOptions
{
    public byte UnitId { get; init; } = 1;

    /// <summary>数据区。仅支持寄存器区（线圈/离散输入请用数据扫描）。</summary>
    public DataArea Area { get; init; } = DataArea.HoldingRegisters;

    public int StartAddress { get; init; }
    public int EndAddress { get; init; }
    public LocatorMode Mode { get; init; } = LocatorMode.Exact;

    /// <summary>候选解释组合（规划 4.4.3）：类型 × 字节序 × 缩放。</summary>
    public IReadOnlyList<DataType> CandidateTypes { get; init; } =
        new[] { DataType.UInt16, DataType.Int16, DataType.UInt32, DataType.Int32, DataType.Float32 };

    public IReadOnlyList<ByteOrder> CandidateOrders { get; init; } =
        new[] { ByteOrder.Abcd, ByteOrder.Badc, ByteOrder.Cdab, ByteOrder.Dcba };

    public IReadOnlyList<double> CandidateScales { get; init; } = new[] { 1.0, 0.1, 0.01, 10.0 };

    public double Target { get; init; }
    public double TargetMin { get; init; }
    public double TargetMax { get; init; }
    public double Tolerance { get; init; }
    public double TolerancePercent { get; init; }
    public string? TextPattern { get; init; }

    /// <summary>采样轮数；变化/趋势/位变化模式至少需要 2 轮（规划 4.4.4）。</summary>
    public int SampleCount { get; init; } = 2;

    public int SampleIntervalMs { get; init; } = 1000;

    /// <summary>候选数量上限；默认只排序，不自动确认（规划 4.4.6）。</summary>
    public int MaxResults { get; init; } = 200;

    /// <summary>过滤 NaN / Infinity 与明显不合理的浮点值。</summary>
    public bool ExcludeNonFinite { get; init; } = true;

    public int ResponseTimeoutMs { get; init; } = 1000;
    public int RetryCount { get; init; }
    public int IntervalMs { get; init; } = 20;

    /// <summary>实际采样轮数：只有变化类模式需要多轮。</summary>
    public int EffectiveSampleCount() =>
        Mode is LocatorMode.Change or LocatorMode.Trend or LocatorMode.BitChange
            ? Math.Clamp(SampleCount, 2, 10)
            : 1;

    public int EffectiveSampleIntervalMs() => Math.Clamp(SampleIntervalMs, 0, 60_000);

    public int EffectiveStartAddress() => Math.Clamp(StartAddress, 0, 65535);

    public int EffectiveEndAddress() => Math.Clamp(Math.Max(EndAddress, StartAddress), 0, 65535);

    /// <summary>参数自检。预期内的输入问题用值表达，不抛异常（见规划 15.1）。</summary>
    public static bool Validate(RegisterLocatorOptions options, out ErrorInfo? error)
    {
        error = null;

        if (options.Area.IsBitArea())
        {
            error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                "寄存器定位只适用于保持寄存器与输入寄存器",
                Hint: "线圈与离散输入请使用“数据扫描”查看位状态");
            return false;
        }

        if (options.StartAddress is < 0 or > 65535 || options.EndAddress is < 0 or > 65535)
        {
            error = new ErrorInfo(ErrorCodes.ValidationAddressRange, ErrorCategory.Validation, Severity.Warning,
                "地址范围必须在 0 ~ 65535 之间");
            return false;
        }

        if (options.EndAddress < options.StartAddress)
        {
            error = new ErrorInfo(ErrorCodes.ValidationAddressRange, ErrorCategory.Validation, Severity.Warning,
                "结束地址必须大于或等于起始地址");
            return false;
        }

        if (options.Mode is LocatorMode.Change or LocatorMode.Trend or LocatorMode.BitChange
            && options.EffectiveSampleCount() < 2)
        {
            error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                "变化、趋势与位变化模式至少需要 2 次采样");
            return false;
        }

        if (options.Mode == LocatorMode.Trend && options.EffectiveSampleCount() < 3)
        {
            error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                "趋势模式至少需要 3 次采样才能判断持续增大或减小",
                Hint: "把采样轮数设为 3 或更多");
            return false;
        }

        if (options.CandidateTypes.Count == 0 || options.CandidateOrders.Count == 0 || options.CandidateScales.Count == 0)
        {
            error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                "至少需要选择一种数据类型、字节序与缩放系数");
            return false;
        }

        if (options.MaxResults <= 0)
        {
            error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                "候选数量上限必须大于 0");
            return false;
        }

        return true;
    }
}

/// <summary>
/// 寄存器定位候选（规划 4.4.5）。候选项必须同时给出协议地址、原始寄存器与解释规则，
/// 用户确认前不得出现“已识别”字样（规划 4.4.6）。
/// </summary>
public sealed record RegisterCandidate(
    int Address,
    DataType Type,
    ByteOrder Order,
    double Scale,
    string RawHex,
    string ValueText,
    double? MatchedValue,
    string[]? BeforeValues,
    string[]? AfterValues,
    int MatchScore,
    string Reason)
{
    /// <summary>解释规则文本，例如“浮点 32 位 / CDAB（字交换）/ ×0.1”。</summary>
    public string InterpretationText =>
        $"{Type.ToChinese()} / {Order.ToDisplay()} / ×{Scale.ToString("G", CultureInfo.InvariantCulture)}";

    public string ScoreText => $"{MatchScore} 分";
}

/// <summary>
/// 寄存器定位（见规划 4.4，需求 R-03）：已知数值 / 变化 / 位变化 / 文本片段，反查可能的寄存器地址与数据格式。
/// 核心策略是**一次采样、多次解释**——现场总线很慢，绝不为每种“类型 × 字节序 × 缩放”组合重新发一次请求。
/// </summary>
public sealed class RegisterLocator
{
    private readonly ModbusSession _session;

    public RegisterLocator(ModbusSession session)
    {
        _session = session;
    }

    /// <summary>采样并把原始寄存器按“类型 × 字节序 × 缩放”逐个解释、打分、稳定排序。</summary>
    public async Task<IReadOnlyList<RegisterCandidate>> LocateAsync(RegisterLocatorOptions options, PauseGate? gate,
        IProgress<ScanProgress>? progress, IProgress<RegisterCandidate>? candidateProgress,
        CancellationToken cancellationToken)
    {
        if (!RegisterLocatorOptions.Validate(options, out var error))
        {
            throw new CommException(error!);
        }

        var snapshots = await SampleAsync(options, gate, progress, cancellationToken).ConfigureAwait(false);
        var candidates = MatchSnapshots(options, snapshots);

        progress?.Report(new ScanProgress("寄存器定位", 0, candidates.Count, "匹配完成，正在输出候选", ScanStatus.Running));
        for (int i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            candidateProgress?.Report(candidates[i]);
            progress?.Report(new ScanProgress("寄存器定位", i + 1, candidates.Count, "候选结果", ScanStatus.Running));
        }

        progress?.Report(new ScanProgress("寄存器定位", candidates.Count, candidates.Count, "完成", ScanStatus.Completed));
        return candidates;
    }

    /// <summary>按采样轮数取回范围内原始寄存器；非正常地址不参与匹配。</summary>
    private async Task<List<IReadOnlyDictionary<int, ushort>>> SampleAsync(RegisterLocatorOptions options, PauseGate? gate,
        IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        int rounds = options.EffectiveSampleCount();
        var snapshots = new List<IReadOnlyDictionary<int, ushort>>(rounds);
        var scanService = new DataScanService(_session);
        var translated = progress is null ? null : new TaskNameProgress(progress, "寄存器定位");

        for (int round = 0; round < rounds; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (gate is not null)
            {
                if (gate.IsPaused)
                {
                    progress?.Report(new ScanProgress("寄存器定位", round, rounds, "已暂停", ScanStatus.Paused));
                }
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new ScanProgress("寄存器定位", round, rounds,
                $"采样第 {round + 1}/{rounds} 轮", ScanStatus.Running));

            var items = await scanService.ScanAsync(new DataScanOptions
            {
                UnitId = options.UnitId,
                Area = options.Area,
                StartAddress = options.EffectiveStartAddress(),
                EndAddress = options.EffectiveEndAddress(),
                BatchSize = 0,
                Type = DataType.UInt16,
                Order = ByteOrder.Abcd,
                ResponseTimeoutMs = options.ResponseTimeoutMs,
                RetryCount = options.RetryCount,
                IntervalMs = options.IntervalMs,
                RefineInvalidBlocks = true
            }, gate, translated, null, cancellationToken).ConfigureAwait(false);

            var map = new Dictionary<int, ushort>();
            foreach (var item in items)
            {
                if (item.Status == ScanItemStatus.Ok && item.RawValue.HasValue) map[item.Address] = item.RawValue.Value;
            }
            snapshots.Add(map);

            if (round + 1 < rounds && options.EffectiveSampleIntervalMs() > 0)
            {
                await Task.Delay(options.EffectiveSampleIntervalMs(), cancellationToken).ConfigureAwait(false);
            }
        }

        return snapshots;
    }

    /// <summary>
    /// 纯匹配逻辑（不含 I/O），便于无硬件测试：对每一轮快照按“类型 × 字节序 × 缩放”解释并打分。
    /// 排序稳定：分值降序 → 地址升序 → 类型 → 字节序 → 缩放升序（规划 4.4.6）。
    /// </summary>
    public static IReadOnlyList<RegisterCandidate> MatchSnapshots(RegisterLocatorOptions options,
        IReadOnlyList<IReadOnlyDictionary<int, ushort>> snapshots)
    {
        if (snapshots.Count == 0) return Array.Empty<RegisterCandidate>();

        int start = options.EffectiveStartAddress();
        int end = options.EffectiveEndAddress();
        var found = new List<RegisterCandidate>();

        if (options.Mode == LocatorMode.Text)
        {
            for (int address = start; address <= end; address++)
            {
                if (TryReadTextRun(snapshots[0], address, end, out var fragment, out var runLength)
                    && (string.IsNullOrEmpty(options.TextPattern)
                        || fragment.Contains(options.TextPattern, StringComparison.OrdinalIgnoreCase)))
                {
                    found.Add(new RegisterCandidate(address, DataType.Ascii, ByteOrder.Abcd, 1.0,
                        RawHex(snapshots[0], address, runLength), fragment, null, null, null, 90,
                        $"命中文本片段“{fragment}”"));
                }
            }
        }
        else
        {
            foreach (var type in options.CandidateTypes)
            {
                int registerCount = type.RegisterCount();
                foreach (var order in options.CandidateOrders)
                {
                    foreach (var scale in options.CandidateScales)
                    {
                        for (int address = start; address + registerCount - 1 <= end; address++)
                        {
                            if (!TryReadSeries(snapshots, address, registerCount, type, order, scale,
                                    options.ExcludeNonFinite, out var series))
                            {
                                continue;
                            }

                            var match = Evaluate(options, series, snapshots, address, type, order, scale);
                            if (match is null) continue;

                            var hit = match.Value;
                            if (options.Mode == LocatorMode.BitChange)
                            {
                                // 位变化：一个地址只报一行，行内汇总发生变化的所有位。
                                found.Add(new RegisterCandidate(address, type, order, scale,
                                    RawHex(snapshots[0], address, registerCount),
                                    ValueText(snapshots[0], address, registerCount, type, order, scale),
                                    hit.Value, hit.Before, hit.After, hit.Score, hit.Reason));
                                continue;
                            }

                            found.Add(new RegisterCandidate(address, type, order, scale,
                                RawHex(snapshots[0], address, registerCount),
                                ValueText(snapshots[0], address, registerCount, type, order, scale),
                                hit.Value, hit.Before, hit.After, hit.Score, hit.Reason));
                        }
                    }
                }
            }
        }

        return found
            .OrderByDescending(c => c.MatchScore)
            .ThenBy(c => c.Address)
            .ThenBy(c => (int)c.Type)
            .ThenBy(c => (int)c.Order)
            .ThenBy(c => c.Scale)
            .Take(Math.Max(0, options.MaxResults))
            .ToArray();
    }

    private static MatchResult? Evaluate(RegisterLocatorOptions options, double[] series,
        IReadOnlyList<IReadOnlyDictionary<int, ushort>> snapshots, int address, DataType type, ByteOrder order, double scale)
    {
        double current = series[^1];
        double first = series[0];

        switch (options.Mode)
        {
            case LocatorMode.Exact:
            case LocatorMode.Tolerance:
            {
                double tolerance = EffectiveTolerance(options);
                double delta = Math.Abs(current - options.Target);
                if (delta > tolerance) return null;

                int score;
                if (delta == 0)
                {
                    score = 100;
                }
                else
                {
                    double ratio = tolerance <= 0 ? 1 : delta / tolerance;
                    score = Math.Clamp(100 - (int)Math.Round(40 * ratio), 60, 99);
                }

                var reason = delta == 0
                    ? $"精确匹配（目标 {Format(options.Target)}）"
                    : $"容差匹配（目标 {Format(options.Target)}，偏差 {Format(delta)}，容差 {Format(tolerance)}）";
                return new MatchResult(score, current, null, null, reason);
            }

            case LocatorMode.Range:
            {
                if (current < options.TargetMin || current > options.TargetMax) return null;
                bool atEdge = current == options.TargetMin || current == options.TargetMax;
                return new MatchResult(atEdge ? 90 : 80, current, null, null,
                    $"落在区间 {Format(options.TargetMin)} ~ {Format(options.TargetMax)}");
            }

            case LocatorMode.Change:
            {
                if (series.Distinct().Count() <= 1) return null;

                var before = series[..^1].Select(Format).ToArray();
                var after = new[] { Format(current) };
                double amplitude = Math.Abs(current - first);
                double reference = Math.Max(1.0, Math.Max(Math.Abs(first), Math.Abs(current)));
                int score = 70 + Math.Min(25, (int)Math.Round(25 * Math.Min(1.0, amplitude / reference)));
                return new MatchResult(score, current, before, after,
                    $"采样值由 {Format(first)} 变为 {Format(current)}（幅度 {Format(amplitude)}）");
            }

            case LocatorMode.Trend:
            {
                if (series.Length < 3) return null;

                bool rising = true;
                bool falling = true;
                bool moved = false;
                for (int i = 1; i < series.Length; i++)
                {
                    if (series[i] > series[i - 1]) falling = false;
                    if (series[i] < series[i - 1]) rising = false;
                    if (series[i] != series[i - 1]) moved = true;
                }

                if (!moved) return null;
                if (!rising && !falling) return null;

                int changes = 0;
                for (int i = 1; i < series.Length; i++)
                {
                    if (series[i] != series[i - 1]) changes++;
                }

                int score = 75 + Math.Min(20, changes);
                return new MatchResult(score, current,
                    series[..^1].Select(Format).ToArray(),
                    new[] { Format(current) },
                    rising ? $"持续增大（{series.Length} 次采样）" : $"持续减小（{series.Length} 次采样）");
            }

            case LocatorMode.BitChange:
            {
                if (type.RegisterCount() != 1) return null;

                var changes = new List<string>();
                var previousTexts = new List<string>();
                var afterTexts = new List<string>();
                for (int i = 1; i < snapshots.Count; i++)
                {
                    if (!snapshots[i - 1].TryGetValue(address, out var beforeRaw)) continue;
                    if (!snapshots[i].TryGetValue(address, out var afterRaw)) continue;
                    if (beforeRaw == afterRaw) continue;

                    for (int bit = 0; bit < 16; bit++)
                    {
                        bool before = (beforeRaw & (1 << bit)) != 0;
                        bool after = (afterRaw & (1 << bit)) != 0;
                        if (before == after) continue;
                        changes.Add($"第 {bit} 位 {(before ? "1→0" : "0→1")}");
                    }

                    previousTexts.Add($"0x{beforeRaw:X4}");
                    afterTexts.Add($"0x{afterRaw:X4}");
                }

                if (changes.Count == 0) return null;
                return new MatchResult(85, current,
                    previousTexts.Count > 0 ? previousTexts.ToArray() : null,
                    afterTexts.Count > 0 ? afterTexts.ToArray() : null,
                    string.Join("；", changes.Distinct()));
            }

            default:
                return null;
        }
    }

    private static double EffectiveTolerance(RegisterLocatorOptions options) =>
        Math.Max(Math.Max(0, options.Tolerance), Math.Abs(options.Target) * Math.Max(0, options.TolerancePercent) / 100.0);

    /// <summary>
    /// 逐轮把寄存器解释为数值。任一轮缺字节、非有限值或数值明显不合理即视为不匹配
    /// （规划 4.4.6 的误判控制）。
    /// </summary>
    private static bool TryReadSeries(IReadOnlyList<IReadOnlyDictionary<int, ushort>> snapshots, int address,
        int registerCount, DataType type, ByteOrder order, double scale, bool excludeNonFinite, out double[] series)
    {
        series = new double[snapshots.Count];
        for (int i = 0; i < snapshots.Count; i++)
        {
            var registers = ToRegisters(snapshots[i], address, registerCount);
            if (registers.Length < registerCount) return false;
            if (!DataDecoder.TryDecodeNumber(registers, type, order, scale, 0, out var value)) return false;
            if (excludeNonFinite && (!double.IsFinite(value) || Math.Abs(value) > 1e15)) return false;
            series[i] = value;
        }
        return true;
    }

    private static ushort[] ToRegisters(IReadOnlyDictionary<int, ushort> map, int address, int count)
    {
        var registers = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            if (!map.TryGetValue(address + i, out var value)) return Array.Empty<ushort>();
            registers[i] = value;
        }
        return registers;
    }

    private static string RawHex(IReadOnlyDictionary<int, ushort> map, int address, int count)
    {
        var parts = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            parts.Add(map.TryGetValue(address + i, out var value) ? $"0x{value:X4}" : "-");
        }
        return string.Join(' ', parts);
    }

    private static string ValueText(IReadOnlyDictionary<int, ushort> map, int address, int count, DataType type,
        ByteOrder order, double scale)
    {
        var registers = ToRegisters(map, address, count);
        return registers.Length < count ? string.Empty : DataDecoder.Decode(registers, type, order, scale);
    }

    /// <summary>从指定地址开始读取最长的可打印 ASCII 片段；只有片段起点地址才算命中，避免重叠重复。</summary>
    private static bool TryReadTextRun(IReadOnlyDictionary<int, ushort> map, int address, int end, out string fragment,
        out int runLength)
    {
        fragment = string.Empty;
        runLength = 0;

        // 上一个寄存器的低字节可打印，说明当前地址不是片段起点。
        if (map.TryGetValue(address - 1, out var previous) && IsPrintable((byte)(previous & 0xFF))) return false;

        var text = new StringBuilder();
        for (int i = address; i <= end && map.TryGetValue(i, out var value); i++)
        {
            byte high = (byte)(value >> 8);
            byte low = (byte)(value & 0xFF);
            if (!IsPrintable(high)) break;
            text.Append((char)high);
            if (!IsPrintable(low)) break;
            text.Append((char)low);
        }

        if (text.Length < 2) return false;
        fragment = text.ToString();
        runLength = (fragment.Length + 1) / 2;
        return true;
    }

    private static bool IsPrintable(byte value) => value is >= 0x20 and < 0x7F;

    private static string Format(double value) => value.ToString("G9", CultureInfo.InvariantCulture);

    private readonly record struct MatchResult(int Score, double? Value, string[]? Before, string[]? After, string Reason);

    /// <summary>把数据扫描的进度回调改写成“寄存器定位”的任务名（不改变底层请求行为）。</summary>
    private sealed class TaskNameProgress : IProgress<ScanProgress>
    {
        private readonly IProgress<ScanProgress> _inner;
        private readonly string _taskName;

        public TaskNameProgress(IProgress<ScanProgress> inner, string taskName)
        {
            _inner = inner;
            _taskName = taskName;
        }

        public void Report(ScanProgress value) =>
            _inner.Report(value with { TaskName = _taskName, CurrentStep = "采样：" + value.CurrentStep });
    }

    /// <summary>导出候选 CSV；格式与设备/数据扫描导出一致（见规划 6.6）。</summary>
    public static string ExportCsv(string path, IReadOnlyList<RegisterCandidate> candidates, string? filter = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            writer.WriteLine("通讯测试工具 寄存器定位候选结果");
            writer.WriteLine($"导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            if (!string.IsNullOrEmpty(filter)) writer.WriteLine($"定位条件：{filter}");
            writer.WriteLine("协议地址,数据类型,字节序,缩放,原始寄存器,解析值,匹配值,匹配分数,说明,变化前,变化后");
            foreach (var candidate in candidates)
            {
                writer.WriteLine(string.Join(',',
                    candidate.Address,
                    candidate.Type.ToChinese(),
                    candidate.Order.ToDisplay(),
                    candidate.Scale.ToString("G", CultureInfo.InvariantCulture),
                    candidate.RawHex,
                    Csv(candidate.ValueText),
                    candidate.MatchedValue?.ToString("G9", CultureInfo.InvariantCulture) ?? string.Empty,
                    candidate.MatchScore,
                    Csv(candidate.Reason),
                    Csv(string.Join('|', candidate.BeforeValues ?? Array.Empty<string>())),
                    Csv(string.Join('|', candidate.AfterValues ?? Array.Empty<string>()))));
            }
            return string.Empty;
        }
        catch (Exception ex)
        {
            return $"导出失败：{ex.Message}";
        }
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
}
