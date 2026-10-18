using Comm.Core.Tasks;

namespace Comm.Network.Discovery;

/// <summary>主机发现方式（见规划 4.7.2）。</summary>
public enum HostDiscoveryKind
{
    Arp,
    Icmp,
    Tcp,
    Unknown
}

/// <summary>
/// 主机状态分级（见规划 4.7.2）：**不能因为 ICMP 无响应就判定“离线”**，
/// 现场设备常常禁 ICMP 或在路由之后，只能标为“未确认”。
/// </summary>
public enum LanHostState
{
    /// <summary>ARP 或 ICMP 有响应。</summary>
    Discovered,

    /// <summary>TcpPorts 中至少一个端口开放。</summary>
    PortOpen,

    /// <summary>开放端口上还有 Modbus 只读响应。</summary>
    ModbusResponding,

    /// <summary>无任何响应，但不等于离线。</summary>
    Unconfirmed
}

/// <summary>局域网扫描参数（见规划 4.7.1）。</summary>
public sealed record LanScanOptions
{
    /// <summary>字符串范围：<c>192.168.1.0/24</c>、<c>192.168.1.0/255.255.255.0</c>、<c>192.168.1.10-192.168.1.50</c> 或 <c>192.168.1.10-50</c>。</summary>
    public string? CidrOrRange { get; init; }

    /// <summary>与 <see cref="MaskText"/> 配合计算网段的地址。</summary>
    public string? IpAddress { get; init; }

    public string? MaskText { get; init; }

    /// <summary>起止地址（优先级高于 CidrOrRange）。</summary>
    public string? StartIp { get; init; }

    public string? EndIp { get; init; }

    public IReadOnlyList<int> TcpPorts { get; init; } = new[] { 502 };

    /// <summary>ARP 只在同一二层网段有效，路由之后拿不到（规划 4.7.2）。</summary>
    public bool UseArp { get; init; } = true;

    public bool UseIcmp { get; init; } = true;

    /// <summary>是否在开放端口上再做一次 Modbus 只读识别；默认关闭（规划 4.7.2）。</summary>
    public bool IdentifyModbus { get; init; }

    /// <summary>反查主机名（会增加每台设备的耗时，默认关闭）。</summary>
    public bool ResolveHostName { get; init; }

    /// <summary>并发探测数；使用时夹到 16 ~ 256。</summary>
    public int Concurrency { get; init; } = 64;

    public int PerHostTimeoutMs { get; init; } = 800;

    /// <summary>地址数量上限；超过时拦截并要求用户确认（规划 4.7.1）。</summary>
    public int MaxAddresses { get; init; } = 4096;

    /// <summary>全局限速：两次“开始新探测”之间的最小间隔（毫秒）。</summary>
    public int SendDelayMs { get; init; }

    public byte UnitId { get; init; } = 1;

    public int EffectiveConcurrency() => Math.Clamp(Concurrency, 16, 256);

    public int EffectiveTimeoutMs() => Math.Clamp(PerHostTimeoutMs, 20, 30_000);
}

/// <summary>单台主机的探测原始结果。</summary>
public sealed record HostProbeResult(
    bool ArpResponded,
    bool IcmpResponded,
    bool PortOpen,
    double LatencyMs,
    string? Mac,
    string? Error)
{
    public bool AnyResponse => ArpResponded || IcmpResponded || PortOpen;
}

/// <summary>扫描进度（与扫描任务的状态栏口径一致）。</summary>
public sealed record LanScanProgress(int Completed, int Total, string CurrentStep)
{
    public double Percent => Total <= 0 ? 0 : Math.Min(100.0, Completed * 100.0 / Total);

    public string ToText() => $"局域网扫描：{Completed}/{Total}（{Percent:F1}%）{CurrentStep}";
}

/// <summary>单台主机的扫描结果（规划 4.7.4 的结果表字段）。</summary>
public sealed record LanHostResult(
    string Ip,
    string? Mac,
    string? HostName,
    HostDiscoveryKind Kind,
    bool PortOpen,
    bool ModbusResponding,
    LanHostState State,
    double LatencyMs,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string Note)
{
    public string StateText => State switch
    {
        LanHostState.Discovered => "已发现",
        LanHostState.PortOpen => "端口开放",
        LanHostState.ModbusResponding => "Modbus 有响应",
        _ => "未确认"
    };

    public string KindText => Kind switch
    {
        HostDiscoveryKind.Arp => "ARP",
        HostDiscoveryKind.Icmp => "ICMP",
        HostDiscoveryKind.Tcp => "TCP",
        _ => "-"
    };

    public string PortText => PortOpen ? "开放" : "未开放";

    public string ModbusText => ModbusResponding ? "有响应" : "未识别";

    public string LatencyText => LatencyMs > 0 ? $"{LatencyMs:F0}" : "-";
}

/// <summary>
/// Modbus 识别委托：界面把“临时会话 + 一次只读请求（03 或 43/14）”接上。
/// 之所以用委托，是为了让 <c>Comm.Network</c> 不反向依赖 <c>Comm.Session</c>（见规划 6.2、12.3）。
/// </summary>
public delegate Task<string?> ModbusIdentifyDelegate(string ip, int port, byte unitId, CancellationToken cancellationToken);

/// <summary>主机探测抽象；便于测试注入假实现，CI 无需真实网络。</summary>
public interface IHostProbe
{
    Task<HostProbeResult> ProbeAsync(string ip, LanScanOptions options, CancellationToken cancellationToken);
}
