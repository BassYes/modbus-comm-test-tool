using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Comm.Network;
using System.Diagnostics.CodeAnalysis;

// 冻结契约 NetConfigContracts.cs 里的 AdapterBackendFactory 在生产路径上只被 Windows 层
// （Comm.NetConfigHelper）调用；该文件不允许修改，因此在此按成员精确抑制这一条平台兼容性告警。
// 其余调用点仍然受 CA1416 保护（IpHelperBackend 等类型上的 SupportedOSPlatform 标注保留）。
[assembly: SuppressMessage("Interoperability", "CA1416:Validate platform compatibility",
    Scope = "member", Target = "~M:Comm.Platform.Windows.AdapterBackendFactory.Create")]

namespace Comm.Platform.Windows;

// ---------------------------------------------------------------------------------------------
// 网卡配置后端的公共支撑（IpHelperBackend 与 NetshBackend 共用）。
//
// 规划依据：
//   4.8.3 —— 修改前读取快照、应用后回读校验；
//   15.8  —— 第 1 步快照、第 3 步"应用后回读校验"、第 4 步"回读不一致则回滚"；
//   AD-07 —— netsh 输出随系统语言变化，因此这里只取退出码，绝不解析文本。
// ---------------------------------------------------------------------------------------------

/// <summary>
/// 网卡只读读取（普通权限即可）与"回读比较"的公共实现。
/// 两个后端都从这里读，保证 IP Helper 与 netsh 两条路径给出的 <see cref="AdapterSnapshot"/> 语义完全一致。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AdapterReadback
{
    /// <summary>AF_INET。</summary>
    public const int AfInet = 2;

    /// <summary>枚举所有网卡（单个网卡读取失败不影响其他网卡，见规划 4.8.2）。</summary>
    public static IReadOnlyList<AdapterSnapshot> ReadAll(Action<string, string, string?>? log = null)
    {
        var list = new List<AdapterSnapshot>();

        NetworkInterface[] adapters;
        try
        {
            adapters = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (Exception ex)
        {
            log?.Invoke("Error", "NetConfig", $"枚举网卡失败：{ex.Message}");
            return list;
        }

        foreach (var adapter in adapters)
        {
            var snapshot = TryRead(adapter, log);
            if (snapshot is not null) list.Add(snapshot);
        }

        return list;
    }

    /// <summary>读取单个网卡；不存在时返回 null。允许用网卡 Id（GUID）或显示名匹配。</summary>
    public static AdapterSnapshot? ReadOne(string? adapterId, Action<string, string, string?>? log = null)
    {
        var adapter = Find(adapterId);
        return adapter is null ? null : TryRead(adapter, log);
    }

    /// <summary>按 Id（GUID）或名称查找 <see cref="NetworkInterface"/>。</summary>
    public static NetworkInterface? Find(string? adapterId)
    {
        if (string.IsNullOrWhiteSpace(adapterId)) return null;

        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(a =>
                string.Equals(a.Id, adapterId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Name, adapterId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            // 枚举本身失败时无法定位网卡；由调用方报 PLT-5003。
            return null;
        }
    }

    /// <summary>把一块网卡读成快照；读取失败返回 null（不让单个网卡拖垮整个枚举）。</summary>
    public static AdapterSnapshot? TryRead(NetworkInterface adapter, Action<string, string, string?>? log = null)
    {
        try
        {
            var props = adapter.GetIPProperties();

            var ipv4 = new List<Ipv4AddressInfo>();
            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                int prefix = TryGetPrefixLength(unicast);
                ipv4.Add(new Ipv4AddressInfo(
                    Address: unicast.Address.ToString(),
                    PrefixLength: prefix,
                    Mask: MaskText(prefix),
                    IsDhcpLease: IsDhcpLease(unicast),
                    IsAutomatic: IsAutomatic(unicast)));
            }

            var gateways = props.GatewayAddresses
                .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(g => g.Address.ToString())
                .Where(text => text != "0.0.0.0")
                .ToList();

            var dns = props.DnsAddresses
                .Where(d => d.AddressFamily == AddressFamily.InterNetwork)
                .Select(d => d.ToString())
                .ToList();

            return new AdapterSnapshot
            {
                Id = adapter.Id,
                Name = adapter.Name,
                Description = adapter.Description,
                MacAddress = FormatMac(adapter.GetPhysicalAddress()),
                IsUp = adapter.OperationalStatus == OperationalStatus.Up,
                IsVirtual = NetworkAdapters.IsLikelyVirtualAdapter(adapter.Name, adapter.Description),
                IsLoopback = adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                DhcpEnabled = TryGetDhcpEnabled(props),
                Ipv4 = ipv4,
                Gateways = gateways,
                DnsServers = dns,
                // 快照时间取本地时间（界面按本地时间显示，见 AdapterSnapshot.CapturedAt 默认值）。
                CapturedAt = DateTimeOffset.Now
            };
        }
        catch (Exception ex)
        {
            log?.Invoke("Warning", "NetConfig", $"读取网卡 {adapter.Name} 失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>网卡索引（NET_IFINDEX）；IP Helper 的 MIB 行用它定位网卡。</summary>
    public static uint TryGetInterfaceIndex(IPInterfaceProperties props)
    {
        try
        {
            var index = props.GetIPv4Properties()?.Index ?? 0;
            if (index > 0) return (uint)index;
        }
        catch (Exception)
        {
            // 纯 IPv6 网卡没有 IPv4 属性，继续尝试 IPv6 属性。
        }

        try
        {
            var index = props.GetIPv6Properties()?.Index ?? 0;
            if (index > 0) return (uint)index;
        }
        catch (Exception)
        {
            // 无索引时返回 0，由调用方按 PLT-5004 处理。
        }

        return 0;
    }

    /// <summary>前缀长度对应的点分十进制掩码文本。</summary>
    public static string MaskText(int prefixLength) => SubnetCalculator.Format(MaskValue(prefixLength));

    /// <summary>前缀长度对应的掩码值。</summary>
    public static uint MaskValue(int prefixLength) => prefixLength switch
    {
        <= 0 => 0u,
        >= 32 => uint.MaxValue,
        _ => uint.MaxValue << (32 - prefixLength)
    };

    /// <summary>MAC 用冒号分隔，与 <see cref="NetworkAdapters"/> 的展示保持一致。</summary>
    public static string FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? "-" : string.Join(':', bytes.Select(b => b.ToString("X2")));
    }

    /// <summary>把 <c>a.b.c.d/prefix</c> 集合渲染成便于阅读的差异文本。</summary>
    public static string Text(IEnumerable<Ipv4AddressInfo> addresses) =>
        string.Join("、", addresses.Select(a => a.Cidr));

    /// <summary>把 <c>a.b.c.d/prefix</c> 归一化成集合（做回读比较时忽略顺序，保留重复检测）。</summary>
    public static HashSet<string> CidrSet(IEnumerable<Ipv4AddressInfo> addresses) =>
        addresses.Select(a => a.Cidr).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static int TryGetPrefixLength(UnicastIPAddressInformation unicast)
    {
        try
        {
            int prefix = unicast.PrefixLength;
            if (prefix is > 0 and <= 32) return prefix;

            // PrefixLength 在个别老系统/虚拟驱动上可能为 0；此时退回 IPv4Mask 反算。
            if (prefix == 0 && unicast.IPv4Mask is { } mask)
            {
                uint value = ToUInt32(mask);
                int fromMask = 0;
                for (uint probe = value; probe != 0; probe <<= 1) fromMask++;
                if (fromMask is > 0 and <= 32) return fromMask;
            }

            return prefix is >= 0 and <= 32 ? prefix : 32;
        }
        catch (Exception)
        {
            return 32;
        }
    }

    private static bool IsDhcpLease(UnicastIPAddressInformation unicast)
    {
        try
        {
            return unicast.PrefixOrigin == PrefixOrigin.Dhcp || unicast.SuffixOrigin == SuffixOrigin.OriginDhcp;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsAutomatic(UnicastIPAddressInformation unicast)
    {
        try
        {
            return unicast.PrefixOrigin is PrefixOrigin.WellKnown or PrefixOrigin.RouterAdvertisement;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryGetDhcpEnabled(IPInterfaceProperties props)
    {
        try
        {
            return props.GetIPv4Properties() is { IsDhcpEnabled: true };
        }
        catch (Exception)
        {
            // 没有 IPv4 属性的网卡（纯 IPv6 / 已禁用）没有 DHCP 概念。
            return false;
        }
    }

    private static uint ToUInt32(System.Net.IPAddress mask)
    {
        var bytes = mask.GetAddressBytes();
        if (bytes.Length != 4) return 0;
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }
}

/// <summary>
/// 回读比较（规划 15.8 第 3 步）：把"期望"和"实际读到的快照"对比，返回人可读的差异说明。
/// 所有后端都必须经过这一层，避免出现"命令返回 0 就认为改成功"的静默失败。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NetConfigCompare
{
    /// <summary>应用静态地址/DHCP 后的回读校验；返回 null 表示一致。</summary>
    public static string? ForApply(NetworkConfigRequest request, AdapterSnapshot? before, AdapterSnapshot? readback)
    {
        if (readback is null) return "回读失败：无法读取网卡当前配置";

        if (request.DhcpEnabled)
        {
            return readback.DhcpEnabled
                ? null
                : $"期望切换到 DHCP，但回读到静态配置（{AdapterReadback.Text(readback.Ipv4)}）";
        }

        var target = BuildTarget(request.Address, request.PrefixLength);
        if (target is null) return $"请求中的地址 \"{request.Address}\" 无法解析";

        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target };
        if (!request.ReplaceExistingAddresses && before is not null)
        {
            foreach (var cidr in AdapterReadback.CidrSet(before.Ipv4)) expected.Add(cidr);
        }

        var addressDiff = CompareAddresses(expected, readback, "应用");
        if (addressDiff is not null) return addressDiff;

        if (request.ClearGateway && readback.Gateways.Count > 0)
        {
            return $"期望清除默认网关，但回读到网关 {string.Join(",", readback.Gateways)}";
        }

        if (!request.ClearGateway && !string.IsNullOrWhiteSpace(request.Gateway) &&
            !readback.Gateways.Contains(request.Gateway, StringComparer.OrdinalIgnoreCase))
        {
            return $"期望默认网关 {request.Gateway}，但回读到 {(readback.Gateways.Count == 0 ? "无网关" : string.Join(",", readback.Gateways))}";
        }

        if (request.ApplyDns && !string.IsNullOrWhiteSpace(request.Dns1) &&
            !readback.DnsServers.Contains(request.Dns1, StringComparer.OrdinalIgnoreCase))
        {
            return $"期望首选 DNS {request.Dns1}，但回读到 " +
                   $"{(readback.DnsServers.Count == 0 ? "无 DNS" : string.Join(",", readback.DnsServers))}";
        }

        return null;
    }

    /// <summary>辅助 IP 增删后的回读校验：<paramref name="expectedCidrs"/> 是期望的最终地址集合。</summary>
    public static string? ForAddressSet(IEnumerable<string> expectedCidrs, AdapterSnapshot? readback, string operation)
    {
        if (readback is null) return "回读失败：无法读取网卡当前配置";
        return CompareAddresses(expectedCidrs.ToHashSet(StringComparer.OrdinalIgnoreCase), readback, operation);
    }

    /// <summary>回滚后的回读校验：地址、网关、DNS、DHCP 状态都必须回到快照（规划 15.8 第 4 步）。</summary>
    public static string? ForRestore(AdapterSnapshot expected, AdapterSnapshot? readback)
    {
        if (readback is null) return "回读失败：无法读取网卡当前配置";

        var addressDiff = CompareAddresses(AdapterReadback.CidrSet(expected.Ipv4), readback, "回滚");
        if (addressDiff is not null) return addressDiff;

        if (expected.DhcpEnabled != readback.DhcpEnabled)
        {
            return $"期望的 DHCP 状态为 {(expected.DhcpEnabled ? "DHCP" : "静态")}，实际为 {(readback.DhcpEnabled ? "DHCP" : "静态")}";
        }

        var expectedGateways = new HashSet<string>(expected.Gateways, StringComparer.OrdinalIgnoreCase);
        var actualGateways = new HashSet<string>(readback.Gateways, StringComparer.OrdinalIgnoreCase);
        if (!expectedGateways.SetEquals(actualGateways))
        {
            return $"期望默认网关 {(expectedGateways.Count == 0 ? "无" : string.Join(",", expectedGateways))}，" +
                   $"实际 {(actualGateways.Count == 0 ? "无" : string.Join(",", actualGateways))}";
        }

        // DNS 只在快照确实有值时校验：快照里没有 DNS 说明"当时无 DNS"，DHCP 环境下可能自然为空。
        if (expected.DnsServers.Count > 0)
        {
            var missing = expected.DnsServers
                .Where(d => !readback.DnsServers.Contains(d, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (missing.Length > 0) return $"期望 DNS {string.Join(",", missing)} 未恢复";
        }

        return null;
    }

    /// <summary>构造 <c>a.b.c.d/prefix</c>；地址非法时返回 null。</summary>
    public static string? BuildTarget(string? address, int prefixLength)
    {
        return SubnetCalculator.TryParseAddress(address, out var value)
            ? $"{SubnetCalculator.Format(value)}/{prefixLength}"
            : null;
    }

    private static string? CompareAddresses(HashSet<string> expected, AdapterSnapshot readback, string operation)
    {
        var actual = AdapterReadback.CidrSet(readback.Ipv4);
        if (expected.SetEquals(actual)) return null;

        var missing = expected.Where(c => !actual.Contains(c)).ToArray();
        var extra = actual.Where(c => !expected.Contains(c)).ToArray();

        var parts = new List<string>();
        if (missing.Length > 0) parts.Add($"缺少 {string.Join("、", missing)}");
        if (extra.Length > 0) parts.Add($"多出 {string.Join("、", extra)}");

        return $"{operation}后回读不一致：期望 {string.Join("、", expected)}，实际 {(actual.Count == 0 ? "无 IPv4 地址" : string.Join("、", actual))}" +
               $"（{string.Join("；", parts)}）";
    }
}

/// <summary>
/// netsh 调用封装（AD-07：只看退出码，绝不解析文本输出——输出随系统语言变化）。
/// 主程序侧的 netsh 调用只用于 IP Helper 没有稳定 API 的两处：DHCP 开关与 DNS 改写。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NetshCommand
{
    /// <summary>netsh 单次调用的超时；正常调用在百毫秒级，超时通常意味着网络栈异常。</summary>
    public const int TimeoutMs = 20_000;

    /// <summary>把网卡名包装成 netsh 需要的 <c>name="..."</c> 形式（含空格的名字必须带引号）。</summary>
    public static string NameArgument(string adapterName) => $"name=\"{adapterName}\"";

    /// <summary>
    /// 执行一条 netsh 命令。返回 false 时 <paramref name="failure"/> 为可读原因（含退出码与命令行）。
    /// 输出被丢弃：既避免弹控制台窗口，也杜绝依赖文本内容（AD-07）。
    /// </summary>
    public static bool Run(Action<string, string, string?>? log, out int exitCode, out string? failure, params string[] arguments)
    {
        exitCode = -1;
        failure = null;
        var commandLine = "netsh " + string.Join(' ', arguments);

        try
        {
            var startInfo = new ProcessStartInfo("netsh")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                failure = $"无法启动 netsh（{commandLine}）";
                return false;
            }

            // 必须把两路输出读空，否则可能写满管道缓冲区导致子进程阻塞。
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(TimeoutMs))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // 结束进程失败不影响错误上报。
                }

                failure = $"netsh 超时（{TimeoutMs} ms）：{commandLine}";
                log?.Invoke("Error", "NetConfig", failure);
                return false;
            }

            exitCode = process.ExitCode;
            _ = stdout.Result;
            _ = stderr.Result;

            if (exitCode == 0)
            {
                log?.Invoke("Debug", "NetConfig", $"netsh 成功：{commandLine}");
                return true;
            }

            failure = $"netsh 返回 {exitCode}：{commandLine}";
            log?.Invoke("Warning", "NetConfig", failure);
            return false;
        }
        catch (Exception ex)
        {
            failure = $"调用 netsh 失败（{commandLine}）：{ex.Message}";
            log?.Invoke("Error", "NetConfig", failure);
            return false;
        }
    }

    /// <summary>把 Win32 / IP Helper 错误码翻译成中文原因（放进 <see cref="NetConfigResult.Message"/>）。</summary>
    public static string Describe(int errorCode) => errorCode switch
    {
        0 => "成功",
        5 => "拒绝访问（需要管理员权限）",
        8 => "内存不足",
        87 => "参数无效（地址/掩码/网卡索引不被接受）",
        505 => "IP 地址冲突：该地址已被网络中其他设备占用",
        1168 => "找不到对象（网卡或路由条目可能已被移除）",
        2250 => "网络连接不存在",
        1169 => "没有更多条目",
        1722 => "RPC 服务器不可用",
        5010 => "对象已存在",
        1162 => "提权助手未运行",
        _ => new Win32Exception(errorCode).Message
    };
}
