using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Comm.Core;
using Comm.Core.Errors;
using Comm.Core.Transport;

namespace Comm.Transport;

/// <summary>
/// 串口通道（见规划 14.2.2）。使用 BaseStream 异步读写，不使用 DataReceived 事件。
/// </summary>
public sealed class SerialChannel : ITransportChannel
{
    private readonly SerialSettings _settings;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private SerialPort? _port;
    private ChannelState _state = ChannelState.Closed;

    public SerialChannel(SerialSettings settings)
    {
        _settings = settings;
    }

    public string Id => $"serial:{_settings.PortName}";
    public TransportKind Kind => TransportKind.Serial;
    public string Endpoint => _settings.PortName;

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

    public static IReadOnlyList<string> GetPortNames()
    {
        try
        {
            return SerialPort.GetPortNames().OrderBy(NaturalOrder).ToArray();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private static int NaturalOrder(string name)
    {
        var digits = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_state == ChannelState.Open) return Task.CompletedTask;
        State = ChannelState.Opening;

        try
        {
            var port = new SerialPort(_settings.PortName, _settings.BaudRate, ToParity(_settings.Parity),
                _settings.DataBits, ToStopBits(_settings.StopBits))
            {
                Handshake = Handshake.None,
                ReadTimeout = Math.Max(100, _settings.ReadTimeoutMs),
                WriteTimeout = Math.Max(100, _settings.WriteTimeoutMs),
                DtrEnable = true,
                RtsEnable = true
            };
            port.Open();
            _port = port;
            State = ChannelState.Open;
            return Task.CompletedTask;
        }
        catch (UnauthorizedAccessException ex)
        {
            State = ChannelState.Faulted;
            StateChanged?.Invoke(this, new ChannelStateChanged(ChannelState.Faulted, "串口被占用或权限不足",
                new ErrorInfo(ErrorCodes.TransportPortBusy, ErrorCategory.Transport, Severity.Error,
                    $"无法打开 {_settings.PortName}：串口被其他程序占用，或当前用户权限不足",
                    ex.Message, Retriable: false, Hint: "关闭占用该串口的程序后重试；必要时以管理员身份运行")));
            throw new CommException(new ErrorInfo(ErrorCodes.TransportPortBusy, ErrorCategory.Transport, Severity.Error,
                $"无法打开 {_settings.PortName}：串口被其他程序占用，或当前用户权限不足", ex.Message,
                Hint: "关闭占用该串口的程序后重试"), ex);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            State = ChannelState.Faulted;
            var error = new ErrorInfo(ErrorCodes.TransportOpenFailed, ErrorCategory.Transport, Severity.Error,
                $"无法打开串口 {_settings.PortName}：{ex.Message}", ex.ToString(),
                Hint: "确认串口存在、参数正确，并已插入适配器");
            StateChanged?.Invoke(this, new ChannelStateChanged(ChannelState.Faulted, error.Message, error));
            throw new CommException(error, ex);
        }
    }

    public Task CloseAsync()
    {
        State = ChannelState.Closing;
        try
        {
            _port?.Close();
        }
        catch (Exception)
        {
            // 关闭失败不影响退出流程。
        }
        _port?.Dispose();
        _port = null;
        State = ChannelState.Closed;
        return Task.CompletedTask;
    }

    public async ValueTask<TxStamp> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var port = _port;
        if (port is null || _state != ChannelState.Open)
            throw new CommException(new ErrorInfo(ErrorCodes.TransportNotOpen, ErrorCategory.Transport, Severity.Error,
                "串口未打开，无法发送"));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await port.BaseStream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
            await port.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new TxStamp(Clock.Now, Clock.MonotonicTicks);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
        {
            var error = new ErrorInfo(ErrorCodes.TransportWriteFailed, ErrorCategory.Transport, Severity.Error,
                $"串口写入失败：{ex.Message}", ex.ToString(),
                Hint: "检查适配器是否被拔出或驱动异常");
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
        var port = _port;
        if (port is null) yield break;

        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(Math.Max(100, _settings.ReadTimeoutMs));
                read = await port.BaseStream.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                continue; // 读超时属正常情况，继续等待
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
            {
                var error = new ErrorInfo(ErrorCodes.TransportLinkLost, ErrorCategory.Transport, Severity.Error,
                    $"串口读取中断：{ex.Message}", ex.ToString(),
                    Hint: "USB 串口可能已被拔出；请重新插入后重新连接");
                State = ChannelState.Faulted;
                StateChanged?.Invoke(this, new ChannelStateChanged(ChannelState.Faulted, error.Message, error));
                yield break;
            }

            if (read <= 0) continue;

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

    private static Parity ToParity(SerialParity parity) => parity switch
    {
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        SerialParity.Mark => Parity.Mark,
        SerialParity.Space => Parity.Space,
        _ => Parity.None
    };

    private static StopBits ToStopBits(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.OnePointFive => StopBits.OnePointFive,
        SerialStopBits.Two => StopBits.Two,
        _ => StopBits.One
    };
}
