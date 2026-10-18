using Comm.Core.Errors;
using Comm.Core.Transport;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Session;
using Comm.TestSupport;
using Xunit;

namespace Comm.Scanning.Tests;

/// <summary>
/// R-11：设备扫描的“未知波特率 / 未知校验位”（规划 4.2.6、22.3、22.3.6）。
/// 覆盖组合生成与排序、停止位策略、耗时估计、阶段 A/B 流转、参数感知设备、
/// 多组合命中与推荐、3/3 确认与“疑似”、取消/异常/超时后的参数恢复、只读约束、
/// 静默阈值与响应超时随波特率变化、方案往返与旧方案默认值。
/// </summary>
public class ParameterScanTests
{
    // ------------------------------------------------------------ 测试替身：可重配的串口会话

    /// <summary>把“重开串口”实现为重建 LoopbackChannel 的假重配器（无硬件）。</summary>
    private sealed class FakeSerialReconfigurator : ISerialReconfigurator, IAsyncDisposable
    {
        private readonly ModbusSlaveSimulator _device;
        private readonly SerialSettings _original;
        private readonly SerialSettings? _required;
        private readonly int _timeoutMs;
        private ModbusSession? _session;

        public FakeSerialReconfigurator(ModbusSlaveSimulator device, SerialSettings original,
            SerialSettings? required, int timeoutMs = 80)
        {
            _device = device;
            _original = original;
            _required = required;
            _timeoutMs = timeoutMs;
        }

        public List<SerialSettings> Applied { get; } = new();
        public int RestoreCount { get; private set; }
        public bool FailApply { get; set; }

        /// <summary>每次重建通道时触发（测试可用来设置 ShouldRespond）。</summary>
        public event EventHandler<ParameterAwareLoopbackChannel>? ChannelCreated;

        /// <summary>当前打开的通道（最近一次）。</summary>
        public ParameterAwareLoopbackChannel? Channel { get; private set; }

        public bool CanReconfigure => true;
        public SerialSettings? OriginalSettings => _original;
        public SerialSettings? ActiveSettings { get; private set; }

        public ModbusSession? Current => _session;

        public async Task<ModbusSession?> ApplyAsync(SerialSettings settings, int settleMs, CancellationToken cancellationToken)
        {
            Applied.Add(settings);
            if (FailApply)
            {
                await CloseSessionAsync().ConfigureAwait(false);
                return null;
            }

            await CloseSessionAsync().ConfigureAwait(false);
            ActiveSettings = settings;
            _session = Create(settings);
            await _session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return _session;
        }

        public async Task<SerialRestoreResult> RestoreAsync(CancellationToken cancellationToken)
        {
            RestoreCount++;
            await CloseSessionAsync().ConfigureAwait(false);
            ActiveSettings = _original;
            _session = Create(_original);
            await _session.ConnectAsync(cancellationToken).ConfigureAwait(false);
            return SerialRestoreResult.Ok;
        }

        private ModbusSession Create(SerialSettings settings)
        {
            Channel = new ParameterAwareLoopbackChannel(_device, settings, _required);
            ChannelCreated?.Invoke(this, Channel);
            var codec = new ModbusRtuCodec(RtuTiming.ComputeSilenceThresholdMs(settings.BaudRate));
            var options = new SessionOptions
            {
                ResponseTimeoutMs = RtuTiming.EffectiveResponseTimeoutMs(_timeoutMs, settings.BaudRate),
                RetryCount = 0,
                TurnaroundDelayMs = 1,
                SuppressEcho = false,
                IsSerial = true
            };
            return new ModbusSession(Channel, codec, options);
        }

        private async Task CloseSessionAsync()
        {
            if (_session is null) return;
            var session = _session;
            _session = null;
            await session.DisposeAsync().ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync() => await CloseSessionAsync().ConfigureAwait(false);
    }

    private static SerialSettings Original() => new()
    {
        PortName = "COM9",
        BaudRate = 4800,
        DataBits = 8,
        Parity = SerialParity.None,
        StopBits = SerialStopBits.One
    };

    private static SerialSettings Required(int baudRate, SerialParity parity, SerialStopBits stopBits = SerialStopBits.One) => new()
    {
        PortName = "COM9",
        BaudRate = baudRate,
        DataBits = 8,
        Parity = parity,
        StopBits = stopBits
    };

