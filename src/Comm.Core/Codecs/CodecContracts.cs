using Comm.Core.Frames;

namespace Comm.Core.Codecs;

/// <summary>校验算法（见规划 14.3.5）。</summary>
public interface IChecksumProvider
{
    string Name { get; }

    /// <summary>校验字段占用的字节数。</summary>
    int Length { get; }

    /// <summary>计算校验值，按线上字节序返回。</summary>
    byte[] Compute(ReadOnlySpan<byte> data);

    /// <summary>校验带校验字段的完整帧。</summary>
    bool Verify(ReadOnlySpan<byte> frameWithChecksum);
}

/// <summary>成帧角色：主站等待响应；监视模式被动监听双向总线。</summary>
public enum FramerRole
{
    Master,
    Monitor
}

/// <summary>单个成帧结果。</summary>
public sealed record FrameCandidate(byte[] Bytes, FrameStatus Status, string? Detail = null);

public interface IFramer
{
    /// <summary>喂入字节块，产出 0..N 个帧。</summary>
    IReadOnlyList<FrameCandidate> Feed(ReadOnlySpan<byte> data, long monotonicTicks);

    /// <summary>静默等时间事件上调用，用于无长度可预测时的收尾。</summary>
    IReadOnlyList<FrameCandidate> OnTimer(long monotonicTicks);

    /// <summary>当前缓冲中待判定的字节数。</summary>
    int PendingBytes { get; }

    void Reset();
}

/// <summary>手工校验结果。</summary>
public sealed record ValidationResult(
    bool IsValid,
    string Message,
    int ChecksumOffset = -1,
    byte[]? Expected = null,
    byte[]? Actual = null);

/// <summary>帧编解码器（协议栈 L1）。构建 ADU、成帧与校验都在此实现，界面不含校验算法。</summary>
public interface IFrameCodec
{
    string Name { get; }

    /// <summary>校验字段长度；Modbus TCP 为 0。</summary>
    int ChecksumLength { get; }

    IFramer CreateFramer(FramerRole role);

    /// <summary>由 PDU 构建完整 ADU（RTU 追加 CRC，TCP 追加 MBAP）。</summary>
    byte[] BuildAdu(ReadOnlySpan<byte> pdu, byte unitId, ushort transactionId = 0);

    /// <summary>为帧主体追加校验字段（手工输入使用）。</summary>
    byte[] AppendChecksum(ReadOnlySpan<byte> body);

    /// <summary>校验完整帧。</summary>
    ValidationResult Validate(ReadOnlySpan<byte> frame);

    byte? TryGetUnitId(ReadOnlySpan<byte> frame);

    byte? TryGetFunctionCode(ReadOnlySpan<byte> frame);
}
