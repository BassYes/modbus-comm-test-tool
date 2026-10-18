using Comm.Core.Codecs;
using Comm.Core.Frames;
using Comm.Protocols.Modbus;

namespace Comm.Protocols.Modbus;

/// <summary>由 ADU 生成一行报文摘要（解析开关关闭时也可只显示原始十六进制）。</summary>
public static class FrameSummaryBuilder
{
    public static string Build(IFrameCodec codec, ReadOnlySpan<byte> adu, bool isRequest)
    {
        try
        {
            if (codec.ChecksumLength == 0)
            {
                if (adu.Length < 8) return "帧过短";
                return PduParser.Parse(ModbusPdu.Parse(adu[7..]), isRequest).Describe();
            }

            if (adu.Length < 4) return "帧过短";
            var body = adu[..^2];
            return PduParser.Parse(ModbusPdu.Parse(body[1..]), isRequest).Describe();
        }
        catch (Exception)
        {
            return "解析失败";
        }
    }
}
