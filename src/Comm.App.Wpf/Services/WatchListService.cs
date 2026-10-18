using System.Collections.ObjectModel;
using Comm.Core.Tasks;
using Comm.Diagnostics;
using Comm.Protocols.Modbus;
using Comm.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Comm.App.Wpf.Services;

/// <summary>
/// 监视表（M2-4，规划 4.3.5、4.4.5）。
/// 作为应用级单例：数据扫描结果、寄存器定位候选、手工添加的点位都能“加入监视表”，
/// 周期刷新使用 <see cref="RequestPriority.Periodic"/>，因此可以与扫描任务并存（规划 13.3）。
/// </summary>
public sealed partial class WatchListService : ObservableObject
{
    private readonly SessionManager _sessions;
    private readonly LogGate _log;
    private readonly Dictionary<string, WatchPointSample> _latest = new();
    private CancellationTokenSource? _cts;
    private PauseGate? _gate;
    private Task? _loop;

    public WatchListService(SessionManager sessions, LogGate log)
    {
        _sessions = sessions;
        _log = log;
        _sessions.CurrentChanged += (_, _) =>
        {
            if (_sessions.IsConnected) return;
            if (IsRunning) Stop("连接已断开，监视表已停止");
        };
    }

    /// <summary>界面侧最近一次采样记录（新记录在前）。</summary>
    public ObservableCollection<WatchPointSample> Samples { get; } = new();

    /// <summary>点位列表（可观察集合：界面直接绑定，增删立即刷新）。</summary>
    public ObservableCollection<RegisterPoint> Points { get; } = new();

    public event EventHandler? PointsChanged;
    public event EventHandler? SamplesChanged;

    /// <summary>界面上限：避免长时间运行把内存吃满。</summary>
    public int UiCapacity { get; set; } = 5000;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private string _statusMessage = "监视表为空。可在数据扫描或寄存器定位结果中“加入监视表”。";

    [ObservableProperty]
    private int _changedCount;

    public string PointsSummary =>
        $"点位 {Points.Count} 个；本轮变化 {ChangedCount} 个；采样记录 {Samples.Count} 条";

    public WatchPointSample? LatestFor(string pointId)
    {
        lock (_latest) return _latest.TryGetValue(pointId, out var sample) ? sample : null;
    }

    public void Add(RegisterPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        if (Points.Any(p => p.Area == point.Area && p.Address == point.Address && p.UnitId == point.UnitId))
        {
            StatusMessage = $"点位已存在：{point.Area.ToShortChinese()} {point.Address}。";
            return;
        }

        Points.Add(point);

        _log.Info("Watch", $"加入监视点位：{point.Name} {point.Area.ToShortChinese()} {point.Address}");
        StatusMessage = $"已加入点位 {point.Name}（{point.Area.ToShortChinese()} {point.Address}）。";
        PointsChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(PointsSummary));
    }

    public bool Remove(string pointId)
    {
        var point = Points.FirstOrDefault(p => p.Id == pointId);
        if (point is null) return false;

        Points.Remove(point);
        lock (_latest) _latest.Remove(pointId);
        PointsChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(PointsSummary));
        return true;
    }

    public void ClearPoints()
    {
        Points.Clear();
        lock (_latest) _latest.Clear();
        Samples.Clear();
        ChangedCount = 0;
        StatusMessage = "监视表已清空。";
        PointsChanged?.Invoke(this, EventArgs.Empty);
        SamplesChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(PointsSummary));
    }

    /// <summary>立即读取一次（不改变周期刷新状态）。</summary>
    public async Task RefreshOnceAsync(CancellationToken cancellationToken = default)
    {
        var service = BuildService();
        if (service is null)
        {
            StatusMessage = "尚未连接设备，无法读取点位。";
            return;
        }

        try
        {
            var samples = await service.RefreshOnceAsync(cancellationToken).ConfigureAwait(true);
            ApplySamples(samples);
            StatusMessage = $"读取完成：{samples.Count} 个点位，{samples.Count(s => s.Changed)} 个发生变化" +
                            (service.LastCycleRequestCount > 1 ? $"；已合并为 {service.LastCycleRequestCount} 次请求" : string.Empty) + "。";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "读取已取消。";
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取失败：{ex.Message}";
            _log.Error("Watch", "监视表读取失败", ex);
        }
    }

    /// <summary>开始周期刷新（最小 200 ms，规划 13.3）。</summary>
    public void Start(TimeSpan interval)
    {
        if (IsRunning) return;

        var service = BuildService();
        if (service is null)
        {
            StatusMessage = "尚未连接设备，无法开始周期刷新。";
            return;
        }

        _cts = new CancellationTokenSource();
        _gate = new PauseGate();
        IsRunning = true;
        IsPaused = false;

        // 周期任务在后台线程回调，界面集合只能在 UI 线程改（见规划 4.6.6）。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        service.CycleCompleted += (_, samples) =>
        {
            if (dispatcher is null) return;
            dispatcher.BeginInvoke(() => ApplySamples(samples));
        };

        var token = _cts.Token;
        var progress = new Progress<ScanProgress>(p => StatusMessage = p.ToText());

        _log.Info("Watch", $"开始周期刷新监视表：{interval.TotalMilliseconds:F0} ms，{Points.Count} 个点位");

        _loop = Task.Run(async () =>
        {
            try
            {
                await service.RunAsync(interval, _gate, progress, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 正常停止
            }
            catch (Exception ex)
            {
                _log.Error("Watch", "监视表周期刷新异常终止", ex);
                StatusMessage = $"周期刷新异常终止：{ex.Message}";
            }
            finally
            {
                IsRunning = false;
                IsPaused = false;
            }
        }, token);
    }

    public void Stop(string reason = "监视表已停止")
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _gate = null;
        IsRunning = false;
        IsPaused = false;
        StatusMessage = reason;
        _log.Info("Watch", reason);
    }

    public void TogglePause()
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

    private WatchTableService? BuildService()
    {
        var service = _sessions.CreateWatchTableService();
        if (service is null) return null;

        foreach (var point in Points.ToArray()) service.Add(point);
        return service;
    }

    /// <summary>周期刷新时需要把新采样投递到界面；周期任务内部按 50 ms 批量约定在 VM 中处理。</summary>
    public void ApplySamples(IReadOnlyList<WatchPointSample> samples)
    {
        int changed = 0;
        foreach (var sample in samples)
        {
            if (sample.Changed) changed++;
            lock (_latest) _latest[sample.PointId] = sample;
            Samples.Insert(0, sample);
        }

        while (Samples.Count > UiCapacity) Samples.RemoveAt(Samples.Count - 1);

        ChangedCount = changed;
        SamplesChanged?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(PointsSummary));
    }
}
