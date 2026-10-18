using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Comm.Platform.Windows;
using Xunit;

// 本测试程序集专测 Windows 平台层（IP Helper / netsh / 命名管道），因此整体声明为仅支持 Windows，
// 这样调用 IpHelperBackend 等带 SupportedOSPlatform 标注的类型时不会被 CA1416 误报。
[assembly: SupportedOSPlatform("windows")]

namespace Comm.Platform.Windows.Tests;

/// <summary>
/// IP Helper 后端的冒烟测试（规划 16.3.7）。
///
/// 只做**只读**验证：<see cref="IpHelperBackend.ReadAdapters"/> / <see cref="IpHelperBackend.ReadAdapter"/>。
/// 绝不调用 Apply / Restore / AddAuxiliaryAddress / RemoveAuxiliaryAddress，不跑 netsh set、不请求提权、
/// 不改本机网络配置——CI 与开发机上都可以安全反复执行。
/// </summary>
public class IpHelperBackendSmokeTests
{
    [Fact]
    public void ReadAdapters_returns_snapshots_without_throwing()
    {
        var backend = new IpHelperBackend();

        var adapters = backend.ReadAdapters();

        Assert.NotNull(adapters);
        // 任何 Windows 机器至少有一个回环接口，因此列表不会是空的。
        Assert.NotEmpty(adapters);
        Assert.All(adapters, adapter =>
        {
            Assert.False(string.IsNullOrWhiteSpace(adapter.Id));
            Assert.False(string.IsNullOrWhiteSpace(adapter.Name));
            Assert.All(adapter.Ipv4, address =>
            {
                Assert.InRange(address.PrefixLength, 0, 32);
                Assert.Equal(address.PrefixLength, ParsePrefix(address.Mask));
            });
        });
    }

    [Fact]
    public void ReadAdapters_marks_loopback_interface()
    {
        var backend = new IpHelperBackend();

        var loopback = backend.ReadAdapters().FirstOrDefault(a => a.IsLoopback);

        Assert.NotNull(loopback);
        Assert.Contains(loopback!.Ipv4, address => address.Address == "127.0.0.1");
    }

    [Fact]
    public void ReadAdapter_matches_by_id_and_name_and_returns_null_for_unknown()
    {
        var backend = new IpHelperBackend();
        var first = backend.ReadAdapters()[0];

        var byId = backend.ReadAdapter(first.Id);
        var byName = backend.ReadAdapter(first.Name);

        Assert.NotNull(byId);
        Assert.NotNull(byName);
        Assert.Equal(first.Id, byName!.Id);
        Assert.Null(backend.ReadAdapter("不存在的网卡-id"));
        Assert.Null(backend.ReadAdapter(string.Empty));
    }

    [Fact]
    public void Netsh_backend_reads_the_same_adapter_set()
    {
        // 两个后端的读路径共用 NetworkInterface，读到的网卡集合必须一致（写入路径不同）。
        var ipHelper = new IpHelperBackend().ReadAdapters();
        var netsh = new NetshBackend().ReadAdapters();

        Assert.Equal(
            ipHelper.Select(a => a.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
            netsh.Select(a => a.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 布局自检（只读）：直接调用 IP Helper 的 <c>GetUnicastIpAddressTable</c>，按后端自己声明的
    /// 结构体尺寸/偏移解析真实表体，确认能正确读出回环地址。
    ///
    /// 这个用例覆盖了写路径最危险的假设——「x64 下 MIB_UNICASTIPADDRESS_ROW 是 80 字节、表体从偏移 8 开始」。
    /// 如果这里失败，写操作会因 <c>LayoutVerified</c> 自检而整体拒绝服务，因此必须在真实系统上验证一次。
    /// 全程只读，不调用任何 set/create/delete。
    /// </summary>
    [Fact]
    public void IpHelper_unicast_table_layout_parses_real_addresses()
    {
        var backendType = typeof(IpHelperBackend);
        var flags = BindingFlags.NonPublic | BindingFlags.Static;

        var getTable = backendType.GetMethod("GetUnicastIpAddressTable", flags);
        var freeTable = backendType.GetMethod("FreeMibTable", flags);
        var rowType = backendType.GetNestedType("MIB_UNICASTIPADDRESS_ROW", BindingFlags.NonPublic | BindingFlags.Public);
        var sockaddrType = backendType.GetNestedType("SOCKADDR_INET", BindingFlags.NonPublic | BindingFlags.Public);

        Assert.NotNull(getTable);
        Assert.NotNull(freeTable);
        Assert.NotNull(rowType);
        Assert.NotNull(sockaddrType);

        // 与 IpHelperBackend 的布局自检保持一致：尺寸不对就不该放行写操作。
        Assert.Equal(80, Marshal.SizeOf(rowType!));

        // AF_INET = 2
        var arguments = new object?[] { 2, IntPtr.Zero };
        int error = (int)getTable!.Invoke(null, arguments)!;
        Assert.Equal(0, error);

        var table = (IntPtr)arguments[1]!;
        Assert.NotEqual(IntPtr.Zero, table);

        try
        {
            int count = Marshal.ReadInt32(table);
            Assert.True(count > 0, "IP Helper 地址表不应为空");

            int rowSize = Marshal.SizeOf(rowType!);
            var addressField = rowType!.GetField("Address")!;
            var familyField = sockaddrType!.GetField("Family")!;
            var ipv4Field = sockaddrType.GetField("Ipv4Address")!;

            var addresses = new List<string>();
            for (int index = 0; index < count; index++)
            {
                // MIB_UNICASTIPADDRESS_TABLE2 = { ULONG NumEntries; MIB_UNICASTIPADDRESS_ROW Table[]; }
                var row = Marshal.PtrToStructure(IntPtr.Add(table, 8 + (index * rowSize)), rowType!);
                Assert.NotNull(row);

                var sockaddr = addressField.GetValue(row)!;
                if ((ushort)familyField.GetValue(sockaddr)! != 2) continue;

                uint raw = (uint)ipv4Field.GetValue(sockaddr)!;
                // 内存中是网络字节序的 4 字节，逐字节拼回可读地址。
                addresses.Add(new IPAddress(new[] { (byte)raw, (byte)(raw >> 8), (byte)(raw >> 16), (byte)(raw >> 24) })
                    .ToString());
            }

            Assert.Contains("127.0.0.1", addresses);
        }
        finally
        {
            freeTable!.Invoke(null, new object?[] { table });
        }
    }

    /// <summary>把点分十进制掩码换算成前缀长度（用于交叉校验 Ipv4AddressInfo 的两个字段是否一致）。</summary>
    private static int ParsePrefix(string mask)
    {
        var parts = mask.Split('.');
        Assert.Equal(4, parts.Length);

        int prefix = 0;
        foreach (var part in parts)
        {
            int value = int.Parse(part);
            for (int bit = 7; bit >= 0; bit--)
            {
                if ((value & (1 << bit)) != 0) prefix++;
            }
        }

        return prefix;
    }
}
