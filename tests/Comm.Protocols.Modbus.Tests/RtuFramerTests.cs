using Comm.Core;
using Comm.Core.Codecs;
using Comm.Core.Frames;
using Comm.Protocols.Modbus;
using Xunit;

namespace Comm.Protocols.Modbus.Tests;

/// <summary>RTU 成帧：整帧、逐字节、任意切分、粘包、噪声、校验错误重同步、静默收尾（见规划 16.3.1）。</summary>
public class RtuFramerTests
{
    private static readonly byte[] ReadRegistersResponse = HexUtil.Parse("01 03 04 00 0A 00 14 DA 3E");

    private static RtuFramer MasterFramer() => new(FramerRole.Master);

    [Fact]
    public void Frames_complete_frame_in_one_chunk()
    {
        var framer = MasterFramer();
        var frames = framer.Feed(ReadRegistersResponse, 1);

        Assert.Single(frames);
        Assert.Equal(FrameStatus.Valid, frames[0].Status);
        Assert.Equal(ReadRegistersResponse, frames[0].Bytes);
        Assert.Equal(0, framer.PendingBytes);
    }

    [Fact]
    public void Frames_byte_by_byte_delivery()
    {
        var framer = MasterFramer();
        var collected = new List<FrameCandidate>();

        foreach (var b in ReadRegistersResponse)
        {
            collected.AddRange(framer.Feed(new[] { b }, 1));
        }

        Assert.Single(collected);
        Assert.Equal(FrameStatus.Valid, collected[0].Status);
        Assert.Equal(ReadRegistersResponse, collected[0].Bytes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Frames_any_split_point_produces_same_result(int splitIndex)
    {
        var framer = MasterFramer();
        var first = framer.Feed(ReadRegistersResponse.AsSpan(0, splitIndex), 1);
        var second = framer.Feed(ReadRegistersResponse.AsSpan(splitIndex), 2);

        var all = first.Concat(second).ToList();
        Assert.Single(all);
        Assert.Equal(FrameStatus.Valid, all[0].Status);
        Assert.Equal(ReadRegistersResponse, all[0].Bytes);
    }

    [Fact]
    public void Frames_multiple_frames_in_one_chunk()
    {
        var second = HexUtil.Parse("01 03 02 00 05 78 47");
        var chunk = ReadRegistersResponse.Concat(second).ToArray();

        var framer = MasterFramer();
        var frames = framer.Feed(chunk, 1);

        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(FrameStatus.Valid, f.Status));
        Assert.Equal(second, frames[1].Bytes);
    }

    [Fact]
    public void Sliding_resync_finds_frame_after_corrupted_prefix()
    {
        var corrupted = ReadRegistersResponse.ToArray();
        corrupted[^1] ^= 0xFF;

        var chunk = corrupted.Concat(ReadRegistersResponse).ToArray();
        var framer = MasterFramer();
        var frames = framer.Feed(chunk, 1);

        Assert.Contains(frames, f => f.Status == FrameStatus.ChecksumError);
        Assert.Contains(frames, f => f.Status == FrameStatus.Valid && f.Bytes.SequenceEqual(ReadRegistersResponse));
    }

    [Fact]
    public void Master_with_expectation_skips_foreign_traffic()
    {
        var framer = new RtuFramer(FramerRole.Master);
        framer.SetExpectation(1, Fn.ReadHoldingRegisters);

        // 总线上出现了别的从站的响应（地址 2），随后才是本机请求的响应。
        var other = HexUtil.Parse("02 03 02 00 01 3D 84");
        var chunk = other.Concat(ReadRegistersResponse).ToArray();

        var frames = framer.Feed(chunk, 1);

        Assert.Contains(frames, f => f.Status == FrameStatus.Garbage);
        Assert.Contains(frames, f => f.Status == FrameStatus.Valid && f.Bytes.SequenceEqual(ReadRegistersResponse));
    }

    [Fact]
    public void Exception_response_length_is_five_bytes()
    {
        var exception = HexUtil.Parse("01 83 02 C0 F1");
        var framer = MasterFramer();
        var frames = framer.Feed(exception, 1);

        Assert.Single(frames);
        Assert.Equal(FrameStatus.Valid, frames[0].Status);
        Assert.Equal(5, frames[0].Bytes.Length);
    }

    [Fact]
    public void Unknown_function_code_waits_for_silence()
    {
        var unknown = HexUtil.Parse("01 45 00 01 00 02");
        var unknownWithCrc = new ModbusCrc16().Compute(unknown).Length == 2
            ? unknown.Concat(new ModbusCrc16().Compute(unknown)).ToArray()
            : unknown;

        var framer = MasterFramer();
        var immediate = framer.Feed(unknownWithCrc, 1);
        Assert.Empty(immediate);
        Assert.Equal(unknownWithCrc.Length, framer.PendingBytes);

        var onSilence = framer.OnTimer(2);
        Assert.Single(onSilence);
        Assert.Equal(FrameStatus.Valid, onSilence[0].Status);
        Assert.Equal(unknownWithCrc, onSilence[0].Bytes);
    }

    [Fact]
    public void Silence_reports_garbage_when_nothing_valid_is_found()
    {
        var noise = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
        var framer = MasterFramer();
        framer.Feed(noise, 1);

        var frames = framer.OnTimer(2);

        Assert.Single(frames);
        Assert.NotEqual(FrameStatus.Valid, frames[0].Status);
    }

    [Fact]
    public void Message_boundary_is_respected_when_quantity_is_zero()
    {
        // 响应中字节数字段为 0：长度预测得到 5 字节，CRC 不通过 → 校验错误并按字节重同步。
        var body = HexUtil.Parse("01 03 00 00 00");
        var framer = MasterFramer();
        var frames = framer.Feed(body, 1);

        Assert.All(frames, f => Assert.NotEqual(FrameStatus.Valid, f.Status));
    }
}
