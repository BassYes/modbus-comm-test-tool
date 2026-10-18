using Comm.Core.Errors;
using Comm.Core.Transport;
using Comm.Protocols.Modbus;

namespace Comm.Scanning;

/// <summary>串口参数的取值来源（规划 4.2.6 / 22.3.2）。</summary>
public enum SerialParameterSource
{
    /// <summary>使用连接中心当前参数（不遍历）。</summary>
    UseConnection,

    /// <summary>用户指定一个值（不遍历）。</summary>
    Specify,

    /// <summary>未知：把该维度加入遍历（波特率按候选集，校验位按 N/E/O）。</summary>
    Unknown
}

/// <summary>停止位策略（规划 22.3.2 高级选项）。</summary>
public enum StopBitsStrategy
{
    /// <summary>无校验时同时尝试 1 位与 2 位；有校验时只试 1 位。</summary>
    Auto,

    One,
    Two
}

/// <summary>一组完整的串口参数组合（数据位固定 8，Modbus RTU 要求）。</summary>
public sealed record SerialCombination(int BaudRate, SerialParity Parity, SerialStopBits StopBits)
{
    /// <summary>形如 <c>19200-8-E-1</c>。</summary>
    public string Text => $"{BaudRate}-8-{SerialSettings.ParityCode(Parity)}-{SerialSettings.StopBitsCode(StopBits)}";

    public int StopBitCount => StopBits == SerialStopBits.Two ? 2 : 1;

    public SerialSettings ToSettings(string portName) => new()
    {
        PortName = portName,
        BaudRate = BaudRate,
        DataBits = 8,
        Parity = Parity,
        StopBits = StopBits
    };
}

/// <summary>候选波特率的默认集合与“常用优先”顺序（规划 D-11、22.3.3）。</summary>
public static class SerialParameterCandidates
{
    /// <summary>可选波特率（1200 ~ 115200）。</summary>
    public static readonly IReadOnlyList<int> BaudRates = new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };

    /// <summary>候选校验位（数据位固定 8）。</summary>
    public static readonly IReadOnlyList<SerialParity> Parities = new[]
    {
        SerialParity.None, SerialParity.Even, SerialParity.Odd
    };

    /// <summary>“常用优先”的 (波特率, 校验位) 前置顺序。</summary>
    public static readonly IReadOnlyList<(int BaudRate, SerialParity Parity)> CommonFirst = new[]
    {
        (9600, SerialParity.None),
        (9600, SerialParity.Even),
        (19200, SerialParity.None),
        (19200, SerialParity.Even),
        (4800, SerialParity.None),
        (38400, SerialParity.None),
        (115200, SerialParity.None)
    };

    /// <summary>阶段 A 的默认探测地址：1～10 与 247（规划 22.3.2 高级选项默认值）。</summary>
    public static readonly IReadOnlyList<byte> DefaultPhaseAAddresses =
        new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 247 };
}

/// <summary>参数扫描的两阶段耗时估计（规划 22.3.2 的“预计耗时”）。</summary>
public sealed record ParameterScanEstimate(
    int CombinationCount,
    int PhaseAAddressCount,
    int FullAddressCount,
    int ProbeTemplateCount,
    TimeSpan PhaseADuration,
    TimeSpan PhaseBDuration)
{
    public TimeSpan Total => PhaseADuration + PhaseBDuration;

    public bool RequiresConfirmation { get; init; }

    public string ToText() =>
        $"组合 {CombinationCount} 组 × 阶段 A {PhaseAAddressCount} 个地址 × 超时；" +
        $"阶段 A 最坏 {PhaseADuration.TotalMinutes:F1} 分钟，" +
        $"阶段 B 最坏 {PhaseBDuration.TotalMinutes:F1} 分钟（{CombinationCount} 组 × {FullAddressCount} 地址 × {ProbeTemplateCount} 个模板），" +
        $"合计约 {Total.TotalMinutes:F1} 分钟" +
        (RequiresConfirmation ? "；已超过 10 分钟，开始前需要二次确认" : string.Empty);
}

