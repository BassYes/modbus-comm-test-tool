using System.Collections.ObjectModel;
using System.Windows.Threading;
using Comm.App.Wpf.Services;
using Comm.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

public sealed record NavItem(string Title, string Glyph, object ViewModel);

/// <summary>
/// 主窗口显示模型（M3b R-16 重构后）：顶部是会话标签页，左侧导航由"活动会话的页面 + 全局页面"组成。
/// 每个会话的页面来自它自己的工作区（<see cref="SessionWorkspaceViewModel"/>），因此多会话之间不共享连接与状态。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _app;
    private readonly SessionWorkspaces _workspaces;
    private readonly SessionWindowManager _windows;
    private readonly DispatcherTimer _statusTimer;
    private readonly List<NavItem> _globalNav;
    private bool _syncing;

    public MainViewModel(
        AppServices app,
        SessionWorkspaces workspaces,
        SessionWindowManager windows,
        NetworkToolsViewModel networkTools,
        LanScanViewModel lanScan,
        SettingsViewModel settings,
        ExtensionCenterViewModel extensions)
    {
        _app = app;
        _workspaces = workspaces;
        _windows = windows;

        // 全局页面（不属于任何会话）：网络工具、局域网扫描、扩展中心、设置与日志。
        _globalNav = new List<NavItem>
        {
            new("网络工具", "⌘", networkTools),
            new("局域网扫描", "⇉", lanScan),
            new("扩展中心", "⧉", extensions),
            new("设置与日志", "⚙", settings)
        };

        _workspaces.Changed += (_, _) => SyncWorkspaces();
        _windows.Changed += (_, _) => SyncWorkspaces();
        _app.Sessions.SessionsChanged += (_, _) => SyncActive();
        SyncWorkspaces();
        ApplyStartupPageArgument();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        _app.Notified += (_, message) => MessageText = message;
        RefreshStatus();
    }

    /// <summary>会话标签页数据源（每个会话一个工作区）。</summary>
    public ObservableCollection<SessionWorkspaceViewModel> Workspaces { get; } = new();

    [ObservableProperty]
    private SessionWorkspaceViewModel? _activeWorkspace;

    /// <summary>左侧导航：活动会话的页面 + 全局页面。</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new();

    [ObservableProperty]
    private NavItem? _selectedNav;

    [ObservableProperty]
    private string _connectionText = "未连接";

    [ObservableProperty]
    private string _statisticsText = "TX 0 | RX 0";

    [ObservableProperty]
    private string _monitorText = "监视：运行；显示 0 / 共 0";

    /// <summary>底栏已连接参数（R-18）：来源是连接成功时的不可变快照，不是仍在编辑的输入框。</summary>
    [ObservableProperty]
    private string _connectionParamsText = "未连接";

    /// <summary>会话摘要（M3b：会话数 / 上限 / 已拆分窗口数）。</summary>
    [ObservableProperty]
    private string _sessionText = "会话 1/8";

    /// <summary>跨页面通知（例如"已生成读取报文"）。</summary>
    [ObservableProperty]
    private string _messageText = string.Empty;

    [ObservableProperty]
    private string _logText = "日志：关闭";

    [ObservableProperty]
    private bool _logEnabled;

    [ObservableProperty]
    private string _taskText = "任务：空闲";

    [ObservableProperty]
    private double _taskPercent;

    [ObservableProperty]
    private string _versionText = AppInfo.Summary();

    /// <summary>窗口标题（X-9：版本号单一来源，不再写死在 XAML 里）。</summary>
    public string VersionTitle => AppInfo.Title;

    /// <summary>"关于"行：版本 + 提交号 + 构建时间。</summary>
    public string AboutText => AppInfo.FullVersionText;

    // ------------------------------------------------------------------ 会话标签页

    private void SyncWorkspaces()
    {
        _syncing = true;
        try
        {
            var previous = ActiveWorkspace;
            Workspaces.Clear();
            foreach (var workspace in _workspaces.Workspaces) Workspaces.Add(workspace);

            // 活动会话（注册表）优先；否则保持原选择；再否则取第一个。
            var active = _workspaces.Find(_app.Sessions.Active.SessionId);
            ActiveWorkspace = active ?? (previous is not null && Workspaces.Contains(previous) ? previous : Workspaces.FirstOrDefault());
        }
        finally
        {
            _syncing = false;
        }

        RebuildNav();
        RefreshStatus();
    }

    private void SyncActive()
    {
        var active = _workspaces.Find(_app.Sessions.Active.SessionId);
        if (active is not null && !ReferenceEquals(active, ActiveWorkspace))
        {
            ActiveWorkspace = active;
        }

        RefreshStatus();
    }

    partial void OnActiveWorkspaceChanged(SessionWorkspaceViewModel? value)
    {
        if (!_syncing && value is not null) _app.Sessions.Activate(value.SessionId);
        RebuildNav();
        RefreshStatus();
    }

    private void RebuildNav()
    {
        var workspace = ActiveWorkspace;
        var previousSelection = SelectedNav;

        _syncing = true;
        try
        {
            NavItems.Clear();
            if (workspace is not null)
            {
                foreach (var item in workspace.NavItems) NavItems.Add(item);
            }

            foreach (var item in _globalNav) NavItems.Add(item);

            // 保持当前选择（同一工作区切换页面时不要跳回第一页）。
            SelectedNav = previousSelection is not null && NavItems.Contains(previousSelection)
                ? previousSelection
                : workspace?.SelectedNav ?? _globalNav.FirstOrDefault();
        }
        finally
        {
            _syncing = false;
        }

        OnPropertyChanged(nameof(CurrentPage));
    }

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value is null)
        {
            OnPropertyChanged(nameof(CurrentPage));
            return;
        }

        // 会话页面：把选择回写到该会话自己的工作区，拆分窗口与本窗口保持一致。
        if (ActiveWorkspace is not null && ActiveWorkspace.NavItems.Contains(value))
        {
            ActiveWorkspace.SelectedNav = value;
        }

        OnPropertyChanged(nameof(CurrentPage));
    }

    public object? CurrentPage => SelectedNav?.ViewModel;

    partial void OnLogEnabledChanged(bool value)
    {
        if (_suppressLogToggle) return;
        _app.SetLogEnabled(value);
        RefreshStatus();
    }

    private bool _suppressLogToggle;

    // ------------------------------------------------------------------ 命令

    [RelayCommand]
    private void NewSession()
    {
        var result = _workspaces.CreateSession();
        if (!result.IsSuccess)
        {
            _app.Dialogs.Warn("无法新建会话", result.Error!.ToUserText());
            return;
        }

        SyncWorkspaces();
        ActiveWorkspace = _workspaces.Find(result.Value!.SessionId);
        MessageText = $"已新建会话「{result.Value.Name}」；它拥有独立的连接、扫描、报文与监视上下文。";
    }

    [RelayCommand]
    private void CloseSession()
    {
        var workspace = ActiveWorkspace;
        if (workspace is null) return;

        if (!_app.Dialogs.Confirm("关闭会话",
                $"确定关闭会话「{workspace.Name}」吗？\n\n" +
                $"当前状态：{workspace.Session.StatusText}\n" +
                "该会话的扫描与循环发送会先停止；其他会话不受影响。")) return;

        var result = _workspaces.CloseSession(workspace.SessionId);
        if (!result.IsSuccess)
        {
            _app.Dialogs.Warn("无法关闭会话", result.Error!.ToUserText());
            return;
        }

        MessageText = result.Value ?? "会话已关闭";
        SyncWorkspaces();
    }

    [RelayCommand]
    private void SplitSession()
    {
        var workspace = ActiveWorkspace;
        if (workspace is null) return;

        var result = _windows.Split(workspace.SessionId);
        if (!result.IsSuccess)
        {
            _app.Dialogs.Warn("无法拆分窗口", result.Error!.ToUserText());
            return;
        }

        MessageText = result.Value ?? "已拆分到独立窗口";
        RebuildNav();
    }

    [RelayCommand]
    private void ReturnSession()
    {
        var workspace = ActiveWorkspace;
        if (workspace is null) return;

        var result = _windows.Return(workspace.SessionId);
        MessageText = result.IsSuccess ? result.Value ?? "已移回主窗口" : result.Error!.ToUserText();
        RebuildNav();
    }

    /// <summary>支持 --page=N 直接打开指定功能页（N：1~8 会话页面，9~12 全局页面，共 12 页）。</summary>
    private void ApplyStartupPageArgument()
    {
        try
        {
            foreach (var arg in Environment.GetCommandLineArgs().Skip(1))
            {
                if (!arg.StartsWith("--page=", StringComparison.OrdinalIgnoreCase)) continue;
                if (int.TryParse(arg[7..], out var index) && index >= 1 && index <= NavItems.Count)
                {
                    SelectedNav = NavItems[index - 1];
                }
            }
        }
        catch (Exception)
        {
            // 参数解析失败不影响正常启动
        }
    }

    private void RefreshStatus()
    {
        var workspace = ActiveWorkspace;
        var session = workspace?.Session;

        ConnectionText = session?.StatusText ?? "未连接";
        ConnectionParamsText = session?.Snapshot?.ShortText ?? "未连接";
        StatisticsText = session is null ? "TX 0 | RX 0" : session.Statistics.ToStatusText();

        var monitor = workspace?.Session.Monitor;
        MonitorText = monitor is null
            ? "监视：未就绪"
            : $"监视：{(monitor.DisplayPaused ? "已暂停显示" : "运行")}；本会话显示 {monitor.Rows.Count} 条" +
              (monitor.DroppedFromDisplay > 0 ? $"（显示已丢弃 {monitor.DroppedFromDisplay} 条，落盘不受影响）" : string.Empty) +
              (monitor.IsRecording ? "；记录中" : string.Empty);

        SessionText = $"会话 {Workspaces.Count}/{_app.Sessions.MaxSessions}" +
                      (_windows.DetachedCount > 0 ? $"（{_windows.DetachedCount} 个已拆分窗口）" : string.Empty);

        LogText = _app.Log.Enabled
            ? $"日志：开 → {_app.Log.ActiveLogFile ?? _app.Log.Directory}"
            : "日志：关闭（仅在内存保留最近 2000 条用于诊断）";
        if (LogEnabled != _app.Log.Enabled)
        {
            _suppressLogToggle = true;
            LogEnabled = _app.Log.Enabled;
            _suppressLogToggle = false;
        }

        TaskText = _app.Tasks.IsBusy
            ? $"任务：{_app.Tasks.TaskName} — {_app.Tasks.Detail}"
            : "任务：空闲";
        TaskPercent = _app.Tasks.Percent;
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _app.Log.Directory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("打开目录失败", ex.Message);
        }
    }
}
