using System;
using System.Globalization;
using System.Windows.Data;

namespace Kudaki.App.Converters;

// DatePicker.SelectedDate (DateTime?) と Model の DateOnly? を橋渡し。
public sealed class DateOnlyToDateTimeConverter : IValueConverter
{
    public static readonly DateOnlyToDateTimeConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateOnly d)
        {
            return d.ToDateTime(TimeOnly.MinValue);
        }
        return null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DateTime dt)
        {
            return DateOnly.FromDateTime(dt);
        }
        return null;
    }
}
