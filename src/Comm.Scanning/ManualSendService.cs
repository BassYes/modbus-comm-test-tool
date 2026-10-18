using Comm.Core;
using Comm.Core.Codecs;
using Comm.Core.Errors;
using Comm.Core.Frames;
using Comm.Protocols.Modbus;
using Comm.Session;

namespace Comm.Scanning;

public sealed record ManualSendOutcome(
    ExchangeStatus Status,
    string Message,
    byte[] Frame,
    ValidationResult? Validation,
    ExchangeResult? Result)
{
    public bool Sent => Result is not null;

    public string StatusText => Result?.StatusText ?? Status.ToString();
}

/// <summary>
/// 手动报文服务（见规划 4.5）：校验自动计算与回填、发送前检查、写操作二次确认。
/// 界面本身不含任何校验算法；构建/校验/解析只依赖编解码器，**因此未连接设备也能生成报文**
/// （R-19：显式选择 RTU 或 TCP，不再要求先建立会话）。只有真正发送时才需要活动会话。
/// </summary>
public sealed class ManualSendService
{
    private readonly ModbusSession? _session;

    /// <summary>使用指定编解码器构建报文；<paramref name="session"/> 仅用于发送。</summary>
    public ManualSendService(IFrameCodec codec, ModbusSession? session = null)
    {
        Codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _session = session;
    }

    /// <summary>沿用会话编解码器（兼容既有调用）。</summary>
    public ManualSendService(ModbusSession session) : this(session.Codec, session)
    {
    }

    public IFrameCodec Codec { get; }

    /// <summary>当前是否有可发送的活动会话（未连接时仍可构建与校验报文）。</summary>
    public bool CanSend => _session is { IsOpen: true };

    /// <summary>待发送报文格式是否与当前会话协议一致（不一致时必须阻止发送，不做静默转换）。</summary>
    public bool MatchesSessionProtocol =>
        _session is null || string.Equals(_session.Codec.Name, Codec.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>由字段构建完整帧（RTU 追加 CRC，TCP 生成 MBAP）。</summary>
    public byte[] BuildFrame(byte unitId, byte function, byte[] data, ushort transactionId = 0)
    {
        var pdu = new ModbusPdu(function, data).ToBytes();
        return Codec.BuildAdu(pdu, unitId, transactionId);
    }

    /// <summary>为「帧主体」追加校验字段（界面“自动填入校验位”使用）。</summary>
    public byte[] AppendChecksum(byte[] body) => Codec.AppendChecksum(body);

    public ValidationResult Validate(byte[] frame) => Codec.Validate(frame);

    /// <summary>解析帧并给出摘要，供界面预览。</summary>
    public string Describe(byte[] frame)
    {
        var unitId = Codec.TryGetUnitId(frame);
        var function = Codec.TryGetFunctionCode(frame);
        if (unitId is null || function is null) return "帧过短，无法识别";

        var summary = FrameSummaryBuilder.Build(Codec, frame, isRequest: true);
        return $"从站/单元 {unitId} | 功能码 0x{function:X2} {Fn.Name(function.Value)} | {summary}";
    }

    /// <summary>
    /// 展示 Modbus TCP 的 MBAP 各字段（事务标识 / 协议标识 / 长度 / 单元标识）。
    /// RTU 帧返回空字符串（规划 4.5.3）。
    /// </summary>
    public string DescribeMbufFields(byte[] frame)
    {
        if (Codec.ChecksumLength != 0 || frame.Length < 8) return string.Empty;

        ushort transactionId = (ushort)((frame[0] << 8) | frame[1]);
        ushort protocolId = (ushort)((frame[2] << 8) | frame[3]);
        ushort length = (ushort)((frame[4] << 8) | frame[5]);
        var pdu = frame[7..];

        return $"事务标识 {transactionId}｜协议标识 {protocolId}｜长度 {length}" +
               $"（单元标识 1 字节 + PDU {pdu.Length} 字节）｜单元标识 {frame[6]}" +
               $"｜Length 校验：{(length == frame.Length - 6 ? "正确" : $"与实际不符（应为 {frame.Length - 6}）")}" +
               Environment.NewLine + $"PDU：{HexUtil.ToHex(pdu)}";
    }

    /// <summary>把用户的十六进制文本解析为完整帧。</summary>
    public Result<byte[]> ParseHex(string text)
    {
        var bytes = HexUtil.TryParse(text, out var error);
        if (bytes is null)
        {
            return Result<byte[]>.Fail(new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation,
                Severity.Warning, $"报文解析失败：{error}"));
        }

        int minimum = Codec.ChecksumLength == 0 ? 8 : 4;
        if (bytes.Length < minimum)
        {
            return Result<byte[]>.Fail(new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation,
                Severity.Warning, $"报文过短：{(Codec.ChecksumLength == 0 ? "Modbus TCP 至少需要 8 字节（MBAP + 功能码）" : "至少应包含从站地址、功能码与校验字段")}"));
        }

