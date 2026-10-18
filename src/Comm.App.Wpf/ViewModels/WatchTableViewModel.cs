using Comm.App.Wpf.Services;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>
/// 监视表页面（M2-4，规划 4.3.5、4.4.5）。
/// 点位的实际存储与周期刷新在应用级单例 <see cref="WatchListService"/> 中，因此数据扫描与寄存器定位页也能“加入监视表”。
/// </summary>
public partial class WatchTableViewModel : ObservableObject
{
    private readonly AppServices _app;

    public WatchTableViewModel(AppServices app)
    {
        _app = app;
        var scan = app.Settings.Scan;
        _intervalMs = scan.WatchTableIntervalMs;
        _area = scan.DefaultArea;
        _dataType = scan.DefaultDataType;
        _byteOrder = scan.DefaultByteOrder;
        _unitId = app.Sessions.DefaultUnitId;

        Watch.PointsChanged += (_, _) => RefreshView();
        Watch.SamplesChanged += (_, _) => RefreshView();
        Watch.PropertyChanged += (_, _) => RefreshView();
        RefreshView();
    }

    public WatchListService Watch => _app.WatchList;

    /// <summary>监视表最小刷新周期（不低于 200 ms，避免打爆现场总线）。</summary>
    public TimeSpan MinimumInterval => TimeSpan.FromMilliseconds(200);

    public IReadOnlyList<Option<DataArea>> AreaOptions { get; } = Enum.GetValues<DataArea>()
        .Select(a => new Option<DataArea>(a, a.ToChinese())).ToArray();

    public IReadOnlyList<Option<DataType>> TypeOptions { get; } = Enum.GetValues<DataType>()
        .Select(t => new Option<DataType>(t, t.ToChinese())).ToArray();

    public IReadOnlyList<Option<ByteOrder>> OrderOptions { get; } = Enum.GetValues<ByteOrder>()
        .Select(o => new Option<ByteOrder>(o, o.ToDisplay())).ToArray();

    // ---- 新增点位
    [ObservableProperty] private string _newPointName = "新点位";
    [ObservableProperty] private byte _unitId = 1;
    [ObservableProperty] private DataArea _area = DataArea.HoldingRegisters;
    [ObservableProperty] private int _address;
    [ObservableProperty] private DataType _dataType = DataType.UInt16;
    [ObservableProperty] private ByteOrder _byteOrder = ByteOrder.Abcd;
    [ObservableProperty] private double _scale = 1.0;
    [ObservableProperty] private double _offset;
    [ObservableProperty] private string _unit = string.Empty;
    [ObservableProperty] private string _note = string.Empty;

    // ---- 周期刷新
    [ObservableProperty] private int _intervalMs = 1000;

    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private string _pointsSummary = string.Empty;
    [ObservableProperty] private WatchPointSample? _selectedSample;
    [ObservableProperty] private RegisterPoint? _selectedPoint;

    public string IntervalHint =>
        $"最小 {MinimumInterval.TotalMilliseconds:F0} ms；周期请求优先级低于手动发送、高于扫描，" +
        "扫描进行中允许穿插，但会影响扫描耗时统计（规划 13.3）。";

