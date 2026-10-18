using System.Text;
using Microsoft.Extensions.Logging;

namespace Comm.Diagnostics;

/// <summary>把 Microsoft.Extensions.Logging 的日志写入 <see cref="LogGate"/>（见 AD-09）。</summary>
public sealed class LogGateLoggerProvider : ILoggerProvider
{
    private readonly LogGate _gate;

    public LogGateLoggerProvider(LogGate gate)
    {
        _gate = gate;
    }

    public ILogger CreateLogger(string categoryName) => new GateLogger(_gate, categoryName);

    public void Dispose()
    {
    }

    private sealed class GateLogger : ILogger
    {
        private readonly LogGate _gate;
        private readonly string _category;

        public GateLogger(LogGate gate, string category)
        {
            _gate = gate;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) =>
            _gate.IsLevelEnabled(Map(logLevel));

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var level = Map(logLevel);
            if (!_gate.IsLevelEnabled(level)) return;

            _gate.Write(new LogEntry(DateTimeOffset.Now, level, _category, formatter(state, exception),
                exception?.ToString(), Environment.CurrentManagedThreadId));
        }

        private static LogLevel Map(Microsoft.Extensions.Logging.LogLevel level) => level switch
        {
            Microsoft.Extensions.Logging.LogLevel.Trace => LogLevel.Trace,
            Microsoft.Extensions.Logging.LogLevel.Debug => LogLevel.Debug,
            Microsoft.Extensions.Logging.LogLevel.Information => LogLevel.Information,
            Microsoft.Extensions.Logging.LogLevel.Warning => LogLevel.Warning,
            Microsoft.Extensions.Logging.LogLevel.Error => LogLevel.Error,
            Microsoft.Extensions.Logging.LogLevel.Critical => LogLevel.Critical,
            _ => LogLevel.None
        };
    }
}

/// <summary>崩溃报告：不受日志总开关控制（见规划 15.9）。</summary>
public sealed class CrashReporter : IDisposable
{
    private readonly LogGate _gate;
    private readonly bool _enabled;
    private readonly int _keep;
    private bool _installed;

    public CrashReporter(LogGate gate, bool enabled = true, int keep = 10)
    {
        _gate = gate;
        _enabled = enabled;
        _keep = keep;
    }

    public string? LastReportPath { get; private set; }

    public void Install()
    {
        if (_installed) return;
        _installed = true;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteReport(e.ExceptionObject as Exception, "未处理的异常（AppDomain）");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteReport(e.Exception, "未观察的任务异常");
            e.SetObserved();
        };
    }

    public string? WriteReport(Exception? exception, string reason)
    {
        if (!_enabled) return null;

        try
        {
            Directory.CreateDirectory(_gate.Directory);
            var path = Path.Combine(_gate.Directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            var sb = new StringBuilder();
            sb.AppendLine(AppInfo.Summary());
            sb.AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
            sb.AppendLine($"原因：{reason}");
            sb.AppendLine(new string('=', 80));
            sb.AppendLine(exception?.ToString() ?? "(无异常对象)");
            sb.AppendLine(new string('=', 80));
            sb.AppendLine("最近日志（内存环形缓冲，不受日志开关控制）：");
            sb.AppendLine(_gate.BuildDiagnosticText(2000));

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            LastReportPath = path;
            TrimOldReports();
            return path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void TrimOldReports()
    {
        try
        {
            var files = new DirectoryInfo(_gate.Directory).GetFiles("crash-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).Skip(_keep);
            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }
    }

    /// <summary>上次是否异常退出（运行标志文件，见规划 15.9）。</summary>
    public static string RunningFlagPath => Path.Combine(AppContext.BaseDirectory, "data", ".running");

    public static void MarkRunning()
    {
        try
        {
            var path = RunningFlagPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception)
        {
        }
    }

    public static void MarkCleanExit()
    {
        try
        {
            if (File.Exists(RunningFlagPath)) File.Delete(RunningFlagPath);
        }
        catch (Exception)
        {
        }
    }

    public static bool DetectPreviousCrash()
    {
        try
        {
            return File.Exists(RunningFlagPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
    }
}
