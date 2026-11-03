using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using Jer.Core;
using Jer.Models;
using Jer.Services;

namespace Jer.ViewModels;

/// <summary>One display in the picker (list row and tile in the map).</summary>
public sealed class DisplayOption : ObservableObject
{
    private readonly SettingsViewModel _owner;

    public DisplayOption(SettingsViewModel owner, DisplayInfo info)
    {
        _owner = owner;
        Info = info;
        SelectCommand = new RelayCommand(() => _owner.Select(this));
    }

    public DisplayInfo Info { get; }
    public RelayCommand SelectCommand { get; }
    public string Title => Info.Title;
    public string Description => Info.Description;
    public string NumberText => Info.Number.ToString();
    public string ShortName => Info.FriendlyName;

    public bool IsSelected
    {
        get;
        set
        {
            if (!SetProperty(ref field, value))
            {
                return;
            }

            if (value && !_syncing)
            {
                _owner.Select(this);
            }
        }
    }

    /// <summary>The notch is on this display right now (it may be the main display as a fallback).</summary>
    public bool IsCurrent { get; set => SetProperty(ref field, value); }

    public double MapLeft { get; set => SetProperty(ref field, value); }
    public double MapTop { get; set => SetProperty(ref field, value); }
    public double MapWidth { get; set => SetProperty(ref field, value); }
    public double MapHeight { get; set => SetProperty(ref field, value); }

    private bool _syncing;

    /// <summary>Mirrors the saved setting without writing it back.</summary>
    internal void SetSelectedSilently(bool value)
    {
        _syncing = true;
        IsSelected = value;
        _syncing = false;
    }
}

