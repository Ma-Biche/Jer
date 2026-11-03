using System;
using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Threading;
using Jer.Core;
using Jer.Models;
using Jer.Services;

namespace Jer.ViewModels;

public enum PomodoroMode
{
    Focus,
    ShortBreak,
    LongBreak,
}

/// <summary>Focus timer: work in focus sessions, with short breaks and a longer one every few sessions.</summary>
public sealed class PomodoroViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly SoundService _sound;
    private readonly NotchEvents _events;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _endsAt;

    public PomodoroViewModel(AppSettings settings, SoundService sound, NotchEvents events)
    {
        _settings = settings;
        _sound = sound;
        _events = events;
        _timer.Tick += (_, _) => OnTick();
        _settings.PropertyChanged += OnSettingsChanged;

        StartPauseCommand = new RelayCommand(StartPause);
        ResetCommand = new RelayCommand(Reset);
        SkipCommand = new RelayCommand(() => Complete(skipped: true));

        Remaining = Total = DurationOf(Mode);
        UpdateTexts();
    }

    public event EventHandler? StateChanged;

    public RelayCommand StartPauseCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand SkipCommand { get; }

    public PomodoroMode Mode { get; private set => SetProperty(ref field, value); } = PomodoroMode.Focus;
    public bool IsRunning { get; private set => SetProperty(ref field, value); }
    public TimeSpan Remaining { get; private set => SetProperty(ref field, value); }
    public TimeSpan Total { get; private set => SetProperty(ref field, value); }
    public double Progress { get; private set => SetProperty(ref field, value); }
    public string RemainingText { get; private set => SetProperty(ref field, value); } = "25:00";
    public string ModeTitle { get; private set => SetProperty(ref field, value); } = "Focus time";
    public string ModeShort { get; private set => SetProperty(ref field, value); } = "Focus";
    public string SessionText { get; private set => SetProperty(ref field, value); } = "";
    public string NextText { get; private set => SetProperty(ref field, value); } = "";
    public string StartLabel { get; private set => SetProperty(ref field, value); } = "Start";
    public Brush ModeBrush { get; private set => SetProperty(ref field, value); } = Ui.Tomato;

    /// <summary>Finished focus sessions in the current cycle (0 .. LongBreakEvery).</summary>
    public int CompletedInCycle { get; private set => SetProperty(ref field, value); }

    public int CycleLength => _settings.LongBreakEvery;

    /// <summary>True while a session is counting down or paused part-way.</summary>
    public bool IsActive => IsRunning || Remaining < Total;

    public void StartPause()
    {
        if (IsRunning)
        {
            Remaining = _endsAt - DateTime.Now;
            IsRunning = false;
            _timer.Stop();
        }
        else
        {
            if (Remaining <= TimeSpan.Zero)
            {
                Remaining = Total;
            }

            _endsAt = DateTime.Now + Remaining;
            IsRunning = true;
            ScheduleTick();
        }

        UpdateTexts();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        _timer.Stop();
        IsRunning = false;
        Remaining = Total = DurationOf(Mode);
        UpdateTexts();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTick()
    {
        Remaining = _endsAt - DateTime.Now;
        if (Remaining <= TimeSpan.Zero)
        {
            Complete(skipped: false);
            return;
        }

        UpdateTexts();
        ScheduleTick();
    }

    /// <summary>
    /// Wakes up just after the displayed second changes: one tick per second, and the countdown
    /// never skips or repeats a number the way a plain one-second timer drifts into doing.
    /// </summary>
    private void ScheduleTick()
    {
        var remaining = _endsAt - DateTime.Now;
        var untilNextSecond = remaining.Ticks % TimeSpan.TicksPerSecond;
        if (untilNextSecond <= 0)
        {
            untilNextSecond += TimeSpan.TicksPerSecond;
        }

        _timer.Stop();
        _timer.Interval = TimeSpan.FromTicks(untilNextSecond) + TimeSpan.FromMilliseconds(15);
        _timer.Start();
    }

    private void Complete(bool skipped)
    {
        _timer.Stop();
        IsRunning = false;
        var finished = Mode;

        if (finished == PomodoroMode.Focus)
        {
            CompletedInCycle = Math.Min(CycleLength, CompletedInCycle + 1);
            Mode = CompletedInCycle >= CycleLength ? PomodoroMode.LongBreak : PomodoroMode.ShortBreak;
        }
        else
        {
            if (finished == PomodoroMode.LongBreak)
            {
                CompletedInCycle = 0;
            }

            Mode = PomodoroMode.Focus;
        }

        Remaining = Total = DurationOf(Mode);
        UpdateTexts();

        if (!skipped)
        {
            if (_settings.PomodoroSound)
            {
                _sound.PlayChime();
            }

            var next = Mode == PomodoroMode.Focus ? "focus session" : Mode == PomodoroMode.LongBreak ? "long break" : "short break";
            _events.ShowAlert(new AlertInfo
            {
                Icon = Ui.Icon("Timer"),
                Accent = finished == PomodoroMode.Focus ? Ui.Tomato : Ui.Green,
                Title = finished == PomodoroMode.Focus ? "Focus session done" : "Break's over",
                Message = $"Up next: a {(int)Total.TotalMinutes}-minute {next}.",
                Timeout = _settings.AutoStartNextSession ? TimeSpan.FromSeconds(8) : null,
                Actions = new[]
                {
                    new AlertAction(Mode == PomodoroMode.Focus ? "Start focus" : "Start break", () => { if (!IsRunning) StartPause(); }, isPrimary: true),
                    new AlertAction("Later"),
                },
            });

            if (_settings.AutoStartNextSession)
            {
                StartPause();
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private TimeSpan DurationOf(PomodoroMode mode) => TimeSpan.FromMinutes(mode switch
    {
        PomodoroMode.ShortBreak => _settings.ShortBreakMinutes,
        PomodoroMode.LongBreak => _settings.LongBreakMinutes,
        _ => _settings.FocusMinutes,
    });

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.FocusMinutes) or nameof(AppSettings.ShortBreakMinutes) or nameof(AppSettings.LongBreakMinutes))
        {
            // A session that hasn't started yet picks up the new length right away.
            if (!IsRunning && Remaining == Total)
            {
                Remaining = Total = DurationOf(Mode);
            }

            UpdateTexts();
        }
        else if (e.PropertyName == nameof(AppSettings.LongBreakEvery))
        {
            CompletedInCycle = Math.Min(CompletedInCycle, CycleLength);
            OnPropertyChanged(nameof(CycleLength));
            UpdateTexts();
        }
    }

    private void UpdateTexts()
    {
        var remaining = Remaining < TimeSpan.Zero ? TimeSpan.Zero : Remaining;
        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        RemainingText = $"{seconds / 60:00}:{seconds % 60:00}";
        Progress = Total.TotalSeconds <= 0 ? 0 : 1 - remaining.TotalSeconds / Total.TotalSeconds;
        ModeTitle = Mode switch { PomodoroMode.ShortBreak => "Short break", PomodoroMode.LongBreak => "Long break", _ => "Focus time" };
        ModeShort = Mode switch { PomodoroMode.ShortBreak => "Break", PomodoroMode.LongBreak => "Long break", _ => "Focus" };
        ModeBrush = Mode == PomodoroMode.Focus ? Ui.Tomato : Ui.Green;
        StartLabel = IsRunning ? "Pause" : (remaining < Total ? "Resume" : "Start");
        var session = Math.Min(CompletedInCycle + (Mode == PomodoroMode.Focus ? 1 : 0), CycleLength);
        SessionText = Mode == PomodoroMode.Focus
            ? $"Session {Math.Max(1, session)} of {CycleLength}"
            : $"{CompletedInCycle} of {CycleLength} sessions done";
        var nextIsLong = Mode == PomodoroMode.Focus && CompletedInCycle + 1 >= CycleLength;
        NextText = Mode == PomodoroMode.Focus
            ? (nextIsLong ? $"Next: {_settings.LongBreakMinutes}-min long break" : $"Next: {_settings.ShortBreakMinutes}-min break")
            : $"Next: {_settings.FocusMinutes}-min focus";
        OnPropertyChanged(nameof(IsActive));
    }
}
