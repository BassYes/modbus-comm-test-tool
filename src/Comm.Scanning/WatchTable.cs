using System.Globalization;
using System.Text;
using Comm.Core.Errors;
using Comm.Core.Tasks;
using Comm.Protocols.Modbus;
using Comm.Session;

namespace Comm.Scanning;

/// <summary>监视表点位（规划 7.4 的 RegisterPoint）。</summary>
public sealed record RegisterPoint
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = string.Empty;
    public byte UnitId { get; init; } = 1;
    public DataArea Area { get; init; } = DataArea.HoldingRegisters;
    public int Address { get; init; }
    public DataType Type { get; init; } = DataType.UInt16;
    public ByteOrder Order { get; init; } = ByteOrder.Abcd;
    public double Scale { get; init; } = 1.0;
    public double Offset { get; init; }
    public string? Unit { get; init; }
    public string? Note { get; init; }
    public bool Enabled { get; init; } = true;

    public int RegisterCount => Type.RegisterCount();
    public int EndAddress => Address + RegisterCount - 1;
}

/// <summary>一次监视采样的结果（供界面与导出使用）。</summary>
public sealed record WatchPointSample(
    string PointId,
    string Name,
    DataArea Area,
    int Address,
    DataType Type,
    ByteOrder Order,
    double Scale,
    string? Unit,
    string? ValueText,
    string RawHex,
    ScanItemStatus Status,
    bool Changed,
    string? PreviousValue,
    double ElapsedMs,
    DateTimeOffset Timestamp)
{
    public string AddressText => $"{Area.ToShortChinese()} {Address}";

    public string TypeText => Type.ToChinese();

    public string OrderText => Order.ToDisplay();

    public string StatusText => Status switch
    {
        ScanItemStatus.Ok => "正常",
        ScanItemStatus.InvalidAddress => "非法地址",
        ScanItemStatus.Timeout => "超时",
        ScanItemStatus.Unsupported => "不支持",
        _ => "错误"
    };

    public string ChangedText => Changed ? "变化" : string.Empty;

    public string DisplayText => string.IsNullOrEmpty(Unit) ? ValueText ?? string.Empty : $"{ValueText} {Unit}";
}

/// <summary>
/// 监视表（见规划 4.3.5、4.4.5）：把关注的点位按周期读取并标记变化。
/// 使用 <see cref="RequestPriority.Periodic"/>，优先级介于交互与扫描之间，因此可以与扫描任务并存而不破坏 CC-1；
/// 扫描进行中允许穿插周期请求，但界面需提示“已穿插请求，可能影响扫描耗时统计”（规划 13.3）。
/// </summary>
public sealed class WatchTableService
{
    private readonly ModbusSession _session;
    private readonly List<RegisterPoint> _points = new();
    private readonly Dictionary<string, string?> _previousValues = new();
    private readonly object _sync = new();

    public WatchTableService(ModbusSession session)
    {
        _session = session;
    }

    /// <summary>点位列表快照（顺序为加入顺序）。</summary>
    public IReadOnlyList<RegisterPoint> Points
    {
        get { lock (_sync) return _points.ToArray(); }
    }

    /// <summary>最小刷新周期：不低于 200 ms，避免打爆现场总线（见 M2 待确认项 10）。</summary>
    public TimeSpan MinimumInterval { get; } = TimeSpan.FromMilliseconds(200);

    /// <summary>最近一轮实际发出的请求数；用于界面提示“已穿插请求”。</summary>
    public int LastCycleRequestCount { get; private set; }

    public event EventHandler<WatchPointSample>? SampleReceived;
    public event EventHandler<IReadOnlyList<WatchPointSample>>? CycleCompleted;

