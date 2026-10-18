using System.Text;

namespace Comm.Diagnostics;

/// <summary>
/// 日志总闸门：开关、内存环形日志、落盘、目录回退与清理都集中在这里（见规划 4.10、12.6）。
/// </summary>
public sealed class LogGate : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly DiagnosticRing _ring;
    private RollingFileWriter? _writer;
    private bool _enabled;

    public LogGate(LogOptions options, int ringCapacity = 2000)
    {
        Options = options;
        _ring = new DiagnosticRing(ringCapacity);
        var (dir, warning) = ResolveDirectory(options.Directory);
        Directory = dir;
        DirectoryWarning = warning;
        if (options.Enabled) SetEnabled(true, writeMarker: false);
    }

    public LogOptions Options { get; private set; }
    public string Directory { get; }
    public string? DirectoryWarning { get; }
    public DiagnosticRing Ring => _ring;
    public string? ActiveLogFile { get; private set; }

    public bool Enabled
    {
        get { lock (_sync) return _enabled; }
    }

    public event EventHandler? StateChanged;

    public long DroppedCount => _writer?.DroppedCount ?? 0;

    /// <summary>打开或关闭日志。关闭时不创建文件、不打开句柄（见规划 4.10.1）。</summary>
    public void SetEnabled(bool enabled, bool writeMarker = true)
    {
        RollingFileWriter? toDispose = null;

        lock (_sync)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;

            if (enabled)
            {
                var file = Path.Combine(Directory, $"debug-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                _writer = new RollingFileWriter(file, Options.MaxFileSizeMb);
                ActiveLogFile = file;
            }
            else
            {
                toDispose = _writer;
                _writer = null;
            }
        }

        if (enabled && writeMarker)
        {
            Write(new LogEntry(DateTimeOffset.Now, LogLevel.Information, "Diagnostics",
                $"日志已开启。{AppInfo.Summary()}"));
            CleanupOldFiles();
        }
        else if (!enabled && writeMarker)
        {
            toDispose?.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} [Information] [Diagnostics] 日志已关闭。");
        }

        toDispose?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplyOptions(LogOptions options)
    {
        Options = options;
        SetEnabled(options.Enabled);
    }

    public bool IsLevelEnabled(LogLevel level) =>
        level >= Options.MinimumLevel && level != LogLevel.None;

    public void Write(LogEntry entry)
    {
        _ring.Add(entry);
        if (!Enabled) return;

        RollingFileWriter? writer;
        lock (_sync) writer = _writer;
        writer?.WriteLine(entry.ToString());
    }

    public void Info(string category, string message, string? correlation = null) =>
        Write(new LogEntry(DateTimeOffset.Now, LogLevel.Information, category, message, null, Environment.CurrentManagedThreadId, correlation));

    public void Debug(string category, string message, string? correlation = null) =>
        Write(new LogEntry(DateTimeOffset.Now, LogLevel.Debug, category, message, null, Environment.CurrentManagedThreadId, correlation));

    public void Warning(string category, string message, string? correlation = null) =>
        Write(new LogEntry(DateTimeOffset.Now, LogLevel.Warning, category, message, null, Environment.CurrentManagedThreadId, correlation));

    public void Error(string category, string message, Exception? ex = null, string? correlation = null) =>
        Write(new LogEntry(DateTimeOffset.Now, LogLevel.Error, category, message, ex?.ToString(), Environment.CurrentManagedThreadId, correlation));

    /// <summary>最近日志文本（用于“复制诊断信息”）。</summary>
    public string BuildDiagnosticText(int maxEntries = 500)
    {
        var entries = Ring.Snapshot();
        var sb = new StringBuilder();
        sb.AppendLine(AppInfo.Summary());
        sb.AppendLine($"日志开关：{(Enabled ? "开" : "关")}；日志目录：{Directory}；当前文件：{ActiveLogFile ?? "无"}");
        if (!string.IsNullOrEmpty(DirectoryWarning)) sb.AppendLine($"目录提示：{DirectoryWarning}");
        sb.AppendLine(new string('-', 80));
        foreach (var entry in entries.Skip(Math.Max(0, entries.Count - maxEntries)))
        {
            sb.AppendLine(entry.ToString());
        }
        return sb.ToString();
    }

    /// <summary>按保留天数与总容量清理旧日志（见规划 4.10.4）。</summary>
    public void CleanupOldFiles()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory)) return;
            var files = new DirectoryInfo(Directory).GetFiles("*.log").OrderByDescending(f => f.LastWriteTimeUtc).ToList();

            var cutoff = DateTime.UtcNow.AddDays(-Options.RetentionDays);
            foreach (var file in files.Where(f => f.LastWriteTimeUtc < cutoff))
            {
                TryDelete(file);
            }

            long total = 0;
            long limit = (long)Options.TotalSizeMb * 1024 * 1024;
            foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc))
            {
                total += file.Length;
                if (total > limit) TryDelete(file);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
        }
        catch (Exception)
        {
        }
    }

    private static (string Directory, string? Warning) ResolveDirectory(string? configured)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured!);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "logs"));
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "通讯测试工具", "logs"));

        foreach (var candidate in candidates)
        {
            if (TryEnsureWritable(candidate)) return (candidate, null);
        }

        var fallback = Path.GetTempPath();
        return (fallback, "程序目录与用户数据目录均不可写，日志已回退到系统临时目录；请在设置中指定可写目录。");
    }

    private static bool TryEnsureWritable(string directory)
    {
        try
        {
            System.IO.Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        RollingFileWriter? writer;
        lock (_sync)
        {
            writer = _writer;
            _writer = null;
            _enabled = false;
        }
        if (writer is not null) await writer.DisposeAsync().ConfigureAwait(false);
    }
}