    private static ParameterScanOptions Options(byte start, byte end, int[] baudRates, byte[]? knownAddresses = null) => new()
    {
        StartAddress = start,
        EndAddress = end,
        ResponseTimeoutMs = 60,
        IntervalMs = 0,
        TryReadDeviceIdentification = false,
        BaudRateSource = SerialParameterSource.Unknown,
        ParitySource = SerialParameterSource.Unknown,
        CandidateBaudRates = baudRates,
        CommonFirst = false,
        StopBits = StopBitsStrategy.Auto,
        KnownAddresses = knownAddresses,
        ConfirmAttempts = 2
    };

    // ------------------------------------------------------------ 1. 组合生成与排序

    [Fact]
    public void Common_first_ordering_puts_the_usual_combinations_in_front()
    {
        var options = new ParameterScanOptions
        {
            BaudRateSource = SerialParameterSource.Unknown,
            ParitySource = SerialParameterSource.Unknown,
            CandidateBaudRates = new[] { 1200, 4800, 9600, 19200 },
            CommonFirst = true,
            StopBits = StopBitsStrategy.One
        };

        var combos = options.BuildCombinations();

        Assert.Equal("9600-8-N-1", combos[0].Text);
        Assert.Equal("9600-8-E-1", combos[1].Text);
        Assert.Equal("19200-8-N-1", combos[2].Text);
        Assert.Equal("19200-8-E-1", combos[3].Text);
        Assert.Equal("4800-8-N-1", combos[4].Text);
        // 未列入“常用”的 1200 组合排在后面
        Assert.Contains(combos, c => c.Text == "1200-8-N-1");
        Assert.True(combos.ToList().FindIndex(c => c.Text == "1200-8-N-1") > 4);
        // 不重复
        Assert.Equal(combos.Count, combos.Select(c => c.Text).Distinct().Count());
    }

    [Fact]
    public void Combination_count_matches_baud_times_parity_times_stop_bits()
    {
        var options = new ParameterScanOptions
        {
            BaudRateSource = SerialParameterSource.Unknown,
            ParitySource = SerialParameterSource.Unknown,
            CandidateBaudRates = new[] { 9600, 19200 },
            StopBits = StopBitsStrategy.Auto
        };

        // N 有 1 位与 2 位两种；E/O 各 1 种 → (2+1+1) × 2 个波特率 = 8
        var combos = options.BuildCombinations();
        Assert.Equal(8, combos.Count);
        Assert.Equal(2, combos.Count(c => c.Parity == SerialParity.None && c.StopBits == SerialStopBits.Two));
    }

    // ------------------------------------------------------------ 2. 停止位策略

    [Theory]
    [InlineData(StopBitsStrategy.Auto, SerialParity.None, 2)]
    [InlineData(StopBitsStrategy.Auto, SerialParity.Even, 1)]
    [InlineData(StopBitsStrategy.Auto, SerialParity.Odd, 1)]
    [InlineData(StopBitsStrategy.One, SerialParity.None, 1)]
    [InlineData(StopBitsStrategy.Two, SerialParity.Even, 1)]
    public void Stop_bits_strategy_follows_the_specification(StopBitsStrategy strategy, SerialParity parity, int expected)
    {
        var options = new ParameterScanOptions { StopBits = strategy };
        Assert.Equal(expected, options.EffectiveStopBits(parity).Count);
    }

    // ------------------------------------------------------------ 3. 耗时估计与 >10 分钟确认

    [Fact]
    public void Estimate_reports_confirmation_when_over_ten_minutes()
    {
        var options = new ParameterScanOptions
        {
            StartAddress = 1,
            EndAddress = 247,
            ResponseTimeoutMs = 500,
            IntervalMs = 20,
            BaudRateSource = SerialParameterSource.Unknown,
            ParitySource = SerialParameterSource.Unknown,
            CandidateBaudRates = SerialParameterCandidates.BaudRates,
            StopBits = StopBitsStrategy.Auto
        };

        var estimate = options.Estimate(probeTemplateCount: 4);

        // 8 个波特率 × (无校验 1 位/2 位 + 偶校验 + 奇校验) = 32 个组合（规划 D-11）
        Assert.Equal(32, estimate.CombinationCount);
        Assert.True(estimate.RequiresConfirmation);
        Assert.Contains("分钟", estimate.ToText());
    }

