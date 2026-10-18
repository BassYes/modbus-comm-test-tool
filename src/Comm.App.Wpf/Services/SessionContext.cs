using Comm.Capture;
using Comm.Core;
using Comm.Core.Codecs;
using Comm.Core.Errors;
using Comm.Core.Transport;
using Comm.Diagnostics;
using Comm.Network.Discovery;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Session;
using Comm.Settings;
using Comm.Transport;

namespace Comm.App.Wpf.Services;

public enum ConnectionKind
{
    None,
    Serial,
    Tcp
}

/// <summary>报文构建 / 发送使用的协议（R-19：允许显式选择，未连接时也能生成）。</summary>
public enum FrameTransport
{
    /// <summary>跟随当前会话；未连接时按 RTU 处理并在界面提示。</summary>
    FollowSession,

    Rtu,
    Tcp
}

/// <summary>已连接参数的**不可变快照**。界面底栏必须显示它，而不是仍可编辑的输入框（R-18）。</summary>
public sealed record ConnectionSnapshot(
    ConnectionKind Kind,
    string Endpoint,
    string CodecName,
    string ParametersText,
    byte UnitId)
{
    public string ShortText => Kind switch
    {
        ConnectionKind.Serial => $"已连接 {Endpoint}（{CodecName}）· {ParametersText}",
        ConnectionKind.Tcp => $"已连接 {Endpoint}（{CodecName}）· {ParametersText}",
        _ => "未连接"
    };
}

/// <summary>
/// 一个会话的全部运行时状态（M3b R-16 重构：原 <c>SessionManager</c> 的单会话逻辑搬到这里）。
/// 每个会话拥有独立的：传输通道与事务层、统计、默认 Unit ID、串口重配上下文、显示/监视投影。
/// 串口资源通过 <see cref="SerialPortRegistry"/> 全局独占（同一 COM 口不能被两个会话打开）。
/// 说明：本类只实现 <see cref="IAsyncDisposable"/>，**退出时必须异步释放**（见 M1 反馈问题 5/10），
/// 且释放是幂等的——容器释放与手工释放可能都会调用它。
/// </summary>
public sealed class SessionContext : IAsyncDisposable, ISerialReconfigurator, ISessionHandle
{
    private readonly FrameBus _frameBus;
    private readonly LogGate _log;
    private readonly SerialPortRegistry _ports;
    private readonly Action<string, string?>? _changed;

    private ModbusSession? _session;
    private SerialProfile? _serialProfile;
    private TcpProfile? _tcpProfile;

    /// <summary>参数扫描前的原始串口配置（R-11：四种终态都要恢复到它）。</summary>
    private SerialProfile? _originalSerialProfile;
    private int _originalSilenceThresholdMs = RtuTiming.MinimumSilenceThresholdMs;

    private readonly SessionStatistics _statistics = new();
    private bool _disposed;

    public SessionContext(
        string sessionId,
        string name,
        FrameBus frameBus,
        LogGate log,
        SerialPortRegistry ports,
        AppSettings settings,
        Action<string, string?>? changed = null)
    {
        SessionId = sessionId;
        Name = string.IsNullOrWhiteSpace(name) ? $"会话 {sessionId[..Math.Min(4, sessionId.Length)]}" : name;
        _frameBus = frameBus;
        _log = log;
        _ports = ports;
        _changed = changed;
        CreatedAt = DateTimeOffset.Now;
        LastUsedAt = CreatedAt;
        Monitor = new MonitorService(frameBus, log, settings, sessionId);
        WatchList = new WatchListService(this, log);
    }

    /// <summary>会话唯一标识（也是报文记录里的 <see cref="Comm.Core.Frames.FrameRecord.SessionId"/>）。</summary>
    public string SessionId { get; }

