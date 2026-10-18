using Comm.Capture;
using Comm.Core;
using Comm.Core.Errors;
using Comm.Core.Transport;
using Comm.Diagnostics;
using Comm.Network.Discovery;
using Comm.Scanning;
using Comm.Session;
using Comm.Settings;

namespace Comm.App.Wpf.Services;

/// <summary>
/// 会话注册表（M3b R-16 重构，规划 4.1.3、21.2.2）。
/// <para>
/// 每个会话是一个独立的 <see cref="SessionContext"/>：各自持有传输通道、事务层、统计、请求队列、
/// 串口重配上下文与监视投影；<see cref="FrameBus"/>、日志、设置是全局共享服务，帧记录带 SessionId，
/// 各窗口只投影自己的数据。
/// </para>
/// <para>
/// 串口资源按 <c>serial:COMx</c> 全局独占（同一 COM 口不能被两个会话打开）；TCP 会话可并行指向同一目标。
/// 会话数量上限默认 8（设置页可调），至少保留 1 个会话。
/// </para>
/// <para>
/// 兼容说明：本类仍保留了 M2 时期"单会话"形态的属性与方法（<see cref="Current"/>、<see cref="IsConnected"/>、
/// <see cref="CreateDeviceScanService"/> 等），它们**等价于当前活动会话**（<see cref="Active"/>）。
/// 新代码请直接依赖会话作用域的 <see cref="SessionContext"/>；这些转发成员只用于平滑迁移。
/// </para>
/// </summary>
public sealed class SessionManager : IAsyncDisposable, ISerialReconfigurator
{
    private readonly List<SessionContext> _sessions = new();
    private readonly FrameBus _frameBus;
    private readonly LogGate _log;
    private readonly SerialPortRegistry _ports;
    private AppSettings _settings;
    private SessionContext _active;
    private int _sessionCounter;
    private bool _disposed;

    public SessionManager(FrameBus frameBus, LogGate log, SerialPortRegistry ports, AppSettings settings)
    {
        _frameBus = frameBus;
        _log = log;
        _ports = ports;
        _settings = settings;

        // 审计 F-04：解析开关与 JSONL 记录是全局状态，集中到一个实例，注入给每个会话的监视投影。
        MonitorShared = new MonitorSharedState(frameBus, log, settings.Monitor);

        _active = CreateSessionInternal($"会话 1");
        _sessions.Add(_active);
    }

    /// <summary>按创建顺序排列的全部会话。</summary>
    public IReadOnlyList<SessionContext> Sessions => _sessions;

    /// <summary>跨会话共享的监视状态（解析开关 / 记录）；UI 上标注为"全局"。</summary>
    public MonitorSharedState MonitorShared { get; }

    /// <summary>当前活动会话（界面顶部标签页 / 底栏状态来源）。</summary>
    public SessionContext Active => _active;

    /// <summary>会话数量上限（规划 4.1.3：默认 8，设置页可调）。</summary>
    public int MaxSessions => Math.Clamp(_settings.Sessions.MaxSessions, 1, 32);

    /// <summary>会话列表或活动会话发生变化（会话标签页、状态栏刷新）。</summary>
    public event EventHandler? SessionsChanged;

    /// <summary>新增会话；达到上限时返回失败并说明原因（规划 21.2.2 第 4 点）。</summary>
    public Result<SessionContext> CreateSession(string? name = null, bool activate = true)
    {
        if (_sessions.Count >= MaxSessions)
        {
            return Result<SessionContext>.Fail(new ErrorInfo(
                ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                $"已达到会话上限（{MaxSessions} 个）。",
                Hint: "可在“设置与日志”页调高上限，或先关闭不再使用的会话。"));
        }

        var session = CreateSessionInternal(name ?? $"会话 {_sessions.Count + 1}");
        _sessions.Add(session);
        if (activate) Activate(session.SessionId);
        else SessionsChanged?.Invoke(this, EventArgs.Empty);
        _log.Info("Session", $"已创建会话「{session.Name}」（{session.SessionId}），当前会话数 {_sessions.Count}/{MaxSessions}");
        return Result<SessionContext>.Ok(session);
    }

    private SessionContext CreateSessionInternal(string name)
    {
        _sessionCounter++;
        var id = Guid.NewGuid().ToString("N")[..8];
        var session = new SessionContext(id, name, _frameBus, _log, _ports, _settings, OnSessionStateChanged,
            MonitorShared);
        _ports.RegisterName(id, session.Name);
        return session;
    }

    /// <summary>关闭会话；至少保留一个会话（最后一个会话不允许关闭，只允许断开连接）。</summary>
    public Result<string> CloseSession(string sessionId)
    {
        var session = _sessions.FirstOrDefault(s => s.SessionId == sessionId);
        if (session is null) return Result<string>.Ok("会话已不存在");

        if (_sessions.Count <= 1)
        {
            return Result<string>.Fail(new ErrorInfo(
                ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning,
                "至少需要保留一个会话。",
                Hint: "如需停止通信，请在“连接中心”断开连接；也可以先新建一个会话再关闭当前会话。"));
        }

        _sessions.Remove(session);
        _ = session.DisposeAsync();

        if (ReferenceEquals(_active, session))
        {
            _active = _sessions[0];
        }

        SessionsChanged?.Invoke(this, EventArgs.Empty);
        _log.Info("Session", $"已关闭会话「{session.Name}」（{sessionId}），当前会话数 {_sessions.Count}");
        return Result<string>.Ok($"已关闭会话「{session.Name}」");
    }

