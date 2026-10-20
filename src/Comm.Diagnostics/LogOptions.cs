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

/// <summary>程序与运行环境信息，用于诊断包与“关于”页面（R-21 X-8 / X-9）。</summary>
public static class AppInfo
{
    /// <summary>程序集版本（单一来源：Directory.Build.props 的 <c>Version</c>）。</summary>
    public static string Version =>
        Metadata("ProductVersion")
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>构建时间（UTC，构建时注入）。</summary>
    public static string BuildTimeUtc => Metadata("BuildTimeUtc") ?? "未知";

    /// <summary>
    /// 构建号（<c>yyyyMMddHHmm</c>，取构建时间 UTC），与更新清单里的 <c>build</c> 同格式。
    /// 仅用于展示与诊断：“是否有更新”只比版本号（见 UpdateService.IsNewer）。
    /// </summary>
    public static long BuildNumber
    {
        get
        {
            if (!DateTime.TryParse(BuildTimeUtc,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out var time))
            {
                return 0;
            }

            return long.TryParse(time.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture), out var build)
                ? build
                : 0;
        }
    }

    /// <summary>构建时的提交号（简短哈希）；非 Git 构建时为 <c>unknown</c>。</summary>
    public static string Commit => Metadata("SourceRevisionId") ?? "unknown";

    public static string ProductName => "通讯测试工具";
    public static string RuntimeVersion => Environment.Version.ToString();
    public static string OsVersion => Environment.OSVersion.VersionString;
    public static string MachineName => Environment.MachineName;
    public static bool Is64Bit => Environment.Is64BitProcess;

    /// <summary>界面标题用：`通讯测试工具 v0.2.5 — Modbus RTU / TCP 现场测试`。</summary>
    public static string Title => $"{ProductName} v{Version} — Modbus RTU / TCP 现场测试";

    /// <summary>“关于”/设置页用：版本 + 提交号 + 构建时间。若须改名，只需改 Directory.Build.props。</summary>
    public static string FullVersionText =>
        $"{ProductName} v{Version}" +
        (Commit == "unknown" ? string.Empty : $"（提交 {Commit}）") +
        $"，构建于 {BuildTimeUtc} UTC";

    public static string Summary() =>
        $"{ProductName} v{Version}{(Commit == "unknown" ? string.Empty : $"+{Commit}")} | " +
        $"构建 {BuildTimeUtc} | 运行时 .NET {RuntimeVersion} | {OsVersion} | {(Is64Bit ? "x64" : "x86")} | 机器名 {MachineName}";

    private static string? Metadata(string key) =>
        typeof(AppInfo).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase))
            ?.Value;
}
