using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kudaki.App.Converters;

// null なら Collapsed、非null なら Visible。プロパティパネルを選択の有無で出し分けるのに使う。
public sealed class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
