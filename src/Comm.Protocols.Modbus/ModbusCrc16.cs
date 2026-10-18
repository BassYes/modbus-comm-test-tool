using Comm.Core.Codecs;

namespace Comm.Protocols.Modbus;

/// <summary>Modbus CRC-16（多项式 0xA001 反射，初值 0xFFFF，低字节在前）。</summary>
public sealed class ModbusCrc16 : IChecksumProvider
{
    public string Name => "crc16-modbus";
    public int Length => 2;

    public static ushort ComputeCrc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                bool lsb = (crc & 1) != 0;
                crc >>= 1;
                if (lsb) crc ^= 0xA001;
            }
        }
        return crc;
    }

    /// <summary>返回线上字节序（低字节、高字节）。</summary>
    public byte[] Compute(ReadOnlySpan<byte> data)
    {
        var crc = ComputeCrc(data);
        return new[] { (byte)(crc & 0xFF), (byte)(crc >> 8) };
    }

    public bool Verify(ReadOnlySpan<byte> frameWithChecksum)
    {
        if (frameWithChecksum.Length < 3) return false;
        var expected = ComputeCrc(frameWithChecksum[..^2]);
        return frameWithChecksum[^2] == (byte)(expected & 0xFF) && frameWithChecksum[^1] == (byte)(expected >> 8);
    }
}

/// <summary>Modbus LRC（ASCII 传输用，v0.1 未启用成帧，仅提供算法）。</summary>
public sealed class ModbusLrc : IChecksumProvider
{
    public string Name => "lrc-modbus";
    public int Length => 1;

    public byte[] Compute(ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        foreach (var b in data) sum += b;
        return new[] { (byte)((byte)(-sum) & 0xFF) };
    }

    public bool Verify(ReadOnlySpan<byte> frameWithChecksum)
    {
        if (frameWithChecksum.Length < 2) return false;
        return Compute(frameWithChecksum[..^1])[0] == frameWithChecksum[^1];
    }
}
