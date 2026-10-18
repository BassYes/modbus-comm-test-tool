using Comm.Core;
using Comm.Core.Codecs;
using Comm.Core.Frames;
using Comm.Protocols.Modbus;
using Xunit;

namespace Comm.Protocols.Modbus.Tests;

public class TcpFramerTests
{
    private static byte[] BuildReadResponse(ushort transactionId = 1)
    {
        var codec = new ModbusTcpCodec();
        var pdu = new ModbusPdu(Fn.ReadHoldingRegisters, new byte[] { 0x04, 0x00, 0x0A, 0x00, 0x14 });
        return codec.BuildAdu(pdu.ToBytes(), 1, transactionId);
    }

    [Fact]
    public void Frames_complete_frame_and_validates_mbap()
    {
        var frame = BuildReadResponse();
        var framer = new TcpFramer();
        var frames = framer.Feed(frame, 1);

        Assert.Single(frames);
        Assert.Equal(FrameStatus.Valid, frames[0].Status);
        Assert.Equal(frame, frames[0].Bytes);
    }

    [Fact]
    public void Reassembles_frame_split_at_every_offset()
    {
        var frame = BuildReadResponse();
        for (int split = 1; split < frame.Length; split++)
        {
            var framer = new TcpFramer();
            var first = framer.Feed(frame.AsSpan(0, split), 1);
            var second = framer.Feed(frame.AsSpan(split), 2);
            var all = first.Concat(second).ToList();

            Assert.True(all.Count == 1 && all[0].Status == FrameStatus.Valid,
                $"split={split} 未正确重组（得到 {all.Count} 帧）");
            Assert.Equal(frame, all[0].Bytes);
        }
    }

    [Fact]
    public void Frames_two_adu_in_one_chunk()
    {
        var first = BuildReadResponse(1);
        var second = BuildReadResponse(2);
        var framer = new TcpFramer();
        var frames = framer.Feed(first.Concat(second).ToArray(), 1);

        Assert.Equal(2, frames.Count);
        Assert.Equal(1, (frames[0].Bytes[0] << 8) | frames[0].Bytes[1]);
        Assert.Equal(2, (frames[1].Bytes[0] << 8) | frames[1].Bytes[1]);
    }

    [Fact]
    public void Invalid_length_field_is_reported_and_buffer_reset()
    {
        var frame = BuildReadResponse();
        frame[4] = 0xFF;
        frame[5] = 0xFF;

        var framer = new TcpFramer();
        var frames = framer.Feed(frame, 1);

        Assert.Single(frames);
        Assert.Equal(FrameStatus.LengthError, frames[0].Status);
        Assert.Equal(0, framer.PendingBytes);
    }

    [Fact]
    public void Non_zero_protocol_id_is_flagged()
    {
        var frame = BuildReadResponse();
        frame[2] = 0x00;
        frame[3] = 0x01;

        var framer = new TcpFramer();
        var frames = framer.Feed(frame, 1);

        Assert.Single(frames);
        Assert.Equal(FrameStatus.ProtocolError, frames[0].Status);
    }
}

public class ModbusCodecTests
{
    [Fact]
    public void Rtu_build_appends_crc_in_low_byte_first_order()
    {
        var codec = new ModbusRtuCodec();
        var pdu = ModbusRequests.Read(DataArea.HoldingRegisters, 0, 10).ToBytes();
        var frame = codec.BuildAdu(pdu, 0x01);

        Assert.Equal("01 03 00 00 00 0A C5 CD", HexUtil.ToHex(frame));
    }

    [Fact]
    public void Rtu_validate_reports_expected_and_actual_crc()
    {
        var codec = new ModbusRtuCodec();
        var frame = HexUtil.Parse("01 03 00 00 00 0A C5 CE");
        var result = codec.Validate(frame);

        Assert.False(result.IsValid);
        Assert.Contains("CRC", result.Message);
        Assert.Equal(new byte[] { 0xC5, 0xCD }, result.Expected);
        Assert.Equal(new byte[] { 0xC5, 0xCE }, result.Actual);
    }

    [Fact]
    public void Rtu_append_checksum_does_not_change_body()
    {
        var codec = new ModbusRtuCodec();
        var body = HexUtil.Parse("01 06 00 01 00 03");
        var frame = codec.AppendChecksum(body);

        Assert.Equal(body, frame[..^2]);
        Assert.True(codec.Validate(frame).IsValid);
    }

