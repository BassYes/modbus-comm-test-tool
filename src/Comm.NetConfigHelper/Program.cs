using System.Text;
using Comm.Platform.Windows;

namespace Comm.NetConfigHelper;

/// <summary>
/// 提权助手入口（规划 AD-08、12.5、15.8）。
/// <para>
/// 用法：<c>Comm.NetConfigHelper.exe --pipe &lt;名称&gt; [--parent &lt;pid&gt;] [--idle-min &lt;分钟&gt;] [--once]
/// [--log-level &lt;debug|info|warning|error&gt;]</c>；
/// 诊断：<c>--self-test</c>（只读枚举网卡，把报告写到 <c>&lt;logs&gt;/confighelper-selftest-&lt;时间戳&gt;.txt</c>）。
/// </para>
/// <para>
/// 退出码：0 = 正常结束；1 = self-test 失败或启动/运行失败；2 = 参数非法（未知参数也走这里）。
/// 本进程是 <b>WinExe</b>：不写 Console，一切诊断只进日志文件与 self-test 报告。
/// </para>
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFailure = 1;
    private const int ExitBadArguments = 2;

    private static HelperLog _log = HelperLog.Null;

    public static async Task<int> Main(string[] args)
    {
        var parsed = CommandLine.Parse(args);

        // 日志级别本身也是参数，所以先解析参数再建日志；参数错误同样要记日志（规划 12.5）。
        _log = HelperLog.CreateDefault(parsed.LogLevel);
        RegisterGlobalHandlers();

        if (parsed.Errors.Count > 0)
        {
            foreach (var error in parsed.Errors) _log.Error($"参数错误：{error}", "12.5");
            _log.Error($"用法：{CommandLine.Usage}", "12.5");
            _log.Info("助手启动失败：参数非法，退出码 2", "12.5");
            _log.Dispose();
            return ExitBadArguments;
        }

        if (parsed.SelfTest)
        {
            var code = RunSelfTest();
            _log.Dispose();
            return code;
        }

        return await RunServerAsync(parsed).ConfigureAwait(false);
    }

    private static async Task<int> RunServerAsync(CommandLine parsed)
    {
        var isAdmin = IsAdministrator();
        _log.Info(
            $"提权助手启动：管道 {parsed.PipeName}；父进程 {(parsed.ParentProcessId?.ToString() ?? "未指定")}；" +
            $"管理员身份 {isAdmin}；单请求 {parsed.Once}；日志 {_log.LogFileHint ?? "（不落盘）"}",
            "12.5");
        if (_log.FallbackNote is not null) _log.Warning(_log.FallbackNote, "12.6");
        if (!isAdmin)
        {
            // 不静默失败（规划 4.8.3）：没提权就明确记录，主程序那边会拿到后端返回的失败结果。
            _log.Warning("助手未以管理员身份运行，网卡写操作可能失败（应为 runas 拉起）", "4.8.3");
        }

        var options = new HelperServerOptions
        {
            PipeName = parsed.PipeName!,
            ParentProcessId = parsed.ParentProcessId,
            IdleExitMinutes = parsed.IdleExitMinutes,
            SingleRequest = parsed.Once,
            // 审计 F-02：生产入口必须携带 --parent，且服务端对取不到调用方 PID 的情况 fail closed。
            // 没有 --parent 的助手不再是"少一层防御"，而是直接不可用（否则固定命令白名单会暴露给同机任意进程）。
            RequireParentProcessId = true
        };

        if (parsed.ParentProcessId is null)
        {
            _log.Error(
                "缺少 --parent <pid>：助手拒绝在无法校验调用方身份的情况下运行（审计 F-02）。" +
                "请由主程序拉起，或用于诊断时改用 --self-test。",
                "12.5");
            _log.Dispose();
            return ExitBadArguments;
        }

        INetworkAdapterBackend backend;
        try
        {
            // 只经由工厂取得后端：生产实现（IpHelperBackend / netsh 降级）由 Comm.Platform.Windows 决定。
            backend = AdapterBackendFactory.Create();
        }
        catch (Exception ex)
        {
            _log.Error("创建网卡读写后端失败，助手退出", "12.5", ex);
            _log.Dispose();
            return ExitFailure;
        }

        using var cts = new CancellationTokenSource();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                cts.Cancel();
            }
            catch (Exception)
            {
            }
        };

        using var server = new HelperServer(backend, options, _log);
        try
        {
            await server.RunAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error("助手运行失败", "12.5", ex);
            _log.Dispose();
            return ExitFailure;
        }

        // 审计 F-03：服务循环已结束但倒计时仍武装时，进程不能就这么退出——
        // 否则"主程序崩溃/关窗也能自动恢复"的安全网会随进程一起消失。
        await HoldUntilCountdownResolvedAsync(server, cts.Token).ConfigureAwait(false);

        _log.Info($"助手退出：共处理 {server.RequestCount} 条请求", "12.5");
        _log.Dispose();
        return ExitOk;
    }

    /// <summary>
    /// 倒计时未决时保持进程存活：自己接管 1 秒节拍推进 <see cref="RollbackCountdown"/>，
    /// 到期回滚或用户确认后才真正退出。安全上限比武装秒数多 30 秒，避免异常状态下永久驻留。
    /// </summary>
    private static async Task HoldUntilCountdownResolvedAsync(HelperServer server, CancellationToken cancellationToken)
    {
        if (!server.Countdown.IsArmed) return;

        _log.Warning(
            $"服务循环已结束，但安全恢复倒计时仍武装（剩余 {server.Countdown.RemainingSeconds} 秒）：" +
            "助手保持运行直到自动回滚或用户确认（审计 F-03）",
            "4.8.3");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(server.Countdown.RemainingSeconds + 30);
        while (server.Countdown.IsArmed && DateTimeOffset.UtcNow < deadline)
        {
            if (server.Countdown.Tick())
            {
                if (server.Countdown.LastError is null)
                {
                    _log.Info("安全恢复倒计时到期，已自动回滚到修改前配置", "15.8");
                }
                else
                {
                    _log.Error($"安全恢复倒计时到期，但自动回滚失败：{server.Countdown.LastError}；请按快照人工恢复", "15.8");
                }

                break;
            }

            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (server.Countdown.IsArmed)
        {
            _log.Error($"等待倒计时结束超时（剩余 {server.Countdown.RemainingSeconds} 秒），助手退出；" +
                       "未确认的网卡配置需要人工按快照恢复", "15.8");
        }
    }

    /// <summary>
    /// <c>--self-test</c>：<b>只读</b>枚举网卡，把报告写到日志目录下的
    /// <c>confighelper-selftest-&lt;时间戳&gt;.txt</c>，退出码 0/1（规划 16.3.7 的现场自检）。
    /// 绝不调用任何写操作（禁止 netsh set / IP Helper 写入）。
    /// </summary>
    private static int RunSelfTest()
    {
        var sb = new StringBuilder();
        var ok = true;

        sb.AppendLine("== 通讯测试工具 网卡配置提权助手 · 自检报告（只读） ==");
        sb.AppendLine($"生成时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        sb.AppendLine($"程序文件：{Environment.ProcessPath ?? "-"}");
        sb.AppendLine($"运行时：{Environment.Version}（{(Environment.Is64BitProcess ? "x64" : "x86")}）");
        sb.AppendLine($"操作系统：{Environment.OSVersion.VersionString}");
        sb.AppendLine($"管理员身份：{IsAdministrator()}");
        sb.AppendLine($"日志文件：{_log.LogFileHint ?? "（不落盘）"}");
        if (_log.FallbackNote is not null) sb.AppendLine($"目录提示：{_log.FallbackNote}");
        sb.AppendLine();

        sb.AppendLine("-- 网卡后端与适配器（只读枚举） --");
        try
        {
            var backend = AdapterBackendFactory.Create();
            sb.AppendLine($"后端实现：{backend.GetType().Name}");
            var adapters = backend.ReadAdapters();
            sb.AppendLine($"适配器数量：{adapters.Count}");
            if (adapters.Count == 0)
            {
                ok = false;
                sb.AppendLine("未检测到任何适配器：请确认网卡驱动正常，或查看上面的后端实现是否可用。");
            }

            foreach (var adapter in adapters)
            {
                sb.AppendLine($"  * {adapter.Name}（{adapter.Description}）");
                sb.AppendLine($"    id：{adapter.Id}");
                sb.AppendLine($"    MAC：{(string.IsNullOrWhiteSpace(adapter.MacAddress) ? "-" : adapter.MacAddress)}；" +
                              $"状态：{(adapter.IsUp ? "已连接" : "未连接")}；" +
                              $"虚拟：{adapter.IsVirtual}；回环：{adapter.IsLoopback}；DHCP：{adapter.DhcpEnabled}");
                sb.AppendLine($"    IPv4：{(adapter.Ipv4.Count == 0 ? "无" : string.Join(" / ", adapter.Ipv4.Select(a => a.DisplayText)))}");
                sb.AppendLine($"    网关：{(adapter.Gateways.Count == 0 ? "-" : string.Join(",", adapter.Gateways))}；" +
                              $"DNS：{(adapter.DnsServers.Count == 0 ? "-" : string.Join(",", adapter.DnsServers))}");
                sb.AppendLine($"    快照采集时间：{adapter.CapturedAt:yyyy-MM-dd HH:mm:ss}");
            }
        }
        catch (Exception ex)
        {
            ok = false;
            sb.AppendLine($"只读枚举网卡失败：{ex.GetType().Name}: {ex.Message}");
            _log.Error("self-test 只读枚举网卡失败", "16.3.7", ex);
        }

        sb.AppendLine();
        sb.AppendLine("说明：self-test 只做只读枚举，不修改任何网卡配置。");
        sb.AppendLine($"结论：{(ok ? "通过" : "失败")}");

        var directory = _log.LogDirectory;
        if (directory is null)
        {
            var (fallback, note) = HelperLog.ResolveWritableDirectory();
            directory = fallback;
            if (note is not null) sb.AppendLine($"目录提示：{note}");
        }

        if (directory is null)
        {
            _log.Error("self-test 找不到可写目录，无法保存自检报告", "16.3.7");
            return ExitFailure;
        }

        var path = Path.Combine(directory, $"confighelper-selftest-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            _log.Info($"self-test 完成（{(ok ? "通过" : "失败")}），报告：{path}", "16.3.7");
            return ok ? ExitOk : ExitFailure;
        }
        catch (Exception ex)
        {
            _log.Error($"self-test 报告写入失败：{path}", "16.3.7", ex);
            return ExitFailure;
        }
    }

    /// <summary>注册全局异常处理（规划 15.9）：助手没有界面，异常只能进日志，绝不能静默消失。</summary>
    private static void RegisterGlobalHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                _log.Error("未处理异常（AppDomain.UnhandledException）", "15.9", e.ExceptionObject as Exception);
                _log.Flush();
            }
            catch (Exception)
            {
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                _log.Error("未观察的任务异常（TaskScheduler.UnobservedTaskException）", "15.9", e.Exception);
            }
            catch (Exception)
            {
            }

            e.SetObserved();
        };
    }

    private static bool IsAdministrator()
    {
        try
        {
            return Environment.IsPrivilegedProcess;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>命令行解析结果。</summary>
    private sealed class CommandLine
    {
        public const string Usage =
            "Comm.NetConfigHelper.exe --pipe <名称> [--parent <pid>] [--idle-min <分钟>] [--once] " +
            "[--log-level <debug|info|warning|error>] [--self-test]";

        public string? PipeName { get; private set; }
        public int? ParentProcessId { get; private set; }
        public int IdleExitMinutes { get; private set; } = 5;
        public bool Once { get; private set; }
        public bool SelfTest { get; private set; }
        public HelperLogLevel LogLevel { get; private set; } = HelperLogLevel.Info;
        public List<string> Errors { get; } = new();

        public static CommandLine Parse(string[] args)
        {
            var result = new CommandLine();

            for (int i = 0; i < args.Length; i++)
            {
                var raw = args[i];
                var (name, inlineValue) = Split(raw);

                switch (name)
                {
                    case "--pipe":
                        result.PipeName = TakeValue(args, ref i, inlineValue, name, result);
                        break;

                    case "--parent":
                        var parentText = TakeValue(args, ref i, inlineValue, name, result);
                        if (parentText is null) break;
                        if (int.TryParse(parentText, out var parent) && parent > 0) result.ParentProcessId = parent;
                        else result.Errors.Add($"--parent 需要正整数进程 ID，收到 \"{parentText}\"");
                        break;

                    case "--idle-min":
                        var idleText = TakeValue(args, ref i, inlineValue, name, result);
                        if (idleText is null) break;
                        if (int.TryParse(idleText, out var idle) && idle > 0) result.IdleExitMinutes = idle;
                        else result.Errors.Add($"--idle-min 需要正整数（分钟），收到 \"{idleText}\"");
                        break;

                    case "--log-level":
                        var levelText = TakeValue(args, ref i, inlineValue, name, result);
                        if (levelText is null) break;
                        if (TryParseLevel(levelText, out var level)) result.LogLevel = level;
                        else result.Errors.Add($"--log-level 只能是 debug / info / warning / error，收到 \"{levelText}\"");
                        break;

                    case "--once":
                        result.Once = true;
                        break;

                    case "--self-test":
                        result.SelfTest = true;
                        break;

                    default:
                        // 未知参数：记日志并以退出码 2 结束（规划 12.5：助手不接受任意命令行）。
                        result.Errors.Add($"未知参数 \"{raw}\"");
                        break;
                }
            }

            if (!result.SelfTest && string.IsNullOrWhiteSpace(result.PipeName))
            {
                result.Errors.Add("缺少必填参数 --pipe <名称>（由主程序用 HelperProtocol.CreatePipeName() 生成）");
            }

            return result;
        }

        private static (string Name, string? Value) Split(string raw)
        {
            int index = raw.IndexOf('=');
            if (!raw.StartsWith("--", StringComparison.Ordinal) || index < 0) return (raw, null);
            return (raw[..index], raw[(index + 1)..]);
        }

        private static string? TakeValue(string[] args, ref int index, string? inlineValue, string name, CommandLine target)
        {
            if (inlineValue is not null) return inlineValue;
            if (index + 1 >= args.Length)
            {
                target.Errors.Add($"{name} 缺少取值");
                return null;
            }

            index++;
            return args[index];
        }

        private static bool TryParseLevel(string text, out HelperLogLevel level)
        {
            switch (text.Trim().ToLowerInvariant())
            {
                case "debug":
                    level = HelperLogLevel.Debug;
                    return true;
                case "info":
                    level = HelperLogLevel.Info;
                    return true;
                case "warning":
                case "warn":
                    level = HelperLogLevel.Warning;
                    return true;
                case "error":
                    level = HelperLogLevel.Error;
                    return true;
                default:
                    level = HelperLogLevel.Info;
                    return false;
            }
        }
    }
}
