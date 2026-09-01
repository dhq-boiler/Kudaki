using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Kudaki.Installer.Converters;

// enum の値と ConverterParameter (enum メンバー名 or 値) が一致すれば Visible、それ以外 Collapsed。
// 4 ページを Grid の可視性で切り替えるのに使う。
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return Visibility.Collapsed;
        return string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
