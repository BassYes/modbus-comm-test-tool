using System.Globalization;
using System.Text;
using Comm.Core.Frames;

namespace Comm.Capture;

/// <summary>报文导出（见规划 4.6.6）：文本、CSV，另提供 JSONL（由记录器产生）。</summary>
public static class FrameExporter
{
    public static string ExportText(string path, IReadOnlyList<FrameRecord> records, string? filterDescription = null)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            writer.WriteLine("通讯测试工具 报文导出");
            writer.WriteLine($"导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
            writer.WriteLine($"时区：{TimeZoneInfo.Local.Id}");
            writer.WriteLine($"记录条数：{records.Count}");
            if (!string.IsNullOrEmpty(filterDescription)) writer.WriteLine($"过滤条件：{filterDescription}");
            writer.WriteLine(new string('-', 100));
            foreach (var record in records)
            {
                writer.Write($"[{record.Timestamp:HH:mm:ss.fff}] {record.DirectionText,-3} ");
                writer.Write(record.Hex);
                if (record.ElapsedMs is { } ms) writer.Write($"  ({ms:F1} ms)");
                if (!string.IsNullOrEmpty(record.StatusText)) writer.Write($"  [{record.StatusText}]");
                if (!string.IsNullOrEmpty(record.MarkerText)) writer.Write($"  <{record.MarkerText}>");
                if (!string.IsNullOrEmpty(record.Summary)) writer.Write($"  # {record.Summary}");
                writer.WriteLine();
            }
            return string.Empty;
        }
        catch (Exception ex)
        {
            return $"导出失败：{ex.Message}";
        }
    }

    public static string ExportCsv(string path, IReadOnlyList<FrameRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
            writer.WriteLine("序号,时间,方向,报文,状态,标记,从站地址,功能码,耗时ms,摘要");
            foreach (var record in records)
            {
                writer.WriteLine(string.Join(',',
                    record.Seq.ToString(CultureInfo.InvariantCulture),
                    Quote(record.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)),
                    record.DirectionText,
                    Quote(record.Hex),
                    Quote(record.StatusText),
                    Quote(record.MarkerText),
                    record.UnitId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    record.FunctionCode is { } fc ? $"0x{fc:X2}" : string.Empty,
                    record.ElapsedMs?.ToString("F1", CultureInfo.InvariantCulture) ?? string.Empty,
                    Quote(record.Summary ?? string.Empty)));
            }
            return string.Empty;
        }
        catch (Exception ex)
        {
            return $"导出失败：{ex.Message}";
        }
    }

    private static string Quote(string value) =>
        value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
