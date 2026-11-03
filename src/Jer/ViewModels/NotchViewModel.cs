using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using Jer.Core;
using Jer.Models;
using Jer.Services;

namespace Jer.ViewModels;

public enum NotchPage
{
    Music,
    Pomodoro,
    Water,
    Shelf,
    Todo,
}

public enum MascotMood
{
    Hidden,
    Awake,
    Dozing,
    Waving,
}

public sealed class PageItem : ObservableObject
{
    public required NotchPage Page { get; init; }
    public required string Title { get; init; }
    public required Geometry Icon { get; init; }
    public bool IsSelected { get; set => SetProperty(ref field, value); }
}

/// <summary>State of the notch: clock, pages, overlays, alerts and the mascot.</summary>
public sealed class NotchViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _clock = new();
    private readonly DispatcherTimer _hudTimer = new() { Interval = TimeSpan.FromMilliseconds(1700) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    private readonly DispatcherTimer _alertTimer = new();
    private readonly DispatcherTimer _waveTimer = new() { Interval = TimeSpan.FromMilliseconds(1700) };
    private readonly Queue<AlertInfo> _alerts = new();

    public NotchViewModel(
        AppSettings settings,
        NotchEvents events,
        MusicViewModel music,
        PomodoroViewModel pomodoro,
        WaterViewModel water,
        ShelfViewModel shelf,
        TodoViewModel todo,
        Action openSettings)
    {
        _settings = settings;
        Music = music;
        Pomodoro = pomodoro;
        Water = water;
        Shelf = shelf;
        Todo = todo;

        SelectPageCommand = new RelayCommand(p =>
        {
            if (p is PageItem item)
            {
                SelectPage(item.Page);
            }
        });
        OpenSettingsCommand = new RelayCommand(openSettings);
        DismissAlertCommand = new RelayCommand(() => DismissAlert(runDismissed: true));

        events.HudRequested += ShowHud;
        events.AlertRequested += ShowAlert;

        _clock.Tick += (_, _) => UpdateClock();
        _hudTimer.Tick += (_, _) =>
        {
            _hudTimer.Stop();
            CurrentHud = null;
            RaiseVisualState();
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastText = "";
        };
        _alertTimer.Tick += (_, _) =>
        {
            _alertTimer.Stop();
            DismissAlert(runDismissed: true);
        };
        _waveTimer.Tick += (_, _) =>
        {
            _waveTimer.Stop();
            Mood = MascotMood.Hidden;
        };

        _settings.PropertyChanged += OnSettingsChanged;
        Music.Media.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MediaService.IsPlaying) or nameof(MediaService.HasSession) or nameof(MediaService.Artwork))
            {
                RaiseVisualState();
            }
        };
        Music.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MusicViewModel.ShowLyricPill) || e.PropertyName == nameof(MusicViewModel.LyricsEnabled))
            {
                RaiseVisualState();
            }
        };
        Pomodoro.StateChanged += (_, _) => RaiseVisualState();

        RebuildPages();
        if (Enum.TryParse<NotchPage>(_settings.LastPage, out var last) && Pages.Any(p => p.Page == last))
        {
            SelectPage(last);
        }
        else
        {
            SelectPage(Pages[0].Page);
        }

        UpdateClock();
    }

    /// <summary>Anything that changes the notch's size or mode.</summary>
    public event EventHandler? VisualStateChanged;

    /// <summary>The selected page changed (for the slide animation). Argument: +1 forward, -1 back.</summary>
    public event EventHandler<int>? PageChanged;

    public MusicViewModel Music { get; }
    public PomodoroViewModel Pomodoro { get; }
    public WaterViewModel Water { get; }
    public ShelfViewModel Shelf { get; }
    public TodoViewModel Todo { get; }
    public AppSettings Settings => _settings;

    public RelayCommand SelectPageCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand DismissAlertCommand { get; }

    public ObservableCollection<PageItem> Pages { get; } = new();
    public NotchPage SelectedPage { get; private set => SetProperty(ref field, value); }
    public string PageTitle { get; private set => SetProperty(ref field, value); } = "";

    public string TimeText { get; private set => SetProperty(ref field, value); } = "";
    public string DateText { get; private set => SetProperty(ref field, value); } = "";

    public HudInfo? CurrentHud { get; private set => SetProperty(ref field, value); }
    public string HudLevelText { get; private set => SetProperty(ref field, value); } = "";
    public AlertInfo? CurrentAlert { get; private set => SetProperty(ref field, value); }
    public string ToastText { get; private set => SetProperty(ref field, value); } = "";

    public MascotMood Mood
    {
        get;
        private set
        {
            if (SetProperty(ref field, value))
            {
                RaiseVisualState();
            }
        }
    }

    public bool HiddenForFullscreen
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RaiseVisualState();
            }
        }
    }

    /// <summary>Set by the window while the notch is open under the mouse.</summary>
    public bool IsExpanded
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                if (value)
                {
                    Mood = MascotMood.Hidden;
                }

                Music.ViewNeedsProgress = value && SelectedPage == NotchPage.Music;
            }
        }
    }

    public bool IsMusicActive => Music.Media.HasSession && Music.Media.IsPlaying;

    /// <summary>No keyboard or mouse input for a couple of minutes: nobody is watching the notch move.</summary>
    public bool IsUserAway { get; private set; }

    public void SelectPage(NotchPage page)
    {
        if (Pages.All(p => p.Page != page))
        {
            return;
        }

        var oldIndex = Pages.ToList().FindIndex(p => p.Page == SelectedPage);
        var newIndex = Pages.ToList().FindIndex(p => p.Page == page);
        var changed = SelectedPage != page;
        SelectedPage = page;
        foreach (var p in Pages)
        {
            p.IsSelected = p.Page == page;
        }

        PageTitle = Pages[newIndex].Title;
        _settings.LastPage = page.ToString();
        Music.ViewNeedsProgress = IsExpanded && page == NotchPage.Music;
        if (changed)
        {
            PageChanged?.Invoke(this, newIndex >= oldIndex ? 1 : -1);
            RaiseVisualState();
        }
    }

    public void NextPage(int direction)
    {
        if (Pages.Count < 2)
        {
            return;
        }

        var index = Pages.ToList().FindIndex(p => p.Page == SelectedPage);
        var next = (index + direction + Pages.Count) % Pages.Count;
        SelectPage(Pages[next].Page);
    }

    public bool HasPage(NotchPage page) => Pages.Any(p => p.Page == page);

    public void ShowHud(HudInfo hud)
    {
        if (HiddenForFullscreen)
        {
            return;
        }

        if (IsExpanded)
        {
            // The user is looking at the open notch; a quiet note in the header is enough.
            ToastText = hud.Detail is null ? hud.Title : $"{hud.Title} · {hud.Detail}";
            _toastTimer.Stop();
            _toastTimer.Start();
            return;
        }

        HudLevelText = hud.Level is { } level ? Math.Round(level * 100).ToString(CultureInfo.CurrentCulture) : "";
        CurrentHud = hud;
        _hudTimer.Stop();
        _hudTimer.Start();
        RaiseVisualState();
    }

    public void ShowAlert(AlertInfo alert)
    {
        foreach (var action in alert.Actions)
        {
            var a = action;
            a.Command = new RelayCommand(() =>
            {
                DismissAlert(runDismissed: false);
                try
                {
                    a.Run?.Invoke();
                }
                catch (Exception ex)
                {
                    Log.Error("Alert action failed", ex);
                }
            });
        }

        if (CurrentAlert is null)
        {
            Present(alert);
        }
        else
        {
            _alerts.Enqueue(alert);
        }
    }

    public void DismissAlert(bool runDismissed)
    {
        _alertTimer.Stop();
        var closing = CurrentAlert;
        CurrentAlert = null;
        if (runDismissed)
        {
            closing?.OnDismissed?.Invoke();
        }

        if (_alerts.Count > 0)
        {
            Present(_alerts.Dequeue());
        }
        else
        {
            RaiseVisualState();
        }
    }

    /// <summary>Called once a second with the time since the last keyboard/mouse input.</summary>
    public void UpdateIdle(TimeSpan idle)
    {
        var away = idle >= TimeSpan.FromMinutes(2);
        if (away != IsUserAway)
        {
            IsUserAway = away;
            RaiseVisualState(); // Pauses or resumes the equalizer bars.
        }

        var eligible = _settings.MascotEnabled && !_settings.MinimalNotch && !IsExpanded && !HiddenForFullscreen &&
                       CurrentAlert is null && CurrentHud is null && !IsMusicActive && !Pomodoro.IsRunning;
        var delay = TimeSpan.FromSeconds(_settings.MascotDelaySeconds);

        if (!eligible)
        {
            if (Mood is MascotMood.Awake or MascotMood.Dozing)
            {
                Mood = MascotMood.Hidden;
            }

            return;
        }

        switch (Mood)
        {
            case MascotMood.Hidden when idle >= delay:
                Mood = MascotMood.Awake;
                break;
            case MascotMood.Awake when idle >= delay + TimeSpan.FromMinutes(2.5):
                Mood = MascotMood.Dozing;
                break;
            case MascotMood.Awake or MascotMood.Dozing when idle < TimeSpan.FromSeconds(1.5):
                Mood = MascotMood.Waving; // Hello again!
                _waveTimer.Stop();
                _waveTimer.Start();
                break;
        }
    }

    public void RaiseVisualState() => VisualStateChanged?.Invoke(this, EventArgs.Empty);

    private void Present(AlertInfo alert)
    {
        CurrentAlert = alert;
        Mood = MascotMood.Hidden;
        if (alert.Timeout is { } timeout)
        {
            _alertTimer.Interval = timeout;
            _alertTimer.Start();
        }

        RaiseVisualState();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.MusicPage):
            case nameof(AppSettings.PomodoroPage):
            case nameof(AppSettings.WaterPage):
            case nameof(AppSettings.ShelfPage):
            case nameof(AppSettings.TodoPage):
                RebuildPages();
                if (!HasPage(SelectedPage))
                {
                    SelectPage(Pages[0].Page);
                }

                RaiseVisualState();
                break;
            case nameof(AppSettings.MinimalNotch):
            case nameof(AppSettings.MascotEnabled):
                if (!_settings.MascotEnabled || _settings.MinimalNotch)
                {
                    Mood = MascotMood.Hidden;
                }

                RaiseVisualState();
                break;
        }
    }

    private void RebuildPages()
    {
        var wanted = new List<PageItem>();
        void AddIf(bool enabled, NotchPage page, string title, string icon)
        {
            if (enabled)
            {
                wanted.Add(new PageItem { Page = page, Title = title, Icon = Ui.Icon(icon), IsSelected = page == SelectedPage });
            }
        }

        AddIf(_settings.MusicPage, NotchPage.Music, "Music", "Music");
        AddIf(_settings.PomodoroPage, NotchPage.Pomodoro, "Pomodoro", "Timer");
        AddIf(_settings.WaterPage, NotchPage.Water, "Water", "Water");
        AddIf(_settings.ShelfPage, NotchPage.Shelf, "Shelf", "Shelf");
        AddIf(_settings.TodoPage, NotchPage.Todo, "To-do", "Todo");
        if (wanted.Count == 0)
        {
            wanted.Add(new PageItem { Page = NotchPage.Music, Title = "Music", Icon = Ui.Icon("Music") });
        }

        Pages.Clear();
        foreach (var p in wanted)
        {
            Pages.Add(p);
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        TimeText = Ui.FormatTime(now);
        var monthDay = culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM");
        DateText = now.ToString("ddd", culture) + ", " + now.ToString(monthDay, culture);

        // Wake up exactly at the next minute instead of polling.
        _clock.Stop();
        _clock.Interval = TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(30);
        _clock.Start();
    }

    /// <summary>Re-reads the Windows date/time format after the user changes it.</summary>
    public void RefreshClock()
    {
        CultureInfo.CurrentCulture.ClearCachedData();
        UpdateClock();
    }
}