    public void Add(RegisterPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);
        lock (_sync)
        {
            _points.RemoveAll(p => p.Id == point.Id);
            _points.Add(point);
        }
    }

    public bool Remove(string pointId)
    {
        lock (_sync)
        {
            _previousValues.Remove(pointId);
            return _points.RemoveAll(p => p.Id == pointId) > 0;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _points.Clear();
            _previousValues.Clear();
        }
    }

    /// <summary>立即读取一次全部启用点位（界面“立即刷新”按钮）。</summary>
    public async Task<IReadOnlyList<WatchPointSample>> RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        RegisterPoint[] active;
        lock (_sync) active = _points.Where(p => p.Enabled).ToArray();

        var samples = new List<WatchPointSample>();
        int requests = 0;

        foreach (var group in active.GroupBy(p => (p.UnitId, p.Area)).OrderBy(g => g.Key.UnitId).ThenBy(g => g.Key.Area))
        {
            foreach (var block in BuildBlocks(group))
            {
                cancellationToken.ThrowIfCancellationRequested();

                int start = block[0].Address;
                int count = block[^1].EndAddress - start + 1;
                int protocolMax = Fn.MaxQuantity(group.Key.Area.ReadFunctionCode());
                int effective = protocolMax > 0 ? Math.Min(count, protocolMax) : count;

                long begin = Comm.Core.Clock.MonotonicTicks;
                var result = await _session.ExecuteAsync(
                    ModbusRequest.Periodic(group.Key.UnitId, ModbusRequests.Read(group.Key.Area, (ushort)start, (ushort)effective)),
                    cancellationToken).ConfigureAwait(false);
                double elapsedMs = Comm.Core.Clock.MillisecondsBetween(begin, Comm.Core.Clock.MonotonicTicks);
                requests++;

                var registers = result.ResponsePdu?.Registers ?? Array.Empty<ushort>();
                var bits = result.ResponsePdu?.Bits;
                foreach (var point in block)
                {
                    samples.Add(BuildSample(point, result, registers, bits, effective, start, elapsedMs));
                }
            }
        }

        LastCycleRequestCount = requests;

        foreach (var sample in samples) SampleReceived?.Invoke(this, sample);
        CycleCompleted?.Invoke(this, samples);
        return samples;
    }

    /// <summary>周期刷新直到取消。定时器只负责触发，不做 I/O。</summary>
    public async Task RunAsync(TimeSpan interval, PauseGate? gate, IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var effective = interval < MinimumInterval ? MinimumInterval : interval;
        using var timer = new PeriodicTimer(effective);
        int cycle = 0;

        progress?.Report(new ScanProgress("监视表", 0, 0, $"周期 {effective.TotalMilliseconds:F0} ms", ScanStatus.Running));

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (gate is not null)
            {
                if (gate.IsPaused) progress?.Report(new ScanProgress("监视表", cycle, 0, "已暂停", ScanStatus.Paused));
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var samples = await RefreshOnceAsync(cancellationToken).ConfigureAwait(false);
            cycle++;
            int changed = samples.Count(s => s.Changed);
            progress?.Report(new ScanProgress("监视表", cycle, 0,
                $"第 {cycle} 轮：{samples.Count} 个点位，{changed} 个变化", ScanStatus.Running));
        }
    }

    private WatchPointSample BuildSample(RegisterPoint point, ExchangeResult result, ushort[] registers, bool[]? bits,
        int requestedCount, int blockStart, double elapsedMs)
    {
        string raw = "-";
        string? value = null;
        var status = MapStatus(result);

        if (status == ScanItemStatus.Ok)
        {
            if (point.Area.IsBitArea())
            {
                int bitIndex = point.Address - blockStart;
                if (bits is not null && bitIndex >= 0 && bitIndex < bits.Length)
                {
                    bool on = bits[bitIndex];
                    raw = on ? "01" : "00";
                    value = on ? "1 (ON)" : "0 (OFF)";
                }
                else
                {
                    status = ScanItemStatus.Error;
                }
            }
            else
            {
                int offset = point.Address - blockStart;
                if (offset >= 0 && offset + point.RegisterCount <= registers.Length && offset + point.RegisterCount <= requestedCount)
                {
                    var span = registers.AsSpan(offset, point.RegisterCount);
                    raw = string.Join(' ', span.ToArray().Select(r => $"0x{r:X4}"));
                    value = DataDecoder.Decode(span, point.Type, point.Order, point.Scale, point.Offset);
                }
                else
                {
                    status = ScanItemStatus.Error;
                }
            }
        }

        bool changed;
        string? previous;
        lock (_sync)
        {
            changed = _previousValues.TryGetValue(point.Id, out var old) && !string.Equals(old, value, StringComparison.Ordinal);
            previous = changed ? old : null;
            _previousValues[point.Id] = value;
        }

        return new WatchPointSample(point.Id, point.Name, point.Area, point.Address, point.Type, point.Order,
            point.Scale, point.Unit, value, raw, status, changed, previous, elapsedMs, DateTimeOffset.Now);
    }

    private static ScanItemStatus MapStatus(ExchangeResult result) => result.Status switch
    {
        ExchangeStatus.Ok => ScanItemStatus.Ok,
        ExchangeStatus.ModbusException => result.ExceptionCode switch
        {
            0x02 => ScanItemStatus.InvalidAddress,
            0x01 => ScanItemStatus.Unsupported,
            _ => ScanItemStatus.Error
        },
        ExchangeStatus.Timeout or ExchangeStatus.ChecksumError => ScanItemStatus.Timeout,
        _ => ScanItemStatus.Error
    };

    /// <summary>
    /// 把同区域、地址相邻（间隔不超过 8 个寄存器）的点位合并成一次请求，减少总线占用。
    /// </summary>
    private static List<List<RegisterPoint>> BuildBlocks(IEnumerable<RegisterPoint> points)
    {
        var sorted = points.OrderBy(p => p.Address).ToList();
        var blocks = new List<List<RegisterPoint>>();
        var current = new List<RegisterPoint>();

        foreach (var point in sorted)
        {
            if (current.Count > 0 && point.Address - current[^1].EndAddress > 9)
            {
                blocks.Add(current);
                current = new List<RegisterPoint>();
            }
            current.Add(point);
        }

        if (current.Count > 0) blocks.Add(current);
        return blocks;
    }

    /// <summary>导出监视采样 CSV；格式与设备/数据扫描导出一致。</summary>
    public static string ExportCsv(string path, IReadOnlyList<WatchPointSample> samples, string? filter = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            writer.WriteLine("通讯测试工具 监视表采样结果");
            writer.WriteLine($"导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            if (!string.IsNullOrEmpty(filter)) writer.WriteLine($"筛选条件：{filter}");
            writer.WriteLine("点位名称,数据区,地址,类型,字节序,缩放,单位,解析值,原始值,状态,是否变化,上次值,耗时ms,时间");
            foreach (var sample in samples)
            {
                writer.WriteLine(string.Join(',',
                    Csv(sample.Name),
                    sample.Area.ToShortChinese(),
                    sample.Address,
                    sample.TypeText,
                    sample.OrderText,
                    sample.Scale.ToString("G", CultureInfo.InvariantCulture),
                    Csv(sample.Unit ?? string.Empty),
                    Csv(sample.ValueText ?? string.Empty),
                    sample.RawHex,
                    sample.StatusText,
                    sample.Changed ? "是" : "否",
                    Csv(sample.PreviousValue ?? string.Empty),
                    sample.ElapsedMs.ToString("F1"),
                    sample.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")));
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