    public string Name { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastUsedAt { get; private set; }

    /// <summary>本会话的报文监视投影（只显示本会话的报文，规划 21.2.2 第 3 点）。</summary>
    public MonitorService Monitor { get; }

    /// <summary>本会话的监视表（点位与采样记录都只属于本会话）。</summary>
    public WatchListService WatchList { get; }

    public ModbusSession? Current => _session;
    public ConnectionKind Kind { get; private set; } = ConnectionKind.None;
    public bool IsConnected => _session is { IsOpen: true };
    public bool IsSerialConnected => IsConnected && Kind == ConnectionKind.Serial;
    public bool IsTcpConnected => IsConnected && Kind == ConnectionKind.Tcp;
    public byte DefaultUnitId { get; set; } = 1;

    /// <summary>RTU 静默判定下限（打开串口时使用）。</summary>
    public int SilenceThresholdMs { get; private set; } = 5;

    /// <summary>当前连接参数快照；未连接时为 null。</summary>
    public ConnectionSnapshot? Snapshot { get; private set; }

    /// <summary>累计统计。未连接时返回全零的稳定实例，便于界面绑定。</summary>
    public SessionStatistics Statistics => _session?.Statistics ?? _statistics;

    /// <summary>会话标题（窗口标题栏 / 会话标签页）。</summary>
    public string Title => IsConnected ? $"{Name} · {Snapshot?.Endpoint}" : Name;

    public string StatusText => _session switch
    {
        null => "未连接",
        { IsOpen: true } => Snapshot?.ShortText ?? $"已连接 {_session.Channel.Endpoint}（{_session.Codec.Name}）",
        _ => $"已断开 {_session.Channel.Endpoint}（{_session.Codec.Name}）"
    };

    /// <summary>会话状态变化（连接/断开/统计刷新由会话标签页与状态栏读取）。</summary>
    public event EventHandler? Changed;

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        Name = name.Trim();
        _ports.RegisterName(SessionId, Name);
        RaiseChanged();
    }

