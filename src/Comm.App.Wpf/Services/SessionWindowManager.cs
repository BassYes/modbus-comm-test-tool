using Comm.App.Wpf.ViewModels;
using Comm.App.Wpf.Views;
using Comm.Core;
using Comm.Core.Errors;
using Comm.Settings;

namespace Comm.App.Wpf.Services;

/// <summary>
/// 拆分窗口的窗口管理（M3b R-16，规划 21.2.1）。
/// 每个被拆分的会话对应一个独立 <see cref="SessionWindow"/>；窗口位置/尺寸按会话分别持久化，
/// 显示器被移除或布局非法时回退到可见区域（规划 21.2.3）。
/// </summary>
public sealed class SessionWindowManager : IDisposable
{
    private readonly AppServices _app;
    private readonly SessionWorkspaces _workspaces;
    private readonly Dictionary<string, SessionWindow> _windows = new(StringComparer.Ordinal);

    public SessionWindowManager(AppServices app, SessionWorkspaces workspaces)
    {
        _app = app;
        _workspaces = workspaces;
    }

    /// <summary>拆分/移回引起的状态变化（主窗口标签页状态与按钮可用性刷新）。</summary>
    public event EventHandler? Changed;

    public bool IsDetached(string sessionId) => _windows.ContainsKey(sessionId);

    public int DetachedCount => _windows.Count;

    /// <summary>把会话工作区拆分到独立窗口。</summary>
    public Result<string> Split(string sessionId)
    {
        var workspace = _workspaces.Find(sessionId);
        if (workspace is null)
        {
            return Result<string>.Fail(new ErrorInfo(ErrorCodes.ValidationBadInput, ErrorCategory.Validation,
                Severity.Warning, "找不到该会话的工作区。"));
        }

        if (_windows.ContainsKey(sessionId))
        {
            // 已经拆分过：把现有窗口激活到前台即可。
            _windows[sessionId].Activate();
            return Result<string>.Ok("该会话已经拆分到独立窗口。");
        }

        var window = new SessionWindow(workspace, this, _workspaces);
        ApplyLayout(window, _workspaces.GetLayout(sessionId));
        _windows[sessionId] = window;
        workspace.IsDetached = true;
        window.Show();
        Changed?.Invoke(this, EventArgs.Empty);
        _app.Log.Info("Session", $"已把会话「{workspace.Name}」拆分到独立窗口", sessionId);
        return Result<string>.Ok($"已把会话「{workspace.Name}」拆分到独立窗口。");
    }

    /// <summary>把独立窗口中的会话移回主窗口。</summary>
    public Result<string> Return(string sessionId)
    {
        if (!_windows.TryGetValue(sessionId, out var window))
        {
            return Result<string>.Ok("该会话已在主窗口中。");
        }

        window.MoveBackToMain();
        Changed?.Invoke(this, EventArgs.Empty);
        var workspace = _workspaces.Find(sessionId);
        _app.Log.Info("Session", $"已把会话「{workspace?.Name ?? sessionId}」移回主窗口", sessionId);
        return Result<string>.Ok("已移回主窗口。");
    }

    /// <summary>关闭某个会话的独立窗口（会话被关闭或程序退出时调用）。</summary>
    public void CloseWindow(string sessionId)
    {
        if (!_windows.TryGetValue(sessionId, out var window)) return;
        window.CloseWithoutPrompt();
    }

    /// <summary>退出流程：关闭所有独立窗口（见规划 13.9；由 ShutdownCoordinator 调用）。</summary>
    public void CloseAll()
    {
        foreach (var window in _windows.Values.ToArray())
        {
            window.CloseWithoutPrompt();
        }
    }

    /// <summary>窗口自行关闭时（用户点右上角 X 或移回主窗口）回调。</summary>
    internal void OnWindowClosed(SessionWindow window)
    {
        if (!_windows.Remove(window.SessionId)) return;
        window.Workspace.IsDetached = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>窗口状态变化时保存布局（位置、尺寸、最大化、所属显示器）。</summary>
    internal void SaveLayout(SessionWindow window)
    {
        var bounds = window.WindowState == System.Windows.WindowState.Maximized ? window.RestoreBounds : new System.Windows.Rect(window.Left, window.Top, window.Width, window.Height);
        if (double.IsNaN(bounds.Width) || bounds.Width < 200 || double.IsNaN(bounds.Height) || bounds.Height < 200) return;

        _workspaces.SaveLayout(new SessionWindowLayout
        {
            SessionId = window.SessionId,
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = window.WindowState == System.Windows.WindowState.Maximized,
            MonitorDeviceName = window.CurrentMonitorName
        });
    }

    /// <summary>恢复上次位置；位置落在已移除的显示器上时回到主显示器可见区域（规划 21.2.3）。</summary>
    private static void ApplyLayout(SessionWindow window, SessionWindowLayout? layout)
    {
        if (layout is null)
        {
            window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
            return;
        }

        var target = SessionWindow.EnsureVisible(layout.Left, layout.Top, layout.Width, layout.Height);
        window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
        window.Left = target.X;
        window.Top = target.Y;
        window.Width = target.Width;
        window.Height = target.Height;
        if (layout.Maximized) window.WindowState = System.Windows.WindowState.Maximized;
    }

    public void Dispose()
    {
        CloseAll();
        _windows.Clear();
    }
}
