using Comm.Diagnostics;
using Comm.Plugins.Abstractions;
using Comm.Plugins.Host;

namespace Comm.App.Wpf.Services;

/// <summary>
/// 应用级插件服务（M3b R-10）：把 <see cref="PluginHost"/> 接到设置文件与日志上，
/// 并把"禁用状态 + 连续失败次数"持久化（规划 15.11：自动禁用的插件下次启动仍保持禁用）。
/// </summary>
public sealed class PluginService : IDisposable
{
    private readonly AppServices _app;
    private readonly PluginHost _host;
    private bool _disposed;

    public PluginService(AppServices app)
    {
        _app = app;

        // 受信目录固定为"程序目录/plugins"（规划 8.3、AD-13；安装版只放程序与 plugins/）。
        Directory = System.IO.Path.Combine(AppPaths.BaseDirectory, "plugins");

        var hostVersion = Version.TryParse(AppInfo.Version, out var parsed) ? parsed : new Version(0, 0, 0);
        _host = new PluginHost(Directory, new SettingsPluginStateStore(app), hostVersion, 5000, LogPlugin);
        _host.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);

        if (app.Settings.Plugins.LoadOnStartup) LoadAll();
    }

    /// <summary>受信插件目录（只加载这里的插件；不存在时不报错）。</summary>
    public string Directory { get; }

    public IReadOnlyList<PluginInfo> Plugins => _host.Plugins;

    /// <summary>已注册的扩展点（供其他页面调用；例如导出格式、报文解析器）。</summary>
    public PluginCatalog Catalog => _host.Catalog;

    public int LoadedCount => _host.LoadedCount;

    public bool PluginsDirectoryExists => System.IO.Directory.Exists(Directory);

    public event EventHandler? Changed;

    public string StatusText
    {
        get
        {
            if (!PluginsDirectoryExists) return $"插件目录不存在：{Directory}（没有加载任何插件）";

            var disabled = Plugins.Count(p => p.State == PluginState.Disabled);
            var failed = Plugins.Count(p => p.State is PluginState.Failed or PluginState.Invalid or PluginState.Incompatible);
            return $"已加载 {LoadedCount} / 共发现 {Plugins.Count} 个插件" +
                   (disabled > 0 ? $"；已禁用 {disabled} 个" : string.Empty) +
                   (failed > 0 ? $"；异常 {failed} 个" : string.Empty);
        }
    }

    /// <summary>重新扫描并加载（已加载的不会重复注册）。</summary>
    public void LoadAll()
    {
        try
        {
            _host.LoadAll();
        }
        catch (Exception ex)
        {
            // 插件问题绝不能影响主程序（规划 15.11）。
            _app.Log.Error("Plugins", $"加载插件时发生异常：{ex.Message}", ex);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>启用（或重新加载）插件：清除禁用状态与失败计数。</summary>
    public PluginInfo? Enable(string pluginId)
    {
        var info = _host.Reload(pluginId);
        Changed?.Invoke(this, EventArgs.Empty);
        return info;
    }

    /// <summary>禁用插件（下次启动仍保持禁用，直到手动启用）。</summary>
    public void Disable(string pluginId)
    {
        _host.Disable(pluginId);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>尽力创建插件目录（程序目录不可写时明确记录，不静默失败）。</summary>
    public string? TryCreateDirectory()
    {
        try
        {
            if (PluginsDirectoryExists) return null;
            System.IO.Directory.CreateDirectory(Directory);
            return null;
        }
        catch (Exception ex)
        {
            return $"无法创建插件目录 {Directory}：{ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host.Dispose();
    }

    private void LogPlugin(string category, string message, string? correlation) =>
        _app.Log.Info($"Plugins/{category}", message, correlation);

    /// <summary>用设置文件实现插件状态存储（规划 15.11：禁用状态与失败计数跨次启动保留）。</summary>
    private sealed class SettingsPluginStateStore : IPluginStateStore
    {
        private readonly AppServices _app;

        public SettingsPluginStateStore(AppServices app) => _app = app;

        public bool IsDisabled(string pluginId) =>
            _app.Settings.Plugins.DisabledPlugins.Contains(pluginId, StringComparer.OrdinalIgnoreCase);

        public void SetDisabled(string pluginId, bool disabled)
        {
            var current = _app.Settings.Plugins.DisabledPlugins.ToList();
            current.RemoveAll(id => string.Equals(id, pluginId, StringComparison.OrdinalIgnoreCase));
            if (disabled) current.Add(pluginId);

            _app.Update(s => s with { Plugins = s.Plugins with { DisabledPlugins = current } });
        }

        public int GetFailureCount(string pluginId) =>
            _app.Settings.Plugins.FailureCounts.TryGetValue(pluginId, out var count) ? count : 0;

        public void SetFailureCount(string pluginId, int count)
        {
            var counts = new Dictionary<string, int>(_app.Settings.Plugins.FailureCounts, StringComparer.OrdinalIgnoreCase)
            {
                [pluginId] = count
            };

            if (count <= 0) counts.Remove(pluginId);

            _app.Update(s => s with { Plugins = s.Plugins with { FailureCounts = counts } });
        }
    }
}
