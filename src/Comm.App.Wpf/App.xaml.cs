using System.Windows;
using Comm.App.Wpf.Services;
using Comm.App.Wpf.ViewModels;
using Comm.Capture;
using Comm.Core.Tasks;
using Comm.Diagnostics;
using Comm.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace Comm.App.Wpf;

public partial class App : Application
{
    private ServiceProvider? _services;
    private ShutdownCoordinator? _shutdown;

    /// <summary>服务容器（窗口在关闭时用它取会话窗口管理器等 UI 线程服务）。</summary>
    public IServiceProvider? Services => _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 设置加载（损坏时保留原文件并以默认值启动；v1 → v2 自动迁移，见规划 15.10）
        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load(out var settingsWarning);

        // 日志闸门与崩溃报告（见规划 4.10、12.6、15.9）
        var logGate = new LogGate(settings.Log);
        var crashReporter = new CrashReporter(logGate, settings.Log.WriteCrashReports, settings.Log.CrashReportsToKeep);
        crashReporter.Install();
        var previousCrash = CrashReporter.DetectPreviousCrash();
        if (previousCrash)
        {
            logGate.Warning("App", "检测到上次运行未正常退出（存在 .running 标志文件）");
        }
        CrashReporter.MarkRunning();
        logGate.Info("App", $"程序启动。{AppInfo.Summary()}");
        logGate.Info("App", $"数据目录：{AppPaths.ToDisplayText()}");
        if (!string.IsNullOrEmpty(AppPaths.Info.FallbackReason)) logGate.Warning("App", AppPaths.Info.FallbackReason);
        if (!string.IsNullOrEmpty(settingsWarning)) logGate.Warning("App", settingsWarning);
        if (!string.IsNullOrEmpty(logGate.DirectoryWarning)) logGate.Warning("App", logGate.DirectoryWarning);

        // 报文总线（显示缓冲 + 落盘）。R-21 X-2：落盘目录跟随 AppPaths 的数据目录。
        var captureDirectory = settings.LastCaptureDirectory ?? AppPaths.CapturesDirectory;
        var writer = new FrameFileWriter(maxFileSizeMb: 64);
        var frameBus = new FrameBus(settings.Monitor.DisplayCapacity, writer);

        var services = new ServiceCollection();
        services.AddSingleton(settingsStore);
        services.AddSingleton(settings);
        services.AddSingleton(logGate);
        services.AddSingleton(crashReporter);
        services.AddSingleton(frameBus);
        services.AddSingleton<SerialPortRegistry>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<SessionWorkspaces>();
        services.AddSingleton<SessionWindowManager>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<TaskStatusService>();
        services.AddSingleton<AppServices>();
        services.AddSingleton<PluginService>();

        // 会话页面（连接中心/设备扫描/数据扫描/寄存器定位/监视表/报文工具/报文监视）
        // 由 SessionWorkspaces 按会话创建，不再注册为应用级单例（M3b R-16）。
        services.AddSingleton<NetworkToolsViewModel>();
        services.AddSingleton<LanScanViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<ExtensionCenterViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _services = services.BuildServiceProvider();

        // M3b：按设置自动重新打开上次的工作区文件（规划 4.12；失败不影响启动）。
        TryReopenLastWorkspace(logGate);

