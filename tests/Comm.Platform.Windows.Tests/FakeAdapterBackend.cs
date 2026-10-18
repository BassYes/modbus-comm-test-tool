using Comm.Platform.Windows;

namespace Comm.Platform.Windows.Tests;

/// <summary>
/// 内存网卡后端（规划 16.3.7：CI 用模拟后端，绝不触碰真实网卡）。
///
/// 服务层只通过它**读**（<see cref="ReadAdapters"/> / <see cref="ReadAdapter"/>）；
/// 写入方法由提权助手进程调用，服务层不该碰。这里把写入调用记录下来，
/// 测试据此断言"服务层没有绕过助手直接改网卡"（AD-08）。
/// </summary>
public sealed class FakeAdapterBackend : INetworkAdapterBackend
{
    private readonly Dictionary<string, AdapterSnapshot> _adapters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>服务层若误调用写入方法，会记在这里（测试断言其为空）。</summary>
    public List<string> WriteCalls { get; } = new();

    /// <summary>放一块网卡进内存。</summary>
    public FakeAdapterBackend Add(AdapterSnapshot snapshot)
    {
        _adapters[snapshot.Id] = snapshot;
        return this;
    }

    /// <summary>替换某块网卡的当前状态（模拟"网卡真的被改了"）。</summary>
    public void Update(AdapterSnapshot snapshot) => _adapters[snapshot.Id] = snapshot;

    public IReadOnlyList<AdapterSnapshot> ReadAdapters() => _adapters.Values.ToArray();

    public AdapterSnapshot? ReadAdapter(string adapterId) =>
        _adapters.TryGetValue(adapterId, out var snapshot) ? snapshot : null;

    public NetConfigResult Apply(string adapterId, NetworkConfigRequest request)
    {
        WriteCalls.Add("apply");
        return NetConfigResult.Ok(ReadAdapter(adapterId));
    }

    public NetConfigResult Restore(string adapterId, AdapterSnapshot snapshot)
    {
        WriteCalls.Add("restore");
        return NetConfigResult.Ok(ReadAdapter(adapterId));
    }

    public NetConfigResult AddAuxiliaryAddress(string adapterId, string address, int prefixLength)
    {
        WriteCalls.Add("add-aux");
        return NetConfigResult.Ok(ReadAdapter(adapterId));
    }

    public NetConfigResult RemoveAuxiliaryAddress(string adapterId, string address)
    {
        WriteCalls.Add("remove-aux");
        return NetConfigResult.Ok(ReadAdapter(adapterId));
    }

    // -----------------------------------------------------------------------------------------
    // 构造常用快照
    // -----------------------------------------------------------------------------------------

    /// <summary>一块"静态 + 有地址 + 有网关"的普通以太网卡。</summary>
    public static AdapterSnapshot StaticAdapter(
        string id = "fake-adapter-0001",
        string name = "以太网",
        string address = "192.168.1.50",
        int prefixLength = 24,
        string? gateway = "192.168.1.1",
        params string[] dns)
    {
        return new AdapterSnapshot
        {
            Id = id,
            Name = name,
            Description = "假网卡（测试用）",
            MacAddress = "00:11:22:33:44:55",
            IsUp = true,
            DhcpEnabled = false,
            Ipv4 = new[] { Address(address, prefixLength) },
            Gateways = string.IsNullOrWhiteSpace(gateway) ? Array.Empty<string>() : new[] { gateway! },
            DnsServers = dns.Length == 0 ? new[] { "8.8.8.8" } : dns
        };
    }

    /// <summary>一块 DHCP 网卡。</summary>
    public static AdapterSnapshot DhcpAdapter(string id = "fake-adapter-0002", string name = "WLAN")
    {
        return new AdapterSnapshot
        {
            Id = id,
            Name = name,
            Description = "假无线网卡（测试用）",
            MacAddress = "AA:BB:CC:DD:EE:FF",
            IsUp = true,
            DhcpEnabled = true,
            Ipv4 = new[] { Address("10.0.0.7", 22, isDhcpLease: true) },
            Gateways = new[] { "10.0.0.1" },
            DnsServers = new[] { "10.0.0.1" }
        };
    }

    /// <summary>按前缀长度构造地址条目（掩码文本用点分十进制书写）。</summary>
    public static Ipv4AddressInfo Address(string address, int prefixLength, bool isDhcpLease = false)
    {
        uint mask = prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);
        var maskText = $"{(mask >> 24) & 0xFF}.{(mask >> 16) & 0xFF}.{(mask >> 8) & 0xFF}.{mask & 0xFF}";
        return new Ipv4AddressInfo(address, prefixLength, maskText, isDhcpLease);
    }
}

/// <summary>
/// 测试临时目录：统一放在测试程序集的输出目录下（<c>test-tmp/</c>）。
/// 不写系统 %TEMP%：部分受限环境（沙箱/无写权限的临时目录）会在那里直接拒绝创建目录。
/// </summary>
public static class TestPaths
{
    /// <summary>新建一个唯一的临时目录。</summary>
    public static string NewDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "test-tmp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>尽力删除临时目录（删除失败不影响用例结论）。</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