    [Fact]
    public void Tcp_build_computes_mbap_length_and_keeps_transaction_id()
    {
        var codec = new ModbusTcpCodec();
        var pdu = ModbusRequests.Read(DataArea.HoldingRegisters, 0x0000, 0x000A).ToBytes();
        var frame = codec.BuildAdu(pdu, 0x11, 0x1234);

        Assert.Equal("12 34 00 00 00 06 11 03 00 00 00 0A", HexUtil.ToHex(frame));
        Assert.True(codec.Validate(frame).IsValid);
        Assert.Equal((byte)0x11, codec.TryGetUnitId(frame));
        Assert.Equal((byte)Fn.ReadHoldingRegisters, codec.TryGetFunctionCode(frame));
    }

    [Fact]
    public void Tcp_validate_detects_length_mismatch()
    {
        var codec = new ModbusTcpCodec();
        var frame = HexUtil.Parse("00 01 00 00 00 09 01 03 00 00");
        Assert.False(codec.Validate(frame).IsValid);
    }
}

public class PduAndAddressTests
{
    [Fact]
    public void Parses_read_registers_response()
    {
        var pdu = ModbusPdu.Parse(HexUtil.Parse("03 04 00 0A 00 14"));
        var parsed = PduParser.Parse(pdu, isRequest: false);

        Assert.Equal(PduKind.ReadRegisters, parsed.Kind);
        Assert.Equal(new ushort[] { 10, 20 }, parsed.Registers);
        Assert.Contains("读保持寄存器", parsed.Describe());
    }

    [Fact]
    public void Parses_exception_response()
    {
        var pdu = ModbusPdu.Parse(HexUtil.Parse("83 02"));
        var parsed = PduParser.Parse(pdu, isRequest: false);

        Assert.Equal(PduKind.Exception, parsed.Kind);
        Assert.Equal((byte)0x02, parsed.ExceptionCode);
        Assert.Contains("非法数据地址", parsed.Describe());
    }

    [Fact]
    public void Parses_bit_response_with_low_bit_first()
    {
        var pdu = ModbusPdu.Parse(HexUtil.Parse("01 03 05 00"));
        var parsed = PduParser.Parse(pdu, isRequest: false);

        Assert.Equal(PduKind.ReadBits, parsed.Kind);
        Assert.True(parsed.Bits![0]);
        Assert.False(parsed.Bits[1]);
        Assert.True(parsed.Bits[2]);
        Assert.Equal(3, PduParser.TrimBits(parsed.Bits!, 3).Length);
    }

    [Theory]
    [InlineData(AddressStyle.ProtocolZeroBased, 0, 0)]
    [InlineData(AddressStyle.DocumentOneBased, 0, 1)]
    [InlineData(AddressStyle.Modicon, 0, 40001)]
    [InlineData(AddressStyle.Modicon, 9, 40010)]
    public void Address_map_round_trips(AddressStyle style, int protocol, int display)
    {
        Assert.Equal(display, AddressMap.ToDisplayAddress(protocol, style, DataArea.HoldingRegisters));
        Assert.Equal(protocol, AddressMap.ToProtocolAddress(display, style, DataArea.HoldingRegisters));
    }

    [Theory]
    [InlineData(Fn.ReadCoils, 2000)]
    [InlineData(Fn.ReadDiscreteInputs, 2000)]
    [InlineData(Fn.ReadHoldingRegisters, 125)]
    [InlineData(Fn.ReadInputRegisters, 125)]
    [InlineData(Fn.WriteMultipleCoils, 1968)]
    [InlineData(Fn.WriteMultipleRegisters, 123)]
    public void Protocol_quantity_limits_match_standard(byte function, int expected)
    {
        Assert.Equal(expected, Fn.MaxQuantity(function));
    }

    [Theory]
    [InlineData(Fn.ReadCoils, false)]
    [InlineData(Fn.WriteSingleCoil, true)]
    [InlineData(Fn.WriteMultipleRegisters, true)]
    [InlineData(Fn.MaskWriteRegister, true)]
    public void Write_detection_is_correct(byte function, bool isWrite)
    {
        Assert.Equal(isWrite, Fn.IsWrite(function));
    }
}
