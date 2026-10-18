namespace Comm.Core.Frames;

public enum FrameDirection
{
    Tx,
    Rx,
    System
}

/// <summary>成帧结果状态（见规划 14.3.1）。</summary>
public enum FrameStatus
{
    Valid,
    ChecksumError,
    LengthError,
    Truncated,
    Garbage,
    Ambiguous,
    ProtocolError
}

/// <summary>报文附加标记（见规划 13.5、14.2.2）。</summary>
[Flags]
public enum FrameMarker
{
    None = 0,
    Echo = 1,
    Late = 2,
    Unmatched = 4,
    Broadcast = 8,
    Write = 16,
    Noise = 32
}

/// <summary>统一的报文记录，供监视、落盘与解析使用。</summary>
public sealed class FrameRecord
{
    public long Seq { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public FrameDirection Direction { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public long MonotonicTicks { get; init; }
    public long CompletedMonotonicTicks { get; init; }
    public byte[] RawBytes { get; init; } = Array.Empty<byte>();
    public FrameStatus Status { get; init; } = FrameStatus.Valid;
    public FrameMarker Markers { get; set; }
    public byte? UnitId { get; init; }
    public byte? FunctionCode { get; init; }
    public string? Summary { get; set; }
    public string? Detail { get; init; }
    public double? ElapsedMs { get; init; }
    public long? RequestSeq { get; init; }
    public string? ErrorCode { get; init; }

    private string? _hex;
    public string Hex => _hex ??= Core.HexUtil.ToHex(RawBytes);

    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
    public string ElapsedText => ElapsedMs is { } ms ? ms.ToString("F1") : string.Empty;
    public string UnitIdText => UnitId?.ToString() ?? string.Empty;
    public string FunctionText => FunctionCode is { } fc ? $"0x{fc:X2}" : string.Empty;

    public string DirectionText => Direction switch
    {
        FrameDirection.Tx => "TX",
        FrameDirection.Rx => "RX",
        _ => "SYS"
    };

    public string StatusText => Status switch
    {
        FrameStatus.Valid => string.Empty,
        FrameStatus.ChecksumError => "校验错误",
        FrameStatus.LengthError => "长度错误",
        FrameStatus.Truncated => "半帧",
        FrameStatus.Garbage => "噪声",
        FrameStatus.Ambiguous => "歧义帧",
        FrameStatus.ProtocolError => "协议错误",
        _ => Status.ToString()
    };

    public string MarkerText
    {
        get
        {
            if (Markers == FrameMarker.None) return string.Empty;
            var parts = new List<string>(3);
            if (Markers.HasFlag(FrameMarker.Echo)) parts.Add("ECHO");
            if (Markers.HasFlag(FrameMarker.Late)) parts.Add("LATE");
            if (Markers.HasFlag(FrameMarker.Unmatched)) parts.Add("UNMATCHED");
            if (Markers.HasFlag(FrameMarker.Broadcast)) parts.Add("BROADCAST");
            if (Markers.HasFlag(FrameMarker.Write)) parts.Add("WRITE");
            if (Markers.HasFlag(FrameMarker.Noise)) parts.Add("NOISE");
            return string.Join('|', parts);
        }
    }

    public bool IsError => Status != FrameStatus.Valid;
}

/// <summary>报文观察者（FrameTap 的下游，见规划 13.5、14.1）。</summary>
public interface IFrameObserver
{
    void OnFrame(FrameRecord record);
}

/// <summary>报文序号生成器。</summary>
public sealed class FrameSequence
{
    private long _value;
    public long Next() => Interlocked.Increment(ref _value);
    public long Current => Interlocked.Read(ref _value);
}
