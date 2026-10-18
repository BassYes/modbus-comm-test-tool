using System.Reflection;
using Comm.Core.Errors;
using Comm.Plugins.Abstractions;

namespace Comm.Plugins.Host;

/// <summary>
/// 插件宿主（M3b R-10，规划 8.1/8.2、13.57、14.7、15.11；AD-13）。
/// <para>
/// 职责：在**受信目录**中按 <c>&lt;plugins&gt;/*/plugin.json</c> 发现插件 → 校验清单与兼容性 →
/// 用独立 <see cref="PluginLoadContext"/> 加载 → 调用 <see cref="ICommPlugin.Register"/> 注册扩展点 →
/// 把失败/不兼容/禁用状态呈现给扩展中心，并保证**任何插件故障都不影响主程序启动与其他插件**。
/// </para>
/// <para>
/// 故障隔离（规划 15.11）：注册调用有超时（默认 5 s）与异常边界；单插件连续失败 3 次自动禁用，
/// 且禁用状态由 <see cref="IPluginStateStore"/> 持久化，"下次启动保持禁用直至用户手动启用"。
/// </para>
/// </summary>
public sealed class PluginHost : IDisposable
{
    /// <summary>自动禁用阈值（规划 15.11：单个插件连续失败默认 3 次后自动禁用）。</summary>
    public const int AutoDisableFailureThreshold = 3;

    private readonly Dictionary<string, PluginInfo> _infos = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LoadedPlugin> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private readonly IPluginStateStore _stateStore;
    private readonly Version _hostVersion;
    private readonly int _registerTimeoutMs;
    private readonly Action<string, string, string?>? _log;
    private bool _disposed;

    public PluginHost(
        string pluginDirectory,
        IPluginStateStore stateStore,
        Version hostVersion,
        int registerTimeoutMs = 5000,
        Action<string, string, string?>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginDirectory);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(hostVersion);

