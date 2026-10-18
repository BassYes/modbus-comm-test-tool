using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using Comm.App.Wpf.Services;
using Comm.Core;
using Comm.Core.Tasks;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>
/// 寄存器定位（M2-1，规划 4.4，需求 R-03）。
/// 向导式三步：范围与目标 → 采样 / 快照 → 候选列表；候选项必须同时显示协议地址、原始寄存器与解释规则。
/// </summary>
public partial class RegisterLocatorViewModel : ObservableObject
{
    private readonly AppServices _app;
    private CancellationTokenSource? _cts;
    private PauseGate? _gate;

    public RegisterLocatorViewModel(AppServices app)
    {
        _app = app;
        var scan = app.Settings.Scan;
        _area = scan.DefaultArea == DataArea.InputRegisters ? DataArea.InputRegisters : DataArea.HoldingRegisters;
        _unitId = app.Sessions.DefaultUnitId;

        CandidatesView = CollectionViewSource.GetDefaultView(Candidates);
        CandidatesView.Filter = o => !OnlyHighScore || ((RegisterCandidate)o).MatchScore >= MinScore;
    }

    public ObservableCollection<RegisterCandidate> Candidates { get; } = new();
    public ICollectionView CandidatesView { get; }

    public IReadOnlyList<Option<DataArea>> AreaOptions { get; } = new[]
    {
        new Option<DataArea>(DataArea.HoldingRegisters, "保持寄存器 (03)"),
        new Option<DataArea>(DataArea.InputRegisters, "输入寄存器 (04)")
    };

    public IReadOnlyList<Option<LocatorMode>> ModeOptions { get; } = new[]
    {
        new Option<LocatorMode>(LocatorMode.Exact, "精确值（可叠加容差）"),
        new Option<LocatorMode>(LocatorMode.Range, "数值区间"),
        new Option<LocatorMode>(LocatorMode.Tolerance, "容差区间"),
        new Option<LocatorMode>(LocatorMode.Change, "值发生变化"),
        new Option<LocatorMode>(LocatorMode.Trend, "持续增大 / 减小（≥3 次采样）"),
        new Option<LocatorMode>(LocatorMode.BitChange, "位发生变化"),
        new Option<LocatorMode>(LocatorMode.Text, "ASCII 文本片段")
    };

    public IReadOnlyList<Option<ByteOrder>> OrderOptions { get; } = Enum.GetValues<ByteOrder>()
        .Select(o => new Option<ByteOrder>(o, o.ToDisplay())).ToArray();

    // ---- 范围与目标
    [ObservableProperty] private byte _unitId = 1;
    [ObservableProperty] private DataArea _area = DataArea.HoldingRegisters;
    [ObservableProperty] private int _startAddress;
    [ObservableProperty] private int _endAddress = 99;
    [ObservableProperty] private LocatorMode _mode = LocatorMode.Exact;
    [ObservableProperty] private double _target;
    [ObservableProperty] private double _targetMin;
    [ObservableProperty] private double _targetMax;
    [ObservableProperty] private double _tolerance;
    [ObservableProperty] private double _tolerancePercent;
    [ObservableProperty] private string _textPattern = string.Empty;

    // ---- 解释组合
    [ObservableProperty] private bool _typeUInt16 = true;
    [ObservableProperty] private bool _typeInt16 = true;
    [ObservableProperty] private bool _typeUInt32 = true;
    [ObservableProperty] private bool _typeInt32 = true;
    [ObservableProperty] private bool _typeFloat32 = true;
    [ObservableProperty] private bool _typeUInt64;
    [ObservableProperty] private bool _typeInt64;
    [ObservableProperty] private bool _typeFloat64;
    [ObservableProperty] private string _scalesText = "1, 0.1, 0.01, 10";
    [ObservableProperty] private ByteOrder _candidateOrder = DefaultCandidateOrder;
    [ObservableProperty] private bool _allByteOrders = true;

    private const ByteOrder DefaultCandidateOrder = ByteOrder.Abcd;

    // ---- 采样与读取
    [ObservableProperty] private int _sampleCount = 2;
    [ObservableProperty] private int _sampleIntervalMs = 1000;
    [ObservableProperty] private int _maxResults = 200;
    [ObservableProperty] private bool _excludeNonFinite = true;
    [ObservableProperty] private int _responseTimeoutMs = 1000;
    [ObservableProperty] private int _retryCount = 1;
    [ObservableProperty] private int _intervalMs = 20;

    // ---- 状态
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _progressText = "未开始";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private RegisterCandidate? _selectedCandidate;
    [ObservableProperty] private bool _onlyHighScore;
    [ObservableProperty] private int _minScore = 80;

