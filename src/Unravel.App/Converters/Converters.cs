using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Unravel.Core.Diagnostics;
using Unravel.Core.Summary;

namespace Unravel.App.Converters;

/// <summary>Severity to the theme's traffic-light brush. Null (not checked) is muted.</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current.FindResource(value switch
        {
            Severity.Ok => "Success",
            Severity.Info => "Accent",
            Severity.Warning => "Warning",
            Severity.Critical => "Danger",
            _ => "Muted",
        });

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Severity to an icon glyph (Segoe Fluent Icons), so status is never shown by colour alone.</summary>
public sealed class SeverityToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Severity.Ok => "",       // check mark
        Severity.Info => "",     // info
        Severity.Warning => "",  // warning
        Severity.Critical => "", // error badge
        _ => "",                 // unknown: not checked yet
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Loading and failed tiles are muted; ready tiles use the normal text colour.</summary>
public sealed class TileStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current.FindResource(value is TileState.Ready ? "Text" : "Muted");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True to Visible. Pass "invert" as the parameter to flip it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapses an element when its value is null or an empty string. Pass "invert" to show it only then.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value is null || value is string { Length: 0 };
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase))
        {
            empty = !empty;
        }

        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Upper-cases a label for the small caps headings.</summary>
public sealed class UpperConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string)?.ToUpper(culture);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Two-way: true when the bound enum equals the parameter (sidebar navigation RadioButtons).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter as string, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

/// <summary>Visible when the bound enum equals the parameter (which page is shown).</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && string.Equals(value.ToString(), parameter as string, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Bytes to "1.2 GB" / "350 MB" for the software list.</summary>
public sealed class SizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ulong b when b >= 1024UL * 1024 * 1024 => $"{b / (1024d * 1024 * 1024):0.0} GB",
        ulong b when b >= 1024UL * 1024 => $"{b / (1024d * 1024):0} MB",
        ulong b => $"{Math.Max(1, b / 1024)} KB",
        _ => "",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>DateOnly to a short local date.</summary>
public sealed class DateOnlyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateOnly d ? d.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Inverts a bool (enable an option while a test is not running).</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
