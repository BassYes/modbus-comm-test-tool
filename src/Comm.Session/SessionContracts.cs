using Comm.Core.Errors;
using Comm.Protocols.Modbus;

namespace Comm.Session;

/// <summary>请求优先级（见规划 13.3）。数字越小优先级越高。</summary>
public enum RequestPriority
{
    Interactive = 0,
    Periodic = 1,
    Scan = 2
}

/// <summary>事务结果状态。预期内的协议结果在这里表达，不抛异常。</summary>
public enum ExchangeStatus
{
    Ok,
    ModbusException,
    Timeout,
    ChecksumError,
    FrameError,
    Unmatched,
    TransportError,
    Cancelled,
    Rejected,
    NotConnected
}

public sealed record ExchangeResult(
    ExchangeStatus Status,
    byte UnitId,
    byte FunctionCode,
    byte[] Request,
    byte[]? Response,
    ParsedPdu? ResponsePdu,
    byte? ExceptionCode,
    int Attempts,
    TimeSpan Elapsed,
    ErrorInfo? Error = null,
    long? RequestSeq = null,
    long? ResponseSeq = null)
{
    public bool IsSuccess => Status == ExchangeStatus.Ok;

    public string StatusText => Status switch
    {
        ExchangeStatus.Ok => "正常",
        ExchangeStatus.ModbusException => $"异常响应 0x{ExceptionCode:X2}",
        ExchangeStatus.Timeout => "超时",
        ExchangeStatus.ChecksumError => "校验错误",
        ExchangeStatus.FrameError => "帧格式错误",
        ExchangeStatus.Unmatched => "响应未匹配",
        ExchangeStatus.TransportError => "链路错误",
        ExchangeStatus.Cancelled => "已取消",
        ExchangeStatus.Rejected => "已拒绝",
        ExchangeStatus.NotConnected => "未连接",
        _ => Status.ToString()
    };

    public static ExchangeResult NotConnectedResult(ModbusRequest request, ErrorInfo error) =>
        new(ExchangeStatus.NotConnected, request.UnitId, request.Pdu.Function, Array.Empty<byte>(), null, null, null, 0,
            TimeSpan.Zero, error);
}

public sealed record ModbusRequest(
    byte UnitId,
    ModbusPdu Pdu,
    RequestPriority Priority = RequestPriority.Interactive,
    bool Confirmed = false,
    bool BroadcastConfirmed = false,
    string? Label = null,
    int? TimeoutMs = null,
    int? RetryCount = null,
    byte[]? RawAdu = null,
    bool NoResponseExpected = false)
{
    public bool IsWrite => Fn.IsWrite(Pdu.Function);

    public static ModbusRequest Scan(byte unitId, ModbusPdu pdu, string? label = null, int? timeoutMs = null) =>
        new(unitId, pdu, RequestPriority.Scan, false, false, label, timeoutMs);

    public static ModbusRequest Periodic(byte unitId, ModbusPdu pdu, string? label = null) =>
        new(unitId, pdu, RequestPriority.Periodic, false, false, label);
}

/// <summary>会话参数与默认值（见规划 15.5 的超时与重试默认值）。</summary>
public sealed record SessionOptions
{
    public string SessionId { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; init; } = "未命名会话";

    /// <summary>响应超时（毫秒）。</summary>
    public int ResponseTimeoutMs { get; init; } = 1000;

    /// <summary>重试次数（不含首次发送）。手动发送默认 0，见规划 15.5。</summary>
    public int RetryCount { get; init; } = 2;

    /// <summary>请求间最小间隔（RTU 轮转延迟）。</summary>
    public int TurnaroundDelayMs { get; init; } = 20;

    /// <summary>RS-485 回显抑制。</summary>
    public bool SuppressEcho { get; init; } = true;

    public int EchoWindowMs { get; init; } = 200;

    /// <summary>广播（地址 0）写请求后的等待时间。</summary>
    public int BroadcastWaitMs { get; init; } = 100;

    /// <summary>是否允许写请求重试；默认不重试，避免重复写入。</summary>
    public bool RetryWrites { get; init; }

    public bool IsSerial { get; init; } = true;
}

/// <summary>会话统计（底部状态栏与任务中心使用）。</summary>
public sealed class SessionStatistics
{
    private long _tx, _rx, _timeouts, _checksumErrors, _exceptions, _unmatched, _retries, _echoes, _rejected;

    public long TxCount => Interlocked.Read(ref _tx);
    public long RxCount => Interlocked.Read(ref _rx);
    public long TimeoutCount => Interlocked.Read(ref _timeouts);
    public long ChecksumErrorCount => Interlocked.Read(ref _checksumErrors);
    public long ExceptionCount => Interlocked.Read(ref _exceptions);
    public long UnmatchedCount => Interlocked.Read(ref _unmatched);
    public long RetryCount => Interlocked.Read(ref _retries);
    public long EchoCount => Interlocked.Read(ref _echoes);
    public long RejectedCount => Interlocked.Read(ref _rejected);

    /// <summary>错误计数：超时 + 校验错误 + 未匹配（异常响应是有效协议结果，不计入错误）。</summary>
    public long ErrorCount => TimeoutCount + ChecksumErrorCount + UnmatchedCount;

    internal void IncTx() => Interlocked.Increment(ref _tx);
    internal void IncRx() => Interlocked.Increment(ref _rx);
    internal void IncTimeout() => Interlocked.Increment(ref _timeouts);
    internal void IncChecksum() => Interlocked.Increment(ref _checksumErrors);
    internal void IncException() => Interlocked.Increment(ref _exceptions);
    internal void IncUnmatched() => Interlocked.Increment(ref _unmatched);
    internal void IncRetry() => Interlocked.Increment(ref _retries);
    internal void IncEcho() => Interlocked.Increment(ref _echoes);
    internal void IncRejected() => Interlocked.Increment(ref _rejected);

    public void Reset()
    {
        Interlocked.Exchange(ref _tx, 0);
        Interlocked.Exchange(ref _rx, 0);
        Interlocked.Exchange(ref _timeouts, 0);
        Interlocked.Exchange(ref _checksumErrors, 0);
        Interlocked.Exchange(ref _exceptions, 0);
        Interlocked.Exchange(ref _unmatched, 0);
        Interlocked.Exchange(ref _retries, 0);
        Interlocked.Exchange(ref _echoes, 0);
        Interlocked.Exchange(ref _rejected, 0);
    }

    public string ToStatusText() =>
        $"TX {TxCount} | RX {RxCount} | 超时 {TimeoutCount} | 校验错误 {ChecksumErrorCount} | 异常响应 {ExceptionCount} | 未匹配 {UnmatchedCount}";
}
