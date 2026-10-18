using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using Comm.Core.Errors;

namespace Comm.Platform.Windows;

// ---------------------------------------------------------------------------------------------
// 提权助手客户端（规划 12.5 / AD-08）：
//   主程序（普通用户）永远不直接改网卡。它把"请求"通过命名管道发给管理员身份的
//   Comm.NetConfigHelper，由助手调用 IpHelperBackend / NetshBackend 完成写入。
//
//   要点：
//     ① 只在第一次发送时定位并拉起助手，之后复用同一个助手进程（避免每次都弹 UAC）；
//     ② 管道名带随机后缀，且由主程序生成后通过命令行参数交给助手（不写死，避免被抢注）；
//     ③ 每次 Send 新建一条连接（一条连接 = 一条请求 + 一条响应），结束后立刻关闭；
//     ④ 用户拒绝 UAC 后记住状态，绝不反复弹窗（规划 12.5：UAC 拒绝返回明确错误，不影响其他功能）；
//     ⑤ 助手缺失/超时/协议异常一律转换成 HelperResponse.Fail，绝不抛异常给界面。
// ---------------------------------------------------------------------------------------------

/// <summary>
/// 通过命名管道与提权助手通信的客户端（<see cref="INetConfigHelper"/> 的生产实现）。
/// 所有失败都以 <see cref="HelperResponse"/> 返回，方法本身不抛异常。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetConfigHelperClient : INetConfigHelper
{
    /// <summary>连接助手的超时（秒级足够：管道是本地环回，慢的通常是 UAC 弹窗，那段时间算在拉起阶段）。</summary>
    public const int ConnectTimeoutMs = 15_000;

    /// <summary>默认空闲退出时间（分钟），与规划 12.5 一致。</summary>
    public const int DefaultIdleExitMinutes = 5;

    private readonly IHelperLauncher _launcher;
    private readonly string? _configuredHelperPath;
    private readonly int _idleExitMinutes;
    private readonly Action<string, string, string?>? _log;

    /// <summary>本次运行使用的管道名（首次 Send 时生成）。</summary>
    private string? _pipeName;

    /// <summary>助手进程（可能为 null：助手已存在但由其他窗口拉起）。</summary>
    private Process? _process;

    /// <summary>用户是否已经拒绝过 UAC；为 true 时不再尝试拉起（避免反复弹窗）。</summary>
    private bool _elevationDenied;

    /// <summary>创建客户端。</summary>
    /// <param name="launcher">拉起助手的方式；默认 <see cref="RunAsHelperLauncher"/>（runas 动词 = 触发 UAC）。</param>
    /// <param name="helperPath">助手路径；null 时用 <see cref="NetConfigHelperLocator.Resolve"/> 按约定顺序查找。</param>
    /// <param name="idleExitMinutes">助手空闲自动退出时间（分钟）。</param>
    /// <param name="log">可选日志回调（级别、分类、正文）；库内不使用 Console。</param>
    public NetConfigHelperClient(
        IHelperLauncher? launcher = null,
        string? helperPath = null,
        int idleExitMinutes = DefaultIdleExitMinutes,
        Action<string, string, string?>? log = null)
    {
        _launcher = launcher ?? new RunAsHelperLauncher();
        _configuredHelperPath = string.IsNullOrWhiteSpace(helperPath) ? null : helperPath;
        _idleExitMinutes = idleExitMinutes <= 0 ? DefaultIdleExitMinutes : idleExitMinutes;
        _log = log;
        StatusText = IsHelperPresent ? $"助手已就绪（{ExpectedPath}）" : $"未找到助手：{ExpectedPath}";
    }

    /// <inheritdoc />
    public bool IsHelperPresent => File.Exists(ExpectedPath);

    /// <inheritdoc />
    /// <remarks>未显式注入路径时，按 <see cref="NetConfigHelperLocator.Resolve"/> 的顺序每次重新定位。</remarks>
    public string ExpectedPath =>
        _configuredHelperPath ?? NetConfigHelperLocator.Resolve();

    /// <inheritdoc />
    public string StatusText { get; private set; }

    /// <summary>用户是否拒绝过 UAC（界面据此显示"已取消提权"并提供重新授权入口）。</summary>
    public bool ElevationDenied => _elevationDenied;

    /// <summary>
    /// 清除"用户拒绝过 UAC"的记忆。仅当界面上用户主动点击"重新授权"时才应调用，
    /// 否则会破坏"不得反复弹窗"的约束（规划 12.5）。
    /// </summary>
    public void ResetElevationDenied()
    {
        _elevationDenied = false;
        StatusText = "将再次请求管理员权限";
        Log("Information", "用户请求重新提权");
    }

    /// <inheritdoc />
    public HelperResponse Send(HelperRequest request, int timeoutMs = 60_000)
    {
        if (!HelperCommands.IsKnown(request.Command))
        {
            // 助手只接受固定命令集；客户端也先挡一层，避免把任意字符串送进管理员进程。
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol,
                $"拒绝发送未知命令 \"{request.Command}\"（助手只接受 {string.Join(" / ", HelperCommands.All)}）");
        }

        if (_elevationDenied)
        {
            return HelperResponse.Fail(ErrorCodes.PlatformHelperUnavailable,
                "上次修改网卡时取消了管理员权限请求，本次不再弹出提权窗口；如需继续，请在界面点击“重新授权”");
        }

        if (!IsHelperPresent)
        {
            StatusText = $"未找到助手：{ExpectedPath}";
            return HelperResponse.Fail(ErrorCodes.PlatformHelperUnavailable,
                $"未找到助手：{ExpectedPath}（{NetConfigHelperLocator.FileName}）。已查找 {NetConfigHelperLocator.DescribeSearchPaths()}");
        }

        try
        {
            if (_pipeName is null && !StartHelper())
            {
                return HelperResponse.Fail(ErrorCodes.PlatformHelperUnavailable, StatusText);
            }

            return Exchange(request, timeoutMs);
        }
        catch (OperationCanceledException)
        {
            StatusText = "等待提权助手响应超时";
            Log("Warning", $"命令 {request.Command} 超时（{timeoutMs} ms）");
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol,
                $"等待提权助手响应超时（{timeoutMs / 1000} 秒），请检查是否被安全软件拦截");
        }
        catch (UnauthorizedAccessException ex)
        {
            // 审计 F-02：助手管道现在带 ACL（仅目标用户/主程序可连），被拒时给出可操作的提示，
            // 不要混进"通信失败"里让现场误判成软件 bug。
            StatusText = "无权连接提权助手";
            Log("Error", $"连接提权助手被拒绝（管道 ACL）：{ex.Message}");
            return HelperResponse.Fail(ErrorCodes.PlatformPermission,
                "无权连接提权助手：命名管道的 ACL 只允许拉起它的用户与主程序。" +
                "请确认主程序与提权助手以同一用户身份运行；如使用另一管理员账户提权，请改用该账户登录后重试");
        }
        catch (TimeoutException ex)
        {
            // 15 秒内没连上管道：通常是助手没起来（UAC 未确认 / 被拦截），归为 PLT-5007 而不是协议错误。
            StatusText = "无法连接提权助手";
            Log("Error", $"命令 {request.Command} 连接超时：{ex.Message}");
            return HelperResponse.Fail(ErrorCodes.PlatformHelperUnavailable,
                $"无法连接提权助手（{ConnectTimeoutMs / 1000} 秒内未就绪）：{ex.Message}");
        }
        catch (Exception ex)
        {
            StatusText = $"与提权助手通信失败：{ex.Message}";
            Log("Error", $"命令 {request.Command} 失败：{ex}");
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol,
                $"与提权助手通信失败：{ex.Message}");
        }
    }

    /// <summary>尽力让助手退出（助手在空闲超时后也会自行退出，这里是"现在就走"）。</summary>
    /// <remarks>
    /// 审计 F-03：若助手仍持有未确认的回滚倒计时，<b>不能</b>通知它退出——那样等于把"主程序退出后
    /// 仍能自动恢复网卡"的安全网拆掉。此时只释放本地进程句柄，助手会继续驻留到确认或到期回滚。
    /// </remarks>
    public void Dispose()
    {
        if (_pipeName is null) return;

        try
        {
            if (IsRollbackArmed())
            {
                Log("Warning", "安全恢复倒计时仍武装：不通知助手退出，助手将保持运行直到确认或到期回滚");
            }
            else
            {
                // 短超时：关窗时不该被助手拖住。
                Send(new HelperRequest { Command = HelperCommands.Shutdown }, 3_000);
            }
        }
        catch (Exception ex)
        {
            Log("Warning", $"通知助手退出失败（忽略）：{ex.Message}");
        }
        finally
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    // 不 Kill：助手自己会退出；只是不再等待。
                    Log("Information", "已请求提权助手退出");
                }
            }
            catch (Exception)
            {
                // 进程状态查询失败无所谓，不影响退出流程。
            }

            _process?.Dispose();
            _process = null;
            _pipeName = null;
        }
    }

    /// <summary>
    /// 查询助手是否仍持有未确认的回滚倒计时。查询失败时<b>保守地</b>当作"已武装"：
    /// 误关助手会直接失去自动回滚，而多留一个空闲进程只是几 MB 内存（审计 F-03）。
    /// </summary>
    private bool IsRollbackArmed()
    {
        var status = Send(new HelperRequest { Command = HelperCommands.Status }, 3_000);
        if (!status.IsSuccess)
        {
            Log("Warning", $"无法确认助手倒计时状态（{status.ToUserText()}），保守处理：不通知助手退出");
            return true;
        }

        return status.RollbackArmed;
    }

    // -----------------------------------------------------------------------------------------
    // 内部：拉起助手、握手
    // -----------------------------------------------------------------------------------------

    private bool StartHelper()
    {
        if (!IsHelperPresent)
        {
            StatusText = $"未找到助手：{ExpectedPath}";
            return false;
        }

        _pipeName = HelperProtocol.CreatePipeName();
        StatusText = "正在请求管理员权限启动提权助手…";
        Log("Information", $"拉起提权助手：{ExpectedPath} --pipe {_pipeName} --idle-min {_idleExitMinutes}");

        var launch = _launcher.Launch(ExpectedPath, _pipeName, _idleExitMinutes);
        if (launch.ElevationDenied)
        {
            // 记住被拒状态：后续 Send 直接返回 PLT-5007，不再弹窗（规划 12.5）。
            _elevationDenied = true;
            _pipeName = null;
            StatusText = "用户取消了提权";
            Log("Warning", "用户取消了 UAC 提权");
            return false;
        }

        if (launch.Process is null)
        {
            _pipeName = null;
            StatusText = launch.Error ?? "提权助手未能启动";
            Log("Error", $"提权助手未启动：{StatusText}");
            return false;
        }

        _process = launch.Process;
        StatusText = "提权助手已启动，正在连接…";
        return true;
    }

    /// <summary>一条连接 = 一条请求 + 一条响应；每次调用都新建连接（规划 12.5）。</summary>
    private HelperResponse Exchange(HelperRequest request, int timeoutMs)
    {
        using var pipe = new NamedPipeClientStream(".", HelperProtocol.PipeName(_pipeName!),
            PipeDirection.InOut, PipeOptions.Asynchronous);

        // UAC 弹窗期间助手还没开始监听，这里会等；15 秒等不到就认为助手没起来。
        // 取较短的超时（例如 Dispose 里的 shutdown 只给 3 秒）时按调用方的超时等，避免关窗被拖住。
        pipe.Connect(Math.Clamp(timeoutMs, 1_000, ConnectTimeoutMs));
        Log("Information", $"已连接提权助手，发送命令 {request.Command}");

        HelperProtocol.WriteMessage(pipe, HelperProtocol.Serialize(request));

        var json = ReadMessageWithTimeout(pipe, timeoutMs);
        if (json is null)
        {
            StatusText = "提权助手返回了空响应";
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol,
                "提权助手返回了空响应或协议数据不完整，请重试");
        }

        var response = HelperProtocol.ParseResponse(json);
        if (response is null)
        {
            StatusText = "提权助手返回的响应无法解析";
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol,
                "提权助手返回的响应无法解析（协议版本可能不匹配）");
        }

        StatusText = response.ToUserText();
        Log(response.IsSuccess ? "Information" : "Warning", $"助手响应：{StatusText}");
        return response;
    }

    /// <summary>
    /// 带超时的消息读取。<see cref="HelperProtocol.ReadMessage"/> 是同步阻塞的，
    /// 这里用可取消的异步读取自己做一遍，保证助手卡住时能按超时返回而不是永久挂起。
    /// 帧格式与 <see cref="HelperProtocol"/> 完全一致：4 字节小端长度 + UTF-8 JSON。
    /// </summary>
    private static string? ReadMessageWithTimeout(Stream stream, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(Math.Max(timeoutMs, 1_000));

        var header = new byte[4];
        if (!ReadExact(stream, header, cts.Token)) return null;

        int length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > HelperProtocol.MaxMessageBytes) return null;

        var payload = new byte[length];
        if (!ReadExact(stream, payload, cts.Token)) return null;

        return Encoding.UTF8.GetString(payload);
    }

    private static bool ReadExact(Stream stream, byte[] buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.ReadAsync(buffer.AsMemory(offset), token).AsTask().GetAwaiter().GetResult();
            if (read <= 0) return false;
            offset += read;
        }

        return true;
    }

    private void Log(string level, string message) => _log?.Invoke(level, "NetConfigHelper", message);
}

