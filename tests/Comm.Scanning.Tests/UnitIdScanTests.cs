using Comm.Core.Codecs;
using Comm.Core.Errors;
using Comm.Core.Tasks;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Session;
using Comm.TestSupport;
using Comm.Transport;
using Xunit;

namespace Comm.Scanning.Tests;

/// <summary>
/// TCP Unit ID 扫描（R-12，规划 4.2.7、13.4.2）：
/// 探测规则与串口设备扫描一致、默认串行、并发有界且每个 Unit ID 只探一次、
/// 异常响应判在线、绝不产生写功能码与广播、取消与释放、参数门禁。
/// </summary>
public class UnitIdScanTests
{
    /// <summary>一个能同时应答多个 Unit ID 的“网关”模拟器。</summary>
    private sealed class GatewaySimulator
    {
        private readonly Dictionary<byte, ModbusSlaveSimulator> _slaves = new();
        private readonly HashSet<byte> _illegalUnitIds = new();
        private readonly object _sync = new();

        public int OpenConnectionCount;
        public int MaxConcurrentRequests;

        private int _inflight;
        private readonly List<byte> _allUnitIds = new();
        private readonly List<ModbusPdu> _allRequests = new();

        public IReadOnlyList<byte> AllUnitIds
        {
            get { lock (_sync) return _allUnitIds.ToArray(); }
        }

        public IReadOnlyList<ModbusPdu> AllRequests
        {
            get { lock (_sync) return _allRequests.ToArray(); }
        }

        public void AddSlave(ModbusSlaveSimulator slave) => _slaves[slave.UnitId] = slave;

        /// <summary>该 Unit ID 返回合法 Modbus 异常响应（非法数据地址）→ 仍判定“在线”。</summary>
        public void AddExceptionOnlyUnitId(byte unitId) => _illegalUnitIds.Add(unitId);

        public Func<byte[], byte[]?> CreateResponder(IFrameCodec codec) => request =>
        {
            lock (_sync) _allRequests.Add(ModbusPdu.Parse(request.AsSpan(1, request.Length - 4)));

            var unitId = request[0];
            lock (_sync) _allUnitIds.Add(unitId);

            int now = Interlocked.Increment(ref _inflight);
            lock (_sync) MaxConcurrentRequests = Math.Max(MaxConcurrentRequests, now);

            try
            {
                var slave = _slaves.FirstOrDefault(kv => kv.Key == unitId).Value;
                if (slave is not null) return slave.CreateResponder(codec)(request);

                if (_illegalUnitIds.Contains(unitId))
                {
                    // 只读请求 + 非法地址 → 异常响应 02（说明该 Unit ID 上的从站在线）
                    var function = request[1];
                    var adu = new List<byte> { unitId, (byte)(function | 0x80), 0x02 };
                    ushort crc = ModbusCrc16.ComputeCrc(adu.ToArray());
                    adu.Add((byte)(crc & 0xFF));          // Modbus RTU：CRC 低字节在前
                    adu.Add((byte)(crc >> 8));
                    return adu.ToArray();
                }

                // 其他 Unit ID：不回帧（超时）
                return null;
            }
            finally
            {
                Interlocked.Decrement(ref _inflight);
            }
        };

        public void NoteConnection() => Interlocked.Increment(ref OpenConnectionCount);
    }

    private static (ModbusSession Session, ModbusSlaveSimulator Slave, LoopbackChannel Channel) CreateSession(
        ModbusSlaveSimulator? slave = null, int responseTimeoutMs = 120)
    {
        slave ??= new ModbusSlaveSimulator();
        var codec = new ModbusRtuCodec();
        var channel = new LoopbackChannel(slave.CreateResponder(codec), responseDelayMs: 1);
        var options = new SessionOptions
        {
            ResponseTimeoutMs = responseTimeoutMs,
            RetryCount = 0,
            TurnaroundDelayMs = 1,
            IsSerial = true,
            SuppressEcho = false
        };
        return (new ModbusSession(channel, codec, options), slave, channel);
    }

