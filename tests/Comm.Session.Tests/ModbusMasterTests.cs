using System.Runtime.CompilerServices;
using Comm.Core.Codecs;
using Comm.Core.Frames;
using Comm.Core.Transport;
using Comm.Protocols.Modbus;
using Comm.Session;
using Comm.TestSupport;
using Comm.Transport;
using Xunit;

namespace Comm.Session.Tests;

/// <summary>事务层：请求/响应匹配、重试、异常响应、写守卫、回显抑制（见规划 16.3.3、16.3.4）。</summary>
public class ModbusMasterTests
{
    private static (ModbusSession Session, ModbusSlaveSimulator Slave, CollectingFrameObserver Observer) CreateRtu(
        SessionOptions? options = null, ModbusSlaveSimulator? slave = null)
    {
        slave ??= new ModbusSlaveSimulator();
        var codec = new ModbusRtuCodec();
        var observer = new CollectingFrameObserver();
        var channel = new LoopbackChannel(slave.CreateResponder(codec), responseDelayMs: 1);
        var sessionOptions = options ?? new SessionOptions
        {
            ResponseTimeoutMs = 300,
            RetryCount = 0,
            TurnaroundDelayMs = 1,
            IsSerial = true,
            SuppressEcho = true
        };

        var session = new ModbusSession(channel, codec, sessionOptions, observer);
        return (session, slave, observer);
    }

