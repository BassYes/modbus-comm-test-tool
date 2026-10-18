using System.Text.Json;
using System.Text.Json.Serialization;
using Comm.Core.Errors;
using Comm.Plugins.Abstractions;

namespace Comm.Plugins.Host;

/// <summary>清单读取结果：成功时带清单，失败时带错误码与可读原因（供扩展中心显示）。</summary>
public sealed record PluginManifestReadResult(PluginManifest? Manifest, string? ErrorCode, string? Error)
{
    /// <summary>是否可以加载。注意：不兼容（PLG-7003）时 <see cref="Manifest"/> 仍会带回来，
    /// 这样扩展中心能显示插件的真实 Id/Name/版本，而不是退化成目录名。</summary>
    public bool IsSuccess => Manifest is not null && ErrorCode is null;
}

/// <summary>
/// 读取并校验插件清单 <c>plugin.json</c>（规划 8.2、15.11）。
/// 校验项：JSON 可解析、Id/Name 必填、Version 可解析、HostVersionRange 与当前宿主版本兼容。
/// </summary>
public static class PluginManifestReader
{
    public const string ManifestFileName = "plugin.json";

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>读取插件目录下的清单。目录不存在、缺清单、内容非法都返回可读原因，不抛异常。</summary>
    public static PluginManifestReadResult Read(string pluginDirectory, Version hostVersion)
    {
        var manifestPath = System.IO.Path.Combine(pluginDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return new PluginManifestReadResult(null, ErrorCodes.PluginManifestInvalid,
                $"缺少插件清单 {ManifestFileName}");
        }

        PluginManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), Options);
        }
        catch (Exception ex)
        {
            return new PluginManifestReadResult(null, ErrorCodes.PluginManifestInvalid,
                $"插件清单无法解析：{ex.Message}");
        }

        if (manifest is null)
        {
            return new PluginManifestReadResult(null, ErrorCodes.PluginManifestInvalid, "插件清单内容为空");
        }

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            return new PluginManifestReadResult(null, ErrorCodes.PluginManifestInvalid, "插件清单缺少 id");
        }

        if (string.IsNullOrWhiteSpace(manifest.Name))
        {
            return new PluginManifestReadResult(null, ErrorCodes.PluginManifestInvalid, $"插件 {manifest.Id} 缺少 name");
        }

        if (!Version.TryParse(manifest.Version, out _))
        {
            return new PluginManifestReadResult(null, ErrorCodes.PluginManifestInvalid,
                $"插件 {manifest.Id} 的 version \"{manifest.Version}\" 无法解析（应形如 1.0.0）");
        }

        if (!PluginVersionRange.TryParse(manifest.HostVersionRange, out var range, out var rangeError))
        {
            return new PluginManifestReadResult(null, ErrorCodes.PluginManifestInvalid,
                $"插件 {manifest.Id} 的 hostVersionRange 非法：{rangeError}");
        }

        if (!range!.IsSatisfiedBy(hostVersion))
        {
            return new PluginManifestReadResult(manifest, ErrorCodes.PluginIncompatible,
                $"插件 {manifest.Id} 需要主程序 {manifest.HostVersionRange}，当前为 {hostVersion}（已跳过加载）");
        }

        return new PluginManifestReadResult(manifest, null, null);
    }

    /// <summary>目录是否是插件目录（存在 plugin.json）。</summary>
    public static bool LooksLikePlugin(string directory) => File.Exists(System.IO.Path.Combine(directory, ManifestFileName));
}
