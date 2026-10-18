namespace Comm.App.Wpf.Services;

/// <summary>
/// 串口占用登记表（规划 4.1.3、21.2.2 第 4 点）：串口资源以 <c>serial:COMx</c> 为键全局独占，
/// 多个 TCP 会话可并行，但同一个 COM 口不能被两个会话同时打开。
/// 这里只做"友好提示"的登记；真正的独占由操作系统打开串口时保证。
/// </summary>
public sealed class SerialPortRegistry
{
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    /// <summary>尝试占用串口。失败时 <paramref name="error"/> 给出可读原因（含占用者会话名）。</summary>
    public bool TryAcquire(string portName, string sessionId, string sessionName, out string? error)
    {
        lock (_sync)
        {
            if (_owners.TryGetValue(portName, out var owner) && !string.Equals(owner, sessionId, StringComparison.Ordinal))
            {
                error = $"{portName} 已被会话「{DescribeOwner(owner)}」占用；同一串口同一时刻只能被一个会话打开。";
                return false;
            }

            _owners[portName] = sessionId;
            _sessionNames[sessionId] = sessionName;
            error = null;
            return true;
        }
    }

    public void Release(string portName, string sessionId)
    {
        lock (_sync)
        {
            if (_owners.TryGetValue(portName, out var owner) && string.Equals(owner, sessionId, StringComparison.Ordinal))
            {
                _owners.Remove(portName);
            }
        }
    }

    public void ReleaseAll(string sessionId)
    {
        lock (_sync)
        {
            foreach (var port in _owners.Where(p => p.Value == sessionId).Select(p => p.Key).ToArray())
            {
                _owners.Remove(port);
            }

            _sessionNames.Remove(sessionId);
        }
    }

    /// <summary>记住会话名，供占用提示使用。</summary>
    public void RegisterName(string sessionId, string sessionName)
    {
        lock (_sync) _sessionNames[sessionId] = sessionName;
    }

    public string? OwnerSessionIdOf(string portName)
    {
        lock (_sync) return _owners.TryGetValue(portName, out var owner) ? owner : null;
    }

    /// <summary>当前所有占用项的可读摘要（用于任务中心/诊断）。</summary>
    public string Describe()
    {
        lock (_sync)
        {
            return _owners.Count == 0
                ? "当前没有串口被会话占用"
                : string.Join("；", _owners.Select(p => $"{p.Key} ← {DescribeOwner(p.Value)}"));
        }
    }

    private string DescribeOwner(string sessionId) =>
        _sessionNames.TryGetValue(sessionId, out var name) ? name : sessionId;

    private readonly Dictionary<string, string> _sessionNames = new(StringComparer.Ordinal);
}
