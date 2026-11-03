using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Jer.Core;
using Jer.Interop;
using Jer.Models;

namespace Jer.Services;

/// <summary>A rectangle in physical pixels (virtual-screen coordinates).</summary>
public readonly record struct PixelRect(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

/// <summary>One connected display.</summary>
public sealed class DisplayInfo
{
    public required IntPtr Handle { get; init; }

    /// <summary>GDI device name, for example \\.\DISPLAY2.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Stable id used in settings: the monitor device path when Windows provides one.</summary>
    public required string Key { get; init; }

    public required string FriendlyName { get; init; }
    public required int Number { get; init; }
    public required bool IsPrimary { get; init; }
    public required PixelRect Bounds { get; init; }
    public required PixelRect WorkArea { get; init; }
    public required uint Dpi { get; init; }

    public double Scale => Dpi / 96.0;

    public string Title => IsPrimary ? $"Display {Number} (main)" : $"Display {Number}";

    public string Description =>
        $"{FriendlyName} · {Bounds.Width} × {Bounds.Height} · {Math.Round(Scale * 100)}%";

    public bool SameAs(DisplayInfo other) =>
        Key == other.Key && Bounds == other.Bounds && Dpi == other.Dpi && IsPrimary == other.IsPrimary;
}

/// <summary>Lists the connected displays and resolves the one the notch should live on.</summary>
public sealed class MonitorService
{
    public IReadOnlyList<DisplayInfo> Displays { get; private set; } = Array.Empty<DisplayInfo>();

    /// <summary>Raised (on the UI thread) when displays are added, removed, moved or rescaled.</summary>
    public event EventHandler? DisplaysChanged;

    public DisplayInfo Primary => Displays.FirstOrDefault(d => d.IsPrimary) ?? Displays.First();

    public void Refresh()
    {
        List<DisplayInfo> next;
        try
        {
            next = Enumerate();
        }
        catch (Exception ex)
        {
            Log.Error("Could not enumerate displays", ex);
            next = new List<DisplayInfo>();
        }

        if (next.Count == 0)
        {
            next.Add(Fallback());
        }

        var changed = next.Count != Displays.Count || next.Where((d, i) => !d.SameAs(Displays[i])).Any();
        Displays = next;
        if (changed)
        {
            DisplaysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public DisplayInfo? Find(string key) =>
        Displays.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase))
        ?? Displays.FirstOrDefault(d => string.Equals(d.DeviceName, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The display for a settings value. Falls back to the main display when the chosen one is unplugged.</summary>
    public DisplayInfo Resolve(string target, out bool fellBack)
    {
        fellBack = false;
        if (Displays.Count == 0)
        {
            Refresh();
        }

        if (string.Equals(target, AppSettings.PrimaryDisplay, StringComparison.OrdinalIgnoreCase))
        {
            return Primary;
        }

        var match = Find(target);
        if (match is not null)
        {
            return match;
        }

        fellBack = true;
        return Primary;
    }

    private static List<DisplayInfo> Enumerate()
    {
        var names = DisplayConfig.GetMonitorNames();
        var result = new List<DisplayInfo>();
        var index = 0;

        bool Callback(IntPtr monitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data)
        {
            index++;
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(), szDevice = "" };
            if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                return true;
            }

            var device = info.szDevice ?? $"DISPLAY{index}";
            names.TryGetValue(device, out var name);
            result.Add(new DisplayInfo
            {
                Handle = monitor,
                DeviceName = device,
                Key = string.IsNullOrWhiteSpace(name?.DevicePath) ? device : name!.DevicePath,
                FriendlyName = name?.FriendlyName ?? "Display",
                Number = ParseNumber(device, index),
                IsPrimary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0,
                Bounds = ToPixelRect(info.rcMonitor),
                WorkArea = ToPixelRect(info.rcWork),
                Dpi = NativeMethods.GetMonitorDpi(monitor),
            });
            return true;
        }

        NativeMethods.MonitorEnumProc proc = Callback;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);

        return result.OrderBy(d => d.Number).ThenBy(d => d.Bounds.Left).ToList();
    }

    private static DisplayInfo Fallback()
    {
        var monitor = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(), NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>(), szDevice = "" };
        NativeMethods.GetMonitorInfo(monitor, ref info);
        var bounds = info.rcMonitor.Width > 0 ? ToPixelRect(info.rcMonitor) : new PixelRect(0, 0, 1920, 1080);
        return new DisplayInfo
        {
            Handle = monitor,
            DeviceName = info.szDevice ?? "DISPLAY1",
            Key = info.szDevice ?? "DISPLAY1",
            FriendlyName = "Display",
            Number = 1,
            IsPrimary = true,
            Bounds = bounds,
            WorkArea = info.rcWork.Width > 0 ? ToPixelRect(info.rcWork) : bounds,
            Dpi = NativeMethods.GetMonitorDpi(monitor),
        };
    }

    private static PixelRect ToPixelRect(NativeMethods.RECT r) => new(r.Left, r.Top, r.Width, r.Height);

    private static int ParseNumber(string deviceName, int fallback)
    {
        var digits = new string(deviceName.Reverse().TakeWhile(char.IsDigit).Reverse().ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : fallback;
    }
}