        PluginDirectory = System.IO.Path.GetFullPath(pluginDirectory);
        _stateStore = stateStore;
        _hostVersion = hostVersion;
        _registerTimeoutMs = Math.Clamp(registerTimeoutMs, 200, 60000);
        _log = log;
    }

    /// <summary>受信插件目录（规划 AD-13：只加载此目录下的插件）。</summary>
    public string PluginDirectory { get; }

    /// <summary>已注册的扩展点（宿主把插件的解析器/导出器等交给上层使用）。</summary>
    public PluginCatalog Catalog { get; } = new();

    /// <summary>插件列表变化（扩展中心刷新）。</summary>
    public event EventHandler? Changed;

    /// <summary>按目录名排序的全部插件（含失败/禁用/不兼容）。</summary>
    public IReadOnlyList<PluginInfo> Plugins => _infos.Values
        .OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public int LoadedCount => _infos.Values.Count(i => i.State == PluginState.Loaded);

    /// <summary>刷新并按需加载全部插件（可重复调用；已加载的不重复注册）。</summary>
    public void LoadAll()
    {
        foreach (var directory in DiscoverDirectories())
        {
            var manifest = PluginManifestReader.Read(directory, _hostVersion);

            if (!manifest.IsSuccess)
            {
                var id = manifest.Manifest?.Id ?? System.IO.Path.GetFileName(directory);
                _log?.Invoke("Plugins", $"跳过插件目录 {System.IO.Path.GetFileName(directory)}：{manifest.Error}", null);
                Record(new PluginInfo(
                    Id: id,
                    Name: manifest.Manifest?.Name ?? id,
                    Version: manifest.Manifest?.Version ?? "-",
                    Author: null,
                    Description: null,
                    Directory: directory,
                    State: manifest.ErrorCode == ErrorCodes.PluginIncompatible ? PluginState.Incompatible : PluginState.Invalid,
                    Error: $"[{manifest.ErrorCode}] {manifest.Error}",
                    Capabilities: manifest.Manifest?.Capabilities ?? Array.Empty<PluginCapability>(),
                    MayWrite: manifest.Manifest?.MayWrite ?? false,
                    RequiredPrivilege: manifest.Manifest?.RequiredPrivilege,
                    FailureCount: 0,
                    AutoDisabled: false));
                continue;
            }

            var parsed = manifest.Manifest!;
            if (_infos.TryGetValue(parsed.Id, out var already) && already.State == PluginState.Loaded)
            {
                // 已加载的不重复注册（LoadAll 可安全重复调用）。
                continue;
            }

            LoadOne(directory, parsed);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>只校验清单与兼容性，不加载（扩展中心可先预览再启用）。</summary>
    public IReadOnlyList<PluginInfo> ValidateAll()
    {
        var result = new List<PluginInfo>();
        foreach (var directory in DiscoverDirectories())
        {
            var manifest = PluginManifestReader.Read(directory, _hostVersion);
            if (manifest.IsSuccess)
            {
                var m = manifest.Manifest!;
                result.Add(new PluginInfo(m.Id, m.Name, m.Version, m.Author, m.Description, directory,
                    PluginState.NotLoaded, null, m.Capabilities, m.MayWrite, m.RequiredPrivilege,
                    _stateStore.GetFailureCount(m.Id), false));
            }
            else
            {
                var id = System.IO.Path.GetFileName(directory);
                result.Add(new PluginInfo(id, id, "-", null, null, directory,
                    manifest.ErrorCode == ErrorCodes.PluginIncompatible ? PluginState.Incompatible : PluginState.Invalid,
                    $"[{manifest.ErrorCode}] {manifest.Error}", Array.Empty<PluginCapability>(),
                    false, null, 0, false));
            }
        }

        return result;
    }

    /// <summary>启用（或重新加载）指定插件：清除禁用状态与失败计数后重新注册。</summary>
    public PluginInfo? Reload(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return null;

        var directory = _infos.TryGetValue(pluginId, out var current)
            ? current.Directory
            : DiscoverDirectories().FirstOrDefault(d => PluginManifestReader.LooksLikePlugin(d) &&
                string.Equals(PluginManifestReader.Read(d, _hostVersion).Manifest?.Id, pluginId, StringComparison.OrdinalIgnoreCase));

        if (directory is null) return null;

        Unload(pluginId);
        _stateStore.SetDisabled(pluginId, false);
        _stateStore.SetFailureCount(pluginId, 0);

        var manifest = PluginManifestReader.Read(directory, _hostVersion);
        if (!manifest.IsSuccess)
        {
            var id = System.IO.Path.GetFileName(directory);
            Record(new PluginInfo(id, id, "-", null, null, directory, PluginState.Invalid,
                $"[{manifest.ErrorCode}] {manifest.Error}", Array.Empty<PluginCapability>(), false, null, 0, false));
            Changed?.Invoke(this, EventArgs.Empty);
            return _infos.TryGetValue(id, out var invalid) ? invalid : null;
        }

        LoadOne(directory, manifest.Manifest!);
        Changed?.Invoke(this, EventArgs.Empty);
        return _infos.TryGetValue(manifest.Manifest!.Id, out var info) ? info : null;
    }

    /// <summary>禁用插件：卸载并持久化禁用状态（下次启动不再加载，直到用户手动启用）。</summary>
    public void Disable(string pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return;

        Unload(pluginId);
        _stateStore.SetDisabled(pluginId, true);

        if (_infos.TryGetValue(pluginId, out var info))
        {
            Record(info with { State = PluginState.Disabled, Error = null, AutoDisabled = false });
        }

        _log?.Invoke("Plugins", $"已禁用插件：{pluginId}", null);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>宿主退出：卸载所有插件上下文（尽力而为；ALC 不是沙箱，卸载失败不影响退出）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var id in _loaded.Keys.ToArray()) Unload(id);
        Catalog.Clear();
    }

    // ---------------------------------------------------------------------------------------
    // 内部实现
    // ---------------------------------------------------------------------------------------

    private IEnumerable<string> DiscoverDirectories()
    {
        if (!Directory.Exists(PluginDirectory))
        {
            _log?.Invoke("Plugins", $"插件目录不存在：{PluginDirectory}（按没有插件处理）", null);
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(PluginDirectory).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            // 防目录逃逸：只接受插件目录的直接子目录（规划 AD-13"仅加载受信目录"）。
            var full = System.IO.Path.GetFullPath(directory);
            if (!full.StartsWith(PluginDirectory + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (full.Contains("..", StringComparison.Ordinal)) continue;

            yield return full;
        }
    }

    private void LoadOne(string directory, PluginManifest fileManifest)
    {
        var pluginId = fileManifest.Id;

        if (_stateStore.IsDisabled(pluginId))
        {
            Record(new PluginInfo(pluginId, fileManifest.Name, fileManifest.Version, fileManifest.Author,
                fileManifest.Description, directory, PluginState.Disabled,
                _stateStore.GetFailureCount(pluginId) >= AutoDisableFailureThreshold
                    ? $"[{ErrorCodes.PluginAutoDisabled}] 连续失败 {_stateStore.GetFailureCount(pluginId)} 次后已被自动禁用；可在扩展中心手动启用"
                    : null,
                fileManifest.Capabilities, fileManifest.MayWrite, fileManifest.RequiredPrivilege,
                _stateStore.GetFailureCount(pluginId), _stateStore.GetFailureCount(pluginId) >= AutoDisableFailureThreshold));
            return;
        }

        var entryPath = ResolveEntryAssembly(directory, fileManifest, out var entryError);
        if (entryPath is null)
        {
            Record(new PluginInfo(pluginId, fileManifest.Name, fileManifest.Version, fileManifest.Author,
                fileManifest.Description, directory, PluginState.Invalid,
                $"[{ErrorCodes.PluginManifestInvalid}] {entryError}",
                fileManifest.Capabilities, fileManifest.MayWrite, fileManifest.RequiredPrivilege, 0, false));
            return;
        }

        PluginLoadContext? context = null;
        try
        {
            context = new PluginLoadContext(entryPath);
            var assembly = context.LoadFromAssemblyPath(entryPath);
            var plugin = CreatePlugin(assembly, fileManifest, out var createError);
            if (plugin is null)
            {
                Unload(context);
                Record(new PluginInfo(pluginId, fileManifest.Name, fileManifest.Version, fileManifest.Author,
                    fileManifest.Description, directory, PluginState.Invalid,
                    $"[{ErrorCodes.PluginManifestInvalid}] {createError}",
                    fileManifest.Capabilities, fileManifest.MayWrite, fileManifest.RequiredPrivilege, 0, false));
                return;
            }

            // 插件自身声明的清单优先用于风险/能力展示，但 Id 必须与 plugin.json 一致（否则视为清单冲突）。
            var declared = SafeManifest(plugin, fileManifest);
            if (!string.Equals(declared.Id, pluginId, StringComparison.OrdinalIgnoreCase))
            {
                Unload(context);
                Record(new PluginInfo(pluginId, fileManifest.Name, fileManifest.Version, fileManifest.Author,
                    fileManifest.Description, directory, PluginState.Invalid,
                    $"[{ErrorCodes.PluginManifestInvalid}] plugin.json 的 id（{pluginId}）与程序集声明（{declared.Id}）不一致",
                    declared.Capabilities, declared.MayWrite, declared.RequiredPrivilege, 0, false));
                return;
            }

            Register(plugin, declared, context, directory, entryPath, out var error);
        }
        catch (Exception ex)
        {
            Unload(context);
            var failures = _stateStore.GetFailureCount(pluginId) + 1;
            _stateStore.SetFailureCount(pluginId, failures);
            var autoDisabled = failures >= AutoDisableFailureThreshold;
            if (autoDisabled) _stateStore.SetDisabled(pluginId, true);

            _log?.Invoke("Plugins", $"插件 {pluginId} 加载失败：{ex.Message}", null);
            Record(new PluginInfo(pluginId, fileManifest.Name, fileManifest.Version, fileManifest.Author,
                fileManifest.Description, directory,
                autoDisabled ? PluginState.Disabled : PluginState.Failed,
                $"[{ErrorCodes.PluginLoadFailed}] 加载失败：{ex.Message}",
                fileManifest.Capabilities, fileManifest.MayWrite, fileManifest.RequiredPrivilege, failures, autoDisabled));
        }
    }

    private void Register(ICommPlugin plugin, PluginManifest declared, PluginLoadContext context, string directory,
        string entryPath, out string? error)
    {
        error = null;
        var pluginId = declared.Id;
        var scope = new PluginRegistryScope(Catalog, pluginId);

        try
        {
            // 超时 + 异常边界（规划 15.11）。Register 是同步契约，超时后无法真正中断，只能隔离其结果。
            var task = Task.Run(() => plugin.Register(scope));
            if (!task.Wait(_registerTimeoutMs))
            {
                // Register 是同步契约，超时无法真正中断；迟到的注册结果必须被清理，避免"已失败却仍生效"。
                _ = task.ContinueWith(_ =>
                {
                    if (!_loaded.ContainsKey(pluginId)) Catalog.RemoveByPlugin(pluginId);
                }, TaskScheduler.Default);

                throw new TimeoutException($"插件注册超时（超过 {_registerTimeoutMs} ms）");
            }
        }
        catch (Exception ex)
        {
            var actual = ex is AggregateException { InnerExceptions.Count: > 0 } aggregate
                ? aggregate.InnerExceptions[0]
                : ex;

            Catalog.RemoveByPlugin(pluginId);
            Unload(context);

            var failures = _stateStore.GetFailureCount(pluginId) + 1;
            _stateStore.SetFailureCount(pluginId, failures);
            var autoDisabled = failures >= AutoDisableFailureThreshold;
            if (autoDisabled) _stateStore.SetDisabled(pluginId, true);

            error = $"[{ErrorCodes.PluginCallFailed}] 注册失败：{actual.Message}";
            _log?.Invoke("Plugins", $"插件 {pluginId} 注册失败（第 {failures} 次）：{actual.Message}", null);

            Record(new PluginInfo(pluginId, declared.Name, declared.Version, declared.Author, declared.Description,
                directory,
                autoDisabled ? PluginState.Disabled : PluginState.Failed,
                autoDisabled
                    ? $"[{ErrorCodes.PluginAutoDisabled}] 连续失败 {failures} 次后已被自动禁用；可在扩展中心手动启用（{error}）"
                    : error,
                declared.Capabilities, declared.MayWrite, declared.RequiredPrivilege, failures, autoDisabled));
            return;
        }

        _loaded[pluginId] = new LoadedPlugin(context, entryPath, plugin);
        _stateStore.SetFailureCount(pluginId, 0);

        Record(new PluginInfo(pluginId, declared.Name, declared.Version, declared.Author, declared.Description,
            directory, PluginState.Loaded, null, declared.Capabilities, declared.MayWrite,
            declared.RequiredPrivilege, 0, false));

        _log?.Invoke("Plugins",
            $"已加载插件：{declared.Name} {declared.Version}（{pluginId}，能力 {declared.Capabilities.Count} 项" +
            (declared.MayWrite ? "，声明可能写设备" : string.Empty) + "）", null);
    }

    private static PluginManifest SafeManifest(ICommPlugin plugin, PluginManifest fallback)
    {
        try
        {
            return plugin.Manifest ?? fallback;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static string? ResolveEntryAssembly(string directory, PluginManifest manifest, out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(manifest.EntryAssembly))
        {
            var candidate = System.IO.Path.Combine(directory, manifest.EntryAssembly);
            if (File.Exists(candidate)) return candidate;

            error = $"清单声明的入口程序集不存在：{manifest.EntryAssembly}";
            return null;
        }

        // 未声明入口：插件目录里必须恰好有一个程序集（共享契约程序集不应出现在这里）。
        var assemblies = Directory.Exists(directory)
            ? Directory.GetFiles(directory, "*.dll")
                .Where(f => !f.EndsWith("Comm.Plugins.Abstractions.dll", StringComparison.OrdinalIgnoreCase))
                .Where(f => !f.EndsWith("Comm.Core.dll", StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : Array.Empty<string>();

        if (assemblies.Length == 1) return assemblies[0];

        error = assemblies.Length == 0
            ? "插件目录中没有程序集（也未见清单声明的 entry）"
            : $"插件目录中有 {assemblies.Length} 个程序集，清单必须用 entry 指明入口";
        return null;
    }

    private static ICommPlugin? CreatePlugin(Assembly assembly, PluginManifest manifest, out string? error)
    {
        error = null;

        Type? type = null;
        if (!string.IsNullOrWhiteSpace(manifest.EntryType))
        {
            type = assembly.GetType(manifest.EntryType, throwOnError: false);
            if (type is null)
            {
                error = $"清单声明的入口类型不存在：{manifest.EntryType}";
                return null;
            }
        }
        else
        {
            var candidates = assembly.GetTypes()
                .Where(t => typeof(ICommPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
                .ToArray();

            if (candidates.Length == 0)
            {
                error = "程序集中没有 ICommPlugin 实现";
                return null;
            }

            if (candidates.Length > 1)
            {
                error = $"程序集中有 {candidates.Length} 个 ICommPlugin 实现，清单必须用 type 指明入口";
                return null;
            }

            type = candidates[0];
        }

        try
        {
            return Activator.CreateInstance(type) as ICommPlugin
                   ?? throw new InvalidOperationException($"{type.FullName} 不是可实例化的 ICommPlugin");
        }
        catch (Exception ex)
        {
            error = $"无法创建插件实例：{ex.Message}";
            return null;
        }
    }

    private void Unload(string pluginId)
    {
        if (!_loaded.Remove(pluginId, out var loaded)) return;
        Catalog.RemoveByPlugin(pluginId);
        try
        {
            loaded.Context.Unload();
        }
        catch (Exception ex)
        {
            _log?.Invoke("Plugins", $"卸载插件上下文失败（不影响主程序）：{ex.Message}", null);
        }
    }

    private static void Unload(PluginLoadContext? context)
    {
        try
        {
            context?.Unload();
        }
        catch (Exception)
        {
            // 卸载失败不影响主程序
        }
    }

    private void Record(PluginInfo info) => _infos[info.Id] = info;

    private sealed record LoadedPlugin(PluginLoadContext Context, string EntryPath, ICommPlugin Instance);
}
