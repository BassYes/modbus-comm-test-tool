using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Comm.Core;
using Comm.Core.Errors;
using Comm.Core.Transport;

namespace Comm.Transport;

/// <summary>TCP 通道（见规划 14.2.3）。</summary>
public sealed class TcpChannel : ITransportChannel
{
    private readonly TcpSettings _settings;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private TcpClient? _client;
    private NetworkStream? _stream;
    private ChannelState _state = ChannelState.Closed;

    public TcpChannel(TcpSettings settings)
    {
        _settings = settings;
    }

    public string Id => $"tcp:{_settings.Host}:{_settings.Port}";
    public TransportKind Kind => TransportKind.Tcp;
    public string Endpoint => _settings.ToDisplayString();

    public ChannelState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, new ChannelStateChanged(value));
        }
    }

    public event EventHandler<ChannelStateChanged>? StateChanged;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_state == ChannelState.Open) return;
        State = ChannelState.Opening;

        var client = new TcpClient
        {
            NoDelay = _settings.NoDelay
        };
        if (_settings.KeepAlive) client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Math.Max(200, _settings.ConnectTimeoutMs));
            await client.ConnectAsync(_settings.Host, _settings.Port, timeout.Token).ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();
            State = ChannelState.Open;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            State = ChannelState.Faulted;
            var error = new ErrorInfo(ErrorCodes.TransportTimeout, ErrorCategory.Transport, Severity.Error,
                $"连接 {_settings.ToDisplayString()} 超时（{_settings.ConnectTimeoutMs} ms）", null,
                Hint: "确认设备 IP、端口与网络连通性");
            StateChanged?.Invoke(this, new ChannelStateChanged(ChannelState.Faulted, error.Message, error));
            throw new CommException(error);
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            client.Dispose();
            State = ChannelState.Faulted;
            var error = new ErrorInfo(ErrorCodes.TransportOpenFailed, ErrorCategory.Transport, Severity.Error,
                $"连接 {_settings.ToDisplayString()} 失败：{ex.Message}", ex.ToString(),
                Hint: "确认目标可达、端口开放且未被防火墙拦截");
            StateChanged?.Invoke(this, new ChannelStateChanged(ChannelState.Faulted, error.Message, error));
            throw new CommException(error, ex);
        }
    }

    public Task CloseAsync()
    {
        State = ChannelState.Closing;
        try
        {
            _stream?.Dispose();
            _client?.Dispose();
        }
        catch (Exception)
        {
            // 忽略关闭异常
        }
        _stream = null;
        _client = null;
        State = ChannelState.Closed;
        return Task.CompletedTask;
    }

    public async ValueTask<TxStamp> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var stream = _stream;
        if (stream is null || _state != ChannelState.Open)
            throw new CommException(new ErrorInfo(ErrorCodes.TransportNotOpen, ErrorCategory.Transport, Severity.Error,
                "TCP 连接未建立，无法发送"));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new TxStamp(Clock.Now, Clock.MonotonicTicks);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            var error = new ErrorInfo(ErrorCodes.TransportLinkLost, ErrorCategory.Transport, Severity.Error,
                $"TCP 写入失败：{ex.Message}", ex.ToString(), Retriable: true,
                Hint: "对端可能已断开，可尝试重新连接");
            State = ChannelState.Faulted;
            StateChanged?.Invoke(this, new ChannelStateChanged(ChannelState.Faulted, error.Message, error));
            throw new CommException(error, ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<RxChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stream = _stream;
        if (stream is null) yield break;

        var buffer = new byte[8192];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                var error = new ErrorInfo(ErrorCodes.TransportLinkLost, ErrorCategory.Transport, Severity.Error,
                    $"TCP 连接中断：{ex.Message}", ex.ToString(), Retriable: true,
                    Hint: "对端可能已关闭连接或被网络中断；可重新连接");
                State = ChannelState.Faulted;
                StateChanged?.Invoke(this, new ChannelStateChanged(ChannelState.Faulted, error.Message, error));
                yield break;
            }

            if (read == 0)
            {
                var error = new ErrorInfo(ErrorCodes.TransportLinkLost, ErrorCategory.Transport, Severity.Warning,
                    "对端关闭了连接", null, Retriable: true, Hint: "重新连接后继续测试");
                State = ChannelState.Faulted;
                StateChanged?.Invoke(this, new ChannelStateChanged(ChannelState.Faulted, error.Message, error));
                yield break;
            }

            yield return new RxChunk
            {
                Data = buffer[..read],
                Timestamp = Clock.Now,
                MonotonicTicks = Clock.MonotonicTicks
            };
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _writeLock.Dispose();
    }
}