    /// <summary>标记最近使用时间（激活标签页时调用）。</summary>
    public void Touch() => LastUsedAt = DateTimeOffset.Now;

    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        _changed?.Invoke(SessionId, Name);
    }

    public async Task<Result<string>> ConnectSerialAsync(SerialProfile profile, int silenceThresholdMs,
        CancellationToken cancellationToken = default)
    {
        // 规划 21.2.2 第 4 点：同一 COM 口不能被两个会话打开（先给出可读提示，再交给系统打开）。
        if (!_ports.TryAcquire(profile.PortName, SessionId, Name, out var busy))
        {
            return Result<string>.Fail(new ErrorInfo(ErrorCodes.TransportPortBusy, ErrorCategory.Transport,
                Severity.Warning, busy ?? $"{profile.PortName} 已被占用"));
        }

        // R-11：用户主动连接时记住"原始参数"，参数扫描结束后要恢复到它。
        _originalSerialProfile = profile;
        _originalSilenceThresholdMs = silenceThresholdMs;
        var result = await OpenSerialAsync(profile, silenceThresholdMs, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            // 打开失败（端口不存在/被系统占用等）时必须释放登记，否则本会话会一直"占用"一个并未打开的端口，
            // 导致同一会话或其他会话后续都无法再次尝试（M3b 回归）。
            _ports.Release(profile.PortName, SessionId);
        }

        return result;
    }

    /// <summary>打开（或重开）串口会话。参数扫描切换参数时也走这条路径。</summary>
    private async Task<Result<string>> OpenSerialAsync(SerialProfile profile, int silenceThresholdMs,
        CancellationToken cancellationToken)
    {
        await DisconnectAsync().ConfigureAwait(false);

        // F-5：静默阈值随波特率变化。固定 5 ms 在 1200 bps（3.5 字符 ≈ 32 ms）下会误拆帧。
        int silence = RtuTiming.ComputeSilenceThresholdMs(profile.BaudRate, silenceThresholdMs);
        int effectiveTimeout = RtuTiming.EffectiveResponseTimeoutMs(profile.ResponseTimeoutMs, profile.BaudRate);

        var options = new SessionOptions
        {
            SessionId = SessionId,
            Name = Name,
            ResponseTimeoutMs = effectiveTimeout,
            RetryCount = profile.RetryCount,
            TurnaroundDelayMs = profile.TurnaroundDelayMs,
            SuppressEcho = profile.SuppressEcho,
            IsSerial = true
        };

        var channel = new SerialChannel(profile.ToSerialSettings());
        var session = new ModbusSession(channel, new ModbusRtuCodec(silence), options, _frameBus);

        try
        {
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CommException ex)
        {
            _log.Error("Session", $"串口连接失败：{ex.Error.Message}", ex);
            await session.DisposeAsync().ConfigureAwait(false);
            return Result<string>.Fail(ex.Error);
        }

        _session = session;
        _serialProfile = profile;
        _tcpProfile = null;
        SilenceThresholdMs = silence;
        Kind = ConnectionKind.Serial;
        DefaultUnitId = 1;
        Snapshot = new ConnectionSnapshot(ConnectionKind.Serial, profile.PortName, session.Codec.Name,
            $"{profile.BaudRate},{profile.DataBits},{SerialSettings.ParityCode(profile.Parity)},{SerialSettings.StopBitsCode(profile.StopBits)}" +
            $" · 超时 {effectiveTimeout} ms · 重试 {profile.RetryCount}",
            DefaultUnitId);

        _log.Info("Session",
            $"[{Name}] 串口已连接：{profile.PortName} {profile.BaudRate},{profile.DataBits},{SerialSettings.ParityCode(profile.Parity)},{SerialSettings.StopBitsCode(profile.StopBits)}；" +
            $"超时 {effectiveTimeout} ms，重试 {profile.RetryCount}，静默阈值 {silence} ms",
            SessionId);
        RaiseChanged();
        return Result<string>.Ok(StatusText);
    }

    public async Task<Result<string>> ConnectTcpAsync(TcpProfile profile, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var options = new SessionOptions
        {
            SessionId = SessionId,
            Name = Name,
            ResponseTimeoutMs = profile.ResponseTimeoutMs,
            RetryCount = profile.RetryCount,
            SuppressEcho = false,
            IsSerial = false
        };

        var channel = new TcpChannel(profile.ToTcpSettings());
        var session = new ModbusSession(channel, new ModbusTcpCodec(), options, _frameBus);

        try
        {
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (CommException ex)
        {
            _log.Error("Session", $"TCP 连接失败：{ex.Error.Message}", ex);
            await session.DisposeAsync().ConfigureAwait(false);
            return Result<string>.Fail(ex.Error);
        }

        _session = session;
        _tcpProfile = profile;
        _serialProfile = null;
        Kind = ConnectionKind.Tcp;
        DefaultUnitId = profile.UnitId;
        Snapshot = new ConnectionSnapshot(ConnectionKind.Tcp, $"{profile.Host}:{profile.Port}", session.Codec.Name,
            $"Unit ID {profile.UnitId} · 超时 {profile.ResponseTimeoutMs} ms · 重试 {profile.RetryCount}",
            profile.UnitId);

        _log.Info("Session", $"[{Name}] TCP 已连接：{profile.Host}:{profile.Port}，Unit ID {profile.UnitId}", SessionId);
        RaiseChanged();
        return Result<string>.Ok(StatusText);
    }

    public async Task DisconnectAsync()
    {
        var session = _session;
        var serialPort = _serialProfile?.PortName;
        if (session is null)
        {
            if (serialPort is not null) _ports.Release(serialPort, SessionId);
            return;
        }

        _session = null;
        Kind = ConnectionKind.None;
        Snapshot = null;
        _log.Info("Session", $"[{Name}] 断开连接：{session.Channel.Endpoint}", SessionId);
        await session.DisposeAsync().ConfigureAwait(false);
        if (serialPort is not null) _ports.Release(serialPort, SessionId);
        RaiseChanged();
    }

    /// <summary>把发现的地址设为默认 Unit ID（扫描页与连接中心共用）。</summary>
    public void SetDefaultUnitId(byte unitId)
    {
        DefaultUnitId = unitId;
        if (Snapshot is { Kind: ConnectionKind.Tcp } snapshot)
        {
            Snapshot = snapshot with { UnitId = unitId };
        }

        RaiseChanged();
    }

    // ------------------------------------------------- R-11 串口参数扫描：重配与恢复

    /// <summary>参数扫描是否需要串口重配；TCP 会话返回 false。</summary>
    public bool CanReconfigure => Kind == ConnectionKind.Serial && IsConnected && _originalSerialProfile is not null;

    /// <summary>扫描前的原始串口参数（用于恢复与界面显示"原 4800-8-N-1"）。</summary>
    public SerialSettings? OriginalSettings => _originalSerialProfile?.ToSerialSettings();

    /// <summary>当前实际打开的串口参数（扫描期间会随组合变化）。</summary>
    public SerialSettings? ActiveSettings => _serialProfile?.ToSerialSettings();

    /// <summary>
    /// 切换到指定串口参数。更换参数 = 关串口 + 重开 + 清缓冲；失败返回 null（已记日志）。
    /// 调用方负责在四个终态调用 <see cref="RestoreAsync"/>。
    /// </summary>
    public async Task<ModbusSession?> ApplyAsync(SerialSettings settings, int settleMs, CancellationToken cancellationToken)
    {
        var original = _originalSerialProfile;
        if (original is null)
        {
            _log.Warning("Session", "参数扫描：没有可用的原始串口配置，无法切换参数", SessionId);
            return null;
        }

        var profile = original with
        {
            PortName = settings.PortName,
            BaudRate = settings.BaudRate,
            DataBits = settings.DataBits,
            Parity = settings.Parity,
            StopBits = settings.StopBits
        };

        var result = await OpenSerialAsync(profile, _originalSilenceThresholdMs, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            _log.Warning("Session",
                $"参数扫描：切换到 {settings.PortName} {settings.BaudRate},{settings.DataBits}," +
                $"{SerialSettings.ParityCode(settings.Parity)},{SerialSettings.StopBitsCode(settings.StopBits)} 失败：" +
                (result.Error?.Message ?? "未知原因"),
                SessionId);
            return null;
        }

        // USB 转串口重开后需要稳定等待（可配，默认 150 ms）。
        await Task.Delay(Math.Clamp(settleMs, 0, 5000), cancellationToken).ConfigureAwait(false);
        return _session;
    }

    /// <summary>恢复到扫描前的原始参数；恢复失败时明确置为"未连接"，不静默停留在错误参数。</summary>
    public async Task<SerialRestoreResult> RestoreAsync(CancellationToken cancellationToken)
    {
        var original = _originalSerialProfile;
        if (original is null) return SerialRestoreResult.Ok;

        try
        {
            // 取消路径也必须恢复，因此不受外部令牌影响，只给自己一个超时。
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var result = await OpenSerialAsync(original, _originalSilenceThresholdMs, timeout.Token).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _log.Info("Session",
                    $"已恢复原串口参数：{original.PortName} {original.BaudRate},{original.DataBits}," +
                    $"{SerialSettings.ParityCode(original.Parity)},{SerialSettings.StopBitsCode(original.StopBits)}",
                    SessionId);
                return SerialRestoreResult.Ok;
            }

            await DisconnectAsync().ConfigureAwait(false);
            return new SerialRestoreResult(false, result.Error?.Message ?? "恢复原串口参数失败");
        }
        catch (Exception ex)
        {
            _log.Error("Session", "恢复原串口参数时异常", ex, SessionId);
            try
            {
                await DisconnectAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // 已尽力断开；不掩盖原始错误
            }
            return new SerialRestoreResult(false, ex.Message);
        }
    }

    // ---------------------------------------------------------------- 服务工厂

    public DeviceScanService? CreateDeviceScanService() => _session is null ? null : new DeviceScanService(_session);

    public DataScanService? CreateDataScanService() => _session is null ? null : new DataScanService(_session);

    public RegisterLocator? CreateRegisterLocator() => _session is null ? null : new RegisterLocator(_session);

    public WatchTableService? CreateWatchTableService() => _session is null ? null : new WatchTableService(_session);

    /// <summary>会话版手动报文服务（沿用当前会话编解码器）。</summary>
    public ManualSendService? CreateManualSendService() => _session is null ? null : new ManualSendService(_session);

    /// <summary>
    /// 按显式协议选择构建报文服务（R-19）：未连接时也能生成与校验报文，只有发送需要活动会话。
    /// </summary>
    public ManualSendService CreateManualSendService(FrameTransport transport, int silenceThresholdMs = 5)
    {
        var codec = transport switch
        {
            FrameTransport.Rtu => new ModbusRtuCodec(Math.Max(1, silenceThresholdMs)),
            FrameTransport.Tcp => new ModbusTcpCodec(),
            _ => _session?.Codec ?? new ModbusRtuCodec(Math.Max(1, silenceThresholdMs))
        };

        return new ManualSendService(codec, _session);
    }

    /// <summary>
    /// 创建 TCP Unit ID 扫描服务（R-12，规划 4.2.7）：
    /// 并发为 1 时复用当前会话；并发 &gt; 1 时由工厂为额外 worker 建立**临时 TCP 连接**
    /// （同一目标 IP:端口、相同超时与重试设置），扫描结束后由服务统一释放。
    /// 非 TCP 会话返回 null。
    /// </summary>
    public UnitIdScanService? CreateUnitIdScanService()
    {
        if (_session is null || _tcpProfile is not { } profile) return null;

        return new UnitIdScanService(_session, async cancellationToken =>
        {
            var options = new SessionOptions
            {
                SessionId = SessionId,
                Name = $"{Name} · Unit ID 扫描",
                ResponseTimeoutMs = profile.ResponseTimeoutMs,
                RetryCount = profile.RetryCount,
                TurnaroundDelayMs = _tcpProfile.TurnaroundDelayMsOrZero(),
                SuppressEcho = false,
                IsSerial = false
            };

            var channel = new TcpChannel(profile.ToTcpSettings());
            var session = new ModbusSession(channel, new ModbusTcpCodec(), options, _frameBus);

            try
            {
                await session.ConnectAsync(cancellationToken).ConfigureAwait(false);
                return session;
            }
            catch (Exception ex)
            {
                _log.Warning("Session", $"Unit ID 扫描：建立并发连接失败（{profile.Host}:{profile.Port}）：{ex.Message}", SessionId);
                await session.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        });
    }

    /// <summary>局域网扫描使用的 Modbus 识别委托：为每台主机建立一次临时 TCP 会话，只发一次只读请求。</summary>
    public ModbusIdentifyDelegate CreateLanIdentifyDelegate() => async (ip, port, unitId, cancellationToken) =>
    {
        var options = new SessionOptions
        {
            SessionId = SessionId,
            Name = $"{Name} · 局域网识别",
            ResponseTimeoutMs = 800,
            RetryCount = 0,
            SuppressEcho = false,
            IsSerial = false
        };

        var channel = new TcpChannel(new TcpSettings { Host = ip, Port = port, ConnectTimeoutMs = 800 });
        var session = new ModbusSession(channel, new ModbusTcpCodec(), options, _frameBus);

        try
        {
            await session.ConnectAsync(cancellationToken).ConfigureAwait(false);

            var result = await session.ExecuteAsync(
                ModbusRequest.Scan(unitId, ModbusRequests.Read(DataArea.HoldingRegisters, 0, 1)),
                cancellationToken).ConfigureAwait(false);

            return result.Status switch
            {
                ExchangeStatus.Ok => "读保持寄存器成功",
                ExchangeStatus.ModbusException =>
                    $"异常响应 0x{result.ExceptionCode:X2}（{ModbusExceptions.Describe(result.ExceptionCode ?? 0)}）",
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    };

    /// <summary>幂等释放：容器释放与手工释放都可能调用（见 M1 反馈问题 5/10）。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _ports.ReleaseAll(SessionId);
    }
}
