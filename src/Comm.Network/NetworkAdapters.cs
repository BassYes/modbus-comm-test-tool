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
}

/// <summary>本机网卡只读枚举（v0.1 只读；写入在 M3 由提权助手完成，见规划 4.8、12.5）。</summary>
[SupportedOSPlatform("windows")]
public static class NetworkAdapters
{
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
