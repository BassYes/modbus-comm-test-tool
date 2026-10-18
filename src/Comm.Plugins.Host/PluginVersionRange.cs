namespace Comm.Plugins.Host;

/// <summary>
/// 插件声明的宿主版本范围（规划 8.2、12.8）。
/// 支持：<c>*</c> / 空（任意）、<c>&gt;=1.0.0</c>、<c>&gt;1.0.0</c>、<c>&lt;=2.0.0</c>、<c>&lt;2.0.0</c>、
/// <c>=1.2.3</c>、裸版本 <c>1.2.3</c>（等价于精确匹配）、以及空格分隔的多个条件（同时满足）。
/// </summary>
public sealed class PluginVersionRange
{
    private readonly List<(string Operator, Version Version)> _conditions = new();

    private PluginVersionRange(string text)
    {
        Text = text;
    }

    /// <summary>原始文本（界面显示用）。</summary>
    public string Text { get; }

    /// <summary>是否任何版本都满足（<c>*</c> 或空）。</summary>
    public bool IsAny => _conditions.Count == 0;

    public static bool TryParse(string? text, out PluginVersionRange? range, out string? error)
    {
        range = null;
        error = null;

        var trimmed = (text ?? string.Empty).Trim();
        if (trimmed.Length == 0 || trimmed == "*")
        {
            range = new PluginVersionRange("*");
            return true;
        }

        var parsed = new PluginVersionRange(trimmed);
        foreach (var token in trimmed.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string op;
            string versionText;

            if (token.StartsWith(">=", StringComparison.Ordinal)) (op, versionText) = (">=", token[2..]);
            else if (token.StartsWith("<=", StringComparison.Ordinal)) (op, versionText) = ("<=", token[2..]);
            else if (token.StartsWith(">", StringComparison.Ordinal)) (op, versionText) = (">", token[1..]);
            else if (token.StartsWith("<", StringComparison.Ordinal)) (op, versionText) = ("<", token[1..]);
            else if (token.StartsWith("=", StringComparison.Ordinal)) (op, versionText) = ("=", token[1..]);
            else (op, versionText) = ("=", token);

            if (!Version.TryParse(versionText.Trim(), out var version))
            {
                error = $"无法解析版本号 \"{versionText}\"（应形如 1.2.3）";
                return false;
            }

            parsed._conditions.Add((op, Normalize(version)));
        }

        range = parsed;
        return true;
    }

    /// <summary>解析失败时抛异常（供测试与内部使用；界面路径请用 <see cref="TryParse"/> 表达预期失败）。</summary>
    public static PluginVersionRange Parse(string? text)
    {
        if (!TryParse(text, out var range, out var error)) throw new ArgumentException(error, nameof(text));
        return range!;
    }

    public bool IsSatisfiedBy(Version version)
    {
        if (IsAny) return true;

        var candidate = Normalize(version);
        foreach (var (op, required) in _conditions)
        {
            int compare = candidate.CompareTo(required);
            bool ok = op switch
            {
                ">=" => compare >= 0,
                ">" => compare > 0,
                "<=" => compare <= 0,
                "<" => compare < 0,
                _ => compare == 0
            };

            if (!ok) return false;
        }

        return true;
    }

    /// <summary>把 <c>1.2</c> 之类的版本补齐为四段，避免 <see cref="Version.CompareTo(Version)"/> 的段数差异。</summary>
    private static Version Normalize(Version version) => new(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        version.Build < 0 ? 0 : version.Build,
        version.Revision < 0 ? 0 : version.Revision);

    public override string ToString() => Text;
}
