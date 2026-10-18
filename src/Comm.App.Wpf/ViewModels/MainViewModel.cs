using System.Collections.ObjectModel;
using System.Windows.Threading;
using Comm.App.Wpf.Services;
using Comm.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

public sealed record NavItem(string Title, string Glyph, object ViewModel);

public partial class MainViewModel : ObservableObject
{
    private readonly AppServices _app;
    private readonly DispatcherTimer _statusTimer;

    public MainViewModel(
        AppServices app,
        ConnectionViewModel connection,
        DeviceScanViewModel deviceScan,
        DataScanViewModel dataScan,
        RegisterLocatorViewModel registerLocator,
        WatchTableViewModel watchTable,
        ManualSendViewModel manualSend,
        MonitorViewModel monitor,
        NetworkToolsViewModel networkTools,
        LanScanViewModel lanScan,
        SettingsViewModel settings)
    {
        _app = app;

        // 导航顺序决定 --page=N 的编号（新增页后请同步更新 README 与 docs/capture-pages.sh）
        NavItems = new ObservableCollection<NavItem>
        {
            new("连接中心", "⇄", connection),
            new("设备扫描", "⌕", deviceScan),
            new("数据扫描", "▤", dataScan),
            new("寄存器定位", "◎", registerLocator),
            new("监视表", "◉", watchTable),
            new("报文工具", "✎", manualSend),
            new("报文监视", "≡", monitor),
            new("网络工具", "⌘", networkTools),
            new("局域网扫描", "⇉", lanScan),
            new("设置与日志", "⚙", settings)
        };

        SelectedNav = NavItems[0];
        ApplyStartupPageArgument();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        _app.Notified += (_, message) => MessageText = message;
        RefreshStatus();
    }

    /// <summary>支持 --page=N 直接打开指定功能页（便于支持、演示与界面截图）。</summary>
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

    public ObservableCollection<NavItem> NavItems { get; }

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

    /// <summary>跨页面通知（例如“已生成读取报文”）。</summary>
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

    /// <summary>“关于”行：版本 + 提交号 + 构建时间。</summary>
    public string AboutText => AppInfo.FullVersionText;

    partial void OnSelectedNavChanged(NavItem? value)
    {
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

    private void RefreshStatus()
    {
        ConnectionText = _app.Sessions.StatusText;
        ConnectionParamsText = _app.Sessions.Snapshot?.ShortText ?? "未连接";
        var stats = _app.Sessions.Current?.Statistics;
        StatisticsText = stats is null
            ? "TX 0 | RX 0 | 超时 0 | 校验错误 0 | 异常响应 0"
            : $"TX {stats.TxCount} | RX {stats.RxCount} | 超时 {stats.TimeoutCount} | 校验错误 {stats.ChecksumErrorCount} | 异常响应 {stats.ExceptionCount} | 未匹配 {stats.UnmatchedCount}";

        var monitor = _app.Monitor;
        MonitorText = $"监视：{(monitor.DisplayPaused ? "已暂停显示" : "运行")}；显示 {monitor.Rows.Count} / 共 {monitor.TotalCount}" +
                       (monitor.DroppedFromDisplay > 0 ? $"（显示已丢弃 {monitor.DroppedFromDisplay} 条，落盘不受影响）" : string.Empty) +
                       (monitor.IsRecording ? "；记录中" : string.Empty);

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
