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
/// </summary>
public sealed class SessionManager : IAsyncDisposable
{
    private readonly IFrameObserver _observer;
    private readonly LogGate _log;
    private ModbusSession? _session;
    private SerialProfile? _serialProfile;
    private TcpProfile? _tcpProfile;
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
        await DisconnectAsync().ConfigureAwait(false);

        var options = new SessionOptions
        {
            SessionId = Guid.NewGuid().ToString("N")[..8],
            Name = profile.Name,
            ResponseTimeoutMs = profile.ResponseTimeoutMs,
            RetryCount = profile.RetryCount,
            TurnaroundDelayMs = profile.TurnaroundDelayMs,
            SuppressEcho = profile.SuppressEcho,
            IsSerial = true
        };

        var channel = new SerialChannel(profile.ToSerialSettings());
        var session = new ModbusSession(channel, new ModbusRtuCodec(silenceThresholdMs), options, _observer);

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
        SilenceThresholdMs = Math.Max(1, silenceThresholdMs);
        Kind = ConnectionKind.Serial;
        DefaultUnitId = 1;
        Snapshot = new ConnectionSnapshot(ConnectionKind.Serial, profile.PortName, session.Codec.Name,
            $"{profile.BaudRate},{profile.DataBits},{SerialSettings.ParityCode(profile.Parity)},{SerialSettings.StopBitsCode(profile.StopBits)}" +
            $" · 超时 {profile.ResponseTimeoutMs} ms · 重试 {profile.RetryCount}",
            DefaultUnitId);

        _log.Info("Session",
            $"串口已连接：{profile.PortName} {profile.BaudRate},{profile.DataBits},{SerialSettings.ParityCode(profile.Parity)},{SerialSettings.StopBitsCode(profile.StopBits)}；超时 {profile.ResponseTimeoutMs} ms，重试 {profile.RetryCount}",
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
