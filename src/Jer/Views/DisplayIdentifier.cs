using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Jer.Interop;
using Jer.Services;

namespace Jer.Views;

/// <summary>Shows each display's number in the middle of its screen for a moment (like Windows' "Identify").</summary>
public static class DisplayIdentifier
{
    public static void Show(MonitorService monitors)
    {
        monitors.Refresh();
        foreach (var display in monitors.Displays)
        {
            ShowOn(display);
        }
    }

    private static void ShowOn(DisplayInfo display)
    {
        const double size = 180;
        var content = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(28),
            Background = new SolidColorBrush(Color.FromArgb(235, 0, 0, 0)),
            Child = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = display.Number.ToString(),
                        FontSize = 84,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = display.IsPrimary ? "Main display" : display.FriendlyName,
                        FontSize = 13,
                        Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = size - 24,
                    },
                },
            },
        };

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            ResizeMode = ResizeMode.NoResize,
            Width = size,
            Height = size,
            Content = content,
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        };

        void Place()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var px = (int)Math.Round(size * display.Scale);
            var x = display.Bounds.Left + (display.Bounds.Width - px) / 2;
            var y = display.Bounds.Top + (display.Bounds.Height - px) / 2;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, px, px, NativeMethods.SWP_NOACTIVATE);
        }

        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            NativeMethods.SetExStyle(hwnd, NativeMethods.GetExStyle(hwnd) | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
            Place();
        };
        window.DpiChanged += (_, _) => window.Dispatcher.BeginInvoke(Place, DispatcherPriority.Background);
        window.Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            window.Close();
        };
        timer.Start();
    }
}
