using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Jer.Core;
using Jer.Models;
using Jer.Services;
using Jer.ViewModels;
using Jer.Views;

namespace Jer;

public partial class App : Application
{
    private const string MutexName = @"Local\Jer.SingleInstance.7f3c2a9e";
    private const string ShowSettingsEventName = @"Local\Jer.ShowSettings.7f3c2a9e";
    private const string SettingsFile = "settings.json";

    private readonly NotchEvents _events = new();
    private Mutex? _mutex;
    private EventWaitHandle? _showSettingsEvent;
    private RegisteredWaitHandle? _showSettingsWait;
    private AppSettings _settings = new();
    private Debouncer? _saveSettings;
    private MonitorService _monitors = new();
    private MediaService? _media;
    private VolumeService? _volume;
    private SystemWatcher? _watcher;
    private TrayService? _tray;
    private NotchViewModel? _vm;
    private NotchWindow? _notch;
    private SettingsWindow? _settingsWindow;
    private bool _demo;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args.Select(a => a.Trim()).ToArray();
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var firstInstance);
        _showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        if (!firstInstance)
        {
            // Jer is already running: opening it again shows its Settings.
            if (!args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
            {
                AllowSetForegroundWindow(-1);
                _showSettingsEvent.Set();
            }

            _mutex.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Unhandled exception", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Warn("Unobserved task exception", ex.Exception);
            ex.SetObserved();
        };

        _demo = args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        JsonStore.ReadOnly = _demo;
        Log.Info($"Jer {Assembly.GetExecutingAssembly().GetName().Version} starting{(_demo ? " (demo)" : "")}");

        try
        {
            Start(args);
        }
        catch (Exception ex)
        {
            Log.Error("Jer could not start", ex);
            MessageBox.Show($"Jer could not start.\n\n{ex.Message}\n\nDetails are in {Log.FilePath}", "Jer", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _showSettingsWait = ThreadPool.RegisterWaitForSingleObject(
            _showSettingsEvent,
            (_, _) => Dispatcher.BeginInvoke(() => ShowSettings(null)),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _saveSettings?.Flush();
            _showSettingsWait?.Unregister(null);
            _tray?.Dispose();
            _watcher?.Dispose();
            _volume?.Dispose();
            if (_mutex is not null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Cleanup on exit failed", ex);
        }

        Log.Info("Jer stopped");
        base.OnExit(e);
    }

    private void Start(string[] args)
    {
        _settings = JsonStore.Load<AppSettings>(SettingsFile);
        _settings.Normalize();
        _saveSettings = new Debouncer(TimeSpan.FromMilliseconds(400));
        _settings.PropertyChanged += OnSettingsChanged;

        _monitors.Refresh();
        var sound = new SoundService();
        _media = new MediaService(Dispatcher);
        var lyrics = new LyricsService();
        _volume = VolumeService.TryCreate(Dispatcher);

        var music = new MusicViewModel(_media, lyrics, _volume, _settings);
        var pomodoro = new PomodoroViewModel(_settings, sound, _events);
        var water = new WaterViewModel(_settings, sound, _events, _demo);
        var shelf = new ShelfViewModel(_events, _demo);
        var todo = new TodoViewModel(sound, _events, _demo);
        _vm = new NotchViewModel(_settings, _events, music, pomodoro, water, shelf, todo, () => ShowSettings(null));

        _notch = new NotchWindow(_vm, _settings, _monitors, () => ShowSettings(null));
        _notch.Show();

        _watcher = new SystemWatcher(Dispatcher, () => _notch.CurrentMonitor, () => _notch.Handle);
        WireOverlays();
        _watcher.Start();
        _watcher.SetKeyWatching(_settings.CapsLockHud || _settings.KeyboardLayoutHud);

        _tray = new TrayService(_settings, _monitors, () => ShowSettings(null), () => ShowSettings("About"), () => Shutdown());

        if (_demo)
        {
            DemoData.Apply(_vm);
        }
        else
        {
            _ = _media.InitializeAsync();
            if (!_settings.FirstRunDone)
            {
                if (_settings.StartWithWindows)
                {
                    StartupService.SetEnabled(true);
                }
            }
            else
            {
                StartupService.RefreshPathIfEnabled();
            }

            _settings.StartWithWindows = StartupService.IsEnabled();
        }

        if (!_settings.FirstRunDone)
        {
            _events.ShowAlert(new AlertInfo
            {
                Icon = Ui.Icon("Jer"),
                Accent = Ui.Accent,
                Title = "Hi, I'm Jer",
                Message = "Hover the notch to open it. My settings live in the Jer icon next to the clock.",
                Timeout = TimeSpan.FromSeconds(20),
                Actions = new[]
                {
                    new AlertAction("Settings", () => ShowSettings(null)),
                    new AlertAction("Got it", isPrimary: true),
                },
            });
            _settings.FirstRunDone = true;
        }

        HandleArguments(args);
    }

    private void HandleArguments(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var next = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--settings":
                    ShowSettings(next);
                    break;
                case "--pin" when next is not null && Enum.TryParse<NotchPage>(next, ignoreCase: true, out var page):
                    _notch?.Pin(page);
                    break;
            }
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        _saveSettings?.Run(() => JsonStore.Save(SettingsFile, _settings));
        switch (e.PropertyName)
        {
            case nameof(AppSettings.CapsLockHud):
            case nameof(AppSettings.KeyboardLayoutHud):
                _watcher?.SetKeyWatching(_settings.CapsLockHud || _settings.KeyboardLayoutHud);
                break;
            case nameof(AppSettings.HideInFullscreen):
                if (_vm is not null && _watcher is not null)
                {
                    _vm.HiddenForFullscreen = _settings.HideInFullscreen && _watcher.IsFullscreen;
                }

                break;
        }
    }

    /// <summary>Turns system events into the small overlays that pop out of the notch.</summary>
    private void WireOverlays()
    {
        var watcher = _watcher!;
        var vm = _vm!;

        watcher.Tick += () => vm.UpdateIdle(watcher.IdleTime);
        watcher.FullscreenChanged += fullscreen => vm.HiddenForFullscreen = fullscreen && _settings.HideInFullscreen;

        watcher.CapsLockChanged += on =>
        {
            if (_settings.CapsLockHud)
            {
                _events.ShowHud(new HudInfo("caps", Ui.Icon("CapsLock"), on ? "Caps Lock on" : "Caps Lock off", null, null, on ? Ui.Accent : Ui.White));
            }
        };

        watcher.KeyboardLayoutChanged += culture =>
        {
            if (_settings.KeyboardLayoutHud)
            {
                var code = culture.ThreeLetterISOLanguageName.ToUpperInvariant();
                _events.ShowHud(new HudInfo("keyboard", Ui.Icon("Keyboard"), code, culture.DisplayName, null, Ui.Accent));
            }
        };

        watcher.PowerChanged += (before, after) =>
        {
            if (!_settings.BatteryHud || !after.HasBattery)
            {
                return;
            }

            var percent = after.Percent.ToString(CultureInfo.CurrentCulture) + "%";
            if (!before.PluggedIn && after.PluggedIn)
            {
                _events.ShowHud(new HudInfo("power", Ui.Icon("Bolt"), "Charging", percent, null, Ui.Green));
            }
            else if (before.PluggedIn && !after.PluggedIn)
            {
                _events.ShowHud(new HudInfo("power", Ui.Icon("Battery"), "On battery", percent, null, Ui.White));
            }
            else if (!after.PluggedIn && ((before.Percent > 20 && after.Percent <= 20) || (before.Percent > 10 && after.Percent <= 10)))
            {
                _events.ShowHud(new HudInfo("power", Ui.Icon("Battery"), "Low battery", percent, null, after.Percent <= 10 ? Ui.Red : Ui.Orange));
            }
        };

        watcher.NetworkChanged += state =>
        {
            if (!_settings.NetworkHud)
            {
                return;
            }

            _events.ShowHud(state.Connected
                ? new HudInfo("network", Ui.Icon(state.IsWifi ? "Wifi" : "Display"), "Connected", string.IsNullOrWhiteSpace(state.Name) ? null : state.Name, null, Ui.Green)
                : new HudInfo("network", Ui.Icon("WifiOff"), "No internet", null, null, Ui.Orange));
        };

        if (_volume is not null)
        {
            _volume.Changed += (_, change) =>
            {
                if (!change.FromJer && _settings.VolumeHud)
                {
                    _events.ShowHud(new HudInfo(
                        "volume",
                        Ui.Icon(change.IsMuted || change.Level <= 0.001 ? "SpeakerMute" : "Speaker"),
                        change.IsMuted ? "Muted" : "Volume",
                        null,
                        change.IsMuted ? 0 : change.Level,
                        Ui.White));
                }
            };
        }
    }

    private void ShowSettings(string? tab)
    {
        if (_vm is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            var viewModel = new SettingsViewModel(_settings, _monitors, DisplayIdentifier.Show, () => Shutdown());
            _settingsWindow = new SettingsWindow(viewModel);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }

        _settingsWindow.ShowTab(tab);
        if (!_settingsWindow.IsVisible)
        {
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the notch alive; a broken feature shouldn't take the whole app down.
        Log.Error("Unexpected error", e.Exception);
        e.Handled = true;
    }
}