    [Fact]
    public async Task Reads_holding_registers_successfully()
    {
        var (session, slave, observer) = CreateRtu();
        slave.HoldingRegisters[0] = 0x000A;
        slave.HoldingRegisters[1] = 0x0014;

        await session.ConnectAsync();
        var result = await session.ReadAsync(1, DataArea.HoldingRegisters, 0, 2);

        Assert.Equal(ExchangeStatus.Ok, result.Status);
        Assert.Equal(new ushort[] { 10, 20 }, result.ResponsePdu!.Registers);
        Assert.Equal(1, result.Attempts);
        Assert.True(result.Elapsed.TotalMilliseconds >= 0);
        Assert.Equal(1, session.Statistics.TxCount);
        Assert.Equal(1, session.Statistics.RxCount);

        // 报文旁路：TX 与 RX 都被记录，且 RX 带响应耗时
        Assert.Equal(1, observer.Count(FrameDirection.Tx));
        Assert.Equal(1, observer.Count(FrameDirection.Rx));
        Assert.NotNull(observer.Records.First(r => r.Direction == FrameDirection.Rx).ElapsedMs);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Wrong_slave_address_times_out_and_is_not_matched()
    {
        var (session, _, observer) = CreateRtu();
        await session.ConnectAsync();

        var result = await session.ReadAsync(9, DataArea.HoldingRegisters, 0, 1);

        Assert.Equal(ExchangeStatus.Timeout, result.Status);
        Assert.Equal(1, session.Statistics.TimeoutCount);
        Assert.DoesNotContain(observer.Records, r => r.Direction == FrameDirection.Rx);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Retries_until_configured_attempts_and_counts_timeouts()
    {
        var (session, slave, _) = CreateRtu(new SessionOptions
        {
            ResponseTimeoutMs = 80,
            RetryCount = 2,
            TurnaroundDelayMs = 1,
            IsSerial = true,
            SuppressEcho = false
        });
        slave.Offline = true;

        await session.ConnectAsync();
        var result = await session.ReadAsync(1, DataArea.HoldingRegisters, 0, 1);

        Assert.Equal(ExchangeStatus.Timeout, result.Status);
        Assert.Equal(3, result.Attempts);
        Assert.Equal(2, session.Statistics.RetryCount);
        Assert.Equal(3, session.Statistics.TxCount);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Modbus_exception_response_is_a_value_not_an_error()
    {
        var (session, slave, _) = CreateRtu();
        slave.IllegalAddresses.Add(5);

        await session.ConnectAsync();
        var result = await session.ReadAsync(1, DataArea.HoldingRegisters, 5, 1);

        Assert.Equal(ExchangeStatus.ModbusException, result.Status);
        Assert.Equal((byte)0x02, result.ExceptionCode);
        Assert.Equal(1, result.Attempts); // 异常响应不重试
        Assert.Equal(1, session.Statistics.ExceptionCount);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Checksum_error_is_reported_and_retriable()
    {
        var (session, slave, observer) = CreateRtu(new SessionOptions
        {
            ResponseTimeoutMs = 120,
            RetryCount = 1,
            TurnaroundDelayMs = 1,
            IsSerial = true,
            SuppressEcho = false
        });
        slave.CorruptChecksum = true;

        await session.ConnectAsync();
        var result = await session.ReadAsync(1, DataArea.HoldingRegisters, 0, 1);

        Assert.Equal(ExchangeStatus.ChecksumError, result.Status);
        Assert.Equal(2, result.Attempts);
        Assert.Contains(observer.Records, r => r.Status == FrameStatus.ChecksumError);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Unconfirmed_write_is_rejected_by_the_guard()
    {
        var (session, slave, observer) = CreateRtu();
        await session.ConnectAsync();

        var request = new ModbusRequest(1, ModbusRequests.WriteSingleRegister(0, 0x1234));
        var result = await session.ExecuteAsync(request);

        Assert.Equal(ExchangeStatus.Rejected, result.Status);
        Assert.Equal("VAL-4004", result.Error!.Code);
        Assert.DoesNotContain(observer.Records, r => r.Direction == FrameDirection.Tx);
        Assert.Empty(slave.ReceivedRequests);
        Assert.Equal(1, session.Statistics.RejectedCount);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Broadcast_write_requires_explicit_broadcast_confirmation()
    {
        var (session, slave, _) = CreateRtu();
        await session.ConnectAsync();

        var refused = await session.ExecuteAsync(new ModbusRequest(0, ModbusRequests.WriteSingleRegister(0, 1), Confirmed: true));
        Assert.Equal(ExchangeStatus.Rejected, refused.Status);
        Assert.Equal("VAL-4005", refused.Error!.Code);

        var accepted = await session.ExecuteAsync(new ModbusRequest(0, ModbusRequests.WriteSingleRegister(0, 1),
            Confirmed: true, BroadcastConfirmed: true));
        Assert.Equal(ExchangeStatus.Ok, accepted.Status);
        Assert.Equal((ushort)1, slave.HoldingRegisters[0]);
        Assert.Null(accepted.Response);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Confirmed_write_updates_slave_register()
    {
        var (session, slave, _) = CreateRtu();
        await session.ConnectAsync();

        var result = await session.ExecuteAsync(new ModbusRequest(1, ModbusRequests.WriteSingleRegister(7, 0xBEEF),
            Confirmed: true));

        Assert.Equal(ExchangeStatus.Ok, result.Status);
        Assert.Equal((ushort)0xBEEF, slave.HoldingRegisters[7]);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Rs485_echo_is_suppressed_and_does_not_break_the_exchange()
    {
        var (session, slave, observer) = CreateRtu(slave: new ModbusSlaveSimulator { EchoRequest = true });
        slave.HoldingRegisters[0] = 0x0102;

        await session.ConnectAsync();
        var result = await session.ReadAsync(1, DataArea.HoldingRegisters, 0, 1);

        Assert.Equal(ExchangeStatus.Ok, result.Status);
        Assert.Equal(new ushort[] { 0x0102 }, result.ResponsePdu!.Registers);
        Assert.Contains(observer.Records, r => r.Markers.HasFlag(FrameMarker.Echo));
        Assert.Equal(1, session.Statistics.EchoCount);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Cancelling_a_request_returns_cancelled_status()
    {
        var (session, slave, _) = CreateRtu(new SessionOptions
        {
            ResponseTimeoutMs = 5000,
            RetryCount = 0,
            TurnaroundDelayMs = 1,
            IsSerial = true,
            SuppressEcho = false
        });
        slave.Offline = true;

        await session.ConnectAsync();
        using var cts = new CancellationTokenSource(60);
        var result = await session.ReadAsync(1, DataArea.HoldingRegisters, 0, 1, cancellationToken: cts.Token);

        Assert.Equal(ExchangeStatus.Cancelled, result.Status);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Requests_are_serialized_on_one_link()
    {
        var slave = new ModbusSlaveSimulator { ResponseDelayMs = 25 };
        var (session, _, _) = CreateRtu(slave: slave);
        await session.ConnectAsync();

        for (int i = 0; i < 4; i++) slave.HoldingRegisters[(ushort)i] = (ushort)(i + 1);

        var results = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(i => session.ReadAsync(1, DataArea.HoldingRegisters, (ushort)i, 1)));

        Assert.All(results, r => Assert.Equal(ExchangeStatus.Ok, r.Status));
        Assert.Equal(4, session.Statistics.TxCount);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Tcp_session_matches_by_transaction_id()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 0x11 };
        var codec = new ModbusTcpCodec();
        var observer = new CollectingFrameObserver();
        var channel = new LoopbackChannel(slave.CreateResponder(codec), responseDelayMs: 1);
        slave.HoldingRegisters[3] = 0x4321;

        var options = new SessionOptions
        {
            ResponseTimeoutMs = 300,
            RetryCount = 0,
            TurnaroundDelayMs = 1,
            IsSerial = false,
            SuppressEcho = false
        };
        var session = new ModbusSession(channel, codec, options, observer);

        await session.ConnectAsync();
        var result = await session.ReadAsync(0x11, DataArea.HoldingRegisters, 3, 1);

        Assert.Equal(ExchangeStatus.Ok, result.Status);
        Assert.Equal(new ushort[] { 0x4321 }, result.ResponsePdu!.Registers);
        Assert.Contains("modbus-tcp", session.Codec.Name);

        await session.DisposeAsync();
    }

    // -----------------------------------------------------------------------------------------
    // 审计 F-01 回归：写门禁必须按"最终发送字节"判定，不能只看声明字段
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task RawAdu_declaring_read_but_carrying_write_is_rejected_without_tx()
    {
        var (session, slave, observer) = CreateRtu();
        await session.ConnectAsync();

        var codec = new ModbusRtuCodec();
        var declaredRead = ModbusRequests.Read(DataArea.HoldingRegisters, 0, 1);
        var writeFrame = codec.BuildAdu(ModbusRequests.WriteSingleRegister(5, 0x1234).ToBytes(), 1);

        // 声明"读"，原始帧里却是"写 0x1234"，且没有任何写确认
        var result = await session.ExecuteAsync(new ModbusRequest(1, declaredRead, RawAdu: writeFrame));

        Assert.Equal(ExchangeStatus.Rejected, result.Status);
        Assert.Equal(Comm.Core.Errors.ErrorCodes.ValidationRawAduMismatch, result.Error!.Code);
        Assert.Equal(0, session.Statistics.TxCount);                          // 一个字节都没发
        Assert.DoesNotContain(observer.Records, r => r.Direction == FrameDirection.Tx);
        Assert.Empty(slave.ReceivedRequests);
        Assert.False(slave.HoldingRegisters.ContainsKey(5));                   // 设备数据没被改

        await session.DisposeAsync();
    }

    [Fact]
    public async Task RawAdu_with_broadcast_bytes_under_normal_address_is_rejected()
    {
        var (session, slave, _) = CreateRtu();
        await session.ConnectAsync();

        var codec = new ModbusRtuCodec();
        var declared = ModbusRequests.Read(DataArea.HoldingRegisters, 0, 1);
        // 声明地址 1（普通地址），原始帧却是地址 0 的广播写
        var broadcastWrite = codec.BuildAdu(ModbusRequests.WriteSingleRegister(5, 0x2222).ToBytes(), 0);

        var result = await session.ExecuteAsync(new ModbusRequest(1, declared, RawAdu: broadcastWrite));

        Assert.Equal(ExchangeStatus.Rejected, result.Status);
        Assert.Equal(Comm.Core.Errors.ErrorCodes.ValidationRawAduMismatch, result.Error!.Code);
        Assert.Equal(0, session.Statistics.TxCount);
        Assert.Empty(slave.ReceivedRequests);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task RawAdu_write_frame_in_scan_priority_is_rejected_even_if_confirmed()
    {
        var (session, slave, observer) = CreateRtu();
        await session.ConnectAsync();

        var codec = new ModbusRtuCodec();
        var declaredRead = ModbusRequests.Read(DataArea.HoldingRegisters, 0, 1);
        var writeFrame = codec.BuildAdu(ModbusRequests.WriteSingleRegister(5, 0x3333).ToBytes(), 1);

        // 扫描优先级 + 已确认 + 原始帧是写：三种绕过条件叠加也必须被挡
        var result = await session.ExecuteAsync(new ModbusRequest(1, declaredRead, RequestPriority.Scan,
            Confirmed: true, BroadcastConfirmed: true, RawAdu: writeFrame));

        Assert.Equal(ExchangeStatus.Rejected, result.Status);
        Assert.Equal(0, session.Statistics.TxCount);
        Assert.DoesNotContain(observer.Records, r => r.Direction == FrameDirection.Tx);
        Assert.Empty(slave.ReceivedRequests);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task RawAdu_mismatch_is_rejected_on_tcp_too()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 0x11 };
        var codec = new ModbusTcpCodec();
        var observer = new CollectingFrameObserver();
        var channel = new LoopbackChannel(slave.CreateResponder(codec), responseDelayMs: 1);
        var options = new SessionOptions
        {
            ResponseTimeoutMs = 300,
            RetryCount = 0,
            TurnaroundDelayMs = 1,
            IsSerial = false,
            SuppressEcho = false
        };
        var session = new ModbusSession(channel, codec, options, observer);
        await session.ConnectAsync();

        var declaredRead = ModbusRequests.Read(DataArea.HoldingRegisters, 0, 1);
        var writeFrame = codec.BuildAdu(ModbusRequests.WriteSingleRegister(5, 0x4444).ToBytes(), 0x11, 1);

        var result = await session.ExecuteAsync(new ModbusRequest(0x11, declaredRead, RawAdu: writeFrame));

        Assert.Equal(ExchangeStatus.Rejected, result.Status);
        Assert.Equal(Comm.Core.Errors.ErrorCodes.ValidationRawAduMismatch, result.Error!.Code);
        Assert.Equal(0, session.Statistics.TxCount);
        Assert.DoesNotContain(observer.Records, r => r.Direction == FrameDirection.Tx);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Consistent_raw_adu_write_still_succeeds()
    {
        var (session, slave, _) = CreateRtu();
        await session.ConnectAsync();

        var codec = new ModbusRtuCodec();
        var pdu = ModbusRequests.WriteSingleRegister(6, 0x5555);
        var frame = codec.BuildAdu(pdu.ToBytes(), 1);

        // 声明与字节完全一致 + 已确认：保留合法路径
        var result = await session.ExecuteAsync(new ModbusRequest(1, pdu, Confirmed: true, RawAdu: frame));

        Assert.Equal(ExchangeStatus.Ok, result.Status);
        Assert.Equal((ushort)0x5555, slave.HoldingRegisters[6]);
        Assert.Equal(1, session.Statistics.TxCount);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Cancelled_queued_write_is_never_sent()
    {
        var slave = new ModbusSlaveSimulator();
        var codec = new ModbusRtuCodec();
        var channel = new GatedFirstWriteChannel(slave.CreateResponder(codec));
        var observer = new CollectingFrameObserver();
        var session = new ModbusSession(channel, codec, new SessionOptions
        {
            ResponseTimeoutMs = 1000,
            RetryCount = 0,
            TurnaroundDelayMs = 1,
            IsSerial = true,
            SuppressEcho = false
        }, observer);

        await session.ConnectAsync();
        var first = session.ReadAsync(1, DataArea.HoldingRegisters, 0, 1);
        await channel.FirstWriteStarted.WaitAsync(TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource();
        var write = session.ExecuteAsync(new ModbusRequest(1, ModbusRequests.WriteSingleRegister(5, 0x1234),
            Confirmed: true), cts.Token);
        cts.Cancel();
        // 这条读取排在已取消的写请求之后；完成时调度器已经处理过取消项。
        var after = session.ReadAsync(1, DataArea.HoldingRegisters, 1, 1);
        channel.ReleaseFirstWrite();

        Assert.Equal(ExchangeStatus.Cancelled, (await write).Status);
        Assert.Equal(ExchangeStatus.Ok, (await first).Status);
        Assert.Equal(ExchangeStatus.Ok, (await after).Status);
        Assert.False(slave.HoldingRegisters.ContainsKey(5));
        Assert.Equal(2, observer.Count(FrameDirection.Tx));
        Assert.Equal(2, slave.ReceivedRequests.Count);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Split_read_response_restores_prefix_when_it_is_not_an_echo()
    {
        var slave = new ModbusSlaveSimulator();
        slave.HoldingRegisters[0] = 0x1234;
        var codec = new ModbusRtuCodec();
        var channel = new SplitResponseChannel(slave.CreateResponder(codec));
        var session = new ModbusSession(channel, codec, new SessionOptions
        {
            ResponseTimeoutMs = 300,
            RetryCount = 0,
            TurnaroundDelayMs = 1,
            IsSerial = true,
            SuppressEcho = true
        });

        await session.ConnectAsync();
        var result = await session.ReadAsync(1, DataArea.HoldingRegisters, 0, 1);

        Assert.Equal(ExchangeStatus.Ok, result.Status);
        Assert.Equal(new ushort[] { 0x1234 }, result.ResponsePdu!.Registers);
        Assert.Equal(0, session.Statistics.EchoCount);

        await session.DisposeAsync();
    }

    [Fact]
    public async Task Tcp_raw_adu_matches_response_using_its_mbap_transaction_id()
    {
        var slave = new ModbusSlaveSimulator { UnitId = 0x11 };
        slave.HoldingRegisters[3] = 0x4321;
        var codec = new ModbusTcpCodec();
        var channel = new LoopbackChannel(slave.CreateResponder(codec), responseDelayMs: 1);
        var session = new ModbusSession(channel, codec, new SessionOptions
        {
            ResponseTimeoutMs = 300,
            RetryCount = 0,
            TurnaroundDelayMs = 1,
            IsSerial = false,
            SuppressEcho = false
        });

        await session.ConnectAsync();
        var pdu = ModbusRequests.Read(DataArea.HoldingRegisters, 3, 1);
        var rawAdu = codec.BuildAdu(pdu.ToBytes(), 0x11, transactionId: 0x1234);
        var result = await session.ExecuteAsync(new ModbusRequest(0x11, pdu, RawAdu: rawAdu));

        Assert.Equal(ExchangeStatus.Ok, result.Status);
        Assert.Equal(rawAdu, result.Request);
        Assert.Equal(new ushort[] { 0x4321 }, result.ResponsePdu!.Registers);
        Assert.Equal(0x1234, (result.Response![0] << 8) | result.Response[1]);

        var next = await session.ReadAsync(0x11, DataArea.HoldingRegisters, 3, 1);
        Assert.Equal(ExchangeStatus.Ok, next.Status);
        Assert.Equal(0x1235, (next.Request[0] << 8) | next.Request[1]);

        await session.DisposeAsync();
    }

    private sealed class SplitResponseChannel : ITransportChannel
    {
        private readonly LoopbackChannel _inner;

        public SplitResponseChannel(Func<byte[], byte[]?> responder) =>
            _inner = new LoopbackChannel(responder, responseDelayMs: 1);

        public string Id => _inner.Id;
        public TransportKind Kind => _inner.Kind;
        public string Endpoint => _inner.Endpoint;
        public ChannelState State => _inner.State;
        public event EventHandler<ChannelStateChanged>? StateChanged
        {
            add => _inner.StateChanged += value;
            remove => _inner.StateChanged -= value;
        }

        public Task OpenAsync(CancellationToken cancellationToken = default) => _inner.OpenAsync(cancellationToken);
        public Task CloseAsync() => _inner.CloseAsync();
        public ValueTask<TxStamp> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(data, cancellationToken);

        public async IAsyncEnumerable<RxChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in _inner.ReadAsync(cancellationToken))
            {
                // 确保请求已完成发送并启用回显抑制，然后把响应头与剩余字节分两次交付。
                await Task.Delay(20, cancellationToken);
                yield return new RxChunk
                {
                    Data = chunk.Data[..2], Timestamp = chunk.Timestamp, MonotonicTicks = chunk.MonotonicTicks
                };
                yield return new RxChunk
                {
                    Data = chunk.Data[2..], Timestamp = chunk.Timestamp, MonotonicTicks = chunk.MonotonicTicks
                };
            }
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class GatedFirstWriteChannel : ITransportChannel
    {
        private readonly LoopbackChannel _inner;
        private readonly TaskCompletionSource _firstWriteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;

        public GatedFirstWriteChannel(Func<byte[], byte[]?> responder) =>
            _inner = new LoopbackChannel(responder, responseDelayMs: 1);

        public Task FirstWriteStarted => _firstWriteStarted.Task;
        public void ReleaseFirstWrite() => _releaseFirstWrite.TrySetResult();
        public string Id => _inner.Id;
        public TransportKind Kind => _inner.Kind;
        public string Endpoint => _inner.Endpoint;
        public ChannelState State => _inner.State;
        public event EventHandler<ChannelStateChanged>? StateChanged
        {
            add => _inner.StateChanged += value;
            remove => _inner.StateChanged -= value;
        }

        public Task OpenAsync(CancellationToken cancellationToken = default) => _inner.OpenAsync(cancellationToken);
        public Task CloseAsync() => _inner.CloseAsync();

        public async ValueTask<TxStamp> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) == 1)
            {
                _firstWriteStarted.TrySetResult();
                await _releaseFirstWrite.Task.WaitAsync(cancellationToken);
            }
            return await _inner.WriteAsync(data, cancellationToken);
        }

        public IAsyncEnumerable<RxChunk> ReadAsync(CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
