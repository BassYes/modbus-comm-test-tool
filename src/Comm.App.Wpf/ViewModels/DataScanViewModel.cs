using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Comm.App.Wpf.Services;
using Comm.Core.Tasks;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using Comm.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>数据区扫描（见规划 4.3）。</summary>
public partial class DataScanViewModel : ObservableObject
{
    private const string ScanPlanKind = ScanPlanKinds.Data;

    private readonly AppServices _app;
    private CancellationTokenSource? _cts;
    private PauseGate? _gate;

    public DataScanViewModel(AppServices app)
    {
        _app = app;
        var scan = app.Settings.Scan;
        _area = scan.DefaultArea;
        _dataType = scan.DefaultDataType;
        _byteOrder = scan.DefaultByteOrder;
        _unitId = app.Sessions.DefaultUnitId;

        ItemsView = CollectionViewSource.GetDefaultView(Items);
        ItemsView.Filter = o =>
        {
            if (!OnlyIssues) return true;
            return ((DataScanItem)o).Status != ScanItemStatus.Ok;
        };

        RefreshPlans();
    }

    public ObservableCollection<DataScanItem> Items { get; } = new();
    public ICollectionView ItemsView { get; }

    /// <summary>已保存的数据扫描方案（规划 4.12）。</summary>
    public ObservableCollection<ScanPlan> Plans { get; } = new();

    public IReadOnlyList<Option<DataArea>> AreaOptions { get; } = new[]
    {
        new Option<DataArea>(DataArea.Coils, "线圈 (01)"),
        new Option<DataArea>(DataArea.DiscreteInputs, "离散输入 (02)"),
        new Option<DataArea>(DataArea.HoldingRegisters, "保持寄存器 (03)"),
        new Option<DataArea>(DataArea.InputRegisters, "输入寄存器 (04)")
    };

    public IReadOnlyList<Option<DataType>> TypeOptions { get; } = Enum.GetValues<DataType>()
        .Select(t => new Option<DataType>(t, t.ToChinese())).ToArray();

    public IReadOnlyList<Option<ByteOrder>> OrderOptions { get; } = Enum.GetValues<ByteOrder>()
        .Select(o => new Option<ByteOrder>(o, o.ToDisplay())).ToArray();

    public IReadOnlyList<Option<AddressStyle>> StyleOptions { get; } = Enum.GetValues<AddressStyle>()
        .Select(s => new Option<AddressStyle>(s, AddressMap.StyleName(s))).ToArray();

    [ObservableProperty] private byte _unitId = 1;
    [ObservableProperty] private DataArea _area = DataArea.HoldingRegisters;
    [ObservableProperty] private int _startAddress;
    [ObservableProperty] private int _endAddress = 99;
    [ObservableProperty] private int _batchSize;
    [ObservableProperty] private DataType _dataType = DataType.UInt16;
    [ObservableProperty] private ByteOrder _byteOrder = ByteOrder.Abcd;
    [ObservableProperty] private double _scale = 1.0;
    [ObservableProperty] private double _offset;
    [ObservableProperty] private AddressStyle _addressStyle = AddressStyle.ProtocolZeroBased;
    [ObservableProperty] private int _responseTimeoutMs = 1000;
    [ObservableProperty] private int _retryCount = 2;
    [ObservableProperty] private int _intervalMs = 20;
    [ObservableProperty] private bool _refineInvalidBlocks = true;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _onlyIssues;
    [ObservableProperty] private string _progressText = "未开始";
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private DataScanItem? _selectedItem;
    [ObservableProperty] private string _planName = string.Empty;
    [ObservableProperty] private ScanPlan? _selectedPlan;

    public string RangeHint
    {
        get
        {
            int protocolMax = Fn.MaxQuantity(Area.ReadFunctionCode());
            int total = Math.Max(0, EndAddress - StartAddress + 1);
            int batch = BatchSize > 0 ? Math.Min(BatchSize, protocolMax) : protocolMax;
            int requests = batch <= 0 ? 0 : (int)Math.Ceiling(total / (double)batch);
            return $"点数 {total}；协议单次上限 {protocolMax}，本次每次请求 {batch} 点，共约 {requests} 次请求" +
                   $"（预计 {requests * (ResponseTimeoutMs + IntervalMs) / 1000.0:F1} 秒）";
        }
    }

