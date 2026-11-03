using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Threading;
using Jer.Core;
using Jer.Models;
using Jer.Services;

namespace Jer.ViewModels;

public sealed class TodoItemViewModel : ObservableObject
{
    private readonly TodoViewModel _owner;

    public TodoItemViewModel(TodoViewModel owner, TodoItemModel model)
    {
        _owner = owner;
        Model = model;
    }

    public TodoItemModel Model { get; }

    public string Title => Model.Title;

    public Brush ColorBrush => Ui.TaskColors[Math.Abs(Model.Color) % Ui.TaskColors.Length];

    public bool IsDone
    {
        get => Model.Done;
        set
        {
            if (Model.Done == value)
            {
                return;
            }

            Model.Done = value;
            Model.CompletedAt = value ? DateTime.Now : null;
            OnPropertyChanged();
            _owner.OnItemChanged();
        }
    }

    public string DueText
    {
        get
        {
            if (Model.DueAt is not { } due)
            {
                return "";
            }

            var time = Ui.FormatTime(due);
            return due.Date == DateTime.Today ? time
                : due.Date == DateTime.Today.AddDays(1) ? "Tomorrow " + time
                : due.ToString("d MMM", CultureInfo.CurrentCulture) + " " + time;
        }
    }

    public bool IsOverdue => !Model.Done && Model.DueAt is { } due && due <= DateTime.Now;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(ColorBrush));
        OnPropertyChanged(nameof(DueText));
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(IsDone));
    }
}

/// <summary>A small to-do list. Give a task a time and the notch taps you on the shoulder when it's due.</summary>
public sealed partial class TodoViewModel : ObservableObject
{
    private const string FileName = "todo.json";
    private readonly SoundService _sound;
    private readonly NotchEvents _events;
    private readonly TodoData _data;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly bool _demo;
    private TodoItemViewModel? _editing;

    public TodoViewModel(SoundService sound, NotchEvents events, bool demo)
    {
        _sound = sound;
        _events = events;
        _demo = demo;
        _data = demo ? new TodoData() : JsonStore.Load<TodoData>(FileName);

        // Keep yesterday's finished tasks out of the way.
        _data.Items.RemoveAll(i => i.Done && i.CompletedAt is { } done && done.Date < DateTime.Today.AddDays(-1));

        SubmitCommand = new RelayCommand(Submit);
        DeleteCommand = new RelayCommand(p => Delete(p as TodoItemViewModel));
        EditCommand = new RelayCommand(p => BeginEdit(p as TodoItemViewModel));
        CancelEditCommand = new RelayCommand(CancelEdit);
        NextColorCommand = new RelayCommand(() => NewColor = (NewColor + 1) % Ui.TaskColors.Length);
        ClearDoneCommand = new RelayCommand(ClearDone, () => Items.Any(i => i.IsDone));

        foreach (var item in _data.Items)
        {
            Items.Add(new TodoItemViewModel(this, item));
        }

        Sort();
        UpdateSummary();
        _timer.Tick += (_, _) => CheckReminders();
        _timer.Start();
    }

    public ObservableCollection<TodoItemViewModel> Items { get; } = new();

    public RelayCommand SubmitCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand EditCommand { get; }
    public RelayCommand CancelEditCommand { get; }
    public RelayCommand NextColorCommand { get; }
    public RelayCommand ClearDoneCommand { get; }

    public string NewTitle { get; set => SetProperty(ref field, value); } = "";
    public string NewTime { get; set => SetProperty(ref field, value); } = "";

