using System.Windows;

namespace Comm.App.Wpf.Views;

/// <summary>关闭拆分窗口时的三选一对话框（规划 21.2.1：取消任务并关闭 / 保持会话并移回主窗口 / 取消）。</summary>
public enum SessionCloseChoice
{
    Cancel,
    MoveBackToMain,
    CloseSession
}

public sealed class SessionCloseChoiceRequest
{
    public string Message { get; init; } = string.Empty;
}

public partial class SessionCloseDialogWindow : Window
{
    private readonly SessionCloseChoiceRequest _request;

    public SessionCloseDialogWindow(SessionCloseChoiceRequest request)
    {
        _request = request;
        InitializeComponent();
        DataContext = request;
    }

    public SessionCloseChoice Choice { get; private set; } = SessionCloseChoice.Cancel;

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = SessionCloseChoice.Cancel;
        DialogResult = false;
        Close();
    }

    private void OnMoveBack(object sender, RoutedEventArgs e)
    {
        Choice = SessionCloseChoice.MoveBackToMain;
        DialogResult = true;
        Close();
    }

    private void OnCloseSession(object sender, RoutedEventArgs e)
    {
        Choice = SessionCloseChoice.CloseSession;
        DialogResult = true;
        Close();
    }
}
