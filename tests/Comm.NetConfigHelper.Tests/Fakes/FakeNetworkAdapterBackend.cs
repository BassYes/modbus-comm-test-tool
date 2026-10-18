using Comm.Platform.Windows;

namespace Comm.NetConfigHelper.Tests.Fakes;

/// <summary>
/// 内存假后端（规划 16.3.7：CI 中以 <see cref="INetworkAdapterBackend"/> 的模拟实现验证流程逻辑，不触碰真实网卡）。
/// 记录每一次调用，便于断言"哪些方法被调用、参数对不对、有没有被误调用"。
/// </summary>
internal sealed class FakeNetworkAdapterBackend : INetworkAdapterBackend
{
    public List<AdapterSnapshot> Adapters { get; } = new();

    public List<(string AdapterId, NetworkConfigRequest Request)> ApplyCalls { get; } = new();

    public List<(string AdapterId, AdapterSnapshot Snapshot)> RestoreCalls { get; } = new();

    public List<(string AdapterId, string Address, int PrefixLength)> AddAuxCalls { get; } = new();

    public List<(string AdapterId, string Address)> RemoveAuxCalls { get; } = new();

    /// <summary>非 null 时 <see cref="Apply"/> 抛出该异常（验证"后端异常不会让助手崩掉"）。</summary>
    public Exception? ApplyException { get; set; }

    public NetConfigResult ApplyResult { get; set; } = NetConfigResult.Ok();

    public NetConfigResult RestoreResult { get; set; } = NetConfigResult.Ok();

    public NetConfigResult AuxResult { get; set; } = NetConfigResult.Ok();

    public int ReadAdaptersCount { get; private set; }

    /// <summary>所有写操作调用总数（用于断言"未知命令没有触碰后端"）。</summary>
    public int WriteCallCount => ApplyCalls.Count + RestoreCalls.Count + AddAuxCalls.Count + RemoveAuxCalls.Count;

    public int TotalCallCount => WriteCallCount + ReadAdaptersCount + ReadAdapterCount;

    public int ReadAdapterCount { get; private set; }

    public IReadOnlyList<AdapterSnapshot> ReadAdapters()
    {
        ReadAdaptersCount++;
        return Adapters.ToArray();
    }

    public AdapterSnapshot? ReadAdapter(string adapterId)
    {
        ReadAdapterCount++;
        return Adapters.FirstOrDefault(a => a.Id == adapterId);
    }

    public NetConfigResult Apply(string adapterId, NetworkConfigRequest request)
    {
        ApplyCalls.Add((adapterId, request));
        if (ApplyException is not null) throw ApplyException;
        return ApplyResult;
    }

    public NetConfigResult Restore(string adapterId, AdapterSnapshot snapshot)
    {
        RestoreCalls.Add((adapterId, snapshot));
        return RestoreResult;
    }

    public NetConfigResult AddAuxiliaryAddress(string adapterId, string address, int prefixLength)
    {
        AddAuxCalls.Add((adapterId, address, prefixLength));
        return AuxResult;
    }

    public NetConfigResult RemoveAuxiliaryAddress(string adapterId, string address)
    {
        RemoveAuxCalls.Add((adapterId, address));
        return AuxResult;
    }

    /// <summary>构造一个可用的测试快照（含一个静态地址与网关）。</summary>
    public static AdapterSnapshot SampleSnapshot(string id = "adapter-1", string name = "测试网卡",
        string address = "192.168.1.10", int prefixLength = 24, string gateway = "192.168.1.1") =>
        new()
        {
            Id = id,
            Name = name,
            Description = "Fake Adapter",
            MacAddress = "00-11-22-33-44-55",
            IsUp = true,
            DhcpEnabled = false,
            Ipv4 = new[] { new Ipv4AddressInfo(address, prefixLength, "255.255.255.0") },
            Gateways = new[] { gateway },
            DnsServers = new[] { "223.5.5.5" },
            CapturedAt = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(8))
        };

    /// <summary>构造一个带一个默认网卡的假后端。</summary>
    public static FakeNetworkAdapterBackend WithSampleAdapter(out AdapterSnapshot snapshot)
    {
        var backend = new FakeNetworkAdapterBackend();
        snapshot = SampleSnapshot();
        backend.Adapters.Add(snapshot);
        return backend;
    }
}
