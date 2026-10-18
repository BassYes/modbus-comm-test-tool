using System.Collections.ObjectModel;
using Comm.App.Wpf.Services;
using Comm.Core.Transport;
using Comm.Network;
using Comm.Settings;
using Comm.Transport;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>
/// 连接中心（协议栈 L0 参数，见规划 4.1）。
/// R-18 的两点改进：串口提供明确的“关闭串口”入口；连接后锁定参数编辑（避免界面值与实际链路不一致）。
/// </summary>
public partial class ConnectionViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _app;
    private readonly SessionContext _session;

    public ConnectionViewModel(AppServices app, SessionContext session)
    {
        _app = app;
        _session = session;

        var serial = app.Settings.Serial;
        _selectedPort = serial.PortName;
        _baudRate = serial.BaudRate;
        _dataBits = serial.DataBits;
        _parity = serial.Parity;
        _stopBits = serial.StopBits;
        _responseTimeoutMs = serial.ResponseTimeoutMs;
        _retryCount = serial.RetryCount;
        _turnaroundDelayMs = serial.TurnaroundDelayMs;
        _suppressEcho = serial.SuppressEcho;

        var tcp = app.Settings.Tcp;
        _host = tcp.Host;
        _port = tcp.Port;
        _unitId = tcp.UnitId;
        _connectTimeoutMs = tcp.ConnectTimeoutMs;

        RefreshPorts();
        RefreshTargets();
        UpdateStatus();
        _session.Changed += OnSessionChanged;

        // R-11：设备扫描的“应用到连接中心”通过事件回填串口参数（并可选择立即重连）。
        _app.SerialParametersRequested += OnSerialParametersRequested;
    }

    /// <summary>把参数扫描得到的串口参数回填到界面，必要时立即重连。</summary>
    private async Task ApplySerialParametersAsync(ApplySerialParametersRequest request)
    {
        try
        {
            BaudRate = request.Combination.BaudRate;
            DataBits = 8;
            Parity = request.Combination.Parity;
            StopBits = request.Combination.StopBits;
            UpdateStatus();
            Message = $"已应用串口参数 {request.Combination.Text}。";

            if (request.Reconnect)
            {
                await ConnectSerialAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            Message = $"应用串口参数失败：{ex.Message}";
        }
    }

    public ObservableCollection<string> Ports { get; } = new();

    /// <summary>常用设备目标（规划 7.2、4.12）。</summary>
    public ObservableCollection<DeviceTarget> Targets { get; } = new();

    public IReadOnlyList<int> BaudRates { get; } = new[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 };
    public IReadOnlyList<int> DataBitsOptions { get; } = new[] { 7, 8 };

    public IReadOnlyList<Option<SerialParity>> ParityOptions { get; } = new[]
    {
        new Option<SerialParity>(SerialParity.None, "无校验 (N)"),
        new Option<SerialParity>(SerialParity.Odd, "奇校验 (O)"),
        new Option<SerialParity>(SerialParity.Even, "偶校验 (E)")
    };

    public IReadOnlyList<Option<SerialStopBits>> StopBitsOptions { get; } = new[]
    {
        new Option<SerialStopBits>(SerialStopBits.One, "1 位"),
        new Option<SerialStopBits>(SerialStopBits.OnePointFive, "1.5 位"),
        new Option<SerialStopBits>(SerialStopBits.Two, "2 位")
    };

    [ObservableProperty] private string _selectedPort = "COM1";
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private int _dataBits = 8;
    [ObservableProperty] private SerialParity _parity = SerialParity.None;
    [ObservableProperty] private SerialStopBits _stopBits = SerialStopBits.One;
    [ObservableProperty] private int _responseTimeoutMs = 1000;
    [ObservableProperty] private int _retryCount = 2;
    [ObservableProperty] private int _turnaroundDelayMs = 20;
    [ObservableProperty] private bool _suppressEcho = true;

    /// <summary>RTU 静默判定下限（见规划 14.3.2：Windows 计时精度有限，默认 5 ms）。</summary>
    [ObservableProperty] private int _silenceThresholdMs = 5;

    [ObservableProperty] private string _host = "127.0.0.1";
    [ObservableProperty] private int _port = 502;
    [ObservableProperty] private byte _unitId = 1;
    [ObservableProperty] private int _connectTimeoutMs = 3000;

    [ObservableProperty] private string _statusText = "未连接";
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isSerialConnected;
    [ObservableProperty] private bool _isTcpConnected;
    [ObservableProperty] private string _connectionParamsText = "未连接";
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private DeviceTarget? _selectedTarget;
    [ObservableProperty] private string _targetName = string.Empty;

    public string SerialSummary =>
        $"{SelectedPort}  {BaudRate},{DataBits},{SerialSettings.ParityCode(Parity)},{SerialSettings.StopBitsCode(StopBits)}" +
        $"  超时 {ResponseTimeoutMs} ms  重试 {RetryCount}";

    public string TcpSummary => $"{Host}:{Port}  Unit ID {UnitId}  连接超时 {ConnectTimeoutMs} ms";

    public string StatisticsText
    {
        get
        {
            var stats = _session.Current?.Statistics;
            return stats is null ? "尚无会话统计" : stats.ToStatusText();
        }
    }

    /// <summary>串口已连接时锁定参数编辑（R-18：界面值必须反映实际链路参数）。</summary>
    public bool IsSerialParametersEnabled => !IsSerialConnected;
    public bool IsTcpParametersEnabled => !IsTcpConnected;

    public bool CanRefreshPorts => !IsSerialConnected;

    partial void OnSelectedPortChanged(string value) => OnPropertyChanged(nameof(SerialSummary));
    partial void OnBaudRateChanged(int value) => OnPropertyChanged(nameof(SerialSummary));
    partial void OnDataBitsChanged(int value) => OnPropertyChanged(nameof(SerialSummary));
    partial void OnParityChanged(SerialParity value) => OnPropertyChanged(nameof(SerialSummary));
    partial void OnStopBitsChanged(SerialStopBits value) => OnPropertyChanged(nameof(SerialSummary));
    partial void OnResponseTimeoutMsChanged(int value) => OnPropertyChanged(nameof(SerialSummary));
    partial void OnRetryCountChanged(int value) => OnPropertyChanged(nameof(SerialSummary));
    partial void OnHostChanged(string value) => OnPropertyChanged(nameof(TcpSummary));
    partial void OnPortChanged(int value) => OnPropertyChanged(nameof(TcpSummary));
    partial void OnUnitIdChanged(byte value) => OnPropertyChanged(nameof(TcpSummary));
    partial void OnConnectTimeoutMsChanged(int value) => OnPropertyChanged(nameof(TcpSummary));

    [RelayCommand]
    private void RefreshPorts()
    {
        if (IsSerialConnected)
        {
            Message = "串口已连接，参数与端口列表已锁定；如需更改请先“关闭串口”。";
            return;
        }

        var current = SelectedPort;
        Ports.Clear();
        foreach (var port in SerialChannel.GetPortNames()) Ports.Add(port);

        if (Ports.Count == 0)
        {
            // 没有检测到串口时保留用户当前填写的端口名，便于手工输入或使用虚拟串口。
            if (!string.IsNullOrWhiteSpace(current)) Ports.Add(current);
            Message = "未检测到串口。请插入 USB 转串口适配器后点“刷新串口”；如使用 TCP 设备可忽略。";
        }
        else
        {
            SelectedPort = Ports.Contains(current) ? current : Ports[0];
            Message = $"已检测到 {Ports.Count} 个串口。";
        }
    }

    [RelayCommand]
    private async Task ConnectSerialAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedPort))
        {
            _app.Dialogs.Warn("参数不完整", "请先选择串口。");
            return;
        }

        var profile = new SerialProfile
        {
            Name = "串口会话",
            PortName = SelectedPort,
            BaudRate = BaudRate,
            DataBits = DataBits,
            Parity = Parity,
            StopBits = StopBits,
            ResponseTimeoutMs = ResponseTimeoutMs,
            RetryCount = RetryCount,
            TurnaroundDelayMs = TurnaroundDelayMs,
            SuppressEcho = SuppressEcho
        };

        _app.Update(s => s with { Serial = profile });

        Message = $"正在打开 {SerialSummary} …";
        var result = await _session.ConnectSerialAsync(profile, SilenceThresholdMs).ConfigureAwait(true);
        Message = result.IsSuccess ? "串口已连接。" : result.Error!.ToUserText();
        UpdateStatus();
    }

    /// <summary>关闭当前串口会话（R-18）。</summary>
    [RelayCommand]
    private async Task CloseSerialAsync()
    {
        if (!_session.IsSerialConnected)
        {
            Message = "当前没有打开的串口。";
            return;
        }

        if (_app.Tasks.IsBusy &&
            !_app.Dialogs.Confirm("任务进行中", $"当前任务：{_app.Tasks.TaskName}\n\n关闭串口会中断该任务的通信。是否继续？"))
        {
            return;
        }

        await _session.DisconnectAsync().ConfigureAwait(true);
        Message = "串口已关闭。";
        _app.Log.Info("Session", "用户关闭串口");
        UpdateStatus();
    }

    [RelayCommand]
    private async Task ConnectTcpAsync()
    {
        if (!SubnetCalculator.TryParseAddress(Host, out _))
        {
            _app.Dialogs.Warn("参数不正确", "目标 IP 地址格式不正确（暂不支持主机名）。");
            return;
        }

        var profile = new TcpProfile
        {
            Name = "TCP 会话",
            Host = Host,
            Port = Port,
            UnitId = UnitId,
            ConnectTimeoutMs = ConnectTimeoutMs,
            ResponseTimeoutMs = ResponseTimeoutMs,
            RetryCount = RetryCount
        };

        _app.Update(s => s with { Tcp = profile });

        Message = $"正在连接 {TcpSummary} …";
        var result = await _session.ConnectTcpAsync(profile).ConfigureAwait(true);
        Message = result.IsSuccess ? "TCP 已连接。" : result.Error!.ToUserText();
        UpdateStatus();
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _session.DisconnectAsync().ConfigureAwait(true);
        Message = "已断开连接。";
        UpdateStatus();
    }

    [RelayCommand]
    private void ResetStatistics()
    {
        _session.Current?.Statistics.Reset();
        _app.Log.Info("Session", "已清零会话统计");
        OnPropertyChanged(nameof(StatisticsText));
    }

    // ---------------------------------------------------------------- 设备目标（规划 7.2）

    private void RefreshTargets()
    {
        Targets.Clear();
        foreach (var target in _app.Settings.Targets.OrderBy(t => t.Name)) Targets.Add(target);
    }

    /// <summary>把当前填写的端点保存为设备目标。</summary>
    [RelayCommand]
    private void SaveTarget()
    {
        var isSerial = !string.IsNullOrWhiteSpace(SelectedPort) && IsSerialConnected;
        var kind = isSerial || !IsTcpConnected ? ConnectionKindHint.Serial : ConnectionKindHint.Tcp;
        var endpoint = kind == ConnectionKindHint.Serial ? SelectedPort.Trim() : $"{Host.Trim()}:{Port}";

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _app.Dialogs.Warn("参数不完整", "端点为空，无法保存设备目标。");
            return;
        }

        var target = new DeviceTarget
        {
            Name = string.IsNullOrWhiteSpace(TargetName) ? endpoint : TargetName.Trim(),
            Kind = kind,
            Endpoint = endpoint,
            UnitId = UnitId
        };

        var error = _app.SaveTarget(target);
        if (!string.IsNullOrEmpty(error))
        {
            _app.Dialogs.Error("保存失败", error);
            return;
        }

        RefreshTargets();
        Message = $"已保存设备目标：{target.DisplayText}";
    }

    [RelayCommand]
    private void ApplyTarget()
    {
        if (SelectedTarget is null) return;

        var target = SelectedTarget;
        if (target.Kind == ConnectionKindHint.Serial)
        {
            SelectedPort = target.Endpoint;
        }
        else
        {
            var parts = target.Endpoint.Split(':');
            Host = parts[0];
            if (parts.Length > 1 && int.TryParse(parts[1], out var port)) Port = port;
        }

        UnitId = target.UnitId;
        Message = $"已载入设备目标：{target.DisplayText}。点击“打开串口”或“连接 TCP”建立连接。";
    }

    [RelayCommand]
    private void RemoveTarget()
    {
        if (SelectedTarget is null) return;
        var name = SelectedTarget.DisplayText;
        _app.RemoveTarget(SelectedTarget.Id);
        RefreshTargets();
        Message = $"已删除设备目标：{name}";
    }

    private void UpdateStatus()
    {
        IsConnected = _session.IsConnected;
        IsSerialConnected = _session.IsSerialConnected;
        IsTcpConnected = _session.IsTcpConnected;
        StatusText = _session.StatusText;
        ConnectionParamsText = _session.Snapshot?.ShortText ?? "未连接";

        OnPropertyChanged(nameof(StatisticsText));
        OnPropertyChanged(nameof(IsSerialParametersEnabled));
        OnPropertyChanged(nameof(IsTcpParametersEnabled));
        OnPropertyChanged(nameof(CanRefreshPorts));
    }

    // ---------------------------------------------------------------- 会话事件与释放（M3b：页面属于某个会话）

    private void OnSessionChanged(object? sender, EventArgs e) => UpdateStatus();

    private void OnSerialParametersRequested(object? sender, ApplySerialParametersRequest request) =>
        _ = ApplySerialParametersAsync(request);

    /// <summary>关闭会话时由工作区调用：解除订阅，避免已关闭会话继续收到全局事件。</summary>
    public void Dispose()
    {
        _session.Changed -= OnSessionChanged;
        _app.SerialParametersRequested -= OnSerialParametersRequested;
    }
}
