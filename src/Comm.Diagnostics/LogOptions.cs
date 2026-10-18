namespace Comm.Diagnostics;

/// <summary>日志设置（见规划 4.10、12.6）。</summary>
public sealed record LogOptions
{
    /// <summary>日志总开关；默认关闭，关闭时不创建、不写入任何文件。</summary>
    public bool Enabled { get; init; }

    /// <summary>仅本次运行开启（不写入设置文件）。</summary>
    public bool CurrentRunOnly { get; init; }

    /// <summary>日志目录；为空表示程序同级 logs 目录。</summary>
    public string? Directory { get; init; }

    public LogLevel MinimumLevel { get; init; } = LogLevel.Debug;

    /// <summary>是否把原始报文写入日志（默认关，可能包含现场数据）。</summary>
    public bool IncludeRawFrames { get; init; }

    public int MaxFileSizeMb { get; init; } = 20;
    public int RetentionDays { get; init; } = 14;
    public int TotalSizeMb { get; init; } = 500;

    /// <summary>崩溃报告开关（不受总开关控制，见规划 15.9）。</summary>
    public bool WriteCrashReports { get; init; } = true;
    public int CrashReportsToKeep { get; init; } = 10;
}

public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
    None
}

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception = null,
    int ThreadId = 0,
    string? Correlation = null)
{
    public override string ToString()
    {
        var text = $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level,-11}] [{Category}] {Message}";
        if (!string.IsNullOrEmpty(Correlation)) text += $" (关联: {Correlation})";
        if (!string.IsNullOrEmpty(Exception)) text += Environment.NewLine + Exception;
        return text;
    }
}

/// <summary>程序与运行环境信息，用于诊断包与关于页面。</summary>
public static class AppInfo
{
    public static string Version => typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
    public static string ProductName => "通讯测试工具";
    public static string RuntimeVersion => Environment.Version.ToString();
    public static string OsVersion => Environment.OSVersion.VersionString;
    public static string MachineName => Environment.MachineName;
    public static bool Is64Bit => Environment.Is64BitProcess;

    public static string Summary() =>
        $"{ProductName} v{Version} | 运行时 .NET {RuntimeVersion} | {OsVersion} | {(Is64Bit ? "x64" : "x86")} | 机器名 {MachineName}";
}
