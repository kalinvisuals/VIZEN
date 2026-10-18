using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VIZEN.App.Converters;

/// <summary>
/// true → Visible, false → Collapsed. Pass ConverterParameter="Invert" to reverse.
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (IsInvert(parameter))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    internal static bool IsInvert(object? parameter)
        => string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// null → Collapsed, non-null → Visible. Pass ConverterParameter="Invert" to reverse.
/// </summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (BoolToVisibilityConverter.IsInvert(parameter))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 0 → Visible (for empty states), otherwise Collapsed. Pass ConverterParameter="Invert" to reverse.
/// </summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isZero = value is int i && i == 0;
        if (BoolToVisibilityConverter.IsInvert(parameter))
        {
            isZero = !isZero;
        }

        return isZero ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>double ≥ ConverterParameter → true (e.g. only show a toolbar when there is room for it).</summary>
public sealed class AtLeastConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d && double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var min) && d >= min;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Scrollbar thumb with a minimum visible length. WPF shrinks the thumb of long content to about 8 px (half a system
/// button), which on a 4 px-wide bar looks like no scrollbar at all. Bound to Track.ViewportSize with the bar's
/// ViewportSize, Minimum, Maximum and the track length; ConverterParameter = minimum thumb length in pixels. Only the
/// thumb's size changes: the scroll range and position are the same.
/// </summary>
public sealed class MinThumbViewportConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [double viewport, double minimum, double maximum, double trackLength]
            ? MinThumbViewport(viewport, minimum, maximum, trackLength, double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var min) ? min : 32)
            : values.Length > 0 ? values[0] : DependencyProperty.UnsetValue;

    /// <summary>The viewport size that makes the thumb at least <paramref name="minThumb"/> long.</summary>
    public static double MinThumbViewport(double viewport, double minimum, double maximum, double trackLength, double minThumb)
    {
        var range = maximum - minimum;
        if (double.IsNaN(viewport) || viewport <= 0 || range <= 0 || trackLength <= minThumb * 2)
        {
            return viewport;
        }

        var thumb = trackLength * viewport / (range + viewport);
        return thumb >= minThumb ? viewport : minThumb * range / (trackLength - minThumb);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>true when the bound enum value's name equals ConverterParameter (e.g. the selected Smart Draw tool).</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Enum e && parameter is string name && string.Equals(e.ToString(), name, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>An ItemsControl alternation index (0, 1, …) as a channel label: "01", "02", …</summary>
public sealed class ChannelNumberConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int index ? (index + 1).ToString("00", CultureInfo.InvariantCulture) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