    public string RangeHint
    {
        get
        {
            int addresses = Math.Max(0, EndAddress - StartAddress + 1);
            int combos = CandidateTypes.Count * CandidateOrders.Count * CandidateScales.Count;
            int rounds = Mode is LocatorMode.Change or LocatorMode.Trend or LocatorMode.BitChange
                ? Math.Clamp(SampleCount, 2, 10)
                : 1;
            int perPass = (int)Math.Ceiling(addresses / (double)Math.Min(125, Math.Max(1, addresses == 0 ? 1 : addresses)));
            return $"地址 {addresses} 个；候选解释组合 {combos} 种；采样 {rounds} 轮" +
                   $"（每轮至少 {Math.Max(1, perPass)} 次读取）；只排序不自动确认，请人工复核。";
        }
    }

    public IReadOnlyList<DataType> CandidateTypes
    {
        get
        {
            var types = new List<DataType>();
            if (TypeUInt16) types.Add(DataType.UInt16);
            if (TypeInt16) types.Add(DataType.Int16);
            if (TypeUInt32) types.Add(DataType.UInt32);
            if (TypeInt32) types.Add(DataType.Int32);
            if (TypeFloat32) types.Add(DataType.Float32);
            if (TypeUInt64) types.Add(DataType.UInt64);
            if (TypeInt64) types.Add(DataType.Int64);
            if (TypeFloat64) types.Add(DataType.Float64);
            return types;
        }
    }

    public IReadOnlyList<ByteOrder> CandidateOrders => AllByteOrders
        ? Enum.GetValues<ByteOrder>()
        : new[] { CandidateOrder };

