using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using Comm.App.Wpf.Services;
using Comm.Core.Errors;
using Comm.Core.Tasks;
using Comm.Core.Transport;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>候选波特率的可勾选项（R-11“未知”模式）。</summary>
public partial class BaudCandidateOption : ObservableObject
{
    public BaudCandidateOption(int baudRate, bool selected)
    {
        BaudRate = baudRate;
        _isSelected = selected;
    }

    public int BaudRate { get; }

    public string Text => $"{BaudRate.ToString(CultureInfo.InvariantCulture)}";

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// 串口 / TCP 从站地址扫描（规划 4.2，需求 R-01）。
/// R-17：增加“手动探测”模式（自选只读功能码、起始地址、数量）；手动模式的写功能码在服务层被拒绝。
/// R-11（M2.5）：增加“未知波特率 / 未知校验位”两阶段参数扫描，见规划 4.2.6 与 22.3。
/// </summary>
public partial class DeviceScanViewModel : ObservableObject, IDisposable
{
    private const string ScanPlanKind = ScanPlanKinds.Device;

    private readonly AppServices _app;
    private readonly SessionContext _session;
    private CancellationTokenSource? _cts;
    private PauseGate? _gate;

    public DeviceScanViewModel(AppServices app, SessionContext session)
    {
        _app = app;
        _session = session;
        var scan = app.Settings.Scan;
        _startAddress = scan.ScanStartAddress;
        _endAddress = scan.ScanEndAddress;
        _responseTimeoutMs = scan.ScanResponseTimeoutMs;
        _intervalMs = scan.ScanIntervalMs;

        var serial = app.Settings.Serial;
        _currentBaudRate = serial.BaudRate;
        _currentParity = serial.Parity;
        _specifiedBaudRate = serial.BaudRate;
        _specifiedParity = serial.Parity;

        foreach (var baud in SerialParameterCandidates.BaudRates)
        {
            CandidateBauds.Add(new BaudCandidateOption(baud, true));
        }

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o => !OnlyOnline || ((DeviceScanItem)o).Status != DeviceOnlineStatus.Offline;

        RefreshPlans();
        _session.Changed += OnSessionChanged;
    }

    public ObservableCollection<DeviceScanItem> Items { get; } = new();
    public ICollectionView ItemsView { get; }

    /// <summary>已保存的设备扫描方案（规划 4.12）。</summary>
    public ObservableCollection<ScanPlan> Plans { get; } = new();

    /// <summary>参数扫描的分组结果（R-11：多组合同时命中时按组合分组展示）。</summary>
    public ObservableCollection<ParameterScanGroup> ParameterGroups { get; } = new();

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

    // ---- R-11 串口参数来源
    public IReadOnlyList<Option<SerialParameterSource>> ParameterSourceOptions { get; } = new[]
    {
        new Option<SerialParameterSource>(SerialParameterSource.UseConnection, "使用连接中心参数"),
        new Option<SerialParameterSource>(SerialParameterSource.Specify, "指定"),
        new Option<SerialParameterSource>(SerialParameterSource.Unknown, "未知（自动尝试）")
    };

    public IReadOnlyList<int> BaudRateOptions { get; } = SerialParameterCandidates.BaudRates;

    public IReadOnlyList<Option<SerialParity>> ParityOptions { get; } = new[]
    {
        new Option<SerialParity>(SerialParity.None, "无校验 (N)"),
        new Option<SerialParity>(SerialParity.Even, "偶校验 (E)"),
        new Option<SerialParity>(SerialParity.Odd, "奇校验 (O)")
    };

    public IReadOnlyList<Option<StopBitsStrategy>> StopBitsOptions { get; } = new[]
    {
        new Option<StopBitsStrategy>(StopBitsStrategy.Auto, "自动（无校验时 1 与 2 都试；有校验只试 1）"),
        new Option<StopBitsStrategy>(StopBitsStrategy.One, "固定 1 位"),
        new Option<StopBitsStrategy>(StopBitsStrategy.Two, "固定 2 位")
    };

    public ObservableCollection<BaudCandidateOption> CandidateBauds { get; } = new();

