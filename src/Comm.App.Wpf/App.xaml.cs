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
        services.AddSingleton<SessionManager>();
        services.AddSingleton<MonitorService>();
        services.AddSingleton<WatchListService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<TaskStatusService>();
        services.AddSingleton<AppServices>();

        services.AddSingleton<ConnectionViewModel>();
        services.AddSingleton<DeviceScanViewModel>();
        services.AddSingleton<DataScanViewModel>();
        services.AddSingleton<RegisterLocatorViewModel>();
        services.AddSingleton<WatchTableViewModel>();
        services.AddSingleton<ManualSendViewModel>();
        services.AddSingleton<MonitorViewModel>();
        services.AddSingleton<NetworkToolsViewModel>();
        services.AddSingleton<LanScanViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _services = services.BuildServiceProvider();

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
                _services?.GetService<WatchListService>()?.Stop("程序退出");
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