    public void Activate(string sessionId)
    {
        var session = _sessions.FirstOrDefault(s => s.SessionId == sessionId);
        if (session is null) return;

        _active = session;
        session.Touch();
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RenameSession(string sessionId, string name)
    {
        _sessions.FirstOrDefault(s => s.SessionId == sessionId)?.Rename(name);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>设置变化后同步（会话上限、监视显示选项下发到所有会话）。</summary>
    public void ApplySettings(AppSettings settings)
    {
        _settings = settings;
        foreach (var session in _sessions) session.Monitor.ApplySettings(settings.Monitor);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把监视显示选项下发给所有会话的监视投影（设置页保存时调用）。</summary>
    public void ApplyMonitorSettings(MonitorSettings settings)
    {
        foreach (var session in _sessions) session.Monitor.ApplySettings(settings);
    }

    private void OnSessionStateChanged(string sessionId, string? name) => SessionsChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>停止所有会话的监视表周期刷新（退出流程使用，见规划 13.9）。</summary>
    public void StopAllWatchLists(string reason)
    {
        foreach (var session in _sessions)
        {
            try
            {
                if (session.WatchList.IsRunning) session.WatchList.Stop(reason);
            }
            catch (Exception ex)
            {
                _log.Warning("Session", $"停止会话「{session.Name}」监视表时异常：{ex.Message}");
            }
        }
    }

    // ------------------------------------------------------------------------------------------
    // 兼容转发（等价于 Active.*）。新代码请直接用 SessionContext，见类注释。
    // ------------------------------------------------------------------------------------------

    public ModbusSession? Current => _active.Current;
    public ConnectionKind Kind => _active.Kind;
    public bool IsConnected => _active.IsConnected;
    public bool IsSerialConnected => _active.IsSerialConnected;
    public bool IsTcpConnected => _active.IsTcpConnected;
    public ConnectionSnapshot? Snapshot => _active.Snapshot;
    public int SilenceThresholdMs => _active.SilenceThresholdMs;
    public string StatusText => _active.StatusText;

    public byte DefaultUnitId
    {
        get => _active.DefaultUnitId;
        set => _active.DefaultUnitId = value;
    }

    public event EventHandler? CurrentChanged
    {
        add => _active.Changed += value;
        remove => _active.Changed -= value;
    }

    public Task<Result<string>> ConnectSerialAsync(SerialProfile profile, int silenceThresholdMs,
        CancellationToken cancellationToken = default) =>
        _active.ConnectSerialAsync(profile, silenceThresholdMs, cancellationToken);

    public Task<Result<string>> ConnectTcpAsync(TcpProfile profile, CancellationToken cancellationToken = default) =>
        _active.ConnectTcpAsync(profile, cancellationToken);

    public Task DisconnectAsync() => _active.DisconnectAsync();

    public void SetDefaultUnitId(byte unitId) => _active.SetDefaultUnitId(unitId);

    public bool CanReconfigure => _active.CanReconfigure;
    public SerialSettings? OriginalSettings => _active.OriginalSettings;
    public SerialSettings? ActiveSettings => _active.ActiveSettings;

    public Task<ModbusSession?> ApplyAsync(SerialSettings settings, int settleMs, CancellationToken cancellationToken) =>
        _active.ApplyAsync(settings, settleMs, cancellationToken);

    public Task<SerialRestoreResult> RestoreAsync(CancellationToken cancellationToken) =>
        _active.RestoreAsync(cancellationToken);

    public DeviceScanService? CreateDeviceScanService() => _active.CreateDeviceScanService();
    public DataScanService? CreateDataScanService() => _active.CreateDataScanService();
    public RegisterLocator? CreateRegisterLocator() => _active.CreateRegisterLocator();
    public WatchTableService? CreateWatchTableService() => _active.CreateWatchTableService();
    public ManualSendService? CreateManualSendService() => _active.CreateManualSendService();

    public ManualSendService CreateManualSendService(FrameTransport transport, int silenceThresholdMs = 5) =>
        _active.CreateManualSendService(transport, silenceThresholdMs);

    public ModbusIdentifyDelegate CreateLanIdentifyDelegate() => _active.CreateLanIdentifyDelegate();

    /// <summary>会话占用摘要（诊断/任务中心）。</summary>
    public string DescribeSessions() =>
        $"{_sessions.Count}/{MaxSessions} 个会话：" +
        string.Join("；", _sessions.Select(s => $"{s.Name}{(ReferenceEquals(s, _active) ? "（活动）" : string.Empty)}={s.StatusText}")) +
        $"。串口占用：{_ports.Describe()}";

    /// <summary>幂等释放：关闭全部会话（见 M1 反馈问题 5/10）。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var session in _sessions.ToArray())
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warning("Session", $"关闭会话「{session.Name}」时异常：{ex.Message}");
            }
        }

        _sessions.Clear();
    }
}
