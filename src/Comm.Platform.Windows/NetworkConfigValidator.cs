using Comm.Core;
using Comm.Core.Errors;
using Comm.Network;

namespace Comm.Platform.Windows;

/// <summary>校验选项（测试与界面均可调整；默认值对应规划 15.8 第 2 步的硬性检查）。</summary>
public sealed record NetworkConfigValidationOptions
{
    /// <summary>允许把网卡上已存在的同一个地址再写一次（幂等重放，不报错但标记为"无变化"）。</summary>
    public bool AllowSameAddressOnAdapter { get; init; } = true;

    /// <summary>允许使用网络地址/广播地址（默认禁止；/31、/32 自动放行）。</summary>
    public bool AllowNetworkOrBroadcastAddress { get; init; }

    /// <summary>要求网关与新地址在同一网段（默认要求，见规划 15.8）。</summary>
    public bool RequireGatewayInSubnet { get; init; } = true;

    /// <summary>允许公网地址（默认允许，但给出提示——现场偶尔需要临时用公网地址）。</summary>
    public bool AllowPublicAddress { get; init; } = true;
}

/// <summary>校验通过后的规范化结果（界面显示与提交助手都用这一份，避免各处自行解析）。</summary>
public sealed record NetworkConfigValidation
{
    public string Address { get; init; } = string.Empty;
    public int PrefixLength { get; init; }
    public string Mask { get; init; } = string.Empty;
    public string NetworkAddress { get; init; } = string.Empty;
    public string BroadcastAddress { get; init; } = string.Empty;
    public string HostRangeText { get; init; } = string.Empty;
    public string? Gateway { get; init; }
    public string? Dns1 { get; init; }
    public string? Dns2 { get; init; }
    public bool ApplyDns { get; init; }
    public bool ReplaceExistingAddresses { get; init; } = true;

    /// <summary>目标地址与网卡当前配置相同（重放同一方案时不算错误，只是没有变化）。</summary>
    public bool IsNoOp { get; init; }

    /// <summary>非阻断性提示（公网地址、地址与现有地址重复但被允许等）。</summary>
    public string? Warning { get; init; }

    public string SummaryText =>
        $"{Address}/{PrefixLength}（掩码 {Mask}，网络 {NetworkAddress}，广播 {BroadcastAddress}" +
        $"，主机范围 {HostRangeText}）" +
        (string.IsNullOrWhiteSpace(Gateway) ? " · 不修改网关" : $" · 网关 {Gateway}") +
        (ApplyDns ? $" · DNS {Dns1 ?? "-"}{(string.IsNullOrWhiteSpace(Dns2) ? string.Empty : "," + Dns2)}" : " · 不改 DNS");
}

