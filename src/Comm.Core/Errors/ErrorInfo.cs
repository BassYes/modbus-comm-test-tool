namespace Comm.Core.Errors;

/// <summary>错误分类（见规划 15.2）。</summary>
public enum ErrorCategory
{
    Transport,
    Framing,
    Protocol,
    Validation,
    Platform,
    Storage,
    Plugin,
    Internal
}

public enum Severity
{
    Info,
    Warning,
    Error,
    Fatal
}

/// <summary>统一错误描述。错误码一旦发布不得更改含义。</summary>
public sealed record ErrorInfo(
    string Code,
    ErrorCategory Category,
    Severity Severity,
    string Message,
    string? Detail = null,
    bool Retriable = false,
    string? Hint = null)
{
    public override string ToString() => $"[{Code}] {Message}";

    /// <summary>面向用户的多行说明：发生了什么 / 可能原因 / 建议操作。</summary>
    public string ToUserText()
    {
        var text = Message;
        if (!string.IsNullOrWhiteSpace(Hint)) text += Environment.NewLine + "建议：" + Hint;
        return text;
    }
}

/// <summary>错误码常量。</summary>
public static class ErrorCodes
{
    // 传输
    public const string TransportOpenFailed = "TRN-1001";
    public const string TransportNotOpen = "TRN-1002";
    public const string TransportLinkLost = "TRN-1003";
    public const string TransportTimeout = "TRN-1004";
    public const string TransportWriteFailed = "TRN-1005";
    public const string TransportPortBusy = "TRN-1006";
    public const string TransportAccessDenied = "TRN-1007";
    /// <summary>串口参数重配（R-11 未知波特率/校验位扫描）失败。</summary>
    public const string TransportSerialReconfigureFailed = "TRN-1008";

    // 成帧
    public const string FramingChecksum = "FRM-2001";
    public const string FramingLength = "FRM-2002";
    public const string FramingGarbage = "FRM-2003";
    public const string FramingAmbiguous = "FRM-2004";

    // 协议
    public const string ProtocolException = "PRO-3001";
    public const string ProtocolUnmatched = "PRO-3002";
    public const string ProtocolMalformed = "PRO-3003";

    // 校验 / 输入
    public const string ValidationBadInput = "VAL-4001";
    public const string ValidationAddressRange = "VAL-4002";
    public const string ValidationQuantity = "VAL-4003";
    public const string ValidationWriteNotConfirmed = "VAL-4004";
    public const string ValidationBroadcastWrite = "VAL-4005";
    public const string ValidationScanWriteBlocked = "VAL-4006";
    public const string ValidationNetworkRange = "VAL-4007";
    /// <summary>参数扫描需要可重配的串口会话（R-11）。</summary>
    public const string ValidationParameterScanNotSerial = "VAL-4008";

    // 平台
    public const string PlatformPermission = "PLT-5001";
    public const string PlatformNotSupported = "PLT-5002";

    // 存储
    public const string StorageWriteFailed = "STO-6001";
    public const string StorageReadFailed = "STO-6002";
    public const string StorageLowSpace = "STO-6003";

    // 插件
    public const string PluginLoadFailed = "PLG-7001";
    public const string PluginCallFailed = "PLG-7002";

    // 内部
    public const string InternalError = "INT-9001";
}

/// <summary>携带 <see cref="ErrorInfo"/> 的异常。仅用于基础设施故障与程序错误。</summary>
public sealed class CommException : Exception
{
    public CommException(ErrorInfo error, Exception? inner = null)
        : base(error.Message, inner)
    {
        Error = error;
    }

    public ErrorInfo Error { get; }
}
