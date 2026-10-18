using Comm.Core.Errors;
using Comm.Network;
using Comm.Platform.Windows;
using Xunit;

namespace Comm.Platform.Windows.Tests;

/// <summary>
/// 网卡配置输入校验（规划 15.8 第 2 步、4.9.3）。
/// 校验器是纯函数：这里不碰网卡、不碰文件，全部可确定性验证。
/// </summary>
public class NetworkConfigValidatorTests
{
    // ---------------------------------------------------------------- 掩码写法

    [Theory]
    [InlineData("24", 24)]
    [InlineData("/24", 24)]
    [InlineData("255.255.255.0", 24)]
    [InlineData("255.255.255.252", 30)]
    [InlineData("255.255.255.255", 32)]
    public void ParsePrefixLength_accepts_three_notations(string text, int expected)
    {
        var result = NetworkConfigValidator.ParsePrefixLength(text);
        Assert.True(result.IsSuccess);
        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("255.0.255.0")]
    [InlineData("/33")]
    [InlineData("abc")]
    [InlineData("")]
    public void ParsePrefixLength_rejects_invalid_input(string text)
    {
        var result = NetworkConfigValidator.ParsePrefixLength(text);
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationNetworkConfig, result.Error!.Code);
    }

    [Fact]
    public void Non_contiguous_mask_is_rejected()
    {
        // 255.0.255.0 这类非连续掩码必须拒绝（规划 16.3.1）
        Assert.False(NetworkConfigValidator.IsContiguousMask(0xFF00FF00));
        Assert.True(NetworkConfigValidator.IsContiguousMask(0xFFFFFF00));
        Assert.True(NetworkConfigValidator.IsContiguousMask(0x00000000));
        Assert.True(NetworkConfigValidator.IsContiguousMask(0xFFFFFFFF));

        var parsed = SubnetCalculator.TryParseMask("255.0.255.0", out _, out _, out var error);
        Assert.False(parsed);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // ---------------------------------------------------------------- 网络 / 广播地址

    [Theory]
    [InlineData("192.168.1.0")]
    [InlineData("192.168.1.255")]
    public void Network_and_broadcast_addresses_are_rejected(string address)
    {
        var result = NetworkConfigValidator.Validate(new NetworkConfigRequest
        {
            Address = address,
            PrefixLength = 24,
            ReplaceExistingAddresses = true
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationNetworkConfig, result.Error!.Code);
    }

    [Fact]
    public void Network_address_can_be_allowed_by_option()
    {
        var result = NetworkConfigValidator.Validate(
            new NetworkConfigRequest { Address = "192.168.1.0", PrefixLength = 24 },
            options: new NetworkConfigValidationOptions { AllowNetworkOrBroadcastAddress = true });

        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("192.168.1.0", 31)]
    [InlineData("192.168.1.1", 31)]
    [InlineData("192.168.1.255", 32)]
    public void Host_addresses_for_31_and_32_prefixes_are_allowed(string address, int prefix)
    {
        // /31 点对点与 /32 单主机没有"网络地址/广播地址"的概念（规划 4.9.3）
        var result = NetworkConfigValidator.Validate(new NetworkConfigRequest
        {
            Address = address,
            PrefixLength = prefix
        });

        Assert.True(result.IsSuccess);
        Assert.Equal(prefix, result.Value!.PrefixLength);
    }

    // ---------------------------------------------------------------- 网关 / DNS

    [Fact]
    public void Gateway_outside_subnet_is_rejected()
    {
        var result = NetworkConfigValidator.Validate(new NetworkConfigRequest
        {
            Address = "192.168.1.10",
            PrefixLength = 24,
            Gateway = "10.0.0.1"
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationNetworkConfig, result.Error!.Code);
        Assert.Contains("不在同一网段", result.Error.Message);
    }

    [Fact]
    public void Gateway_inside_subnet_is_accepted()
    {
        var result = NetworkConfigValidator.Validate(new NetworkConfigRequest
        {
            Address = "192.168.1.10",
            PrefixLength = 24,
            Gateway = "192.168.1.1"
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("192.168.1.1", result.Value!.Gateway);
        Assert.Equal("255.255.255.0", result.Value.Mask);
        Assert.Equal("192.168.1.0", result.Value.NetworkAddress);
        Assert.Equal("192.168.1.255", result.Value.BroadcastAddress);
    }

    [Fact]
    public void ClearGateway_ignores_gateway_field()
    {
        var result = NetworkConfigValidator.Validate(new NetworkConfigRequest
        {
            Address = "192.168.1.10",
            PrefixLength = 24,
            Gateway = "10.0.0.1",
            ClearGateway = true
        });

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Gateway);
    }

    [Theory]
    [InlineData("999.1.1.1")]
    [InlineData("8.8.8")]
    [InlineData("dns")]
    public void Invalid_dns_is_rejected(string dns)
    {
        var result = NetworkConfigValidator.Validate(new NetworkConfigRequest
        {
            Address = "192.168.1.10",
            PrefixLength = 24,
            ApplyDns = true,
            Dns1 = dns
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("DNS", result.Error!.Message);
    }

    // ---------------------------------------------------------------- 幂等 / 提示

    [Fact]
    public void Replaying_same_address_is_no_op()
    {
        var adapter = FakeAdapterBackend.StaticAdapter(address: "192.168.1.50", prefixLength: 24);

        var result = NetworkConfigValidator.Validate(new NetworkConfigRequest
        {
            Address = "192.168.1.50",
            PrefixLength = 24,
            Gateway = "192.168.1.1"
        }, adapter);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsNoOp);
        Assert.Contains("不会改变配置", result.Value.Warning);
    }

    [Fact]
    public void Same_address_with_different_prefix_is_not_no_op()
    {
        var adapter = FakeAdapterBackend.StaticAdapter(address: "192.168.1.50", prefixLength: 24);

        var result = NetworkConfigValidator.Validate(new NetworkConfigRequest
        {
            Address = "192.168.1.50",
            PrefixLength = 25
        }, adapter);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsNoOp);
        Assert.Contains("前缀长度", result.Value.Warning);
    }

    [Fact]
    public void Dhcp_request_is_no_op_when_adapter_already_dhcp()
    {
        var adapter = FakeAdapterBackend.DhcpAdapter();

        var result = NetworkConfigValidator.Validate(new NetworkConfigRequest { DhcpEnabled = true }, adapter);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsNoOp);
        Assert.Equal("DHCP", result.Value.Address);
    }

    [Fact]
    public void Public_address_produces_warning()
    {
        var result = NetworkConfigValidator.Validate(new NetworkConfigRequest
        {
            Address = "8.8.4.4",
            PrefixLength = 24
        });

        Assert.True(result.IsSuccess);
        Assert.Contains("公网", result.Value!.Warning);
    }

    [Fact]
    public void Public_address_can_be_refused_by_option()
    {
        var result = NetworkConfigValidator.Validate(
            new NetworkConfigRequest { Address = "8.8.4.4", PrefixLength = 24 },
            options: new NetworkConfigValidationOptions { AllowPublicAddress = false });

        Assert.False(result.IsSuccess);
        Assert.Contains("公网", result.Error!.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(33)]
    public void Out_of_range_prefix_is_rejected(int prefix)
    {
        var result = NetworkConfigValidator.Validate(new NetworkConfigRequest
        {
            Address = "192.168.1.10",
            PrefixLength = prefix
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCodes.ValidationNetworkConfig, result.Error!.Code);
    }

    [Fact]
    public void Replacing_existing_addresses_produces_warning()
    {
        var adapter = FakeAdapterBackend.StaticAdapter(address: "192.168.1.50");

        var result = NetworkConfigValidator.Validate(new NetworkConfigRequest
        {
            Address = "192.168.1.60",
            PrefixLength = 24,
            ReplaceExistingAddresses = true
        }, adapter);

        Assert.True(result.IsSuccess);
        Assert.Contains("替换", result.Value!.Warning);
    }

    // ---------------------------------------------------------------- 辅助 IP（R-13）

    [Fact]
    public void Auxiliary_address_duplicate_is_no_op()
    {
        var adapter = FakeAdapterBackend.StaticAdapter(address: "192.168.1.50");

        var result = NetworkConfigValidator.ValidateAuxiliaryAddress("192.168.1.50", 24, adapter);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value!.IsNoOp);
        Assert.False(result.Value.ReplaceExistingAddresses);
    }

    [Fact]
    public void Auxiliary_address_new_value_keeps_existing_addresses()
    {
        var adapter = FakeAdapterBackend.StaticAdapter(address: "192.168.1.50");

        var result = NetworkConfigValidator.ValidateAuxiliaryAddress("192.168.2.10", 24, adapter);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value!.IsNoOp);
        Assert.False(result.Value.ReplaceExistingAddresses);
        Assert.Equal("192.168.2.10", result.Value.Address);
    }
}