/// <summary>
/// 设备扫描页的“串口参数扫描”选项（R-11，规划 4.2.6 / 22.3）。
/// <para>它同时描述“要不要遍历”和“阶段 A/B 怎么扫”，因此继承并复用了 <see cref="DeviceScanOptions"/> 的地址与超时语义。</para>
/// </summary>
public sealed record ParameterScanOptions
{
    // ---- 地址范围与通信（与普通设备扫描一致）
    public byte StartAddress { get; init; } = 1;
    public byte EndAddress { get; init; } = 247;
    public int ResponseTimeoutMs { get; init; } = 500;
    public int RetryCount { get; init; }
    public int IntervalMs { get; init; } = 20;
    public bool TryReadDeviceIdentification { get; init; } = true;

    // ---- 串口参数来源
    public SerialParameterSource BaudRateSource { get; init; } = SerialParameterSource.UseConnection;
    public SerialParameterSource ParitySource { get; init; } = SerialParameterSource.UseConnection;

    /// <summary>“使用连接中心参数”时用于补齐的当前值。</summary>
    public int CurrentBaudRate { get; init; } = 9600;
    public SerialParity CurrentParity { get; init; } = SerialParity.None;
    public SerialStopBits CurrentStopBits { get; init; } = SerialStopBits.One;

    public int SpecifiedBaudRate { get; init; } = 9600;
    public SerialParity SpecifiedParity { get; init; } = SerialParity.None;

    // ---- 遍历候选集
    public IReadOnlyList<int> CandidateBaudRates { get; init; } = SerialParameterCandidates.BaudRates;
    public bool CommonFirst { get; init; } = true;
    public StopBitsStrategy StopBits { get; init; } = StopBitsStrategy.Auto;

    /// <summary>用户已知的地址（优先用于阶段 A）。为空时使用 <see cref="PhaseAAddresses"/> 或默认集合。</summary>
    public IReadOnlyList<byte>? KnownAddresses { get; init; }

    /// <summary>阶段 A 的自定义探测地址；为空时用 <see cref="KnownAddresses"/>，再为空用默认 `1～10,247`。</summary>
    public IReadOnlyList<byte>? PhaseAAddresses { get; init; }

    // ---- 阶段 B 的探测方式（阶段 A 固定只发一次 03 只读探测，见规划 22.3.3）
    public DeviceProbeMode ProbeMode { get; init; } = DeviceProbeMode.AutoTemplate;
    public byte ManualProbeFunction { get; init; } = Fn.ReadHoldingRegisters;
    public ushort ManualProbeAddress { get; init; }
    public ushort ManualProbeQuantity { get; init; } = 1;

    /// <summary>命中后是否继续遍历其余组合（默认否）。</summary>
    public bool ContinueAfterHit { get; init; }

    /// <summary>更换参数后的稳定等待（USB 转串口重开通常需要 100～500 ms）。</summary>
    public int ReconfigureSettleMs { get; init; } = 150;

    /// <summary>命中后的额外确认次数；2 表示“1 次首探 + 2 次确认 = 3/3”。</summary>
    public int ConfirmAttempts { get; init; } = 2;

    /// <summary>超过该分钟数需要二次确认。</summary>
    public int ConfirmationThresholdMinutes { get; init; } = 10;

    public int TotalAddresses => EndAddress - StartAddress + 1;

    /// <summary>是否需要遍历串口参数（任一维度选“未知”）。</summary>
    public bool IsParameterScan =>
        BaudRateSource == SerialParameterSource.Unknown || ParitySource == SerialParameterSource.Unknown;

    /// <summary>本次参与遍历的波特率集合。</summary>
    public IReadOnlyList<int> EffectiveBaudRates()
    {
        switch (BaudRateSource)
        {
            case SerialParameterSource.Specify:
                return new[] { SpecifiedBaudRate };
            case SerialParameterSource.UseConnection:
                return new[] { CurrentBaudRate };
            default:
            {
                var allowed = SerialParameterCandidates.BaudRates;
                var picked = CandidateBaudRates.Where(allowed.Contains).Distinct().ToArray();
                return picked.Length > 0 ? picked : allowed;
            }
        }
    }

