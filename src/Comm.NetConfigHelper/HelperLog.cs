using System.Text;
using Comm.Diagnostics;

namespace Comm.NetConfigHelper;

/// <summary>助手日志级别。<c>--log-level</c> 支持 debug / info / warning / error（规划 12.6）。</summary>
public enum HelperLogLevel
{
    Debug = 0,
    Info = 1,
    Warning = 2,
    Error = 3
}

/// <summary>
/// 提权助手专用的轻量文件日志（规划 4.10、12.5 "助手自身也遵守日志开关，写入同一日志目录，使用独立文件名前缀"、
/// 12.6 目录解析顺序）。
/// <para>
/// 设计要点：
/// 1. <b>不阻塞请求处理</b>：写入走 <see cref="RollingFileWriter"/> 的后台通道（有界队列、满了丢弃），
///    命名管道循环里调用日志不会因为磁盘慢而卡住倒计时线程（规划 13.6）。
/// 2. <b>线程安全</b>：所有写入经通道串行化，多线程（管道循环 + 倒计时定时器）可并发调用。
/// 3. <b>目录回退</b>：优先 <see cref="AppPaths.LogsDirectory"/>（程序同级 logs），不可写时回退
///    <c>%LOCALAPPDATA%\通讯测试工具\logs</c>，再不行就只丢弃日志（绝不因日志失败而中断助手的回滚职责）。
/// 4. <b>文件名</b>：<c>confighelper-YYYYMMDD-HHMMSS.log</c>，单文件 20 MB 后滚动为 <c>…-001.log</c>（规划 4.10.2）。
/// 5. 助手是 WinExe：<b>不写 Console</b>，所有诊断只走日志文件与 self-test 报告。
/// </para>
/// </summary>
public sealed class HelperLog : IDisposable
{
    /// <summary>日志文件名前缀（规划 12.5：助手使用独立文件名前缀，与主程序的 debug-* 区分）。</summary>
    public const string FilePrefix = "confighelper";

    /// <summary>单个日志文件上限（MB），超过后滚动（规划 4.10.2）。</summary>
    public const int MaxFileSizeMb = 20;

    private readonly RollingFileWriter? _writer;

    private int _disposed;

    /// <summary>创建一个写入 <paramref name="directory"/> 的日志。<paramref name="directory"/> 为 null 时是"空日志"（不落盘）。</summary>
    public HelperLog(string? directory, HelperLogLevel minimumLevel = HelperLogLevel.Info, string? fallbackNote = null)
    {
        MinimumLevel = minimumLevel;
        FallbackNote = fallbackNote;

        if (string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            System.IO.Directory.CreateDirectory(directory!);
            LogDirectory = directory;
            LogFileHint = Path.Combine(directory!, $"{FilePrefix}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log");
            _writer = new RollingFileWriter(LogFileHint, MaxFileSizeMb);
        }
        catch (Exception ex)
        {
            // 日志目录不可写也不能让助手启动失败：只是不落盘。
            LogDirectory = null;
            LogFileHint = null;
            FallbackNote = $"创建日志目录失败（{directory}）：{ex.Message}；助手日志不落盘";
        }
    }

    /// <summary>不落盘的空日志（测试与"日志关闭"路径使用）。</summary>
    public static HelperLog Null { get; } = new((string?)null);

    /// <summary>实际使用的日志目录；null 表示不落盘。</summary>
    public string? LogDirectory { get; }

    /// <summary>预期日志文件完整路径（供启动日志与 self-test 报告展示）。</summary>
    public string? LogFileHint { get; }

    /// <summary>目录回退/失败说明（规划 12.6 要求回退必须在界面或日志可见）。</summary>
    public string? FallbackNote { get; }

    /// <summary>低于该级别的日志被丢弃。</summary>
    public HelperLogLevel MinimumLevel { get; }

    /// <summary>是否完全不落盘。</summary>
    public bool IsSilent => _writer is null;

    /// <summary>
    /// 解析一个可写的日志目录（规划 12.6）：① 程序同级 <c>logs/</c>；② <c>%LOCALAPPDATA%\通讯测试工具\logs</c>；③ null。
    /// </summary>
    public static (string? Directory, string? Note) ResolveWritableDirectory()
    {
        var preferred = AppPaths.LogsDirectory;
        if (AppPaths.TryEnsureWritable(preferred)) return (preferred, null);

        var fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppPaths.ProductFolderName,
            "logs");

        if (AppPaths.TryEnsureWritable(fallback))
        {
            return (fallback, $"程序目录 logs 不可写（{preferred}），已回退到 {fallback}（规划 12.6）");
        }

        return (null, $"程序目录 logs（{preferred}）与用户目录（{fallback}）均不可写；助手日志不落盘");
    }

    /// <summary>按默认目录解析顺序创建日志。</summary>
    public static HelperLog CreateDefault(HelperLogLevel minimumLevel = HelperLogLevel.Info)
    {
        var (directory, note) = ResolveWritableDirectory();
        return new HelperLog(directory, minimumLevel, note);
    }

    public void Debug(string message, string? section = null) => Write(HelperLogLevel.Debug, message, section);

    public void Info(string message, string? section = null) => Write(HelperLogLevel.Info, message, section);

    public void Warning(string message, string? section = null) => Write(HelperLogLevel.Warning, message, section);

    /// <summary>错误日志；异常堆栈只写日志，绝不通过命名管道外传（规划 12.5、15.9）。</summary>
    public void Error(string message, string? section = null, Exception? exception = null) =>
        Write(HelperLogLevel.Error, message, section, exception);

    /// <summary>
    /// 写一条日志。<paramref name="section"/> 标注对应的规划章节（例如 <c>"12.5"</c>），便于现场按章节排查。
    /// 本方法不做任何同步 I/O，也不会抛出。
    /// </summary>
    public void Write(HelperLogLevel level, string message, string? section = null, Exception? exception = null)
    {
        if (level < MinimumLevel) return;
        if (Volatile.Read(ref _disposed) != 0) return;

        var writer = _writer;
        if (writer is null) return;

        try
        {
            var sb = new StringBuilder(message.Length + 96);
            sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(" [").Append(LevelText(level)).Append("] ");
            if (!string.IsNullOrEmpty(section)) sb.Append('[').Append(section).Append("] ");
            sb.Append(message);

            if (exception is not null)
            {
                sb.AppendLine();
                sb.Append("    ").Append(exception.GetType().FullName).Append(": ").Append(exception.Message);
                if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                {
                    sb.AppendLine();
                    sb.Append(exception.StackTrace);
                }
            }

            writer.WriteLine(sb.ToString());
        }
        catch (Exception)
        {
            // 日志自身绝不抛出（否则会打断回滚倒计时）。
        }
    }

    /// <summary>退出前尽力刷盘（超时即放弃，不阻塞进程退出）。</summary>
    public void Flush(TimeSpan? timeout = null)
    {
        var writer = _writer;
        if (writer is null) return;

        try
        {
            writer.DisposeAsync().AsTask().Wait(timeout ?? TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Flush();
    }

    private static string LevelText(HelperLogLevel level) => level switch
    {
        HelperLogLevel.Debug => "DEBUG",
        HelperLogLevel.Info => "INFO ",
        HelperLogLevel.Warning => "WARN ",
        _ => "ERROR"
    };
}
