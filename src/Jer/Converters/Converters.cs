using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Jer.Core;

namespace Jer.Converters;

/// <summary>true → Visible, false → Collapsed. Parameter "invert" flips it.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Visible;
}

/// <summary>Visible when value.ToString() equals the parameter.</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Collapsed when the value is null or an empty string.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = value is null || (value is string s && s.Length == 0);
        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            empty = !empty;
        }

        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Progress (0..1) → StrokeDashArray for a ring drawn with an Ellipse.
/// Parameter = "diameter,thickness" of the ellipse.
/// </summary>
public sealed class ProgressToDashConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var progress = value is double d ? MathEx.Clamp(d, 0, 1) : 0;
        var parts = (parameter as string ?? "100,8").Split(',');
        var diameter = double.Parse(parts[0], CultureInfo.InvariantCulture);
        var thickness = double.Parse(parts[1], CultureInfo.InvariantCulture);
        var circumference = Math.PI * (diameter - thickness) / thickness; // in stroke-thickness units
        return new DoubleCollection { progress * circumference, circumference + 1 };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Color → SolidColorBrush.</summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not Color color)
        {
            return Brushes.White;
        }

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Number → number × parameter (used to size bars and map tiles).</summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var v = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        var factor = double.Parse(parameter as string ?? "1", CultureInfo.InvariantCulture);
        return v * factor;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