    /// <summary>同步进度实现：Progress&lt;T&gt; 会异步投递到线程池，测试里需要“立即生效”。</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _action;
        private readonly object _sync = new();

        public SyncProgress(Action<T> action) => _action = action;

        public void Report(T value)
        {
            lock (_sync) _action(value);
        }
    }

    private static UnitIdScanOptions DefaultOptions(int start = 1, int end = 5, int concurrency = 1) => new()
    {
        StartUnitId = (byte)start,
        EndUnitId = (byte)end,
        ResponseTimeoutMs = 60,
        IntervalMs = 0,
        TryReadDeviceIdentification = false,
        Concurrency = concurrency
    };

    [Fact]
    public async Task Serial_scan_finds_only_online_unit_ids_and_never_writes()
    {
        var gateway = new GatewaySimulator();
        var slave = new ModbusSlaveSimulator { UnitId = 3 };
        slave.HoldingRegisters[0] = 42;
        gateway.AddSlave(slave);

        var codec = new ModbusRtuCodec();
        var channel = new LoopbackChannel(gateway.CreateResponder(codec), responseDelayMs: 1);
        var options = new SessionOptions { ResponseTimeoutMs = 60, RetryCount = 0, TurnaroundDelayMs = 1, SuppressEcho = false };
        var session = new ModbusSession(channel, codec, options);
        await session.ConnectAsync();

        var service = new UnitIdScanService(session);
        var results = await service.ScanAsync(DefaultOptions(1, 5), null, null, null, CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.Equal(DeviceOnlineStatus.Online, results.Single(r => r.UnitId == 3).Status);
        Assert.All(results.Where(r => r.UnitId != 3), r => Assert.Equal(DeviceOnlineStatus.Offline, r.Status));

        // CC-6：扫描绝不产生写功能码请求，也绝不使用广播地址 0
        Assert.NotEmpty(gateway.AllRequests);
        Assert.All(gateway.AllRequests, r => Assert.False(Fn.IsWrite(r.Function)));
        Assert.All(gateway.AllUnitIds, id => Assert.NotEqual(0, id));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Exception_response_means_online()
    {
        var gateway = new GatewaySimulator();
        gateway.AddExceptionOnlyUnitId(7);

        var (session, _, _) = CreateSession();
        await session.ConnectAsync();

        var codec = new ModbusRtuCodec();
        var channel = new LoopbackChannel(gateway.CreateResponder(codec), responseDelayMs: 1);
        var gatewaySession = new ModbusSession(channel, codec,
            new SessionOptions { ResponseTimeoutMs = 60, RetryCount = 0, TurnaroundDelayMs = 1, SuppressEcho = false });
        await gatewaySession.ConnectAsync();

        var service = new UnitIdScanService(gatewaySession);
        var results = await service.ScanAsync(DefaultOptions(7, 7), null, null, null, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal(DeviceOnlineStatus.Online, item.Status);
        Assert.Equal((byte)0x02, item.ExceptionCode);
        Assert.Contains("非法数据地址", item.ExceptionText);

        await session.DisposeAsync();
        await gatewaySession.DisposeAsync();
    }

    [Fact]
    public async Task Concurrency_is_bounded_and_every_unit_id_is_probed_once()
    {
        var gateway = new GatewaySimulator();
        for (byte id = 1; id <= 3; id++)
        {
            var slave = new ModbusSlaveSimulator { UnitId = id, ResponseDelayMs = 5 };
            gateway.AddSlave(slave);
        }

        var codec = new ModbusRtuCodec();
        var primaryChannel = new LoopbackChannel(gateway.CreateResponder(codec), responseDelayMs: 1);
        var primary = new ModbusSession(primaryChannel, codec,
            new SessionOptions { ResponseTimeoutMs = 200, RetryCount = 0, TurnaroundDelayMs = 1, SuppressEcho = false });
        await primary.ConnectAsync();

        // 3 个并发 worker：额外会话也走同一模拟器
        int created = 0;
        var extras = new List<ModbusSession>();
        var service = new UnitIdScanService(primary, async _ =>
        {
            Interlocked.Increment(ref created);
            gateway.NoteConnection();
            var channel = new LoopbackChannel(gateway.CreateResponder(codec), responseDelayMs: 1);
            var session = new ModbusSession(channel, codec,
                new SessionOptions { ResponseTimeoutMs = 200, RetryCount = 0, TurnaroundDelayMs = 1, SuppressEcho = false });
            await session.ConnectAsync();     // 工厂契约：返回已打开的会话
            extras.Add(session);
            return session;
        });

        var results = await service.ScanAsync(DefaultOptions(1, 12, concurrency: 3), null, null, null, CancellationToken.None);

        Assert.True(results.Count == 12, $"结果数应为 12，实际 {results.Count}");
        Assert.True(results.Select(r => r.UnitId).Distinct().Count() == 12,
            $"每个 Unit ID 只应出现一次，实际不同 Unit ID 数 {results.Select(r => r.UnitId).Distinct().Count()}：" +
            string.Join(",", results.Select(r => r.UnitId)));
        Assert.True(gateway.AllUnitIds.Distinct().Count() == 12,
            $"模拟器观测到的不同 Unit ID 数应为 12，实际 {gateway.AllUnitIds.Distinct().Count()}：" +
            string.Join(",", gateway.AllUnitIds.Distinct()));
        Assert.True(created == 2, $"应只创建 concurrency-1 = 2 个额外会话，实际 {created}");

        // 并发确实发生（模拟器观测到的最大在途请求 > 1），且有界（不超过并发度）
        Assert.True(gateway.MaxConcurrentRequests > 1, $"最大在途 {gateway.MaxConcurrentRequests}");
        Assert.True(gateway.MaxConcurrentRequests <= 3, $"最大在途 {gateway.MaxConcurrentRequests} 超过并发度 3");

        // 临时会话必须被释放；释放后再发请求应返回“未连接”而不是抛异常（预期失败用值表达，规划 15.1）
        foreach (var extra in extras)
        {
            Assert.False(extra.IsOpen, "临时会话应在扫描结束后被释放");

            var result = await extra.ExecuteAsync(
                ModbusRequest.Scan(1, ModbusRequests.Read(DataArea.HoldingRegisters, 0, 1)), CancellationToken.None);
            Assert.Equal(ExchangeStatus.NotConnected, result.Status);
        }

        await primary.DisposeAsync();
    }

    [Fact]
    public async Task Factory_returning_an_unopened_session_degrades_with_a_clear_note()
    {
        var gateway = new GatewaySimulator();
        gateway.AddSlave(new ModbusSlaveSimulator { UnitId = 2 });

        var codec = new ModbusRtuCodec();
        var channel = new LoopbackChannel(gateway.CreateResponder(codec), responseDelayMs: 1);
        var primary = new ModbusSession(channel, codec,
            new SessionOptions { ResponseTimeoutMs = 60, RetryCount = 0, TurnaroundDelayMs = 1, SuppressEcho = false });
        await primary.ConnectAsync();

        var progress = new List<ScanProgress>();
        // 故意返回“没打开”的会话：服务必须降级为串行并给出可读原因，而不是把 Unit ID 静默记成未发现
        var service = new UnitIdScanService(primary, _ => Task.FromResult<ModbusSession?>(new ModbusSession(
            new LoopbackChannel(gateway.CreateResponder(codec), responseDelayMs: 1), codec,
            new SessionOptions { ResponseTimeoutMs = 60, RetryCount = 0, TurnaroundDelayMs = 1, SuppressEcho = false })));

        var results = await service.ScanAsync(DefaultOptions(1, 4, concurrency: 3), null,
            new SyncProgress<ScanProgress>(p => progress.Add(p)), null, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.Equal(DeviceOnlineStatus.Online, results.Single(r => r.UnitId == 2).Status);
        Assert.Contains(progress, p => p.CurrentStep.Contains("尚未打开"));

        await primary.DisposeAsync();
    }

    [Fact]
    public async Task Factory_failure_degrades_to_serial_without_aborting_the_scan()
    {
        var gateway = new GatewaySimulator();
        gateway.AddSlave(new ModbusSlaveSimulator { UnitId = 2 });

        var codec = new ModbusRtuCodec();
        var channel = new LoopbackChannel(gateway.CreateResponder(codec), responseDelayMs: 1);
        var primary = new ModbusSession(channel, codec,
            new SessionOptions { ResponseTimeoutMs = 60, RetryCount = 0, TurnaroundDelayMs = 1, SuppressEcho = false });
        await primary.ConnectAsync();

        var progress = new List<ScanProgress>();
        var service = new UnitIdScanService(primary, _ => Task.FromResult<ModbusSession?>(null));

        var results = await service.ScanAsync(DefaultOptions(1, 4, concurrency: 4), null,
            new SyncProgress<ScanProgress>(p => progress.Add(p)), null, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.Equal(DeviceOnlineStatus.Online, results.Single(r => r.UnitId == 2).Status);
        Assert.Contains(progress, p => p.CurrentStep.Contains("未建立连接"));

        await primary.DisposeAsync();
    }

    [Fact]
    public async Task Cancel_stops_the_scan_without_reporting_fake_results()
    {
        var gateway = new GatewaySimulator();
        var codec = new ModbusRtuCodec();
        var channel = new LoopbackChannel(gateway.CreateResponder(codec), responseDelayMs: 5);
        var primary = new ModbusSession(channel, codec,
            new SessionOptions { ResponseTimeoutMs = 200, RetryCount = 0, TurnaroundDelayMs = 1, SuppressEcho = false });
        await primary.ConnectAsync();

        using var cts = new CancellationTokenSource();
        var service = new UnitIdScanService(primary);

        var scan = service.ScanAsync(DefaultOptions(1, 200), null, null, null, cts.Token);
        await Task.Delay(60);          // 让扫描真正开始
        cts.Cancel();                  // 模拟“用户点了停止”

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await scan);

        await primary.DisposeAsync();
    }

    [Fact]
    public async Task Pause_gate_holds_the_scan()
    {
        var gateway = new GatewaySimulator();
        var codec = new ModbusRtuCodec();
        var channel = new LoopbackChannel(gateway.CreateResponder(codec), responseDelayMs: 1);
        var primary = new ModbusSession(channel, codec,
            new SessionOptions { ResponseTimeoutMs = 60, RetryCount = 0, TurnaroundDelayMs = 1, SuppressEcho = false });
        await primary.ConnectAsync();

        var gate = new PauseGate();
        gate.Pause();

        var service = new UnitIdScanService(primary);
        var scan = service.ScanAsync(DefaultOptions(1, 3), gate, null, null, CancellationToken.None);

        await Task.Delay(150);
        Assert.False(scan.IsCompleted, "暂停期间不应继续扫描");
        Assert.Empty(gateway.AllRequests);

        gate.Resume();
        var results = await scan;

        Assert.Equal(3, results.Count);
        Assert.NotEmpty(gateway.AllRequests);

        await primary.DisposeAsync();
    }

    [Theory]
    [InlineData(0, 247, 1, false)]      // Unit ID 0 非法（广播地址不参与扫描）
    [InlineData(5, 4, 1, false)]        // 起始大于结束
    [InlineData(1, 248, 1, false)]      // 超出 247
    [InlineData(1, 10, 0, false)]       // 并发度 0
    [InlineData(1, 10, 9, false)]       // 并发度超上限
    [InlineData(1, 10, 1, true)]
    [InlineData(1, 247, 8, true)]
    public void Validation_rejects_invalid_options(int start, int end, int concurrency, bool expected)
    {
        var options = new UnitIdScanOptions
        {
            StartUnitId = (byte)start,
            EndUnitId = (byte)end,
            Concurrency = concurrency
        };

        Assert.Equal(expected, UnitIdScanOptions.Validate(options, out _));
    }

    [Fact]
    public void Validation_blocks_write_function_codes_even_in_manual_mode()
    {
        var options = new UnitIdScanOptions
        {
            Mode = DeviceProbeMode.Manual,
            ManualProbeFunction = Fn.WriteSingleRegister,
            ManualProbeAddress = 0,
            ManualProbeQuantity = 1
        };

        Assert.False(UnitIdScanOptions.Validate(options, out var error));
        Assert.Equal(ErrorCodes.ValidationScanWriteBlocked, error!.Code);
    }

    [Fact]
    public void Manual_mode_uses_the_configured_read_template_only()
    {
        var options = new UnitIdScanOptions
        {
            Mode = DeviceProbeMode.Manual,
            ManualProbeFunction = Fn.ReadInputRegisters,
            ManualProbeAddress = 100,
            ManualProbeQuantity = 2
        };

        var templates = options.ProbeTemplates();

        var template = Assert.Single(templates);
        Assert.Equal(Fn.ReadInputRegisters, template.Function);
        Assert.Equal(100, template.Address);
        Assert.Equal(2, template.Quantity);
    }

    [Fact]
    public void Estimated_duration_accounts_for_concurrency_and_templates()
    {
        var serial = new UnitIdScanOptions { StartUnitId = 1, EndUnitId = 10, ResponseTimeoutMs = 100, Concurrency = 1, IntervalMs = 0 };
        var concurrent = serial with { Concurrency = 5 };

        Assert.True(serial.EstimatedDuration() > concurrent.EstimatedDuration());
        Assert.Contains("串行", serial.EstimatedDurationText());
        Assert.Contains("5 并发", concurrent.EstimatedDurationText());

        // 默认模板 4 个 × 100 ms × 10 个 Unit ID = 4 s
        Assert.InRange(serial.EstimatedDuration().TotalSeconds, 3.9, 4.1);
    }

    [Fact]
    public async Task Results_can_be_exported_to_csv()
    {
        var gateway = new GatewaySimulator();
        gateway.AddSlave(new ModbusSlaveSimulator { UnitId = 1 });

        var codec = new ModbusRtuCodec();
        var channel = new LoopbackChannel(gateway.CreateResponder(codec), responseDelayMs: 1);
        var primary = new ModbusSession(channel, codec,
            new SessionOptions { ResponseTimeoutMs = 60, RetryCount = 0, TurnaroundDelayMs = 1, SuppressEcho = false });
        await primary.ConnectAsync();

        var results = await new UnitIdScanService(primary).ScanAsync(DefaultOptions(1, 2), null, null, null, CancellationToken.None);
        var csv = UnitIdScanService.ExportCsv(results);

        Assert.Contains("UnitID", csv);
        Assert.Contains("在线", csv);
        Assert.Equal(3, csv.TrimEnd().Split('\n').Length);   // 表头 + 2 行

        await primary.DisposeAsync();
    }

    [Fact]
    public async Task Unit_id_scan_reports_progress_for_every_unit_id()
    {
        var gateway = new GatewaySimulator();
        var codec = new ModbusRtuCodec();
        var channel = new LoopbackChannel(gateway.CreateResponder(codec), responseDelayMs: 1);
        var primary = new ModbusSession(channel, codec,
            new SessionOptions { ResponseTimeoutMs = 40, RetryCount = 0, TurnaroundDelayMs = 1, SuppressEcho = false });
        await primary.ConnectAsync();

        var items = new List<UnitIdScanItem>();
        var progress = new List<ScanProgress>();
        var service = new UnitIdScanService(primary);

        await service.ScanAsync(DefaultOptions(1, 4), null,
            new SyncProgress<ScanProgress>(p => progress.Add(p)),
            new SyncProgress<UnitIdScanItem>(i => items.Add(i)),
            CancellationToken.None);

        Assert.Equal(4, items.Count);
        Assert.Contains(progress, p => p.Status == ScanStatus.Completed);
        Assert.Equal(4, progress.Last(p => p.Status == ScanStatus.Completed).Total);

        await primary.DisposeAsync();
    }
}
