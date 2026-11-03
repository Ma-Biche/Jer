using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Jer.Services;
using Jer.ViewModels;

namespace Jer;

/// <summary>
/// Sample content for screenshots and quick checks (run Jer.exe --demo).
/// Everything stays in memory: demo mode never saves tasks, the shelf, the water log or settings.
/// </summary>
internal static class DemoData
{
    public static void Apply(NotchViewModel vm, bool paused)
    {
        var duration = TimeSpan.FromSeconds(222);
        var position = TimeSpan.FromSeconds(80);
        vm.Music.Media.UseDemo("Golden Hour Drive", "Neon Harbor", "Coastline", duration, position, CreateArtwork());
        if (paused)
        {
            _ = vm.Music.Media.PlayPauseAsync();
        }

        vm.Music.UseDemoLyrics(new Lyrics(new[]
        {
            new LyricLine(TimeSpan.FromSeconds(62), "Windows down on the coast road"),
            new LyricLine(TimeSpan.FromSeconds(67), "Radio humming something old"),
            new LyricLine(TimeSpan.FromSeconds(72), "Every light turns gold"),
            new LyricLine(TimeSpan.FromSeconds(77), "We keep on driving slow"),
            new LyricLine(TimeSpan.FromSeconds(82), "Chasing the sun before it goes"),
            new LyricLine(TimeSpan.FromSeconds(87), "Hands in the summer air"),
            new LyricLine(TimeSpan.FromSeconds(92), "Nothing to fix, nowhere to be"),
            new LyricLine(TimeSpan.FromSeconds(97), "Just the road and you and me"),
            new LyricLine(TimeSpan.FromSeconds(103), "Golden hour, golden hour"),
            new LyricLine(TimeSpan.FromSeconds(108), "Stay a little longer"),
        }, synced: true, instrumental: false));

        var today = DateTime.Today;
        vm.Todo.AddDemo("Send the weekly report", today.AddHours(10).AddMinutes(30), 1, done: true);
        vm.Todo.AddDemo("Reply to Dina about Friday", today.AddHours(15), 0, done: true);
        vm.Todo.AddDemo("Call the dentist", today.AddDays(1).AddHours(9), 3, done: false);
        vm.Todo.AddDemo("Buy coffee beans", null, 2, done: false);
        vm.Todo.AddDemo("Water the plants", today.AddDays(1).AddHours(19), 5, done: false);

        vm.Water.AddDemoEntries();

        if (Environment.ProcessPath is { } exe)
        {
            vm.Shelf.AddPaths(new[] { exe, Path.GetDirectoryName(exe)! });
        }

        vm.Shelf.AddText("Launch checklist: icons, notes, screenshots");
        vm.Shelf.AddText("https://github.com/Ma-Biche/Jer");
    }

    /// <summary>A sunset-coloured square, drawn in code so the repo ships no third-party images.</summary>
    private static BitmapSource CreateArtwork()
    {
        const int size = 240;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var sky = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromRgb(255, 138, 76), 0),
                    new GradientStop(Color.FromRgb(255, 77, 109), 0.55),
                    new GradientStop(Color.FromRgb(108, 61, 214), 1),
                },
                new Point(0, 0),
                new Point(0, 1));
            dc.DrawRectangle(sky, null, new Rect(0, 0, size, size));
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(235, 255, 214, 120)), null, new Point(size * 0.5, size * 0.58), 46, 46);
            var sea = new SolidColorBrush(Color.FromArgb(220, 52, 28, 120));
            dc.DrawRectangle(sea, null, new Rect(0, size * 0.62, size, size * 0.38));
            for (var i = 0; i < 4; i++)
            {
                var y = size * 0.68 + i * 14;
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)(110 - i * 20), 255, 200, 150)), null,
                    new Rect(size * 0.5 - 40 + i * 6, y, 80 - i * 12, 3));
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
