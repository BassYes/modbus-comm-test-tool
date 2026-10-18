using Comm.Core;
using Comm.Core.Errors;

namespace Comm.Network.Discovery;

/// <summary>
/// 局域网扫描范围解析（见规划 4.7.1）：把字符串范围 / IP+掩码 / 起止地址统一变成 IP 列表。
/// 超过上限时**不展开完整列表**，直接返回 <c>VAL-4007</c>，由界面提示并要求用户确认（规划 4.7.1）。
/// </summary>
public static class LanScanRange
{
    /// <summary>按参数优先级构建范围：起止地址 → IP + 掩码 → 字符串范围。</summary>
    public static Result<List<string>> Build(LanScanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.StartIp) || !string.IsNullOrWhiteSpace(options.EndIp))
        {
            return BuildRange(options.StartIp, options.EndIp, options.MaxAddresses);
        }

        if (!string.IsNullOrWhiteSpace(options.IpAddress) && !string.IsNullOrWhiteSpace(options.MaskText))
        {
            return Build(options.IpAddress, options.MaskText, options.MaxAddresses);
        }

        if (string.IsNullOrWhiteSpace(options.CidrOrRange))
        {
            return Fail("扫描范围为空", "请填写网段（如 192.168.1.0/24）或起止地址");
        }

        return BuildText(options.CidrOrRange!, options.MaxAddresses);
    }

    /// <summary>由 IP + 子网掩码（或前缀）构建可扫描主机范围。</summary>
    public static Result<List<string>> Build(string ip, string mask, int maxAddresses)
    {
        var computed = SubnetCalculator.Compute(ip, mask);
        if (!computed.IsSuccess) return Fail(computed.Error?.Message ?? "IP 地址或子网掩码不正确");

        var hosts = SubnetCalculator.EnumerateHosts(computed.Value!, Math.Max(1, maxAddresses));
        if (hosts is null)
        {
            return Fail($"该网段包含 {computed.Value!.TotalAddresses} 个地址，可用主机 {computed.Value.UsableHosts} 个，超过上限 {maxAddresses} 个",
                "请缩小掩码范围（例如改用 /24）或提高地址数量上限");
        }

        return Result<List<string>>.Ok(hosts.ToList());
    }

    private static Result<List<string>> BuildText(string text, int maxAddresses)
    {
        var trimmed = text.Trim();

        int slash = trimmed.IndexOf('/');
        if (slash > 0)
        {
            return Build(trimmed[..slash].Trim(), trimmed[(slash + 1)..].Trim(), maxAddresses);
        }

        int dash = trimmed.IndexOf('-');
        if (dash > 0)
        {
            var start = trimmed[..dash].Trim();
            var end = trimmed[(dash + 1)..].Trim();

            // 支持 192.168.1.10-50 这种只写末段的简写。
            if (SubnetCalculator.TryParseAddress(start, out _) && !end.Contains('.') &&
                int.TryParse(end, out var lastOctet) && lastOctet is >= 0 and <= 255)
            {
                var parts = start.Split('.');
                end = $"{parts[0]}.{parts[1]}.{parts[2]}.{lastOctet}";
            }

            return BuildRange(start, end, maxAddresses);
        }

        if (SubnetCalculator.TryParseAddress(trimmed, out _)) return BuildRange(trimmed, trimmed, maxAddresses);

        return Fail($"无法识别的范围“{text}”", "请使用 192.168.1.0/24、192.168.1.10-192.168.1.50 或 192.168.1.10-50 形式");
    }

    private static Result<List<string>> BuildRange(string? startText, string? endText, int maxAddresses)
    {
        if (!SubnetCalculator.TryParseAddress(startText, out var start))
        {
            return Fail($"起始地址“{startText}”格式不正确");
        }

        if (!SubnetCalculator.TryParseAddress(endText, out var end))
        {
            return Fail($"结束地址“{endText}”格式不正确");
        }

        if (start > end) (start, end) = (end, start);

        ulong count = (ulong)end - start + 1;
        if (maxAddresses > 0 && count > (ulong)maxAddresses)
        {
            return Fail($"范围包含 {count} 个地址，超过上限 {maxAddresses} 个",
                "请缩小扫描范围或提高地址数量上限");
        }

        var list = new List<string>((int)count);
        for (uint value = start; ; value++)
        {
            list.Add(SubnetCalculator.Format(value));
            if (value == end) break;
        }
        return Result<List<string>>.Ok(list);
    }

    private static Result<List<string>> Fail(string message, string? hint = null) =>
        Result<List<string>>.Fail(new ErrorInfo(ErrorCodes.ValidationNetworkRange, ErrorCategory.Validation,
            Severity.Warning, message, Hint: hint ?? "请检查 IP、掩码与扫描范围"));
}
