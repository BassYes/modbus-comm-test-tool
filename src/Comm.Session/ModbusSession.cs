using Comm.Core.Codecs;
using Comm.Core.Errors;
using Comm.Core.Frames;
using Comm.Core.Transport;
using Comm.Protocols.Modbus;

namespace Comm.Session;

/// <summary>
/// 会话门面：持有传输通道与事务层，供界面与扫描服务使用。
/// </summary>
public sealed class ModbusSession : IAsyncDisposable
{
    private readonly IFrameObserver? _observer;
    private ModbusMaster? _master;

    public ModbusSession(ITransportChannel channel, IFrameCodec codec, SessionOptions options, IFrameObserver? observer = null)
    {
        Channel = channel;
        Codec = codec;
        Options = options;
        _observer = observer;
        channel.StateChanged += (_, e) => StateChanged?.Invoke(this, e);
    }

    public ITransportChannel Channel { get; }
    public IFrameCodec Codec { get; }
    public SessionOptions Options { get; }
    public SessionStatistics Statistics => _master?.Statistics ?? _idleStatistics;
    private readonly SessionStatistics _idleStatistics = new();

    public ChannelState State => Channel.State;
    public bool IsOpen => Channel.State == ChannelState.Open && _master is not null;
    public string SessionId => Options.SessionId;

    public event EventHandler<ChannelStateChanged>? StateChanged;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await Channel.OpenAsync(cancellationToken).ConfigureAwait(false);
        _master = new ModbusMaster(Channel, Codec, Options, _observer);
        _master.Start();
    }

    public async Task DisconnectAsync()
    {
        if (_master is not null)
        {
            await _master.StopAsync().ConfigureAwait(false);
            _master = null;
        }
        await Channel.CloseAsync().ConfigureAwait(false);
    }

    public Task<ExchangeResult> ExecuteAsync(ModbusRequest request, CancellationToken cancellationToken = default)
    {
        if (_master is null)
        {
            return Task.FromResult(ExchangeResult.NotConnectedResult(request, new ErrorInfo(
                ErrorCodes.TransportNotOpen, ErrorCategory.Transport, Severity.Error, "会话未连接，请先连接设备")));
        }
        return _master.ExecuteAsync(request, cancellationToken);
    }

    /// <summary>按当前会话默认超时构造一次读取请求。</summary>
    public Task<ExchangeResult> ReadAsync(byte unitId, DataArea area, ushort address, ushort quantity,
        RequestPriority priority = RequestPriority.Interactive, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new ModbusRequest(unitId, ModbusRequests.Read(area, address, quantity), priority), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await Channel.DisposeAsync().ConfigureAwait(false);
    }
}
