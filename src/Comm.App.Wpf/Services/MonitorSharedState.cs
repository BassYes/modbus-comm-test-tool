using Comm.Capture;
using Comm.Diagnostics;
using Comm.Settings;

namespace Comm.App.Wpf.Services;

/// <summary>
/// 报文监视中<b>本来就是全局</b>的两项状态（审计 F-04 的产品决策）：
/// <list type="bullet">
/// <item><b>解析开关</b>：解析在采集端 <see cref="FrameBus.OnFrame"/> 里完成，
///       一旦关闭，记录里的 <see cref="Comm.Core.Frames.FrameRecord.Summary"/> 就不再生成；
///       按会话拆分需要把解析搬到显示投影并为每条报文重放，代价与收益不匹配。</item>
/// <item><b>JSONL 记录</b>：<see cref="FrameBus.Writer"/> 只有一个，天然是全局的。</item>
/// </list>
/// 因此把它们收敛到本类，由 <see cref="SessionManager"/> 持有并注入每个会话的
/// <see cref="MonitorService"/>：任一会话改动都会同步到其它会话界面，UI 上标注"全局"。
/// <para>
/// 相对地，"暂停显示"和"清空显示"是纯显示投影操作，已改为<b>会话级</b>
/// （各 <see cref="MonitorService"/> 自己维护索引与行集合，不再写入总线）。
/// </para>
/// </summary>
public sealed class MonitorSharedState
{
    private readonly FrameBus _bus;
    private readonly LogGate _log;

    public MonitorSharedState(FrameBus bus, LogGate log, MonitorSettings settings)
    {
        _bus = bus;
        _log = log;

        // 设置页里的"默认关闭解析"此前没有接线（MonitorSettings.ParseEnabled 从未被读取），
        // 现在由本类统一初始化，避免两处状态各说各话。
        _bus.ParseEnabled = settings.ParseEnabled;
    }

    /// <summary>状态变化（解析开关 / 开始停止记录），各会话的 MonitorService 据此同步。</summary>
    public event EventHandler? Changed;

    /// <summary>解析开关（全局）：关闭后不再生成解析摘要，原始报文照常采集与落盘。</summary>
    public bool ParseEnabled
    {
        get => _bus.ParseEnabled;
        set
        {
            if (_bus.ParseEnabled == value) return;
            _bus.ParseEnabled = value;
            _log.Debug("Monitor", value ? "全局启用报文解析" : "全局关闭报文解析（仍保留原始报文）");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsRecording => _bus.Writer?.IsRecording ?? false;

    public string? RecordingFile => _bus.Writer?.Path;

    /// <summary>开始记录；返回空字符串表示成功（沿用既有约定）。</summary>
    public string StartRecording(string directory)
    {
        if (_bus.Writer is null) return "记录器不可用";

        var path = System.IO.Path.Combine(directory, $"frames-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        var error = _bus.Writer.Start(path);
        if (error is null)
        {
            _log.Info("Monitor", $"开始记录报文（全局）：{path}");
            Changed?.Invoke(this, EventArgs.Empty);
            return string.Empty;
        }

        _log.Error("Monitor", error);
        return error;
    }

    /// <summary>停止记录（全局）。</summary>
    public void StopRecording()
    {
        _bus.Writer?.Stop();
        _log.Info("Monitor", "停止记录报文（全局）");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>刷新界面上的记录状态（启动后文件可能由其它会话改变）。</summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
