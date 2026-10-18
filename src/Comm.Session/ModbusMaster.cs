using System.Diagnostics;
using System.Threading.Channels;
using Comm.Core;
using Comm.Core.Codecs;
using Comm.Core.Errors;
using Comm.Core.Frames;
using Comm.Core.Transport;
using Comm.Protocols.Modbus;

namespace Comm.Session;

/// <summary>
/// 事务层（协议栈 L3，见规划 14.5）。
/// 约束：同一链路同一时刻只有一个未完成请求（CC-1）；写请求必须已确认（CC-6/15.7）。
/// </summary>
public sealed class ModbusMaster : IAsyncDisposable
{
    private readonly ITransportChannel _channel;
    private readonly IFrameCodec _codec;
    private readonly SessionOptions _options;
    private readonly IFrameObserver? _observer;
    private readonly FrameSequence _sequence = new();

    private readonly Channel<PendingRequest>[] _queues =
    {
        Channel.CreateUnbounded<PendingRequest>(new UnboundedChannelOptions { SingleReader = true }),
        Channel.CreateUnbounded<PendingRequest>(new UnboundedChannelOptions { SingleReader = true }),
        Channel.CreateUnbounded<PendingRequest>(new UnboundedChannelOptions { SingleReader = true })
    };

    /// <summary>
    /// 队列到达信号（M4）：优先级队列本身是 Channel，但需要“三个队列里任意一个有数据”的等待语义，
    /// 因此用信号量做唤醒。原来用 <c>Task.Delay(5)</c> 轮询，实测每次事务多等约 5~15 ms
    /// （回环往返仅 ~64 次/s）；改为信号量后延迟取决于线程调度（微秒级）。
    /// </summary>
    private readonly SemaphoreSlim _queueSignal = new(0);

    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private Task? _readLoop;
    private Task? _dispatchLoop;
    private TaskCompletionSource<FrameArrival>? _pendingResponse;
    private byte _pendingUnitId;
    private byte _pendingFunction;
    private ushort _pendingTransaction;
    private bool _pendingChecksumError;
    private byte[]? _lastTxBytes;
    private long _lastTxTicks;
    private ushort _transactionCounter;

    // RS-485 半双工回显：按字节级别与刚发出的帧比对，避免回显被误当成响应（见规划 14.2.2）。
    private byte[]? _echoExpected;
    private int _echoMatched;

    // 迟到的响应：超时之后到达，仍要记录但不再匹配给后续请求（见规划 14.5 第 6 条）。
    private byte? _lateUnitId;
    private byte? _lateFunction;
    private long _lateSinceTicks;
    private int _lateWindowMs;

    public ModbusMaster(ITransportChannel channel, IFrameCodec codec, SessionOptions options, IFrameObserver? observer = null)
    {
        _channel = channel;
        _codec = codec;
        _options = options;
        _observer = observer;
    }

    public SessionOptions Options => _options;
    public SessionStatistics Statistics { get; } = new();
    public bool IsRunning => _dispatchLoop is { IsCompleted: false };

    /// <summary>当前传输通道（命名为 Transport，避免与 System.Threading.Channels.Channel 冲突）。</summary>
    public ITransportChannel Transport => _channel;

    private IFramer? _framer;

    private sealed record PendingRequest(ModbusRequest Request, TaskCompletionSource<ExchangeResult> Completion);

