using Comm.Core.Frames;
using Comm.Plugins.Abstractions;

namespace Comm.Plugin.Sample;

/// <summary>
/// M3b 参考插件（规划 8.1、8.4、14.7）：
/// <list type="bullet">
///   <item>注册一个 <see cref="IFrameParser"/>：把原始帧解释成人可读的摘要（旁路解析，不参与收发）；</item>
///   <item>注册一个导出格式 <c>sample-tsv</c>：把当前报文列表导出为制表符分隔文本。</item>
/// </list>
/// 插件本身**不执行任何写操作**，也不依赖 WPF；它是编写自有厂商插件的模板。
/// <para>
/// 故障隔离自测（供 tests/Comm.Plugins.Host.Tests 验证宿主行为）：插件目录下存在
/// <c>throw-on-register.flag</c> 时 Register 故意抛异常；存在 <c>slow-register.flag</c> 时故意睡 30 秒
/// （用于验证宿主的注册超时与连续失败自动禁用）。
/// </para>
/// </summary>
public sealed class SamplePlugin : ICommPlugin
{
    public PluginManifest Manifest { get; } = new()
    {
        Id = "sample-plugin",
        Name = "示例插件（报文解析 + 导出）",
        Version = "1.0.0",
        Author = "通讯测试工具项目组",
        Description = "M3b 参考插件：演示扩展点注册（IFrameParser + 导出格式），只读、不执行任何写操作。",
        HostVersionRange = ">=0.2.0",
        Capabilities = new[] { PluginCapability.FrameParser, PluginCapability.ExportFormat },
        MayWrite = false,
        EntryAssembly = "Comm.Plugin.Sample.dll",
        EntryType = "Comm.Plugin.Sample.SamplePlugin"
    };

    public void Register(IPluginRegistry registry)
    {
        var directory = Path.GetDirectoryName(typeof(SamplePlugin).Assembly.Location) ?? ".";

        if (File.Exists(Path.Combine(directory, "throw-on-register.flag")))
        {
            throw new InvalidOperationException("示例插件：按自测标记故意抛出异常（验证插件故障隔离）");
        }

        if (File.Exists(Path.Combine(directory, "slow-register.flag")))
        {
            Thread.Sleep(TimeSpan.FromSeconds(3));
        }

        registry.AddFrameParser(new SampleFrameParser());
        registry.AddExporter("sample-tsv", SampleExporter.ExportTsv);
    }
}

/// <summary>示例解析器：不追求协议完备，只演示"旁路解析器"的形态（规划 14.1：解析器只消费 FrameRecord）。</summary>
public sealed class SampleFrameParser : IFrameParser
{
    public string Name => "sample-frame-summary";

    public bool CanParse(FrameRecord record) =>
        record.RawBytes.Length > 0 && record.Direction != FrameDirection.System;

    public string? Parse(FrameRecord record)
    {
        if (record.RawBytes.Length == 0) return null;

        var first = record.RawBytes[0];
        var function = record.FunctionCode?.ToString("X2") ?? "--";
        var preview = Convert.ToHexString(record.RawBytes.AsSpan(0, Math.Min(8, record.RawBytes.Length)));

        return $"[示例插件] {record.DirectionText} {record.RawBytes.Length} 字节；首字节 0x{first:X2}；" +
               $"功能码 {function}；从站 {record.UnitIdText}；前 {Math.Min(8, record.RawBytes.Length)} 字节 {preview}";
    }
}

/// <summary>示例导出格式：制表符分隔文本（首行是说明与时间，便于现场留档）。</summary>
public static class SampleExporter
{
    /// <summary>导出为 TSV；返回完整文本内容（宿主负责写文件，规划 14.7：导出格式只产出文本）。</summary>
    public static string ExportTsv(IReadOnlyList<FrameRecord> records, string title)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine($"# {title}");
        builder.AppendLine($"# 导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}  记录数：{records.Count}");
        builder.AppendLine("序号\t时间\t方向\t从站\t功能码\t字节数\t状态\t标记\t报文");

        foreach (var record in records)
        {
            builder.Append(record.Seq).Append('\t')
                .Append(record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append('\t')
                .Append(record.DirectionText).Append('\t')
                .Append(record.UnitIdText).Append('\t')
                .Append(record.FunctionCode?.ToString("X2") ?? string.Empty).Append('\t')
                .Append(record.RawBytes.Length).Append('\t')
                .Append(record.StatusText ?? string.Empty).Append('\t')
                .Append(record.MarkerText ?? string.Empty).Append('\t')
                .Append(record.Hex)
                .AppendLine();
        }

        return builder.ToString();
    }
}
