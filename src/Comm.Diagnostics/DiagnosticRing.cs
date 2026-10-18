namespace Comm.Diagnostics;

/// <summary>环形内存日志：日志开关关闭时仍保留最近若干条，供“复制诊断信息”使用（见规划 12.6）。</summary>
public sealed class DiagnosticRing
{
    private readonly LogEntry[] _buffer;
    private readonly object _sync = new();
    private int _next;
    private int _count;

    public DiagnosticRing(int capacity = 2000)
    {
        _buffer = new LogEntry[Math.Max(16, capacity)];
    }

    public int Capacity => _buffer.Length;

    public void Add(LogEntry entry)
    {
        lock (_sync)
        {
            _buffer[_next] = entry;
            _next = (_next + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }
    }

    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_sync)
        {
            var result = new List<LogEntry>(_count);
            int start = _count == _buffer.Length ? _next : 0;
            for (int i = 0; i < _count; i++)
            {
                result.Add(_buffer[(start + i) % _buffer.Length]);
            }
            return result;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            Array.Clear(_buffer);
            _next = 0;
            _count = 0;
        }
    }
}
