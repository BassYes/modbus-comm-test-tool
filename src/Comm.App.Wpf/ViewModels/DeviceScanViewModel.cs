using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Comm.App.Wpf.Services;
using Comm.Core.Errors;
using Comm.Core.Tasks;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>
/// 串口 / TCP 从站地址扫描（规划 4.2，需求 R-01）。
/// R-17：增加“手动探测”模式（自选只读功能码、起始地址、数量）；手动模式的写功能码在服务层被拒绝。
/// </summary>
public partial class DeviceScanViewModel : ObservableObject
{
    private const string ScanPlanKind = ScanPlanKinds.Device;

    private readonly AppServices _app;
    private CancellationTokenSource? _cts;
    private PauseGate? _gate;

    public DeviceScanViewModel(AppServices app)
    {
        _app = app;
        var scan = app.Settings.Scan;
        _startAddress = scan.ScanStartAddress;
        _endAddress = scan.ScanEndAddress;
        _responseTimeoutMs = scan.ScanResponseTimeoutMs;
        _intervalMs = scan.ScanIntervalMs;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => !OnlyOnline || ((DeviceScanItem)o).Status != DeviceOnlineStatus.Offline;

        RefreshPlans();
    }

    public ObservableCollection<DeviceScanItem> Items { get; } = new();
    public ICollectionView ItemsView { get; }

    /// <summary>已保存的设备扫描方案（规划 4.12）。</summary>
    public ObservableCollection<ScanPlan> Plans { get; } = new();

    public IReadOnlyList<Option<DeviceProbeMode>> ModeOptions { get; } = new[]
    {
        new Option<DeviceProbeMode>(DeviceProbeMode.AutoTemplate, "自动模板（03→04→01→02）"),
        new Option<DeviceProbeMode>(DeviceProbeMode.Manual, "手动探测（自选功能码 / 地址 / 数量）")
    };

    /// <summary>手动探测可选的功能码：**只有只读功能码**。</summary>
    public IReadOnlyList<Option<byte>> ProbeFunctionOptions { get; } = new[]
    {
        new Option<byte>(Fn.ReadHoldingRegisters, "03 读保持寄存器"),
        new Option<byte>(Fn.ReadInputRegisters, "04 读输入寄存器"),
        new Option<byte>(Fn.ReadCoils, "01 读线圈"),
        new Option<byte>(Fn.ReadDiscreteInputs, "02 读离散输入")
    };

    [ObservableProperty] private DeviceProbeMode _probeMode = DeviceProbeMode.AutoTemplate;
    [ObservableProperty] private byte _manualProbeFunction = Fn.ReadHoldingRegisters;
    [ObservableProperty] private int _manualProbeAddress;
    [ObservableProperty] private int _manualProbeQuantity = 1;

    [ObservableProperty] private int _startAddress = 1;
    [ObservableProperty] private int _endAddress = 247;
    [ObservableProperty] private int _responseTimeoutMs = 500;
    [ObservableProperty] private int _retryCount;
    [ObservableProperty] private int _intervalMs = 20;
    [ObservableProperty] private bool _confirmFound;
    [ObservableProperty] private bool _tryDeviceIdentification = true;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _progressText = "未开始";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>最近的发现事件（与任务终态 StatusMessage 分开维护，避免互相覆盖）。</summary>
    [ObservableProperty] private string _recentEventText = string.Empty;

    [ObservableProperty] private DeviceScanItem? _selectedItem;
    [ObservableProperty] private bool _onlyOnline = true;
    [ObservableProperty] private string _planName = string.Empty;
    [ObservableProperty] private ScanPlan? _selectedPlan;

    public bool IsManualMode => ProbeMode == DeviceProbeMode.Manual;

    public string EstimatedText
    {
        get
        {
            var options = BuildOptions();
            var duration = options.EstimatedDuration();
            var templates = options.ProbeTemplates();
            var templateText = templates.Count == 1
                ? templates[0].Text
                : "03 → 04 → 01 → 02（地址 0、数量 1）";
            return $"地址数 {options.TotalAddresses}；探测模板 {templates.Count} 个（{templateText}）；" +
                   $"最坏情况预计耗时 {duration.TotalMinutes:F1} 分钟（无设备时每个地址都会尝试全部模板并超时）。";
        }
    }

    public string DefaultUnitIdHint => $"扫描完成后可把发现地址设为默认 Unit ID（当前默认 {_app.Sessions.DefaultUnitId}）。";

