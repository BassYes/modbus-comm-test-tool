using System.IO;
using System.Windows;
using Comm.Core.Errors;
using Comm.Protocols.Modbus;
using Microsoft.Win32;

namespace Comm.App.Wpf.Services;

/// <summary>对话框与文件选择（见规划 15.12：就地提示/状态栏/通知条/模态对话框的载体）。</summary>
public sealed class DialogService
{
    public void Info(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Warn(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void Error(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowError(ErrorInfo error, string title = "操作失败") =>
        MessageBox.Show(error.ToUserText() + $"\n\n错误码：{error.Code}", title, MessageBoxButton.OK, MessageBoxImage.Warning);

    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    /// <summary>写操作二次确认（见规划 4.5.6、6.5）：显示目标、地址、数值与风险。</summary>
    public bool ConfirmWrite(string target, byte function, string address, string value, bool broadcast)
    {
        var text = $"即将执行写操作：\n\n" +
                   $"目标设备：{target}\n" +
                   $"功能码：0x{function:X2} {Fn.Name(function)}\n" +
                   $"地址：{address}\n" +
                   $"数值：{value}\n\n" +
                   (broadcast
                       ? "注意：目标为广播地址 0，总线上所有设备都会收到该写请求。\n\n"
                       : "写操作会修改现场设备数据，请确认目标与数值无误。\n\n") +
                   "确认发送？";

        return MessageBox.Show(text, "写操作确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    public string? SaveFile(string title, string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultFileName,
            AddExtension = true,
            OverwritePrompt = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrEmpty(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public void ShowText(string title, string content)
    {
        var window = new Window
        {
            Title = title,
            Width = 900,
            Height = 600,
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new System.Windows.Controls.TextBox
            {
                Text = content,
                IsReadOnly = true,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 12,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                Padding = new Thickness(6)
            }
        };
        window.ShowDialog();
    }
}
