using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Jer.Core;
using Jer.Models;
using Jer.Services;

namespace Jer.ViewModels;

public sealed class LyricLineViewModel : ObservableObject
{
    public LyricLineViewModel(int index, LyricLine line)
    {
        Index = index;
        Time = line.Time;
        Text = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text;
    }

    public int Index { get; }
    public TimeSpan Time { get; }
    public string Text { get; }
    public bool IsCurrent { get; set => SetProperty(ref field, value); }
    public bool IsPast { get; set => SetProperty(ref field, value); }
}

public enum LyricsStatus
{
    Off,
    Waiting,
    Loading,
    Synced,
    Unsynced,
    Instrumental,
    NotFound,
}

/// <summary>Now playing, controls, progress, volume and synced lyrics.</summary>
public sealed class MusicViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly LyricsService _lyrics;
    private readonly VolumeService? _volume;
    private readonly DispatcherTimer _ticker = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Debouncer _lyricsDebounce = new(TimeSpan.FromMilliseconds(450));
    private CancellationTokenSource? _lyricsCts;
    private Lyrics _current = Lyrics.None;
    private string _loadedKey = "";
    private bool _viewNeedsProgress;
    private bool _updatingVolume;

    public MusicViewModel(MediaService media, LyricsService lyrics, VolumeService? volume, AppSettings settings)
    {
        Media = media;
        _lyrics = lyrics;
        _volume = volume;
        _settings = settings;

        PlayPauseCommand = new RelayCommand(() => _ = Media.PlayPauseAsync());
        NextCommand = new RelayCommand(() => _ = Media.NextAsync());
        PreviousCommand = new RelayCommand(() => _ = Media.PreviousAsync());
        ToggleLyricsCommand = new RelayCommand(() => _settings.LyricsEnabled = !_settings.LyricsEnabled);
        SeekToLineCommand = new RelayCommand(p =>
        {
            if (p is LyricLineViewModel line && _current.IsSynced)
            {
                _ = Media.SeekAsync(line.Time + TimeSpan.FromMilliseconds(50));
            }
        });
        ToggleMuteCommand = new RelayCommand(() => _volume?.ToggleMute());

        _ticker.Tick += (_, _) => Tick();
        Media.PropertyChanged += OnMediaChanged;
        Media.TrackChanged += (_, _) => OnTrackChanged();
        Media.TimelineChanged += (_, _) => Tick();
        _settings.PropertyChanged += OnSettingsChanged;

        if (_volume is not null)
        {
            Volume = _volume.Level * 100;
            IsMuted = _volume.IsMuted;
            _volume.Changed += (_, e) =>
            {
                _updatingVolume = true;
                Volume = e.Level * 100;
                IsMuted = e.IsMuted;
                _updatingVolume = false;
            };
        }

        LyricsStatus = _settings.LyricsEnabled ? LyricsStatus.Waiting : LyricsStatus.Off;
        UpdateTicker();
    }

    /// <summary>Raised when the sung line changes (index into <see cref="Lines"/>, or -1).</summary>
    public event EventHandler<int>? CurrentLineChanged;

    public MediaService Media { get; }

    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand NextCommand { get; }
    public RelayCommand PreviousCommand { get; }
    public RelayCommand ToggleLyricsCommand { get; }
    public RelayCommand SeekToLineCommand { get; }
    public RelayCommand ToggleMuteCommand { get; }

    public ObservableCollection<LyricLineViewModel> Lines { get; } = new();

    public bool LyricsEnabled => _settings.LyricsEnabled;
    public LyricsStatus LyricsStatus { get; private set => SetProperty(ref field, value); }
    public string LyricsMessage { get; private set => SetProperty(ref field, value); } = "";
    public int CurrentLineIndex { get; private set; } = -1;
    public string CurrentLineText { get; private set => SetProperty(ref field, value); } = "";

    /// <summary>True when the small lyrics pill under the closed notch should show.</summary>
    public bool ShowLyricPill => LyricsEnabled && Media.IsPlaying && LyricsStatus == LyricsStatus.Synced && CurrentLineText.Length > 0;

    public double Progress { get; private set => SetProperty(ref field, value); }
    public string ElapsedText { get; private set => SetProperty(ref field, value); } = "0:00";
    public string RemainingText { get; private set => SetProperty(ref field, value); } = "-0:00";
    public bool IsSeeking { get; set; }
    public bool HasVolume => _volume?.IsAvailable == true;

    public double Volume
    {
        get;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (SetProperty(ref field, value) && !_updatingVolume)
            {
                _volume?.SetLevel((float)(value / 100.0));
            }
        }
    }

    public bool IsMuted { get; private set => SetProperty(ref field, value); }

    /// <summary>The expanded Music page tells us when it is on screen, so the clock only ticks when needed.</summary>
    public bool ViewNeedsProgress
    {
        get => _viewNeedsProgress;
        set
        {
            _viewNeedsProgress = value;
            UpdateTicker();
            if (value)
            {
                Tick();
            }
        }
    }

    public void SeekToFraction(double fraction)
    {
        if (Media.Duration <= TimeSpan.Zero)
        {
            return;
        }

        _ = Media.SeekAsync(TimeSpan.FromTicks((long)(Media.Duration.Ticks * Math.Clamp(fraction, 0, 1))));
    }

    /// <summary>Shows demo lyrics without a network lookup (--demo).</summary>
    public void UseDemoLyrics(Lyrics lyrics)
    {
        _lyrics.Seed(Media.Title, Media.Artist, lyrics);
        _loadedKey = "";
        OnTrackChanged();
    }

    private void OnMediaChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MediaService.IsPlaying))
        {
            UpdateTicker();
            Tick();
            OnPropertyChanged(nameof(ShowLyricPill));
        }
        else if (e.PropertyName == nameof(MediaService.Duration))
        {
            Tick();
            if (LyricsEnabled && LyricsStatus is LyricsStatus.NotFound or LyricsStatus.Waiting)
            {
                _loadedKey = "";
                ScheduleLyrics();
            }
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.LyricsEnabled))
        {
            return;
        }

        OnPropertyChanged(nameof(LyricsEnabled));
        if (LyricsEnabled)
        {
            LyricsStatus = LyricsStatus.Waiting;
            _loadedKey = "";
            ScheduleLyrics();
        }
        else
        {
            _lyricsCts?.Cancel();
            LyricsStatus = LyricsStatus.Off;
            SetLyrics(Lyrics.None);
        }

        UpdateTicker();
        OnPropertyChanged(nameof(ShowLyricPill));
    }

    private void OnTrackChanged()
    {
        SetLyrics(Lyrics.None);
        Tick();
        if (LyricsEnabled)
        {
            LyricsStatus = Media.HasSession ? LyricsStatus.Waiting : LyricsStatus.Off;
            ScheduleLyrics();
        }

        OnPropertyChanged(nameof(ShowLyricPill));
    }

    private void ScheduleLyrics() => _lyricsDebounce.Run(() => _ = LoadLyricsAsync());

    private async Task LoadLyricsAsync()
    {
        if (!LyricsEnabled || !Media.HasSession || string.IsNullOrWhiteSpace(Media.Title))
        {
            return;
        }

        var key = $"{Media.Artist}\u001f{Media.Title}";
        if (key == _loadedKey)
        {
            return;
        }

        _lyricsCts?.Cancel();
        var cts = _lyricsCts = new CancellationTokenSource();
        LyricsStatus = LyricsStatus.Loading;
        LyricsMessage = "Looking for lyrics…";
        try
        {
            var result = await _lyrics.GetAsync(Media.Title, Media.Artist, Media.Album, Media.Duration, cts.Token);
            if (cts.IsCancellationRequested || key != $"{Media.Artist}\u001f{Media.Title}")
            {
                return;
            }

            _loadedKey = key;
            SetLyrics(result);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetLyrics(Lyrics lyrics)
    {
        _current = lyrics;
        Lines.Clear();
        for (var i = 0; i < lyrics.Lines.Count; i++)
        {
            Lines.Add(new LyricLineViewModel(i, lyrics.Lines[i]));
        }

        CurrentLineIndex = -1;
        CurrentLineText = "";
        if (!LyricsEnabled)
        {
            LyricsStatus = LyricsStatus.Off;
        }
        else if (lyrics.IsInstrumental)
        {
            LyricsStatus = LyricsStatus.Instrumental;
            LyricsMessage = "Instrumental ♪";
        }
        else if (lyrics.IsSynced)
        {
            LyricsStatus = LyricsStatus.Synced;
            LyricsMessage = "";
        }
        else if (lyrics.Lines.Count > 0)
        {
            LyricsStatus = LyricsStatus.Unsynced;
            LyricsMessage = "These lyrics aren't synced";
        }
        else if (LyricsStatus is LyricsStatus.Loading)
        {
            LyricsStatus = LyricsStatus.NotFound;
            LyricsMessage = "No lyrics found for this song";
        }

        UpdateTicker();
        Tick();
        CurrentLineChanged?.Invoke(this, -1);
        OnPropertyChanged(nameof(ShowLyricPill));
    }

    private void UpdateTicker()
    {
        var needed = Media.IsPlaying && (_viewNeedsProgress || (LyricsEnabled && _current.IsSynced));
        if (needed && !_ticker.IsEnabled)
        {
            _ticker.Start();
        }
        else if (!needed && _ticker.IsEnabled)
        {
            _ticker.Stop();
        }
    }

    private void Tick()
    {
        var position = Media.GetPosition();
        var duration = Media.Duration;
        if (!IsSeeking)
        {
            Progress = duration > TimeSpan.Zero ? position.TotalSeconds / duration.TotalSeconds : 0;
        }

        ElapsedText = Ui.FormatClock(position);
        RemainingText = "-" + Ui.FormatClock(duration - position);

        if (!_current.IsSynced || _current.Lines.Count == 0)
        {
            return;
        }

        // Light the line up a touch early; it feels more in time with the singer.
        var index = LyricsService.FindLine(_current.Lines, position + TimeSpan.FromMilliseconds(150));
        if (index == CurrentLineIndex)
        {
            return;
        }

        if (CurrentLineIndex >= 0 && CurrentLineIndex < Lines.Count)
        {
            Lines[CurrentLineIndex].IsCurrent = false;
        }

        CurrentLineIndex = index;
        for (var i = 0; i < Lines.Count; i++)
        {
            Lines[i].IsPast = i < index;
        }

        if (index >= 0 && index < Lines.Count)
        {
            Lines[index].IsCurrent = true;
            CurrentLineText = _current.Lines[index].Text.Trim();
        }
        else
        {
            CurrentLineText = "";
        }

        CurrentLineChanged?.Invoke(this, index);
        OnPropertyChanged(nameof(ShowLyricPill));
    }
}