/// <summary>
/// 网卡配置输入校验（纯函数，无 I/O）：格式、掩码连续性、网关同网段、网络/广播地址、与本机已有地址冲突。
/// 与后端（IP Helper / netsh / 提权助手）解耦，可在无管理员权限、无真实网卡的环境下完整测试。
/// </summary>
public static class NetworkConfigValidator
{
    /// <summary>校验一个"应用静态地址"请求。</summary>
    public static Result<NetworkConfigValidation> Validate(
        NetworkConfigRequest request,
        AdapterSnapshot? adapter = null,
        NetworkConfigValidationOptions? options = null)
    {
        options ??= new NetworkConfigValidationOptions();

        if (request.DhcpEnabled)
        {
            return Result<NetworkConfigValidation>.Ok(new NetworkConfigValidation
            {
                Address = "DHCP",
                PrefixLength = 0,
                Mask = "-",
                NetworkAddress = "-",
                BroadcastAddress = "-",
                HostRangeText = "-",
                IsNoOp = adapter?.DhcpEnabled == true,
                Warning = adapter is null ? null : adapter.DhcpEnabled ? "该网卡当前就是 DHCP 模式。" : null
            });
        }

        var addressResult = SubnetCalculator.TryParseAddress(request.Address, out var address);
        if (!addressResult)
        {
            return Fail($"IP 地址格式非法：\"{request.Address}\"", "请输入形如 192.168.1.10 的 IPv4 地址");
        }

        var prefixResult = NormalizePrefix(request.PrefixLength);
        if (!prefixResult.IsSuccess) return Result<NetworkConfigValidation>.Fail(prefixResult.Error!);
        int prefix = prefixResult.Value;
        uint mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

        var info = SubnetCalculator.ComputeSubnet(address, mask, prefix);
        var addressText = SubnetCalculator.Format(address);

        // 网络地址 / 广播地址（/31 点对点与 /32 单主机不需要排除，见规划 4.9.3）
        if (!options.AllowNetworkOrBroadcastAddress && prefix is > 0 and < 31)
        {
            if (address == info.NetworkAddress)
            {
                return Fail($"地址 {addressText} 是该网段的网络地址（{info.NetworkText}），不能配置到网卡上",
                    $"可改用 {info.FirstHostText} ~ {info.LastHostText} 之间的地址");
            }

            if (address == info.BroadcastAddress)
            {
                return Fail($"地址 {addressText} 是该网段的广播地址（{info.BroadcastText}），不能配置到网卡上",
                    $"可改用 {info.FirstHostText} ~ {info.LastHostText} 之间的地址");
            }

            if (address == uint.MaxValue)
            {
                return Fail("255.255.255.255 是受限广播地址，不能配置到网卡上", "请填写一个可用的主机地址");
            }
        }

        string? gateway = null;
        if (!string.IsNullOrWhiteSpace(request.Gateway) && !request.ClearGateway)
        {
            var gatewayParse = SubnetCalculator.TryParseAddress(request.Gateway, out var gatewayAddress);
            if (!gatewayParse)
            {
                return Fail($"默认网关格式非法：\"{request.Gateway}\"", "请填写形如 192.168.1.1 的 IPv4 地址，或留空表示不修改");
            }

            if (options.RequireGatewayInSubnet && !SubnetCalculator.IsSameSubnet(address, gatewayAddress, mask))
            {
                return Fail(
                    $"默认网关 {SubnetCalculator.Format(gatewayAddress)} 与新地址 {addressText}/{prefix} 不在同一网段",
                    $"网关必须落在 {info.NetworkText}/{prefix} 内，例如 {info.FirstHostText}；或先改掩码");
            }

            gateway = SubnetCalculator.Format(gatewayAddress);
        }

        var dns1 = NormalizeOptionalAddress(request.Dns1, "首选 DNS", out var dnsError);
        if (dnsError is not null) return Result<NetworkConfigValidation>.Fail(dnsError);

        var dns2 = NormalizeOptionalAddress(request.Dns2, "备用 DNS", out dnsError);
        if (dnsError is not null) return Result<NetworkConfigValidation>.Fail(dnsError);

        string? warning = null;
        if (!options.AllowPublicAddress && SubnetCalculator.ClassifyAddress(address).Contains("公网"))
        {
            return Fail($"{addressText} 看起来是公网地址，已按当前设置拒绝", "如需临时使用公网地址，请在高级选项中允许");
        }

        if (SubnetCalculator.ClassifyAddress(address).Contains("公网"))
        {
            warning = $"{addressText} 是公网地址，请确认这是有意为之。";
        }

        bool isNoOp = false;
        if (adapter is not null)
        {
            var existing = adapter.Ipv4.FirstOrDefault(a => a.Address == addressText);
            if (existing is not null)
            {
                if (!options.AllowSameAddressOnAdapter)
                {
                    return Fail($"网卡上已存在地址 {addressText}", "请填写其他地址，或先移除该地址");
                }

                isNoOp = existing.PrefixLength == prefix;
                warning = isNoOp
                    ? $"网卡上已存在相同地址 {addressText}/{prefix}，本次操作不会改变配置。"
                    : $"网卡上已存在地址 {addressText}，本次将把前缀长度改为 /{prefix}。";
            }

            if (request.ReplaceExistingAddresses && adapter.Ipv4.Count > 0 && !isNoOp)
            {
                warning = (warning is null ? string.Empty : warning + " ") +
                          $"该操作会替换网卡上现有的 {adapter.Ipv4.Count} 个 IPv4 地址" +
                          $"（{string.Join("、", adapter.Ipv4.Select(a => a.Cidr))}）；如需保留，请改用“添加辅助 IP”。";
            }
        }

        return Result<NetworkConfigValidation>.Ok(new NetworkConfigValidation
        {
            Address = addressText,
            PrefixLength = prefix,
            Mask = info.MaskText,
            NetworkAddress = info.NetworkText,
            BroadcastAddress = info.BroadcastText,
            HostRangeText = info.HostRangeText,
            Gateway = gateway,
            Dns1 = dns1,
            Dns2 = dns2,
            ApplyDns = request.ApplyDns,
            ReplaceExistingAddresses = request.ReplaceExistingAddresses,
            IsNoOp = isNoOp,
            Warning = warning
        });
    }

