using Comm.Core.Codecs;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Session;
using Comm.TestSupport;
using Comm.Transport;
using Xunit;

namespace Comm.Scanning.Tests;

/// <summary>
/// 监视表（规划 4.3.5、4.4.5、13.3）。
/// 重点验证：周期请求用 Periodic 优先级、连续地址合并、变化标记，以及与扫描任务并行时不破坏 CC-1。
/// </summary>
public class WatchTableTests
{
    private static (ModbusSession Session, ModbusSlaveSimulator Slave) CreateRtu(ModbusSlaveSimulator? slave = null)
    {
        slave ??= new ModbusSlaveSimulator();
        var codec = new ModbusRtuCodec();
        var channel = new LoopbackChannel(slave.CreateResponder(codec), responseDelayMs: 1);
        var options = new SessionOptions
        {
            ResponseTimeoutMs = 120,
            RetryCount = 0,
            TurnaroundDelayMs = 1,
            IsSerial = true,
            SuppressEcho = false
        };
        return (new ModbusSession(channel, codec, options), slave);
    }

    [Fact]
    public async Task Points_can_be_added_removed_and_refreshed()
    {
        var slave = new ModbusSlaveSimulator();
        slave.HoldingRegisters[1] = 42;
        slave.InputRegisters[5] = 7;
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var watch = new WatchTableService(session);
        watch.Add(new RegisterPoint { Id = "a", Name = "温度", Address = 1 });
        watch.Add(new RegisterPoint { Id = "b", Name = "压力", Area = DataArea.InputRegisters, Address = 5 });
        Assert.Equal(2, watch.Points.Count);

        var samples = await watch.RefreshOnceAsync();

        Assert.Equal(2, samples.Count);
        Assert.Equal("42", samples.Single(s => s.PointId == "a").ValueText);
        Assert.Equal("7", samples.Single(s => s.PointId == "b").ValueText);
        Assert.All(samples, s => Assert.Equal(ScanItemStatus.Ok, s.Status));
        Assert.All(samples, s => Assert.False(s.Changed)); // 首次采样不算变化

        Assert.True(watch.Remove("a"));
        Assert.False(watch.Remove("a"));
        Assert.Single(watch.Points);
        Assert.Single(await watch.RefreshOnceAsync());

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Change_is_marked_with_the_previous_value()
    {
        var slave = new ModbusSlaveSimulator();
        slave.HoldingRegisters[2] = 1;
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var watch = new WatchTableService(session);
        watch.Add(new RegisterPoint { Id = "p", Name = "状态", Address = 2 });

        var first = Assert.Single(await watch.RefreshOnceAsync());
        Assert.False(first.Changed);
        Assert.Null(first.PreviousValue);

        slave.HoldingRegisters[2] = 9;
        var second = Assert.Single(await watch.RefreshOnceAsync());
        Assert.True(second.Changed);
        Assert.Equal("1", second.PreviousValue);
        Assert.Equal("9", second.ValueText);
        Assert.Equal("变化", second.ChangedText);

        // 值不变的下一轮不再标记变化
        var third = Assert.Single(await watch.RefreshOnceAsync());
        Assert.False(third.Changed);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Continuous_points_are_merged_into_one_request()
    {
        var slave = new ModbusSlaveSimulator();
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var watch = new WatchTableService(session);
        watch.Add(new RegisterPoint { Id = "1", Address = 1 });
        watch.Add(new RegisterPoint { Id = "2", Address = 2 });
        watch.Add(new RegisterPoint { Id = "3", Address = 3 });

        await watch.RefreshOnceAsync();
        Assert.Single(slave.ReceivedRequests);
        Assert.Equal(1, watch.LastCycleRequestCount);

        // 再放一个远离的点位：应变成两个请求块
        watch.Add(new RegisterPoint { Id = "4", Address = 30 });
        slave.ReceivedRequests.Clear();
        await watch.RefreshOnceAsync();
        Assert.Equal(2, slave.ReceivedRequests.Count);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Periodic_refresh_uses_periodic_priority_and_can_be_cancelled()
    {
        var slave = new ModbusSlaveSimulator();
        slave.HoldingRegisters[0] = 3;
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var watch = new WatchTableService(session);
        watch.Add(new RegisterPoint { Id = "p", Address = 0 });

        Assert.Equal(TimeSpan.FromMilliseconds(200), watch.MinimumInterval);

        int cycles = 0;
        watch.CycleCompleted += (_, _) => Interlocked.Increment(ref cycles);

        using var cts = new CancellationTokenSource();
        var running = watch.RunAsync(TimeSpan.FromMilliseconds(1), null, null, cts.Token);
        await Task.Delay(320);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Assert.True(cycles >= 1);

        // 周期请求走 Periodic 优先级，因此在扫描任务存在时也不会被写守卫拒绝、也不会写寄存器。
        Assert.All(slave.ReceivedRequests, r => Assert.False(Fn.IsWrite(r.Function)));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Watch_refresh_interleaves_with_scan_without_breaking_cc1()
    {
        var slave = new ModbusSlaveSimulator();
        for (ushort i = 0; i < 20; i++) slave.HoldingRegisters[i] = (ushort)(i + 1);
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var watch = new WatchTableService(session);
        watch.Add(new RegisterPoint { Id = "p", Address = 4, Name = "监视点" });

        var scanService = new DataScanService(session);
        for (int round = 0; round < 3; round++)
        {
            var scanned = await scanService.ScanAsync(new DataScanOptions
            {
                UnitId = 1,
                Area = DataArea.HoldingRegisters,
                StartAddress = 0,
                EndAddress = 19,
                BatchSize = 10,
                IntervalMs = 0
            }, null, null, null, CancellationToken.None);
            Assert.All(scanned, item => Assert.Equal(ScanItemStatus.Ok, item.Status));

            var sample = Assert.Single(await watch.RefreshOnceAsync());
            Assert.Equal("5", sample.ValueText);
        }

        Assert.Equal(0, session.Statistics.UnmatchedCount);
        Assert.Equal(0, session.Statistics.TimeoutCount);
        Assert.All(slave.ReceivedRequests, r => Assert.False(Fn.IsWrite(r.Function)));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Bit_area_points_are_decoded_as_on_off()
    {
        var slave = new ModbusSlaveSimulator();
        slave.Coils[3] = true;
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var watch = new WatchTableService(session);
        watch.Add(new RegisterPoint { Id = "c", Name = "开关", Area = DataArea.Coils, Address = 3 });
        watch.Add(new RegisterPoint { Id = "d", Name = "备用", Area = DataArea.Coils, Address = 4 });

        var samples = await watch.RefreshOnceAsync();

        Assert.Equal("1 (ON)", samples.Single(s => s.PointId == "c").ValueText);
        Assert.Equal("0 (OFF)", samples.Single(s => s.PointId == "d").ValueText);

        await session.DisposeAsync();
    }

    /// <summary>测试输出目录（与运行器同级，避免受系统临时目录权限影响）。</summary>
    private static string TempFile(string prefix)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-output");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{prefix}-{Guid.NewGuid():N}.csv");
    }

    [Fact]
    public void Export_writes_chinese_header_and_one_row_per_sample()
    {
        var path = TempFile("watch");        try
        {
            var samples = new[]
            {
                new WatchPointSample("p", "温度", DataArea.HoldingRegisters, 1, DataType.UInt16, ByteOrder.Abcd,
                    0.1, "℃", "23.5", "0x00EB", ScanItemStatus.Ok, true, "23.0", 1.2, DateTimeOffset.Now)
            };

            Assert.Empty(WatchTableService.ExportCsv(path, samples));

            var lines = File.ReadAllLines(path);
            Assert.Equal("通讯测试工具 监视表采样结果", lines[0]);
            Assert.Contains("点位名称,数据区,地址", lines[2]);
            Assert.Contains("温度,保持寄存器,1,无符号 16 位", lines[3]);
            Assert.Contains("是,23.0", lines[3]);
            Assert.Equal(4, lines.Length);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
