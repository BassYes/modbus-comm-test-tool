using System.Collections.ObjectModel;
using System.Windows.Threading;
using Comm.App.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Comm.App.Wpf.ViewModels;

/// <summary>
/// 一个会话的工作区（M3b R-16，规划 21.2.1）：把属于该会话的页面组合在一起，
/// 可以整体拆分到独立窗口、再移回主窗口，而不会与其他会话共享连接与状态。
/// </summary>
public sealed partial class SessionWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _app;
    private readonly DispatcherTimer _statusTimer;
    private bool _disposed;

    public SessionWorkspaceViewModel(AppServices app, SessionContext session)
    {
        _app = app;
        Session = session;

        // 每个会话一组页面实例（规划 21.2.2 第 2 点：会话页面不再全部是应用级单例）。
        Connection = new ConnectionViewModel(app, session);
        DeviceScan = new DeviceScanViewModel(app, session);
        // R-12：TCP Unit ID 扫描（M4 新增，规划 4.2.7）
        UnitIdScan = new UnitIdScanViewModel(app, session);
        DataScan = new DataScanViewModel(app, session);
        RegisterLocator = new RegisterLocatorViewModel(app, session);
        WatchTable = new WatchTableViewModel(app, session);
        ManualSend = new ManualSendViewModel(app, session);
        Monitor = new MonitorViewModel(app, session);

        NavItems = new ObservableCollection<NavItem>
        {
            new("连接中心", "⇄", Connection),
            new("设备扫描", "⌕", DeviceScan),
            new("Unit ID 扫描", "⇉", UnitIdScan),
            new("数据扫描", "▤", DataScan),
            new("寄存器定位", "◎", RegisterLocator),
            new("监视表", "◉", WatchTable),
            new("报文工具", "✎", ManualSend),
            new("报文监视", "≡", Monitor)
        };

        SelectedNav = NavItems[0];
        session.Changed += OnSessionChanged;

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        RefreshStatus();
    }

    public SessionContext Session { get; }

    public string SessionId => Session.SessionId;

    public string Name => Session.Name;

    /// <summary>会话标签页显示文本（含端点，便于区分两个 TCP 会话）。</summary>
    public string Title => Session.Title;

    public ConnectionViewModel Connection { get; }
    public DeviceScanViewModel DeviceScan { get; }

    /// <summary>TCP Unit ID 扫描（R-12）。</summary>
    public UnitIdScanViewModel UnitIdScan { get; }
    public DataScanViewModel DataScan { get; }
    public RegisterLocatorViewModel RegisterLocator { get; }
    public WatchTableViewModel WatchTable { get; }
    public ManualSendViewModel ManualSend { get; }
    public MonitorViewModel Monitor { get; }

    /// <summary>本会话页面（不含全局页面：网络工具 / 局域网扫描 / 设置与日志 / 扩展中心）。</summary>
    public ObservableCollection<NavItem> NavItems { get; }

    [ObservableProperty]
    private NavItem? _selectedNav;

    /// <summary>是否已拆分到独立窗口（拆分后主窗口只显示一行提示，不再显示其页面）。</summary>
    [ObservableProperty]
    private bool _isDetached;

    [ObservableProperty]
    private string _connectionText = "未连接";

    [ObservableProperty]
    private string _statisticsText = "TX 0 | RX 0";

    [ObservableProperty]
    private string _monitorText = "监视：运行；显示 0 / 共 0";

    public object? CurrentPage => SelectedNav?.ViewModel;

    partial void OnSelectedNavChanged(NavItem? value) => OnPropertyChanged(nameof(CurrentPage));

    /// <summary>按 --page=N 的会话内序号选中页面（1 起）。</summary>
    public void SelectPage(int index)
    {
        if (index >= 1 && index <= NavItems.Count) SelectedNav = NavItems[index - 1];
    }

    private void OnSessionChanged(object? sender, EventArgs e) => RefreshStatus();

    public void RefreshStatus()
    {
        ConnectionText = Session.StatusText;
        StatisticsText = Session.Statistics.ToStatusText();

        var monitor = Session.Monitor;
        MonitorText = $"监视：{(monitor.DisplayPaused ? "已暂停显示" : "运行")}；本会话显示 {monitor.Rows.Count} 条" +
                      (monitor.DroppedFromDisplay > 0 ? $"（显示缓冲已丢弃 {monitor.DroppedFromDisplay} 条，落盘不受影响）" : string.Empty) +
                      (monitor.IsRecording ? "；记录中" : string.Empty);

        OnPropertyChanged(nameof(Title));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statusTimer.Stop();
        Session.Changed -= OnSessionChanged;

        Connection.Dispose();
        DeviceScan.Dispose();
        UnitIdScan.Dispose();
        DataScan.Dispose();
        RegisterLocator.Dispose();
        WatchTable.Dispose();
        ManualSend.Dispose();
        Monitor.Dispose();
    }
}