    partial void OnStartAddressChanged(int value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnEndAddressChanged(int value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnResponseTimeoutMsChanged(int value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnIntervalMsChanged(int value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnProbeModeChanged(DeviceProbeMode value)
    {
        OnPropertyChanged(nameof(EstimatedText));
        OnPropertyChanged(nameof(IsManualMode));
    }

    partial void OnManualProbeFunctionChanged(byte value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnManualProbeAddressChanged(int value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnManualProbeQuantityChanged(int value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnOnlyOnlineChanged(bool value) => ItemsView.Refresh();

    private DeviceScanOptions BuildOptions() => new()
    {
        StartAddress = (byte)Math.Clamp(StartAddress, 1, 247),
        EndAddress = (byte)Math.Clamp(EndAddress, 1, 247),
        ResponseTimeoutMs = Math.Max(50, ResponseTimeoutMs),
        RetryCount = Math.Max(0, RetryCount),
        IntervalMs = Math.Max(0, IntervalMs),
        ConfirmFound = ConfirmFound,
        TryReadDeviceIdentification = TryDeviceIdentification,
        Mode = ProbeMode,
        ManualProbeFunction = ManualProbeFunction,
        ManualProbeAddress = (ushort)Math.Clamp(ManualProbeAddress, 0, 65535),
        ManualProbeQuantity = (ushort)Math.Clamp(ManualProbeQuantity, 1, 2000)
    };

    [RelayCommand]
    private async Task StartAsync()
    {
        var service = _app.Sessions.CreateDeviceScanService();
        if (service is null)
        {
            _app.Dialogs.Warn("尚未连接", "请先在“连接中心”打开串口或连接 TCP，然后再扫描。");
            return;
        }

        var options = BuildOptions();
        if (!DeviceScanOptions.Validate(options, out var error))
        {
            _app.Dialogs.ShowError(error!, "扫描参数不正确");
            return;
        }

        _app.Update(s => s with
        {
            Scan = s.Scan with
            {
                ScanStartAddress = options.StartAddress,
                ScanEndAddress = options.EndAddress,
                ScanResponseTimeoutMs = options.ResponseTimeoutMs,
                ScanIntervalMs = options.IntervalMs
            }
        });

        Items.Clear();
        _cts = new CancellationTokenSource();
        _gate = new PauseGate();
        IsRunning = true;
        IsPaused = false;
        ProgressPercent = 0;
        ProgressText = "开始扫描…";
        RecentEventText = string.Empty;

        // 关键（M1 反馈问题 8）：新任务必须立即清掉上一轮的终态提示，否则界面会显示旧结论。
        var templateText = options.ProbeTemplates().Count == 1 ? options.ProbeTemplates()[0].Text : "自动模板 03 → 04 → 01 → 02";
        StatusMessage = $"正在扫描：地址 {options.StartAddress} ~ {options.EndAddress}，探测 {templateText}";

        _app.Log.Info("DeviceScan",
            $"开始设备扫描：地址 {options.StartAddress}~{options.EndAddress}，方式 {options.Mode}，探测 {templateText}，" +
            $"超时 {options.ResponseTimeoutMs} ms，重试 {options.RetryCount}，间隔 {options.IntervalMs} ms");

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressText = p.ToText();
            ProgressPercent = p.Percent;
            _app.Tasks.Report(p);
        });

        var itemProgress = new Progress<DeviceScanItem>(item =>
        {
            Items.Add(item);
            if (item.Status != DeviceOnlineStatus.Offline)
            {
                RecentEventText = $"最近发现：地址 {item.Address}（{item.StatusText}）{item.Note}";
            }
        });

        try
        {
            var results = await service.ScanAsync(options, _gate, progress, itemProgress, _cts.Token).ConfigureAwait(true);
            int online = results.Count(r => r.Status == DeviceOnlineStatus.Online);
            int suspected = results.Count(r => r.Status == DeviceOnlineStatus.Suspected);
            StatusMessage = $"扫描完成：在线 {online} 个，疑似 {suspected} 个，共扫描 {results.Count} 个地址。";
            _app.Log.Info("DeviceScan", StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"扫描已停止。已完成 {Items.Count} 个地址。";
            _app.Log.Info("DeviceScan", "扫描被用户停止");
        }
        catch (CommException ex)
        {
            StatusMessage = ex.Error.ToUserText();
            _app.Dialogs.ShowError(ex.Error, "扫描参数不正确");
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描异常终止：{ex.Message}";
            _app.Log.Error("DeviceScan", "扫描异常终止", ex);
            _app.Dialogs.Error("扫描失败", ex.Message);
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _cts?.Dispose();
            _cts = null;
            _gate = null;
            _app.Tasks.Idle();
        }
    }

    [RelayCommand]
    private void PauseResume()
    {
        if (_gate is null) return;
        if (_gate.IsPaused)
        {
            _gate.Resume();
            IsPaused = false;
        }
        else
        {
            _gate.Pause();
            IsPaused = true;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    [RelayCommand]
    private void Clear()
    {
        Items.Clear();
        StatusMessage = string.Empty;
        RecentEventText = string.Empty;
        ProgressText = "未开始";
        ProgressPercent = 0;
    }

    [RelayCommand]
    private void Export()
    {
        if (Items.Count == 0)
        {
            _app.Dialogs.Warn("无数据", "还没有扫描结果可以导出。");
            return;
        }

        var path = _app.Dialogs.SaveFile("导出设备扫描结果", "CSV 文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt",
            $"设备扫描-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (string.IsNullOrEmpty(path)) return;

        var filter = $"起始 {StartAddress}，结束 {EndAddress}，超时 {ResponseTimeoutMs} ms，" +
                     (IsManualMode ? $"手动探测功能码 0x{ManualProbeFunction:X2}" : "自动模板 03→04→01→02");

        var isText = path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        var error = isText
            ? DeviceScanService.ExportText(path, Items.ToList(), filter)
            : DeviceScanService.ExportCsv(path, Items.ToList(), filter);

        if (string.IsNullOrEmpty(error)) _app.Dialogs.Info("导出完成", $"已导出 {Items.Count} 条结果到：\n{path}");
        else _app.Dialogs.Error("导出失败", error);
    }

    [RelayCommand]
    private void UseAsTarget()
    {
        if (SelectedItem is null) return;
        _app.Sessions.SetDefaultUnitId(SelectedItem.Address);
        _app.Update(s => s with { Tcp = s.Tcp with { UnitId = SelectedItem.Address } });
        StatusMessage = $"已将地址 {SelectedItem.Address} 设为默认 Unit ID，可在数据扫描中使用。";
        OnPropertyChanged(nameof(DefaultUnitIdHint));
    }

    /// <summary>把选中地址加入监视表（M2-4 联动）。</summary>
    [RelayCommand]
    private void AddToWatch()
    {
        if (SelectedItem is null) return;

        _app.WatchList.Add(new RegisterPoint
        {
            Name = $"从站 {SelectedItem.Address}",
            UnitId = SelectedItem.Address,
            Area = DataArea.HoldingRegisters,
            Address = 0,
            Type = DataType.UInt16,
            Note = $"来自设备扫描（探测功能码 {SelectedItem.ProbeFunctionText}）"
        });

        StatusMessage = _app.WatchList.StatusMessage + "（可在“监视表”页调整地址与数据类型）";
    }

    // ---------------------------------------------------------------- 扫描方案（规划 4.12）

    private void RefreshPlans()
    {
        Plans.Clear();
        foreach (var plan in _app.PlansOfKind(ScanPlanKind)) Plans.Add(plan);
    }

    [RelayCommand]
    private void SavePlan()
    {
        var options = BuildOptions();
        if (!DeviceScanOptions.Validate(options, out var error))
        {
            _app.Dialogs.ShowError(error!, "扫描参数不正确");
            return;
        }

        var name = string.IsNullOrWhiteSpace(PlanName)
            ? $"{options.StartAddress}-{options.EndAddress}{(options.Mode == DeviceProbeMode.Manual ? " 手动" : string.Empty)}"
            : PlanName.Trim();

        var message = _app.SavePlan(ScanPlan.Create(name, ScanPlanKind, options));
        if (!string.IsNullOrEmpty(message))
        {
            _app.Dialogs.Error("保存失败", message);
            return;
        }

        RefreshPlans();
        StatusMessage = $"已保存扫描方案：{name}";
    }

    [RelayCommand]
    private void LoadPlan()
    {
        if (SelectedPlan is null) return;

        var plan = SelectedPlan;
        var options = plan.ReadPayload<DeviceScanOptions>();
        if (options is null)
        {
            _app.Dialogs.Warn("方案不可用", $"方案“{plan.DisplayText}”的内容无法读取（可能来自更新的版本），已忽略。");
            return;
        }

        ProbeMode = options.Mode;
        StartAddress = options.StartAddress;
        EndAddress = options.EndAddress;
        ResponseTimeoutMs = options.ResponseTimeoutMs;
        RetryCount = options.RetryCount;
        IntervalMs = options.IntervalMs;
        ConfirmFound = options.ConfirmFound;
        TryDeviceIdentification = options.TryReadDeviceIdentification;
        ManualProbeFunction = options.ManualProbeFunction;
        ManualProbeAddress = options.ManualProbeAddress;
        ManualProbeQuantity = options.ManualProbeQuantity;
        PlanName = plan.Name;

        StatusMessage = $"已载入方案：{plan.DisplayText}";
    }

    [RelayCommand]
    private void DeletePlan()
    {
        if (SelectedPlan is null) return;

        var name = SelectedPlan.DisplayText;
        var error = _app.RemovePlan(SelectedPlan.Id);
        if (!string.IsNullOrEmpty(error))
        {
            _app.Dialogs.Error("删除失败", error);
            return;
        }

        RefreshPlans();
        StatusMessage = $"已删除方案：{name}";
    }
}
