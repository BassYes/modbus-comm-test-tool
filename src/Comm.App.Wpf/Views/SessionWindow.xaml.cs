using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Comm.App.Wpf.Services;
using Comm.App.Wpf.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Comm.App.Wpf.Views;

/// <summary>拆分窗口的显示模型（窗口本身不是 ObservableObject）。</summary>
public sealed partial class SessionWindowHost : ObservableObject
{
    public SessionWindowHost(SessionWorkspaceViewModel workspace)
    {
        Workspace = workspace;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionWorkspaceViewModel.Title))
            {
                OnPropertyChanged(nameof(HeaderText));
                OnPropertyChanged(nameof(WindowTitle));
            }
        };
    }

    public SessionWorkspaceViewModel Workspace { get; }

    public string WindowTitle => $"通讯测试工具 — {Workspace.Title}";

    public string HeaderText => $"会话窗口：{Workspace.Title}（独立的连接与任务，互不影响）";
}

/// <summary>
/// 拆分出来的会话窗口（M3b R-16，规划 21.2.1）。
/// 窗口只负责显示某个会话的工作区；关闭时按规划 21.2.1 给出三选一确认。
/// </summary>
public partial class SessionWindow : Window
{
    private readonly SessionWindowManager _manager;
    private readonly SessionWorkspaces _workspaces;
    private bool _suppressPrompt;

    public SessionWindow(SessionWorkspaceViewModel workspace, SessionWindowManager manager, SessionWorkspaces workspaces)
    {
        Workspace = workspace;
        _manager = manager;
        _workspaces = workspaces;

        InitializeComponent();
        DataContext = new SessionWindowHost(workspace);

        Closing += OnClosing;
        Closed += (_, _) => _manager.OnWindowClosed(this);
        LocationChanged += (_, _) => _manager.SaveLayout(this);
        SizeChanged += (_, _) => _manager.SaveLayout(this);
    }

    public SessionWorkspaceViewModel Workspace { get; }

    public string SessionId => Workspace.SessionId;

    /// <summary>当前所在显示器名（布局持久化用，规划 21.2.2 第 5 点）。</summary>
    public string? CurrentMonitorName
    {
        get
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero) return null;

                var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
                if (monitor == IntPtr.Zero) return null;

                var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                return GetMonitorInfo(monitor, ref info) ? info.szDevice : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>把窗口位置约束到仍然存在的显示器可见区域内（显示器被移除后回到可见区域）。</summary>
    public static Rect EnsureVisible(double left, double top, double width, double height)
    {
        var virtualScreen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        width = Math.Max(900, width);
        height = Math.Max(480, height);

        if (double.IsNaN(left) || double.IsNaN(top)) return new Rect(double.NaN, double.NaN, width, height);

        var target = new Rect(left, top, width, height);
        bool visible = target.Right > virtualScreen.Left + 80 &&
                       target.Left < virtualScreen.Right - 80 &&
                       target.Bottom > virtualScreen.Top + 40 &&
                       target.Top < virtualScreen.Bottom - 40;

        if (visible) return target;

        return new Rect(
            Math.Max(virtualScreen.Left, Math.Min(left, virtualScreen.Right - width)),
            Math.Max(virtualScreen.Top, Math.Min(top, virtualScreen.Bottom - height)),
            width,
            height);
    }

    /// <summary>"移回主窗口"按钮 / 命令：把工作区交回主窗口并关闭本窗口。</summary>
    public void MoveBackToMain()
    {
        _suppressPrompt = true;
        _manager.SaveLayout(this);
        Workspace.IsDetached = false;
        Close();
    }

    /// <summary>程序退出或会话被关闭：不再询问，直接关闭。</summary>
    public void CloseWithoutPrompt()
    {
        _suppressPrompt = true;
        _manager.SaveLayout(this);
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _manager.SaveLayout(this);
        if (_suppressPrompt) return;

        var session = Workspace.Session;
        var dialog = new SessionCloseDialogWindow(new SessionCloseChoiceRequest
        {
            Message = $"会话「{session.Name}」当前状态：{session.StatusText}。\n请选择如何处理这个会话窗口。"
        })
        {
            Owner = this
        };

        dialog.ShowDialog();
        switch (dialog.Choice)
        {
            case SessionCloseChoice.MoveBackToMain:
                Workspace.IsDetached = false;
                break;

            case SessionCloseChoice.CloseSession:
            {
                var result = _workspaces.CloseSession(SessionId);
                if (!result.IsSuccess)
                {
                    e.Cancel = true;
                    MessageBox.Show(this, result.Error!.ToUserText(), "无法关闭会话", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Workspace.IsDetached = false;
                break;
            }

            default:
                e.Cancel = true;
                return;
        }
    }

    private void OnMoveBack(object sender, RoutedEventArgs e) => MoveBackToMain();

    // ---------------------------------------------------------------- 多显示器（Win32）

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
