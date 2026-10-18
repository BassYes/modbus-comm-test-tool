using System.Diagnostics;
using System.Timers;
using Comm.Core.Frames;
using Timer = System.Timers.Timer;

namespace Comm.Capture;

public sealed record FramesAppendedEventArgs(long FromIndex, int Count, long TotalCount, long DroppedFromDisplay);

/// <summary>
/// 报文总线：单点采集、多路分发（落盘 / 界面显示 / 解析），见规划 13.5。
/// 显示是可丢弃的展示层：超出容量时丢最旧的显示条目，不影响落盘。
/// </summary>
public sealed class FrameBus : IFrameObserver, IAsyncDisposable
{
    private readonly List<FrameRecord> _records = new();
    private readonly object _sync = new();
    private readonly Timer _notifyTimer;
    private readonly FrameFileWriter? _writer;

    private long _firstIndex;
    private long _totalCount;
    private long _droppedFromDisplay;
    private long _notifiedIndex;
    private bool _disposed;

    public FrameBus(int displayCapacity = 200_000, FrameFileWriter? writer = null)
    {
        DisplayCapacity = Math.Max(1000, displayCapacity);
        _writer = writer;
        _notifyTimer = new Timer(50) { AutoReset = true };
        _notifyTimer.Elapsed += OnNotifyTick;
        _notifyTimer.Start();
    }

    public int DisplayCapacity { get; }

    /// <summary>解析开关：关闭时不做任何解析（不影响通信与记录，见 4.6.4）。</summary>
    /// <remarks>
    /// 本属性在采集端生效，因此天然是<b>应用级</b>开关（审计 F-04）：
    /// 界面层通过 <c>MonitorSharedState</c> 暴露它，并标注"全局"，不再假装它是会话级状态。
    /// </remarks>
    public bool ParseEnabled { get; set; } = true;

    public long TotalCount => Interlocked.Read(ref _totalCount);
    public long DroppedFromDisplay => Interlocked.Read(ref _droppedFromDisplay);
    public long LastIndex => Interlocked.Read(ref _totalCount) - 1;
    public FrameFileWriter? Writer => _writer;

    public event EventHandler<FramesAppendedEventArgs>? FramesAppended;

    public void OnFrame(FrameRecord record)
    {
        if (!ParseEnabled) record.Summary = null;

        lock (_sync)
        {
            _records.Add(record);
            if (_records.Count > DisplayCapacity)
            {
                int remove = Math.Max(1, DisplayCapacity / 10);
                _records.RemoveRange(0, Math.Min(remove, _records.Count - DisplayCapacity + remove));
                _firstIndex += remove;
                Interlocked.Add(ref _droppedFromDisplay, remove);
            }
        }

        Interlocked.Increment(ref _totalCount);
        _writer?.Enqueue(record);
    }

    /// <summary>取指定索引之后的记录（用于界面增量刷新）。</summary>
    public IReadOnlyList<FrameRecord> GetSince(long fromIndexExclusive, int maxCount = 5000)
    {
        lock (_sync)
        {
            long start = Math.Max(fromIndexExclusive + 1, _firstIndex);
            long end = _firstIndex + _records.Count;
            if (start >= end) return Array.Empty<FrameRecord>();

            int offset = (int)(start - _firstIndex);
            int count = (int)Math.Min(maxCount, end - start);
            return _records.GetRange(offset, count);
        }
    }

    /// <summary>当前保留的全部记录快照（导出用）。</summary>
    public IReadOnlyList<FrameRecord> Snapshot()
    {
        lock (_sync) return _records.ToArray();
    }

    public long FirstIndex
    {
        get { lock (_sync) return _firstIndex; }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _records.Clear();
            _firstIndex = Interlocked.Read(ref _totalCount);
        }
        _notifiedIndex = _firstIndex;
    }

    /// <summary>把界面显示进度重置到最新位置（暂停恢复后一次性补齐）。</summary>
    public void MarkNotified() => _notifiedIndex = LastIndex;

    private void OnNotifyTick(object? sender, ElapsedEventArgs e)
    {
        // 审计 F-04：暂停显示改为会话级（各 MonitorService 自己判断），总线不再持有"全局暂停"状态。
        long last = LastIndex;
        long from = Interlocked.Read(ref _notifiedIndex);
        if (last <= from) return;

        Interlocked.Exchange(ref _notifiedIndex, last);
        FramesAppended?.Invoke(this, new FramesAppendedEventArgs(from + 1, (int)Math.Min(int.MaxValue, last - from),
            TotalCount, DroppedFromDisplay));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyTimer.Stop();
        _notifyTimer.Dispose();
        if (_writer is not null) await _writer.DisposeAsync().ConfigureAwait(false);
    }
}
