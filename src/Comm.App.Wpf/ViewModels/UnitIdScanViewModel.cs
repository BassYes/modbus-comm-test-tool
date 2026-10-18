using System.Collections.ObjectModel;
using System.Globalization;
using Comm.App.Wpf.Services;
using Comm.Core.Tasks;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>
/// TCP Unit ID 扫描（R-12，规划 4.2.7）：Modbus TCP 网关后面挂 RS-485 总线时，
/// 对指定 IP 逐个 Unit ID 找出在线从站。探测规则与串口设备扫描完全一致（复用 <see cref="DeviceScanService"/>），
/// 默认串行，并发度可调（1~8，网关常限制并发连接数）。
/// </summary>
public partial class UnitIdScanViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _app;
    private readonly SessionContext _session;
    private CancellationTokenSource? _cts;
    private PauseGate? _gate;
    private Task<IReadOnlyList<UnitIdScanItem>>? _scanTask;
    private ScanPlan? _selectedPlan;

    public UnitIdScanViewModel(AppServices app, SessionContext session)
    {
        _app = app;
        _session = session;

        var scan = app.Settings.Scan;
        _responseTimeoutMs = scan.ScanResponseTimeoutMs;
        _intervalMs = scan.ScanIntervalMs;

        _session.Changed += OnSessionChanged;
        RefreshPlanList();
        UpdateEstimated();
        RefreshStatus();
    }

    // ------------------------------------------------------------------ 扫描参数

    [ObservableProperty] private int _startUnitId = 1;
    [ObservableProperty] private int _endUnitId = 247;
    [ObservableProperty] private int _responseTimeoutMs = 500;
    [ObservableProperty] private int _retryCount;
    [ObservableProperty] private int _intervalMs = 20;
    [ObservableProperty] private int _concurrency = 1;
    [ObservableProperty] private int _confirmAttempts;
    [ObservableProperty] private bool _tryReadIdentification = true;

    /// <summary>手动探测模式（R-17 同款：只允许只读功能码 01/02/03/04）。</summary>
    [ObservableProperty] private bool _useManualProbe;
    [ObservableProperty] private byte _manualProbeFunction = Fn.ReadHoldingRegisters;
    [ObservableProperty] private int _manualProbeAddress;
    [ObservableProperty] private int _manualProbeQuantity = 1;

    public IReadOnlyList<Option<byte>> ReadFunctionOptions { get; } = new[]
    {
        new Option<byte>(Fn.ReadHoldingRegisters, "03 读保持寄存器"),
        new Option<byte>(Fn.ReadInputRegisters, "04 读输入寄存器"),
        new Option<byte>(Fn.ReadCoils, "01 读线圈"),
        new Option<byte>(Fn.ReadDiscreteInputs, "02 读离散输入")
    };

    /// <summary>并发度选项（规划 13.4.2：默认串行，可调）。</summary>
    public IReadOnlyList<Option<int>> ConcurrencyOptions { get; } = new[]
    {
        new Option<int>(1, "1（串行，推荐）"),
        new Option<int>(2, "2 并发"),
        new Option<int>(4, "4 并发"),
        new Option<int>(8, "8 并发")
    };

    [ObservableProperty] private string _estimatedText = "-";
    [ObservableProperty] private string _connectionContextText = "未连接";

    partial void OnStartUnitIdChanged(int value) => UpdateEstimated();
    partial void OnEndUnitIdChanged(int value) => UpdateEstimated();
    partial void OnResponseTimeoutMsChanged(int value) => UpdateEstimated();
    partial void OnRetryCountChanged(int value) => UpdateEstimated();
    partial void OnIntervalMsChanged(int value) => UpdateEstimated();
    partial void OnConcurrencyChanged(int value) => UpdateEstimated();
    partial void OnConfirmAttemptsChanged(int value) => UpdateEstimated();
    partial void OnUseManualProbeChanged(bool value) => UpdateEstimated();
    partial void OnManualProbeFunctionChanged(byte value) => UpdateEstimated();

    private UnitIdScanOptions BuildOptions() => new()
    {
        StartUnitId = (byte)Math.Clamp(StartUnitId, 0, 255),
        EndUnitId = (byte)Math.Clamp(EndUnitId, 0, 255),
        ResponseTimeoutMs = Math.Max(50, ResponseTimeoutMs),
        RetryCount = Math.Max(0, RetryCount),
        IntervalMs = Math.Max(0, IntervalMs),
        ConfirmAttempts = Math.Max(0, ConfirmAttempts),
        TryReadDeviceIdentification = TryReadIdentification,
        Mode = UseManualProbe ? DeviceProbeMode.Manual : DeviceProbeMode.AutoTemplate,
        ManualProbeFunction = ManualProbeFunction,
        ManualProbeAddress = (ushort)Math.Clamp(ManualProbeAddress, 0, 65535),
        ManualProbeQuantity = (ushort)Math.Clamp(ManualProbeQuantity, 1, 2000),
        Concurrency = Math.Clamp(Concurrency, 1, UnitIdScanOptions.MaxConcurrency)
    };

    private void UpdateEstimated()
    {
        var options = BuildOptions();
        EstimatedText = UnitIdScanOptions.Validate(options, out var error)
            ? options.EstimatedDurationText()
            : "参数非法：" + error!.Message;
    }

    // ------------------------------------------------------------------ 状态与结果

    public ObservableCollection<UnitIdScanItem> Results { get; } = new();

    [ObservableProperty] private UnitIdScanItem? _selectedResult;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _progressText = "尚未开始。";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _summaryText = "在线 0 / 疑似 0 / 未发现 0";
    [ObservableProperty] private string _statusMessage = "选择“开始扫描”后，将逐个 Unit ID 发送只读探测请求（绝不使用写功能码或广播）。";

    public bool CanStart => _session.IsTcpConnected && !IsScanning;

    public string DetailText => SelectedResult is null
        ? "选中一行结果可查看详情。"
        : $"Unit ID：{SelectedResult.UnitId}\n状态：{SelectedResult.StatusText}\n" +
          $"探测功能码：{SelectedResult.ProbeFunctionText}\n" +
          $"异常码：{(string.IsNullOrEmpty(SelectedResult.ExceptionText) ? "-" : SelectedResult.ExceptionText)}\n" +
          $"首次发现：{SelectedResult.FirstSeen:yyyy-MM-dd HH:mm:ss.fff}\n" +
          $"最近响应：{SelectedResult.LastSeen:yyyy-MM-dd HH:mm:ss.fff}\n" +
          $"响应耗时：{SelectedResult.ElapsedMs:F1} ms\n" +
          $"确认：{(string.IsNullOrEmpty(SelectedResult.ConfirmText) ? "-" : SelectedResult.ConfirmText)}\n" +
          $"设备标识：{SelectedResult.DeviceIdentification ?? "-"}\n" +
          $"备注：{SelectedResult.Note}";

    partial void OnSelectedResultChanged(UnitIdScanItem? value) => OnPropertyChanged(nameof(DetailText));

    private void OnSessionChanged(object? sender, EventArgs e) => RefreshStatus();

    private void RefreshStatus()
    {
        ConnectionContextText = _session.IsTcpConnected
            ? $"已连接 {_session.Snapshot?.Endpoint}（{_session.Current?.Codec.Name}）——可扫描该网关后面的 Unit ID"
            : "未连接 Modbus TCP。Unit ID 扫描需要先在“连接中心”建立 TCP 连接（网关后面挂 RS-485 总线的场景）。";

        OnPropertyChanged(nameof(CanStart));
    }

    // ------------------------------------------------------------------ 扫描命令

    [RelayCommand]
    private async Task StartScanAsync()
    {
        if (!_session.IsTcpConnected)
        {
            _app.Dialogs.Warn("需要 Modbus TCP 连接", "请先在“连接中心”建立到网关的 TCP 连接，再执行 Unit ID 扫描。");
            return;
        }

        var service = _session.CreateUnitIdScanService();
        if (service is null)
        {
            _app.Dialogs.Warn("无法建立扫描服务", "当前会话没有可用的 TCP 连接参数。");
            return;
        }

        var options = BuildOptions();
        if (!UnitIdScanOptions.Validate(options, out var error))
        {
            _app.Dialogs.Warn("扫描参数不合法", error!.ToUserText());
            return;
        }

        if (options.Concurrency > 1 &&
            !_app.Dialogs.Confirm("并发扫描确认",
                $"将以 {options.Concurrency} 条并行连接扫描 Unit ID {options.StartUnitId}~{options.EndUnitId}。\n\n" +
                "⚠ 部分 Modbus TCP 网关只允许 1~2 条并发连接；并发过高可能被网关拒绝或影响现场轮询。\n" +
                "⚠ 扫描期间会在总线上发送大量的只读探测请求。\n\n确认继续吗？"))
        {
            return;
        }

        Results.Clear();
        IsScanning = true;
        IsPaused = false;
        _cts = new CancellationTokenSource();
        _gate = new PauseGate();
        OnPropertyChanged(nameof(CanStart));

        _app.Log.Info("UnitIdScan",
            $"开始 Unit ID 扫描：{options.SummaryText}；预计 {options.EstimatedDurationText()}");

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressText = p.ToText();
            ProgressPercent = p.Percent;
        });

        var itemProgress = new Progress<UnitIdScanItem>(item =>
        {
            // 结果按 Unit ID 排序插入，避免并发完成顺序导致表格乱序。
            int index = 0;
            while (index < Results.Count && Results[index].UnitId < item.UnitId) index++;
            if (index < Results.Count && Results[index].UnitId == item.UnitId) Results[index] = item;
            else Results.Insert(index, item);

            UpdateSummary();
        });

        var token = _cts.Token;
        _scanTask = Task.Run(() => service.ScanAsync(options, _gate, progress, itemProgress, token));

        try
        {
            var items = await _scanTask.ConfigureAwait(true);
            UpdateSummary();
            StatusMessage = $"扫描完成：共 {items.Count} 个 Unit ID，在线 {items.Count(i => i.Status == DeviceOnlineStatus.Online)} 个，" +
                            $"疑似 {items.Count(i => i.Status == DeviceOnlineStatus.Suspected)} 个。";
            _app.Log.Info("UnitIdScan", StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"扫描已停止（已完成 {Results.Count} 个 Unit ID，结果保留）。";
            _app.Log.Info("UnitIdScan", StatusMessage);
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描失败：{ex.Message}";
            _app.Log.Error("UnitIdScan", StatusMessage, ex);
            _app.Dialogs.Error("Unit ID 扫描失败", ex.Message);
        }
        finally
        {
            IsScanning = false;
            IsPaused = false;
            OnPropertyChanged(nameof(CanStart));
        }
    }

    [RelayCommand]
    private void PauseScan()
    {
        if (!IsScanning || _gate is null) return;
        _gate.Pause();
        IsPaused = true;
        StatusMessage = "已暂停（在当前请求结束后的下一个请求边界生效）。";
    }

    [RelayCommand]
    private void ResumeScan()
    {
        if (!IsScanning || _gate is null) return;
        _gate.Resume();
        IsPaused = false;
        StatusMessage = "已继续扫描。";
    }

    [RelayCommand]
    private void StopScan()
    {
        if (!IsScanning || _cts is null) return;
        _cts.Cancel();
        StatusMessage = "正在停止……（1 秒内不再发出新请求）";
    }

    [RelayCommand]
    private void ClearResults()
    {
        Results.Clear();
        UpdateSummary();
        StatusMessage = "已清空扫描结果（不影响已导出文件）。";
    }

    private void UpdateSummary()
    {
        int online = Results.Count(i => i.Status == DeviceOnlineStatus.Online);
        int suspected = Results.Count(i => i.Status == DeviceOnlineStatus.Suspected);
        SummaryText = $"在线 {online} / 疑似 {suspected} / 未发现 {Results.Count - online - suspected}（共 {Results.Count} 个 Unit ID）";
    }

    // ------------------------------------------------------------------ 结果操作

    [RelayCommand]
    private void ExportCsv()
    {
        if (Results.Count == 0)
        {
            _app.Dialogs.Warn("没有结果可导出", "请先执行一次 Unit ID 扫描。");
            return;
        }

        var path = _app.Dialogs.SaveFile("导出 Unit ID 扫描结果", "CSV 文件 (*.csv)|*.csv",
            $"UnitID扫描-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            System.IO.File.WriteAllText(path, UnitIdScanService.ExportCsv(Results.ToArray()), System.Text.Encoding.UTF8);
            StatusMessage = $"已导出 {Results.Count} 条结果到 {path}";
            _app.Log.Info("UnitIdScan", StatusMessage);
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("导出失败", ex.Message);
        }
    }

    [RelayCommand]
    private void UseSelectedUnitId()
    {
        if (SelectedResult is null) return;
        _session.SetDefaultUnitId(SelectedResult.UnitId);
        StatusMessage = $"已把 Unit ID {SelectedResult.UnitId} 设为默认（后续数据扫描/监视表会使用它）。";
    }

    [RelayCommand]
    private void SaveAsTarget()
    {
        if (SelectedResult is null) return;

        var endpoint = _session.Snapshot?.Endpoint ?? string.Empty;
        var target = new DeviceTarget
        {
            Name = $"Unit {SelectedResult.UnitId} @ {endpoint}",
            Kind = ConnectionKindHint.Tcp,
            Endpoint = endpoint,
            UnitId = SelectedResult.UnitId,
            Notes = "由 Unit ID 扫描创建"
        };

        var error = _app.SaveTarget(target);
        StatusMessage = string.IsNullOrEmpty(error)
            ? $"已保存设备目标：{target.DisplayText}"
            : error;
    }

    [RelayCommand]
    private void CopySelectedRow()
    {
        if (SelectedResult is null) return;
        try
        {
            System.Windows.Clipboard.SetText(DetailText);
            StatusMessage = "已复制选中结果详情到剪贴板。";
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("复制失败", ex.Message);
        }
    }

    // ------------------------------------------------------------------ 扫描方案（规划 4.2.5、4.12）

    public ObservableCollection<ScanPlan> Plans { get; } = new();

    [ObservableProperty] private string _planName = "Unit ID 扫描方案";

    private void RefreshPlanList()
    {
        Plans.Clear();
        foreach (var plan in _app.PlansOfKind(ScanPlanKinds.UnitId)) Plans.Add(plan);
    }

    [RelayCommand]
    private void SavePlan()
    {
        var name = string.IsNullOrWhiteSpace(PlanName) ? "Unit ID 扫描方案" : PlanName.Trim();
        var plan = ScanPlan.Create(name, ScanPlanKinds.UnitId, new UnitIdScanPlanPayload
        {
            Options = BuildOptions(),
            UseManualProbe = UseManualProbe,
            ReadIdentification = TryReadIdentification
        });

        var error = _app.SavePlan(plan);
        RefreshPlanList();
        StatusMessage = string.IsNullOrEmpty(error) ? $"已保存扫描方案「{name}」。" : error;
    }

    [RelayCommand]
    private void LoadPlan()
    {
        if (_selectedPlan is null) return;

        var payload = _selectedPlan.ReadPayload<UnitIdScanPlanPayload>();
        if (payload?.Options is null)
        {
            _app.Dialogs.Warn("方案无法读取", "该方案的负载为空或格式不兼容（旧方案请重新设置参数）。");
            return;
        }

        var options = payload.Options;
        StartUnitId = options.StartUnitId;
        EndUnitId = options.EndUnitId;
        ResponseTimeoutMs = options.ResponseTimeoutMs;
        RetryCount = options.RetryCount;
        IntervalMs = options.IntervalMs;
        Concurrency = options.Concurrency;
        ConfirmAttempts = options.ConfirmAttempts;
        TryReadIdentification = options.TryReadDeviceIdentification;
        UseManualProbe = payload.UseManualProbe;
        ManualProbeFunction = options.ManualProbeFunction;
        ManualProbeAddress = options.ManualProbeAddress;
        ManualProbeQuantity = options.ManualProbeQuantity;
        PlanName = _selectedPlan.Name;

        StatusMessage = $"已载入扫描方案「{_selectedPlan.Name}」（{options.SummaryText}）。";
    }

    [RelayCommand]
    private void DeletePlan()
    {
        if (_selectedPlan is null) return;
        var name = _selectedPlan.DisplayText;
        var error = _app.RemovePlan(_selectedPlan.Id);
        RefreshPlanList();
        StatusMessage = string.IsNullOrEmpty(error) ? $"已删除扫描方案：{name}" : error;
    }

    /// <summary>选中的扫描方案（界面直接绑定）。</summary>
    public ScanPlan? SelectedPlan
    {
        get => _selectedPlan;
        set
        {
            if (!SetProperty(ref _selectedPlan, value)) return;
            OnPropertyChanged(nameof(SelectedPlan));
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _session.Changed -= OnSessionChanged;
    }
}

/// <summary>Unit ID 扫描方案的负载（用 <see cref="ScanPlan"/> 持久化，字段缺失时按默认值处理）。</summary>
public sealed record UnitIdScanPlanPayload
{
    public UnitIdScanOptions? Options { get; init; }
    public bool UseManualProbe { get; init; }
    public bool ReadIdentification { get; init; } = true;
}
