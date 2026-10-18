using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Comm.Core;
using Comm.Core.Transport;

namespace Comm.Transport;

/// <summary>
/// 回环通道：把写出的字节交给一个响应函数，并把响应回灌到读取流。
/// 用于自动化测试与界面演示（无需真实硬件，见规划 16.1）。
/// </summary>
public sealed class LoopbackChannel : ITransportChannel
{
    private readonly Func<byte[], byte[]?> _responder;
    private readonly int _responseDelayMs;
    private readonly Channel<RxChunk> _rx = Channel.CreateUnbounded<RxChunk>();
    private ChannelState _state = ChannelState.Closed;
    private readonly Random _random = new();

    public LoopbackChannel(Func<byte[], byte[]?> responder, int responseDelayMs = 5, string id = "loopback")
    {
        _responder = responder;
        _responseDelayMs = responseDelayMs;
        Id = id;
    }

    public string Id { get; }
    public TransportKind Kind => TransportKind.Loopback;
    public string Endpoint => Id;

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

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        State = ChannelState.Open;
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        State = ChannelState.Closed;
        _rx.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public async ValueTask<TxStamp> WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (State != ChannelState.Open) throw new InvalidOperationException("回环通道未打开");

        var stamp = new TxStamp(Clock.Now, Clock.MonotonicTicks);
        var request = data.ToArray();
        var response = _responder(request);

        if (response is { Length: > 0 })
        {
            // 注意：Random.Next(min, max) 要求 min < max；delay = 0 时必须完全跳过延时，
            // 否则 Next(1, 1) 会抛 ArgumentOutOfRangeException（M4 性能探针发现）。
            if (_responseDelayMs > 1)
            {
                int min = _responseDelayMs / 2 + 1;
                int delay = _random.Next(min, _responseDelayMs + 1);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            await _rx.Writer.WriteAsync(new RxChunk
            {
                Data = response,
                Timestamp = Clock.Now,
                MonotonicTicks = Clock.MonotonicTicks
            }, cancellationToken).ConfigureAwait(false);
        }

        return stamp;
    }

    public async IAsyncEnumerable<RxChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await _rx.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_rx.Reader.TryRead(out var chunk)) yield return chunk;
        }
    }

    public ValueTask DisposeAsync()
    {
        _rx.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
