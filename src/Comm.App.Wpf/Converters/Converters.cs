using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Comm.Core.Frames;
using Comm.Diagnostics;

namespace Comm.App.Wpf.Converters;

/// <summary>收发方向颜色（TX 蓝 / RX 绿 / 系统灰），配合 TX/RX 文字标记使用。</summary>
public sealed class DirectionToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FrameDirection direction) return Brushes.Black;
        return direction switch
        {
            FrameDirection.Tx => new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x9B)),
            FrameDirection.Rx => new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34)),
            _ => new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61))
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>错误报文使用高对比警示色。</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool isError && isError) return new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>TX/RX 方向着色，可通过第二个绑定（设置开关）关闭；关闭后仍保留 TX/RX 文字与图标。</summary>
public sealed class DirectionBrushToggleConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[1] is not true) return Brushes.Black;
        if (values[0] is not FrameDirection direction) return Brushes.Black;
        return direction switch
        {
            FrameDirection.Tx => new SolidColorBrush(Color.FromRgb(0x1B, 0x5E, 0x9B)),
            FrameDirection.Rx => new SolidColorBrush(Color.FromRgb(0x1E, 0x7B, 0x34)),
            _ => new SolidColorBrush(Color.FromRgb(0x61, 0x61, 0x61))
        };
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>日志开关状态颜色：开启为醒目的橙色（全局状态栏常显）。</summary>
public sealed class LogStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true
            ? new SolidColorBrush(Color.FromRgb(0xE6, 0x51, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x75, 0x75, 0x75));

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