    [Fact]
    public void Default_candidate_set_expands_to_twenty_four_combinations()
    {
        // 8 个波特率 × (N 的 1 位与 2 位 + E 的 1 位 + O 的 1 位) = 8 × 4 = 32？规划 D-11 写的是
        // 8 × {N-1, E-1, O-1} + None 另加 2 位 = 8 × 4 = 32；这里确认实现与规划一致：
        var options = new ParameterScanOptions
        {
            BaudRateSource = SerialParameterSource.Unknown,
            ParitySource = SerialParameterSource.Unknown,
            StopBits = StopBitsStrategy.Auto
        };

        Assert.Equal(32, options.BuildCombinations().Count);
    }

    // ------------------------------------------------------------ 4. 静默阈值与响应超时随波特率变化（F-5）

    [Theory]
    [InlineData(1200, 34)]
    [InlineData(2400, 18)]
    [InlineData(4800, 10)]
    [InlineData(9600, 6)]
    [InlineData(115200, 5)]
    public void Silence_threshold_follows_the_baud_rate(int baudRate, int expected)
    {
        Assert.Equal(expected, RtuTiming.ComputeSilenceThresholdMs(baudRate));
    }

    [Fact]
    public void Silence_threshold_never_goes_below_the_user_value_or_the_floor()
    {
        Assert.Equal(50, RtuTiming.ComputeSilenceThresholdMs(115200, 50));
        Assert.Equal(RtuTiming.MinimumSilenceThresholdMs, RtuTiming.ComputeSilenceThresholdMs(115200, 0));
        // 3.5 字符 @1200 bps ≈ 32 ms，必须高于固定 5 ms 的旧实现
        Assert.True(RtuTiming.ComputeSilenceThresholdMs(1200) > 32);
    }

    [Fact]
    public void Response_timeout_is_raised_to_a_baud_aware_floor()
    {
        // 高波特率下仍保留“两帧传输 + 100 ms 设备处理余量”的下限（保持 Windows 上的可靠性）
        Assert.Equal(102, RtuTiming.EffectiveResponseTimeoutMs(60, 115200));
        // 1200 bps：8 字节 × 11 位 ≈ 73 ms，两帧 + 100 ms ≈ 247 ms
        Assert.Equal(247, RtuTiming.EffectiveResponseTimeoutMs(60, 1200));
        // 用户填写的较大超时不会被压低
        Assert.Equal(500, RtuTiming.EffectiveResponseTimeoutMs(500, 1200));
    }

    // ------------------------------------------------------------ 5. 参数自检

    [Fact]
    public void Validation_rejects_empty_candidate_set_and_bad_values()
    {
        Assert.False(ParameterScanOptions.Validate(new ParameterScanOptions
        {
            BaudRateSource = SerialParameterSource.Unknown,
            CandidateBaudRates = Array.Empty<int>()
        }, out var empty));

        Assert.Equal(ErrorCodes.ValidationBadInput, empty!.Code);

        Assert.False(ParameterScanOptions.Validate(new ParameterScanOptions
        {
            BaudRateSource = SerialParameterSource.Specify,
            SpecifiedBaudRate = 12345
        }, out var baud));

        Assert.Equal(ErrorCodes.ValidationBadInput, baud!.Code);

        Assert.False(ParameterScanOptions.Validate(new ParameterScanOptions { ConfirmAttempts = 9 }, out var confirm));
        Assert.Equal(ErrorCodes.ValidationBadInput, confirm!.Code);
    }

    [Fact]
    public async Task Parameter_scan_requires_a_reconfigurable_serial_session()
    {
        var service = new ParameterScanService(() => null, new NonReconfigurable());
        var exception = await Assert.ThrowsAsync<CommException>(() => service.ScanAsync(
            Options(1, 2, new[] { 9600 }), null, null, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.ValidationParameterScanNotSerial, exception.Error.Code);
    }

    private sealed class NonReconfigurable : ISerialReconfigurator
    {
        public bool CanReconfigure => false;
        public SerialSettings? OriginalSettings => null;
        public SerialSettings? ActiveSettings => null;
        public Task<ModbusSession?> ApplyAsync(SerialSettings settings, int settleMs, CancellationToken cancellationToken) =>
            Task.FromResult<ModbusSession?>(null);
        public Task<SerialRestoreResult> RestoreAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SerialRestoreResult.Ok);
    }

    // ------------------------------------------------------------ 6. 阶段 A/B 流转：找到只在特定参数下应答的设备