    public IReadOnlyList<double> CandidateScales
    {
        get
        {
            var scales = new List<double>();
            foreach (var part in ScalesText.Split(new[] { ',', '，', ' ', ';', '；' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
                {
                    scales.Add(value);
                }
            }
            return scales.Count > 0 ? scales : new List<double> { 1.0 };
        }
    }

    partial void OnStartAddressChanged(int value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnEndAddressChanged(int value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnSampleCountChanged(int value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnModeChanged(LocatorMode value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnScalesTextChanged(string value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnAllByteOrdersChanged(bool value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnCandidateOrderChanged(ByteOrder value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnOnlyHighScoreChanged(bool value) => CandidatesView.Refresh();
    partial void OnMinScoreChanged(int value) => CandidatesView.Refresh();
    partial void OnTypeUInt16Changed(bool value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnTypeFloat64Changed(bool value) => OnPropertyChanged(nameof(RangeHint));

    private RegisterLocatorOptions BuildOptions() => new()
    {
        UnitId = UnitId,
        Area = Area,
        StartAddress = Math.Clamp(StartAddress, 0, 65535),
        EndAddress = Math.Clamp(EndAddress, 0, 65535),
        Mode = Mode,
        CandidateTypes = CandidateTypes,
        CandidateOrders = CandidateOrders,
        CandidateScales = CandidateScales,
        Target = Target,
        TargetMin = TargetMin,
        TargetMax = TargetMax,
        Tolerance = Tolerance,
        TolerancePercent = TolerancePercent,
        TextPattern = string.IsNullOrWhiteSpace(TextPattern) ? null : TextPattern.Trim(),
        SampleCount = Math.Clamp(SampleCount, 2, 10),
        SampleIntervalMs = Math.Clamp(SampleIntervalMs, 0, 60_000),
        MaxResults = Math.Max(1, MaxResults),
        ExcludeNonFinite = ExcludeNonFinite,
        ResponseTimeoutMs = Math.Max(50, ResponseTimeoutMs),
        RetryCount = Math.Max(0, RetryCount),
        IntervalMs = Math.Max(0, IntervalMs)
    };

    [RelayCommand]
    private async Task StartAsync()
    {
        var locator = _app.Sessions.CreateRegisterLocator();
        if (locator is null)
        {
            _app.Dialogs.Warn("尚未连接", "请先在“连接中心”打开串口或连接 TCP。");
            return;
        }

        var options = BuildOptions();
        if (!RegisterLocatorOptions.Validate(options, out var error))
        {
            _app.Dialogs.ShowError(error!, "定位参数不正确");
            return;
        }

        if (options.CandidateTypes.Count == 0)
        {
            _app.Dialogs.Warn("参数不完整", "至少需要勾选一种数据类型。");
            return;
        }

        Candidates.Clear();
        _cts = new CancellationTokenSource();
        _gate = new PauseGate();
        IsRunning = true;
        IsPaused = false;
        ProgressPercent = 0;
        ProgressText = "开始采样…";
        StatusMessage = $"正在采样：{options.Area.ToChinese()} {options.StartAddress} ~ {options.EndAddress}" +
                        $"，模式 {ModeOptions.First(o => o.Value == options.Mode).Display}";

        _app.Log.Info("Locate",
            $"开始寄存器定位：从站 {options.UnitId}，{options.Area.ToChinese()} {options.StartAddress}~{options.EndAddress}，" +
            $"模式 {options.Mode}，目标 {options.Target}，采样 {options.EffectiveSampleCount()} 轮");

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressText = p.ToText();
            ProgressPercent = p.Percent;
            _app.Tasks.Report(p);
        });

        var candidates = new Progress<RegisterCandidate>(candidate => Candidates.Add(candidate));

        try
        {
            var results = await locator.LocateAsync(options, _gate, progress, candidates, _cts.Token).ConfigureAwait(true);
            StatusMessage = results.Count == 0
                ? "未找到候选点位：可放宽容差、扩大地址范围或改用其他模式。"
                : $"完成：共 {results.Count} 个候选。候选项仅是排序结果，请按“原始寄存器 + 解释规则”人工复核后再确认。";
            _app.Log.Info("Locate", StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "定位已停止，已获得的候选结果保留。";
            _app.Log.Info("Locate", "寄存器定位被用户停止");
        }
        catch (Comm.Core.Errors.CommException ex)
        {
            StatusMessage = ex.Error.ToUserText();
            _app.Dialogs.ShowError(ex.Error, "定位参数不正确");
        }
        catch (Exception ex)
        {
            StatusMessage = $"定位异常终止：{ex.Message}";
            _app.Log.Error("Locate", "寄存器定位异常终止", ex);
            _app.Dialogs.Error("定位失败", ex.Message);
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
        Candidates.Clear();
        StatusMessage = string.Empty;
        ProgressText = "未开始";
        ProgressPercent = 0;
    }

    [RelayCommand]
    private void Export()
    {
        if (Candidates.Count == 0)
        {
            _app.Dialogs.Warn("无数据", "还没有候选结果可以导出。");
            return;
        }

        var path = _app.Dialogs.SaveFile("导出寄存器定位候选", "CSV 文件 (*.csv)|*.csv",
            $"寄存器定位-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (string.IsNullOrEmpty(path)) return;

        var error = RegisterLocator.ExportCsv(path, Candidates.ToList(),
            $"{Area.ToChinese()} {StartAddress}~{EndAddress}，模式 {Mode}，目标 {Target}");
        if (string.IsNullOrEmpty(error)) _app.Dialogs.Info("导出完成", $"已导出 {Candidates.Count} 个候选到：\n{path}");
        else _app.Dialogs.Error("导出失败", error);
    }

    /// <summary>把候选项加入监视表（M2-4 联动）。</summary>
    [RelayCommand]
    private void AddToWatch()
    {
        if (SelectedCandidate is null) return;

        var point = new RegisterPoint
        {
            Name = $"{SelectedCandidate.Type.ToChinese()} {SelectedCandidate.Address}",
            UnitId = UnitId,
            Area = Area,
            Address = SelectedCandidate.Address,
            Type = SelectedCandidate.Type,
            Order = SelectedCandidate.Order,
            Scale = SelectedCandidate.Scale,
            Note = $"来自寄存器定位（{SelectedCandidate.Reason}）"
        };

        _app.WatchList.Add(point);
        StatusMessage = _app.WatchList.StatusMessage;
    }

    [RelayCommand]
    private void ShowDetail()
    {
        if (SelectedCandidate is null) return;

        var candidate = SelectedCandidate;
        var text = $"协议地址：{candidate.Address}（显示地址 {AddressMap.Format(candidate.Address, AddressStyle.DocumentOneBased, Area)} / " +
                   $"{AddressMap.Format(candidate.Address, AddressStyle.Modicon, Area)}）\n" +
                   $"数据区：{Area.ToChinese()}\n" +
                   $"解释规则：{candidate.InterpretationText}\n" +
                   $"原始寄存器：{candidate.RawHex}\n" +
                   $"解析值：{candidate.ValueText}\n" +
                   $"匹配值：{candidate.MatchedValue?.ToString("G9", CultureInfo.InvariantCulture) ?? "-"}\n" +
                   $"匹配分数：{candidate.MatchScore}\n" +
                   $"说明：{candidate.Reason}\n" +
                   (candidate.BeforeValues is null ? string.Empty : $"变化前：{string.Join(", ", candidate.BeforeValues)}\n") +
                   (candidate.AfterValues is null ? string.Empty : $"变化后：{string.Join(", ", candidate.AfterValues)}\n") +
                   "\n提示：候选项只是“可能的解释”，请结合设备手册核对后再写入点表。";
        _app.Dialogs.ShowText("候选详情", text);
    }

    /// <summary>按选中的候选生成读取报文，交给“报文工具”页（M2-4 联动的另一种形式）。</summary>
    [RelayCommand]
    private void BuildReadFrame()
    {
        if (SelectedCandidate is null) return;

        var service = _app.Sessions.CreateManualSendService(FrameTransport.FollowSession, _app.Sessions.SilenceThresholdMs);
        if (!service.CanSend && _app.Sessions.Current is null)
        {
            StatusMessage = "尚未连接：仍然生成了报文（可在“报文工具”页查看），连接后才能发送。";
        }

        var pdu = ModbusRequests.Read(Area, (ushort)SelectedCandidate.Address, (ushort)SelectedCandidate.Type.RegisterCount());
        var frame = service.BuildFrame(UnitId, Area.ReadFunctionCode(), pdu.Data);
        var hex = HexUtil.ToHex(frame);
        _app.RequestHex(hex, $"已生成读取报文：{hex}（在“报文工具”页可发送）");
        StatusMessage = "已把读取报文发送到“报文工具”页。";
    }
}