    public int NewColor
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(NewColorBrush));
            }
        }
    }

    public Brush NewColorBrush => Ui.TaskColors[NewColor % Ui.TaskColors.Length];
    public bool IsEditing { get; private set => SetProperty(ref field, value); }
    public string SubmitLabel { get; private set => SetProperty(ref field, value); } = "Add";
    public string SummaryText { get; private set => SetProperty(ref field, value); } = "";
    public bool IsEmpty { get; private set => SetProperty(ref field, value); } = true;
    public string? InputError { get; private set => SetProperty(ref field, value); }

    public void AddDemo(string title, DateTime? due, int color, bool done)
    {
        var model = new TodoItemModel { Title = title, DueAt = due, Color = color, Done = done, CompletedAt = done ? DateTime.Now : null };
        _data.Items.Add(model);
        Items.Add(new TodoItemViewModel(this, model));
        Sort();
        UpdateSummary();
    }

    internal void OnItemChanged()
    {
        Sort();
        UpdateSummary();
        Save();
    }

    private void Submit()
    {
        var title = NewTitle.Trim();
        if (title.Length == 0)
        {
            return;
        }

        DateTime? due = null;
        if (!string.IsNullOrWhiteSpace(NewTime))
        {
            due = ParseTime(NewTime);
            if (due is null)
            {
                InputError = "Use a time like 9:30 or 21:00";
                return;
            }
        }

        InputError = null;
        if (_editing is not null)
        {
            var model = _editing.Model;
            var timeChanged = model.DueAt != due;
            model.Title = title;
            model.DueAt = due;
            model.Color = NewColor;
            if (timeChanged)
            {
                model.Reminded = false;
            }

            _editing.Refresh();
            _editing = null;
        }
        else
        {
            var model = new TodoItemModel { Title = title, DueAt = due, Color = NewColor };
            _data.Items.Add(model);
            Items.Add(new TodoItemViewModel(this, model));
        }

        NewTitle = "";
        NewTime = "";
        IsEditing = false;
        SubmitLabel = "Add";
        OnItemChanged();
    }

    private void BeginEdit(TodoItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _editing = item;
        NewTitle = item.Model.Title;
        NewTime = item.Model.DueAt is { } due ? due.ToString("HH:mm", CultureInfo.InvariantCulture) : "";
        NewColor = item.Model.Color;
        IsEditing = true;
        SubmitLabel = "Save";
    }

    private void CancelEdit()
    {
        _editing = null;
        NewTitle = "";
        NewTime = "";
        IsEditing = false;
        SubmitLabel = "Add";
        InputError = null;
    }

    private void Delete(TodoItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (ReferenceEquals(item, _editing))
        {
            CancelEdit();
        }

        _data.Items.Remove(item.Model);
        Items.Remove(item);
        OnItemChanged();
    }

    private void ClearDone()
    {
        foreach (var item in Items.Where(i => i.IsDone).ToList())
        {
            _data.Items.Remove(item.Model);
            Items.Remove(item);
        }

        OnItemChanged();
    }

    private void Save()
    {
        if (!_demo)
        {
            JsonStore.Save(FileName, _data);
        }
    }

    private void Sort()
    {
        var ordered = Items
            .OrderBy(i => i.IsDone)
            .ThenBy(i => i.Model.DueAt ?? DateTime.MaxValue)
            .ThenBy(i => i.Model.CreatedAt)
            .ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Items.IndexOf(ordered[i]);
            if (current != i)
            {
                Items.Move(current, i);
            }
        }
    }

    private void UpdateSummary()
    {
        var today = DateTime.Today;
        var doneToday = Items.Count(i => i.IsDone && i.Model.CompletedAt?.Date == today);
        var open = Items.Count(i => !i.IsDone);
        SummaryText = $"{doneToday}/{doneToday + open} done today";
        IsEmpty = Items.Count == 0;
        ClearDoneCommand.NotifyCanExecuteChanged();
    }

    private void CheckReminders()
    {
        var now = DateTime.Now;
        foreach (var item in Items.Where(i => !i.IsDone && !i.Model.Reminded && i.Model.DueAt is { } due && due <= now).ToList())
        {
            item.Model.Reminded = true;
            Save();
            _sound.PlayPing();
            var target = item;
            _events.ShowAlert(new AlertInfo
            {
                Icon = Ui.Icon("Bell"),
                Accent = target.ColorBrush,
                Title = "Time's up!",
                Message = target.Title,
                Actions = new[]
                {
                    new AlertAction("Done", () => target.IsDone = true, isPrimary: true),
                    new AlertAction("Snooze 5 min", () => Snooze(target)),
                },
            });
        }

        foreach (var item in Items)
        {
            item.Refresh();
        }
    }

    private void Snooze(TodoItemViewModel item)
    {
        item.Model.DueAt = DateTime.Now.AddMinutes(5);
        item.Model.Reminded = false;
        item.Refresh();
        OnItemChanged();
    }

    /// <summary>Accepts 9, 930, 0930, 9:30, 09.30 and 21:05. A time that already passed means tomorrow.</summary>
    public static DateTime? ParseTime(string text)
    {
        var m = TimePattern().Match(text.Trim());
        if (!m.Success)
        {
            return null;
        }

        int hour, minute;
        if (m.Groups["h"].Success)
        {
            hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            minute = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        }
        else
        {
            var digits = m.Groups["d"].Value;
            hour = int.Parse(digits.Substring(0, digits.Length - 2), CultureInfo.InvariantCulture);
            minute = int.Parse(digits.Substring(digits.Length - 2), CultureInfo.InvariantCulture);
        }

        if (hour > 23 || minute > 59)
        {
            return null;
        }

        var due = DateTime.Today.AddHours(hour).AddMinutes(minute);
        return due <= DateTime.Now ? due.AddDays(1) : due;
    }

    private static readonly Regex TimeRegex = new(@"^(?:(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?|(?<d>\d{3,4}))$", RegexOptions.CultureInvariant);

    private static Regex TimePattern() => TimeRegex;
}
