using System.Text;
using Comm.Platform.Windows;
using Xunit;

namespace Comm.NetConfigHelper.Tests;

/// <summary>
/// 冻结契约（<see cref="HelperProtocol"/> / <see cref="HelperJson"/>）的往返与边界测试（规划 12.5、16.3.7）。
/// 主程序与助手共用这套线格式，因此这里既要测"正常往返"，也要测"畸形输入必须被拒绝而不是崩掉"。
/// </summary>
public sealed class HelperProtocolTests
{
    [Fact]
    public void CreatePipeName_HasPrefixAndIsUnique()
    {
        var first = HelperProtocol.CreatePipeName();
        var second = HelperProtocol.CreatePipeName();

        Assert.StartsWith("comm-netcfg-", first);
        Assert.NotEqual(first, second);           // 管道名带随机后缀（规划 12.5）
        Assert.Equal(first, HelperProtocol.PipeName(first));
        Assert.True(first.Length is > 20 and <= 200);  // 远低于 Windows 管道名长度上限
    }

    [Fact]
    public void SerializeParseRequest_RoundTrips()
    {
        var request = new HelperRequest
        {
            Command = HelperCommands.Apply,
            AdapterId = "adapter-1",
            Payload = "{\"address\":\"192.168.1.10\"}",
            Confirmed = true
        };

        var parsed = HelperProtocol.ParseRequest(HelperProtocol.Serialize(request));

        Assert.NotNull(parsed);
        Assert.Equal(request, parsed);
    }

    [Fact]
    public void SerializeParseResponse_RoundTrips()
    {
        var response = HelperResponse.Ok("payload", "已应用", rollbackArmed: true, remainingSeconds: 42);

        var parsed = HelperProtocol.ParseResponse(HelperProtocol.Serialize(response));

        Assert.NotNull(parsed);
        Assert.True(parsed!.IsSuccess);
        Assert.Equal("payload", parsed.Payload);
        Assert.Equal("已应用", parsed.Message);
        Assert.True(parsed.RollbackArmed);
        Assert.Equal(42, parsed.RollbackSecondsRemaining);
    }

    [Fact]
    public void SerializeParseFailureResponse_RoundTrips()
    {
        var response = HelperResponse.Fail("PLT-5008", "命令不被接受");

        var parsed = HelperProtocol.ParseResponse(HelperProtocol.Serialize(response));

        Assert.NotNull(parsed);
        Assert.False(parsed!.IsSuccess);
        Assert.Equal("PLT-5008", parsed.ErrorCode);
        Assert.Equal("[PLT-5008] 命令不被接受", parsed.ToUserText());
    }

    [Fact]
    public void WriteReadMessage_RoundTripsThroughStream()
    {
        var request = new HelperRequest { Command = HelperCommands.Snapshot, AdapterId = "网卡-一" };
        var json = HelperProtocol.Serialize(request);

        using var stream = new MemoryStream();
        HelperProtocol.WriteMessage(stream, json);
        stream.Position = 0;

        var text = HelperProtocol.ReadMessage(stream);
        Assert.Equal(json, text);
        Assert.Equal(request, HelperProtocol.ParseRequest(text!));

        // 同一条消息的头部必须是 4 字节小端长度（两端必须一致，否则主程序读不到响应）
        var bytes = stream.ToArray();
        Assert.Equal(Encoding.UTF8.GetByteCount(json) + 4, bytes.Length);
        Assert.Equal(Encoding.UTF8.GetByteCount(json), BitConverter.ToInt32(bytes, 0));
    }

    [Fact]
    public void WriteMessage_RejectsOversizedPayload()
    {
        var oversized = new string('x', HelperProtocol.MaxMessageBytes + 16);

        using var stream = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => HelperProtocol.WriteMessage(stream, oversized));
    }

    [Fact]
    public void ReadMessage_EmptyStream_ReturnsNull()
    {
        using var stream = new MemoryStream();
        Assert.Null(HelperProtocol.ReadMessage(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(HelperProtocol.MaxMessageBytes + 1)]
    public void ReadMessage_InvalidLength_ReturnsNull(int length)
    {
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(length));
        stream.Write(new byte[16]);
        stream.Position = 0;

        Assert.Null(HelperProtocol.ReadMessage(stream));
    }

    [Fact]
    public void ReadMessage_TruncatedPayload_ReturnsNull()
    {
        using var stream = new MemoryStream();
        stream.Write(BitConverter.GetBytes(64));   // 声明 64 字节，实际只有 8 字节
        stream.Write(new byte[8]);
        stream.Position = 0;

        Assert.Null(HelperProtocol.ReadMessage(stream));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("[]")]
    [InlineData("null")]
    public void ParseRequest_MalformedJson_ReturnsNull(string json)
    {
        Assert.Null(HelperProtocol.ParseRequest(json));
    }

    [Fact]
    public void ParseResponse_MalformedJson_ReturnsNull()
    {
        Assert.Null(HelperProtocol.ParseResponse("{\"isSuccess\": "));
    }

    [Fact]
    public void IsKnown_OnlyAcceptsFixedCommandSet()
    {
        foreach (var command in HelperCommands.All) Assert.True(HelperCommands.IsKnown(command));

        Assert.False(HelperCommands.IsKnown("cmd.exe /c calc"));
        Assert.False(HelperCommands.IsKnown("apply2"));
        Assert.False(HelperCommands.IsKnown("SNAPSHOT"));  // 大小写敏感：不做宽松匹配
        Assert.False(HelperCommands.IsKnown(null));
        Assert.Equal(8, HelperCommands.All.Count);
    }
}
