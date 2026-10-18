using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using System.Net.Sockets;

namespace Comm.Network;

public sealed record LocalAdapterInfo(
    string Id,
    string Name,
    string Description,
    string MacAddress,
    string Status,
    string SpeedText,
    IReadOnlyList<string> Ipv4Addresses,
    IReadOnlyList<string> Ipv4Masks,
    IReadOnlyList<string> Gateways,
    bool DhcpEnabled,
    string? DnsServers,
    bool IsLoopback)
{
    public string PrimaryIpv4 => Ipv4Addresses.FirstOrDefault() ?? "-";
    public string PrimaryMask => Ipv4Masks.FirstOrDefault() ?? "-";
    public string PrimaryGateway => Gateways.FirstOrDefault() ?? "-";

    /// <summary>是否已连接（OperationalStatus 为 Up）。</summary>
    public bool IsUp => Status.Equals("Up", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否虚拟/隧道类网卡（X-7）。局域网扫描默认选物理且已连接的网卡，
    /// 避免默认落在 Hyper-V / VMware / VPN 的虚拟网段上，把扫描打到错误的地方。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public bool IsVirtual => NetworkAdapters.IsLikelyVirtualAdapter(Name, Description);
}

/// <summary>本机网卡只读枚举（v0.1 只读；写入在 M3 由提权助手完成，见规划 4.8、12.5）。</summary>
[SupportedOSPlatform("windows")]
public static class NetworkAdapters
{
    private static readonly string[] VirtualKeywords =
    {
        "hyper-v", "vmware", "virtualbox", "virtual", "vethernet", "loopback", "tap-", "tap windows",
        "vpn", "wireguard", "openvpn", "wan miniport", "bluetooth", "tunnel", "pseudo", "npcap", "wintun",
        "docker", "zerotier", "tailscale", "radmin", "hamachi"
    };

    /// <summary>按名字/描述判断是否是虚拟或隧道类网卡（启发式，仅用于排序，不做硬过滤）。</summary>
    public static bool IsLikelyVirtualAdapter(string name, string description)
    {
        var text = $"{name} {description}".ToLowerInvariant();
        return VirtualKeywords.Any(text.Contains);
    }

    /// <summary>
    /// X-7：按“物理优先、已连接优先、非环回优先”排序后的网卡列表。
    /// 局域网扫描与“用本机地址”默认取第一个，但界面仍允许手动选择任意网卡。
    /// </summary>
    public static IReadOnlyList<LocalAdapterInfo> ReadPreferred() => Read()
        .OrderBy(a => a.IsVirtual ? 1 : 0)
        .ThenBy(a => a.IsUp ? 0 : 1)
        .ThenBy(a => a.IsLoopback ? 1 : 0)
        .ThenBy(a => a.PrimaryIpv4 == "-" ? 1 : 0)
        .ToArray();

    /// <summary>局域网扫描默认使用的本机地址（物理且已连接、有 IPv4）；没有时返回 null。</summary>
    public static LocalAdapterInfo? PreferredAdapter() => ReadPreferred()
        .FirstOrDefault(a => !a.IsLoopback && a.IsUp && a.PrimaryIpv4 != "-" && !a.IsVirtual)
        ?? ReadPreferred().FirstOrDefault(a => !a.IsLoopback && a.PrimaryIpv4 != "-");

    public static IReadOnlyList<LocalAdapterInfo> Read()
    {
        var result = new List<LocalAdapterInfo>();

        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var props = adapter.GetIPProperties();
                var ipv4 = new List<string>();
                var masks = new List<string>();

                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    ipv4.Add(unicast.Address.ToString());
                    masks.Add(unicast.IPv4Mask is { } mask
                        ? mask.ToString()
                        : SubnetCalculator.Format(0xFFFFFFFF));
                }

                var gateways = props.GatewayAddresses
                    .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(g => g.Address.ToString())
                    .ToList();

                var dns = props.DnsAddresses
                    .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                    .Select(d => d.ToString())
                    .ToList();

                result.Add(new LocalAdapterInfo(
                    Id: adapter.Id,
                    Name: adapter.Name,
                    Description: adapter.Description,
                    MacAddress: FormatMac(adapter.GetPhysicalAddress()),
                    Status: adapter.OperationalStatus.ToString(),
                    SpeedText: adapter.Speed > 0 ? $"{adapter.Speed / 1_000_000} Mbps" : "-",
                    Ipv4Addresses: ipv4,
                    Ipv4Masks: masks,
                    Gateways: gateways,
                    DhcpEnabled: TryGetDhcpEnabled(props),
                    DnsServers: dns.Count > 0 ? string.Join(", ", dns) : null,
                    IsLoopback: adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback));
            }
            catch (Exception)
            {
                // 单个网卡读取失败不影响其他网卡展示。
            }
        }

        return result;
    }

    private static bool TryGetDhcpEnabled(IPInterfaceProperties props)
    {
        try
        {
            return props.GetIPv4Properties() is { IsDhcpEnabled: true };
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "-" : string.Join(':', bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>本机所有 IPv4 地址（用于提示同网段范围）。</summary>
    public static IReadOnlyList<IPAddress> LocalIpv4Addresses() => Read()
        .SelectMany(a => a.Ipv4Addresses)
        .Select(text => IPAddress.TryParse(text, out var ip) ? ip : null)
        .Where(ip => ip is not null)
        .Select(ip => ip!)
        .ToArray();
}
