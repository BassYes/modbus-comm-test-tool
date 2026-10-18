using Comm.Diagnostics;
using Comm.Scanning;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Comm.App.Wpf.Services;

/// <summary>任务中心的最小实现：全局状态栏显示当前任务与进度（见规划 4.11）。</summary>
public partial class TaskStatusService : ObservableObject
{
    private readonly LogGate _log;

    public TaskStatusService(LogGate log)
    {
        _log = log;
    }

    [ObservableProperty]
    private string _taskName = "空闲";

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isBusy;

    public void Report(ScanProgress progress)
    {
        TaskName = progress.TaskName;
        Detail = progress.CurrentStep;
        Percent = progress.Percent;
        IsBusy = progress.Status is ScanStatus.Running or ScanStatus.Paused;

        if (progress.Status is ScanStatus.Completed or ScanStatus.Cancelled or ScanStatus.Faulted)
        {
            _log.Info("Task", $"{progress.TaskName} 结束：{progress.Status}（{progress.Completed}/{progress.Total}）");
        }
    }

    public void Idle(string reason = "")
    {
        IsBusy = false;
        TaskName = "空闲";
        Detail = reason;
        Percent = 0;
    }
}
