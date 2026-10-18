using System.ComponentModel;
using System.Windows;
using Comm.App.Wpf.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Comm.App.Wpf;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 关闭主窗口时先关闭拆分出来的会话窗口（M3b R-16）：窗口必须在 UI 线程上关闭，
    /// 所以这一步放在这里做，而不是放到在后台线程执行的关闭协调器里（见规划 13.9）。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        try
        {
            (Application.Current as App)?.Services?.GetService<SessionWindowManager>()?.CloseAll();
        }
        catch (Exception)
        {
            // 关闭窗口失败不能阻止程序退出
        }

        base.OnClosing(e);
    }
}
