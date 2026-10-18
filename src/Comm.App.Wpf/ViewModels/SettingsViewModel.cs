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
    [ObservableProperty] private string _dataDirectoryText = string.Empty;
    [ObservableProperty] private string _captureDirectoryText = string.Empty;
    [ObservableProperty] private string _message = string.Empty;
    [ObservableProperty] private string _ringPreview = string.Empty;

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
        WatchTableIntervalMs = _app.Settings.Scan.WatchTableIntervalMs;

        LogDirectoryText = _app.Log.Directory + (string.IsNullOrEmpty(_app.Log.DirectoryWarning) ? string.Empty : $"（{_app.Log.DirectoryWarning}）");
        ActiveLogFileText = _app.Log.ActiveLogFile ?? "（日志关闭时不会创建文件）";
        DataDirectoryText = _app.DataDirectory;
        CaptureDirectoryText = _app.CaptureDirectory;
        UpdateRingPreview();
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
}
