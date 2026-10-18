using Comm.Core.Frames;

namespace Comm.TestSupport;

/// <summary>收集报文的观察者，用于测试断言（见规划 16.3.3）。</summary>
public sealed class CollectingFrameObserver : IFrameObserver
{
    private readonly List<FrameRecord> _records = new();
    private readonly object _sync = new();

    public IReadOnlyList<FrameRecord> Records
    {
        get { lock (_sync) return _records.ToArray(); }
    }

    public void OnFrame(FrameRecord record)
    {
        lock (_sync) _records.Add(record);
    }

    public int Count(FrameDirection direction) => Records.Count(r => r.Direction == direction);

    public bool Has(FrameMarker marker) => Records.Any(r => r.Markers.HasFlag(marker));

    public void Clear()
    {
        lock (_sync) _records.Clear();
    }
}
