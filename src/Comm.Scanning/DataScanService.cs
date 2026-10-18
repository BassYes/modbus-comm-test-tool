using Comm.Core.Tasks;
using Comm.Protocols.Modbus;
using Comm.Session;

namespace Comm.Scanning;

public enum ScanItemStatus
{
    Ok,
    InvalidAddress,
    Timeout,
    Unsupported,
    Error
}

public sealed record DataScanOptions
{
    public byte UnitId { get; init; } = 1;
    public DataArea Area { get; init; } = DataArea.HoldingRegisters;
    public int StartAddress { get; init; } = 0;
    public int EndAddress { get; init; } = 99;

    /// <summary>每次请求点数；0 表示按协议上限自动拆分（见规划 14.4）。</summary>
    public int BatchSize { get; init; }

    public DataType Type { get; init; } = DataType.UInt16;
    public ByteOrder Order { get; init; } = ByteOrder.Abcd;
    public double Scale { get; init; } = 1.0;
    public double Offset { get; init; }

    public int ResponseTimeoutMs { get; init; } = 1000;
    public int RetryCount { get; init; }
    public int IntervalMs { get; init; } = 20;

    /// <summary>遇到非法数据地址时按二分细化，定位有效区间（见规划 4.3.5）。</summary>
    public bool RefineInvalidBlocks { get; init; } = true;

    public int TotalRegisters => Math.Max(0, EndAddress - StartAddress + 1);

    public int EffectiveBatchSize()
    {
        int protocolMax = Fn.MaxQuantity(Area.ReadFunctionCode());
        int size = BatchSize > 0 ? BatchSize : protocolMax;
        return Math.Max(1, Math.Min(size, protocolMax));
    }
}

public sealed record DataScanItem(
    DataArea Area,
    int Address,
    ushort? RawValue,
    string RawHex,
    string? ValueText,
    ScanItemStatus Status,
    byte? ExceptionCode,
    string Note,
    double ElapsedMs)
{
    public string StatusText => Status switch
    {
        ScanItemStatus.Ok => "正常",
        ScanItemStatus.InvalidAddress => "非法地址",
        ScanItemStatus.Timeout => "超时",
        ScanItemStatus.Unsupported => "不支持",
        _ => "错误"
    };

    public bool IsBitArea => Area.IsBitArea();
}

/// <summary>标准四类数据区扫描（见规划 4.3）。</summary>
public sealed class DataScanService
{
    private readonly ModbusSession _session;

    public DataScanService(ModbusSession session)
    {
        _session = session;
    }

