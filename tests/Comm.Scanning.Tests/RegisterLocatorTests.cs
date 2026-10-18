using Comm.Core;
using Comm.Core.Codecs;
using Comm.Core.Errors;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Session;
using Comm.TestSupport;
using Comm.Transport;
using Xunit;

namespace Comm.Scanning.Tests;

/// <summary>
/// 寄存器定位（规划 4.4、需求 R-03）。
/// 纯匹配部分直接喂快照（无硬件、无 I/O），端到端部分用回环通道 + 模拟从站。
/// </summary>
public class RegisterLocatorTests
{
    // ---------------------------------------------------------------- 纯匹配（无硬件）

    private static RegisterLocatorOptions Options(
        LocatorMode mode,
        DataType[]? types = null,
        ByteOrder[]? orders = null,
        double[]? scales = null) => new()
    {
        StartAddress = 0,
        EndAddress = 9,
        Mode = mode,
        CandidateTypes = types ?? new[] { DataType.UInt16 },
        CandidateOrders = orders ?? new[] { ByteOrder.Abcd },
        CandidateScales = scales ?? new[] { 1.0 },
        IntervalMs = 0
    };

    private static IReadOnlyDictionary<int, ushort> Map(params (int Address, ushort Value)[] values)
    {
        var map = new Dictionary<int, ushort>();
        foreach (var (address, value) in values) map[address] = value;
        return map;
    }

    [Fact]
    public void Exact_mode_matches_only_the_target_value()
    {
        var snapshots = new[] { Map((0, 100), (1, 123), (2, 200)) };
        var candidates = RegisterLocator.MatchSnapshots(Options(LocatorMode.Exact) with { Target = 123 }, snapshots);

        var hit = Assert.Single(candidates);
        Assert.Equal(1, hit.Address);
        Assert.Equal(100, hit.MatchScore);
        Assert.Equal(123, hit.MatchedValue);
        Assert.Equal("0x007B", hit.RawHex);
        Assert.Contains("精确匹配", hit.Reason);
    }

    [Fact]
    public void Exact_mode_matches_scaled_engineering_value()
    {
        // 寄存器存 235，现场量纲是 23.5（×0.1）：这是现场最常见的情况。
        var snapshots = new[] { Map((2, 235)) };
        var candidates = RegisterLocator.MatchSnapshots(
            Options(LocatorMode.Exact, scales: new[] { 0.1 }) with { Target = 23.5 }, snapshots);

        var hit = Assert.Single(candidates);
        Assert.Equal(2, hit.Address);
        Assert.Equal(0.1, hit.Scale);
        Assert.Contains("×0.1", hit.InterpretationText);
    }

    [Fact]
    public void Tolerance_and_percent_tolerance_are_honoured()
    {
        var snapshots = new[] { Map((0, 97), (1, 101), (2, 104)) };

        var absolute = RegisterLocator.MatchSnapshots(
            Options(LocatorMode.Tolerance) with { Target = 100, Tolerance = 2 }, snapshots);
        Assert.Equal(new[] { 1 }, absolute.Select(c => c.Address));
        Assert.Equal(80, absolute[0].MatchScore); // 偏差 1 / 容差 2 → 扣分一半

        var percent = RegisterLocator.MatchSnapshots(
            Options(LocatorMode.Tolerance) with { Target = 100, TolerancePercent = 5 }, snapshots);
        Assert.Equal(new[] { 0, 1, 2 }, percent.Select(c => c.Address).OrderBy(a => a));
    }

    [Fact]
    public void Range_mode_matches_values_inside_the_bounds()
    {
        var snapshots = new[] { Map((0, 39), (1, 40), (2, 50), (3, 61)) };
        var candidates = RegisterLocator.MatchSnapshots(
            Options(LocatorMode.Range) with { TargetMin = 40, TargetMax = 60 }, snapshots);

        Assert.Equal(new[] { 1, 2 }, candidates.Select(c => c.Address));
        Assert.Equal(90, candidates[0].MatchScore); // 端点给更高分
        Assert.Equal(80, candidates[1].MatchScore);
    }

    [Fact]
    public void Change_mode_needs_two_snapshots_and_reports_before_and_after()
    {
        var snapshots = new[] { Map((0, 10), (1, 20)), Map((0, 10), (1, 25)) };
        var candidates = RegisterLocator.MatchSnapshots(Options(LocatorMode.Change), snapshots);

        var hit = Assert.Single(candidates);
        Assert.Equal(1, hit.Address);
        Assert.Equal(new[] { "20" }, hit.BeforeValues);
        Assert.Equal(new[] { "25" }, hit.AfterValues);
        Assert.True(hit.MatchScore is >= 70 and <= 95);
        Assert.Contains("幅度", hit.Reason);
    }

