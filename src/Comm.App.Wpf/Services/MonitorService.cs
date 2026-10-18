using System.Collections.ObjectModel;
using System.Windows;
using Comm.Capture;
using Comm.Core.Frames;
using Comm.Diagnostics;
using Comm.Settings;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Comm.App.Wpf.Services;

public enum DirectionFilter
{
    All,
    TxOnly,
    RxOnly,
    ErrorsOnly,
    SystemOnly
}

/// <summary>
/// 报文监视服务：显示缓冲、暂停显示、解析开关、过滤、保存（见规划 4.6）。
/// 界面侧只保留有限条数，落盘不受显示丢弃影响。
/// </summary>
public partial class MonitorService : ObservableObject
{
    private readonly FrameBus _bus;
    private readonly LogGate _log;
    private long _uiIndex = -1;

    public MonitorService(FrameBus bus, LogGate log, AppSettings settings)
    {
        _bus = bus;
        _log = log;
        ParseEnabled = bus.ParseEnabled;
        UiCapacity = Math.Max(1000, settings.Monitor.UiCapacity);
        ColorizeDirections = settings.Monitor.ColorizeDirections;
        AutoScroll = settings.Monitor.AutoScroll;
        bus.FramesAppended += OnFramesAppended;
    }

    /// <summary>界面显示的行数上限（M1 欠账 D-3：原为硬编码常量，现可在设置页调整）。</summary>
    public int UiCapacity { get; set; }

    /// <summary>TX/RX 方向着色（M1 欠账 D-2）。颜色之外始终有 TX/RX 文字，不单靠颜色区分。</summary>
    [ObservableProperty]
    private bool _colorizeDirections = true;

    /// <summary>界面显示的行（受 UiCapacity 限制）。</summary>
    public ObservableCollection<FrameRecord> Rows { get; } = new();

    public event EventHandler? RowsChanged;

    public long TotalCount => _bus.TotalCount;
    public long DroppedFromDisplay => _bus.DroppedFromDisplay;

    [ObservableProperty]
    private bool _displayPaused;

    [ObservableProperty]
    private bool _parseEnabled = true;

    [ObservableProperty]
    private bool _autoScroll = true;

    [ObservableProperty]
    private DirectionFilter _directionFilter = DirectionFilter.All;

    [ObservableProperty]
    private string _unitFilter = string.Empty;

    [ObservableProperty]
    private string _functionFilter = string.Empty;

    [ObservableProperty]
    private string _textFilter = string.Empty;

    [ObservableProperty]
    private FrameRecord? _selectedRecord;

    public string FilterDescription =>
        $"方向={DirectionFilter}，从站={UnitFilter}，功能码={FunctionFilter}，文本={TextFilter}";

    public bool IsRecording => _bus.Writer?.IsRecording ?? false;
    public string? RecordingFile => _bus.Writer?.Path;

    partial void OnDisplayPausedChanged(bool value)
    {
        _bus.DisplayPaused = value;
        _log.Debug("Monitor", value ? "暂停显示（采集与记录继续）" : "恢复显示（补齐暂停期间的报文）");
        // 恢复时不重置索引：下一个刷新节拍会把暂停期间积累的报文一次性补齐（见规划 4.6.3）。
    }

    partial void OnParseEnabledChanged(bool value)
    {
        _bus.ParseEnabled = value;
        _log.Debug("Monitor", value ? "启用报文解析" : "关闭报文解析（仍保留原始报文）");
        ReapplyFilters();
    }

    public string StartRecording(string directory)
    {
        if (_bus.Writer is null) return "记录器不可用";
        var path = System.IO.Path.Combine(directory, $"frames-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        var error = _bus.Writer.Start(path);
        if (error is null)
        {
            _log.Info("Monitor", $"开始记录报文：{path}");
            OnPropertyChanged(nameof(IsRecording));
            OnPropertyChanged(nameof(RecordingFile));
        }
        else
        {
            _log.Error("Monitor", error);
        }
        return error ?? string.Empty;
    }

    public void StopRecording()
    {
        _bus.Writer?.Stop();
        _log.Info("Monitor", "停止记录报文");
        OnPropertyChanged(nameof(IsRecording));
    }

    public void Clear()
    {
        _bus.Clear();
        Rows.Clear();
        _uiIndex = _bus.LastIndex;
        OnPropertyChanged(nameof(TotalCount));
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>过滤条件变化后，从显示缓冲的可用起点重新提取（见规划 4.6.5）。</summary>
    public void ReapplyFilters()
    {
        Rows.Clear();
        _uiIndex = _bus.FirstIndex - 1;
        AppendNew();
    }

    private void OnFramesAppended(object? sender, FramesAppendedEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(() => AppendNew());
    }

    private void AppendNew()
    {
        if (DisplayPaused)
        {
            // 暂停期间不向界面投递，但也不推进索引：恢复后一次性补齐（见规划 4.6.3）。
            return;
        }

        long from = _uiIndex;
        var records = _bus.GetSince(from, 20000);
        if (records.Count == 0)
        {
            _uiIndex = _bus.LastIndex;
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(DroppedFromDisplay));
            return;
        }

        _uiIndex = from + records.Count;

        foreach (var record in records)
        {
            if (!Matches(record)) continue;
            Rows.Add(record);
        }

        while (Rows.Count > UiCapacity) Rows.RemoveAt(0);

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(DroppedFromDisplay));
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Matches(FrameRecord record)
    {
        switch (DirectionFilter)
        {
            case DirectionFilter.TxOnly when record.Direction != FrameDirection.Tx: return false;
            case DirectionFilter.RxOnly when record.Direction != FrameDirection.Rx: return false;
            case DirectionFilter.ErrorsOnly when !record.IsError && record.Markers == FrameMarker.None: return false;
            case DirectionFilter.SystemOnly when record.Direction != FrameDirection.System: return false;
        }

        if (!string.IsNullOrWhiteSpace(UnitFilter) && record.UnitIdText != UnitFilter.Trim()) return false;

        if (!string.IsNullOrWhiteSpace(FunctionFilter))
        {
            var text = FunctionFilter.Trim().Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase);
            var expected = record.FunctionCode?.ToString("X2") ?? string.Empty;
            if (!expected.Equals(text, StringComparison.OrdinalIgnoreCase)) return false;
        }

        if (!string.IsNullOrWhiteSpace(TextFilter))
        {
            var needle = TextFilter.Trim();
            if (record.Hex.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
            if (record.Summary?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true) return true;
            return false;
        }

        return true;
    }

    /// <summary>当前显示内容的导出（人可读文本 / CSV / JSONL 由记录器产生）。</summary>
    public string Export(string path, bool csv)
    {
        var records = Rows.ToList();
        var result = csv
            ? FrameExporter.ExportCsv(path, records)
            : FrameExporter.ExportText(path, records, FilterDescription);

        if (string.IsNullOrEmpty(result)) _log.Info("Monitor", $"导出 {records.Count} 条报文到 {path}");
        return result;
    }

    public IReadOnlyList<FrameRecord> Snapshot() => Rows.ToList();
}