    /// <summary>本次参与遍历的校验位集合。</summary>
    public IReadOnlyList<SerialParity> EffectiveParities() => ParitySource switch
    {
        SerialParameterSource.Specify => new[] { SpecifiedParity },
        SerialParameterSource.UseConnection => new[] { CurrentParity },
        _ => SerialParameterCandidates.Parities
    };

    /// <summary>某个校验位下需要尝试的停止位集合。</summary>
    public IReadOnlyList<SerialStopBits> EffectiveStopBits(SerialParity parity) => StopBits switch
    {
        StopBitsStrategy.One => new[] { SerialStopBits.One },
        StopBitsStrategy.Two => new[] { SerialStopBits.Two },
        // 自动：无校验时 1 位与 2 位都试；有校验时按规划只试 1 位
        _ => parity == SerialParity.None
            ? new[] { SerialStopBits.One, SerialStopBits.Two }
            : new[] { SerialStopBits.One }
    };

    /// <summary>阶段 A 的探测地址集合。</summary>
    public IReadOnlyList<byte> PhaseATargets()
    {
        var source = PhaseAAddresses is { Count: > 0 } ? PhaseAAddresses
            : KnownAddresses is { Count: > 0 } ? KnownAddresses
            : SerialParameterCandidates.DefaultPhaseAAddresses;

        var picked = source.Where(a => a is >= 1 and <= 247).Distinct().ToArray();
        return picked.Length > 0 ? picked : SerialParameterCandidates.DefaultPhaseAAddresses;
    }

    /// <summary>
    /// 生成待遍历的参数组合（规划 22.3.3）：“常用优先”把常见组合排到前面，其余按波特率升序补齐。
    /// </summary>
    public IReadOnlyList<SerialCombination> BuildCombinations()
    {
        var bauds = EffectiveBaudRates();
        var parities = EffectiveParities();

        var all = new List<SerialCombination>();
        foreach (var baud in bauds.OrderBy(b => b))
        {
            foreach (var parity in parities)
            {
                foreach (var stopBits in EffectiveStopBits(parity))
                {
                    all.Add(new SerialCombination(baud, parity, stopBits));
                }
            }
        }

        if (!CommonFirst) return all;

        var ordered = new List<SerialCombination>();
        foreach (var (baud, parity) in SerialParameterCandidates.CommonFirst)
        {
            ordered.AddRange(all.Where(c => c.BaudRate == baud && c.Parity == parity)
                .OrderBy(c => c.StopBitCount));
        }
        ordered.AddRange(all.Where(c => !ordered.Contains(c)));
        return ordered;
    }

    /// <summary>耗时估计（规划 22.3.2/22.3.3）。阶段 A 每组每地址只发一次只读探测，阶段 B 用完整模板。</summary>
    public ParameterScanEstimate Estimate(int probeTemplateCount = 4)
    {
        var combinations = Math.Max(1, BuildCombinations().Count);
        var phaseA = Math.Max(1, PhaseATargets().Count);
        int timeout = Math.Max(50, ResponseTimeoutMs);

        var phaseADuration = TimeSpan.FromMilliseconds((double)combinations * phaseA * (timeout + IntervalMs));
        var phaseBDuration = TimeSpan.FromMilliseconds(
            (double)combinations * TotalAddresses * Math.Max(1, probeTemplateCount) * (timeout + IntervalMs));

        return new ParameterScanEstimate(combinations, phaseA, TotalAddresses, Math.Max(1, probeTemplateCount),
            phaseADuration, phaseBDuration)
        {
            RequiresConfirmation = phaseADuration + phaseBDuration > TimeSpan.FromMinutes(ConfirmationThresholdMinutes)
        };
    }

