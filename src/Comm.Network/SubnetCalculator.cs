using System.Globalization;
using Comm.Core;
using Comm.Core.Errors;

namespace Comm.Network;

/// <summary>子网计算结果（见规划 4.9.2）。</summary>
public sealed record Ipv4SubnetInfo(
    uint Address,
    uint Mask,
    int PrefixLength,
    uint NetworkAddress,
    uint BroadcastAddress,
    uint FirstHost,
    uint LastHost,
    long TotalAddresses,
    long UsableHosts,
    bool IsPointToPoint,
    bool IsSingleHost)
{
    public string AddressText => SubnetCalculator.Format(Address);
    public string MaskText => SubnetCalculator.Format(Mask);
    public string WildcardText => SubnetCalculator.Format(~Mask);
    public string NetworkText => SubnetCalculator.Format(NetworkAddress);
    public string BroadcastText => SubnetCalculator.Format(BroadcastAddress);
    public string FirstHostText => SubnetCalculator.Format(FirstHost);
    public string LastHostText => SubnetCalculator.Format(LastHost);
    public string Cidr => $"{NetworkText}/{PrefixLength}";
    public string AddressBinary => SubnetCalculator.ToBinary(Address);
    public string MaskBinary => SubnetCalculator.ToBinary(Mask);
    public string AddressKind => SubnetCalculator.ClassifyAddress(Address);

    public string HostRangeText => IsSingleHost
        ? NetworkText
        : $"{FirstHostText} - {LastHostText}";
}

/// <summary>IPv4 子网计算（纯函数，见规划 4.9）。</summary>
public static class SubnetCalculator
{
    public static string Format(uint address) =>
        $"{(address >> 24) & 0xFF}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}";

    public static string ToBinary(uint address)
    {
        var parts = new string[4];
        for (int i = 0; i < 4; i++)
        {
            parts[i] = Convert.ToString((address >> (8 * (3 - i))) & 0xFF, 2).PadLeft(8, '0');
        }
        return string.Join('.', parts);
    }

    public static bool TryParseAddress(string? text, out uint address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Trim().Split('.');
        if (parts.Length != 4) return false;

        uint result = 0;
        foreach (var part in parts)
        {
            if (!byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return false;
            result = (result << 8) | value;
        }
        address = result;
        return true;
    }

    /// <summary>解析子网掩码：支持 "255.255.255.0"、"24"、"/24"；要求掩码位连续。</summary>
    public static bool TryParseMask(string? text, out uint mask, out int prefixLength, out string? error)
    {
        mask = 0;
        prefixLength = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "子网掩码为空";
            return false;
        }

        var trimmed = text.Trim().TrimStart('/');

        if (int.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
        {
            if (prefix is < 0 or > 32)
            {
                error = "前缀长度必须在 0 ~ 32 之间";
                return false;
            }
            prefixLength = prefix;
            mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            return true;
        }

        if (!TryParseAddress(trimmed, out mask))
        {
            error = "子网掩码格式不正确，应形如 255.255.255.0 或 24";
            return false;
        }

        // 校验掩码位是否连续
        uint inverted = ~mask;
        if ((inverted & (inverted + 1)) != 0)
        {
            error = "子网掩码位不连续（非法的掩码）";
            return false;
        }

        prefixLength = 0;
        uint probe = mask;
        while (probe != 0)
        {
            prefixLength++;
            probe <<= 1;
        }
        return true;
    }

    public static Result<Ipv4SubnetInfo> Compute(string ipText, string maskText)
    {
        if (!TryParseAddress(ipText, out var address))
        {
            return Result<Ipv4SubnetInfo>.Fail(new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation,
                Severity.Warning, "IP 地址格式不正确，应形如 192.168.1.10", Hint: "检查是否缺少段或多出字符"));
        }

        if (!TryParseMask(maskText, out var mask, out var prefix, out var error))
        {
            return Result<Ipv4SubnetInfo>.Fail(new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation,
                Severity.Warning, error ?? "子网掩码不正确"));
        }

        return Result<Ipv4SubnetInfo>.Ok(ComputeSubnet(address, mask, prefix));
    }

    public static Ipv4SubnetInfo ComputeSubnet(uint address, uint mask, int prefixLength)
    {
        uint network = address & mask;
        uint broadcast = network | ~mask;
        long total = 1L << (32 - prefixLength);

        bool singleHost = prefixLength == 32;
        bool pointToPoint = prefixLength == 31;

        long usable;
        uint firstHost;
        uint lastHost;

        if (singleHost)
        {
            usable = 1;
            firstHost = network;
            lastHost = network;
        }
        else if (pointToPoint)
        {
            usable = 2;
            firstHost = network;
            lastHost = broadcast;
        }
        else
        {
            usable = total - 2;
            firstHost = network + 1;
            lastHost = broadcast - 1;
        }

        return new Ipv4SubnetInfo(address, mask, prefixLength, network, broadcast, firstHost, lastHost, total, usable,
            pointToPoint, singleHost);
    }

    public static bool IsSameSubnet(uint a, uint b, uint mask) => (a & mask) == (b & mask);

    public static Result<bool> CheckSameSubnet(string ip1, string ip2, string maskText)
    {
        if (!TryParseAddress(ip1, out var a))
            return Result<bool>.Fail(new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning, "第一个 IP 地址格式不正确"));
        if (!TryParseAddress(ip2, out var b))
            return Result<bool>.Fail(new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning, "第二个 IP 地址格式不正确"));
        if (!TryParseMask(maskText, out var mask, out _, out var error))
            return Result<bool>.Fail(new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation, Severity.Warning, error ?? "子网掩码不正确"));

        return Result<bool>.Ok(IsSameSubnet(a, b, mask));
    }

    public static string ClassifyAddress(uint address)
    {
        byte a = (byte)(address >> 24);
        byte b = (byte)(address >> 16);

        if (a == 10) return "私有地址 (10.0.0.0/8)";
        if (a == 172 && b is >= 16 and <= 31) return "私有地址 (172.16.0.0/12)";
        if (a == 192 && b == 168) return "私有地址 (192.168.0.0/16)";
        if (a == 127) return "环回地址";
        if (a == 169 && b == 254) return "链路本地地址 (APIPA)";
        if (a is >= 224 and <= 239) return "组播地址";
        if (a >= 240) return "保留地址";
        if (address == 0) return "未指定地址";
        return "公网地址";
    }

    /// <summary>枚举可扫描主机地址；超过上限时返回 null，由界面提示（见规划 4.9.3）。</summary>
    public static IReadOnlyList<string>? EnumerateHosts(Ipv4SubnetInfo info, int maxCount = 4096)
    {
        if (info.UsableHosts > maxCount || info.UsableHosts > int.MaxValue) return null;

        var list = new List<string>((int)info.UsableHosts);
        for (uint addr = info.FirstHost; addr <= info.LastHost; addr++)
        {
            list.Add(Format(addr));
            if (addr == uint.MaxValue) break;
        }
        return list;
    }

    public static string EstimatedScanTimeText(Ipv4SubnetInfo info, int concurrency = 64, int perHostMs = 800)
    {
        double seconds = info.TotalAddresses * (double)perHostMs / Math.Max(1, concurrency) / 1000.0;
        return seconds < 60 ? $"约 {seconds:F1} 秒" : $"约 {seconds / 60:F1} 分钟";
    }
}
