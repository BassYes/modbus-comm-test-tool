using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Comm.Core.Errors;
using Comm.Network.Discovery;
using Xunit;

namespace Comm.Network.Tests;

/// <summary>
/// 局域网设备扫描（规划 4.7、需求 R-06）。
/// 全部使用假探测器或本地 TcpListener，不依赖真实网卡与真实网络；分类逻辑与并发限流必须可确定性验证。
/// </summary>
public class LanScannerTests
{
    // ---------------------------------------------------------------- 范围构建

    [Fact]
    public void Range_builds_hosts_from_cidr()
    {
        var result = LanScanRange.Build(new LanScanOptions { CidrOrRange = "192.168.1.0/29" });

        Assert.True(result.IsSuccess);
        Assert.Equal(6, result.Value!.Count);
        Assert.Equal("192.168.1.1", result.Value[0]);
        Assert.Equal("192.168.1.6", result.Value[^1]);
    }

    [Fact]
    public void Range_builds_hosts_from_short_and_full_range()
    {
        var shortForm = LanScanRange.Build(new LanScanOptions { CidrOrRange = "192.168.1.10-12" });
        Assert.Equal(new[] { "192.168.1.10", "192.168.1.11", "192.168.1.12" }, shortForm.Value);

        var full = LanScanRange.Build(new LanScanOptions { CidrOrRange = "10.0.0.5-10.0.0.7" });
        Assert.Equal(new[] { "10.0.0.5", "10.0.0.6", "10.0.0.7" }, full.Value);

        // 起止颠倒自动交换；单地址允许
        Assert.Equal(new[] { "10.0.0.5", "10.0.0.6" }, LanScanRange.Build(new LanScanOptions { CidrOrRange = "10.0.0.6-10.0.0.5" }).Value);
        Assert.Single(LanScanRange.Build(new LanScanOptions { StartIp = "10.0.0.9", EndIp = "10.0.0.9" }).Value!);
    }

    [Fact]
    public void Range_builds_hosts_from_ip_and_mask()
    {
        var result = LanScanRange.Build(new LanScanOptions { IpAddress = "10.0.0.9", MaskText = "255.255.255.0" });
        Assert.True(result.IsSuccess);
        Assert.Equal("10.0.0.1", result.Value![0]);
        Assert.Equal(254, result.Value.Count);

        Assert.True(LanScanRange.Build("192.168.5.7", "28", 4096).IsSuccess);
    }