        return Result<byte[]>.Ok(bytes);
    }

    /// <summary>
    /// 发送一帧完整报文。<paramref name="allowBadChecksum"/> 为 true 时允许发送校验错误的帧（用于故障测试）。
    /// 未连接或无活动会话时返回 <see cref="ExchangeStatus.Rejected"/>，但不抛异常。
    /// </summary>
    public async Task<ManualSendOutcome> SendFrameAsync(byte[] frame, bool confirmedWrite, bool broadcastConfirmed,
        bool allowBadChecksum, bool expectResponse, int? timeoutMs, CancellationToken cancellationToken)
    {
        var validation = Codec.Validate(frame);
        if (!validation.IsValid && !allowBadChecksum)
        {
            return new ManualSendOutcome(ExchangeStatus.Rejected,
                $"发送已阻止：{validation.Message}。如确需发送错误帧，请勾选“允许发送校验错误帧”。",
                frame, validation, null);
        }

        if (_session is null || !_session.IsOpen)
        {
            return new ManualSendOutcome(ExchangeStatus.Rejected,
                "尚未连接设备，只能生成与校验报文；请在“连接中心”建立连接后再发送。", frame, validation, null);
        }

        if (!MatchesSessionProtocol)
        {
            return new ManualSendOutcome(ExchangeStatus.Rejected,
                $"报文格式（{Codec.Name}）与当前连接（{_session.Codec.Name}）不一致，已阻止发送；" +
                "请把格式改回“跟随当前连接”，或连接到对应协议的设备。", frame, validation, null);
        }

        var unitId = Codec.TryGetUnitId(frame) ?? 0;
        var function = Codec.TryGetFunctionCode(frame) ?? 0;

        var pduBytes = Codec.ChecksumLength == 0 ? frame[7..] : frame[1..^2];
        ModbusPdu pdu;
        try
        {
            pdu = ModbusPdu.Parse(pduBytes);
        }
        catch (Exception)
        {
            pdu = new ModbusPdu(function, Array.Empty<byte>());
        }

        var request = new ModbusRequest(unitId, pdu, RequestPriority.Interactive, confirmedWrite, broadcastConfirmed,
            Label: "手动发送", TimeoutMs: timeoutMs, RawAdu: frame, NoResponseExpected: !expectResponse);

        var result = await _session.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);

        var message = result.Status switch
        {
            ExchangeStatus.Ok => "发送成功，收到正常响应",
            ExchangeStatus.ModbusException => $"设备返回异常响应：{ModbusExceptions.Describe(result.ExceptionCode ?? 0)}",
            ExchangeStatus.Timeout => "发送完成，但未收到响应（超时）",
            ExchangeStatus.ChecksumError => "收到字节但校验错误",
            ExchangeStatus.Rejected => result.Error?.Message ?? "发送被拒绝",
            ExchangeStatus.Cancelled => "已取消",
            _ => result.StatusText
        };

        return new ManualSendOutcome(result.Status, message, frame, validation, result);
    }

    /// <summary>由字段构建并发送（需要活动会话）。</summary>
    public async Task<ManualSendOutcome> SendFieldsAsync(byte unitId, byte function, byte[] data, bool confirmedWrite,
        bool broadcastConfirmed, bool expectResponse, int? timeoutMs, CancellationToken cancellationToken)
    {
        var frame = BuildFrame(unitId, function, data);
        return await SendFrameAsync(frame, confirmedWrite, broadcastConfirmed, false, expectResponse, timeoutMs,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>判断是否需要写操作二次确认。</summary>
    public static bool RequiresWriteConfirmation(byte unitId, byte function, out string reason)
    {
        if (Fn.IsWrite(function))
        {
            reason = unitId == 0
                ? "这是写操作且目标为广播地址 0，会影响总线上所有设备，请再次确认"
                : $"这是写操作（{Fn.Name(function)}），将修改从站 {unitId} 的数据，请确认目标、地址与数值";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>从历史记录重发时构造提醒（见规划 6.5）。</summary>
    public static string DescribeRisk(byte function) =>
        Fn.IsWrite(function) ? "该帧为写操作，重发会再次修改设备数据" : "该帧为读操作，无写入风险";
}
