using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Comm.Core.Errors;
using Comm.Platform.Windows;
using Microsoft.Win32.SafeHandles;

namespace Comm.NetConfigHelper;

/// <summary>助手服务端选项（规划 12.5）。</summary>
public sealed record HelperServerOptions
{
    /// <summary>管道名（必填）。由主程序用 <see cref="HelperProtocol.CreatePipeName"/> 生成，经 <c>--pipe</c> 传给助手。</summary>
    public string PipeName { get; init; } = string.Empty;

    /// <summary>
    /// 父进程 ID（可选）。有值时校验命名管道调用方的进程标识，防止其他进程连上管道下发命令（规划 12.5）。
    /// 注意：它<b>不</b>用于监视父进程存活——倒计时武装期间父进程退出也必须继续持有（见 12.5）。
    /// </summary>
    public int? ParentProcessId { get; init; }

    /// <summary>空闲多少分钟后自动退出（默认 5 分钟，规划 12.5）。倒计时武装期间不退出。</summary>
    public int IdleExitMinutes { get; init; } = 5;

    /// <summary>处理多少条请求后退出（0 = 不限）。</summary>
    public int MaxRequests { get; init; }

    /// <summary>处理一条请求后即退出（测试用，等价于 <see cref="MaxRequests"/> = 1）。</summary>
    public bool SingleRequest { get; init; }

    /// <summary>可注入时间源（测试用固定/手动时钟）。</summary>
    public ISystemClock Clock { get; init; } = SystemClock.Instance;

    /// <summary>测试用：覆盖空闲退出时长（默认按 <see cref="IdleExitMinutes"/> 计算）。</summary>
    public TimeSpan? IdleExitTimeoutOverride { get; init; }

    /// <summary>是否校验调用方进程标识（仅在 <see cref="ParentProcessId"/> 有值时生效）。</summary>
    public bool VerifyClientProcess { get; init; } = true;

    /// <summary>
    /// 生产模式：必须提供 <see cref="ParentProcessId"/>，否则拒绝一切请求（审计 F-02 的 fail closed）。
    /// 单元测试与自检不设置本项。
    /// </summary>
    public bool RequireParentProcessId { get; init; }
}

/// <summary>add-aux / remove-aux 的负载（规划 4.8.1、R-13）：形如 <c>{"address":"192.168.1.50","prefixLength":24}</c>。</summary>
public sealed record AuxiliaryAddressPayload(string Address, int PrefixLength = 24);

/// <summary>
/// 提权助手服务端（规划 AD-08、12.5、15.8）。
/// <para>
/// 协议：单条命名管道（名字由 <see cref="HelperProtocol.CreatePipeName"/> 生成），一条连接 = 一条请求 + 一条响应。
/// 安全边界由<b>固定命令白名单</b>决定：<see cref="HelperCommands"/> 之外的任何命令一律拒绝、记入
/// <see cref="RejectedCommands"/>，并且绝不执行 shell / 子进程。异常一律转成 PLT-5004 / PLT-5008 响应，
/// 堆栈只写日志、不外传。
/// </para>
/// <para>
/// 倒计时（<see cref="Countdown"/>）由 1 秒定时器推进；倒计时武装期间即使主程序退出、空闲超时，助手也继续持有，
/// 直到用户确认或到期回滚（规划 4.8.3、12.5、D-6）。
/// </para>
/// 后端通过构造参数注入，测试使用内存假后端；生产代码只经由 <see cref="AdapterBackendFactory.Create"/> 取得后端。
/// </summary>
public sealed class HelperServer : IDisposable
{
    private const int MaxRejectedCommandLength = 120;

    private readonly INetworkAdapterBackend _backend;
    private readonly HelperServerOptions _options;
    private readonly HelperLog _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _sync = new();
    private readonly List<string> _rejected = new();
    private readonly TimeSpan _idleTimeout;
    private readonly TimeSpan _tickInterval;

