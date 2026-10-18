using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Comm.Core;
using Comm.Core.Frames;

namespace Comm.Capture;

/// <summary>
/// 报文落盘写入器（JSONL，追加写、崩溃后最多丢最后一行，见 AD-11、15.10）。
/// 支持按体积滚动（M1 欠账 D-4）：单个文件超过 <see cref="MaxFileSizeMb"/> 时自动切到
/// <c>frames-时间戳-002.jsonl</c>，避免长时间记录无限增长。
/// </summary>
public sealed class FrameFileWriter : IAsyncDisposable
{
    private readonly Channel<FrameRecord> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;
    private readonly object _sync = new();
    private long _written;
    private long _dropped;
    private readonly List<string> _files = new();
    private string? _path;
    private string? _basePath;
    private int _fileIndex;
    private bool _disposed;

    public FrameFileWriter(int capacity = 100_000, int maxFileSizeMb = 64)
    {
        MaxFileSizeMb = Math.Max(1, maxFileSizeMb);
        _channel = Channel.CreateBounded<FrameRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(() => WriteLoopAsync(_cts.Token));
    }

    /// <summary>单个记录文件的体积上限（MB）；超过后滚动到下一个文件。</summary>
    public int MaxFileSizeMb { get; }

    /// <summary>当前正在写入的记录文件路径；为空表示未开始记录。</summary>
    public string? Path
    {
        get { lock (_sync) return _path; }
    }

    /// <summary>本次记录产生的全部文件（含滚动后的分卷），供界面提示与诊断包收集。</summary>
    public IReadOnlyList<string> Files
    {
        get { lock (_sync) return _files.ToArray(); }
    }

    public bool IsRecording { get; private set; }
    public long WrittenCount => Interlocked.Read(ref _written);
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>打开记录文件。失败时返回错误信息（界面必须可见，见 15.10）。</summary>
    public string? Start(string filePath)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var header = JsonSerializer.Serialize(new
                {
                    type = "header",
                    app = "通讯测试工具",
                    version = typeof(FrameFileWriter).Assembly.GetName().Version?.ToString() ?? "0.2.0",
                    startedAt = DateTimeOffset.Now,
                    timeZone = TimeZoneInfo.Local.Id
                });
                stream.Write(Encoding.UTF8.GetBytes(header + Environment.NewLine));
            }

            lock (_sync)
            {
                _basePath = filePath;
                _path = filePath;
                _fileIndex = 0;
                _files.Clear();
                _files.Add(filePath);
            }

            IsRecording = true;
            return null;
        }
        catch (Exception ex)
        {
            IsRecording = false;
            return $"无法创建记录文件：{ex.Message}";
        }
    }

    public void Stop() => IsRecording = false;

    public void Enqueue(FrameRecord record)
    {
        if (!IsRecording) return;
        if (!_channel.Writer.TryWrite(record)) Interlocked.Increment(ref _dropped);
    }

    private async Task WriteLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new StringBuilder(64 * 1024);
        var options = new JsonSerializerOptions { WriteIndented = false };

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) break;

                while (_channel.Reader.TryRead(out var record))
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        type = "frame",
                        seq = record.Seq,
                        session = record.SessionId,
                        ts = record.Timestamp.ToString("O"),
                        dir = record.DirectionText,
                        status = record.Status.ToString(),
                        markers = record.MarkerText,
                        unit = record.UnitId,
                        fn = record.FunctionCode,
                        hex = record.Hex,
                        elapsedMs = record.ElapsedMs,
                        summary = record.Summary
                    }, options);
                    buffer.AppendLine(json);
                }

                if (buffer.Length > 0)
                {
                    await AppendAsync(buffer.ToString(), cancellationToken).ConfigureAwait(false);
                    buffer.Clear();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception)
            {
                // 写盘失败：停止记录并保留计数，由界面提示（见 15.10）。
                IsRecording = false;
                break;
            }
        }

        try
        {
            if (buffer.Length > 0) await AppendAsync(buffer.ToString(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 退出时的最后刷新失败不需要再报错。
        }
    }

    private async Task AppendAsync(string text, CancellationToken cancellationToken)
    {
        var target = EnsureCapacity();
        if (target is null) return;

        var bytes = Encoding.UTF8.GetBytes(text);
        using var stream = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, true);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _written);
    }

    /// <summary>按体积滚动：超过上限时切换到下一个分卷文件。</summary>
    private string? EnsureCapacity()
    {
        lock (_sync)
        {
            if (_path is null || _basePath is null) return null;

            try
            {
                var info = new FileInfo(_path);
                if (!info.Exists || info.Length < (long)MaxFileSizeMb * 1024 * 1024) return _path;

                _fileIndex++;
                var directory = System.IO.Path.GetDirectoryName(_basePath) ?? ".";
                var name = System.IO.Path.GetFileNameWithoutExtension(_basePath);
                var extension = System.IO.Path.GetExtension(_basePath);
                _path = System.IO.Path.Combine(directory, $"{name}-{_fileIndex + 1:D3}{extension}");
                _files.Add(_path);
            }
            catch (Exception)
            {
                // 读取文件长度失败时保持当前文件，不中断记录。
            }

            return _path;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

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
