using System.Globalization;
using Comm.Core.Errors;
using Comm.Core.Tasks;
using Comm.Protocols.Modbus;
using Comm.Session;

namespace Comm.Scanning;

public enum DeviceOnlineStatus
{
    Online,
    Suspected,
    Offline
}

/// <summary>设备扫描的探测方式（见规划 4.2.2）。</summary>
public enum DeviceProbeMode
{
    /// <summary>自动模板：03 → 04 → 01 → 02，起始地址 0、数量 1（规划 D-3）。</summary>
    AutoTemplate,

    /// <summary>手动指定只读功能码、起始地址与数量。</summary>
    Manual
}

/// <summary>单个探测模板。</summary>
public sealed record DeviceProbeTemplate(byte Function, ushort Address, ushort Quantity)
{
    public DataArea Area => DeviceScanOptions.AreaOf(Function);

    public string Text => $"0x{Function:X2} {Fn.Name(Function)} 起始 {Address} 数量 {Quantity}";
}

public sealed record DeviceScanOptions
{
    public byte StartAddress { get; init; } = 1;
    public byte EndAddress { get; init; } = 247;

    /// <summary>单次探测超时；空地址必然超时，重试会成倍放大扫描时间（见规划 15.5）。</summary>
    public int ResponseTimeoutMs { get; init; } = 500;

    public int RetryCount { get; init; }
    public int IntervalMs { get; init; } = 20;

    /// <summary>发现后再做一次二次确认（等价于 <see cref="ConfirmAttempts"/> = 1）。</summary>
    public bool ConfirmFound { get; init; }

    /// <summary>
    /// 发现后的额外确认次数（R-11 阶段 B 用 2 → 首探 + 2 次确认 = 3/3 全通过才算在线）。
    /// 与 <see cref="ConfirmFound"/> 取较大值生效。
    /// </summary>
    public int ConfirmAttempts { get; init; }

    /// <summary>尝试读取设备标识（功能码 43/14），可得到厂商与型号。</summary>
    public bool TryReadDeviceIdentification { get; init; } = true;

    /// <summary>探测方式。</summary>
    public DeviceProbeMode Mode { get; init; } = DeviceProbeMode.AutoTemplate;

    /// <summary>手动探测功能码；仅允许只读功能码 01/02/03/04。默认不调用（见 R-17）。</summary>
    public byte ManualProbeFunction { get; init; } = Fn.ReadHoldingRegisters;

    public ushort ManualProbeAddress { get; init; }
    public ushort ManualProbeQuantity { get; init; } = 1;

    public int TotalAddresses => EndAddress - StartAddress + 1;

    /// <summary>实际生效的额外确认次数。</summary>
    public int EffectiveConfirmAttempts =>
        Math.Clamp(Math.Max(ConfirmAttempts, ConfirmFound ? 1 : 0), 0, 5);

    /// <summary>把探测功能码映射回数据区（只接受四个标准读功能码）。</summary>
    public static DataArea AreaOf(byte function) => function switch
    {
        Fn.ReadCoils => DataArea.Coils,
        Fn.ReadDiscreteInputs => DataArea.DiscreteInputs,
        Fn.ReadInputRegisters => DataArea.InputRegisters,
        _ => DataArea.HoldingRegisters
    };

    /// <summary>本次扫描实际使用的探测模板。手动模式只有 1 个模板，因此耗时与自动模式差别很大。</summary>
    public IReadOnlyList<DeviceProbeTemplate> ProbeTemplates() => Mode == DeviceProbeMode.Manual
        ? new[] { new DeviceProbeTemplate(ManualProbeFunction, ManualProbeAddress, ManualProbeQuantity) }
        : new[]
        {
            new DeviceProbeTemplate(Fn.ReadHoldingRegisters, 0, 1),
            new DeviceProbeTemplate(Fn.ReadInputRegisters, 0, 1),
            new DeviceProbeTemplate(Fn.ReadCoils, 0, 1),
            new DeviceProbeTemplate(Fn.ReadDiscreteInputs, 0, 1)
        };