        // 全局异常处理：可恢复的界面异常不终止程序（见规划 15.9）
        DispatcherUnhandledException += (_, args) =>
        {
            logGate.Error("App", "界面线程未处理异常", args.Exception);
            crashReporter.WriteReport(args.Exception, "界面线程未处理异常");
            MessageBox.Show($"发生内部错误：{args.Exception.Message}\n\n详情已写入日志（错误码 {"INT-9001"}）。",
                "内部错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // 关闭协调器：最多执行一次、按顺序、逐个隔离异常（见 M1 反馈问题 5/10）
        _shutdown = new ShutdownCoordinator(new[]
        {
            new ShutdownStep("停止监视表", () =>
            {
                // M3b：监视表是会话作用域的，需要逐个会话停止（并覆盖运行中新建的会话）。
                _services?.GetService<SessionManager>()?.StopAllWatchLists("程序退出");
                return ValueTask.CompletedTask;
            }),
            new ShutdownStep("断开会话", async () =>
            {
                var sessions = _services?.GetService<SessionManager>();
                if (sessions is not null) await sessions.DisposeAsync().ConfigureAwait(false);
            }),
            new ShutdownStep("刷新并关闭报文落盘", async () =>
            {
                var bus = _services?.GetService<FrameBus>();
                if (bus is not null) await bus.DisposeAsync().ConfigureAwait(false);
            }),
            new ShutdownStep("记录正常退出", () =>
            {
                logGate.Info("App", "程序正常退出");
                CrashReporter.MarkCleanExit();
                return ValueTask.CompletedTask;
            }),
            new ShutdownStep("关闭日志", async () => await logGate.DisposeAsync().ConfigureAwait(false)),
            // 容器内还有 MonitorService / WatchListService 等，异步释放容器（不要用同步 Dispose：
            // 容器中存在只实现 IAsyncDisposable 的服务时会抛 InvalidOperationException）。
            new ShutdownStep("释放服务容器", async () =>
            {
                if (_services is not null) await _services.DisposeAsync().ConfigureAwait(false);
            })
        });

        var mainWindow = _services.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _services.GetRequiredService<MainViewModel>();
        MainWindow = mainWindow;
        mainWindow.Show();

        if (previousCrash || crashReporter.LastReportPath is not null)
        {
            var report = crashReporter.LastReportPath;
            MessageBox.Show(
                "检测到上次运行异常退出。" + (report is null ? string.Empty : $"\n崩溃报告：{report}") +
                "\n\n可在“设置与日志”页查看日志目录与诊断信息。",
                "运行提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 启动时按设置重新打开上次的工作区文件（合并方式导入，避免覆盖用户之后的改动）。
    /// 任何问题都只记日志，不阻断启动（规划 15.1、15.10）。
    /// </summary>
    private void TryReopenLastWorkspace(LogGate logGate)
    {
        try
        {
            var appServices = _services?.GetService<AppServices>();
            if (appServices is null) return;

            var sessions = appServices.Settings.Sessions;
            if (!sessions.ReopenLastWorkspace) return;

            var path = sessions.LastWorkspaceFile;
            if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            {
                logGate.Warning("Workspace", $"设置了自动重开工作区，但文件不存在：{path}");
                return;
            }

            var store = new Comm.Settings.WorkspaceStore(path);
            var loaded = store.Load(out var warning);
            if (!loaded.IsSuccess)
            {
                logGate.Warning("Workspace", $"自动重开工作区失败：{loaded.Error?.ToUserText()}");
                return;
            }

            var applied = Comm.Settings.WorkspaceStore.ApplyTo(loaded.Value!, appServices.Settings,
                Comm.Settings.WorkspaceMergeMode.Merge);
            var error = appServices.SaveSettings(applied);

            logGate.Info("Workspace", error is null
                ? $"已自动重开工作区：{path}（{loaded.Value!.SummaryText}）"
                : $"自动重开工作区后保存设置失败：{error}");
            if (!string.IsNullOrEmpty(warning)) logGate.Warning("Workspace", warning);
        }
        catch (Exception ex)
        {
            logGate.Warning("Workspace", $"自动重开工作区异常：{ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            // 在后台线程上等待异步关闭：避免 UI 线程等待自身导致死锁。
            var failures = _shutdown is null
                ? Array.Empty<string>()
                : Task.Run(() => _shutdown.ShutdownAsync().AsTask()).GetAwaiter().GetResult();

            foreach (var failure in failures)
            {
                _services?.GetService<LogGate>()?.Warning("App", $"退出步骤失败：{failure}");
            }
        }
        catch (Exception)
        {
            // 退出流程中的异常不再上报，更不再弹窗（否则会形成递归提示）
        }
        finally
        {
            base.OnExit(e);
        }
    }
}
