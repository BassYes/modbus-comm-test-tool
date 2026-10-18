using System.Collections.ObjectModel;
using Comm.App.Wpf.Services;
using System.IO;
using Comm.Diagnostics;
using Comm.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Comm.App.Wpf.ViewModels;

/// <summary>设置与日志（见规划 4.10、12.6）。M2 新增：显示缓冲行数、方向着色、方案 / 模板 / 设备目标管理。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _app;

    public SettingsViewModel(AppServices app)
    {
        _app = app;
        LoadFromSettings();
        RefreshStoredItems();
        RefreshWindowLayouts();
    }

    /// <summary>已保存的扫描方案 / 报文模板 / 设备目标（M2-3 持久化的可见入口）。</summary>
    public ObservableCollection<ScanPlan> Plans { get; } = new();
    public ObservableCollection<FrameTemplate> Templates { get; } = new();
    public ObservableCollection<DeviceTarget> Targets { get; } = new();

    public IReadOnlyList<Option<LogLevel>> LevelOptions { get; } = new[]
    {
        new Option<LogLevel>(LogLevel.Trace, "Trace（最详细，体积最大）"),
        new Option<LogLevel>(LogLevel.Debug, "Debug（推荐，含通信细节）"),
        new Option<LogLevel>(LogLevel.Information, "Information（常规运行事件）"),
        new Option<LogLevel>(LogLevel.Warning, "Warning（仅警告与错误）")
    };

    [ObservableProperty] private bool _logEnabled;
    [ObservableProperty] private LogLevel _minimumLevel = LogLevel.Debug;
    [ObservableProperty] private bool _includeRawFrames;
    [ObservableProperty] private int _maxFileSizeMb = 20;
    [ObservableProperty] private int _retentionDays = 14;
    [ObservableProperty] private int _totalSizeMb = 500;
    [ObservableProperty] private bool _writeCrashReports = true;
    [ObservableProperty] private int _crashReportsToKeep = 10;
    [ObservableProperty] private string _directoryOverride = string.Empty;

    // ---- 监视与扫描（M1 欠账 D-2 / D-3 的可见入口）
    [ObservableProperty] private int _monitorUiCapacity = 20_000;
    [ObservableProperty] private bool _colorizeDirections = true;
    [ObservableProperty] private int _watchTableIntervalMs = 1000;
    [ObservableProperty] private ScanPlan? _selectedPlan;
    [ObservableProperty] private FrameTemplate? _selectedTemplate;
    [ObservableProperty] private DeviceTarget? _selectedTarget;

    [ObservableProperty] private string _logDirectoryText = string.Empty;
    [ObservableProperty] private string _activeLogFileText = string.Empty;
    [ObservableProperty] private string _versionText = AppInfo.Summary();

    /// <summary>X-9：“关于”行：版本 + 提交号 + 构建时间。</summary>
    public string AboutText => AppInfo.FullVersionText;

    [ObservableProperty] private string _dataDirectoryText = string.Empty;
    [ObservableProperty] private string _captureDirectoryText = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _ringPreview = string.Empty;

    /// <summary>X-8：环境自检报告（运行“环境检查”后填充）。</summary>
    [ObservableProperty] private string _environmentReportText = string.Empty;

    // ---- 会话与工作区（M3b：规划 4.12 工作区/项目文件、21.2 多会话）
    [ObservableProperty] private int _maxSessions = 8;
    [ObservableProperty] private bool _reopenLastWorkspace;
    [ObservableProperty] private string _workspaceNameText = "现场工作区";
    [ObservableProperty] private string _workspacePathText = string.Empty;
    [ObservableProperty] private string _workspaceStatusText = "尚未载入工作区文件。";
    [ObservableProperty] private string _workspaceSummaryText = string.Empty;
    [ObservableProperty] private bool _hasLoadedWorkspace;

    /// <summary>工作区文件校验发现的问题（提示用，不阻止保存）。</summary>
    public ObservableCollection<string> WorkspaceIssues { get; } = new();

    /// <summary>已保存的会话窗口布局（拆分窗口的位置/尺寸，规划 21.2.2 第 5 点）。</summary>
    public ObservableCollection<string> WindowLayouts { get; } = new();

    private WorkspaceFile? _loadedWorkspace;

    private void LoadFromSettings()
    {
        var log = _app.Settings.Log;
        LogEnabled = log.Enabled;
        MinimumLevel = log.MinimumLevel;
        IncludeRawFrames = log.IncludeRawFrames;
        MaxFileSizeMb = log.MaxFileSizeMb;
        RetentionDays = log.RetentionDays;
        TotalSizeMb = log.TotalSizeMb;
        WriteCrashReports = log.WriteCrashReports;
        CrashReportsToKeep = log.CrashReportsToKeep;
        DirectoryOverride = log.Directory ?? string.Empty;
        MonitorUiCapacity = _app.Settings.Monitor.UiCapacity;
        ColorizeDirections = _app.Settings.Monitor.ColorizeDirections;
        MaxSessions = _app.Settings.Sessions.MaxSessions;
        ReopenLastWorkspace = _app.Settings.Sessions.ReopenLastWorkspace;
        WorkspacePathText = _app.Settings.Sessions.LastWorkspaceFile ?? string.Empty;
        WatchTableIntervalMs = _app.Settings.Scan.WatchTableIntervalMs;

        LogDirectoryText = _app.Log.Directory + (string.IsNullOrEmpty(_app.Log.DirectoryWarning) ? string.Empty : $"（{_app.Log.DirectoryWarning}）");
        ActiveLogFileText = _app.Log.ActiveLogFile ?? "（日志关闭时不会创建文件）";
        DataDirectoryText = _app.DataDirectoryText;
        CaptureDirectoryText = _app.CaptureDirectory;
        UpdateRingPreview();
    }

    /// <summary>X-2：显示实际数据目录、便携优先策略与两个候选目录。</summary>
    public string DirectoryPolicyText
    {
        get
        {
            var info = AppPaths.Info;
            var lines = new List<string>
            {
                $"实际数据目录：{info.Directory}",
                $"便携目录（程序同级）：{AppPaths.PortableDataDirectory}；便携标志 portable.flag：{(AppPaths.HasPortableFlag ? "存在" : "不存在")}",
                $"用户目录（不可写时回退）：{AppPaths.UserDataDirectory}",
                "策略：程序目录可写 → 便携 data/；不可写 → %LOCALAPPDATA%\\通讯测试工具\\data。"
            };
            if (!string.IsNullOrEmpty(info.FallbackReason)) lines.Add($"提示：{info.FallbackReason}");
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>X-8：采集本机环境并生成报告（仅本地生成与保存，不上传）。</summary>
    private string BuildEnvironmentReport()
    {
        IReadOnlyList<string> ports;
        try
        {
            ports = Comm.Transport.SerialChannel.GetPortNames();
        }
        catch (Exception)
        {
            ports = Array.Empty<string>();
        }

        IReadOnlyList<string> adapters;
        try
        {
            adapters = Comm.Network.NetworkAdapters.Read()
                .Select(a => $"{a.Name}｜{a.Description}｜{a.Status}｜IPv4 {a.PrimaryIpv4}/{a.PrimaryMask}｜网关 {a.PrimaryGateway}｜MAC {a.MacAddress}｜{a.SpeedText}" +
                             (a.IsVirtual ? "｜（虚拟/VPN）" : string.Empty))
                .ToArray();
        }
        catch (Exception ex)
        {
            adapters = new[] { $"网卡枚举失败：{ex.Message}" };
        }

        var display = DescribeDisplay();
        return EnvironmentReport.Build(ports, adapters, display);
    }

    private static string DescribeDisplay()
    {
        try
        {
            var window = System.Windows.Application.Current?.MainWindow;
            if (window is null) return "未检测（无主窗口）";
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
            var area = System.Windows.SystemParameters.WorkArea;
            return $"窗口 DPI 缩放：{(dpi.DpiScaleX * 100):F0}%（{dpi.PixelsPerDip:F2} 像素/DIP）；" +
                   $"主屏工作区：{area.Width:F0}×{area.Height:F0} DIP；窗口最小尺寸：900×480 DIP";
        }
        catch (Exception ex)
        {
            return $"检测失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void CheckEnvironment()
    {
        EnvironmentReportText = BuildEnvironmentReport();
        Message = "环境检查已完成（报告可在右侧查看、复制或保存）。";
        _app.Log.Info("Environment", "已执行环境自检（X-8）");
    }

    [RelayCommand]
    private void CopyEnvironmentReport()
    {
        var report = string.IsNullOrEmpty(EnvironmentReportText) ? BuildEnvironmentReport() : EnvironmentReportText;
        EnvironmentReportText = report;
        try
        {
            System.Windows.Clipboard.SetText(report);
            Message = "环境报告已复制到剪贴板。";
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("复制失败", ex.Message);
        }
    }

    [RelayCommand]
    private void SaveEnvironmentReport()
    {
        var report = string.IsNullOrEmpty(EnvironmentReportText) ? BuildEnvironmentReport() : EnvironmentReportText;
        EnvironmentReportText = report;

        var path = _app.Dialogs.SaveFile("保存环境报告", "文本文件 (*.txt)|*.txt",
            $"环境报告-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            File.WriteAllText(path, report, new System.Text.UTF8Encoding(true));
            Message = $"环境报告已保存：{path}";
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("保存失败", ex.Message);
        }
    }

    /// <summary>X-2：在程序目录创建 portable.flag，下次启动即使用便携 data/。</summary>
    [RelayCommand]
    private void CreatePortableFlag()
    {
        var path = Path.Combine(AppPaths.BaseDirectory, AppPaths.PortableFlagName);
        try
        {
            Directory.CreateDirectory(AppPaths.BaseDirectory);
            File.WriteAllText(path, $"便携标记，由“设置与日志”页创建于 {DateTimeOffset.Now:O}" + Environment.NewLine);
            Message = $"已创建便携标记：{path}（重启后使用程序同级 data/ 目录）";
            OnPropertyChanged(nameof(DirectoryPolicyText));
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("创建失败", ex.Message);
        }
    }

    [RelayCommand]
    private void MigrateToPortable() => Migrate(AppPaths.PortableDataDirectory, "便携");

    [RelayCommand]
    private void MigrateToUserData() => Migrate(AppPaths.UserDataDirectory, "用户");

    private void Migrate(string target, string label)
    {
        if (string.Equals(target, AppPaths.DataDirectory, StringComparison.OrdinalIgnoreCase))
        {
            Message = $"当前已经在使用{label}数据目录：{target}";
            return;
        }

        var error = AppPaths.MigrateSettings(AppPaths.DataDirectory, target, overwrite: false);
        Message = string.IsNullOrEmpty(error)
            ? $"已把当前设置复制到{label}目录：{target}（未覆盖已有文件；重启后按目录策略生效）"
            : error;
        OnPropertyChanged(nameof(DirectoryPolicyText));
    }

    private void UpdateRingPreview()
    {
        RingPreview = _app.Log.BuildDiagnosticText(60);
    }

    [RelayCommand]
    private void Apply()
    {
        var options = new LogOptions
        {
            Enabled = LogEnabled,
            Directory = string.IsNullOrWhiteSpace(DirectoryOverride) ? null : DirectoryOverride.Trim(),
            MinimumLevel = MinimumLevel,
            IncludeRawFrames = IncludeRawFrames,
            MaxFileSizeMb = Math.Max(1, MaxFileSizeMb),
            RetentionDays = Math.Max(1, RetentionDays),
            TotalSizeMb = Math.Max(10, TotalSizeMb),
            WriteCrashReports = WriteCrashReports,
            CrashReportsToKeep = Math.Max(1, CrashReportsToKeep)
        };

        var error = _app.SaveSettings(_app.Settings with
        {
            Log = options,
            Sessions = _app.Settings.Sessions with
            {
                MaxSessions = Math.Clamp(MaxSessions, 1, 32),
                ReopenLastWorkspace = ReopenLastWorkspace
            },
            Monitor = _app.Settings.Monitor with
            {
                UiCapacity = Math.Max(1000, MonitorUiCapacity),
                ColorizeDirections = ColorizeDirections
            },
            Scan = _app.Settings.Scan with
            {
                WatchTableIntervalMs = Math.Max(200, WatchTableIntervalMs)
            }
        });
        _app.Log.ApplyOptions(options);
        _app.Log.CleanupOldFiles();
        _app.Monitor.UiCapacity = Math.Max(1000, MonitorUiCapacity);
        _app.Monitor.ColorizeDirections = ColorizeDirections;

        Message = string.IsNullOrEmpty(error) ? "设置已保存。" : error;
        if (!string.IsNullOrEmpty(error)) _app.Dialogs.Error("保存失败", error);

        LogDirectoryText = _app.Log.Directory;
        ActiveLogFileText = _app.Log.ActiveLogFile ?? "（日志关闭时不会创建文件）";
        UpdateRingPreview();
    }

    [RelayCommand]
    private void RefreshPreview() => UpdateRingPreview();

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(_app.Log.Directory);

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_app.DataDirectory);

    [RelayCommand]
    private void OpenCaptureFolder() => OpenFolder(_app.CaptureDirectory);

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("打开目录失败", ex.Message);
        }
    }

    [RelayCommand]
    private void CopyDiagnostics()
    {
        try
        {
            var text = _app.Log.BuildDiagnosticText(500);
            System.Windows.Clipboard.SetText(text);
            Message = "诊断信息已复制到剪贴板（可直接粘贴给开发人员）。";
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("复制失败", ex.Message);
        }
    }

    [RelayCommand]
    private void CreateBundle()
    {
        var path = _app.Dialogs.SaveFile("生成诊断包", "ZIP 压缩包 (*.zip)|*.zip",
            $"诊断包-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (string.IsNullOrEmpty(path)) return;

        var settingsJson = File.Exists(_app.Store.Path) ? File.ReadAllText(_app.Store.Path) : null;
        var error = DiagnosticsCollector.Collect(_app.Log, path, settingsJson, IncludeRawFrames);
        if (string.IsNullOrEmpty(error))
        {
            Message = $"诊断包已生成：{path}";
            _app.Dialogs.Info("完成", $"诊断包已生成（仅保存在本机，不会自动上传）：\n{path}");
        }
        else
        {
            _app.Dialogs.Error("生成失败", error);
        }
    }

    [RelayCommand]
    private void ClearLogs()
    {
        if (!_app.Dialogs.Confirm("确认清理", $"将删除日志目录中的日志文件：\n{_app.Log.Directory}\n\n" +
                                             "（正在写入的当前日志文件会保留）\n继续？"))
        {
            return;
        }

        int deleted = 0;
        try
        {
            var active = _app.Log.ActiveLogFile;
            foreach (var file in Directory.GetFiles(_app.Log.Directory, "*.log"))
            {
                if (string.Equals(file, active, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception)
                {
                    // 单个文件被占用时跳过
                }
            }
        }
        catch (Exception ex)
        {
            _app.Dialogs.Error("清理失败", ex.Message);
            return;
        }

        Message = $"已删除 {deleted} 个日志文件。";
    }

    [RelayCommand]
    private void ResetSettings()
    {
        if (!_app.Dialogs.Confirm("确认重置", "将把设置恢复为默认值（连接参数、扫描参数、显示与日志设置）。是否继续？"))
        {
            return;
        }

        var error = _app.SaveSettings(new AppSettings());
        LoadFromSettings();
        Message = string.IsNullOrEmpty(error) ? "设置已恢复默认值，重启程序后完全生效。" : error;
    }

    // ---------------------------------------------------------------- 已保存内容管理（M2-3）

    private void RefreshStoredItems()
    {
        Plans.Clear();
        foreach (var plan in _app.Settings.Plans.OrderByDescending(p => p.UpdatedAt)) Plans.Add(plan);

        Templates.Clear();
        foreach (var template in _app.Settings.Templates.OrderByDescending(t => t.UpdatedAt)) Templates.Add(template);

        Targets.Clear();
        foreach (var target in _app.Settings.Targets.OrderBy(t => t.Name)) Targets.Add(target);
    }

    [RelayCommand]
    private void DeletePlan()
    {
        if (SelectedPlan is null) return;
        var name = SelectedPlan.DisplayText;
        var error = _app.RemovePlan(SelectedPlan.Id);
        RefreshStoredItems();
        Message = string.IsNullOrEmpty(error) ? $"已删除扫描方案：{name}" : error;
    }

    [RelayCommand]
    private void DeleteTemplate()
    {
        if (SelectedTemplate is null) return;
        var name = SelectedTemplate.DisplayText;
        var error = _app.RemoveTemplate(SelectedTemplate.Id);
        RefreshStoredItems();
        Message = string.IsNullOrEmpty(error) ? $"已删除报文模板：{name}" : error;
    }

    [RelayCommand]
    private void DeleteTarget()
    {
        if (SelectedTarget is null) return;
        var name = SelectedTarget.DisplayText;
        var error = _app.RemoveTarget(SelectedTarget.Id);
        RefreshStoredItems();
        Message = string.IsNullOrEmpty(error) ? $"已删除设备目标：{name}" : error;
    }

    // ---------------------------------------------------------------- 会话与工作区（M3b，规划 4.12 / 21.2）

    /// <summary>把当前设置保存为工作区/项目文件：方案、模板、设备目标、网卡方案、串口与 TCP 模板。</summary>
    [RelayCommand]
    private void SaveWorkspace()
    {
        var name = string.IsNullOrWhiteSpace(WorkspaceNameText) ? WorkspaceDefaults.DefaultName : WorkspaceNameText.Trim();
        var defaultName = System.IO.Path.GetFileName(
            string.IsNullOrWhiteSpace(WorkspacePathText) ? WorkspaceStore.DefaultPath(_app.DataDirectory, name) : WorkspacePathText);

        var path = _app.Dialogs.SaveFile("保存工作区/项目文件", "工作区文件 (*.ctws)|*.ctws", defaultName);
        if (string.IsNullOrEmpty(path)) return;

        var store = new WorkspaceStore(path);
        var file = WorkspaceStore.FromSettings(name, _app.Settings, $"由 {AppInfo.Summary()} 保存", AppInfo.Version);
        RefreshWorkspaceIssues(file);

        var error = store.Save(file);
        if (!string.IsNullOrEmpty(error))
        {
            _app.Dialogs.Error("保存工作区失败", error);
            return;
        }

        _loadedWorkspace = file;
        HasLoadedWorkspace = true;
        WorkspacePathText = store.Path;
        WorkspaceSummaryText = file.SummaryText;
        WorkspaceStatusText = $"已保存工作区：{store.Path}";
        _app.Update(s => s with { Sessions = s.Sessions with { LastWorkspaceFile = store.Path } });
        _app.Log.Info("Workspace", $"已保存工作区文件：{store.Path}（{file.SummaryText}）");
        Message = WorkspaceStatusText;
    }

    /// <summary>打开工作区文件并预览（不立即应用）。</summary>
    [RelayCommand]
    private void OpenWorkspace()
    {
        var path = _app.Dialogs.OpenFile("打开工作区/项目文件", "工作区文件 (*.ctws)|*.ctws|所有文件 (*.*)|*.*");
        if (string.IsNullOrEmpty(path)) return;

        var store = new WorkspaceStore(path);
        var result = store.Load(out var warning);
        if (!result.IsSuccess)
        {
            _app.Dialogs.Error("打开工作区失败", result.Error!.ToUserText());
            return;
        }

        _loadedWorkspace = result.Value!;
        HasLoadedWorkspace = true;
        WorkspacePathText = store.Path;
        WorkspaceNameText = _loadedWorkspace.Name;
        WorkspaceSummaryText = _loadedWorkspace.SummaryText;
        RefreshWorkspaceIssues(_loadedWorkspace);

        var issues = WorkspaceIssues.Count;
        WorkspaceStatusText = $"已载入工作区：{store.Path}" +
                              (issues == 0 ? string.Empty : $"（有 {issues} 个提示）") +
                              (store.LastLoadWasReadOnly ? "（只读：文件版本高于当前程序，不会被覆盖）" : string.Empty);

        if (!string.IsNullOrEmpty(warning)) _app.Dialogs.Warn("工作区文件提示", warning);
        _app.Update(s => s with { Sessions = s.Sessions with { LastWorkspaceFile = store.Path } });
        _app.Log.Info("Workspace", $"已载入工作区文件：{store.Path}（{_loadedWorkspace.SummaryText}）");
        Message = WorkspaceStatusText;
    }

    /// <summary>用工作区内容替换当前配置集合。</summary>
    [RelayCommand]
    private void ApplyWorkspaceReplace() => ApplyWorkspace(WorkspaceMergeMode.Replace);

    /// <summary>把工作区内容合并进当前配置（同 Id 以工作区为准，其余保留）。</summary>
    [RelayCommand]
    private void ApplyWorkspaceMerge() => ApplyWorkspace(WorkspaceMergeMode.Merge);

    private void ApplyWorkspace(WorkspaceMergeMode mode)
    {
        if (_loadedWorkspace is null)
        {
            _app.Dialogs.Warn("尚未载入工作区", "请先用“打开工作区文件”载入一个 .ctws 文件。");
            return;
        }

        var modeText = mode == WorkspaceMergeMode.Replace ? "替换当前配置集合" : "合并到当前配置（同 Id 以工作区为准）";
        var confirmText = "将把工作区「" + _loadedWorkspace.Name + "」应用到本机设置。\n\n" +
                          "内容：" + _loadedWorkspace.SummaryText + "\n" +
                          "方式：" + modeText + "\n\n" +
                          "日志、监视、扫描默认值等运行期设置不会被改动。确认应用吗？";

        if (!_app.Dialogs.Confirm("应用工作区", confirmText)) return;

        var updated = WorkspaceStore.ApplyTo(_loadedWorkspace, _app.Settings, mode);
        var error = _app.SaveSettings(updated);
        if (!string.IsNullOrEmpty(error))
        {
            _app.Dialogs.Error("应用工作区失败", error);
            return;
        }

        RefreshStoredItems();
        LoadFromSettings();
        RefreshWindowLayouts();

        var points = _loadedWorkspace.Contents.WatchPoints.Count;
        WorkspaceStatusText = $"已应用工作区「{_loadedWorkspace.Name}」（{modeText}）" +
                              (points > 0 ? $"；工作区内含 {points} 个监视点位（监视表为会话作用域，请在监视表页添加）。" : string.Empty);
        _app.Log.Info("Workspace", WorkspaceStatusText);
        Message = WorkspaceStatusText;
    }

    [RelayCommand]
    private void RefreshWorkspaceIssues()
    {
        if (_loadedWorkspace is null)
        {
            WorkspaceIssues.Clear();
            Message = "尚未载入工作区文件。";
            return;
        }

        RefreshWorkspaceIssues(_loadedWorkspace);
        Message = WorkspaceIssues.Count == 0 ? "工作区内容校验通过。" : $"工作区内容有 {WorkspaceIssues.Count} 个提示。";
    }

    private void RefreshWorkspaceIssues(WorkspaceFile file)
    {
        WorkspaceIssues.Clear();
        foreach (var issue in WorkspaceValidator.Validate(file)) WorkspaceIssues.Add(issue);
    }

    /// <summary>清除所有会话窗口布局（显示器变更、布局异常时的恢复手段）。</summary>
    [RelayCommand]
    private void ClearWindowLayouts()
    {
        var count = _app.Settings.Sessions.WindowLayouts.Count;
        if (count == 0)
        {
            Message = "当前没有保存的会话窗口布局。";
            return;
        }

        if (!_app.Dialogs.Confirm("清除窗口布局", $"将删除 {count} 条会话窗口布局记录（下次拆分窗口回到默认位置）。确认继续？")) return;

        _app.Update(s => s with { Sessions = s.Sessions with { WindowLayouts = Array.Empty<SessionWindowLayout>() } });
        RefreshWindowLayouts();
        Message = $"已清除 {count} 条会话窗口布局。";
    }

    private void RefreshWindowLayouts()
    {
        WindowLayouts.Clear();
        foreach (var layout in _app.Settings.Sessions.WindowLayouts)
        {
            WindowLayouts.Add($"会话 {layout.SessionId}：{layout.Width:F0}×{layout.Height:F0} @ ({layout.Left:F0},{layout.Top:F0})" +
                              (layout.Maximized ? " 最大化" : string.Empty) +
                              (string.IsNullOrWhiteSpace(layout.MonitorDeviceName) ? string.Empty : $" · {layout.MonitorDeviceName}"));
        }
    }
}
