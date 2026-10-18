namespace Comm.Scanning;

/// <summary>
/// 解析“地址列表”文本，支持单值、逗号/分号/空格分隔与区间（如 <c>1-10,247</c>）。
/// 用于设备扫描页的“阶段 A 探测地址”等输入（R-11）。
/// </summary>
public static class AddressListParser
{
    private static readonly char[] Separators = { ',', '，', ';', '；', ' ', '\t', '\n', '\r' };

    /// <summary>解析结果只保留 1~247（Modbus 从站地址范围）；无有效项时返回 null。</summary>
    public static IReadOnlyList<byte>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var result = new List<byte>();
        foreach (var part in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;

            var dash = trimmed.IndexOf('-');
            if (dash > 0
                && byte.TryParse(trimmed[..dash], out var from)
                && byte.TryParse(trimmed[(dash + 1)..], out var to))
            {
                for (int address = Math.Min(from, to); address <= Math.Max(from, to); address++)
                {
                    if (address is >= 1 and <= 247) result.Add((byte)address);
                }
                continue;
            }

            if (byte.TryParse(trimmed, out var single) && single is >= 1 and <= 247) result.Add(single);
        }

        return result.Count > 0 ? result.Distinct().ToArray() : null;
    }
}