    /// <summary>把参数扫描选项降级为某一组合上的普通设备扫描选项。</summary>
    /// <param name="forPhaseA">true = 阶段 A（固定一次 03 只读探测，不读设备标识、不额外确认）；false = 阶段 B（按用户配置的探测方式扫完整地址段）。</param>
    public DeviceScanOptions ToDeviceScanOptions(SerialCombination combination, bool forPhaseA)
    {
        var mode = forPhaseA ? DeviceProbeMode.Manual : ProbeMode;
        var function = forPhaseA ? Fn.ReadHoldingRegisters : ManualProbeFunction;
        var address = forPhaseA ? (ushort)0 : ManualProbeAddress;
        var quantity = forPhaseA ? (ushort)1 : ManualProbeQuantity;

        return new DeviceScanOptions
        {
            StartAddress = StartAddress,
            EndAddress = EndAddress,
            // F-5：响应超时必须不小于“一帧在该波特率下的传输时间 + 设备处理余量”。
            ResponseTimeoutMs = RtuTiming.EffectiveResponseTimeoutMs(ResponseTimeoutMs, combination.BaudRate),
            RetryCount = RetryCount,
            IntervalMs = IntervalMs,
            TryReadDeviceIdentification = !forPhaseA && TryReadDeviceIdentification,
            Mode = mode,
            ManualProbeFunction = function,
            ManualProbeAddress = address,
            ManualProbeQuantity = quantity,
            ConfirmAttempts = forPhaseA ? 0 : Math.Max(0, ConfirmAttempts)
        };
    }

    /// <summary>参数自检（预期内的输入问题用值表达，不抛异常）。</summary>
    public static bool Validate(ParameterScanOptions options, out ErrorInfo? error)
    {
        error = null;

        if (options.EndAddress < options.StartAddress)
        {
            error = new ErrorInfo(ErrorCodes.ValidationAddressRange, ErrorCategory.Validation, Severity.Warning,
                "结束地址必须大于或等于起始地址");
            return false;
        }

        if (options.ResponseTimeoutMs < 50)
        {
            error = new ErrorInfo(ErrorCodes.ValidationQuantity, ErrorCategory.Validation, Severity.Warning,
                "响应超时不能小于 50 ms");
            return false;
        }

        if (options.BaudRateSource == SerialParameterSource.Unknown)
        {
            var allowed = SerialParameterCandidates.BaudRates;
            var picked = options.CandidateBaudRates.Where(allowed.Contains).Distinct().ToArray();
            if (picked.Length == 0)
            {
                error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                    "波特率选“未知”时至少要勾选一个候选波特率");
                return false;
            }
        }

        if (options.BaudRateSource == SerialParameterSource.Specify
            && !SerialParameterCandidates.BaudRates.Contains(options.SpecifiedBaudRate))
        {
            error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                $"指定的波特率 {options.SpecifiedBaudRate} 不在支持的范围内（1200～115200）");
            return false;
        }

        if (options.ReconfigureSettleMs is < 0 or > 5000)
        {
            error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                "参数更换稳定等待应在 0～5000 ms 之间");
            return false;
        }

        if (options.ConfirmAttempts is < 0 or > 5)
        {
            error = new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                "确认次数应在 0～5 之间（0 表示不额外确认）");
            return false;
        }

        return true;
    }
}

/// <summary>
/// 设备扫描方案的持久化负载（R-11，规划 22.3.4 第 6 点）。
/// <para>
/// 旧方案（M2 及以前）直接保存的是 <see cref="DeviceScanOptions"/> 的扁平形状；
/// 读取时先按本类型解析，若 <see cref="Parameters"/> 为空则再按旧形状解析，
/// 因此**不需要升 schemaVersion**，旧方案缺字段时默认“使用连接中心参数”。
/// </para>
/// </summary>
public sealed record DeviceScanPlan
{
    public DeviceScanOptions Scan { get; init; } = new();

    /// <summary>R-11 串口参数扫描设置；旧方案为 null。</summary>
    public ParameterScanOptions? Parameters { get; init; }
}
