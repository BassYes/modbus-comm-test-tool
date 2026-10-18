using Comm.Core.Codecs;

namespace Comm.Protocols.Modbus;

/// <summary>Modbus RTU 编解码器：帧结构 [地址][功能码][数据][CRC 低][CRC 高]。</summary>
public sealed class ModbusRtuCodec : IFrameCodec
{
    private readonly ModbusCrc16 _crc = new();
    private readonly int _silenceThresholdMs;

    public ModbusRtuCodec(int silenceThresholdMs = 5)
    {
        _silenceThresholdMs = silenceThresholdMs;
    }

    public string Name => "modbus-rtu";
    public int ChecksumLength => 2;

    public IFramer CreateFramer(FramerRole role) => new RtuFramer(role, _silenceThresholdMs);

    public byte[] BuildAdu(ReadOnlySpan<byte> pdu, byte unitId, ushort transactionId = 0)
    {
        var body = new byte[1 + pdu.Length];
        body[0] = unitId;
        pdu.CopyTo(body.AsSpan(1));
        return AppendChecksum(body);
    }

    public byte[] AppendChecksum(ReadOnlySpan<byte> body)
    {
        var crc = _crc.Compute(body);
        var frame = new byte[body.Length + 2];
        body.CopyTo(frame);
        frame[^2] = crc[0];
        frame[^1] = crc[1];
        return frame;
    }

    public ValidationResult Validate(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 4)
            return new ValidationResult(false, "RTU 帧过短：至少需要地址、功能码与 CRC 共 4 字节");

        int offset = frame.Length - 2;
        var expected = _crc.Compute(frame[..offset]);
        var actual = frame[offset..].ToArray();

        if (_crc.Verify(frame))
            return new ValidationResult(true, $"CRC 正确（0x{expected[1]:X2}{expected[0]:X2}）", offset, expected, actual);

        return new ValidationResult(false,
            $"CRC 不正确：期望 0x{expected[1]:X2}{expected[0]:X2}，实际 0x{actual[1]:X2}{actual[0]:X2}",
            offset, expected, actual);
    }

    public byte? TryGetUnitId(ReadOnlySpan<byte> frame) => frame.Length >= 1 ? frame[0] : null;

    public byte? TryGetFunctionCode(ReadOnlySpan<byte> frame) => frame.Length >= 2 ? frame[1] : null;
}

/// <summary>Modbus TCP 编解码器：MBAP(7) + PDU，无 CRC。</summary>
public sealed class ModbusTcpCodec : IFrameCodec
{
    public string Name => "modbus-tcp";
    public int ChecksumLength => 0;

    public IFramer CreateFramer(FramerRole role) => new TcpFramer();

    public byte[] BuildAdu(ReadOnlySpan<byte> pdu, byte unitId, ushort transactionId = 0)
    {
        int length = 1 + pdu.Length;
        var frame = new byte[7 + pdu.Length];
        frame[0] = (byte)(transactionId >> 8);
        frame[1] = (byte)(transactionId & 0xFF);
        frame[2] = 0;
        frame[3] = 0;
        frame[4] = (byte)(length >> 8);
        frame[5] = (byte)(length & 0xFF);
        frame[6] = unitId;
        pdu.CopyTo(frame.AsSpan(7));
        return frame;
    }

    /// <summary>Modbus TCP 不追加 RTU CRC，本方法原样返回（界面会给出提示）。</summary>
    public byte[] AppendChecksum(ReadOnlySpan<byte> body) => body.ToArray();

    public ValidationResult Validate(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 8)
            return new ValidationResult(false, "Modbus TCP 帧过短：至少需要 7 字节 MBAP 头与 1 字节功能码");

        ushort protocolId = (ushort)((frame[2] << 8) | frame[3]);
        if (protocolId != 0)
            return new ValidationResult(false, $"协议标识应为 0，实际为 {protocolId}");

        int length = (frame[4] << 8) | frame[5];
        if (length != frame.Length - 6)
            return new ValidationResult(false, $"MBAP 长度字段为 {length}，与实际帧长不符（应为 {frame.Length - 6}）");

        return new ValidationResult(true, $"MBAP 正确（长度 {length}），Modbus TCP 不含 CRC");
    }

    public byte? TryGetUnitId(ReadOnlySpan<byte> frame) => frame.Length >= 7 ? frame[6] : null;

    public byte? TryGetFunctionCode(ReadOnlySpan<byte> frame) => frame.Length >= 8 ? frame[7] : null;
}