    partial void OnStartAddressChanged(int value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnEndAddressChanged(int value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnBatchSizeChanged(int value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnAreaChanged(DataArea value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnResponseTimeoutMsChanged(int value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnIntervalMsChanged(int value) => OnPropertyChanged(nameof(RangeHint));
    partial void OnOnlyIssuesChanged(bool value) => ItemsView.Refresh();

    [RelayCommand]
    private async Task StartAsync()
    {
        var service = _app.Sessions.CreateDataScanService();
        if (service is null)
        {
            _app.Dialogs.Warn("尚未连接", "请先在“连接中心”打开串口或连接 TCP。");
            return;
        }

        if (EndAddress < StartAddress)
        {
            _app.Dialogs.Warn("参数不正确", "结束地址必须大于或等于起始地址。");
            return;
        }

        if (StartAddress < 0 || EndAddress > 65535)
        {
            _app.Dialogs.Warn("参数不正确", "地址范围必须在 0 ~ 65535 之间。");
            return;
        }

        var options = new DataScanOptions
        {
            UnitId = UnitId,
            Area = Area,
            StartAddress = StartAddress,
            EndAddress = EndAddress,
            BatchSize = BatchSize,
            Type = DataType,
            Order = ByteOrder,
            Scale = Scale,
            Offset = Offset,
            ResponseTimeoutMs = Math.Max(50, ResponseTimeoutMs),
            RetryCount = Math.Max(0, RetryCount),
            IntervalMs = Math.Max(0, IntervalMs),
            RefineInvalidBlocks = RefineInvalidBlocks
        };

        _app.Update(s => s with
        {
            Scan = s.Scan with
            {
                DefaultArea = options.Area,
                DefaultDataType = options.Type,
                DefaultByteOrder = options.Order,
                DefaultAddressStyle = AddressStyle
            }
        });

        Items.Clear();
        _cts = new CancellationTokenSource();
        _gate = new PauseGate();
        IsRunning = true;
        IsPaused = false;
        ProgressPercent = 0;
        ProgressText = "开始扫描…";
        StatusMessage = $"正在扫描：{options.Area.ToChinese()} {options.StartAddress} ~ {options.EndAddress}";

        _app.Log.Info("DataScan",
            $"开始数据扫描：从站 {options.UnitId}，{options.Area.ToChinese()}，地址 {options.StartAddress}~{options.EndAddress}，每次 {options.EffectiveBatchSize()} 点，类型 {options.Type}，字节序 {options.Order}");

        var progress = new Progress<ScanProgress>(p =>
        {
            ProgressText = p.ToText();
            ProgressPercent = p.Percent;
            _app.Tasks.Report(p);
        });

        var batchProgress = new Progress<IReadOnlyList<DataScanItem>>(batch =>
        {
            foreach (var item in batch) Items.Add(item);
        });

        try
        {
            var results = await service.ScanAsync(options, _gate, progress, batchProgress, _cts.Token).ConfigureAwait(true);
            int ok = results.Count(r => r.Status == ScanItemStatus.Ok);
            int bad = results.Count - ok;
            StatusMessage = $"扫描完成：正常 {ok} 点，异常/无效 {bad} 点。";
            _app.Log.Info("DataScan", StatusMessage);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "扫描已停止，已获得的结果保留。";
            _app.Log.Info("DataScan", "扫描被用户停止");
        }
        catch (Exception ex)
        {
            StatusMessage = $"扫描异常终止：{ex.Message}";
            _app.Log.Error("DataScan", "扫描异常终止", ex);
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

        var path = _app.Dialogs.SaveFile("导出数据扫描结果", "CSV 文件 (*.csv)|*.csv",
            $"数据扫描-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (string.IsNullOrEmpty(path)) return;

        var error = DataScanService.ExportCsv(path, Items.ToList(),
            $"从站 {UnitId}，{Area.ToChinese()}，{StartAddress}~{EndAddress}，{DataType.ToChinese()}，{ByteOrder.ToDisplay()}");
        if (string.IsNullOrEmpty(error)) _app.Dialogs.Info("导出完成", $"已导出 {Items.Count} 条结果到：\n{path}");
        else _app.Dialogs.Error("导出失败", error);
    }

    /// <summary>显示地址列（按当前地址显示风格换算）。</summary>
    public string DisplayAddress(DataScanItem item) => AddressMap.Format(item.Address, AddressStyle, Area);


    [RelayCommand]
    private void RefreshDisplay()
    {
        foreach (var item in Items.ToList())
        {
            int index = Items.IndexOf(item);
            Items[index] = item;
        }
    }

    /// <summary>把选中点位加入监视表（M2-4 联动）。</summary>
    [RelayCommand]
    private void AddToWatch()
    {
        if (SelectedItem is null) return;
        if (SelectedItem.Status != ScanItemStatus.Ok)
        {
            _app.Dialogs.Warn("点位不可用", "只有读取正常的点位才能加入监视表。");
            return;
        }

        _app.WatchList.Add(new RegisterPoint
        {
            Name = $"{SelectedItem.Area.ToShortChinese()} {SelectedItem.Address}",
            UnitId = UnitId,
            Area = Area,
            Address = SelectedItem.Address,
            Type = DataType,
            Order = ByteOrder,
            Scale = Scale,
            Offset = Offset,
            Note = "来自数据扫描"
        });

        StatusMessage = _app.WatchList.StatusMessage;
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
        if (EndAddress < StartAddress)
        {
            _app.Dialogs.Warn("参数不正确", "结束地址必须大于或等于起始地址。");
            return;
        }

        var options = new DataScanOptions
        {
            UnitId = UnitId,
            Area = Area,
            StartAddress = StartAddress,
            EndAddress = EndAddress,
            BatchSize = BatchSize,
            Type = DataType,
            Order = ByteOrder,
            Scale = Scale,
            Offset = Offset,
            ResponseTimeoutMs = Math.Max(50, ResponseTimeoutMs),
            RetryCount = Math.Max(0, RetryCount),
            IntervalMs = Math.Max(0, IntervalMs),
            RefineInvalidBlocks = RefineInvalidBlocks
        };

        var name = string.IsNullOrWhiteSpace(PlanName)
            ? $"{Area.ToShortChinese()} {options.StartAddress}-{options.EndAddress}"
            : PlanName.Trim();

        var error = _app.SavePlan(ScanPlan.Create(name, ScanPlanKind, options));
        if (!string.IsNullOrEmpty(error))
        {
            _app.Dialogs.Error("保存失败", error);
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
        var options = plan.ReadPayload<DataScanOptions>();
        if (options is null)
        {
            _app.Dialogs.Warn("方案不可用", $"方案“{plan.DisplayText}”的内容无法读取（可能来自更新的版本），已忽略。");
            return;
        }

        UnitId = options.UnitId;
        Area = options.Area;
        StartAddress = options.StartAddress;
        EndAddress = options.EndAddress;
        BatchSize = options.BatchSize;
        DataType = options.Type;
        ByteOrder = options.Order;
        Scale = options.Scale;
        Offset = options.Offset;
        ResponseTimeoutMs = options.ResponseTimeoutMs;
        RetryCount = options.RetryCount;
        IntervalMs = options.IntervalMs;
        RefineInvalidBlocks = options.RefineInvalidBlocks;
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

    [RelayCommand]
    private void DecodeSelected()
    {
        if (SelectedItem is null) return;
        var text = $"数据区：{SelectedItem.Area.ToChinese()}\n" +
                   $"协议地址：{SelectedItem.Address}（显示地址 {DisplayAddress(SelectedItem)}）\n" +
                   $"原始值：{SelectedItem.RawHex}\n" +
                   $"按 {DataType.ToChinese()} / {ByteOrder.ToDisplay()} 解析：{SelectedItem.ValueText}\n" +
                   $"状态：{SelectedItem.StatusText}\n" +
                   $"耗时：{SelectedItem.ElapsedMs:F1} ms\n" +
                   $"说明：{SelectedItem.Note}";
        _app.Dialogs.ShowText("点位详情", text);
    }
}
