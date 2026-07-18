using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using D4LootBench.Core.Validation;

namespace D4LootBench.App.Converters;

/// <summary>Maps <see cref="ValidationSeverity"/> to a glyph for inline display.</summary>
public sealed class ValidationSeverityGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ValidationSeverity s
            ? s switch
            {
                ValidationSeverity.Error   => "✖",  // ✖
                ValidationSeverity.Warning => "⚠",  // ⚠
                ValidationSeverity.Info    => "ℹ",  // ℹ
                _                          => "•",  // •
            }
            : "•";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Maps <see cref="ValidationSeverity"/> to a brush.</summary>
public sealed class ValidationSeverityBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush ErrorBrush   = new(Color.FromRgb(0xD0, 0x33, 0x33));
    private static readonly SolidColorBrush WarningBrush = new(Color.FromRgb(0xC8, 0x8A, 0x00));
    private static readonly SolidColorBrush InfoBrush    = new(Color.FromRgb(0x2A, 0x6F, 0xB8));
    private static readonly SolidColorBrush DefaultBrush = new(Color.FromRgb(0x55, 0x55, 0x55));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ValidationSeverity s
            ? s switch
            {
                ValidationSeverity.Error   => ErrorBrush,
                ValidationSeverity.Warning => WarningBrush,
                ValidationSeverity.Info    => InfoBrush,
                _                          => DefaultBrush,
            }
            : DefaultBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
