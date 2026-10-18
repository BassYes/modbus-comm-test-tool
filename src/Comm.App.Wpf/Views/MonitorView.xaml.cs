using System.Windows.Controls;
using Comm.App.Wpf.Services;
using Comm.App.Wpf.ViewModels;

namespace Comm.App.Wpf.Views;

public partial class MonitorView : UserControl
{
    private MonitorService? _monitor;

    public MonitorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        Detach();
        if (e.NewValue is MonitorViewModel viewModel)
        {
            _monitor = viewModel.Monitor;
            _monitor.RowsChanged += OnRowsChanged;
        }
    }

    private void Detach()
    {
        if (_monitor is not null) _monitor.RowsChanged -= OnRowsChanged;
        _monitor = null;
    }

    /// <summary>自动滚动：只滚动到最新一行，不逐条操作集合。</summary>
    private void OnRowsChanged(object? sender, EventArgs e)
    {
        if (_monitor is null || !_monitor.AutoScroll || _monitor.DisplayPaused) return;
        if (_monitor.Rows.Count == 0) return;

        var last = _monitor.Rows[^1];
        if (FramesGrid.Items.Count > 0 && !ReferenceEquals(FramesGrid.Items[FramesGrid.Items.Count - 1], last))
        {
            FramesGrid.ScrollIntoView(last);
        }
        else
        {
            FramesGrid.ScrollIntoView(last);
        }
    }
}