    private Task? _runTask;
    private int _requestCount;
    private long _lastActivityUtcTicks;
    private volatile bool _exitRequested;
    private int _disposed;

    public HelperServer(INetworkAdapterBackend backend, HelperServerOptions options, HelperLog log)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(options);
        _backend = backend;
        _options = options;
        _log = log ?? HelperLog.Null;

        if (string.IsNullOrWhiteSpace(options.PipeName))
        {
            throw new ArgumentException("助手必须指定命名管道名（--pipe <名称>）", nameof(options));
        }

        // 空闲退出时长：默认 5 分钟；测试可覆盖（分钟级精度不便测试，因此允许直接注入 TimeSpan）。
        _idleTimeout = options.IdleExitTimeoutOverride ?? TimeSpan.FromMinutes(Math.Max(1, options.IdleExitMinutes));

        // 定时器周期：正常 1 秒（规划 12.5 的 1 s 倒计时 Tick）；测试把空闲时长压到毫秒级时自动缩小，
        // 保证空闲检测与倒计时都能在用例时限内触发。
        var intervalMs = Math.Clamp(_idleTimeout.TotalMilliseconds / 4, 25, 1000);
        _tickInterval = TimeSpan.FromMilliseconds(intervalMs);

        Countdown = new RollbackCountdown(options.Clock);
        _lastActivityUtcTicks = options.Clock.UtcNow.UtcTicks;
    }

    /// <summary>安全恢复倒计时（规划 4.8.3、D-6）。</summary>
    public RollbackCountdown Countdown { get; }

    /// <summary>已接受的连接数（= 已处理的请求数，含被拒绝的命令）。</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>被白名单拒绝的命令原文（快照，供界面/日志展示与测试断言）。</summary>
    public IReadOnlyList<string> RejectedCommands
    {
        get { lock (_sync) return _rejected.ToArray(); }
    }

    /// <summary>在后台启动服务循环（测试与"不阻塞调用方"的场景使用）。</summary>
    public void Start()
    {
        lock (_sync)
        {
            _runTask ??= Task.Run(() => RunAsync(_stop.Token));
        }
    }

    /// <summary>
    /// 服务循环：逐个接受命名管道连接，每条连接处理一条请求。<paramref name="ct"/> 取消或
    /// <see cref="Stop"/> / 空闲退出 / 达到请求上限时返回。
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        var token = linked.Token;

        _log.Info(
            $"助手服务启动：管道 {_options.PipeName}；父进程 {(_options.ParentProcessId?.ToString() ?? "未指定")}；" +
            $"空闲退出 {_idleTimeout.TotalMinutes:0.###} 分钟；请求上限 {(_options.MaxRequests == 0 ? "不限" : _options.MaxRequests.ToString())}" +
            (_options.SingleRequest ? "；单请求模式" : string.Empty),
            "12.5");

        using var timer = new Timer(_ => OnTimer(), null, _tickInterval, _tickInterval);

        try
        {
            while (!_exitRequested && !token.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = HelperPipeSecurity.CreateServer(HelperProtocol.PipeName(_options.PipeName),
                        _options.ParentProcessId, out var aclNote);
                    if (aclNote is not null) _log.Warning(aclNote, "12.5");

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    server?.Dispose();
                    break;
                }
                catch (Exception ex)
                {
                    // 管道创建/等待失败（例如同名管道已被占用）不能让助手退出：记日志后短暂退避重试。
                    _log.Warning($"等待管道连接失败：{ex.GetType().Name}: {ex.Message}", "12.5");
                    server?.Dispose();
                    try
                    {
                        await Task.Delay(200, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                using (server)
                {
                    // 每条连接 = 一条请求 + 一条响应（同步处理，天然串行，避免并发写网卡）。
                    TouchActivity();
                    await HandleConnectionAsync(server).ConfigureAwait(false);
                }

                if (ShouldExitAfterRequest()) break;
            }
        }
        catch (Exception ex)
        {
            _log.Error("助手服务循环异常终止", "12.5", ex);
        }
        finally
        {
            _log.Info($"助手服务停止：共处理 {RequestCount} 条请求" +
                      (Countdown.IsArmed ? $"；注意：回滚倒计时仍然武装（剩余 {Countdown.RemainingSeconds} 秒）" : string.Empty),
                "12.5");
        }
    }

    /// <summary>请求退出（取消等待中的连接）。</summary>
    public void Stop()
    {
        _exitRequested = true;
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        Task? runTask;
        lock (_sync) runTask = _runTask;
        try
        {
            runTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Stop();
        _stop.Dispose();
    }

    // -----------------------------------------------------------------------------------------
    // 连接处理与命令分发
    // -----------------------------------------------------------------------------------------

    private async Task HandleConnectionAsync(NamedPipeServerStream server)
    {
        Interlocked.Increment(ref _requestCount);

        HelperResponse response;
        bool shutdownRequested = false;

        if (!TryVerifyClientProcess(server, out var denial))
        {
            // 关键：即使要拒绝，也必须先把对端的请求读掉。
            // 在"入站数据未读取"的命名管道上直接同步写响应，会在 Windows 上互等到超时
            // （客户端阻塞在读、服务端阻塞在写）——这是实测出来的坑，不是理论担心。
            // 读取有 2 秒上限：对"连上但不发数据"的探测进程不会无限占用服务循环。
            await DrainRequestAsync(server, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            response = denial!;
        }
        else
        {
            response = ProcessRequest(server, out shutdownRequested);
        }

        try
        {
            HelperProtocol.WriteMessage(server, HelperProtocol.Serialize(response));
        }
        catch (Exception ex)
        {
            _log.Warning($"回写响应失败（客户端可能已断开）：{ex.GetType().Name}: {ex.Message}", "12.5");
        }

        if (shutdownRequested)
        {
            _log.Info("收到 shutdown 命令，准备退出", "12.5");
            RequestExit();
        }
    }

    /// <summary>丢弃一条入站请求（有上限），返回是否在超时前读到。</summary>
    private static async Task<bool> DrainRequestAsync(Stream stream, TimeSpan timeout)
    {
        var read = Task.Run(() =>
        {
            try
            {
                HelperProtocol.ReadMessage(stream);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        });

        var finished = await Task.WhenAny(read, Task.Delay(timeout)).ConfigureAwait(false);
        if (finished != read)
        {
            // 超时：不再等待，管道随后释放会让后台读取自然结束。
            return false;
        }

        return read.Result;
    }

    private HelperResponse ProcessRequest(NamedPipeServerStream server, out bool shutdownRequested)
    {
        shutdownRequested = false;

        string? json;
        try
        {
            json = HelperProtocol.ReadMessage(server);
        }
        catch (Exception ex)
        {
            _log.Warning($"读取请求失败：{ex.GetType().Name}: {ex.Message}", "12.5");
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, "请求读取失败（管道中断或长度非法）");
        }

        if (json is null)
        {
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, "请求为空或长度非法（期望 4 字节长度前缀 + UTF-8 JSON）");
        }

        var request = HelperProtocol.ParseRequest(json);
        if (request is null)
        {
            _log.Warning("收到无法解析的请求 JSON，已按 PLT-5008 拒绝", "12.5");
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, "请求不是合法的 JSON（HelperRequest）");
        }

        if (!HelperCommands.IsKnown(request.Command))
        {
            // 安全边界：未知命令一律拒绝，绝不执行 shell / 子进程（规划 12.5）。
            RecordRejected(request.Command);
            _log.Warning($"拒绝未知命令：{(string.IsNullOrEmpty(request.Command) ? "（空）" : request.Command)}", "12.5");
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol,
                $"命令不被接受：\"{request.Command}\"；助手只接受 {string.Join(" / ", HelperCommands.All)}");
        }

        try
        {
            switch (request.Command)
            {
                case HelperCommands.Snapshot:
                    return WithCountdown(HandleSnapshot());

                case HelperCommands.Apply:
                    return WithCountdown(HandleApply(request));

                case HelperCommands.Restore:
                    return WithCountdown(HandleRestore(request));

                case HelperCommands.AddAuxiliary:
                    return WithCountdown(HandleAuxiliary(request, add: true));

                case HelperCommands.RemoveAuxiliary:
                    return WithCountdown(HandleAuxiliary(request, add: false));

                case HelperCommands.Confirm:
                    Countdown.Confirm();
                    TouchActivity(); // 确认后重新开始计算空闲时长
                    _log.Info("用户确认保留当前配置，已解除安全恢复倒计时", "4.8.3");
                    return WithCountdown(HelperResponse.Ok(null, "已确认保留当前配置"));

                case HelperCommands.Status:
                    // 只读：不触碰网卡、不改倒计时状态（规划 4.8.3）。
                    return WithCountdown(HelperResponse.Ok(null, Countdown.IsArmed
                        ? $"安全恢复倒计时进行中，剩余 {Countdown.RemainingSeconds} 秒"
                        : "无未确认的安全恢复倒计时"));

                case HelperCommands.Shutdown:
                    // 审计 F-03：武装期间绝不允许退出——否则"主程序崩溃也能自动恢复"的安全网会被一条
                    // shutdown 命令（或主程序关窗）直接拆掉。到期或用户确认后才允许退出。
                    if (Countdown.IsArmed)
                    {
                        _log.Warning(
                            $"拒绝 shutdown：安全恢复倒计时仍武装（剩余 {Countdown.RemainingSeconds} 秒），助手继续运行",
                            "4.8.3");
                        return WithCountdown(HelperResponse.Fail(ErrorCodes.PlatformNetConfigPendingRollback,
                            $"安全恢复倒计时仍武装（剩余 {Countdown.RemainingSeconds} 秒），助手必须继续运行；" +
                            "请先在主程序点击“保留此配置”，或等待到期自动回滚"));
                    }

                    shutdownRequested = true;
                    return WithCountdown(HelperResponse.Ok(null, "助手即将退出"));

                default:
                    // HelperCommands.IsKnown 已放行，理论上不会到这里；保守起见仍然拒绝。
                    RecordRejected(request.Command);
                    return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, $"命令不被接受：\"{request.Command}\"");
            }
        }
        catch (Exception ex)
        {
            // 后端异常不能让助手崩掉：堆栈只写日志（规划 15.9），响应只带错误码与摘要。
            _log.Error($"执行命令 {request.Command} 时发生未预期异常（适配器 {request.AdapterId ?? "-"}）", "12.5", ex);
            return HelperResponse.Fail(ErrorCodes.PlatformNetConfigApplyFailed,
                $"助手内部错误：{ex.GetType().Name}（详情见助手日志）");
        }
    }

    private HelperResponse HandleSnapshot()
    {
        var adapters = _backend.ReadAdapters();
        var payload = JsonSerializer.Serialize(adapters, HelperJson.Options);
        _log.Info($"读取网卡快照：{adapters.Count} 个适配器", "15.8");
        return HelperResponse.Ok(payload, $"共 {adapters.Count} 个网卡");
    }

    private HelperResponse HandleApply(HelperRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.AdapterId))
        {
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, "apply 请求缺少 adapterId");
        }

        var config = DeserializePayload<NetworkConfigRequest>(request.Payload);
        if (config is null)
        {
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol,
                "apply 的 payload 不是合法的 NetworkConfigRequest JSON");
        }

        var adapterId = request.AdapterId!;

        // 倒计时需要"修改前"的快照：由助手自己在改动前读取，保证即使主程序随后崩溃也能回滚（规划 12.5、15.8 第 1 步）。
        AdapterSnapshot? before = null;
        if (config.AutoRollbackSeconds > 0)
        {
            before = _backend.ReadAdapter(adapterId);
            if (before is null)
            {
                _log.Warning($"找不到网卡 {adapterId}，未执行任何修改", "15.8");
                return HelperResponse.Fail(ErrorCodes.PlatformAdapterNotFound,
                    $"找不到网卡 {adapterId}（可能已被拔出或禁用），未做任何修改");
            }
        }

        var result = _backend.Apply(adapterId, config);
        if (!result.IsSuccess)
        {
            _log.Warning($"应用网卡配置失败（{adapterId}）：{result.ToUserText()}" +
                         (result.RolledBack ? "；后端已自动回滚" : string.Empty), "15.8");
            return HelperResponse.Fail(result.ErrorCode ?? ErrorCodes.PlatformNetConfigApplyFailed,
                result.Message ?? "应用网卡配置失败");
        }

        _log.Info($"已应用网卡配置（{adapterId}）：{(config.DhcpEnabled ? "DHCP" : $"{config.Address}/{config.PrefixLength}")}" +
                  $"；回读 {(result.Readback?.SummaryText ?? "不可用")}", "15.8");

        if (config.AutoRollbackSeconds > 0 && before is not null)
        {
            var snapshot = before;
            Countdown.Arm(config.AutoRollbackSeconds,
                () => _backend.Restore(adapterId, snapshot),
                $"自动回滚到修改前配置（{snapshot.SummaryText}）");
            _log.Info($"已武装安全恢复倒计时 {config.AutoRollbackSeconds} 秒（未确认将回滚到 {snapshot.SummaryText}）", "4.8.3");
        }

        var payload = result.Readback is null
            ? null
            : JsonSerializer.Serialize(result.Readback, HelperJson.Options);

        return HelperResponse.Ok(payload, result.ToUserText());
    }

    private HelperResponse HandleRestore(HelperRequest request)
    {
        var snapshot = DeserializePayload<AdapterSnapshot>(request.Payload);
        if (snapshot is null)
        {
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol,
                "restore 的 payload 不是合法的 AdapterSnapshot JSON");
        }

        var adapterId = string.IsNullOrWhiteSpace(request.AdapterId) ? snapshot.Id : request.AdapterId!;
        if (string.IsNullOrWhiteSpace(adapterId))
        {
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol, "restore 请求缺少 adapterId");
        }

        var result = _backend.Restore(adapterId, snapshot);
        if (!result.IsSuccess)
        {
            _log.Warning($"按快照回滚失败（{adapterId}）：{result.ToUserText()}", "15.8");
            return HelperResponse.Fail(result.ErrorCode ?? ErrorCodes.PlatformNetConfigRollbackFailed,
                result.Message ?? "按快照回滚失败");
        }

        _log.Info($"已按快照回滚（{adapterId}）：{result.Readback?.SummaryText ?? snapshot.SummaryText}", "15.8");

        // 手工回滚成功即等于"配置已恢复"，此时再保留倒计时只会重复回滚同一份快照，因此一并解除。
        if (Countdown.IsArmed)
        {
            Countdown.Confirm();
            _log.Info("手工回滚成功，已解除安全恢复倒计时", "4.8.3");
        }

        var payload = result.Readback is null ? null : JsonSerializer.Serialize(result.Readback, HelperJson.Options);
        return HelperResponse.Ok(payload, result.Message ?? "已恢复到快照配置");
    }

    private HelperResponse HandleAuxiliary(HelperRequest request, bool add)
    {
        if (string.IsNullOrWhiteSpace(request.AdapterId))
        {
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol,
                $"{(add ? HelperCommands.AddAuxiliary : HelperCommands.RemoveAuxiliary)} 请求缺少 adapterId");
        }

        var payload = DeserializePayload<AuxiliaryAddressPayload>(request.Payload);
        if (payload is null || string.IsNullOrWhiteSpace(payload.Address))
        {
            return HelperResponse.Fail(ErrorCodes.PlatformHelperProtocol,
                "payload 需形如 {\"address\":\"192.168.1.50\",\"prefixLength\":24}");
        }

        var adapterId = request.AdapterId!;

        if (!add)
        {
            // 删除辅助 IP 只用地址（掩码无关，见契约 INetworkAdapterBackend.RemoveAuxiliaryAddress）。
            var removed = _backend.RemoveAuxiliaryAddress(adapterId, payload.Address);
            if (!removed.IsSuccess)
            {
                _log.Warning($"移除辅助 IP 失败（{adapterId} {payload.Address}）：{removed.ToUserText()}", "4.8.1");
                return HelperResponse.Fail(removed.ErrorCode ?? ErrorCodes.PlatformNetConfigApplyFailed,
                    removed.Message ?? "移除辅助 IP 失败");
            }

            _log.Info($"已移除辅助 IP（{adapterId} {payload.Address}）", "4.8.1");
            var removedPayload = removed.Readback is null ? null : JsonSerializer.Serialize(removed.Readback, HelperJson.Options);
            return HelperResponse.Ok(removedPayload, removed.Message ?? $"已移除辅助 IP {payload.Address}");
        }

        var prefix = NetworkConfigValidator.NormalizePrefix(payload.PrefixLength);
        if (!prefix.IsSuccess)
        {
            return HelperResponse.Fail(prefix.Error!.Code, prefix.Error!.Message);
        }

        var result = _backend.AddAuxiliaryAddress(adapterId, payload.Address, prefix.Value);
        if (!result.IsSuccess)
        {
            _log.Warning($"添加辅助 IP 失败（{adapterId} {payload.Address}/{prefix.Value}）：{result.ToUserText()}", "4.8.1");
            return HelperResponse.Fail(result.ErrorCode ?? ErrorCodes.PlatformNetConfigApplyFailed,
                result.Message ?? "添加辅助 IP 失败");
        }

        _log.Info($"已添加辅助 IP（{adapterId} {payload.Address}/{prefix.Value}）", "4.8.1");
        var addedPayload = result.Readback is null ? null : JsonSerializer.Serialize(result.Readback, HelperJson.Options);
        return HelperResponse.Ok(addedPayload, result.Message ?? $"已添加辅助 IP {payload.Address}/{prefix.Value}");
    }

    private static T? DeserializePayload<T>(string? payload) where T : class
    {
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(payload, HelperJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把倒计时状态贴到响应上（统一出口，避免各分支漏填）。</summary>
    private HelperResponse WithCountdown(HelperResponse response) => response with
    {
        RollbackArmed = Countdown.IsArmed,
        RollbackSecondsRemaining = Countdown.RemainingSeconds
    };

    private void RecordRejected(string? command)
    {
        var text = command ?? string.Empty;
        if (text.Length > MaxRejectedCommandLength) text = text[..MaxRejectedCommandLength] + "…";
        lock (_sync) _rejected.Add(text);
    }

    private bool ShouldExitAfterRequest()
    {
        if (_exitRequested) return true;

        // 审计 F-03：倒计时武装期间就算达到单请求/请求上限也不退出，避免安全网随进程一起消失。
        if (Countdown.IsArmed) return false;

        if (_options.SingleRequest) return true;
        return _options.MaxRequests > 0 && RequestCount >= _options.MaxRequests;
    }

    private void TouchActivity() =>
        Interlocked.Exchange(ref _lastActivityUtcTicks, _options.Clock.UtcNow.UtcTicks);

    private void RequestExit()
    {
        _exitRequested = true;
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// 定时器：推进倒计时（1 秒一次，规划 12.5）并判断空闲退出。
    /// <b>倒计时武装期间绝不退出</b>——这是"主程序崩溃后仍能恢复"的关键（规划 4.8.3、12.5）。
    /// </summary>
    private void OnTimer()
    {
        try
        {
            if (Countdown.Tick())
            {
                var error = Countdown.LastError;
                if (error is null)
                {
                    _log.Info("安全恢复倒计时到期，已自动回滚到修改前配置", "15.8");
                }
                else
                {
                    _log.Error($"安全恢复倒计时到期，但自动回滚失败：{error}；请按快照人工恢复", "15.8");
                }
            }

            if (_exitRequested || _options.SingleRequest) return;
            if (Countdown.IsArmed) return;

            var nowTicks = _options.Clock.UtcNow.UtcTicks;
            var idleTicks = nowTicks - Interlocked.Read(ref _lastActivityUtcTicks);
            if (idleTicks < _idleTimeout.Ticks) return;

            _log.Info($"空闲超过 {_idleTimeout.TotalMinutes:0.###} 分钟且无未确认倒计时，助手自动退出", "12.5");
            RequestExit();
        }
        catch (Exception ex)
        {
            _log.Warning($"助手定时器异常（不影响已武装的倒计时）：{ex.GetType().Name}: {ex.Message}", "12.5");
        }
    }

    // -----------------------------------------------------------------------------------------
    // 调用方进程标识校验（规划 12.5：管道名带随机后缀，并校验调用方进程标识）
    // -----------------------------------------------------------------------------------------

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>
    /// 校验命名管道调用方进程。返回 false 时 <paramref name="denial"/> 为要回给客户端的拒绝响应。
    /// <para>
    /// 审计 F-02 修正：原实现"取不到 PID 就放行"（fail open），等于把固定命令白名单暴露给同机任意进程。
    /// 现在改为 <b>fail closed</b>：
    /// <list type="bullet">
    /// <item>生产入口（<see cref="HelperServerOptions.RequireParentProcessId"/> = true）没有父进程标识时拒绝一切请求；</item>
    /// <item>拿到父进程标识后，取不到调用方 PID 一律拒绝，不再"放行本次请求"；</item>
    /// <item>只有显式关闭 <see cref="HelperServerOptions.VerifyClientProcess"/>（单元测试）才跳过校验。</item>
    /// </list>
    /// </para>
    /// </summary>
    private bool TryVerifyClientProcess(NamedPipeServerStream server, out HelperResponse? denial)
    {
        denial = null;

        if (!_options.VerifyClientProcess) return true;

        var expected = _options.ParentProcessId;
        if (expected is null)
        {
            if (_options.RequireParentProcessId)
            {
                _log.Error("拒绝连接：助手启动时未收到 --parent，已按安全策略拒绝所有请求", "12.5");
                denial = HelperResponse.Fail(ErrorCodes.PlatformPermission,
                    "助手未收到预期父进程标识（--parent），已拒绝本次请求；请由主程序拉起提权助手");
                return false;
            }

            // 兼容：单请求模式/单元测试/自检等未指定父进程的场景。
            return true;
        }

        uint clientPid;
        try
        {
            if (!GetNamedPipeClientProcessId(server.SafePipeHandle, out clientPid))
            {
                var code = Marshal.GetLastWin32Error();
                _log.Error($"无法获取管道调用方进程标识（Win32 错误 {code}），按安全策略拒绝本次请求", "12.5");
                denial = HelperResponse.Fail(ErrorCodes.PlatformPermission,
                    $"无法校验调用方进程标识（Win32 错误 {code}），已拒绝本次请求");
                return false;
            }
        }
        catch (Exception ex)
        {
            _log.Error($"校验调用方进程标识失败：{ex.GetType().Name}: {ex.Message}；按安全策略拒绝本次请求", "12.5");
            denial = HelperResponse.Fail(ErrorCodes.PlatformPermission,
                "无法校验调用方进程标识，已拒绝本次请求");
            return false;
        }

        if (clientPid == (uint)expected.Value) return true;

        _log.Error($"拒绝来自进程 {clientPid} 的连接：与期望的父进程 {expected.Value} 不一致", "12.5");
        denial = HelperResponse.Fail(ErrorCodes.PlatformPermission,
            $"调用方进程 {clientPid} 与父进程 {expected.Value} 不一致，已拒绝（助手只服务拉起它的主程序）");
        return false;
    }
}