    /// <summary>校验"添加辅助 IP"（R-13）：只校验地址本身与是否重复，不影响原有地址。</summary>
    public static Result<NetworkConfigValidation> ValidateAuxiliaryAddress(
        string? addressText,
        int prefixLength,
        AdapterSnapshot? adapter = null,
        NetworkConfigValidationOptions? options = null)
    {
        var request = new NetworkConfigRequest
        {
            Address = addressText,
            PrefixLength = prefixLength,
            ReplaceExistingAddresses = false
        };

        var result = Validate(request, adapter: null, options);
        if (!result.IsSuccess) return result;

        var value = result.Value!;
        if (adapter is not null)
        {
            var existing = adapter.Ipv4.FirstOrDefault(a => a.Address == value.Address);
            if (existing is not null)
            {
                // 辅助 IP 幂等：同地址同前缀直接视为"已存在"，不算错误。
                return Result<NetworkConfigValidation>.Ok(value with
                {
                    IsNoOp = existing.PrefixLength == value.PrefixLength,
                    Warning = $"网卡上已存在地址 {existing.Cidr}，无需重复添加。"
                });
            }
        }

        return Result<NetworkConfigValidation>.Ok(value with { ReplaceExistingAddresses = false });
    }

    /// <summary>前缀长度归一化：把 <c>24</c>、<c>/24</c> 之类的写法统一成 0~32。</summary>
    public static Result<int> NormalizePrefix(int prefixLength) => prefixLength switch
    {
        < 0 => Result<int>.Fail(BadPrefix(prefixLength)),
        > 32 => Result<int>.Fail(BadPrefix(prefixLength)),
        _ => Result<int>.Ok(prefixLength)
    };

    /// <summary>解析用户输入的掩码或前缀（<c>24</c> / <c>/24</c> / <c>255.255.255.0</c>）。</summary>
    public static Result<int> ParsePrefixLength(string? maskOrPrefix)
    {
        if (string.IsNullOrWhiteSpace(maskOrPrefix))
        {
            return Result<int>.Fail(BadPrefixText("（空）"));
        }

        if (SubnetCalculator.TryParseMask(maskOrPrefix, out _, out int prefix, out _))
        {
            return Result<int>.Ok(prefix);
        }

        return Result<int>.Fail(BadPrefixText(maskOrPrefix));
    }

    /// <summary>掩码是否连续（<c>255.0.255.0</c> 这类非连续掩码必须拒绝，见规划 16.3.1）。</summary>
    public static bool IsContiguousMask(uint mask)
    {
        uint inverted = ~mask;
        return (inverted & (inverted + 1)) == 0;
    }

    private static string? NormalizeOptionalAddress(string? text, string fieldName, out ErrorInfo? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return null;

        if (!SubnetCalculator.TryParseAddress(text, out var address))
        {
            error = ValidationError($"{fieldName}格式非法：\"{text}\"", "请填写形如 8.8.8.8 的 IPv4 地址，或留空");
            return null;
        }

        return SubnetCalculator.Format(address);
    }

    private static Result<NetworkConfigValidation> Fail(string message, string hint) =>
        Result<NetworkConfigValidation>.Fail(ValidationError(message, hint));

    private static ErrorInfo BadPrefix(int prefix) => ValidationError(
        $"前缀长度 {prefix} 非法（必须在 0 ~ 32 之间）",
        "填 24 表示 255.255.255.0；也可以直接填掩码");

    private static ErrorInfo BadPrefixText(string text) => ValidationError(
        $"掩码或前缀 \"{text}\" 无法解析",
        "支持 24、/24、255.255.255.0 三种写法，且掩码必须连续（255.0.255.0 非法）");

    private static ErrorInfo ValidationError(string message, string hint) => new(
        ErrorCodes.ValidationNetworkConfig,
        ErrorCategory.Validation,
        Severity.Warning,
        message,
        Detail: null,
        Retriable: false,
        Hint: hint);
}