    /// <summary>
    /// 参数自检（预期内的输入问题用值表达，不抛异常）。
    /// **写功能码与广播地址在服务层就被拒绝**——不能只靠下拉框隐藏（CC-6、R-17）。
    /// </summary>
    public static bool Validate(DeviceScanOptions options, out ErrorInfo? error)
    {
        error = null;

        if (options.EndAddress < options.StartAddress)
        {
            error = new ErrorInfo(ErrorCodes.ValidationAddressRange, ErrorCategory.Validation, Severity.Warning,
                "结束地址必须大于或等于起始地址");
            return false;
        }

        if (options.Mode != DeviceProbeMode.Manual) return true;

        if (Fn.IsWrite(options.ManualProbeFunction) || !Fn.IsRead(options.ManualProbeFunction))
        {
            error = new ErrorInfo(ErrorCodes.ValidationScanWriteBlocked, ErrorCategory.Validation, Severity.Error,
                $"扫描任务不允许使用功能码 0x{options.ManualProbeFunction:X2}，只能选择只读功能码 01/02/03/04",
                Hint: "写操作请在“报文工具”中手工发送，并完成二次确认");
            return false;
        }

        if (options.ManualProbeFunction is not (Fn.ReadCoils or Fn.ReadDiscreteInputs or Fn.ReadHoldingRegisters or Fn.ReadInputRegisters))
        {
            error = new ErrorInfo(ErrorCodes.ValidationScanWriteBlocked, ErrorCategory.Validation, Severity.Error,
                $"手动探测只支持 01/02/03/04，不支持 0x{options.ManualProbeFunction:X2}");
            return false;
        }

        int max = Fn.MaxQuantity(options.ManualProbeFunction);
        if (options.ManualProbeQuantity < 1 || options.ManualProbeQuantity > max)
        {
            error = new ErrorInfo(ErrorCodes.ValidationQuantity, ErrorCategory.Validation, Severity.Warning,
                $"数量必须在 1 ~ {max} 之间（功能码 0x{options.ManualProbeFunction:X2} 的协议上限）");
            return false;
        }

        if ((int)options.ManualProbeAddress + options.ManualProbeQuantity - 1 > 65535)
        {
            error = new ErrorInfo(ErrorCodes.ValidationAddressRange, ErrorCategory.Validation, Severity.Warning,
                "起始地址 + 数量超出了 65535 的地址空间");
            return false;
        }

        return true;
    }

    public TimeSpan EstimatedDuration()
    {
        // 无设备时每个地址会依次尝试全部模板并超时，这是最坏情况。
        double perAddress = ProbeTemplates().Count * (double)ResponseTimeoutMs + IntervalMs;
        return TimeSpan.FromMilliseconds(perAddress * TotalAddresses);
    }
}

public sealed record DeviceScanItem(
    byte Address,
    DeviceOnlineStatus Status,
    byte ProbeFunction,
    byte? ExceptionCode,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    double ElapsedMs,
    string? DeviceIdentification,
    string Note,
    SerialCombination? Combination = null,
    bool Recommended = false,
    int NoiseFrames = 0,
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

    // ---- R-11 结果表新增列（规划 4.2.6）：波特率 / 校验 / 停止位
    public string BaudRateText => Combination is null ? string.Empty : Combination.BaudRate.ToString(CultureInfo.InvariantCulture);

    public string ParityText => Combination is null ? string.Empty : Comm.Core.Transport.SerialSettings.ParityCode(Combination.Parity).ToString();

    public string StopBitsText => Combination is null
        ? string.Empty
        : Comm.Core.Transport.SerialSettings.StopBitsCode(Combination.StopBits);

    public string CombinationText => Combination?.Text ?? string.Empty;

    /// <summary>“推荐”标记的说明文本（多个组合同时命中时由用户确认，见规划 22.3.3）。</summary>
    public string RecommendationText => Recommended ? "推荐" : string.Empty;
}

/// <summary>串口/TCP 从站地址扫描（见规划 4.2、14.6.1）。默认只使用读功能码，绝不使用写功能码与广播。</summary>
public sealed class DeviceScanService
{
    private readonly ModbusSession _session;

    public DeviceScanService(ModbusSession session)
    {
        _session = session;
    }