    [Fact]
    public void Range_over_the_limit_is_rejected_without_expanding()
    {
        var result = LanScanRange.Build(new LanScanOptions { CidrOrRange = "10.0.0.0/16", MaxAddresses = 1024 });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationNetworkRange, result.Error!.Code);
        Assert.Null(result.Value);
        Assert.Contains("1024", result.Error.Message);
        Assert.False(string.IsNullOrEmpty(result.Error.Hint));
    }

    [Fact]
    public void Invalid_range_returns_a_typed_error()
    {
        Assert.Equal(ErrorCodes.ValidationNetworkRange,
            LanScanRange.Build(new LanScanOptions { CidrOrRange = "192.168.1" }).Error!.Code);
        Assert.Equal(ErrorCodes.ValidationNetworkRange,
            LanScanRange.Build(new LanScanOptions { IpAddress = "10.0.0.1", MaskText = "255.0.255.0" }).Error!.Code);
        Assert.Equal(ErrorCodes.ValidationNetworkRange,
            LanScanRange.Build(new LanScanOptions()).Error!.Code);
    }

    [Fact]
    public async Task Illegal_range_makes_the_scanner_fail_with_a_typed_error()
    {
        var scanner = new LanScanner(new FixedProbe(new HostProbeResult(false, false, false, 0, null, null)));
        var error = await Assert.ThrowsAsync<CommException>(() => scanner.ScanAsync(
            new LanScanOptions { CidrOrRange = "10.0.0.0/16", MaxAddresses = 16 },
            null, null, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.ValidationNetworkRange, error.Error.Code);
    }

    // ---------------------------------------------------------------- 状态分级

    [Theory]
    [InlineData(true, false, false, LanHostState.Discovered)]
    [InlineData(false, true, false, LanHostState.Discovered)]
    [InlineData(false, false, true, LanHostState.PortOpen)]
    [InlineData(true, true, true, LanHostState.PortOpen)]
    [InlineData(false, false, false, LanHostState.Unconfirmed)]
    public async Task Host_state_follows_the_probe_result(bool arp, bool icmp, bool port, LanHostState expected)
    {
        var probe = new FixedProbe(new HostProbeResult(arp, icmp, port, 3, arp ? "AA:BB:CC:DD:EE:FF" : null, null));
        var results = await new LanScanner(probe).ScanAsync(Single("192.168.1.20"), null, null, null, CancellationToken.None);

        Assert.Equal(expected, Assert.Single(results).State);
    }

    [Fact]
    public async Task Modbus_identification_has_the_highest_priority_and_is_optional()
    {
        var probe = new FixedProbe(new HostProbeResult(false, false, true, 3, null, null));
        int calls = 0;

        var withIdentify = await new LanScanner(probe, (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>("厂商=ACME；型号=X1");
        }).ScanAsync(Single("192.168.1.30") with { IdentifyModbus = true }, null, null, null, CancellationToken.None);

        Assert.Equal(LanHostState.ModbusResponding, withIdentify[0].State);
        Assert.True(withIdentify[0].ModbusResponding);
        Assert.Contains("ACME", withIdentify[0].Note);
        Assert.Equal(1, calls);

        calls = 0;
        var withoutIdentify = await new LanScanner(probe, (_, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<string?>("x");
        }).ScanAsync(Single("192.168.1.30"), null, null, null, CancellationToken.None);

        Assert.Equal(LanHostState.PortOpen, withoutIdentify[0].State);
        Assert.Equal(0, calls); // 默认不发起识别请求（规划 4.7.2）
    }

    [Fact]
    public async Task Unresponsive_host_is_unconfirmed_not_offline()
    {
        var probe = new FixedProbe(new HostProbeResult(false, false, false, 0, null, null));
        var host = Assert.Single(await new LanScanner(probe)
            .ScanAsync(Single("192.168.1.40"), null, null, null, CancellationToken.None));

        Assert.Equal(LanHostState.Unconfirmed, host.State);
        Assert.Equal("未确认", host.StateText);
        Assert.Contains("未确认", host.Note);
        Assert.Contains("不能据此判定离线", host.Note);

        // ARP 拿不到 MAC 也绝不能判定离线
        var arpOnly = Assert.Single(await new LanScanner(new FixedProbe(new HostProbeResult(true, false, false, 1, null, null)))
            .ScanAsync(Single("192.168.1.41"), null, null, null, CancellationToken.None));
        Assert.Equal(LanHostState.Discovered, arpOnly.State);
    }

    // ---------------------------------------------------------------- 并发、限速、取消

    [Fact]
    public async Task Concurrency_is_bounded_and_results_are_sorted_by_ip()
    {
        var probe = new CountingProbe(delayMs: 15);
        var options = new LanScanOptions
        {
            CidrOrRange = "192.168.1.0/27",
            UseArp = false,
            UseIcmp = false,
            TcpPorts = Array.Empty<int>(),
            Concurrency = 16,
            PerHostTimeoutMs = 5000
        };

        var results = await new LanScanner(probe).ScanAsync(options, null, null, null, CancellationToken.None);

        Assert.Equal(30, results.Count);
        Assert.True(probe.MaximumInFlight <= 16, $"并发上限被突破：{probe.MaximumInFlight}");
        Assert.True(results.Select(r => r.Ip).SequenceEqual(results.Select(r => r.Ip).OrderBy(ip => IpValue(ip))));
    }

    [Fact]
    public async Task Cancellation_stops_starting_new_probes()
    {
        var probe = new CountingProbe(delayMs: 50);
        using var cts = new CancellationTokenSource(60);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new LanScanner(probe).ScanAsync(
            new LanScanOptions
            {
                CidrOrRange = "192.168.1.0/24",
                UseArp = false,
                UseIcmp = false,
                TcpPorts = Array.Empty<int>(),
                Concurrency = 16,
                PerHostTimeoutMs = 5000
            }, null, null, null, cts.Token));

        int started = probe.Started;
        await Task.Delay(80);
        Assert.Equal(started, probe.Started);
    }

    [Fact]
    public async Task A_single_timing_out_host_does_not_abort_the_scan()
    {
        // 假探测器：第一台永远卡住，其余立即返回；单台超时必须只影响自己（规划 4.7.3）
        var probe = new SlowFirstProbe();

        var results = await new LanScanner(probe).ScanAsync(new LanScanOptions
        {
            CidrOrRange = "192.168.1.0/30",
            UseArp = false,
            UseIcmp = false,
            TcpPorts = Array.Empty<int>(),
            Concurrency = 16,
            PerHostTimeoutMs = 50
        }, null, null, null, CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.State == LanHostState.Unconfirmed && r.Note.Contains("超时"));
        Assert.Contains(results, r => r.PortOpen && r.State == LanHostState.PortOpen);
    }

    // ---------------------------------------------------------------- 真实探测与导出

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task System_probe_detects_a_listening_tcp_port()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var open = await new SystemHostProbe().ProbeAsync("127.0.0.1", new LanScanOptions
        {
            UseArp = false,
            UseIcmp = false,
            TcpPorts = new[] { port },
            PerHostTimeoutMs = 500
        }, CancellationToken.None);

        Assert.True(open.PortOpen);

        listener.Stop();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task System_probe_reports_a_closed_port_as_not_open()
    {
        int port;
        using (var probeListener = new TcpListener(IPAddress.Loopback, 0))
        {
            probeListener.Start();
            port = ((IPEndPoint)probeListener.LocalEndpoint).Port;
            probeListener.Stop();
        }

        var closed = await new SystemHostProbe().ProbeAsync("127.0.0.1", new LanScanOptions
        {
            UseArp = false,
            UseIcmp = false,
            TcpPorts = new[] { port },
            PerHostTimeoutMs = 300
        }, CancellationToken.None);

        Assert.False(closed.PortOpen);
    }

    [Fact]
    public async Task Export_writes_chinese_header_and_one_row_per_host()
    {
        var path = TempFile("lan");
        try
        {
            var probe = new FixedProbe(new HostProbeResult(true, false, false, 2, "AA:BB:CC:DD:EE:FF", null));
            var results = await new LanScanner(probe).ScanAsync(Single("192.168.1.50"), null, null, null,
                CancellationToken.None);

            Assert.Empty(LanScanner.ExportCsv(path, results, "网段 192.168.1.0/24"));

            var lines = File.ReadAllLines(path);
            Assert.Equal("通讯测试工具 局域网设备扫描结果", lines[0]);
            Assert.Contains("扫描参数：网段 192.168.1.0/24", lines[2]);
            Assert.Contains("IP,MAC,主机名", lines[3]);
            Assert.Contains("192.168.1.50", lines[4]);
            Assert.Contains("AA:BB:CC:DD:EE:FF", lines[4]);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---------------------------------------------------------------- 测试替身

    private static LanScanOptions Single(string ip) => new()
    {
        CidrOrRange = ip,
        UseArp = false,
        UseIcmp = false,
        TcpPorts = new[] { 502 },
        PerHostTimeoutMs = 500
    };

    private static uint IpValue(string ip)
    {
        return SubnetCalculator.TryParseAddress(ip, out var value) ? value : 0;
    }

    /// <summary>测试输出目录（与运行器同级，避免受系统临时目录权限影响）。</summary>
    private static string TempFile(string prefix)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-output");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{prefix}-{Guid.NewGuid():N}.csv");
    }

    private sealed class FixedProbe : IHostProbe
    {
        private readonly HostProbeResult _result;

        public FixedProbe(HostProbeResult result) => _result = result;

        public Task<HostProbeResult> ProbeAsync(string ip, LanScanOptions options, CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }

    /// <summary>统计同时在途的探测数，用于验证并发上限。</summary>
    private sealed class CountingProbe : IHostProbe
    {
        private readonly int _delayMs;
        private int _inFlight;

        public CountingProbe(int delayMs) => _delayMs = delayMs;

        private int _started;
        private int _maximumInFlight;

        public int MaximumInFlight => Volatile.Read(ref _maximumInFlight);
        public int Started => Volatile.Read(ref _started);

        public async Task<HostProbeResult> ProbeAsync(string ip, LanScanOptions options, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _started);
            int current = Interlocked.Increment(ref _inFlight);
            int observed;
            do
            {
                observed = Volatile.Read(ref _maximumInFlight);
            }
            while (current > observed && Interlocked.CompareExchange(ref _maximumInFlight, current, observed) != observed);
            try
            {
                await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
                return new HostProbeResult(false, false, false, _delayMs, null, null);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>第一台主机永不响应（直到被单台超时取消），其余主机立即返回端口开放。</summary>
    private sealed class SlowFirstProbe : IHostProbe
    {
        private int _calls;

        public async Task<HostProbeResult> ProbeAsync(string ip, LanScanOptions options, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            return new HostProbeResult(false, false, true, 1, null, null);
        }
    }
}
