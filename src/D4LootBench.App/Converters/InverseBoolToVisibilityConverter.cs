using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace D4LootBench.App.Converters;

/// <summary>Visible when false — the complement of <see cref="BooleanToVisibilityConverter"/>.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
