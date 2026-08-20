using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Madi.GUI.Presentation;

namespace Madi.GUI;

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is false ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class NavigationIndentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        new Thickness(value is int indent ? indent * 18 : 0, 0, 0, 0);

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class HeaderWeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class TimelineBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var color = value is TimelineItemKind kind ? kind switch
        {
            TimelineItemKind.User => ColorHelper.FromArgb(255, 31, 86, 145),
            TimelineItemKind.Error => ColorHelper.FromArgb(70, 196, 43, 28),
            TimelineItemKind.Warning => ColorHelper.FromArgb(60, 255, 185, 0),
            TimelineItemKind.Tool => ColorHelper.FromArgb(45, 0, 120, 212),
            TimelineItemKind.Subagent => ColorHelper.FromArgb(45, 136, 23, 152),
            _ => ColorHelper.FromArgb(30, 128, 128, 128),
        } : Colors.Transparent;
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class TimelineAlignmentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is TimelineItemKind.User ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