    private void RefreshView()
    {
        PointsSummary = Watch.PointsSummary;
        StatusMessage = Watch.StatusMessage;
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(IntervalHint));
    }

    public bool IsRunning => Watch.IsRunning;
    public bool IsPaused => Watch.IsPaused;

    [RelayCommand]
    private void AddPoint()
    {
        if (Address is < 0 or > 65535)
        {
            _app.Dialogs.Warn("参数不正确", "地址必须在 0 ~ 65535 之间。");
            return;
        }

        Watch.Add(new RegisterPoint
        {
            Name = string.IsNullOrWhiteSpace(NewPointName) ? $"{DataType.ToChinese()} {Address}" : NewPointName.Trim(),
            UnitId = UnitId,
            Area = Area,
            Address = Address,
            Type = DataType,
            Order = ByteOrder,
            Scale = Scale,
            Offset = Offset,
            Unit = string.IsNullOrWhiteSpace(Unit) ? null : Unit.Trim(),
            Note = string.IsNullOrWhiteSpace(Note) ? null : Note.Trim()
        });

        _app.Update(s => s with
        {
            Scan = s.Scan with
            {
                DefaultArea = Area,
                DefaultDataType = DataType,
                DefaultByteOrder = ByteOrder,
                WatchTableIntervalMs = Math.Max(200, IntervalMs)
            }
        });

        Address++; // 连续录入时多半是相邻地址
        RefreshView();
    }

    [RelayCommand]
    private void RemovePoint()
    {
        if (SelectedPoint is null) return;
        Watch.Remove(SelectedPoint.Id);
        RefreshView();
    }

    [RelayCommand]
    private void ClearPoints()
    {
        if (!_app.Dialogs.Confirm("确认清空", "将移除监视表中的全部点位与采样记录。是否继续？")) return;
        Watch.ClearPoints();
        RefreshView();
    }

    [RelayCommand]
    private async Task RefreshOnceAsync()
    {
        await Watch.RefreshOnceAsync().ConfigureAwait(true);
        RefreshView();
    }

    [RelayCommand]
    private void Start()
    {
        Watch.Start(TimeSpan.FromMilliseconds(Math.Max(200, IntervalMs)));
        RefreshView();
    }

    [RelayCommand]
    private void Stop()
    {
        Watch.Stop();
        RefreshView();
    }

    [RelayCommand]
    private void PauseResume()
    {
        Watch.TogglePause();
        RefreshView();
    }

    [RelayCommand]
    private void ClearSamples()
    {
        Watch.Samples.Clear();
        RefreshView();
    }

    [RelayCommand]
    private void Export()
    {
        if (Watch.Samples.Count == 0)
        {
            _app.Dialogs.Warn("无数据", "还没有采样记录可以导出。");
            return;
        }

        var path = _app.Dialogs.SaveFile("导出监视表采样结果", "CSV 文件 (*.csv)|*.csv",
            $"监视表-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        if (string.IsNullOrEmpty(path)) return;

        var error = WatchTableService.ExportCsv(path, Watch.Samples.ToList(),
            $"{Watch.Points.Count} 个点位，周期 {IntervalMs} ms");
        if (string.IsNullOrEmpty(error)) _app.Dialogs.Info("导出完成", $"已导出 {Watch.Samples.Count} 条采样到：\n{path}");
        else _app.Dialogs.Error("导出失败", error);
    }

    [RelayCommand]
    private void ShowPointDetail()
    {
        if (SelectedPoint is null) return;
        var point = SelectedPoint;
        var latest = Watch.LatestFor(point.Id);

        var text = $"点位名称：{point.Name}\n" +
                   $"数据区：{point.Area.ToChinese()}\n" +
                   $"协议地址：{point.Address}（显示地址 {AddressMap.Format(point.Address, AddressStyle.DocumentOneBased, point.Area)} / " +
                   $"{AddressMap.Format(point.Address, AddressStyle.Modicon, point.Area)}）\n" +
                   $"数据类型：{point.Type.ToChinese()}    字节序：{point.Order.ToDisplay()}    缩放：{point.Scale}    偏移：{point.Offset}\n" +
                   $"单位：{point.Unit ?? "-"}\n" +
                   $"备注：{point.Note ?? "-"}\n" +
                   (latest is null
                       ? "\n尚未采样。"
                       : $"\n最近采样：{latest.ValueText ?? "-"} {latest.Unit}\n" +
                         $"原始寄存器：{latest.RawHex}\n" +
                         $"状态：{latest.StatusText}    变化：{latest.ChangedText}\n" +
                         $"上一次值：{latest.PreviousValue ?? "-"}\n" +
                         $"耗时：{latest.ElapsedMs:F1} ms    时间：{latest.Timestamp:yyyy-MM-dd HH:mm:ss.fff}");

        _app.Dialogs.ShowText("点位详情", text);
    }
}
