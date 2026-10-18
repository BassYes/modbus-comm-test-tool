using Comm.Core.Codecs;
using Comm.Core.Frames;
using Comm.Plugins.Abstractions;

namespace Comm.Plugins.Host;

/// <summary>插件状态（规划 15.11：加载失败/不兼容/被禁用都不影响主程序启动）。</summary>
public enum PluginState
{
    /// <summary>已发现但尚未加载（例如启动了"仅校验"模式）。</summary>
    NotLoaded,

    /// <summary>已成功加载并完成扩展点注册。</summary>
    Loaded,

    /// <summary>被禁用（用户手动禁用，或连续失败后被自动禁用）。</summary>
    Disabled,

    /// <summary>加载或注册失败（错误码 PLG-7001/PLG-7002）。</summary>
    Failed,

    /// <summary>清单声明的宿主版本范围与当前版本不兼容（PLG-7003）。</summary>
    Incompatible,

    /// <summary>清单非法或入口类型缺失（PLG-7004）。</summary>
    Invalid
}

/// <summary>插件在扩展中心展示的只读信息。</summary>
public sealed record PluginInfo(
    string Id,
    string Name,
    string Version,
    string? Author,
    string? Description,
    string Directory,
    PluginState State,
    string? Error,
    IReadOnlyList<PluginCapability> Capabilities,
    bool MayWrite,
    string? RequiredPrivilege,
    int FailureCount,
    bool AutoDisabled)
{
    public string StateText => State switch
    {
        PluginState.Loaded => "已加载",
        PluginState.Disabled => AutoDisabled ? "已自动禁用（连续失败）" : "已禁用",
        PluginState.Failed => "加载/注册失败",
        PluginState.Incompatible => "版本不兼容",
        PluginState.Invalid => "清单非法",
        _ => "未加载"
    };

    public string CapabilitiesText => Capabilities.Count == 0 ? "-" : string.Join("、", Capabilities);

    public string RiskText => MayWrite ? "可能写设备（使用时会再次确认）" : "只读";
}

/// <summary>一条已注册的扩展点记录（扩展中心展示 + 目录查询）。</summary>
public sealed record PluginRegistration(string PluginId, string Kind, string Name, object Instance);

/// <summary>
/// 插件注册状态存储（规划 15.11：自动禁用的插件"下次启动保持禁用，直至用户手动启用"）。
/// 由宿主定义契约，应用侧用设置文件实现（<c>AppSettings.Plugins.DisabledPlugins</c>）。
/// </summary>
public interface IPluginStateStore
{
    bool IsDisabled(string pluginId);
    void SetDisabled(string pluginId, bool disabled);
    int GetFailureCount(string pluginId);
    void SetFailureCount(string pluginId, int count);
}

/// <summary>内存实现（测试/离线使用；正式程序用设置文件实现）。</summary>
public sealed class InMemoryPluginStateStore : IPluginStateStore
{
    private readonly Dictionary<string, bool> _disabled = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);

    public bool IsDisabled(string pluginId) => _disabled.TryGetValue(pluginId, out var disabled) && disabled;

    public void SetDisabled(string pluginId, bool disabled) => _disabled[pluginId] = disabled;

    public int GetFailureCount(string pluginId) => _failures.TryGetValue(pluginId, out var count) ? count : 0;

    public void SetFailureCount(string pluginId, int count) => _failures[pluginId] = count;
}

/// <summary>扩展点登记表：插件通过 <see cref="IPluginRegistry"/> 注册进来的所有实例（规划 14.7）。</summary>
public sealed class PluginCatalog
{
    public static class Kinds
    {
        public const string FrameCodec = "FrameCodec";
        public const string ChecksumProvider = "ChecksumProvider";
        public const string FrameParser = "FrameParser";
        public const string Exporter = "Exporter";
        public const string Transport = "Transport";
        public const string DeviceDiscovery = "DeviceDiscovery";
        public const string DeviceConfigurator = "DeviceConfigurator";
        public const string DataDecoder = "DataDecoder";
        public const string ScanStrategy = "ScanStrategy";
    }

    private readonly List<PluginRegistration> _registrations = new();
    private readonly List<(string PluginId, string Format, Func<IReadOnlyList<FrameRecord>, string, string> Exporter)> _exporters = new();
    private readonly List<(string PluginId, string Name, Func<object> Factory)> _transportFactories = new();

