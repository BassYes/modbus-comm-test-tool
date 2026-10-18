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
/// <para>
/// 作用域（审计 F-04 修正）：
/// <list type="bullet">
/// <item><b>会话级</b>——暂停显示、清空显示、过滤、行集合：每实例独立，不再改写共享总线，
///       因此 A 窗口暂停/清空不会影响 B 窗口。</item>
/// <item><b>全局</b>——解析开关与 JSONL 记录：由 <see cref="MonitorSharedState"/> 统一持有，
///       任一会话操作都会同步到所有会话，UI 上标注"全局"。</item>
/// </list>
/// </para>
/// </summary>
public partial class MonitorService : ObservableObject, IDisposable
{
    private readonly FrameBus _bus;
    private readonly LogGate _log;
    private readonly MonitorSharedState _shared;
    private long _uiIndex = -1;
    private bool _syncingShared;
    private bool _disposed;

    public MonitorService(FrameBus bus, LogGate log, AppSettings settings, string? sessionIdFilter = null,
        MonitorSharedState? shared = null)
    {
        _bus = bus;
        _log = log;
        SessionIdFilter = sessionIdFilter;

        // 未注入时自建（探针/单元测试场景）：行为与单会话一致，只是不跨实例同步。
        _shared = shared ?? new MonitorSharedState(bus, log, settings.Monitor);
        _shared.Changed += OnSharedStateChanged;

        _syncingShared = true;
        try
        {
            _parseEnabled = _shared.ParseEnabled;
        }
        finally
        {
            _syncingShared = false;
        }

        UiCapacity = Math.Max(1000, settings.Monitor.UiCapacity);
        ColorizeDirections = settings.Monitor.ColorizeDirections;
        AutoScroll = settings.Monitor.AutoScroll;
        bus.FramesAppended += OnFramesAppended;
    }

    /// <summary>
    /// 会话过滤（M3b）：只投影本会话的报文（<see cref="FrameRecord.SessionId"/>），
    /// 两个窗口各自监视自己的会话，统计与结果不串会话（规划 21.2.2 第 3 点）。null = 不过滤。
    /// </summary>
    public string? SessionIdFilter { get; set; }

    /// <summary>按设置页的监视选项刷新本实例（设置页改动后由 SessionManager 统一下发）。</summary>
    public void ApplySettings(MonitorSettings settings)
    {
        UiCapacity = Math.Max(1000, settings.UiCapacity);
        ColorizeDirections = settings.ColorizeDirections;
        AutoScroll = settings.AutoScroll;
        while (Rows.Count > UiCapacity) Rows.RemoveAt(0);
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

    /// <summary>本会话（按 <see cref="SessionIdFilter"/> 过滤后）已投递到界面的条数。</summary>
    public long MatchedCount { get; private set; }

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

    public bool IsRecording => _shared.IsRecording;
    public string? RecordingFile => _shared.RecordingFile;

    /// <summary>暂停显示（会话级）：只影响本会话的界面投递，不写入共享总线（审计 F-04）。</summary>
    partial void OnDisplayPausedChanged(bool value)
    {
        _log.Debug("Monitor", value ? "暂停本会话显示（采集与记录继续）" : "恢复本会话显示（补齐暂停期间的报文）");
        // 恢复时立即补投一次；暂停期间 _uiIndex 未推进，因此能一次补齐（见规划 4.6.3）。
        if (!value) AppendNew();
    }

    /// <summary>解析开关（全局）：写入 <see cref="MonitorSharedState"/>，由它同步到其它会话。</summary>
    partial void OnParseEnabledChanged(bool value)
    {
        if (_syncingShared) return;

        _shared.ParseEnabled = value;
        ReapplyFilters();
    }

    /// <summary>全局状态（解析/记录）变化后同步本实例的界面状态。</summary>
    private void OnSharedStateChanged(object? sender, EventArgs e)
    {
        _syncingShared = true;
        try
        {
            ParseEnabled = _shared.ParseEnabled;
        }
        finally
        {
            _syncingShared = false;
        }

        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordingFile));
    }

    public string StartRecording(string directory)
    {
        var error = _shared.StartRecording(directory);
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordingFile));
        return error;
    }

    public void StopRecording()
    {
        _shared.StopRecording();
        OnPropertyChanged(nameof(IsRecording));
        OnPropertyChanged(nameof(RecordingFile));
    }

    /// <summary>
    /// 清空显示（<b>仅本会话</b>）：只丢掉本会话的行集合并把读取游标推到最新，
    /// 不再调用 <see cref="FrameBus.Clear"/>——那会删掉其它会话正在看的记录（审计 F-04）。
    /// </summary>
    public void Clear()
    {
        Rows.Clear();
        _uiIndex = _bus.LastIndex;
        MatchedCount = 0;
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(MatchedCount));
        OnPropertyChanged(nameof(DroppedFromDisplay));
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>解除订阅（会话关闭时调用，避免共享状态与总线持有已废弃实例）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _shared.Changed -= OnSharedStateChanged;
        _bus.FramesAppended -= OnFramesAppended;
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
            MatchedCount++;
            Rows.Add(record);
        }

        while (Rows.Count > UiCapacity) Rows.RemoveAt(0);

        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(MatchedCount));
        OnPropertyChanged(nameof(DroppedFromDisplay));
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Matches(FrameRecord record)
    {
        if (SessionIdFilter is { Length: > 0 } filter &&
            !string.Equals(record.SessionId, filter, StringComparison.Ordinal))
        {
            return false;
        }

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