    [Fact]
    public void Trend_mode_requires_three_snapshots_and_rejects_reversal()
    {
        var twoSnapshots = new[] { Map((0, 1)), Map((0, 2)) };
        Assert.Empty(RegisterLocator.MatchSnapshots(Options(LocatorMode.Trend), twoSnapshots));

        var rising = new[] { Map((0, 1)), Map((0, 2)), Map((0, 3)) };
        var hit = Assert.Single(RegisterLocator.MatchSnapshots(Options(LocatorMode.Trend), rising));
        Assert.Contains("持续增大", hit.Reason);
        Assert.Equal(77, hit.MatchScore);

        var reversed = new[] { Map((0, 1)), Map((0, 3)), Map((0, 2)) };
        Assert.Empty(RegisterLocator.MatchSnapshots(Options(LocatorMode.Trend), reversed));
    }

    [Fact]
    public void Bit_change_mode_reports_the_changed_bit_number()
    {
        var snapshots = new[] { Map((0, 0x0000), (1, 0x0000)), Map((0, 0x0005), (1, 0x0000)) };
        var candidates = RegisterLocator.MatchSnapshots(Options(LocatorMode.BitChange), snapshots);

        var hit = Assert.Single(candidates);
        Assert.Equal(0, hit.Address);
        Assert.Contains("第 0 位 0→1", hit.Reason);
        Assert.Contains("第 2 位 0→1", hit.Reason);
        Assert.Equal(85, hit.MatchScore);
    }

    [Fact]
    public void Text_mode_finds_ascii_runs_and_honours_the_pattern()
    {
        // "TEM1" = 0x5445 0x4D31
        var snapshots = new[] { Map((0, 0x5445), (1, 0x4D31)) };

        var all = RegisterLocator.MatchSnapshots(Options(LocatorMode.Text), snapshots);
        var hit = Assert.Single(all); // 只有片段起点地址命中，不重复
        Assert.Equal(0, hit.Address);
        Assert.Contains("TEM1", hit.Reason);
        Assert.Equal("0x5445 0x4D31", hit.RawHex);

        Assert.Empty(RegisterLocator.MatchSnapshots(
            Options(LocatorMode.Text) with { TextPattern = "ALM" }, snapshots));
        Assert.Single(RegisterLocator.MatchSnapshots(
            Options(LocatorMode.Text) with { TextPattern = "tem" }, snapshots));
    }

    [Fact]
    public void Non_finite_float_and_invalid_bcd_are_filtered_out()
    {
        var snapshots = new[] { Map((0, 0x7FC0), (1, 0x0000), (2, 0xFA00), (4, 0x0000)) };
        var candidates = RegisterLocator.MatchSnapshots(
            Options(LocatorMode.Exact, types: new[] { DataType.Float32, DataType.Bcd }) with { Target = 0 },
            snapshots);

        Assert.DoesNotContain(candidates, c => c.Address == 0 && c.Type == DataType.Float32); // NaN
        Assert.DoesNotContain(candidates, c => c.Address == 2 && c.Type == DataType.Bcd);     // 非法 BCD
    }

    [Fact]
    public void Candidates_are_sorted_stably_by_score_then_address()
    {
        var snapshots = new[] { Map((0, 500), (1, 500), (2, 501), (3, 500)) };
        var options = Options(LocatorMode.Exact) with { Target = 500, Tolerance = 10, MaxResults = 50 };

        var first = RegisterLocator.MatchSnapshots(options, snapshots);
        var second = RegisterLocator.MatchSnapshots(options, snapshots);

        Assert.Equal(first.Select(c => (c.Address, c.MatchScore)), second.Select(c => (c.Address, c.MatchScore)));
        // 完全命中的地址按升序排在容差命中之前
        Assert.Equal(new[] { 0, 1, 3 }, first.Where(c => c.MatchScore == 100).Select(c => c.Address));
        Assert.Equal(2, first[^1].Address);
    }

    [Fact]
    public void Max_results_caps_the_candidate_list()
    {
        var snapshots = new[] { Map((0, 5), (1, 5), (2, 5), (3, 5)) };
        var candidates = RegisterLocator.MatchSnapshots(
            Options(LocatorMode.Exact) with { Target = 5, MaxResults = 2 }, snapshots);

        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public void Validation_rejects_bit_areas_and_short_trend_sampling()
    {
        Assert.False(RegisterLocatorOptions.Validate(
            Options(LocatorMode.Exact) with { Area = DataArea.Coils }, out var areaError));
        Assert.Equal(ErrorCodes.ValidationBadInput, areaError!.Code);

        Assert.False(RegisterLocatorOptions.Validate(
            Options(LocatorMode.Trend) with { SampleCount = 2 }, out var trendError));
        Assert.Contains("3 次采样", trendError!.Message);

        Assert.False(RegisterLocatorOptions.Validate(
            Options(LocatorMode.Exact) with { EndAddress = 0, StartAddress = 10 }, out _));
        Assert.True(RegisterLocatorOptions.Validate(Options(LocatorMode.Exact) with { Target = 1 }, out _));
    }

    // ---------------------------------------------------------------- 端到端（模拟从站）

    private static (ModbusSession Session, ModbusSlaveSimulator Slave) CreateRtu(ModbusSlaveSimulator? slave = null,
        int responseTimeoutMs = 120)
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
        return (new ModbusSession(channel, codec, options), slave);
    }