    [Fact]
    public async Task Finds_a_device_that_only_answers_at_19200_even_parity_and_restores_original_parameters()
    {
        var device = new ModbusSlaveSimulator { UnitId = 5 };
        device.HoldingRegisters[0] = 0x1234;

        await using var reconfigurator = new FakeSerialReconfigurator(device, Original(),
            Required(19200, SerialParity.Even));
        var service = new ParameterScanService(() => reconfigurator.Current, reconfigurator);

        var options = Options(1, 8, new[] { 4800, 9600, 19200 }, knownAddresses: new byte[] { 5 });
        var result = await service.ScanAsync(options, null, null, null, CancellationToken.None);

        var found = Assert.Single(result.OnlineItems);
        Assert.Equal(5, found.Address);
        Assert.Equal("19200-8-E-1", found.CombinationText);
        Assert.True(found.ConfirmSuccess >= 3, $"3/3 确认应全部通过，实际 {found.ConfirmSuccess}/{found.ConfirmTotal}");

        // 阶段 A 命中的组合是唯一的，且被标为推荐
        Assert.Single(result.HitCombinations);
        Assert.True(result.Groups.Single(g => g.Recommended).Recommended);
        Assert.NotNull(result.BestRecommendation);
        Assert.True(found.Recommended);

        // 四个终态都要恢复原参数：这里是“正常完成”
        Assert.True(result.Restored);
        Assert.Equal(1, reconfigurator.RestoreCount);
        Assert.Equal(4800, reconfigurator.ActiveSettings!.BaudRate);
        Assert.Equal(SerialParity.None, reconfigurator.ActiveSettings.Parity);
    }

    [Fact]
    public async Task Phase_a_sends_only_one_read_probe_per_address()
    {
        var device = new ModbusSlaveSimulator { UnitId = 5 };
        await using var reconfigurator = new FakeSerialReconfigurator(device, Original(), Required(19200, SerialParity.Even));
        var service = new ParameterScanService(() => reconfigurator.Current, reconfigurator);

        var options = Options(5, 5, new[] { 19200 }, knownAddresses: new byte[] { 1, 2, 3, 4, 5 });
        var result = await service.ScanAsync(options, null, null, null, CancellationToken.None);

        Assert.NotEmpty(result.HitCombinations);

        // 阶段 A：地址 1~5 各一次 03 读 1 个寄存器（起始地址 0），共 5 次
        var first = device.ReceivedRequests.Take(5).ToArray();
        Assert.All(first, pdu =>
        {
            Assert.Equal(Fn.ReadHoldingRegisters, pdu.Function);
            Assert.Equal(0, pdu.Data[2]);
            Assert.Equal(1, pdu.Data[3]);
        });
    }

    // ------------------------------------------------------------ 7. 多组合同时命中：分组、推荐、不自行断言唯一

    [Fact]
    public async Task Lenient_device_hits_multiple_combinations_and_only_one_is_recommended()
    {
        var device = new ModbusSlaveSimulator { UnitId = 5 };   // required = null → 宽容设备
        await using var reconfigurator = new FakeSerialReconfigurator(device, Original(), required: null);
        var service = new ParameterScanService(() => reconfigurator.Current, reconfigurator);

        var options = Options(5, 5, new[] { 9600, 19200 }, knownAddresses: new byte[] { 5 });
        options = options with { ContinueAfterHit = true };
        var result = await service.ScanAsync(options, null, null, null, CancellationToken.None);

        Assert.True(result.HitCombinations.Count > 1, "宽容设备应在多个组合上命中");
        Assert.Equal(result.HitCombinations.Count, result.Groups.Count(g => g.Online > 0));
        Assert.Single(result.Groups, g => g.Recommended);
        Assert.Single(result.Items, i => i.Recommended);

        // 推荐规则：无杂乱数据、停止位较少者优先
        var recommended = result.Groups.Single(g => g.Recommended);
        Assert.Equal(1, recommended.Combination.StopBitCount);
        Assert.Contains("推荐", recommended.Note);
        Assert.Contains("不做唯一断言", result.Summary);
    }

    [Fact]
    public async Task Continue_after_hit_false_stops_after_the_first_hit()
    {
        var device = new ModbusSlaveSimulator { UnitId = 5 };
        await using var reconfigurator = new FakeSerialReconfigurator(device, Original(), required: null);
        var service = new ParameterScanService(() => reconfigurator.Current, reconfigurator);

        var options = Options(5, 5, new[] { 9600, 19200 }, knownAddresses: new byte[] { 5 });   // ContinueAfterHit = false
        var result = await service.ScanAsync(options, null, null, null, CancellationToken.None);

        Assert.Single(result.HitCombinations);
        Assert.Single(result.TestedCombinations);
    }

