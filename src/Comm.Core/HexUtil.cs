using System.Text;

namespace Comm.Core;

/// <summary>十六进制文本工具（宽松解析，见规划 4.5：允许空格、逗号、0x 前缀、大小写混用）。</summary>
public static class HexUtil
{
    private const string HexDigits = "0123456789ABCDEF";

    public static string ToHex(ReadOnlySpan<byte> data, string separator = " ")
    {
        if (data.Length == 0) return string.Empty;
        var sb = new StringBuilder(data.Length * (2 + separator.Length));
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(separator);
            sb.Append(HexDigits[data[i] >> 4]).Append(HexDigits[data[i] & 0x0F]);
        }
        return sb.ToString();
    }

    public static string ToAscii(ReadOnlySpan<byte> data, char nonPrintable = '.')
    {
        var sb = new StringBuilder(data.Length);
        foreach (var b in data) sb.Append(b is >= 0x20 and < 0x7F ? (char)b : nonPrintable);
        return sb.ToString();
    }

    /// <summary>
    /// 宽松解析十六进制文本。返回 null 表示解析失败，<paramref name="error"/> 给出原因。
    /// </summary>
    public static byte[]? TryParse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "输入为空";
            return null;
        }

        var nibbles = new List<byte>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is ' ' or '\t' or '\r' or '\n' or ',' or ';' or '-' or ':') continue;
            if (c == '0' && i + 1 < text.Length && (text[i + 1] == 'x' || text[i + 1] == 'X'))
            {
                i++;
                continue;
            }
            int v = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1
            };
            if (v < 0)
            {
                error = $"第 {i + 1} 个字符 '{c}' 不是合法的十六进制字符";
                return null;
            }
            nibbles.Add((byte)v);
        }

        if (nibbles.Count == 0)
        {
            error = "没有可解析的十六进制字符";
            return null;
        }

        if (nibbles.Count % 2 != 0)
        {
            error = "十六进制字符个数为奇数，无法组成完整字节（不自动补位，请补全）";
            return null;
        }

        var bytes = new byte[nibbles.Count / 2];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)((nibbles[i * 2] << 4) | nibbles[i * 2 + 1]);
        return bytes;
    }

    public static byte[] Parse(string text) => TryParse(text, out var e) ?? throw new FormatException(e);
}
