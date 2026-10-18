namespace Comm.Core.Transport;

public enum TransportKind
{
    Serial,
    Tcp,
    Loopback
}

public enum ChannelState
{
    Closed,
    Opening,
    Open,
    Closing,
    Faulted
}

public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space
}

public enum SerialStopBits
{
    One = 1,
    OnePointFive = 3,
    Two = 2
}

public sealed record SerialSettings
{
    public string PortName { get; init; } = "COM1";
    public int BaudRate { get; init; } = 9600;
    public int DataBits { get; init; } = 8;
    public SerialParity Parity { get; init; } = SerialParity.None;
    public SerialStopBits StopBits { get; init; } = SerialStopBits.One;
    public int ReadTimeoutMs { get; init; } = 1000;
    public int WriteTimeoutMs { get; init; } = 1000;

    public string ToDisplayString() => $"{PortName} {BaudRate},{DataBits},{ParityCode(Parity)},{StopBitsCode(StopBits)}";

    public static char ParityCode(SerialParity p) => p switch
    {
        SerialParity.None => 'N',
        SerialParity.Odd => 'O',
        SerialParity.Even => 'E',
        SerialParity.Mark => 'M',
        _ => 'S'
    };

    public static string StopBitsCode(SerialStopBits s) => s switch
    {
        SerialStopBits.One => "1",
        SerialStopBits.OnePointFive => "1.5",
        _ => "2"
    };
}

public sealed record TcpSettings
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 502;
    public int ConnectTimeoutMs { get; init; } = 3000;
    public bool KeepAlive { get; init; } = true;
    public bool NoDelay { get; init; } = true;

    public string ToDisplayString() => $"{Host}:{Port}";
}

public readonly record struct TxStamp(DateTimeOffset Timestamp, long MonotonicTicks);

/// <summary>收到的一段字节及其到达时间。到达边界与帧边界无关。</summary>
public sealed class RxChunk
{
    public required byte[] Data { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required long MonotonicTicks { get; init; }
}

public sealed record ChannelStateChanged(ChannelState State, string? Reason = null, Errors.ErrorInfo? Error = null);

/// <summary>传输通道（协议栈 L0）。实现必须把平台异常转换为统一错误，不向上抛平台特有异常类型。</summary>
public interface ITransportChannel : IAsyncDisposable
{
    string Id { get; }
    TransportKind Kind { get; }
    ChannelState State { get; }

    /// <summary>面向用户的端点描述，如 "COM3" 或 "192.168.1.10:502"。</summary>
    string Endpoint { get; }

    event EventHandler<ChannelStateChanged>? StateChanged;

    Task OpenAsync(CancellationToken cancellationToken = default);
    Task CloseAsync();

    /// <summary>写出一段字节，返回写入完成时刻（用于时标）。</summary>
    ValueTask<TxStamp> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>接收字节块。通道关闭或故障时序列结束。</summary>
    IAsyncEnumerable<RxChunk> ReadAsync(CancellationToken cancellationToken = default);
}
