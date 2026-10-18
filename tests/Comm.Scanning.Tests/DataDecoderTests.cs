using Comm.Scanning;
using Xunit;

namespace Comm.Scanning.Tests;

/// <summary>数据解码矩阵：类型 × 字节序 / 字序（见规划 16.3.1）。</summary>
public class DataDecoderTests
{
    [Fact]
    public void Decodes_unsigned_and_signed_16_bit()
    {
        Assert.Equal("4660", DataDecoder.Decode(new ushort[] { 0x1234 }, DataType.UInt16, ByteOrder.Abcd));
        Assert.Equal("-1", DataDecoder.Decode(new ushort[] { 0xFFFF }, DataType.Int16, ByteOrder.Abcd));
        Assert.Equal("0 (OFF)", DataDecoder.Decode(new ushort[] { 0x0000 }, DataType.Bit, ByteOrder.Abcd));
        Assert.Equal("1 (ON)", DataDecoder.Decode(new ushort[] { 0x0001 }, DataType.Bit, ByteOrder.Abcd));
    }

    [Theory]
    [InlineData(ByteOrder.Abcd, 0x3F80, 0x0000)]
    [InlineData(ByteOrder.Badc, 0x803F, 0x0000)]
    [InlineData(ByteOrder.Cdab, 0x0000, 0x3F80)]
    [InlineData(ByteOrder.Dcba, 0x0000, 0x803F)]
    public void Decodes_float32_in_all_four_byte_orders(ByteOrder order, ushort first, ushort second)
    {
        var text = DataDecoder.Decode(new[] { first, second }, DataType.Float32, order);
        Assert.Equal("1", text);
    }

    [Fact]
    public void Decodes_32_bit_integers()
    {
        // 0x00010000 = 65536
        Assert.Equal("65536", DataDecoder.Decode(new ushort[] { 0x0001, 0x0000 }, DataType.UInt32, ByteOrder.Abcd));
        Assert.Equal("-1", DataDecoder.Decode(new ushort[] { 0xFFFF, 0xFFFF }, DataType.Int32, ByteOrder.Abcd));
        // 字交换后 0x0000_0001 = 1
        Assert.Equal("65536", DataDecoder.Decode(new ushort[] { 0x0000, 0x0001 }, DataType.UInt32, ByteOrder.Cdab));
    }

    [Fact]
    public void Decodes_64_bit_float()
    {
        var registers = DataDecoder.Encode(3.5, DataType.Float64, ByteOrder.Abcd);

        Assert.Equal(4, registers.Length);
        Assert.Equal("3.5", DataDecoder.Decode(registers, DataType.Float64, ByteOrder.Abcd));
        Assert.Equal("3.5", DataDecoder.Decode(DataDecoder.Encode(3.5, DataType.Float64, ByteOrder.Dcba),
            DataType.Float64, ByteOrder.Dcba));
    }

    [Fact]
    public void Decodes_bcd_hex_binary_and_ascii()
    {
        Assert.Equal("1234", DataDecoder.Decode(new ushort[] { 0x1234 }, DataType.Bcd, ByteOrder.Abcd));
        Assert.Equal("0x1234", DataDecoder.Decode(new ushort[] { 0x1234 }, DataType.Hex, ByteOrder.Abcd));
        Assert.Equal("0001001000110100", DataDecoder.Decode(new ushort[] { 0x1234 }, DataType.Binary, ByteOrder.Abcd));
        Assert.Equal("AB", DataDecoder.Decode(new ushort[] { 0x4142 }, DataType.Ascii, ByteOrder.Abcd));
    }

    [Fact]
    public void Flags_invalid_bcd_and_nan_float()
    {
        Assert.Contains("非法 BCD", DataDecoder.Decode(new ushort[] { 0x12AB }, DataType.Bcd, ByteOrder.Abcd));
        Assert.Contains("NaN", DataDecoder.Decode(new ushort[] { 0x7FC0, 0x0000 }, DataType.Float32, ByteOrder.Abcd));
    }

    [Fact]
    public void Applies_scale_and_offset()
    {
        Assert.Equal("25", DataDecoder.Decode(new ushort[] { 250 }, DataType.UInt16, ByteOrder.Abcd, scale: 0.1));
        Assert.Equal("15", DataDecoder.Decode(new ushort[] { 10 }, DataType.UInt16, ByteOrder.Abcd, offset: 5));
    }

    [Fact]
    public void TryDecodeNumber_returns_false_for_nan()
    {
        Assert.True(DataDecoder.TryDecodeNumber(new ushort[] { 0x42C8, 0x0000 }, DataType.Float32, ByteOrder.Abcd, 1, 0, out var value));
        Assert.Equal(100.0, value, 3);

        Assert.False(DataDecoder.TryDecodeNumber(new ushort[] { 0x7FC0, 0x0000 }, DataType.Float32, ByteOrder.Abcd, 1, 0, out _));
    }

    [Theory]
    [InlineData(DataType.UInt16, 1)]
    [InlineData(DataType.Float32, 2)]
    [InlineData(DataType.Float64, 4)]
    [InlineData(DataType.Bit, 1)]
    public void Register_count_matches_type(DataType type, int expected)
    {
        Assert.Equal(expected, type.RegisterCount());
    }

    [Fact]
    public void Encode_is_inverse_of_decode_for_common_types()
    {
        var registers = DataDecoder.Encode(220.5, DataType.Float32, ByteOrder.Cdab);
        var text = DataDecoder.Decode(registers, DataType.Float32, ByteOrder.Cdab);
        Assert.Equal("220.5", text);
    }
}