    [ObservableProperty] private DeviceProbeMode _probeMode = DeviceProbeMode.AutoTemplate;
    [ObservableProperty] private byte _manualProbeFunction = Fn.ReadHoldingRegisters;
    [ObservableProperty] private int _manualProbeAddress;
    [ObservableProperty] private int _manualProbeQuantity = 1;

    [ObservableProperty] private SerialParameterSource _baudRateSource = SerialParameterSource.UseConnection;
    [ObservableProperty] private SerialParameterSource _paritySource = SerialParameterSource.UseConnection;
    [ObservableProperty] private int _specifiedBaudRate = 9600;
    [ObservableProperty] private SerialParity _specifiedParity = SerialParity.None;
    [ObservableProperty] private bool _commonFirst = true;
    [ObservableProperty] private StopBitsStrategy _stopBitsStrategy = StopBitsStrategy.Auto;
    [ObservableProperty] private string _knownAddressesText = string.Empty;
    [ObservableProperty] private bool _continueAfterHit;
    [ObservableProperty] private int _reconfigureSettleMs = 150;
    [ObservableProperty] private int _confirmAttempts = 2;
    [ObservableProperty] private bool _parameterScanAdvancedExpanded;

    private int _currentBaudRate = 9600;
    private SerialParity _currentParity = SerialParity.None;

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

    /// <summary>是否需要遍历串口参数（波特率或校验位选“未知”）。</summary>
    public bool IsParameterScan =>
        BaudRateSource == SerialParameterSource.Unknown || ParitySource == SerialParameterSource.Unknown;

    /// <summary>“未知”模式才显示候选波特率多选（规划 22.3.2）。</summary>
    public bool ShowBaudCandidates => BaudRateSource == SerialParameterSource.Unknown;

    public bool ShowSpecifiedBaudRate => BaudRateSource == SerialParameterSource.Specify;
    public bool ShowSpecifiedParity => ParitySource == SerialParameterSource.Specify;

    /// <summary>参数扫描期间底栏/状态区显示的“当前 / 原”串口参数（规划 22.3.4 第 3 点）。</summary>
    public string ConnectionContextText
    {
        get
        {
            var active = _session.ActiveSettings;
            var original = _session.OriginalSettings;
            if (active is null) return "未连接";
            if (original is null || !IsParameterScan) return active.ToDisplayString();
            return $"当前 {active.BaudRate}-{active.DataBits}-{SerialSettings.ParityCode(active.Parity)}-{SerialSettings.StopBitsCode(active.StopBits)}" +
                   $"（原 {original.BaudRate}-{original.DataBits}-{SerialSettings.ParityCode(original.Parity)}-{SerialSettings.StopBitsCode(original.StopBits)}）";
        }
    }

    /// <summary>“应用到连接中心”是否可用（选中项或推荐项带有串口参数）。</summary>
    public bool CanApplyToConnectionCenter => (SelectedItem ?? Items.FirstOrDefault(i => i.Recommended))?.Combination is not null;

    public string EstimatedText
    {
        get
        {
            if (IsParameterScan)
            {
                var parameters = BuildParameterOptions();
                if (!ParameterScanOptions.Validate(parameters, out var parameterError))
                {
                    return $"参数扫描设置不正确：{parameterError!.Message}";
                }

                var sample = new SerialCombination(
                    parameters.EffectiveBaudRates()[0],
                    parameters.EffectiveParities()[0],
                    SerialStopBits.One);
                var templates = parameters.ToDeviceScanOptions(sample, forPhaseA: false).ProbeTemplates().Count;
                var estimate = parameters.Estimate(templates);
                var commonFirstText = CommonFirst ? "常用优先" : "按波特率顺序";
                return $"参数扫描（两阶段）：{estimate.ToText()}。排序：{commonFirstText}；" +
                       "阶段 A 每个组合只发一次只读探测（03 读 1 个寄存器、起始地址 0），命中后才在完整地址段上扫描。";
            }

            var options = BuildOptions();
            var duration = options.EstimatedDuration();
            var probeTemplates = options.ProbeTemplates();
            var templateText = probeTemplates.Count == 1
                ? probeTemplates[0].Text
                : "03 → 04 → 01 → 02（地址 0、数量 1）";
            return $"地址数 {options.TotalAddresses}；探测模板 {probeTemplates.Count} 个（{templateText}）；" +
                   $"最坏情况预计耗时 {duration.TotalMinutes:F1} 分钟（无设备时每个地址都会尝试全部模板并超时）。";
        }
    }

