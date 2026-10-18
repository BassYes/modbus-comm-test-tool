using Comm.Core.Errors;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Session;
using Comm.TestSupport;
using Comm.Transport;
using Xunit;

namespace Comm.Scanning.Tests;

/// <summary>
/// 设备扫描的手动探测模式（R-17）与报文工具的离线构建 / 显式协议选择（R-19）。
/// </summary>
public class ManualProbeAndOfflineFrameTests
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

    // ---------------------------------------------------------------- R-17 手动探测

    [Fact]
    public void Manual_mode_uses_exactly_one_template()
    {
        var auto = new DeviceScanOptions { StartAddress = 1, EndAddress = 10 };
        var manual = new DeviceScanOptions
        {
            StartAddress = 1,
            EndAddress = 10,
            Mode = DeviceProbeMode.Manual,
            ManualProbeFunction = Fn.ReadInputRegisters,
            ManualProbeAddress = 100,
            ManualProbeQuantity = 2
        };

        Assert.Equal(4, auto.ProbeTemplates().Count);

        var template = Assert.Single(manual.ProbeTemplates());
        Assert.Equal(Fn.ReadInputRegisters, template.Function);
        Assert.Equal(DataArea.InputRegisters, template.Area);
        Assert.Equal(100, template.Address);
        Assert.Equal(2, template.Quantity);

        // 手动模式按 1 个模板估算耗时，明显小于自动模式的 4 个模板
        Assert.True(manual.EstimatedDuration() < auto.EstimatedDuration());
    }

    [Fact]
    public void Manual_mode_rejects_write_functions_and_bad_quantities()
    {
        var write = new DeviceScanOptions
        {
            Mode = DeviceProbeMode.Manual,
            ManualProbeFunction = Fn.WriteSingleRegister,
            ManualProbeQuantity = 1
        };
        Assert.False(DeviceScanOptions.Validate(write, out var writeError));
        Assert.Equal(ErrorCodes.ValidationScanWriteBlocked, writeError!.Code);

        var tooMany = new DeviceScanOptions
        {
            Mode = DeviceProbeMode.Manual,
            ManualProbeFunction = Fn.ReadHoldingRegisters,
            ManualProbeQuantity = 126 // 03 的协议上限是 125
        };
        Assert.False(DeviceScanOptions.Validate(tooMany, out var quantityError));
        Assert.Equal(ErrorCodes.ValidationQuantity, quantityError!.Code);

        var overflow = new DeviceScanOptions
        {
            Mode = DeviceProbeMode.Manual,
            ManualProbeFunction = Fn.ReadCoils,
            ManualProbeAddress = 65530,
            ManualProbeQuantity = 20
        };
        Assert.False(DeviceScanOptions.Validate(overflow, out _));

        Assert.True(DeviceScanOptions.Validate(new DeviceScanOptions(), out _));
    }

    [Fact]
    public async Task Scan_service_refuses_write_function_even_if_validation_is_bypassed()
    {
        var (session, slave) = CreateRtu();
        await session.ConnectAsync();

        var service = new DeviceScanService(session);
        var error = await Assert.ThrowsAsync<CommException>(() => service.ScanAsync(new DeviceScanOptions
        {
            StartAddress = 1,
            EndAddress = 2,
            Mode = DeviceProbeMode.Manual,
            ManualProbeFunction = Fn.WriteMultipleRegisters,
            ManualProbeQuantity = 1,
            IntervalMs = 0
        }, null, null, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.ValidationScanWriteBlocked, error.Error.Code);
        Assert.Empty(slave.ReceivedRequests);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Manual_probe_sends_only_the_chosen_read_request()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 3 };
        slave.InputRegisters[100] = 0x1234;
        slave.InputRegisters[101] = 0x5678;
        var (session, _) = CreateRtu(slave);
        await session.ConnectAsync();

        var results = await new DeviceScanService(session).ScanAsync(new DeviceScanOptions
        {
            StartAddress = 3,
            EndAddress = 3,
            ResponseTimeoutMs = 80,
            IntervalMs = 0,
            TryReadDeviceIdentification = false,
            Mode = DeviceProbeMode.Manual,
            ManualProbeFunction = Fn.ReadInputRegisters,
            ManualProbeAddress = 100,
            ManualProbeQuantity = 2
        }, null, null, null, CancellationToken.None);

        Assert.Equal(DeviceOnlineStatus.Online, Assert.Single(results).Status);

        var request = Assert.Single(slave.ReceivedRequests);
        Assert.Equal(Fn.ReadInputRegisters, request.Function);
        Assert.Equal(new byte[] { 0x00, 0x64, 0x00, 0x02 }, request.Data);

        await session.DisposeAsync();
    }

    // ---------------------------------------------------------------- R-19 离线构建报文

    [Fact]
    public void Tcp_frame_can_be_built_without_any_session()
    {
        var service = new ManualSendService(new ModbusTcpCodec());
        var pdu = ModbusRequests.Read(DataArea.HoldingRegisters, 0, 1);

        var frame = service.BuildFrame(1, Fn.ReadHoldingRegisters, pdu.Data, transactionId: 0x0102);

        // MBAP：事务标识 | 协议标识 0 | 长度 | 单元标识 | PDU
        Assert.Equal("01 02 00 00 00 06 01 03 00 00 00 01", Comm.Core.HexUtil.ToHex(frame));
        Assert.True(service.Validate(frame).IsValid);
        Assert.Contains("MBAP 正确", service.Validate(frame).Message);
        Assert.False(service.CanSend); // 未连接：只能生成与校验

        var mbap = service.DescribeMbufFields(frame);
        Assert.Contains("事务标识 258", mbap);
        Assert.Contains("协议标识 0", mbap);
        Assert.Contains("长度 6", mbap);
        Assert.Contains("Length 校验：正确", mbap);

        // Modbus TCP 不追加 RTU CRC
        Assert.Equal(0, service.Codec.ChecksumLength);
        Assert.Equal(7 + pdu.ToBytes().Length, frame.Length);
        Assert.Equal(Comm.Core.HexUtil.ToHex(frame), Comm.Core.HexUtil.ToHex(service.AppendChecksum(frame)));
    }

    [Fact]
    public void Rtu_frame_can_be_built_without_any_session_and_keeps_low_byte_first_crc()
    {
        var service = new ManualSendService(new ModbusRtuCodec());
        var frame = service.BuildFrame(0x01, Fn.ReadHoldingRegisters, new byte[] { 0x00, 0x00, 0x00, 0x0A });

        Assert.Equal("01 03 00 00 00 0A C5 CD", Comm.Core.HexUtil.ToHex(frame));
        Assert.True(service.Validate(frame).IsValid);
        Assert.Empty(service.DescribeMbufFields(frame)); // RTU 没有 MBAP
        Assert.False(service.CanSend);
        Assert.False(service.ParseHex("01 03").IsSuccess); // 未连接也要能给出输入错误
    }

    [Fact]
    public async Task Sending_without_a_session_is_rejected_as_a_value_not_an_exception()
    {
        var service = new ManualSendService(new ModbusTcpCodec());
        var frame = Comm.Core.HexUtil.Parse("01 02 00 00 00 06 01 03 00 00 00 01");

        var outcome = await service.SendFrameAsync(frame, confirmedWrite: true, broadcastConfirmed: false,
            allowBadChecksum: false, expectResponse: true, timeoutMs: null, CancellationToken.None);

        Assert.Equal(ExchangeStatus.Rejected, outcome.Status);
        Assert.Contains("尚未连接", outcome.Message);
    }

    [Fact]
    public async Task Sending_a_tcp_frame_over_an_rtu_session_is_blocked()
    {
        var (session, slave) = CreateRtu();
        await session.ConnectAsync();

        // 会话是 RTU，但报文按 TCP 构建：必须阻止，不做静默转换
        var service = new ManualSendService(new ModbusTcpCodec(), session);
        var frame = service.BuildFrame(1, Fn.ReadHoldingRegisters, new byte[] { 0x00, 0x00, 0x00, 0x01 });

        var outcome = await service.SendFrameAsync(frame, true, false, false, true, null, CancellationToken.None);

        Assert.Equal(ExchangeStatus.Rejected, outcome.Status);
        Assert.Contains("不一致", outcome.Message);
        Assert.Empty(slave.ReceivedRequests);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Session_bound_service_still_sends_rtu_frames()
    {
        var (session, slave) = CreateRtu();
        await session.ConnectAsync();

        var service = new ManualSendService(session);
        Assert.True(service.CanSend);
        Assert.True(service.MatchesSessionProtocol);

        var frame = service.BuildFrame(1, Fn.ReadHoldingRegisters, new byte[] { 0x00, 0x00, 0x00, 0x01 });
        var outcome = await service.SendFrameAsync(frame, true, false, false, true, null, CancellationToken.None);

        Assert.Equal(ExchangeStatus.Ok, outcome.Status);
        Assert.Single(slave.ReceivedRequests);

        await session.DisposeAsync();
    }
}
