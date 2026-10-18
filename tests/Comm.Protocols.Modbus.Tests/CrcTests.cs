using Comm.Core;
using Comm.Core.Codecs;
using Comm.Protocols.Modbus;
using Xunit;

namespace Comm.Protocols.Modbus.Tests;

/// <summary>CRC-16/Modbus 标准向量（见规划 16.3.1）。</summary>
public class CrcTests
{
    private readonly ModbusCrc16 _crc = new();

    [Theory]
    [InlineData("01 03 00 00 00 0A", "C5 CD")]
    [InlineData("11 03 00 6B 00 03", "76 87")]
    [InlineData("01 04 02 FF FF", "B8 80")]
    [InlineData("01 06 00 01 00 03", "98 0B")]
    public void Computes_known_vectors_in_low_byte_first_order(string body, string expectedCrc)
    {
        var bytes = HexUtil.Parse(body);
        var crc = _crc.Compute(bytes);
        Assert.Equal(expectedCrc, $"{crc[0]:X2} {crc[1]:X2}");
    }

    [Fact]
    public void Verify_accepts_valid_frame_and_rejects_corrupted_frame()
    {
        var frame = HexUtil.Parse("01 03 00 00 00 0A C5 CD");
        Assert.True(_crc.Verify(frame));

        var corrupted = frame.ToArray();
        corrupted[3] ^= 0x01;
        Assert.False(_crc.Verify(corrupted));
    }

    [Fact]
    public void Handles_empty_and_single_byte_input()
    {
        Assert.Equal(new byte[] { 0xFF, 0xFF }, _crc.Compute(ReadOnlySpan<byte>.Empty));
        Assert.Equal(2, _crc.Compute(new byte[] { 0x00 }).Length);
    }

    [Fact]
    public void Accepts_maximum_rtu_payload_length()
    {
        var body = new byte[252];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)i;
        var frame = _crc.Compute(body);
        var full = body.Concat(frame).ToArray();
        Assert.True(_crc.Verify(full));
    }
}