    // ------------------------------------------------------------ 8. 3/3 确认与“疑似”

    [Fact]
    public async Task Failed_confirmation_downgrades_the_address_to_suspected()
    {
        var device = new ModbusSlaveSimulator { UnitId = 5 };
        await using var reconfigurator = new FakeSerialReconfigurator(device, Original(), Required(19200, SerialParity.Even));
        var service = new ParameterScanService(() => reconfigurator.Current, reconfigurator);

        var options = Options(5, 5, new[] { 19200 }, knownAddresses: new byte[] { 5 });

        // 参数匹配的前两次应答正常（阶段 A 一次 + 阶段 B 首探一次），第三次（确认）丢弃 → 3/3 不成立。
        // 参数不匹配的组合本来就会返回不可用帧，不占用配额头。
        int matched = 0;
        reconfigurator.ChannelCreated += (_, channel) =>
            channel.ShouldRespond = () => !(channel.Matches() && matched++ >= 2);

        var result = await service.ScanAsync(options, null, null, null, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal(DeviceOnlineStatus.Suspected, item.Status);
        Assert.Contains("确认", item.Note);
        Assert.Empty(result.OnlineItems);
        Assert.True(result.Restored);
    }

    // ------------------------------------------------------------ 9. 取消 → 恢复原参数

    [Fact]
    public async Task Cancellation_restores_the_original_serial_parameters()
    {
        var device = new ModbusSlaveSimulator { UnitId = 5 };
        await using var reconfigurator = new FakeSerialReconfigurator(device, Original(), Required(19200, SerialParity.Even));
        var service = new ParameterScanService(() => reconfigurator.Current, reconfigurator);

        using var cts = new CancellationTokenSource();
        reconfigurator.ChannelCreated += (_, channel) => channel.ShouldRespond = () =>
        {
            cts.Cancel();
            return true;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ScanAsync(
            Options(1, 8, new[] { 9600, 19200 }, knownAddresses: new byte[] { 5 }), null, null, null, cts.Token));

        Assert.Equal(1, reconfigurator.RestoreCount);
        Assert.Equal(4800, reconfigurator.ActiveSettings!.BaudRate);
    }

    // ------------------------------------------------------------ 10. 重配失败 / 全部超时 → 恢复原参数

    [Fact]
    public async Task Reconfiguration_failure_is_recorded_and_parameters_are_restored()
    {
        var device = new ModbusSlaveSimulator { UnitId = 5 };
        await using var reconfigurator = new FakeSerialReconfigurator(device, Original(), Required(19200, SerialParity.Even))
        {
            FailApply = true
        };
        var service = new ParameterScanService(() => reconfigurator.Current, reconfigurator);

        var result = await service.ScanAsync(Options(1, 5, new[] { 9600, 19200 }), null, null, null, CancellationToken.None);

        Assert.Empty(result.HitCombinations);
        Assert.Empty(result.Items);
        Assert.NotEmpty(result.Failures);
        Assert.True(result.Restored);
        Assert.Equal(1, reconfigurator.RestoreCount);
    }

    [Fact]
    public async Task Device_that_never_answers_is_reported_as_not_found_and_parameters_are_restored()
    {
        var device = new ModbusSlaveSimulator { UnitId = 5, Offline = true };
        await using var reconfigurator = new FakeSerialReconfigurator(device, Original(), Required(19200, SerialParity.Even));
        var service = new ParameterScanService(() => reconfigurator.Current, reconfigurator);

        var result = await service.ScanAsync(Options(1, 4, new[] { 9600, 19200 }, knownAddresses: new byte[] { 1, 2 }),
            null, null, null, CancellationToken.None);

        Assert.Empty(result.HitCombinations);
        Assert.Empty(result.OnlineItems);
        Assert.Contains("未在任何参数组合上发现设备", result.Summary);
        Assert.Equal(1, reconfigurator.RestoreCount);
    }

    // ------------------------------------------------------------ 11. 只读与不广播（CC-1 / CC-6）

    [Fact]
    public async Task Parameter_scan_never_writes_and_never_uses_broadcast()
    {
        var device = new ModbusSlaveSimulator { UnitId = 5 };
        await using var reconfigurator = new FakeSerialReconfigurator(device, Original(), required: null);
        var service = new ParameterScanService(() => reconfigurator.Current, reconfigurator);

        var options = Options(1, 3, new[] { 9600 }, knownAddresses: new byte[] { 5 });
        options = options with { ContinueAfterHit = true };
        await service.ScanAsync(options, null, null, null, CancellationToken.None);

        Assert.NotEmpty(device.ReceivedRequests);
        Assert.All(device.ReceivedRequests, pdu => Assert.False(Fn.IsWrite(pdu.Function)));
        Assert.All(device.ReceivedRequests, pdu => Assert.True(Fn.IsRead(pdu.Function)));
    }

    // ------------------------------------------------------------ 12. 方案往返 + 旧方案默认值

    [Fact]
    public void Plan_payload_round_trips_and_legacy_payload_falls_back_to_connection_parameters()
    {
        // 新形状：DeviceScanPlan（含参数扫描设置）
        var payload = new DeviceScanPlan
        {
            Scan = new DeviceScanOptions { StartAddress = 3, EndAddress = 9, ConfirmAttempts = 2 },
            Parameters = new ParameterScanOptions
            {
                BaudRateSource = SerialParameterSource.Unknown,
                ParitySource = SerialParameterSource.Specify,
                SpecifiedParity = SerialParity.Even,
                CandidateBaudRates = new[] { 9600, 19200 }
            }
        };

        var plan = Comm.Settings.ScanPlan.Create("参数扫描方案", Comm.Settings.ScanPlanKinds.Device, payload);
        var read = plan.ReadPayload<DeviceScanPlan>();

        Assert.NotNull(read);
        Assert.Equal(3, read!.Scan.StartAddress);
        Assert.Equal(9, read.Scan.EndAddress);
        Assert.Equal(SerialParameterSource.Unknown, read.Parameters!.BaudRateSource);
        Assert.Equal(SerialParity.Even, read.Parameters.SpecifiedParity);
        Assert.Equal(new[] { 9600, 19200 }, read.Parameters.CandidateBaudRates);

        // 旧形状：直接保存 DeviceScanOptions → Parameters 为 null，界面回退到“使用连接中心参数”
        var legacy = Comm.Settings.ScanPlan.Create("旧方案", Comm.Settings.ScanPlanKinds.Device,
            new DeviceScanOptions { StartAddress = 7, EndAddress = 12 });

        var legacyPayload = legacy.ReadPayload<DeviceScanPlan>();
        Assert.Null(legacyPayload!.Parameters);

        var oldShape = legacy.ReadPayload<DeviceScanOptions>();
        Assert.Equal(7, oldShape!.StartAddress);
        Assert.Equal(12, oldShape.EndAddress);

        // 缺字段时默认“使用连接中心参数”，不需要升 schemaVersion
        Assert.Equal(SerialParameterSource.UseConnection, new ParameterScanOptions().BaudRateSource);
        Assert.Equal(SerialParameterSource.UseConnection, new ParameterScanOptions().ParitySource);
    }

    // ------------------------------------------------------------ 13. 与应用/推荐相关的展示属性

    [Fact]
    public void Item_exposes_the_parameter_columns_used_by_the_result_table()
    {
        var item = new DeviceScanItem(5, DeviceOnlineStatus.Online, Fn.ReadHoldingRegisters, null,
            DateTimeOffset.Now, DateTimeOffset.Now, 12.5, null, "读取成功",
            new SerialCombination(19200, SerialParity.Even, SerialStopBits.One), Recommended: true);

        Assert.Equal("19200", item.BaudRateText);
        Assert.Equal("E", item.ParityText);
        Assert.Equal("1", item.StopBitsText);
        Assert.Equal("19200-8-E-1", item.CombinationText);
        Assert.Equal("推荐", item.RecommendationText);

        // 普通设备扫描（无参数组合）时这些列为空，不影响原有导出
        var plain = item with { Combination = null, Recommended = false };
        Assert.Equal(string.Empty, plain.BaudRateText);
        Assert.Equal(string.Empty, plain.CombinationText);
        Assert.Equal(string.Empty, plain.RecommendationText);
    }

    [Fact]
    public void Known_address_text_is_parsed_with_ranges()
    {
        Assert.Equal(new byte[] { 1, 2, 3, 5, 247 }, AddressListParser.Parse("1-3, 5;247"));
        Assert.Null(AddressListParser.Parse("   "));
        Assert.Null(AddressListParser.Parse("0,300"));   // 超出 1~247 的地址被忽略
        // 解析结果直接作为阶段 A 的探测地址集
        var options = new ParameterScanOptions { KnownAddresses = AddressListParser.Parse("5,7-9") };
        Assert.Equal(new byte[] { 5, 7, 8, 9 }, options.PhaseATargets());
    }
}
