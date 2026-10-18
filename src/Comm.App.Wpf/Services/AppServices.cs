using Comm.Capture;
using Comm.Diagnostics;
using Comm.Scanning;
using Comm.Settings;

namespace Comm.App.Wpf.Services;

/// <summary>应用级共享状态：各功能页统一从这里取服务与设置。</summary>
public sealed class AppServices
{
    public AppServices(
        SettingsStore store,
        AppSettings settings,
        LogGate log,
        CrashReporter crashes,
        FrameBus frameBus,
        SessionManager sessions,
        MonitorService monitor,
        WatchListService watchList,
        DialogService dialogs,
        TaskStatusService tasks)
    {
        Store = store;
        Settings = settings;
        Log = log;
        Crashes = crashes;
        FrameBus = frameBus;
        Sessions = sessions;
        Monitor = monitor;
        WatchList = watchList;
        Dialogs = dialogs;
        Tasks = tasks;
    }

    public SettingsStore Store { get; }
    public AppSettings Settings { get; private set; }
    public LogGate Log { get; }
    public CrashReporter Crashes { get; }
    public FrameBus FrameBus { get; }
    public SessionManager Sessions { get; }
    public MonitorService Monitor { get; }

    /// <summary>监视表（跨页面共享：数据扫描 / 寄存器定位都能“加入监视表”）。</summary>
    public WatchListService WatchList { get; }

    public DialogService Dialogs { get; }
    public TaskStatusService Tasks { get; }

    /// <summary>实际使用的数据目录（R-21 X-2：便携优先，不可写时回退 %LOCALAPPDATA%）。</summary>
    public string DataDirectory => Comm.Diagnostics.AppPaths.DataDirectory;

    public string DataDirectoryText => Comm.Diagnostics.AppPaths.ToDisplayText();

    public string CaptureDirectory => Settings.LastCaptureDirectory ?? System.IO.Path.Combine(DataDirectory, "captures");

    public string ExportDirectory => Settings.LastExportDirectory ?? Comm.Diagnostics.AppPaths.ExportsDirectory;

    public string SaveSettings(AppSettings settings)
    {
        Settings = settings;
        return Store.Save(settings) ?? string.Empty;
    }

    /// <summary>修改设置并立即保存；返回空字符串表示成功，否则为错误信息。</summary>
    public string Update(Func<AppSettings, AppSettings> mutate) => SaveSettings(mutate(Settings));

    /// <summary>跨页面传递“请载入这段报文”（寄存器定位 → 报文工具）。</summary>
    public event EventHandler<HexPayload>? HexRequested;

    /// <summary>R-11：把参数扫描结果里的串口参数回填到连接中心（并可选择立即重连）。</summary>
    public event EventHandler<ApplySerialParametersRequest>? SerialParametersRequested;

    /// <summary>跨页面通知（显示在全局状态栏的最后一条消息）。</summary>
    public event EventHandler<string>? Notified;

    public void RequestHex(string hex, string message) => HexRequested?.Invoke(this, new HexPayload(hex, message));

    public void RequestSerialParameters(SerialCombination combination, bool reconnect) =>
        SerialParametersRequested?.Invoke(this, new ApplySerialParametersRequest(combination, reconnect));

    public void Notify(string message) => Notified?.Invoke(this, message);

    /// <summary>日志开关切换（见规划 4.10.1）。</summary>
    public void SetLogEnabled(bool enabled)
    {
        Update(s => s with { Log = s.Log with { Enabled = enabled } });
        Log.SetEnabled(enabled);
    }

    // ---------------------------------------------------------------- M2-3 持久化：设备目标 / 扫描方案 / 报文模板

    /// <summary>新增或更新一个扫描方案（同名同类型视为覆盖）。</summary>
    public string SavePlan(ScanPlan plan)
    {
        var plans = Settings.Plans.Where(p => p.Id != plan.Id).ToList();
        plans.Add(plan);
        return Update(s => s with { Plans = plans });
    }

    public string RemovePlan(string planId)
    {
        var plans = Settings.Plans.Where(p => p.Id != planId).ToList();
        return Update(s => s with { Plans = plans });
    }

    public IReadOnlyList<ScanPlan> PlansOfKind(string kind) =>
        Settings.Plans.Where(p => p.PlanKind == kind).OrderByDescending(p => p.UpdatedAt).ToArray();

    /// <summary>新增或更新一个报文模板。</summary>
    public string SaveTemplate(FrameTemplate template)
    {
        var templates = Settings.Templates.Where(t => t.Id != template.Id).ToList();
        templates.Add(template);
        return Update(s => s with { Templates = templates });
    }

    public string RemoveTemplate(string templateId)
    {
        var templates = Settings.Templates.Where(t => t.Id != templateId).ToList();
        return Update(s => s with { Templates = templates });
    }

    /// <summary>新增或更新一个设备目标（端点 + Unit ID 相同视为同一个目标）。</summary>
    public string SaveTarget(DeviceTarget target)
    {
        var targets = Settings.Targets
            .Where(t => t.Id != target.Id &&
                        !(t.Endpoint == target.Endpoint && t.UnitId == target.UnitId))
            .ToList();
        targets.Add(target);
        return Update(s => s with { Targets = targets });
    }

    public string RemoveTarget(string targetId)
    {
        var targets = Settings.Targets.Where(t => t.Id != targetId).ToList();
        return Update(s => s with { Targets = targets });
    }
}

/// <summary>“请把这段十六进制载入报文工具”的跨页面消息。</summary>
public sealed record HexPayload(string Hex, string Message);

/// <summary>“请把这组串口参数应用到连接中心”的跨页面消息（R-11）。</summary>
public sealed record ApplySerialParametersRequest(SerialCombination Combination, bool Reconnect);
