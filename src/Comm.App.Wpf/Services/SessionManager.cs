using Comm.Capture;
using Comm.Core;
using Comm.Core.Codecs;
using Comm.Core.Errors;
using Comm.Core.Frames;
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
/// 会话管理：创建/连接/断开传输通道，并向各功能页提供会话与服务实例。
/// 说明：本类只实现 <see cref="IAsyncDisposable"/>，**退出时必须异步释放**（见 M1 反馈问题 5/10），
/// 且释放是幂等的——容器释放与手工释放可能都会调用它。
/// R-11：同时实现 <see cref="ISerialReconfigurator"/>，为未知波特率/校验位扫描提供串口重配与恢复。
/// </summary>
public sealed class SessionManager : IAsyncDisposable, ISerialReconfigurator
{
    private readonly IFrameObserver _observer;
    private readonly LogGate _log;
    private ModbusSession? _session;
    private SerialProfile? _serialProfile;
    private TcpProfile? _tcpProfile;

    /// <summary>参数扫描前的原始串口配置（R-11：四种终态都要恢复到它）。</summary>
    private SerialProfile? _originalSerialProfile;
    private int _originalSilenceThresholdMs = RtuTiming.MinimumSilenceThresholdMs;

    private bool _disposed;

    public SessionManager(FrameBus frameBus, LogGate log)
    {
        _observer = frameBus;
        _log = log;
    }

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

    public event EventHandler? CurrentChanged;

    public string StatusText => _session switch
    {
        null => "未连接",
        { IsOpen: true } => Snapshot?.ShortText ?? $"已连接 {_session.Channel.Endpoint}（{_session.Codec.Name}）",
        _ => $"已断开 {_session.Channel.Endpoint}（{_session.Codec.Name}）"
    };

    public async Task<Result<string>> ConnectSerialAsync(SerialProfile profile, int silenceThresholdMs,
        CancellationToken cancellationToken = default)
    {
        // R-11：用户主动连接时记住“原始参数”，参数扫描结束后要恢复到它。
        _originalSerialProfile = profile;
        _originalSilenceThresholdMs = silenceThresholdMs;
        return await OpenSerialAsync(profile, silenceThresholdMs, cancellationToken).ConfigureAwait(false);
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
            SessionId = Guid.NewGuid().ToString("N")[..8],
            Name = profile.Name,
            ResponseTimeoutMs = effectiveTimeout,
            RetryCount = profile.RetryCount,
            TurnaroundDelayMs = profile.TurnaroundDelayMs,
            SuppressEcho = profile.SuppressEcho,
            IsSerial = true
        };

        var channel = new SerialChannel(profile.ToSerialSettings());
        var session = new ModbusSession(channel, new ModbusRtuCodec(silence), options, _observer);

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
            $"串口已连接：{profile.PortName} {profile.BaudRate},{profile.DataBits},{SerialSettings.ParityCode(profile.Parity)},{SerialSettings.StopBitsCode(profile.StopBits)}；" +
            $"超时 {effectiveTimeout} ms，重试 {profile.RetryCount}，静默阈值 {silence} ms",
            session.SessionId);
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return Result<string>.Ok(StatusText);
    }

    public async Task<Result<string>> ConnectTcpAsync(TcpProfile profile, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        var options = new SessionOptions
        {
            SessionId = Guid.NewGuid().ToString("N")[..8],
            Name = profile.Name,
            ResponseTimeoutMs = profile.ResponseTimeoutMs,
            RetryCount = profile.RetryCount,
            SuppressEcho = false,
            IsSerial = false
        };

        var channel = new TcpChannel(profile.ToTcpSettings());
        var session = new ModbusSession(channel, new ModbusTcpCodec(), options, _observer);

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

        _log.Info("Session", $"TCP 已连接：{profile.Host}:{profile.Port}，Unit ID {profile.UnitId}", session.SessionId);
        CurrentChanged?.Invoke(this, EventArgs.Empty);
        return Result<string>.Ok(StatusText);
    }

    public async Task DisconnectAsync()
    {
        var session = _session;
        if (session is null) return;

        _session = null;
        Kind = ConnectionKind.None;
        Snapshot = null;
        _log.Info("Session", $"断开连接：{session.Channel.Endpoint}", session.SessionId);
        await session.DisposeAsync().ConfigureAwait(false);
        CurrentChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把发现的地址设为默认 Unit ID（扫描页与连接中心共用）。</summary>
    public void SetDefaultUnitId(byte unitId)
    {
        DefaultUnitId = unitId;
        if (Snapshot is { Kind: ConnectionKind.Tcp } snapshot)
        {
            Snapshot = snapshot with { UnitId = unitId };
        }
    }

    // ------------------------------------------------- R-11 串口参数扫描：重配与恢复

    /// <summary>参数扫描是否需要串口重配；TCP 会话返回 false。</summary>
    public bool CanReconfigure => Kind == ConnectionKind.Serial && IsConnected && _originalSerialProfile is not null;

    /// <summary>扫描前的原始串口参数（用于恢复与界面显示“原 4800-8-N-1”）。</summary>
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
            _log.Warning("Session", "参数扫描：没有可用的原始串口配置，无法切换参数");
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
                (result.Error?.Message ?? "未知原因"));
            return null;
        }

        // USB 转串口重开后需要稳定等待（可配，默认 150 ms）。
        await Task.Delay(Math.Clamp(settleMs, 0, 5000), cancellationToken).ConfigureAwait(false);
        return _session;
    }

    /// <summary>恢复到扫描前的原始参数；恢复失败时明确置为“未连接”，不静默停留在错误参数。</summary>
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
                    $"{SerialSettings.ParityCode(original.Parity)},{SerialSettings.StopBitsCode(original.StopBits)}");
                return SerialRestoreResult.Ok;
            }

            await DisconnectAsync().ConfigureAwait(false);
            return new SerialRestoreResult(false, result.Error?.Message ?? "恢复原串口参数失败");
        }
        catch (Exception ex)
        {
            _log.Error("Session", "恢复原串口参数时异常", ex);
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

    /// <summary>局域网扫描使用的 Modbus 识别委托：为每台主机建立一次临时 TCP 会话，只发一次只读请求。</summary>
    public ModbusIdentifyDelegate CreateLanIdentifyDelegate() => async (ip, port, unitId, cancellationToken) =>
    {
        var options = new SessionOptions
        {
            SessionId = Guid.NewGuid().ToString("N")[..8],
            Name = "局域网识别",
            ResponseTimeoutMs = 800,
            RetryCount = 0,
            SuppressEcho = false,
            IsSerial = false
        };

        var channel = new TcpChannel(new TcpSettings { Host = ip, Port = port, ConnectTimeoutMs = 800 });
        var session = new ModbusSession(channel, new ModbusTcpCodec(), options, _observer);

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
    }
}