    public async Task<IReadOnlyList<DataScanItem>> ScanAsync(DataScanOptions options, PauseGate? gate,
        IProgress<ScanProgress>? progress, IProgress<IReadOnlyList<DataScanItem>>? batchProgress,
        CancellationToken cancellationToken)
    {
        var results = new List<DataScanItem>();
        int batch = options.EffectiveBatchSize();
        int total = options.TotalRegisters;
        int completed = 0;
        int address = options.StartAddress;

        progress?.Report(new ScanProgress("数据扫描", 0, total, "开始", ScanStatus.Running));

        while (address <= options.EndAddress)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (gate is not null)
            {
                if (gate.IsPaused) progress?.Report(new ScanProgress("数据扫描", completed, total, "已暂停", ScanStatus.Paused));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            int count = Math.Min(batch, options.EndAddress - address + 1);
            var chunk = await ReadChunkAsync(options, address, count, cancellationToken).ConfigureAwait(false);

            results.AddRange(chunk);
            batchProgress?.Report(chunk);
            completed += count;
            address += count;

            progress?.Report(new ScanProgress("数据扫描", Math.Min(completed, total), total,
                $"地址 {address - count} ~ {address - 1}", ScanStatus.Running));

            if (options.IntervalMs > 0) await Task.Delay(options.IntervalMs, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new ScanProgress("数据扫描", total, total, "完成", ScanStatus.Completed));
        return results;
    }

    private async Task<List<DataScanItem>> ReadChunkAsync(DataScanOptions options, int address, int count,
        CancellationToken cancellationToken)
    {
        var pdu = ModbusRequests.Read(options.Area, (ushort)address, (ushort)count);
        var request = ModbusRequest.Scan(options.UnitId, pdu, timeoutMs: options.ResponseTimeoutMs);
        var result = await _session.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        switch (result.Status)
        {
            case ExchangeStatus.Ok when result.ResponsePdu is not null:
                return BuildItems(options, address, count, result);

            case ExchangeStatus.ModbusException:
            {
                byte code = result.ExceptionCode ?? 0;
                if (code == 0x02 && options.RefineInvalidBlocks && count > 1)
                {
                    // 二分细化：找出块内有效区间（见规划 4.3.5）。
                    int half = count / 2;
                    var left = await ReadChunkAsync(options, address, half, cancellationToken).ConfigureAwait(false);
                    var right = await ReadChunkAsync(options, address + half, count - half, cancellationToken).ConfigureAwait(false);
                    left.AddRange(right);
                    return left;
                }

                var status = code switch
                {
                    0x02 => ScanItemStatus.InvalidAddress,
                    0x01 => ScanItemStatus.Unsupported,
                    _ => ScanItemStatus.Error
                };

                var items = new List<DataScanItem>(count);
                for (int i = 0; i < count; i++) items.Add(Failed(options, address + i, status, code, result.Elapsed.TotalMilliseconds));
                return items;
            }

            case ExchangeStatus.Timeout:
            case ExchangeStatus.ChecksumError:
            {
                var items = new List<DataScanItem>(count);
                for (int i = 0; i < count; i++)
                {
                    items.Add(Failed(options, address + i, ScanItemStatus.Timeout, null, result.Elapsed.TotalMilliseconds));
                }
                return items;
            }

            default:
            {
                var items = new List<DataScanItem>(count);
                for (int i = 0; i < count; i++)
                    items.Add(Failed(options, address + i, ScanItemStatus.Error, result.ExceptionCode, result.Elapsed.TotalMilliseconds));
                return items;
            }
        }
    }

    private static List<DataScanItem> BuildItems(DataScanOptions options, int startAddress, int count,
        ExchangeResult result)
    {
        var items = new List<DataScanItem>(count);
        var parsed = result.ResponsePdu!;

        if (options.Area.IsBitArea())
        {
            var bits = parsed.Bits ?? Array.Empty<bool>();
            for (int i = 0; i < count; i++)
            {
                bool? value = i < bits.Length ? bits[i] : null;
                items.Add(new DataScanItem(options.Area, startAddress + i, value.HasValue ? (ushort)(value.Value ? 1 : 0) : null,
                    value.HasValue ? (value.Value ? "01" : "00") : "-",
                    value.HasValue ? (value.Value ? "1 (ON)" : "0 (OFF)") : null,
                    value.HasValue ? ScanItemStatus.Ok : ScanItemStatus.Error, null,
                    value.HasValue ? string.Empty : "响应位数据不完整", result.Elapsed.TotalMilliseconds));
            }
            return items;
        }

        var registers = parsed.Registers ?? Array.Empty<ushort>();
        int typeLength = options.Type.RegisterCount();

        for (int i = 0; i < count; i++)
        {
            if (i >= registers.Length)
            {
                items.Add(Failed(options, startAddress + i, ScanItemStatus.Error, null, result.Elapsed.TotalMilliseconds));
                continue;
            }

            ushort raw = registers[i];
            string valueText = string.Empty;

            if (i + typeLength <= registers.Length)
            {
                var span = registers.AsSpan(i, typeLength);
                valueText = DataDecoder.Decode(span, options.Type, options.Order, options.Scale, options.Offset);
            }

            items.Add(new DataScanItem(options.Area, startAddress + i, raw, $"0x{raw:X4}", valueText,
                ScanItemStatus.Ok, null, string.Empty, result.Elapsed.TotalMilliseconds));
        }

        return items;
    }

    private static DataScanItem Failed(DataScanOptions options, int address, ScanItemStatus status, byte? code, double elapsedMs) =>
        new(options.Area, address, null, "-", null, status, code,
            status switch
            {
                ScanItemStatus.InvalidAddress => "设备报告非法数据地址（异常码 02）",
                ScanItemStatus.Timeout => "响应超时",
                ScanItemStatus.Unsupported => "设备不支持该功能码（异常码 01）",
                _ => "读取失败"
            },
            elapsedMs);

    public static string ExportCsv(string path, IReadOnlyList<DataScanItem> items, string? filter = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
            writer.WriteLine("通讯测试工具 数据扫描结果");
            writer.WriteLine($"导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            if (!string.IsNullOrEmpty(filter)) writer.WriteLine($"扫描参数：{filter}");
            writer.WriteLine("数据区,协议地址,原始值,解析值,状态,异常码,说明,耗时ms");
            foreach (var item in items)
            {
                writer.WriteLine(string.Join(',',
                    item.Area.ToShortChinese(),
                    item.Address,
                    item.RawHex,
                    item.ValueText ?? string.Empty,
                    item.StatusText,
                    item.ExceptionCode is { } code ? $"0x{code:X2}" : string.Empty,
                    item.Note,
                    item.ElapsedMs.ToString("F1")));
            }
            return string.Empty;
        }
        catch (Exception ex)
        {
            return $"导出失败：{ex.Message}";
        }
    }
}
