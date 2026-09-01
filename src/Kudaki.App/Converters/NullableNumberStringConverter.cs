using System;
using System.Globalization;
using System.Windows.Data;

namespace Kudaki.App.Converters;

// double?/int? を TextBox の Text にバインドするための変換。
// 空文字/空白は null にする (WPF デフォルトだと入力エラー扱いで source が更新されない)。
public sealed class NullableNumberStringConverter : IValueConverter
{
    public static readonly NullableNumberStringConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            null => string.Empty,
            double d => d.ToString(culture),
            int i => i.ToString(culture),
            _ => value.ToString() ?? string.Empty
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = value as string;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlying == typeof(double))
        {
            return double.TryParse(text, NumberStyles.Float, culture, out var d) ? d : Binding.DoNothing;
        }
        if (underlying == typeof(int))
        {
            return int.TryParse(text, NumberStyles.Integer, culture, out var i) ? i : Binding.DoNothing;
        }
        return Binding.DoNothing;
    }
}
