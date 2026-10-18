using Comm.Network;
using Xunit;

namespace Comm.Network.Tests;

/// <summary>子网计算（见规划 16.3.1）。</summary>
public class SubnetCalculatorTests
{
    [Fact]
    public void Computes_class_c_network()
    {
        var result = SubnetCalculator.Compute("192.168.1.10", "255.255.255.0");
        Assert.True(result.IsSuccess);

        var info = result.Value!;
        Assert.Equal("192.168.1.0", info.NetworkText);
        Assert.Equal("192.168.1.255", info.BroadcastText);
        Assert.Equal("192.168.1.1", info.FirstHostText);
        Assert.Equal("192.168.1.254", info.LastHostText);
        Assert.Equal(256, info.TotalAddresses);
        Assert.Equal(254, info.UsableHosts);
        Assert.Equal("192.168.1.0/24", info.Cidr);
        Assert.Equal("0.0.0.255", info.WildcardText);
        Assert.Contains("私有地址", info.AddressKind);
    }

    [Fact]
    public void Computes_slash_23_network()
    {
        var result = SubnetCalculator.Compute("10.0.0.1", "255.255.254.0");
        var info = result.Value!;

        Assert.Equal("10.0.0.0", info.NetworkText);
        Assert.Equal("10.0.1.255", info.BroadcastText);
        Assert.Equal(512, info.TotalAddresses);
        Assert.Equal(510, info.UsableHosts);
        Assert.Equal(23, info.PrefixLength);
    }

    [Theory]
    [InlineData("192.168.1.10", "24", "192.168.1.0/24")]
    [InlineData("192.168.1.10", "/24", "192.168.1.0/24")]
    [InlineData("192.168.1.10", "255.255.255.0", "192.168.1.0/24")]
    [InlineData("172.16.5.5", "16", "172.16.0.0/16")]
    public void Accepts_multiple_mask_formats(string ip, string mask, string expectedCidr)
    {
        var result = SubnetCalculator.Compute(ip, mask);
        Assert.True(result.IsSuccess);
        Assert.Equal(expectedCidr, result.Value!.Cidr);
    }

    [Fact]
    public void Handles_point_to_point_and_single_host()
    {
        var pointToPoint = SubnetCalculator.Compute("10.1.1.0", "/31").Value!;
        Assert.True(pointToPoint.IsPointToPoint);
        Assert.Equal(2, pointToPoint.UsableHosts);
        Assert.Equal("10.1.1.0", pointToPoint.FirstHostText);
        Assert.Equal("10.1.1.1", pointToPoint.LastHostText);

        var single = SubnetCalculator.Compute("10.1.1.7", "/32").Value!;
        Assert.True(single.IsSingleHost);
        Assert.Equal(1, single.UsableHosts);
        Assert.Equal("10.1.1.7", single.HostRangeText);
    }

    [Theory]
    [InlineData("192.168.1.1", "192.168.1.200", "255.255.255.0", true)]
    [InlineData("192.168.1.1", "192.168.2.1", "255.255.255.0", false)]
    [InlineData("10.0.0.1", "10.0.1.5", "255.255.254.0", true)]
    public void Compares_two_addresses(string a, string b, string mask, bool expected)
    {
        var result = SubnetCalculator.CheckSameSubnet(a, b, mask);
        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("192.168.1.1", "255.0.255.0")]
    [InlineData("192.168.1.1", "33")]
    [InlineData("192.168.1.1", "abc")]
    [InlineData("192.168.1.256", "24")]
    [InlineData("192.168.1", "24")]
    [InlineData("", "24")]
    public void Rejects_invalid_input(string ip, string mask)
    {
        Assert.False(SubnetCalculator.Compute(ip, mask).IsSuccess);
    }

    [Fact]
    public void Rejects_non_contiguous_mask_with_clear_message()
    {
        var result = SubnetCalculator.Compute("192.168.1.1", "255.0.255.0");
        Assert.False(result.IsSuccess);
        Assert.Contains("不连续", result.Error!.Message);
    }

    [Theory]
    [InlineData("10.1.2.3", "私有地址 (10.0.0.0/8)")]
    [InlineData("172.20.0.1", "私有地址 (172.16.0.0/12)")]
    [InlineData("192.168.0.1", "私有地址 (192.168.0.0/16)")]
    [InlineData("127.0.0.1", "环回地址")]
    [InlineData("169.254.1.1", "链路本地地址 (APIPA)")]
    [InlineData("224.0.0.1", "组播地址")]
    [InlineData("8.8.8.8", "公网地址")]
    public void Classifies_addresses(string ip, string expected)
    {
        SubnetCalculator.TryParseAddress(ip, out var address);
        Assert.Equal(expected, SubnetCalculator.ClassifyAddress(address));
    }

    [Fact]
    public void Enumerates_hosts_and_refuses_huge_ranges()
    {
        var info = SubnetCalculator.Compute("192.168.1.10", "24").Value!;
        var hosts = SubnetCalculator.EnumerateHosts(info, 4096);
        Assert.NotNull(hosts);
        Assert.Equal(254, hosts!.Count);
        Assert.Equal("192.168.1.1", hosts[0]);
        Assert.Equal("192.168.1.254", hosts[^1]);

        var huge = SubnetCalculator.Compute("10.0.0.1", "8").Value!;
        Assert.Null(SubnetCalculator.EnumerateHosts(huge, 4096));
    }

    [Fact]
    public void Formats_binary_representation()
    {
        SubnetCalculator.TryParseAddress("192.168.1.10", out var address);
        Assert.Equal("11000000.10101000.00000001.00001010", SubnetCalculator.ToBinary(address));
    }
}
