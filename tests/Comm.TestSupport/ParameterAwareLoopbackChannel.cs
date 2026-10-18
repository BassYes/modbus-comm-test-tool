using Comm.Core;
using Comm.Core.Transport;
using Comm.Protocols.Modbus;
using Comm.Transport;

namespace Comm.TestSupport;

/// <summary>
/// “参数感知”的回环通道（R-11 测试用，规划 22.3.6）：模拟只在指定波特率/校验位/停止位下才能正常应答的设备。
/// <para>
/// 参数不匹配时线上仍有字节，但成帧不可用（等价于把 CRC 破坏掉）：主站会得到“校验错误”，
/// 因此不会被误判为在线——这与现场“波特率对不上会收到杂乱数据”的现象一致。
/// </para>
/// </summary>
public sealed class ParameterAwareLoopbackChannel : ITransportChannel
{
    private readonly LoopbackChannel _inner;
    private readonly SerialSettings? _required;

    public ParameterAwareLoopbackChannel(ModbusSlaveSimulator device, SerialSettings settings, SerialSettings? required)
    {
        Settings = settings;
        _required = required;

        var codec = new ModbusRtuCodec();
        var responder = device.CreateResponder(codec);

        _inner = new LoopbackChannel(request =>
        {
            if (ShouldRespond is not null && !ShouldRespond()) return null;

            var bytes = responder(request);
            if (bytes is null) return null;
            if (Matches()) return bytes;

            // 参数不匹配 → 帧不可用（CRC 破坏）
            bytes[^1] ^= 0xFF;
            return bytes;
        }, responseDelayMs: 1, id: $"param-{settings.BaudRate}-{settings.Parity}-{settings.StopBits}");
    }

    /// <summary>通道当前使用的串口参数（由测试中的“重配器”改写）。</summary>
    public SerialSettings Settings { get; }

    /// <summary>设备要求匹配的参数；null 表示“宽容设备”（任何参数都正常应答）。</summary>
    public SerialSettings? Required => _required;

    /// <summary>每次请求前调用；返回 false 表示本次不应答（用于模拟超时/确认失败）。</summary>
    public Func<bool>? ShouldRespond { get; set; }

    public bool Matches() =>
        _required is null ||
        (_required.BaudRate == Settings.BaudRate &&
         _required.DataBits == Settings.DataBits &&
         _required.Parity == Settings.Parity &&
         _required.StopBits == Settings.StopBits);

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

    public IAsyncEnumerable<RxChunk> ReadAsync(CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