    public string DefaultUnitIdHint => $"扫描完成后可把发现地址设为默认 Unit ID（当前默认 {_session.DefaultUnitId}）。";

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

    partial void OnSelectedItemChanged(DeviceScanItem? value) => OnPropertyChanged(nameof(CanApplyToConnectionCenter));

    partial void OnBaudRateSourceChanged(SerialParameterSource value) => RaiseParameterChanged();
    partial void OnParitySourceChanged(SerialParameterSource value) => RaiseParameterChanged();
    partial void OnSpecifiedBaudRateChanged(int value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnSpecifiedParityChanged(SerialParity value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnCommonFirstChanged(bool value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnStopBitsStrategyChanged(StopBitsStrategy value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnKnownAddressesTextChanged(string value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnContinueAfterHitChanged(bool value) => OnPropertyChanged(nameof(EstimatedText));
    partial void OnConfirmAttemptsChanged(int value) => OnPropertyChanged(nameof(EstimatedText));

    private void RaiseParameterChanged()
    {
        OnPropertyChanged(nameof(IsParameterScan));
        OnPropertyChanged(nameof(ShowBaudCandidates));
        OnPropertyChanged(nameof(ShowSpecifiedBaudRate));
        OnPropertyChanged(nameof(ShowSpecifiedParity));
        OnPropertyChanged(nameof(EstimatedText));
        OnPropertyChanged(nameof(ConnectionContextText));
    }

    private DeviceScanOptions BuildOptions() => new()
    {
        StartAddress = (byte)Math.Clamp(StartAddress, 1, 247),
        EndAddress = (byte)Math.Clamp(EndAddress, 1, 247),
        ResponseTimeoutMs = Math.Max(50, ResponseTimeoutMs),
        RetryCount = Math.Max(0, RetryCount),
        IntervalMs = Math.Max(0, IntervalMs),
        ConfirmFound = ConfirmFound,
        ConfirmAttempts = ConfirmFound ? Math.Max(1, Math.Clamp(ConfirmAttempts, 0, 5)) : Math.Clamp(ConfirmAttempts, 0, 5),
        TryReadDeviceIdentification = TryDeviceIdentification,
        Mode = ProbeMode,
        ManualProbeFunction = ManualProbeFunction,
        ManualProbeAddress = (ushort)Math.Clamp(ManualProbeAddress, 0, 65535),
        ManualProbeQuantity = (ushort)Math.Clamp(ManualProbeQuantity, 1, 2000)
    };

    private ParameterScanOptions BuildParameterOptions() => new()
    {
        StartAddress = (byte)Math.Clamp(StartAddress, 1, 247),
        EndAddress = (byte)Math.Clamp(EndAddress, 1, 247),
        ResponseTimeoutMs = Math.Max(50, ResponseTimeoutMs),
        RetryCount = Math.Max(0, RetryCount),
        IntervalMs = Math.Max(0, IntervalMs),
        TryReadDeviceIdentification = TryDeviceIdentification,
        BaudRateSource = BaudRateSource,
        ParitySource = ParitySource,
        CurrentBaudRate = _currentBaudRate,
        CurrentParity = _currentParity,
        SpecifiedBaudRate = SpecifiedBaudRate,
        SpecifiedParity = SpecifiedParity,
        CandidateBaudRates = CandidateBauds.Where(c => c.IsSelected).Select(c => c.BaudRate).ToArray(),
        CommonFirst = CommonFirst,
        StopBits = StopBitsStrategy,
        KnownAddresses = AddressListParser.Parse(KnownAddressesText),
        ContinueAfterHit = ContinueAfterHit,
        ReconfigureSettleMs = Math.Clamp(ReconfigureSettleMs, 0, 5000),
        ConfirmAttempts = Math.Clamp(ConfirmAttempts, 0, 5),
        ProbeMode = ProbeMode,
        ManualProbeFunction = ManualProbeFunction,
        ManualProbeAddress = (ushort)Math.Clamp(ManualProbeAddress, 0, 65535),
        ManualProbeQuantity = (ushort)Math.Clamp(ManualProbeQuantity, 1, 2000)
    };

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsParameterScan)
        {
            await StartParameterScanAsync().ConfigureAwait(true);
            return;
        }

        var service = _session.CreateDeviceScanService();
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

        SaveScanDefaults(options);

        PrepareRun();
        ParameterGroups.Clear();

        // 关键（M1 反馈问题 8）：新任务必须立即清掉上一轮的终态提示，否则界面会显示旧结论。
        var templateText = options.ProbeTemplates().Count == 1 ? options.ProbeTemplates()[0].Text : "自动模板 03 → 04 → 01 → 02";
        StatusMessage = $"正在扫描：地址 {options.StartAddress} ~ {options.EndAddress}，探测 {templateText}";

        _app.Log.Info("DeviceScan",
            $"开始设备扫描：地址 {options.StartAddress}~{options.EndAddress}，方式 {options.Mode}，探测 {templateText}，" +
            $"超时 {options.ResponseTimeoutMs} ms，重试 {options.RetryCount}，间隔 {options.IntervalMs} ms");

        try
        {
            var results = await service.ScanAsync(options, _gate, BuildProgress(), BuildItemProgress(), _cts!.Token)
                .ConfigureAwait(true);
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
            FinishRun();
        }
    }

    /// <summary>R-11：未知波特率 / 未知校验位的两阶段参数扫描。</summary>
    private async Task StartParameterScanAsync()
    {
        if (!_session.IsSerialConnected)
        {
            _app.Dialogs.Warn("需要串口连接",
                "未知波特率/校验位扫描需要在运行中重开串口，因此必须先连接串口（TCP 会话不支持）。");
            return;
        }

        if (!_session.CanReconfigure)
        {
            _app.Dialogs.Warn("当前会话不支持", "当前会话不支持串口参数重配，请重新打开串口后再试。");
            return;
        }

        var options = BuildParameterOptions();
        if (!ParameterScanOptions.Validate(options, out var error))
        {
            _app.Dialogs.ShowError(error!, "参数扫描设置不正确");
            return;
        }

        var estimate = options.Estimate();
        if (estimate.RequiresConfirmation &&
            !_app.Dialogs.Confirm("预计耗时较长",
                $"{estimate.ToText()}\n\n是否继续？\n\n（可随时点“停止”，程序会恢复原来的串口参数）"))
        {
            return;
        }

        // 规划 22.3.4 第 5 点：总线影响提示。
        if (!_app.Dialogs.Confirm("总线影响提示",
                "扫描期间会在总线上以不同波特率发送只读请求。\n" +
                "同一总线上的其他设备可能把这些请求视为噪声，请确认现场可以承受。\n\n是否开始？"))
        {
            return;
        }

        SaveScanDefaults(BuildOptions());

        var originalText = _session.OriginalSettings?.ToDisplayString() ?? "(未知)";
        bool watchWasRunning = _session.WatchList.IsRunning;
        if (watchWasRunning)
        {
            _session.WatchList.Stop("参数扫描占用串口，监视表已暂停");
        }

        PrepareRun();
        ParameterGroups.Clear();
        StatusMessage = $"参数扫描中：{estimate.CombinationCount} 个候选组合；原串口参数 {originalText}";

        _app.Log.Info("DeviceScan",
            $"开始参数扫描：{estimate.CombinationCount} 个组合，阶段 A 地址 {string.Join(",", options.PhaseATargets())}，" +
            $"地址范围 {options.StartAddress}~{options.EndAddress}，超时 {options.ResponseTimeoutMs} ms，" +
            $"原参数 {originalText}");

        try
        {
            var service = new ParameterScanService(
                () => _session.Current,
                _session,
                message => _app.Log.Info("ParameterScan", message));

            var result = await service
                .ScanAsync(options, _gate, BuildProgress(), BuildItemProgress(), _cts!.Token)
                .ConfigureAwait(true);

            // 服务端在扫描结束后才标注“推荐”，因此用最终结果重建列表（顺序稳定且带推荐标记）。
            Items.Clear();
            foreach (var item in result.Items) Items.Add(item);
            foreach (var group in result.Groups) ParameterGroups.Add(group);

            StatusMessage = result.Summary;
            if (result.Failures.Count > 0)
            {
                StatusMessage += $" 有 {result.Failures.Count} 个组合未完成。";
                foreach (var failure in result.Failures) _app.Log.Warning("ParameterScan", failure);
            }

            if (!result.Restored)
            {
                _app.Log.Error("ParameterScan", $"恢复原串口参数失败：{result.RestoreError}");
                _app.Dialogs.Error("串口参数未恢复",
                    $"恢复原串口参数失败：{result.RestoreError}\n\n" +
                    "连接已置为“未连接”；请在连接中心确认参数后重新连接。");
            }

            _app.Log.Info("DeviceScan", StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = $"参数扫描已停止。已收集 {Items.Count} 条结果；原串口参数已恢复。";
            _app.Log.Info("DeviceScan", "参数扫描被用户停止");
        }
        catch (CommException ex)
        {
            StatusMessage = ex.Error.ToUserText();
            _app.Dialogs.ShowError(ex.Error, "参数扫描无法开始");
        }
        catch (Exception ex)
        {
            StatusMessage = $"参数扫描异常终止：{ex.Message}";
            _app.Log.Error("DeviceScan", "参数扫描异常终止", ex);
            _app.Dialogs.Error("参数扫描失败", ex.Message);
        }
        finally
        {
            FinishRun();
            if (watchWasRunning)
            {
                _session.WatchList.Start(TimeSpan.FromMilliseconds(Math.Max(200, _app.Settings.Scan.WatchTableIntervalMs)));
                _app.Notify("参数扫描结束，监视表已恢复周期刷新。");
            }
        }
    }

    private void SaveScanDefaults(DeviceScanOptions options) =>
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

    private void PrepareRun()
    {
        Items.Clear();
        _cts = new CancellationTokenSource();
        _gate = new PauseGate();
        IsRunning = true;
        IsPaused = false;
        ProgressPercent = 0;
        ProgressText = "开始扫描…";
        RecentEventText = string.Empty;
    }

    private void FinishRun()
    {
        IsRunning = false;
        IsPaused = false;
        _cts?.Dispose();
        _cts = null;
        _gate = null;
        _app.Tasks.Idle();
        OnPropertyChanged(nameof(ConnectionContextText));
        OnPropertyChanged(nameof(CanApplyToConnectionCenter));
    }

    private IProgress<ScanProgress> BuildProgress() => new Progress<ScanProgress>(p =>
    {
        ProgressText = p.ToText();
        ProgressPercent = p.Percent;
        _app.Tasks.Report(p);
        OnPropertyChanged(nameof(ConnectionContextText));
    });

    private IProgress<DeviceScanItem> BuildItemProgress() => new Progress<DeviceScanItem>(item =>
    {
        Items.Add(item);
        if (item.Status != DeviceOnlineStatus.Offline)
        {
            var where = item.Combination is null ? string.Empty : $"【{item.Combination.Text}】";
            RecentEventText = $"最近发现：{where}地址 {item.Address}（{item.StatusText}）{item.Note}";
        }
    });

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
        ParameterGroups.Clear();
        StatusMessage = string.Empty;
        RecentEventText = string.Empty;
        ProgressText = "未开始";
        ProgressPercent = 0;
        OnPropertyChanged(nameof(CanApplyToConnectionCenter));
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

        var filter = IsParameterScan
            ? $"参数扫描；起始 {StartAddress}，结束 {EndAddress}，超时 {ResponseTimeoutMs} ms；" +
              $"波特率 {BaudRateSource}，校验位 {ParitySource}"
            : $"起始 {StartAddress}，结束 {EndAddress}，超时 {ResponseTimeoutMs} ms，" +
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
        _session.SetDefaultUnitId(SelectedItem.Address);
        _app.Update(s => s with { Tcp = s.Tcp with { UnitId = SelectedItem.Address } });
        StatusMessage = $"已将地址 {SelectedItem.Address} 设为默认 Unit ID，可在数据扫描中使用。";
        OnPropertyChanged(nameof(DefaultUnitIdHint));
    }

    /// <summary>R-11：把选中结果（或推荐结果）的串口参数一键回填到连接中心。</summary>
    [RelayCommand]
    private void ApplyToConnectionCenter()
    {
        var item = SelectedItem ?? Items.FirstOrDefault(i => i.Recommended);
        if (item?.Combination is null)
        {
            _app.Dialogs.Warn("没有可用参数", "请先在结果表中选择一条带“波特率 / 校验 / 停止位”的结果。");
            return;
        }

        bool reconnect = false;
        if (_session.IsSerialConnected)
        {
            reconnect = _app.Dialogs.Confirm("应用并重连",
                $"将把 {item.Combination.Text} 应用到连接中心，并立即重连串口。\n\n是否继续？");
        }

        _app.RequestSerialParameters(item.Combination, reconnect);
        StatusMessage = reconnect
            ? $"已应用 {item.Combination.Text} 并请求重连。"
            : $"已把 {item.Combination.Text} 回填到连接中心（到连接中心点击“打开串口”即可生效）。";
    }

    /// <summary>把选中地址加入监视表（M2-4 联动）。</summary>
    [RelayCommand]
    private void AddToWatch()
    {
        if (SelectedItem is null) return;

        _session.WatchList.Add(new RegisterPoint
        {
            Name = $"从站 {SelectedItem.Address}",
            UnitId = SelectedItem.Address,
            Area = DataArea.HoldingRegisters,
            Address = 0,
            Type = DataType.UInt16,
            Note = $"来自设备扫描（探测功能码 {SelectedItem.ProbeFunctionText}" +
                   (SelectedItem.Combination is null ? "）" : $"；参数 {SelectedItem.Combination.Text}）")
        });

        StatusMessage = _session.WatchList.StatusMessage + "（可在“监视表”页调整地址与数据类型）";
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
            ? $"{options.StartAddress}-{options.EndAddress}" +
              (IsParameterScan ? " 参数扫描" : options.Mode == DeviceProbeMode.Manual ? " 手动" : string.Empty)
            : PlanName.Trim();

        // R-11：方案负载升级为 DeviceScanPlan（含串口参数扫描设置）。旧方案仍可读取（见 LoadPlan）。
        var payload = new DeviceScanPlan { Scan = options, Parameters = BuildParameterOptions() };
        var message = _app.SavePlan(ScanPlan.Create(name, ScanPlanKind, payload));
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

        // 新形状（v0.2.5 起）
        var payload = plan.ReadPayload<DeviceScanPlan>();
        DeviceScanOptions? options = payload?.Scan;
        ParameterScanOptions? parameters = payload?.Parameters;

        // 旧形状（M2 及以前）：扁平保存的就是 DeviceScanOptions，缺字段走默认值 → “使用连接中心参数”。
        if (parameters is null)
        {
            var legacy = plan.ReadPayload<DeviceScanOptions>();
            if (legacy is not null) options = legacy;
        }

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

        if (parameters is not null)
        {
            BaudRateSource = parameters.BaudRateSource;
            ParitySource = parameters.ParitySource;
            SpecifiedBaudRate = parameters.SpecifiedBaudRate;
            SpecifiedParity = parameters.SpecifiedParity;
            CommonFirst = parameters.CommonFirst;
            StopBitsStrategy = parameters.StopBits;
            ContinueAfterHit = parameters.ContinueAfterHit;
            ReconfigureSettleMs = parameters.ReconfigureSettleMs;
            ConfirmAttempts = parameters.ConfirmAttempts;
            KnownAddressesText = parameters.KnownAddresses is { Count: > 0 }
                ? string.Join(",", parameters.KnownAddresses)
                : string.Empty;

            var selected = parameters.CandidateBaudRates.ToHashSet();
            foreach (var candidate in CandidateBauds) candidate.IsSelected = selected.Contains(candidate.BaudRate);
        }
        else
        {
            // 旧方案：串口参数一律“使用连接中心参数”。
            BaudRateSource = SerialParameterSource.UseConnection;
            ParitySource = SerialParameterSource.UseConnection;
        }

        PlanName = plan.Name;
        OnPropertyChanged(nameof(IsParameterScan));
        OnPropertyChanged(nameof(EstimatedText));

        StatusMessage = $"已载入方案：{plan.DisplayText}" + (parameters is null ? "（旧版方案：串口参数使用连接中心参数）" : string.Empty);
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

    // ---------------------------------------------------------------- 会话事件与释放（M3b：页面属于某个会话）

    private void OnSessionChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(ConnectionContextText));

    /// <summary>关闭会话时由工作区调用：解除订阅。</summary>
    public void Dispose() => _session.Changed -= OnSessionChanged;

}
