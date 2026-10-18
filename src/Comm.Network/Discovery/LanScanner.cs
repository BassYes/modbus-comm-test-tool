using System.Globalization;
using System.Text;
using Comm.Core;
using Comm.Core.Errors;
using Comm.Core.Tasks;

namespace Comm.Network.Discovery;

/// <summary>
/// 局域网设备扫描（见规划 4.7，需求 R-06）。
/// 组成：范围构建（<see cref="LanScanRange"/>）+ 主机探测（<see cref="IHostProbe"/>）
/// + 可选的 Modbus 只读识别（<see cref="ModbusIdentifyDelegate"/>，避免 Comm.Network 依赖 Comm.Session）。
/// 限流方式是**固定数量的工作协程**：不会一次创建 4096 个任务，也不会无上限地打爆内网（规划 4.7.3）。
/// </summary>
public sealed class LanScanner
{
    private readonly IHostProbe _probe;
    private readonly ModbusIdentifyDelegate? _modbusIdentify;
    private readonly object _throttleLock = new();
    private long _nextSlotTicks;

    public LanScanner(IHostProbe probe, ModbusIdentifyDelegate? modbusIdentify = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _modbusIdentify = modbusIdentify;
    }

    /// <summary>
    /// 扫描给定范围内的主机。范围非法（含超过上限）时抛 <see cref="CommException"/>（错误码 <c>VAL-4007</c>）；
    /// 界面应先调用 <see cref="LanScanRange.Build"/> 做校验并给出可读提示。
    /// </summary>
    public async Task<IReadOnlyList<LanHostResult>> ScanAsync(LanScanOptions options, PauseGate? gate,
        IProgress<LanScanProgress>? progress, IProgress<LanHostResult>? hostProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var range = LanScanRange.Build(options);
        if (!range.IsSuccess) throw new CommException(range.Error!);

        var addresses = range.Value!;
        if (addresses.Count == 0) return Array.Empty<LanHostResult>();

        var results = new LanHostResult?[addresses.Count];
        int nextIndex = -1;
        int completed = 0;
        int workerCount = Math.Min(options.EffectiveConcurrency(), addresses.Count);

        progress?.Report(new LanScanProgress(0, addresses.Count,
            $"开始（并发 {workerCount}，超时 {options.EffectiveTimeoutMs()} ms）"));

        var workers = new Task[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    int index = Interlocked.Increment(ref nextIndex);
                    if (index >= addresses.Count) return;

                    if (gate is not null) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (options.SendDelayMs > 0)
                    {
                        await ThrottleAsync(options.SendDelayMs, cancellationToken).ConfigureAwait(false);
                    }

                    var address = addresses[index];
                    results[index] = await ProbeHostAsync(address, options, cancellationToken).ConfigureAwait(false);
                    hostProgress?.Report(results[index]!);

                    int done = Interlocked.Increment(ref completed);
                    progress?.Report(new LanScanProgress(done, addresses.Count, $"探测 {address}"));
                }
            }, cancellationToken);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);

        var ordered = results.Where(r => r is not null).Select(r => r!)
            .OrderBy(r => AddressValue(r.Ip))
            .ToArray();

        progress?.Report(new LanScanProgress(addresses.Count, addresses.Count,
            $"完成：发现 {ordered.Count(r => r.State != LanHostState.Unconfirmed)} 个有响应主机"));
        return ordered;
    }

    /// <summary>全局限速：保证两次“开始新探测”之间至少间隔设定毫秒。</summary>
    private async Task ThrottleAsync(int sendDelayMs, CancellationToken cancellationToken)
    {
        long waitTicks;
        lock (_throttleLock)
        {
            long now = Clock.MonotonicTicks;
            long interval = sendDelayMs * Clock.Frequency / 1000;
            long due = Math.Max(now, _nextSlotTicks);
            _nextSlotTicks = due + interval;
            waitTicks = due - now;
        }

        if (waitTicks > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(waitTicks / (double)Clock.Frequency), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<LanHostResult> ProbeHostAsync(string ip, LanScanOptions options,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.EffectiveTimeoutMs());

        HostProbeResult probe;
        try
        {
            probe = await _probe.ProbeAsync(ip, options, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 单台主机超时：不中断整个扫描（规划 4.7.3）。
            probe = new HostProbeResult(false, false, false, options.EffectiveTimeoutMs(), null, "探测超时");
        }

        string? identity = null;
        if (options.IdentifyModbus && probe.PortOpen && _modbusIdentify is not null)
        {
            int port = options.TcpPorts?.FirstOrDefault(p => p > 0) ?? 502;
            try
            {
                identity = await _modbusIdentify(ip, port, options.UnitId, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                identity = null;
            }
        }

        string? hostName = null;
        if (options.ResolveHostName && probe.AnyResponse)
        {
            hostName = await TryResolveHostNameAsync(ip, cancellationToken).ConfigureAwait(false);
        }

        var kind = probe.ArpResponded ? HostDiscoveryKind.Arp
            : probe.IcmpResponded ? HostDiscoveryKind.Icmp
            : probe.PortOpen ? HostDiscoveryKind.Tcp
            : HostDiscoveryKind.Unknown;

        bool modbusResponding = !string.IsNullOrWhiteSpace(identity);
        var state = modbusResponding ? LanHostState.ModbusResponding
            : probe.PortOpen ? LanHostState.PortOpen
            : probe.ArpResponded || probe.IcmpResponded ? LanHostState.Discovered
            : LanHostState.Unconfirmed;

        var now = Clock.Now;
        return new LanHostResult(ip, probe.Mac, hostName, kind, probe.PortOpen, modbusResponding, state,
            probe.LatencyMs, now, now, BuildNote(options, probe, state, identity));
    }

    private static string BuildNote(LanScanOptions options, HostProbeResult probe, LanHostState state, string? identity)
    {
        if (state == LanHostState.ModbusResponding) return $"Modbus 设备：{identity}";
        if (state == LanHostState.PortOpen) return $"端口开放（{string.Join('/', options.TcpPorts ?? Array.Empty<int>())}）";
        if (state == LanHostState.Discovered)
        {
            var via = probe.ArpResponded ? "ARP" : "ICMP";
            return $"已发现（{via} 有响应）" +
                   (probe.Mac is null ? "；同网段外 ARP 通常拿不到 MAC" : string.Empty);
        }

        return "未确认：" + (string.IsNullOrEmpty(probe.Error) ? "ARP/ICMP 无响应且端口未开放" : probe.Error) +
               "，可能被防火墙拦截或在路由之后，不能据此判定离线";
    }

    private static async Task<string?> TryResolveHostNameAsync(string ip, CancellationToken cancellationToken)
    {
        try
        {
            var entry = await System.Net.Dns.GetHostEntryAsync(ip, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(entry.HostName) ? null : entry.HostName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static uint AddressValue(string ip) =>
        SubnetCalculator.TryParseAddress(ip, out var value) ? value : uint.MaxValue;

    /// <summary>导出扫描结果 CSV；格式与设备扫描导出一致（见规划 6.6）。</summary>
    public static string ExportCsv(string path, IReadOnlyList<LanHostResult> results, string? filter = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            writer.WriteLine("通讯测试工具 局域网设备扫描结果");
            writer.WriteLine($"导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            if (!string.IsNullOrEmpty(filter)) writer.WriteLine($"扫描参数：{filter}");
            writer.WriteLine("IP,MAC,主机名,发现方式,端口状态,Modbus状态,状态分级,耗时ms,首次发现,最近响应,备注");
            foreach (var host in results)
            {
                writer.WriteLine(string.Join(',',
                    host.Ip,
                    Csv(host.Mac ?? string.Empty),
                    Csv(host.HostName ?? string.Empty),
                    host.KindText,
                    host.PortText,
                    host.ModbusText,
                    host.StateText,
                    host.LatencyMs.ToString("F0", CultureInfo.InvariantCulture),
                    host.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    host.LastSeen.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    Csv(host.Note)));
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
