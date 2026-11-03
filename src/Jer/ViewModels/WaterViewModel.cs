using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using Jer.Core;
using Jer.Models;
using Jer.Services;

namespace Jer.ViewModels;

public sealed class WaterBar : ObservableObject
{
    public required int Hour { get; init; }
    public required string Label { get; init; }
    public int Ml { get; set => SetProperty(ref field, value); }

    /// <summary>0..1 of the chart height.</summary>
    public double Height { get; set => SetProperty(ref field, value); }

    public bool IsCurrent { get; set => SetProperty(ref field, value); }
}

/// <summary>Daily water goal, a log of drinks and gentle reminders during the user's hours.</summary>
public sealed class WaterViewModel : ObservableObject
{
    private const string FileName = "water.json";
    private readonly AppSettings _settings;
    private readonly SoundService _sound;
    private readonly NotchEvents _events;
    private readonly WaterData _data;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(1) };
    private DateTime _lastReminder = DateTime.MinValue;
    private DateTime _snoozedUntil = DateTime.MinValue;
    private DateTime _today = DateTime.Today;

    public WaterViewModel(AppSettings settings, SoundService sound, NotchEvents events, bool demo)
    {
        _settings = settings;
        _sound = sound;
        _events = events;
        _data = demo ? new WaterData() : JsonStore.Load<WaterData>(FileName);
        _data.Entries.RemoveAll(e => e.Time < DateTime.Today.AddDays(-30));

        DrinkCommand = new RelayCommand(Drink);
        UndoCommand = new RelayCommand(Undo, () => TodayEntries().Any());
        _settings.PropertyChanged += OnSettingsChanged;
        _timer.Tick += (_, _) => CheckReminder();
        _timer.Start();
        Rebuild();
    }

    public RelayCommand DrinkCommand { get; }
    public RelayCommand UndoCommand { get; }
    public ObservableCollection<WaterBar> Bars { get; } = new();

    public int TodayMl { get; private set => SetProperty(ref field, value); }
    public double Progress { get; private set => SetProperty(ref field, value); }
    public string AmountText { get; private set => SetProperty(ref field, value); } = "0.0 L";
    public string GoalText { get; private set => SetProperty(ref field, value); } = "";
    public string StatusText { get; private set => SetProperty(ref field, value); } = "";
    public string LastDrinkText { get; private set => SetProperty(ref field, value); } = "";
    public string DrinkLabel { get; private set => SetProperty(ref field, value); } = "Drink";
    public string ChartMaxText { get; private set => SetProperty(ref field, value); } = "";

    public void Drink()
    {
        _data.Entries.Add(new WaterEntry { Time = DateTime.Now, Ml = _settings.WaterCupMl });
        _snoozedUntil = DateTime.MinValue;
        Save();
        Rebuild();
    }

    public void AddDemoEntries()
    {
        var today = DateTime.Today;
        foreach (var (hour, ml) in new[] { (8, 250), (9, 300), (11, 250), (12, 400), (14, 250), (15, 200) })
        {
            _data.Entries.Add(new WaterEntry { Time = today.AddHours(hour).AddMinutes(10), Ml = ml });
        }

        Rebuild();
    }

    private void Undo()
    {
        var last = TodayEntries().OrderBy(e => e.Time).LastOrDefault();
        if (last is null)
        {
            return;
        }

        _data.Entries.Remove(last);
        Save();
        Rebuild();
    }

    private IEnumerable<WaterEntry> TodayEntries()
    {
        var today = DateTime.Today;
        return _data.Entries.Where(e => e.Time.Date == today);
    }

    private void Save() => JsonStore.Save(FileName, _data);

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppSettings.WaterGoalMl) or nameof(AppSettings.WaterCupMl)
            or nameof(AppSettings.WaterStartHour) or nameof(AppSettings.WaterEndHour) or nameof(AppSettings.WaterReminders))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        _today = DateTime.Today;
        var entries = TodayEntries().ToList();
        TodayMl = entries.Sum(e => e.Ml);
        var goal = Math.Max(1, _settings.WaterGoalMl);
        Progress = Math.Clamp(TodayMl / (double)goal, 0, 1);
        AmountText = Ui.FormatLitres(TodayMl);
        GoalText = "of " + Ui.FormatLitres(goal);
        var percent = (int)Math.Round(TodayMl * 100.0 / goal);
        StatusText = TodayMl >= goal
            ? $"Today · {percent}% · Goal reached"
            : $"Today · {percent}%";
        LastDrinkText = entries.Count == 0
            ? (_settings.WaterReminders ? "No drinks yet today" : "Reminders are off")
            : "Last drink " + Ui.FormatTime(entries.Max(e => e.Time));
        DrinkLabel = $"Drink {_settings.WaterCupMl} ml";

        Bars.Clear();
        var start = _settings.WaterStartHour;
        var end = Math.Max(start + 1, _settings.WaterEndHour);
        var perHour = Enumerable.Range(start, end - start + 1)
            .Select(h => (Hour: h, Ml: entries.Where(e => e.Time.Hour == h).Sum(e => e.Ml)))
            .ToList();
        var max = Math.Max(_settings.WaterCupMl * 2, perHour.Max(p => p.Ml));
        ChartMaxText = $"{max} ml";
        var now = DateTime.Now.Hour;
        foreach (var (hour, ml) in perHour)
        {
            Bars.Add(new WaterBar
            {
                Hour = hour,
                Label = hour % 3 == 0 ? hour.ToString("00") : "",
                Ml = ml,
                Height = ml / (double)max,
                IsCurrent = hour == now,
            });
        }

        UndoCommand.NotifyCanExecuteChanged();
    }

    private void CheckReminder()
    {
        if (_today != DateTime.Today)
        {
            Rebuild(); // A new day started.
        }

        if (!_settings.WaterReminders || TodayMl >= _settings.WaterGoalMl)
        {
            return;
        }

        var now = DateTime.Now;
        if (now.Hour < _settings.WaterStartHour || now.Hour > _settings.WaterEndHour || now < _snoozedUntil)
        {
            return;
        }

        var lastDrink = TodayEntries().Select(e => e.Time).DefaultIfEmpty(DateTime.Today.AddHours(_settings.WaterStartHour)).Max();
        var since = now - (lastDrink > _lastReminder ? lastDrink : _lastReminder);
        if (since < TimeSpan.FromMinutes(_settings.WaterIntervalMinutes))
        {
            return;
        }

        _lastReminder = now;
        _sound.PlayPing();
        _events.ShowAlert(new AlertInfo
        {
            Icon = Ui.Icon("Water"),
            Accent = Ui.Water,
            Title = "Time for some water",
            Message = $"{AmountText} of {Ui.FormatLitres(_settings.WaterGoalMl)} so far today.",
            Timeout = TimeSpan.FromMinutes(2),
            Actions = new[]
            {
                new AlertAction(DrinkLabel, Drink, isPrimary: true),
                new AlertAction("Later", () => _snoozedUntil = DateTime.Now.AddMinutes(15)),
            },
        });
    }
}
