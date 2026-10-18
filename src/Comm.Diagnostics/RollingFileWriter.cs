using System.Text;
using System.Threading.Channels;

namespace Comm.Diagnostics;

/// <summary>
/// 滚动文件写入器：后台线程批量刷盘，任何日志调用都不阻塞通信线程与 UI（见规划 13.6）。
/// </summary>
public sealed class RollingFileWriter : IAsyncDisposable
{
    private readonly Channel<string> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly string _basePath;
    private readonly int _maxFileSizeBytes;
    private readonly object _sync = new();
    private StreamWriter? _writer;
    private string? _currentPath;
    private int _fileIndex;
    private long _dropped;
    private long _written;

    public RollingFileWriter(string basePath, int maxFileSizeMb = 20, int capacity = 10_000)
    {
        _basePath = basePath;
        _maxFileSizeBytes = Math.Max(1, maxFileSizeMb) * 1024 * 1024;
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(() => WriteLoopAsync(_cts.Token));
    }

    public string? ActiveFile
    {
        get { lock (_sync) return _currentPath; }
    }

    /// <summary>已成功写入文件的**日志行数**（M4 更正：原先统计的是刷新批次数）。</summary>
    public long WrittenCount => Interlocked.Read(ref _written);
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public void WriteLine(string line)
    {
        if (!_channel.Writer.TryWrite(line)) Interlocked.Increment(ref _dropped);
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        var sb = new StringBuilder(16 * 1024);
        int batchLines = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) break;
                while (_channel.Reader.TryRead(out var line))
                {
                    sb.AppendLine(line);
                    batchLines++;
                }

                if (sb.Length > 0)
                {
                    int count = batchLines;
                    batchLines = 0;
                    await FlushAsync(sb.ToString(), count).ConfigureAwait(false);
                    sb.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        try
        {
            if (sb.Length > 0) await FlushAsync(sb.ToString(), batchLines).ConfigureAwait(false);
            await CloseAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private async Task FlushAsync(string text, int lineCount)
    {
        await EnsureWriterAsync().ConfigureAwait(false);
        if (_writer is null) return;
        await _writer.WriteAsync(text).ConfigureAwait(false);
        await _writer.FlushAsync().ConfigureAwait(false);
        Interlocked.Add(ref _written, Math.Max(0, lineCount));
    }

    private Task EnsureWriterAsync()
    {
        lock (_sync)
        {
            if (_writer is not null && _writer.BaseStream.Length < _maxFileSizeBytes) return Task.CompletedTask;
            if (_writer is not null)
            {
                try
                {
                    _writer.Dispose();
                }
                catch (Exception)
                {
                }
                _writer = null;
                _fileIndex++;
            }

            var dir = Path.GetDirectoryName(_basePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var path = _fileIndex == 0
                ? _basePath
                : Path.Combine(dir ?? ".", $"{Path.GetFileNameWithoutExtension(_basePath)}-{_fileIndex:D3}{Path.GetExtension(_basePath)}");

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, true);
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
            _currentPath = path;
            return Task.CompletedTask;
        }
    }

    private async Task CloseAsync()
    {
        StreamWriter? writer;
        lock (_sync)
        {
            writer = _writer;
            _writer = null;
        }
        if (writer is null) return;
        await writer.FlushAsync().ConfigureAwait(false);
        writer.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        _cts.Dispose();
    }
}
