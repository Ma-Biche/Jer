using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Jer.ViewModels;

/// <summary>Shared look-ups for view models: icons from Themes/Icons.xaml and the palette.</summary>
public static class Ui
{
    public static Geometry Icon(string name) =>
        Application.Current?.TryFindResource("Icon." + name) as Geometry ?? Geometry.Empty;

    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.White;

    public static Brush Accent => Brush("Jer.Accent");
    public static Brush Tomato => Brush("Jer.Tomato");
    public static Brush Water => Brush("Jer.Water");
    public static Brush Green => Brush("Jer.Green");
    public static Brush Orange => Brush("Jer.Orange");
    public static Brush Red => Brush("Jer.Red");
    public static Brush White => Brushes.White;

    /// <summary>Task colours for the to-do list.</summary>
    public static readonly Brush[] TaskColors = CreateTaskColors();

    /// <summary>"3:07", or "1:02:03" for long media.</summary>
    public static string FormatClock(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }

        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    /// <summary>Short time in the user's Windows format (12 or 24 hour).</summary>
    public static string FormatTime(DateTime time) =>
        time.ToString(CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern, CultureInfo.CurrentCulture);

    public static string FormatLitres(int ml) => (ml / 1000.0).ToString(ml % 100 == 0 ? "0.0" : "0.00", CultureInfo.CurrentCulture) + " L";

    private static Brush[] CreateTaskColors()
    {
        var colors = new[] { "#8B7BFF", "#4FB3FF", "#34D399", "#FFB020", "#FF6B57", "#F472B6" };
        var brushes = new Brush[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
            b.Freeze();
            brushes[i] = b;
        }

        return brushes;
    }
}
