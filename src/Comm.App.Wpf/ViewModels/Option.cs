namespace Comm.App.Wpf.ViewModels;

/// <summary>下拉选项（值 + 中文显示）。</summary>
public sealed record Option<T>(T Value, string Display)
{
    public override string ToString() => Display;
}
