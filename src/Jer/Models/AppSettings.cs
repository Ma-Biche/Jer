using System;
using Jer.Core;

namespace Jer.Models;

/// <summary>Everything the user can change. Saved to settings.json; every change applies instantly.</summary>
public sealed class AppSettings : ObservableObject
{
    /// <summary>Value of <see cref="DisplayTarget"/> that means "follow the main (primary) display".</summary>
    public const string PrimaryDisplay = "primary";

    // General
    public bool StartWithWindows { get; set => SetProperty(ref field, value); } = true;
    public bool HideInFullscreen { get; set => SetProperty(ref field, value); } = true;
    public bool MascotEnabled { get; set => SetProperty(ref field, value); } = true;
    public int MascotDelaySeconds { get; set => SetProperty(ref field, value); } = 45;
    public bool MinimalNotch { get; set => SetProperty(ref field, value); }

    // Display
    public string DisplayTarget { get; set => SetProperty(ref field, value); } = PrimaryDisplay;
    public string? DisplayTargetName { get; set => SetProperty(ref field, value); }
    public double NotchScale { get; set => SetProperty(ref field, value); } = 1.0;
    public bool AutoScaleWideDisplays { get; set => SetProperty(ref field, value); } = true;

    // Pages
    public bool MusicPage { get; set => SetProperty(ref field, value); } = true;
    public bool PomodoroPage { get; set => SetProperty(ref field, value); } = true;
    public bool WaterPage { get; set => SetProperty(ref field, value); } = true;
    public bool ShelfPage { get; set => SetProperty(ref field, value); } = true;
    public bool TodoPage { get; set => SetProperty(ref field, value); } = true;
    public string LastPage { get; set => SetProperty(ref field, value); } = "Music";

    // Music
    public bool LyricsEnabled { get; set => SetProperty(ref field, value); }

    // Pomodoro
    public int FocusMinutes { get; set => SetProperty(ref field, value); } = 25;
    public int ShortBreakMinutes { get; set => SetProperty(ref field, value); } = 5;
    public int LongBreakMinutes { get; set => SetProperty(ref field, value); } = 15;
    public int LongBreakEvery { get; set => SetProperty(ref field, value); } = 4;
    public bool PomodoroSound { get; set => SetProperty(ref field, value); } = true;
    public bool AutoStartNextSession { get; set => SetProperty(ref field, value); }

    // Water
    public bool WaterReminders { get; set => SetProperty(ref field, value); } = true;
    public int WaterGoalMl { get; set => SetProperty(ref field, value); } = 2000;
    public int WaterCupMl { get; set => SetProperty(ref field, value); } = 250;
    public int WaterIntervalMinutes { get; set => SetProperty(ref field, value); } = 60;
    public int WaterStartHour { get; set => SetProperty(ref field, value); } = 8;
    public int WaterEndHour { get; set => SetProperty(ref field, value); } = 21;

    // Alerts (HUD)
    public bool VolumeHud { get; set => SetProperty(ref field, value); } = true;
    public bool CapsLockHud { get; set => SetProperty(ref field, value); } = true;
    public bool KeyboardLayoutHud { get; set => SetProperty(ref field, value); } = true;
    public bool BatteryHud { get; set => SetProperty(ref field, value); } = true;
    public bool NetworkHud { get; set => SetProperty(ref field, value); } = true;

    // Internal
    public bool FirstRunDone { get; set => SetProperty(ref field, value); }

    /// <summary>Clamps values that may have been hand-edited in settings.json.</summary>
    public void Normalize()
    {
        MascotDelaySeconds = Math.Clamp(MascotDelaySeconds, 10, 600);
        NotchScale = Math.Round(Math.Clamp(NotchScale, 0.8, 1.4), 2);
        if (string.IsNullOrWhiteSpace(DisplayTarget))
        {
            DisplayTarget = PrimaryDisplay;
        }

        if (!MusicPage && !PomodoroPage && !WaterPage && !ShelfPage && !TodoPage)
        {
            MusicPage = true;
        }

        FocusMinutes = Math.Clamp(FocusMinutes, 1, 180);
        ShortBreakMinutes = Math.Clamp(ShortBreakMinutes, 1, 60);
        LongBreakMinutes = Math.Clamp(LongBreakMinutes, 1, 120);
        LongBreakEvery = Math.Clamp(LongBreakEvery, 2, 12);
        WaterGoalMl = Math.Clamp(WaterGoalMl, 250, 8000);
        WaterCupMl = Math.Clamp(WaterCupMl, 50, 2000);
        WaterIntervalMinutes = Math.Clamp(WaterIntervalMinutes, 10, 360);
        WaterStartHour = Math.Clamp(WaterStartHour, 0, 23);
        WaterEndHour = Math.Clamp(WaterEndHour, 0, 23);
        if (WaterEndHour <= WaterStartHour)
        {
            WaterEndHour = Math.Min(23, WaterStartHour + 1);
        }
    }
}