/// <summary>Everything the Settings window shows. Changes apply instantly and are saved automatically.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    public const double MapWidth = 480;
    public const double MapHeight = 150;

    private readonly MonitorService _monitors;
    private readonly Action<MonitorService> _identify;
    private readonly Action _exit;

    public SettingsViewModel(AppSettings settings, MonitorService monitors, Action<MonitorService> identify, Action exit)
    {
        Settings = settings;
        _monitors = monitors;
        _identify = identify;
        _exit = exit;

        IdentifyCommand = new RelayCommand(() => _identify(_monitors));
        OpenDataFolderCommand = new RelayCommand(() => OpenInShell(JsonStore.DataDirectory, isFolder: true));
        OpenLogCommand = new RelayCommand(() => OpenInShell(Log.FilePath, isFolder: false));
        OpenWebsiteCommand = new RelayCommand(() => OpenInShell("https://github.com/Ma-Biche/Jer", isFolder: false));
        ExitCommand = new RelayCommand(_exit);

        Settings.PropertyChanged += OnSettingsChanged;
        _monitors.DisplaysChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(RebuildDisplays);
        RebuildDisplays();
    }

    public AppSettings Settings { get; }
    public ObservableCollection<DisplayOption> Displays { get; } = new();

    public RelayCommand IdentifyCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand OpenWebsiteCommand { get; }
    public RelayCommand ExitCommand { get; }

    public string VersionText => "Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
    public string DataFolder => JsonStore.DataDirectory;

    public bool StartWithWindows
    {
        get => StartupService.IsEnabled();
        set
        {
            StartupService.SetEnabled(value);
            Settings.StartWithWindows = StartupService.IsEnabled();
            OnPropertyChanged();
        }
    }

    public bool UsePrimary
    {
        get => string.Equals(Settings.DisplayTarget, AppSettings.PrimaryDisplay, StringComparison.OrdinalIgnoreCase);
        set
        {
            if (value)
            {
                Settings.DisplayTarget = AppSettings.PrimaryDisplay;
                Settings.DisplayTargetName = null;
            }
        }
    }

    public bool HasManyDisplays => Displays.Count > 1;

    public string DisplayNote { get; private set => SetProperty(ref field, value); } = "";

    public string NotchSizeText => $"{Math.Round(Settings.NotchScale * 100)}%";
    public string WaterGoalText => Ui.FormatLitres(Settings.WaterGoalMl);
    public string WaterHoursText => $"{Settings.WaterStartHour:00}:00 – {Settings.WaterEndHour:00}:00";

    // Page switches: the notch always keeps at least one page.
    public bool MusicPage { get => Settings.MusicPage; set => SetPage(value, v => Settings.MusicPage = v); }
    public bool PomodoroPage { get => Settings.PomodoroPage; set => SetPage(value, v => Settings.PomodoroPage = v); }
    public bool WaterPage { get => Settings.WaterPage; set => SetPage(value, v => Settings.WaterPage = v); }
    public bool ShelfPage { get => Settings.ShelfPage; set => SetPage(value, v => Settings.ShelfPage = v); }
    public bool TodoPage { get => Settings.TodoPage; set => SetPage(value, v => Settings.TodoPage = v); }

    internal void Select(DisplayOption option)
    {
        Settings.DisplayTarget = option.Info.Key;
        Settings.DisplayTargetName = option.Info.FriendlyName;
    }

    private void SetPage(bool value, Action<bool> apply)
    {
        var enabled = new[] { Settings.MusicPage, Settings.PomodoroPage, Settings.WaterPage, Settings.ShelfPage, Settings.TodoPage }.Count(b => b);
        if (!value && enabled <= 1)
        {
            // Refuse to switch off the last page; tell the toggles to snap back.
            Application.Current.Dispatcher.BeginInvoke(RaisePages);
            return;
        }

        apply(value);
        RaisePages();
    }

    private void RaisePages()
    {
        OnPropertyChanged(nameof(MusicPage));
        OnPropertyChanged(nameof(PomodoroPage));
        OnPropertyChanged(nameof(WaterPage));
        OnPropertyChanged(nameof(ShelfPage));
        OnPropertyChanged(nameof(TodoPage));
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.DisplayTarget):
                SyncDisplaySelection();
                break;
            case nameof(AppSettings.NotchScale):
                OnPropertyChanged(nameof(NotchSizeText));
                break;
            case nameof(AppSettings.WaterGoalMl):
                OnPropertyChanged(nameof(WaterGoalText));
                break;
            case nameof(AppSettings.WaterStartHour):
            case nameof(AppSettings.WaterEndHour):
                OnPropertyChanged(nameof(WaterHoursText));
                break;
            case nameof(AppSettings.StartWithWindows):
                OnPropertyChanged(nameof(StartWithWindows));
                break;
        }
    }

    private void RebuildDisplays()
    {
        Displays.Clear();
        var list = _monitors.Displays;
        if (list.Count == 0)
        {
            return;
        }

        var minX = list.Min(d => d.Bounds.Left);
        var minY = list.Min(d => d.Bounds.Top);
        var maxX = list.Max(d => d.Bounds.Right);
        var maxY = list.Max(d => d.Bounds.Bottom);
        var totalW = Math.Max(1, maxX - minX);
        var totalH = Math.Max(1, maxY - minY);
        var scale = Math.Min(MapWidth / totalW, MapHeight / totalH) * 0.94;
        var offsetX = (MapWidth - totalW * scale) / 2;
        var offsetY = (MapHeight - totalH * scale) / 2;

        foreach (var info in list)
        {
            Displays.Add(new DisplayOption(this, info)
            {
                MapLeft = offsetX + (info.Bounds.Left - minX) * scale + 3,
                MapTop = offsetY + (info.Bounds.Top - minY) * scale + 3,
                MapWidth = Math.Max(24, info.Bounds.Width * scale - 6),
                MapHeight = Math.Max(18, info.Bounds.Height * scale - 6),
            });
        }

        OnPropertyChanged(nameof(HasManyDisplays));
        SyncDisplaySelection();
    }

    private void SyncDisplaySelection()
    {
        var current = _monitors.Resolve(Settings.DisplayTarget, out var fellBack);
        var usePrimary = UsePrimary;
        foreach (var option in Displays)
        {
            option.SetSelectedSilently(!usePrimary && !fellBack && ReferenceEquals(option.Info, current));
            option.IsCurrent = ReferenceEquals(option.Info, current);
        }

        OnPropertyChanged(nameof(UsePrimary));
        DisplayNote = fellBack
            ? $"{Settings.DisplayTargetName ?? "The display you picked"} isn't connected, so Jer is on your main display for now. It moves back when you reconnect it."
            : "";
    }

    private static void OpenInShell(string target, bool isFolder)
    {
        try
        {
            if (isFolder)
            {
                Directory.CreateDirectory(target);
            }
            else if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(target))
            {
                return;
            }

            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not open {target}", ex);
        }
    }
}