    private static void Fill(ModbusSlaveSimulator slave, int start, int count, ushort value)
    {
        for (int i = 0; i < count; i++) slave.HoldingRegisters[(ushort)(start + i)] = value;
    }

    [Fact]
    public async Task One_sampling_pass_serves_every_interpretation_and_never_writes()
    {
        var slave = new ModbusSlaveSimulator();
        Fill(slave, 0, 10, 0);
        slave.HoldingRegisters[4] = 1234;
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var locator = new RegisterLocator(session);
        var candidates = await locator.LocateAsync(new RegisterLocatorOptions
        {
            StartAddress = 0,
            EndAddress = 9,
            Mode = LocatorMode.Exact,
            Target = 1234,
            IntervalMs = 0
        }, null, null, null, CancellationToken.None);

        Assert.Contains(candidates, c => c.Address == 4);

        // 关键：默认组合是“5 类型 × 4 字节序 × 4 缩放 = 80 种解释”，但只允许发 1 次采样请求。
        Assert.Single(slave.ReceivedRequests);
        Assert.All(slave.ReceivedRequests, r => Assert.False(Fn.IsWrite(r.Function)));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Change_mode_takes_one_sampling_pass_per_round()
    {
        var slave = new ModbusSlaveSimulator();
        Fill(slave, 0, 4, 7);

        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var modifier = Task.Run(async () =>
        {
            await Task.Delay(40);
            slave.HoldingRegisters[2] = 9;
        });

        var candidates = await new RegisterLocator(session).LocateAsync(new RegisterLocatorOptions
        {
            StartAddress = 0,
            EndAddress = 3,
            Mode = LocatorMode.Change,
            SampleCount = 2,
            SampleIntervalMs = 80,
            IntervalMs = 0,
            CandidateTypes = new[] { DataType.UInt16 },
            CandidateOrders = new[] { ByteOrder.Abcd },
            CandidateScales = new[] { 1.0 }
        }, null, null, null, CancellationToken.None);

        await modifier;

        Assert.Equal(new[] { 2 }, candidates.Select(c => c.Address).Distinct());
        Assert.Equal(2, slave.ReceivedRequests.Count); // 两轮采样 = 两次请求，不是“组合数 × 轮数”

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Locator_reports_candidates_and_progress_then_honours_cancellation()
    {
        var slave = new ModbusSlaveSimulator();
        slave.HoldingRegisters[0] = 42;
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var reported = new List<RegisterCandidate>();
        var progress = new List<ScanProgress>();
        var candidates = await new RegisterLocator(session).LocateAsync(new RegisterLocatorOptions
        {
            StartAddress = 0,
            EndAddress = 3,
            Mode = LocatorMode.Exact,
            Target = 42,
            IntervalMs = 0
        }, null, new CollectingProgress<ScanProgress>(progress),
            new CollectingProgress<RegisterCandidate>(reported), CancellationToken.None);

        Assert.NotEmpty(candidates);
        Assert.All(candidates, c => Assert.Equal(0, c.Address));
        Assert.Equal(candidates.Count, reported.Count);
        Assert.Equal(0, reported[0].Address);
        Assert.Contains(progress, p => p.Status == ScanStatus.Completed);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RegisterLocator(session).LocateAsync(
            new RegisterLocatorOptions { StartAddress = 0, EndAddress = 3, Target = 42, IntervalMs = 0 },
            null, null, null, cts.Token));

        await session.DisposeAsync();
    }

    [Fact]
    public void Export_writes_header_and_rows()
    {
        var path = TempFile("locate");
        try
        {
            var candidates = RegisterLocator.MatchSnapshots(
                Options(LocatorMode.Exact) with { Target = 3 }, new[] { Map((1, 3)) });

            Assert.Empty(RegisterLocator.ExportCsv(path, candidates, "目标 3"));

            var lines = File.ReadAllLines(path);
            Assert.Equal("通讯测试工具 寄存器定位候选结果", lines[0]);
            Assert.Contains("定位条件：目标 3", lines[2]);
            Assert.Contains("协议地址,数据类型", lines[3]);
            Assert.Equal(5, lines.Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>测试输出目录（与运行器同级，避免受系统临时目录权限影响）。</summary>
    private static string TempFile(string prefix)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-output");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{prefix}-{Guid.NewGuid():N}.csv");
    }

    /// <summary>把 IProgress 回调收集到列表，便于断言进度与候选上报。</summary>
    private sealed class CollectingProgress<T> : IProgress<T>
    {
        private readonly List<T> _items;

        public CollectingProgress(List<T> items) => _items = items;

        public void Report(T value)
        {
            lock (_items) _items.Add(value);
        }
    }
}
