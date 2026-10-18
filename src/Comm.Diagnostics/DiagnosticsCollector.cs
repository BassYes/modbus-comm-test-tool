using System.IO.Compression;
using System.Text;

namespace Comm.Diagnostics;

/// <summary>诊断包：日志 + 环境摘要 + 设置摘要（见规划 4.10.4，不自动上传）。</summary>
public static class DiagnosticsCollector
{
    public static string Collect(LogGate gate, string targetZipPath, string? settingsJson = null, bool includeRawFrameLogs = false)
    {
        try
        {
            var dir = Path.GetDirectoryName(targetZipPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(targetZipPath)) File.Delete(targetZipPath);

            using var zip = ZipFile.Open(targetZipPath, ZipArchiveMode.Create);

            var summary = zip.CreateEntry("summary.txt");
            using (var writer = new StreamWriter(summary.Open(), new UTF8Encoding(false)))
            {
                writer.WriteLine(AppInfo.Summary());
                writer.WriteLine($"生成时间：{DateTimeOffset.Now:O}");
                writer.WriteLine($"日志开关：{(gate.Enabled ? "开" : "关")}");
                writer.WriteLine($"日志目录：{gate.Directory}");
                if (!string.IsNullOrEmpty(gate.DirectoryWarning)) writer.WriteLine($"目录提示：{gate.DirectoryWarning}");
                writer.WriteLine(new string('-', 80));
                writer.WriteLine(gate.BuildDiagnosticText(1000));
            }

            if (!string.IsNullOrEmpty(settingsJson))
            {
                var entry = zip.CreateEntry("settings.json");
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(settingsJson);
            }

            if (Directory.Exists(gate.Directory))
            {
                foreach (var file in Directory.GetFiles(gate.Directory, "*.log"))
                {
                    var name = Path.GetFileName(file);
                    if (!includeRawFrameLogs && name.StartsWith("frames-", StringComparison.OrdinalIgnoreCase)) continue;
                    try
                    {
                        zip.CreateEntryFromFile(file, "logs/" + name, CompressionLevel.Optimal);
                    }
                    catch (Exception)
                    {
                        // 单个文件被占用不影响整体打包
                    }
                }
            }

            return string.Empty;
        }
        catch (Exception ex)
        {
            return $"生成诊断包失败：{ex.Message}";
        }
    }
}
