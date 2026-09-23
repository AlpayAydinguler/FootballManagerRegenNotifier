using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using FootballManagerRegenNotifier.Core.Model;
using FootballManagerRegenNotifier.Core.Parsing;
using FootballManagerRegenNotifier.Core.Settings;

namespace FootballManagerRegenNotifier.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not true;
}

/// <summary>
/// Colours a dataset row by how much the data behind it can be trusted.
/// </summary>
/// <remarks>
/// The shipped intake dates are FM24-era and partly guessed, and a row the user
/// cannot tell apart from a verified one is worse than no row: the first wrong
/// alert costs all the trust the tool has. Showing confidence makes the
/// uncertainty visible instead of hiding it behind a tidy table.
/// </remarks>
public sealed class ConfidenceBrushConverter : IValueConverter
{
    private static readonly Brush High = new SolidColorBrush(Color.FromRgb(0x7C, 0x88, 0x99));
    private static readonly Brush Medium = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
    private static readonly Brush Low = new SolidColorBrush(Color.FromRgb(0xE5, 0x6B, 0x6B));

    static ConfidenceBrushConverter()
    {
        High.Freeze();
        Medium.Freeze();
        Low.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DataConfidence.High => High,
            DataConfidence.Low => Low,
            _ => Medium,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Turns enum values into the wording used in the UI.</summary>
public sealed class EnumDescriptionConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            InvertMode.Auto => "Auto (detect)",
            InvertMode.Never => "Never invert",
            InvertMode.Always => "Always invert",
            DateOrder.DayFirst => "Day first (31/12/2026)",
            DateOrder.MonthFirst => "Month first (12/31/2026)",
            DateOrder.YearFirst => "Year first (2026/12/31)",
            _ => value?.ToString() ?? string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
