using Comm.Core.Codecs;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Session;
using Comm.TestSupport;
using Comm.Transport;
using Xunit;

namespace Comm.Scanning.Tests;

/// <summary>扫描与手动发送的端到端行为（模拟从站，无硬件，见规划 16.3.3）。</summary>
public class ScanAndManualSendTests
{
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

    [Fact]
    public async Task Device_scan_finds_only_online_addresses_and_never_writes()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 3 };
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var service = new DeviceScanService(session);
        var results = await service.ScanAsync(new DeviceScanOptions
        {
            StartAddress = 1,
            EndAddress = 5,
            ResponseTimeoutMs = 60,
            IntervalMs = 0,
            TryReadDeviceIdentification = false
        }, null, null, null, CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.Equal(DeviceOnlineStatus.Online, results.Single(r => r.Address == 3).Status);
        Assert.All(results.Where(r => r.Address != 3), r => Assert.Equal(DeviceOnlineStatus.Offline, r.Status));

        // CC-6：扫描绝不产生写功能码请求
        Assert.NotEmpty(slave.ReceivedRequests);
        Assert.All(slave.ReceivedRequests, r => Assert.False(Fn.IsWrite(r.Function)));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Device_scan_treats_modbus_exception_as_online()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 4 };
        slave.UnsupportedFunctions.Add(Fn.ReadHoldingRegisters);
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var service = new DeviceScanService(session);
        var results = await service.ScanAsync(new DeviceScanOptions
        {
            StartAddress = 4,
            EndAddress = 4,
            ResponseTimeoutMs = 80,
            IntervalMs = 0,
            TryReadDeviceIdentification = false
        }, null, null, null, CancellationToken.None);

        var item = Assert.Single(results);
        Assert.Equal(DeviceOnlineStatus.Online, item.Status);
        Assert.Equal((byte)0x01, item.ExceptionCode);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Data_scan_reads_registers_with_type_and_byte_order()
    {
        var slave = new ModbusSlaveSimulator();
        slave.HoldingRegisters[0] = 0x0001;
        slave.HoldingRegisters[1] = 0x0002;
        slave.HoldingRegisters[2] = 0x0003;
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var service = new DataScanService(session);
        var results = await service.ScanAsync(new DataScanOptions
        {
            UnitId = 1,
            Area = DataArea.HoldingRegisters,
            StartAddress = 0,
            EndAddress = 2,
            BatchSize = 125,
            Type = DataType.UInt16,
            IntervalMs = 0
        }, null, null, null, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(ScanItemStatus.Ok, r.Status));
        Assert.Equal("1", results[0].ValueText);
        Assert.Equal("0x0003", results[2].RawHex);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Data_scan_refines_blocks_containing_illegal_addresses()
    {
        var slave = new ModbusSlaveSimulator();
        for (ushort i = 0; i < 5; i++) slave.HoldingRegisters[i] = i;
        slave.IllegalAddresses.Add(2);

        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var service = new DataScanService(session);
        var results = await service.ScanAsync(new DataScanOptions
        {
            UnitId = 1,
            Area = DataArea.HoldingRegisters,
            StartAddress = 0,
            EndAddress = 4,
            BatchSize = 5,
            RefineInvalidBlocks = true,
            IntervalMs = 0
        }, null, null, null, CancellationToken.None);

        Assert.Equal(5, results.Count);
        Assert.Equal(ScanItemStatus.InvalidAddress, results.Single(r => r.Address == 2).Status);
        Assert.All(results.Where(r => r.Address != 2), r => Assert.Equal(ScanItemStatus.Ok, r.Status));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Data_scan_marks_all_points_timeout_when_slave_is_offline()
    {
        var slave = new ModbusSlaveSimulator { Offline = true };
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var service = new DataScanService(session);
        var results = await service.ScanAsync(new DataScanOptions
        {
            UnitId = 1,
            StartAddress = 0,
            EndAddress = 3,
            BatchSize = 4,
            IntervalMs = 0
        }, null, null, null, CancellationToken.None);

        Assert.Equal(4, results.Count);
        Assert.All(results, r => Assert.Equal(ScanItemStatus.Timeout, r.Status));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Manual_send_builds_rtu_frame_with_automatic_crc()
    {
        var (session, _) = CreateRtu();
        await session.ConnectAsync();

        var service = new ManualSendService(session);
        var frame = service.BuildFrame(0x01, Fn.ReadHoldingRegisters, new byte[] { 0x00, 0x00, 0x00, 0x0A });

        Assert.Equal("01 03 00 00 00 0A C5 CD", Comm.Core.HexUtil.ToHex(frame));
        Assert.True(service.Validate(frame).IsValid);

        // 只给主体时也能自动补校验
        var body = service.AppendChecksum(Comm.Core.HexUtil.Parse("01 03 00 00 00 0A"));
        Assert.Equal(Comm.Core.HexUtil.ToHex(frame), Comm.Core.HexUtil.ToHex(body));

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Manual_send_of_unconfirmed_write_is_blocked()
    {
        var (session, slave) = CreateRtu();
        await session.ConnectAsync();

        var service = new ManualSendService(session);
        var frame = service.BuildFrame(1, Fn.WriteSingleRegister, new byte[] { 0x00, 0x05, 0x12, 0x34 });
        var outcome = await service.SendFrameAsync(frame, confirmedWrite: false, broadcastConfirmed: false,
            allowBadChecksum: false, expectResponse: true, timeoutMs: null, CancellationToken.None);

        Assert.Equal(ExchangeStatus.Rejected, outcome.Status);
        Assert.Empty(slave.ReceivedRequests);

        var confirmed = await service.SendFrameAsync(frame, confirmedWrite: true, broadcastConfirmed: false,
            allowBadChecksum: false, expectResponse: true, timeoutMs: null, CancellationToken.None);

        Assert.Equal(ExchangeStatus.Ok, confirmed.Status);
        Assert.Equal((ushort)0x1234, slave.HoldingRegisters[5]);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Manual_send_blocks_frame_with_bad_checksum_unless_allowed()
    {
        var (session, _) = CreateRtu();
        await session.ConnectAsync();

        var service = new ManualSendService(session);
        var frame = Comm.Core.HexUtil.Parse("01 03 00 00 00 0A C5 CE"); // CRC 故意写错

        var blocked = await service.SendFrameAsync(frame, true, false, allowBadChecksum: false, expectResponse: true,
            timeoutMs: null, CancellationToken.None);
        Assert.Equal(ExchangeStatus.Rejected, blocked.Status);

        var allowed = await service.SendFrameAsync(frame, true, false, allowBadChecksum: true, expectResponse: true,
            timeoutMs: 100, CancellationToken.None);
        Assert.NotEqual(ExchangeStatus.Rejected, allowed.Status);

        await session.DisposeAsync();
    }

    [Fact]
    public void Manual_send_reports_input_problems_and_write_risk()
    {
        var service = new ManualSendService(CreateRtu().Session);
        Assert.False(service.ParseHex("01 03 0").IsSuccess);
        Assert.False(service.ParseHex("").IsSuccess);

        Assert.True(ManualSendService.RequiresWriteConfirmation(1, Fn.WriteSingleRegister, out var reason));
        Assert.Contains("写操作", reason);
        Assert.False(ManualSendService.RequiresWriteConfirmation(1, Fn.ReadHoldingRegisters, out _));
        Assert.Contains("写操作", ManualSendService.DescribeRisk(Fn.WriteSingleCoil));
    }
}