    public IReadOnlyList<PluginRegistration> Registrations => _registrations;

    public IReadOnlyList<IFrameCodec> FrameCodecs => Instances<IFrameCodec>(Kinds.FrameCodec);

    public IReadOnlyList<IChecksumProvider> ChecksumProviders => Instances<IChecksumProvider>(Kinds.ChecksumProvider);

    public IReadOnlyList<IFrameParser> FrameParsers => Instances<IFrameParser>(Kinds.FrameParser);

    public IReadOnlyList<(string PluginId, string Format, Func<IReadOnlyList<FrameRecord>, string, string> Exporter)> Exporters => _exporters;

    public IReadOnlyList<(string PluginId, string Name, Func<object> Factory)> TransportFactories => _transportFactories;

    public int Count => _registrations.Count;

    /// <summary>是否已经注册过扩展点（用于测试与诊断）。</summary>
    public bool IsEmpty => _registrations.Count == 0;

    /// <summary>按名称查找某个扩展点实例。</summary>
    public T? FindInstance<T>(string name) where T : class =>
        _registrations.FirstOrDefault(r => r.Instance is T && string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Instance as T;

    /// <summary>某个插件注册的全部扩展点。</summary>
    public IReadOnlyList<PluginRegistration> OfPlugin(string pluginId) =>
        _registrations.Where(r => string.Equals(r.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)).ToArray();

    private IReadOnlyList<T> Instances<T>(string kind) =>
        _registrations.Where(r => r.Kind == kind).Select(r => r.Instance).OfType<T>().ToArray();

    internal void Add(PluginRegistration registration) => _registrations.Add(registration);

    internal void AddExporter(string pluginId, string format, Func<IReadOnlyList<FrameRecord>, string, string> exporter) =>
        _exporters.Add((pluginId, format, exporter));

    internal void AddTransportFactory(string pluginId, string name, Func<object> factory) =>
        _transportFactories.Add((pluginId, name, factory));

    internal void RemoveByPlugin(string pluginId)
    {
        _registrations.RemoveAll(r => string.Equals(r.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        _exporters.RemoveAll(e => string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
        _transportFactories.RemoveAll(t => string.Equals(t.PluginId, pluginId, StringComparison.OrdinalIgnoreCase));
    }

    internal void Clear()
    {
        _registrations.Clear();
        _exporters.Clear();
        _transportFactories.Clear();
    }
}

/// <summary>
/// 单个插件的注册作用域：把 <see cref="IPluginRegistry"/> 的调用记到 <see cref="PluginCatalog"/>，
/// 并自动带上插件 Id，便于扩展中心回溯"这个扩展是谁注册的"。
/// </summary>
internal sealed class PluginRegistryScope : IPluginRegistry
{
    private readonly PluginCatalog _catalog;
    private readonly string _pluginId;

    public PluginRegistryScope(PluginCatalog catalog, string pluginId)
    {
        _catalog = catalog;
        _pluginId = pluginId;
    }

    public void AddTransportFactory(string name, Func<object> factory)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("传输名称不能为空", nameof(name));
        ArgumentNullException.ThrowIfNull(factory);

        _catalog.AddTransportFactory(_pluginId, name, factory);
        _catalog.Add(new PluginRegistration(_pluginId, PluginCatalog.Kinds.Transport, name, factory));
    }

    public void AddFrameCodec(IFrameCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        _catalog.Add(new PluginRegistration(_pluginId, PluginCatalog.Kinds.FrameCodec, codec.Name, codec));
    }

    public void AddChecksumProvider(IChecksumProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _catalog.Add(new PluginRegistration(_pluginId, PluginCatalog.Kinds.ChecksumProvider, provider.Name, provider));
    }

    public void AddFrameParser(IFrameParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        _catalog.Add(new PluginRegistration(_pluginId, PluginCatalog.Kinds.FrameParser, parser.Name, parser));
    }

    public void AddExporter(string format, Func<IReadOnlyList<FrameRecord>, string, string> exporter)
    {
        if (string.IsNullOrWhiteSpace(format)) throw new ArgumentException("导出格式名不能为空", nameof(format));
        ArgumentNullException.ThrowIfNull(exporter);

        _catalog.AddExporter(_pluginId, format, exporter);
        _catalog.Add(new PluginRegistration(_pluginId, PluginCatalog.Kinds.Exporter, format, exporter));
    }
}
