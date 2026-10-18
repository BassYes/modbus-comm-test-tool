using Comm.Core.Codecs;
using Comm.Core.Frames;
using System.Text.Json.Serialization;

namespace Comm.Plugins.Abstractions;

/// <summary>
/// 插件清单（见规划 8.2）。宿主据此判断兼容性、权限与风险。
/// </summary>
public sealed record PluginManifest
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = "0.1.0";
    public string? Author { get; init; }
    public string? Description { get; init; }

    /// <summary>兼容的主程序版本范围，如 ">=0.1.0"。</summary>
    public string HostVersionRange { get; init; } = ">=0.1.0";

    public IReadOnlyList<PluginCapability> Capabilities { get; init; } = Array.Empty<PluginCapability>();

    /// <summary>可能执行写操作（会触发额外的用户确认提示）。</summary>
    public bool MayWrite { get; init; }

    /// <summary>需要的系统权限描述，如 "网卡配置"。</summary>
    public string? RequiredPrivilege { get; init; }

    /// <summary>
    /// 入口程序集文件名（相对插件目录），如 <c>Comm.Plugin.Sample.dll</c>（M3b，规划 AD-13）。
    /// 缺省时宿主会在插件目录中查找唯一的 <see cref="ICommPlugin"/> 实现。
    /// </summary>
    [JsonPropertyName("entry")]
    public string? EntryAssembly { get; init; }

    /// <summary>入口类型全名（可选）；缺省时按 <see cref="ICommPlugin"/> 自动查找。</summary>
    [JsonPropertyName("type")]
    public string? EntryType { get; init; }
}

public enum PluginCapability
{
    Transport,
    FrameCodec,
    ProtocolCodec,
    FrameParser,
    ChecksumProvider,
    DeviceDiscovery,
    DeviceConfigurator,
    DataDecoder,
    ScanStrategy,
    ExportFormat,
    Automation
}

/// <summary>插件入口。v0.1 只定义契约，宿主实现在 M3 提供（见规划 8.1、14.7）。</summary>
public interface ICommPlugin
{
    PluginManifest Manifest { get; }

    /// <summary>注册扩展点。宿主在加载后调用一次。</summary>
    void Register(IPluginRegistry registry);
}

/// <summary>扩展点注册表：宿主实现，插件调用。</summary>
public interface IPluginRegistry
{
    void AddTransportFactory(string name, Func<object> factory);
    void AddFrameCodec(IFrameCodec codec);
    void AddChecksumProvider(IChecksumProvider provider);
    void AddFrameParser(IFrameParser parser);
    void AddExporter(string format, Func<IReadOnlyList<FrameRecord>, string, string> exporter);
}

/// <summary>报文解析器（旁路，见规划 14.1、14.7）。</summary>
public interface IFrameParser
{
    string Name { get; }

    /// <summary>能否解析该帧。</summary>
    bool CanParse(FrameRecord record);

    /// <summary>返回字段树文本；无法解析时返回 null。</summary>
    string? Parse(FrameRecord record);
}

/// <summary>厂商设备发现与网络配置（见规划 4.8.5），v0.1 仅预留契约。</summary>
public interface IDeviceDiscoveryProvider
{
    string Name { get; }
    bool Supports(string ipAddress);
}

public interface IDeviceConfigurator
{
    string Name { get; }
    string? RequiredPrivilege { get; }
}