/// <summary>
/// 用 <c>runas</c> 动词拉起提权助手（会弹出 UAC 对话框）。
/// 用户拒绝时的错误码是 <c>ERROR_CANCELLED = 1223</c>，必须换成"明确的用户取消"，
/// 不能当成普通故障（规划 12.5：UAC 拒绝返回明确错误，不影响其他功能）。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RunAsHelperLauncher : IHelperLauncher
{
    /// <summary>ERROR_CANCELLED：用户在 UAC 对话框上点了"否"。</summary>
    public const int ErrorCancelled = 1223;

    private readonly Action<string, string, string?>? _log;

    /// <summary>创建启动器。<paramref name="log"/> 为可选日志回调。</summary>
    public RunAsHelperLauncher(Action<string, string, string?>? log = null)
    {
        _log = log;
    }

    /// <inheritdoc />
    public HelperLaunchResult Launch(string helperPath, string pipeName, int idleExitMinutes)
    {
        if (string.IsNullOrWhiteSpace(helperPath))
        {
            return new HelperLaunchResult(null, false, "未指定提权助手路径");
        }

        if (!File.Exists(helperPath))
        {
            return new HelperLaunchResult(null, false, $"找不到提权助手：{helperPath}");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            // 审计 F-02：必须把"预期调用方进程"告诉助手，否则助手无法区分本程序与同机其他进程。
            // 这里就是本进程（客户端）的 PID：助手的管道校验只放行这个 PID。
            Arguments = $"--pipe {pipeName} --parent {Environment.ProcessId} --idle-min {idleExitMinutes}",
            // UseShellExecute + runas 是唯一能触发 UAC 提权的托管方式。
            Verb = "runas",
            UseShellExecute = true
        };

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
            {
                return new HelperLaunchResult(null, false, "提权助手未能启动（Process.Start 返回 null）");
            }

            _log?.Invoke("Information", "NetConfigHelper", $"提权助手已启动，PID={process.Id}");
            return new HelperLaunchResult(process, false, null);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _log?.Invoke("Warning", "NetConfigHelper", "用户在 UAC 对话框中取消了提权");
            return new HelperLaunchResult(null, true, "用户取消了 UAC 提权");
        }
        catch (Win32Exception ex)
        {
            _log?.Invoke("Error", "NetConfigHelper", $"拉起提权助手失败：{ex.Message}（{ex.NativeErrorCode}）");
            return new HelperLaunchResult(null, false,
                $"拉起提权助手失败：{ex.Message}（Win32 错误码 {ex.NativeErrorCode}）");
        }
        catch (Exception ex)
        {
            _log?.Invoke("Error", "NetConfigHelper", $"拉起提权助手异常：{ex}");
            return new HelperLaunchResult(null, false, $"拉起提权助手异常：{ex.Message}");
        }
    }
}
