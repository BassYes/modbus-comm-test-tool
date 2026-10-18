using System.Collections.ObjectModel;
using Comm.App.Wpf.Services;
using Comm.Plugins.Host;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>
/// 扩展中心（M3b R-10，规划 8、15.11）：
/// 展示插件目录、清单声明的能力/权限/风险、加载状态与失败原因，并支持启用/禁用。
/// 这是"只加载受信目录下的插件"（AD-13 / SR-7）在界面上的可见入口。
/// </summary>
public partial class ExtensionCenterViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _app;
    private readonly PluginService _plugins;

    public ExtensionCenterViewModel(AppServices app, PluginService plugins)
    {
        _app = app;
        _plugins = plugins;
        _plugins.Changed += OnPluginsChanged;

        Refresh();
    }

    public ObservableCollection<PluginInfo> Plugins { get; } = new();

    public ObservableCollection<PluginRegistration> Registrations { get; } = new();

    [ObservableProperty] private PluginInfo? _selectedPlugin;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _directoryText = string.Empty;
    [ObservableProperty] private string _detailText = "选中一个插件可查看能力、权限与失败原因。";
    [ObservableProperty] private string _message = string.Empty;

    /// <summary>安全提示：插件是进程内代码，不是沙箱（规划 AD-13 的诚实边界）。</summary>
    public string SecurityNotice =>
        "插件是进程内代码，AssemblyLoadContext **不是安全沙箱**：只安装来源可信的插件。" +
        "本工具只加载程序目录下 plugins/ 里的插件；插件声明的写能力（mayWrite）在使用时会再次提示。";

    private void OnPluginsChanged(object? sender, EventArgs e) => Refresh();

    [RelayCommand]
    private void Refresh()
    {
        var previous = SelectedPlugin?.Id;
        Plugins.Clear();
        foreach (var plugin in _plugins.Plugins) Plugins.Add(plugin);

        Registrations.Clear();
        foreach (var registration in _plugins.Catalog.Registrations) Registrations.Add(registration);

        SelectedPlugin = Plugins.FirstOrDefault(p => p.Id == previous) ?? Plugins.FirstOrDefault();
        StatusText = _plugins.StatusText;
        DirectoryText = $"插件目录：{_plugins.Directory}" +
                        (_plugins.PluginsDirectoryExists ? string.Empty : "（不存在）") +
                        $"\n扩展点：{_plugins.Catalog.Count} 项（解析器 {_plugins.Catalog.FrameParsers.Count}、" +
                        $"校验 {_plugins.Catalog.ChecksumProviders.Count}、编解码 {_plugins.Catalog.FrameCodecs.Count}、" +
                        $"导出格式 {_plugins.Catalog.Exporters.Count}、传输 {_plugins.Catalog.TransportFactories.Count}）";
    }

    [RelayCommand]
    private void Reload()
    {
        _plugins.LoadAll();
        Message = "已重新扫描插件目录。";
        Refresh();
    }

    [RelayCommand]
    private void Enable()
    {
        if (SelectedPlugin is null) return;

        var info = _plugins.Enable(SelectedPlugin.Id);
        Message = info?.State == PluginState.Loaded
            ? $"已启用插件「{info.Name}」。"
            : $"启用插件「{SelectedPlugin.Name}」失败：{info?.Error ?? "未知原因"}";
        Refresh();
    }

    [RelayCommand]
    private void Disable()
    {
        if (SelectedPlugin is null) return;
        if (!_app.Dialogs.Confirm("禁用插件",
                $"确定禁用插件「{SelectedPlugin.Name}」吗？\n\n" +
                "禁用后它的解析器/导出格式会立即注销，并在下次启动时保持禁用，直到你手动启用。")) return;

        _plugins.Disable(SelectedPlugin.Id);
        Message = $"已禁用插件「{SelectedPlugin.Name}」。";
        Refresh();
    }

    [RelayCommand]
    private void CreatePluginDirectory()
    {
        var error = _plugins.TryCreateDirectory();
        Message = error ?? $"已创建插件目录：{_plugins.Directory}（把插件文件夹放进去后点“重新扫描”）。";
        Refresh();
    }

    [RelayCommand]
    private void OpenPluginDirectory()
    {
        try
        {
            _plugins.TryCreateDirectory();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _plugins.Directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("打开插件目录失败", ex.Message);
        }
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        var text = StatusText + Environment.NewLine + DirectoryText + Environment.NewLine + Environment.NewLine +
                   string.Join(Environment.NewLine, Plugins.Select(p =>
                       $"{p.Id} | {p.Name} | {p.Version} | {p.StateText} | 能力 {p.CapabilitiesText} | {p.RiskText}" +
                       (string.IsNullOrWhiteSpace(p.Error) ? string.Empty : $" | {p.Error}")));

        try
        {
            System.Windows.Clipboard.SetText(text);
            Message = "已复制插件诊断信息到剪贴板。";
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("复制失败", ex.Message);
        }
    }

    partial void OnSelectedPluginChanged(PluginInfo? value)
    {
        if (value is null)
        {
            DetailText = "选中一个插件可查看能力、权限与失败原因。";
            return;
        }

        var registrations = _plugins.Catalog.OfPlugin(value.Id);
        DetailText =
            $"插件 Id：{value.Id}\n名称：{value.Name}\n版本：{value.Version}\n作者：{value.Author ?? "-"}\n" +
            $"状态：{value.StateText}\n能力声明：{value.CapabilitiesText}\n风险：{value.RiskText}\n" +
            $"所需权限：{value.RequiredPrivilege ?? "-"}\n连续失败次数：{value.FailureCount}\n" +
            $"目录：{value.Directory}\n" +
            (string.IsNullOrWhiteSpace(value.Error) ? string.Empty : $"错误：{value.Error}\n") +
            (registrations.Count == 0
                ? "已注册扩展点：无"
                : "已注册扩展点：\n" + string.Join("\n", registrations.Select(r => $"  · {r.Kind}：{r.Name}")));
    }

    public void Dispose() => _plugins.Changed -= OnPluginsChanged;
}