    public async Task<IReadOnlyList<DeviceScanItem>> ScanAsync(DeviceScanOptions options, PauseGate? gate,
        IProgress<ScanProgress>? progress, IProgress<DeviceScanItem>? itemProgress, CancellationToken cancellationToken,
        SerialCombination? combination = null)
    {
        // 服务层门禁：即使界面被绕过，扫描任务也不能出现写功能码或非法数量（CC-6）。
        if (!DeviceScanOptions.Validate(options, out var error)) throw new CommException(error!);

        var results = new List<DeviceScanItem>();
        int total = options.TotalAddresses;
        int completed = 0;
        var taskName = combination is null ? "设备扫描" : $"设备扫描 {combination.Text}";
        var comboHint = combination is null ? string.Empty : $"{combination.Text} ";

        progress?.Report(new ScanProgress(taskName, 0, total, $"{comboHint}开始", ScanStatus.Running));

        for (int address = options.StartAddress; address <= options.EndAddress; address++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (gate is not null)
            {
                if (gate.IsPaused) progress?.Report(new ScanProgress(taskName, completed, total, $"{comboHint}已暂停", ScanStatus.Paused));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var item = await ProbeAddressAsync((byte)address, options, cancellationToken, combination).ConfigureAwait(false);
            results.Add(item);
            itemProgress?.Report(item);

            completed++;
            progress?.Report(new ScanProgress(taskName, completed, total, $"{comboHint}地址 {address}", ScanStatus.Running));

            if (options.IntervalMs > 0)
                await Task.Delay(options.IntervalMs, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ScanProgress(taskName, total, total, $"{comboHint}完成", ScanStatus.Completed));
        return results;
    }

    /// <summary>
    /// 探测单个地址（R-11 阶段 A 直接复用本方法：只发一次只读探测）。
    /// 返回结果：<see cref="DeviceOnlineStatus.Online"/> 或 <see cref="DeviceOnlineStatus.Suspected"/>
    /// 都表示“该参数组合上有东西响应”，可用于判定命中。
    /// </summary>
    public async Task<DeviceScanItem> ProbeAddressAsync(byte address, DeviceScanOptions options,
        CancellationToken cancellationToken, SerialCombination? combination = null)
    {
        if (!DeviceScanOptions.Validate(options, out var error)) throw new CommException(error!);

        var templates = options.ProbeTemplates();
        byte lastFunction = templates[0].Function;
        byte? lastException = null;
        double lastElapsed = 0;
        int noiseFrames = 0;
        var first = DateTimeOffset.Now;
        var last = first;

        foreach (var template in templates)
        {
            lastFunction = template.Function;
            var pdu = ModbusRequests.Read(template.Area, template.Address, template.Quantity);
            var request = ModbusRequest.Scan(address, pdu, label: $"设备扫描 地址 {address}", timeoutMs: options.ResponseTimeoutMs);

            var result = await _session.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            lastElapsed = result.Elapsed.TotalMilliseconds;
            last = DateTimeOffset.Now;

            switch (result.Status)
            {
                case ExchangeStatus.Ok:
                {
                    string? identification = options.TryReadDeviceIdentification
                        ? await TryReadIdentificationAsync(address, options, cancellationToken).ConfigureAwait(false)
                        : null;

                    int extra = options.EffectiveConfirmAttempts;
                    int confirmTotal = 1 + extra;
                    int confirmOk = 1;

                    for (int i = 0; i < extra; i++)
                    {
                        var confirm = await _session.ExecuteAsync(
                            ModbusRequest.Scan(address, pdu, timeoutMs: options.ResponseTimeoutMs), cancellationToken)
                            .ConfigureAwait(false);

                        if (confirm.Status == ExchangeStatus.Ok)
                        {
                            confirmOk++;
                            continue;
                        }

                        return new DeviceScanItem(address, DeviceOnlineStatus.Suspected, lastFunction, null, first, last,
                            lastElapsed, identification,
                            $"首次响应正常，但第 {i + 2} 次确认未通过（{confirm.StatusText}）",
                            combination, false, noiseFrames, confirmOk, confirmTotal);
                    }

                    var note = confirmTotal > 1
                        ? $"读取成功（{confirmOk}/{confirmTotal} 次确认全部通过）"
                        : "读取成功";

                    return new DeviceScanItem(address, DeviceOnlineStatus.Online, lastFunction, null, first, last,
                        lastElapsed, identification, note, combination, false, noiseFrames, confirmOk, confirmTotal);
                }

                case ExchangeStatus.ModbusException:
                    lastException = result.ExceptionCode;
                    return new DeviceScanItem(address, DeviceOnlineStatus.Online, lastFunction, lastException, first, last,
                        lastElapsed, null,
                        $"设备在线，但探测地址不受支持：{ModbusExceptions.Describe(lastException ?? 0)}",
                        combination, false, noiseFrames, 1, 1);

                case ExchangeStatus.ChecksumError:
                    noiseFrames++;
                    continue;

                case ExchangeStatus.Cancelled:
                    throw new OperationCanceledException(cancellationToken);

                default:
                    continue;
            }
        }

        return noiseFrames > 0
            ? new DeviceScanItem(address, DeviceOnlineStatus.Suspected, lastFunction, lastException, first, last,
                lastElapsed, null, $"收到 {noiseFrames} 个校验错误的帧，疑似线上有设备但参数不完全匹配；建议核对串口参数与线路",
                combination, false, noiseFrames, 0, 1)
            : new DeviceScanItem(address, DeviceOnlineStatus.Offline, lastFunction, lastException, first, last,
                lastElapsed, null, templates.Count == 1
                    ? "探测无响应（手动模板）"
                    : "全部探测模板均无响应", combination, false, 0, 0, 1);
    }

    /// <summary>读设备标识（功能码 43 / MEI 14，对象 0）。</summary>
    private async Task<string?> TryReadIdentificationAsync(byte address, DeviceScanOptions options, CancellationToken cancellationToken)
    {
        var pdu = ModbusPdu.Raw(Fn.ReadDeviceIdentification, 0x0E, 0x01, 0x00);
        var result = await _session.ExecuteAsync(
            ModbusRequest.Scan(address, pdu, timeoutMs: options.ResponseTimeoutMs), cancellationToken).ConfigureAwait(false);

        if (result.Status != ExchangeStatus.Ok || result.Response is null) return null;

        try
        {
            var bytes = result.Response;
            int start = _session.Codec.ChecksumLength == 0 ? 7 : 1;
            int end = bytes.Length - _session.Codec.ChecksumLength;
            if (end - start < 7) return null;

            // [fc][mei][readDeviceId][conformity][more][nextObj][count][objects...]
            int index = start + 6;
            int count = bytes[index];
            index++;
            var parts = new List<string>();

            for (int i = 0; i < count && index + 2 <= end; i++)
            {
                byte objectId = bytes[index];
                int length = bytes[index + 1];
                index += 2;
                if (index + length > end) break;
                var text = System.Text.Encoding.ASCII.GetString(bytes, index, length).Trim('\0', ' ');
                index += length;

                var name = objectId switch
                {
                    0x00 => "厂商",
                    0x01 => "产品代码",
                    0x02 => "版本",
                    0x03 => "厂商网址",
                    0x04 => "产品名称",
                    0x05 => "型号",
                    0x06 => "应用名称",
                    _ => $"对象{objectId}"
                };
                if (text.Length > 0) parts.Add($"{name}={text}");
            }

            return parts.Count > 0 ? string.Join("；", parts) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>导出扫描结果 TXT（人可读；M1 欠账 D-1：原来对话框给了 TXT 选项却只写 CSV）。</summary>
    public static string ExportText(string path, IReadOnlyList<DeviceScanItem> items, string? filter = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
            writer.WriteLine("通讯测试工具 设备扫描结果");
            writer.WriteLine($"导出时间：{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
            if (!string.IsNullOrEmpty(filter)) writer.WriteLine($"扫描参数：{filter}");
            writer.WriteLine(new string('-', 100));
            foreach (var item in items)
            {
                writer.WriteLine($"地址 {item.Address,3} | {item.StatusText,-4} | {item.ProbeFunctionText}" +
                                 (item.ExceptionCode is { } code ? $" | 异常码 0x{code:X2}" : string.Empty) +
                                 $" | {item.ElapsedMs.ToString("F1", CultureInfo.InvariantCulture)} ms" +
                                 (item.Combination is null ? string.Empty : $" | {item.Combination.Text}") +
                                 (item.Recommended ? " | 推荐" : string.Empty));
                if (!string.IsNullOrEmpty(item.DeviceIdentification)) writer.WriteLine($"    设备标识：{item.DeviceIdentification}");
                writer.WriteLine($"    说明：{item.Note}");
            }
            return string.Empty;
        }
        catch (Exception ex)
        {
            return $"导出失败：{ex.Message}";
        }
    }

    public static string ExportCsv(string path, IReadOnlyList<DeviceScanItem> items, string? filter = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
            writer.WriteLine("通讯测试工具 设备扫描结果");
            writer.WriteLine($"导出时间：{DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)}");
            if (!string.IsNullOrEmpty(filter)) writer.WriteLine($"扫描参数：{filter}");

            // R-21 X-5：导出固定用 InvariantCulture，避免 de-DE 等区域把 0.1 写成 0,1 破坏 CSV。
            writer.WriteLine("从站地址,波特率,校验,停止位,状态,探测功能码,异常码,首次发现,最近响应,耗时ms,推荐,设备标识,说明");
            foreach (var item in items)
            {
                writer.WriteLine(string.Join(',',
                    item.Address.ToString(CultureInfo.InvariantCulture),
                    item.BaudRateText,
                    item.ParityText,
                    item.StopBitsText,
                    item.StatusText,
                    item.ProbeFunctionText,
                    item.ExceptionCode is { } code ? $"0x{code:X2}" : string.Empty,
                    item.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    item.LastSeen.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    item.ElapsedMs.ToString("F1", CultureInfo.InvariantCulture),
                    item.RecommendationText,
                    Quote(item.DeviceIdentification ?? string.Empty),
                    Quote(item.Note)));
            }
            return string.Empty;
        }
        catch (Exception ex)
        {
            return $"导出失败：{ex.Message}";
        }
    }

    private static string Quote(string value) =>
        value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