    private sealed record FrameArrival(byte[] Bytes, FrameCandidate Candidate, DateTimeOffset Timestamp, long MonotonicTicks, long CompletedTicks, long Seq);

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
        _dispatchLoop = Task.Run(() => DispatchLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        var cts = _cts;
        if (cts is null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        // 让仍在排队的请求以“已取消”返回，避免调用方永久等待。
        foreach (var queue in _queues)
        {
            while (queue.Reader.TryRead(out var pending))
            {
                pending.Completion.TrySetResult(CancelledResult(pending.Request));
            }
        }

        // 清掉可能残留的唤醒信号，避免下一次启动时空转一圈。
        while (_queueSignal.CurrentCount > 0)
        {
            try
            {
                _queueSignal.Wait(0);
            }
            catch (Exception)
            {
                break;
            }
        }

        try
        {
            if (_dispatchLoop is not null) await _dispatchLoop.ConfigureAwait(false);
            if (_readLoop is not null) await _readLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cts?.Dispose();
        _cts = null;
        _dispatchLoop = null;
        _readLoop = null;
    }

    /// <summary>提交一次事务（请求/响应）。</summary>
    public async Task<ExchangeResult> ExecuteAsync(ModbusRequest request, CancellationToken cancellationToken = default)
    {
        if (!IsRunning)
        {
            return ExchangeResult.NotConnectedResult(request, new ErrorInfo(ErrorCodes.TransportNotOpen,
                ErrorCategory.Transport, Severity.Error, "会话未启动，请先连接"));
        }

        if (!TryValidateFinalAdu(request, out var aduError))
        {
            Statistics.IncRejected();
            return new ExchangeResult(ExchangeStatus.Rejected, request.UnitId, request.Pdu.Function,
                Array.Empty<byte>(), null, null, null, 0, TimeSpan.Zero, aduError);
        }

        if (GuardReject(request, out var rejectError))
        {
            Statistics.IncRejected();
            return new ExchangeResult(ExchangeStatus.Rejected, request.UnitId, request.Pdu.Function,
                Array.Empty<byte>(), null, null, null, 0, TimeSpan.Zero, rejectError);
        }

        var completion = new TaskCompletionSource<ExchangeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(request, completion);

        await _queues[(int)request.Priority].Writer.WriteAsync(pending, cancellationToken).ConfigureAwait(false);

        // 唤醒调度循环：把它从无请求的等待中叫醒（多释放一次不会造成问题，TryDequeue 会兜底）。
        if (_queueSignal.CurrentCount == 0)
        {
            try
            {
                _queueSignal.Release();
            }
            catch (SemaphoreFullException)
            {
                // 已有待处理的唤醒信号，忽略
            }
        }

        using var registration = cancellationToken.Register(() => completion.TrySetResult(CancelledResult(request)));
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// 按"最终要写进总线的字节"校验请求（审计 F-01）。
    /// <para>
    /// 背景：<see cref="ModbusRequest"/> 允许同时携带声明的 <see cref="ModbusRequest.Pdu"/> /
    /// <see cref="ModbusRequest.UnitId"/> 和一段原始 ADU；原实现只用声明字段判断"是不是写/是不是广播"，
    /// 实际发送的却是 <see cref="ModbusRequest.RawAdu"/>——于是"声明一次读 + 原始帧里塞一条写"
    /// 或"声明普通地址 + 原始帧广播"可以绕过写确认与广播确认。
    /// </para>
    /// <para>
    /// 现在在入队前先解码最终帧，要求：校验合法、Unit ID 相同、功能码相同、PDU 逐字节相同。
    /// 任一不符立即拒绝，且**不产生任何 TX**（<see cref="SessionStatistics.TxCount"/> 不增加）。
    /// 通过校验后，后续写门禁（<see cref="GuardReject"/>）依据的声明字段与实际字节已必然一致。
    /// </para>
    /// </summary>
    private bool TryValidateFinalAdu(ModbusRequest request, out ErrorInfo error)
    {
        error = null!;

        var raw = request.RawAdu;
        if (raw is null || raw.Length == 0) return true;   // 未提供原始帧：由声明字段构建，天然一致

        var validation = _codec.Validate(raw);
        if (!validation.IsValid && !request.AllowInvalidFrame)
        {
            error = new ErrorInfo(ErrorCodes.ValidationRawAduMismatch, ErrorCategory.Validation, Severity.Error,
                $"拒绝发送：原始帧与当前协议（{_codec.Name}）不匹配——{validation.Message}",
                Hint: "原始帧必须能被声明字段完整解释；请重新生成报文，或改用字段构建方式提交");
            return false;
        }

        var unitId = _codec.TryGetUnitId(raw);
        var function = _codec.TryGetFunctionCode(raw);
        if (unitId is null || function is null)
        {
            error = new ErrorInfo(ErrorCodes.ValidationRawAduMismatch, ErrorCategory.Validation, Severity.Error,
                "拒绝发送：无法从原始帧解析从站地址或功能码",
                Hint: "请检查报文长度与协议格式；无法可靠识别的原始帧不得发送");
            return false;
        }

        if (unitId.Value != request.UnitId)
        {
            error = new ErrorInfo(ErrorCodes.ValidationRawAduMismatch, ErrorCategory.Validation, Severity.Error,
                $"拒绝发送：原始帧的从站地址 0x{unitId.Value:X2} 与声明的 0x{request.UnitId:X2} 不一致",
                Hint: "广播确认与写确认按声明字段判定，声明与字节不一致会绕过安全门禁；请让二者保持一致");
            return false;
        }

        if (function.Value != request.Pdu.Function)
        {
            error = new ErrorInfo(ErrorCodes.ValidationRawAduMismatch, ErrorCategory.Validation, Severity.Error,
                $"拒绝发送：原始帧的功能码 0x{function.Value:X2} 与声明的 0x{request.Pdu.Function:X2} 不一致",
                Hint: "写操作确认按声明功能码判定，声明与字节不一致会绕过写门禁；请让二者保持一致");
            return false;
        }

        ReadOnlySpan<byte> pduBytes;
        if (_codec.ChecksumLength == 0)
        {
            // Modbus TCP：MBAP 7 字节（事务 2 + 协议 2 + 长度 2 + 单元 1），其后是 PDU。
            if (raw.Length < 8)
            {
                error = new ErrorInfo(ErrorCodes.ValidationRawAduMismatch, ErrorCategory.Validation, Severity.Error,
                    "拒绝发送：Modbus TCP 帧不足 8 字节（MBAP + 功能码）");
                return false;
            }

            pduBytes = raw.AsSpan(7);
        }
        else
        {
            // Modbus RTU：从站地址 1 + PDU + 校验 2。
            if (raw.Length < 4)
            {
                error = new ErrorInfo(ErrorCodes.ValidationRawAduMismatch, ErrorCategory.Validation, Severity.Error,
                    "拒绝发送：Modbus RTU 帧不足 4 字节（地址 + 功能码 + 校验）");
                return false;
            }

            pduBytes = raw.AsSpan(1, raw.Length - 3);
        }

        var declaredPdu = request.Pdu.ToBytes();
        if (!pduBytes.SequenceEqual(declaredPdu))
        {
            error = new ErrorInfo(ErrorCodes.ValidationRawAduMismatch, ErrorCategory.Validation, Severity.Error,
                "拒绝发送：原始帧的 PDU 与声明的 PDU 不一致",
                Detail: $"声明 {Convert.ToHexString(declaredPdu)}，帧内 {Convert.ToHexString(pduBytes)}",
                Hint: "同一条请求的声明字段与实际字节必须完全一致，否则写门禁判断的不是真正要发出去的报文");
            return false;
        }

        return true;
    }

    /// <summary>写操作与广播的强制守卫（见规划 15.7）：位于事务层入口，脚本入口同样受约束。</summary>
    private static bool GuardReject(ModbusRequest request, out ErrorInfo error)
    {
        error = null!;

        if (!request.IsWrite) return false;

        if (request.UnitId == 0 && !request.BroadcastConfirmed)
        {
            error = new ErrorInfo(ErrorCodes.ValidationBroadcastWrite, ErrorCategory.Validation, Severity.Error,
                "拒绝向广播地址 0 发送写请求：需要明确的广播专项确认",
                Hint: "广播写会影响总线上所有设备，请确认后重新提交");
            return true;
        }

        if (!request.Confirmed)
        {
            error = new ErrorInfo(ErrorCodes.ValidationWriteNotConfirmed, ErrorCategory.Validation, Severity.Error,
                $"拒绝未确认的写请求（功能码 0x{request.Pdu.Function:X2}）",
                Hint: "写操作必须由用户确认后提交");
            return true;
        }

        // 规划 13.3 / CC-6：写类请求不得出现在 P1（周期）与 P2（扫描）优先级队列，
        // 否则监视表刷新或扫描任务可能在无人确认的情况下改动现场设备（M4 安全回归 SR-2 发现并补上）。
        if (request.Priority != RequestPriority.Interactive)
        {
            var priorityText = request.Priority == RequestPriority.Periodic ? "周期刷新（P1）" : "扫描（P2）";
            error = new ErrorInfo(ErrorCodes.ValidationScanWriteBlocked, ErrorCategory.Validation, Severity.Error,
                $"拒绝在{priorityText}优先级队列中发送写请求（功能码 0x{request.Pdu.Function:X2}）",
                Hint: "写操作只允许交互式（P0）请求，且必须由用户确认；扫描与周期任务不得产生写操作");
            return true;
        }

        return false;
    }

    private static ExchangeResult CancelledResult(ModbusRequest request) =>
        new(ExchangeStatus.Cancelled, request.UnitId, request.Pdu.Function, Array.Empty<byte>(), null, null, null, 0,
            TimeSpan.Zero, new ErrorInfo(ErrorCodes.TransportTimeout, ErrorCategory.Transport, Severity.Info, "请求已取消"));

    private async Task DispatchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!TryDequeue(out var pending))
            {
                // 等“有新请求”的信号；100 ms 兜底超时只为防御信号丢失，正常路径不会被触发。
                await _queueSignal.WaitAsync(100, cancellationToken).ConfigureAwait(false);
                if (!TryDequeue(out pending))
                {
                    continue;
                }
            }

            ExchangeResult result;
            try
            {
                result = await ExecuteOneAsync(pending.Request, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                result = CancelledResult(pending.Request);
            }
            catch (Exception ex)
            {
                result = new ExchangeResult(ExchangeStatus.TransportError, pending.Request.UnitId,
                    pending.Request.Pdu.Function, Array.Empty<byte>(), null, null, null, 0, TimeSpan.Zero,
                    new ErrorInfo(ErrorCodes.InternalError, ErrorCategory.Internal, Severity.Error,
                        $"执行请求时发生内部错误：{ex.Message}", ex.ToString()));
            }

            pending.Completion.TrySetResult(result);

            try
            {
                await Task.Delay(Math.Max(0, _options.TurnaroundDelayMs), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private bool TryDequeue(out PendingRequest pending)
    {
        foreach (var queue in _queues)
        {
            if (queue.Reader.TryRead(out pending!)) return true;
        }
        pending = null!;
        return false;
    }

    private async Task<ExchangeResult> ExecuteOneAsync(ModbusRequest request, CancellationToken cancellationToken)
    {
        int attempts = request.IsWrite && !_options.RetryWrites
            ? 1
            : 1 + Math.Max(0, request.RetryCount ?? _options.RetryCount);
        int timeout = request.TimeoutMs ?? _options.ResponseTimeoutMs;
        var stopwatch = Stopwatch.StartNew();
        var pduBytes = request.Pdu.ToBytes();
        byte[]? adu = null;
        long? requestSeq = null;
        FrameArrival? arrival = null;
        bool sawChecksumError = false;
        bool isBroadcast = request.UnitId == 0 || request.NoResponseExpected;

        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 1) Statistics.IncRetry();

            ushort transaction = _codec.ChecksumLength == 0
                ? unchecked(++_transactionCounter)
                : (ushort)0;

            adu = request.RawAdu ?? _codec.BuildAdu(pduBytes, request.UnitId, transaction);

            // 关键顺序：先登记待配对响应，再发送。
            // 否则设备响应很快（或回环/模拟器立即响应）时，响应会在登记之前到达并被丢弃，导致假超时。
            var pending = isBroadcast ? null : PrepareResponseWait(request, transaction);

            requestSeq = await WriteFrameAsync(adu, request, cancellationToken).ConfigureAwait(false);

            if (isBroadcast)
            {
                // 广播无响应：等待固定时间后返回成功（见规划 14.5 第 5 条）。
                await Task.Delay(Math.Max(20, _options.BroadcastWaitMs), cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                return new ExchangeResult(ExchangeStatus.Ok, request.UnitId, request.Pdu.Function, adu, null, null,
                    null, attempt, stopwatch.Elapsed, null, requestSeq, null);
            }

            arrival = await WaitForResponseAsync(pending!, timeout, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                if (_pendingChecksumError) sawChecksumError = true;
            }

            if (arrival is not null)
            {
                if (arrival.Candidate.Status == FrameStatus.ChecksumError)
                {
                    sawChecksumError = true;
                    Statistics.IncChecksum();
                    continue; // 按规划 15.3：校验错误可重试
                }

                if (arrival.Candidate.Status != FrameStatus.Valid)
                {
                    continue;
                }

                stopwatch.Stop();
                return BuildResultFromResponse(request, adu, arrival, attempt, stopwatch.Elapsed, requestSeq);
            }

            lock (_sync)
            {
                _lateUnitId = request.UnitId;
                _lateFunction = request.Pdu.Function;
                _lateSinceTicks = Clock.MonotonicTicks;
                _lateWindowMs = timeout;
            }

            if (cancellationToken.IsCancellationRequested) break;
        }

        stopwatch.Stop();
        if (cancellationToken.IsCancellationRequested)
            return CancelledResult(request) with { Attempts = attempts, Elapsed = stopwatch.Elapsed };

        Statistics.IncTimeout();
        var status = sawChecksumError ? ExchangeStatus.ChecksumError : ExchangeStatus.Timeout;
        var error = sawChecksumError
            ? new ErrorInfo(ErrorCodes.FramingChecksum, ErrorCategory.Framing, Severity.Warning,
                $"未收到有效响应：{attempts} 次尝试均校验失败", Hint: "检查波特率、校验位、线路质量与终端电阻")
            : new ErrorInfo(ErrorCodes.TransportTimeout, ErrorCategory.Transport, Severity.Warning,
                $"响应超时（{timeout} ms × {attempts} 次）",
                Hint: "确认从站地址、串口参数、超时时间；设备可能未上线或地址未实现");

        return new ExchangeResult(status, request.UnitId, request.Pdu.Function, adu ?? Array.Empty<byte>(), null, null,
            null, attempts, stopwatch.Elapsed, error, requestSeq, null);
    }

    private ExchangeResult BuildResultFromResponse(ModbusRequest request, byte[] adu, FrameArrival arrival,
        int attempt, TimeSpan elapsed, long? requestSeq)
    {
        var (_, parsed) = TryParseResponse(arrival.Bytes);
        if (parsed is { Kind: PduKind.Exception })
        {
            Statistics.IncException();
            var code = parsed.ExceptionCode ?? 0;
            return new ExchangeResult(ExchangeStatus.ModbusException, request.UnitId, request.Pdu.Function, adu,
                arrival.Bytes, parsed, code, attempt, elapsed,
                new ErrorInfo(ErrorCodes.ProtocolException, ErrorCategory.Protocol, Severity.Warning,
                    $"设备返回异常响应：{ModbusExceptions.Describe(code)}",
                    Hint: ModbusExceptions.Hint(code)), requestSeq, arrival.Seq);
        }

        return new ExchangeResult(ExchangeStatus.Ok, request.UnitId, request.Pdu.Function, adu, arrival.Bytes,
            parsed, null, attempt, elapsed, null, requestSeq, arrival.Seq);
    }

    private (ModbusPdu? Pdu, ParsedPdu? Parsed) TryParseResponse(byte[] adu)
    {
        try
        {
            var pduBytes = _codec.ChecksumLength == 0 ? adu[7..] : adu[1..^2];
            var pdu = ModbusPdu.Parse(pduBytes);
            return (pdu, PduParser.Parse(pdu, isRequest: false));
        }
        catch (Exception)
        {
            return (null, null);
        }
    }

    private async Task<long> WriteFrameAsync(byte[] adu, ModbusRequest request, CancellationToken cancellationToken)
    {
        SetFramerExpectation(request);

        var stamp = await _channel.WriteAsync(adu, cancellationToken).ConfigureAwait(false);
        Statistics.IncTx();

        lock (_sync)
        {
            _lastTxBytes = adu;
            _lastTxTicks = stamp.MonotonicTicks;
            // 回显抑制只对“响应必然与请求不同”的功能码启用：
            // 写单个线圈/寄存器、写多个线圈/寄存器、诊断等功能的响应与请求可能逐字节相同，
            // 此时无法用字节比较区分回显与真实响应，误判会丢掉真正的响应（见规划 14.2.2）。
            if (_options.SuppressEcho && _options.IsSerial && Fn.IsRead(request.Pdu.Function))
            {
                _echoExpected = adu;
                _echoMatched = 0;
            }
            else
            {
                _echoExpected = null;
                _echoMatched = 0;
            }
            _lateUnitId = null;
            _lateFunction = null;
        }

        var seq = _sequence.Next();
        Emit(FrameDirection.Tx, adu, FrameStatus.Valid, FrameMarker.None, request.UnitId, request.Pdu.Function,
            FrameSummaryBuilder.Build(_codec, adu, isRequest: true), stamp.MonotonicTicks, seq, null, null, null);
        return seq;
    }

    private void SetFramerExpectation(ModbusRequest request)
    {
        lock (_sync)
        {
            if (_framer is IExpectationAwareFramer aware) aware.SetExpectation(request.UnitId, request.Pdu.Function);
        }
    }

    /// <summary>登记本次请求的待配对响应（必须在写入之前调用）。</summary>
    private TaskCompletionSource<FrameArrival> PrepareResponseWait(ModbusRequest request, ushort transaction)
    {
        var tcs = new TaskCompletionSource<FrameArrival>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            _pendingResponse = tcs;
            _pendingUnitId = request.UnitId;
            _pendingFunction = request.Pdu.Function;
            _pendingTransaction = transaction;
            _pendingChecksumError = false;
        }
        return tcs;
    }

    private async Task<FrameArrival?> WaitForResponseAsync(TaskCompletionSource<FrameArrival> pending, int timeoutMs,
        CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);
            try
            {
                return await pending.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pendingResponse, pending)) _pendingResponse = null;
            }
        }
    }

    /// <summary>
    /// 接收循环：把字节块喂给成帧器，并在空闲时驱动静默超时收尾。
    /// 关键点：同一时刻只允许存在一个未完成的 MoveNextAsync（异步迭代器不支持并发推进）。
    /// </summary>
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var framer = _codec.CreateFramer(FramerRole.Master);
        lock (_sync) _framer = framer;

        var enumerator = _channel.ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            Task<bool>? moveNext = enumerator.MoveNextAsync().AsTask();

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!moveNext.IsCompleted)
                {
                    var delay = Task.Delay(5, CancellationToken.None);
                    var finished = await Task.WhenAny(moveNext, delay).ConfigureAwait(false);
                    if (finished != moveNext)
                    {
                        // 空闲：驱动静默判定，然后继续等待数据。
                        HandleFrames(framer.OnTimer(Clock.MonotonicTicks));
                        continue;
                    }
                }

                bool hasData;
                try
                {
                    hasData = await moveNext.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (CommException ex)
                {
                    lock (_sync) _pendingResponse?.TrySetException(ex);
                    break;
                }
                catch (Exception ex)
                {
                    var error = new ErrorInfo(ErrorCodes.TransportLinkLost, ErrorCategory.Transport, Severity.Error,
                        $"接收循环异常终止：{ex.Message}", ex.ToString());
                    lock (_sync) _pendingResponse?.TrySetException(new CommException(error, ex));
                    break;
                }

                if (!hasData)
                {
                    // 通道结束（连接断开或关闭）：用静默收尾仍留在缓冲中的半帧。
                    HandleFrames(framer.OnTimer(Clock.MonotonicTicks));
                    break;
                }

                var chunk = enumerator.Current;
                int offset = SuppressEcho(chunk);
                if (offset < chunk.Data.Length)
                {
                    HandleFrames(framer.Feed(chunk.Data.AsSpan(offset), chunk.MonotonicTicks), chunk.Timestamp,
                        chunk.MonotonicTicks);
                }

                moveNext = enumerator.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            try
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 关闭时的异常不影响退出流程
            }
        }
    }

    /// <summary>
    /// RS-485 半双工回显抑制：把刚发出的帧对应的回显字节从接收流中剔除，
    /// 返回需要交给成帧器的起始偏移。仅对串口且开启抑制时生效。
    /// </summary>
    private int SuppressEcho(RxChunk chunk)
    {
        if (!_options.SuppressEcho || !_options.IsSerial) return 0;

        byte[]? expected;
        int matched;
        lock (_sync)
        {
            expected = _echoExpected;
            matched = _echoMatched;
        }

        if (expected is null) return 0;

        int offset = 0;
        while (offset < chunk.Data.Length && matched < expected.Length && chunk.Data[offset] == expected[matched])
        {
            offset++;
            matched++;
        }

        if (matched == expected.Length)
        {
            // 完整回显：把这段字节记下来并从数据流中剔除。
            Emit(FrameDirection.Rx, expected, FrameStatus.Valid, FrameMarker.Echo, _codec.TryGetUnitId(expected),
                _codec.TryGetFunctionCode(expected), "回显（本机发出的帧）", chunk.MonotonicTicks, _sequence.Next(), 0.0,
                chunk.MonotonicTicks, null);
            Statistics.IncRx();
            Statistics.IncEcho();
            lock (_sync)
            {
                _echoExpected = null;
                _echoMatched = 0;
            }
            return offset;
        }

        if (offset < chunk.Data.Length)
        {
            // 中途不匹配：本机没有回显现象，取消抑制，全部字节按真实数据交给成帧器。
            lock (_sync)
            {
                _echoExpected = null;
                _echoMatched = 0;
            }
            return 0;
        }

        // 块内字节恰好用尽但尚未匹配完：回显可能被拆分成多块，保留进度，本块不投递。
        lock (_sync) _echoMatched = matched;
        return chunk.Data.Length;
    }

    private void HandleFrames(IReadOnlyList<FrameCandidate> frames, DateTimeOffset? timestamp = null,
        long monotonicTicks = 0)
    {
        foreach (var frame in frames)
        {
            var ts = timestamp ?? Clock.Now;
            var ticks = monotonicTicks != 0 ? monotonicTicks : Clock.MonotonicTicks;
            var completed = Clock.MonotonicTicks;
            var seq = _sequence.Next();

            // 回显已在字节层面被剔除（见 SuppressEcho），此处不再按内容比较，
            // 否则写功能码的响应与请求逐字节相同时会被误判为回显而丢弃真正的响应。
            var markers = FrameMarker.None;

            Statistics.IncRx();

            byte? unitId = _codec.TryGetUnitId(frame.Bytes);
            byte? function = _codec.TryGetFunctionCode(frame.Bytes);
            if (markers == FrameMarker.None && unitId == 0) markers |= FrameMarker.Broadcast;
            if (function.HasValue && Fn.IsWrite(function.Value)) markers |= FrameMarker.Write;

            string? summary = frame.Status == FrameStatus.Valid
                ? FrameSummaryBuilder.Build(_codec, frame.Bytes, isRequest: false)
                : null;

            double? elapsed = null;
            TaskCompletionSource<FrameArrival>? pending;
            bool matched = false;

            lock (_sync)
            {
                pending = _pendingResponse;
                if (pending is not null && frame.Status == FrameStatus.Valid && Matches(frame.Bytes, unitId, function))
                {
                    matched = true;
                    elapsed = Clock.MillisecondsBetween(_lastTxTicks, completed);
                }
            }

            var arrival = new FrameArrival(frame.Bytes, frame, ts, ticks, completed, seq);

            if (matched && pending is not null)
            {
                Emit(FrameDirection.Rx, frame.Bytes, frame.Status, markers, unitId, function, summary, ticks, seq,
                    elapsed, completed, null);
                pending.TrySetResult(arrival);
                continue;
            }

            if (pending is not null && frame.Status == FrameStatus.ChecksumError)
            {
                // 期望响应但校验失败：记录下来但继续等待有效响应，避免因线上噪声或回显而误判（见规划 15.3）。
                lock (_sync) _pendingChecksumError = true;
                Emit(FrameDirection.Rx, frame.Bytes, frame.Status, markers, unitId, function,
                    $"CRC 校验失败：{frame.Detail}", ticks, seq, null, completed, ErrorCodes.FramingChecksum);
                continue;
            }

            if (pending is not null)
            {
                markers |= FrameMarker.Unmatched;
                Statistics.IncUnmatched();
            }
            else
            {
                bool isLate;
                lock (_sync)
                {
                    isLate = _lateUnitId.HasValue
                             && _lateUnitId == unitId
                             && _lateFunction.HasValue
                             && (function == _lateFunction || function == (byte)(_lateFunction.Value | 0x80))
                             && Clock.MillisecondsBetween(_lateSinceTicks, ticks) <= Math.Max(1000, _lateWindowMs * 2);
                }

                if (isLate) markers |= FrameMarker.Late;
                else if (frame.Status == FrameStatus.ChecksumError) Statistics.IncChecksum();
            }

            Emit(FrameDirection.Rx, frame.Bytes, frame.Status, markers, unitId, function, summary, ticks, seq,
                null, completed, frame.Status == FrameStatus.ChecksumError ? ErrorCodes.FramingChecksum : null);
        }
    }

    private bool Matches(byte[] bytes, byte? unitId, byte? function)
    {
        if (unitId != _pendingUnitId) return false;

        if (_codec.ChecksumLength == 0)
        {
            // TCP：优先按事务标识匹配（见规划 14.5）。
            if (bytes.Length < 8) return false;
            ushort transaction = (ushort)((bytes[0] << 8) | bytes[1]);
            if (transaction != _pendingTransaction) return false;
        }

        if (!function.HasValue) return false;
        return function.Value == _pendingFunction || function.Value == (byte)(_pendingFunction | 0x80);
    }

    private void Emit(FrameDirection direction, byte[] bytes, FrameStatus status, FrameMarker markers, byte? unitId,
        byte? function, string? summary, long monotonicTicks, long seq, double? elapsed, long? completedTicks,
        string? errorCode)
    {
        if (_observer is null) return;

        var record = new FrameRecord
        {
            Seq = seq,
            SessionId = _options.SessionId,
            Direction = direction,
            Timestamp = Clock.Now,
            MonotonicTicks = monotonicTicks,
            CompletedMonotonicTicks = completedTicks ?? monotonicTicks,
            RawBytes = bytes,
            Status = status,
            Markers = markers,
            UnitId = unitId,
            FunctionCode = function,
            Summary = summary,
            ElapsedMs = elapsed,
            ErrorCode = errorCode
        };

        try
        {
            _observer.OnFrame(record);
        }
        catch (Exception)
        {
            // 观察者异常不得影响通信。
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
