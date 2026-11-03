using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Jer.Core;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Jer.Services;

/// <summary>
/// What's playing, from any app that shows up in the Windows media overlay
/// (Spotify, Apple Music, Media Player, browsers...). Everything is raised on the UI thread.
/// </summary>
public sealed class MediaService : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _onProperties;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _onPlayback;
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> _onTimeline;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _propertiesVersion;
    private bool _demo;

    private TimeSpan _positionAtUpdate;
    private DateTimeOffset _positionUpdatedAt = DateTimeOffset.Now;

    public MediaService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _onProperties = (s, e) => _dispatcher.BeginInvoke(() => _ = RefreshPropertiesAsync(s));
        _onPlayback = (s, e) => _dispatcher.BeginInvoke(() => RefreshPlayback(s));
        _onTimeline = (s, e) => _dispatcher.BeginInvoke(() => RefreshTimeline(s));
    }

    /// <summary>Raised when the title/artist changes.</summary>
    public event EventHandler? TrackChanged;

    /// <summary>Raised when the position jumps (seek, new timeline from the player).</summary>
    public event EventHandler? TimelineChanged;

    public bool HasSession { get; private set => SetProperty(ref field, value); }
    public bool IsPlaying { get; private set => SetProperty(ref field, value); }
    public string Title { get; private set => SetProperty(ref field, value); } = "";
    public string Artist { get; private set => SetProperty(ref field, value); } = "";
    public string Album { get; private set => SetProperty(ref field, value); } = "";
    public string SourceName { get; private set => SetProperty(ref field, value); } = "";
    public ImageSource? Artwork { get; private set => SetProperty(ref field, value); }
    public Color Accent { get; private set => SetProperty(ref field, value); } = Colors.White;
    public TimeSpan Duration { get; private set => SetProperty(ref field, value); }
    public bool CanPlayPause { get; private set => SetProperty(ref field, value); }
    public bool CanNext { get; private set => SetProperty(ref field, value); }
    public bool CanPrevious { get; private set => SetProperty(ref field, value); }
    public bool CanSeek { get; private set => SetProperty(ref field, value); }

    /// <summary>Current position, interpolated between the player's (sometimes rare) updates.</summary>
    public TimeSpan GetPosition()
    {
        var position = _positionAtUpdate;
        if (IsPlaying)
        {
            position += DateTimeOffset.Now - _positionUpdatedAt;
        }

        if (position < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
    }

    public async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Media sessions are not available", ex);
            return;
        }

        _manager.CurrentSessionChanged += (_, _) => _dispatcher.BeginInvoke(AttachCurrentSession);
        _manager.SessionsChanged += (_, _) => _dispatcher.BeginInvoke(AttachCurrentSession);
        AttachCurrentSession();
    }

    public async Task PlayPauseAsync()
    {
        if (_demo)
        {
            SetDemoPlaying(!IsPlaying);
            return;
        }

        await TryAsync(s => s.TryTogglePlayPauseAsync().AsTask());
    }

    public Task NextAsync() => TryAsync(s => s.TrySkipNextAsync().AsTask());

    public Task PreviousAsync() => TryAsync(s => s.TrySkipPreviousAsync().AsTask());

    public async Task SeekAsync(TimeSpan position)
    {
        if (Duration > TimeSpan.Zero)
        {
            position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, Duration.Ticks));
        }

        // Move the local clock right away so the UI doesn't snap back while the player catches up.
        _positionAtUpdate = position;
        _positionUpdatedAt = DateTimeOffset.Now;
        TimelineChanged?.Invoke(this, EventArgs.Empty);

        if (_demo)
        {
            return;
        }

        var session = _session;
        if (session is null)
        {
            return;
        }

        var start = TimeSpan.Zero;
        try
        {
            start = session.GetTimelineProperties().StartTime;
        }
        catch
        {
            // Some players don't report a timeline.
        }

        await TryAsync(s => s.TryChangePlaybackPositionAsync((start + position).Ticks).AsTask());
    }

    /// <summary>Fills in a fake track for screenshots (--demo). Nothing talks to Windows.</summary>
    public void UseDemo(string title, string artist, string album, TimeSpan duration, TimeSpan position, ImageSource artwork)
    {
        _demo = true;
        HasSession = true;
        Title = title;
        Artist = artist;
        Album = album;
        SourceName = "Demo";
        Duration = duration;
        Artwork = artwork;
        Accent = artwork is BitmapSource bmp ? ComputeAccent(bmp) : Colors.White;
        CanPlayPause = CanNext = CanPrevious = CanSeek = true;
        _positionAtUpdate = position;
        _positionUpdatedAt = DateTimeOffset.Now;
        IsPlaying = true;
        TrackChanged?.Invoke(this, EventArgs.Empty);
        TimelineChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetDemoPlaying(bool playing)
    {
        _positionAtUpdate = GetPosition();
        _positionUpdatedAt = DateTimeOffset.Now;
        IsPlaying = playing;
    }

    private async Task TryAsync(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        var session = _session;
        if (session is null || _demo)
        {
            return;
        }

        try
        {
            await action(session);
        }
        catch (Exception ex)
        {
            Log.Warn("Media command failed", ex);
        }
    }

    private void AttachCurrentSession()
    {
        if (_demo || _manager is null)
        {
            return;
        }

        GlobalSystemMediaTransportControlsSession? next;
        try
        {
            next = _manager.GetCurrentSession();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the current media session", ex);
            next = null;
        }

        if (ReferenceEquals(_session, next))
        {
            return; // Same session; its own events keep us up to date.
        }

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= _onProperties;
            _session.PlaybackInfoChanged -= _onPlayback;
            _session.TimelinePropertiesChanged -= _onTimeline;
        }

        _session = next;
        if (next is null)
        {
            Clear();
            return;
        }

        next.MediaPropertiesChanged += _onProperties;
        next.PlaybackInfoChanged += _onPlayback;
        next.TimelinePropertiesChanged += _onTimeline;
        HasSession = true;
        SourceName = FriendlyAppName(next.SourceAppUserModelId);
        RefreshPlayback(next);
        RefreshTimeline(next);
        _ = RefreshPropertiesAsync(next);
    }

    private bool IsCurrent(GlobalSystemMediaTransportControlsSession session) =>
        _session is not null && (ReferenceEquals(session, _session) || session.SourceAppUserModelId == _session.SourceAppUserModelId);

    private void Clear()
    {
        _propertiesVersion++;
        HasSession = false;
        IsPlaying = false;
        Title = Artist = Album = SourceName = "";
        Artwork = null;
        Accent = Colors.White;
        Duration = TimeSpan.Zero;
        CanPlayPause = CanNext = CanPrevious = CanSeek = false;
        _positionAtUpdate = TimeSpan.Zero;
        TrackChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task RefreshPropertiesAsync(GlobalSystemMediaTransportControlsSession session)
    {
        if (!IsCurrent(session))
        {
            return;
        }

        var version = ++_propertiesVersion;
        GlobalSystemMediaTransportControlsSessionMediaProperties? props;
        try
        {
            props = await session.TryGetMediaPropertiesAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read media properties", ex);
            return;
        }

        if (version != _propertiesVersion || props is null)
        {
            return;
        }

        var title = props.Title?.Trim() ?? "";
        var artist = props.Artist?.Trim() ?? "";
        var album = props.AlbumTitle?.Trim() ?? "";
        var trackChanged = title != Title || artist != Artist;
        Title = title;
        Artist = artist;
        Album = album;

        BitmapSource? art = null;
        try
        {
            art = await LoadThumbnailAsync(props.Thumbnail);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not load album art", ex);
        }

        if (version != _propertiesVersion)
        {
            return;
        }

        Artwork = art;
        Accent = art is null ? Colors.White : ComputeAccent(art);
        if (trackChanged)
        {
            TrackChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RefreshPlayback(GlobalSystemMediaTransportControlsSession session)
    {
        if (!IsCurrent(session))
        {
            return;
        }

        try
        {
            var info = session.GetPlaybackInfo();
            var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing != IsPlaying)
            {
                // Freeze or resume the local clock at the current interpolated position.
                _positionAtUpdate = GetPosition();
                _positionUpdatedAt = DateTimeOffset.Now;
            }

            IsPlaying = playing;
            var controls = info.Controls;
            CanPlayPause = controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled;
            CanNext = controls.IsNextEnabled;
            CanPrevious = controls.IsPreviousEnabled;
            CanSeek = controls.IsPlaybackPositionEnabled;
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read playback info", ex);
        }
    }

    private void RefreshTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        if (!IsCurrent(session))
        {
            return;
        }

        try
        {
            var t = session.GetTimelineProperties();
            var duration = t.EndTime - t.StartTime;
            Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero;
            _positionAtUpdate = t.Position - t.StartTime;

            // Players report when they sampled the position; distrust stale or future stamps.
            var now = DateTimeOffset.Now;
            var stamp = t.LastUpdatedTime;
            _positionUpdatedAt = stamp > now || now - stamp > TimeSpan.FromMinutes(10) ? now : stamp;
            TimelineChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not read the timeline", ex);
        }
    }

    private static async Task<BitmapSource?> LoadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        using var winrtStream = await reference.OpenReadAsync();
        if (winrtStream.Size == 0)
        {
            return null;
        }

        var buffer = new MemoryStream();
        using (var stream = winrtStream.AsStreamForRead())
        {
            await stream.CopyToAsync(buffer);
        }

        buffer.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 240;
        bitmap.StreamSource = buffer;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>A colour from the artwork, bright enough to read on black (used for the equalizer bars).</summary>
    public static Color ComputeAccent(BitmapSource source)
    {
        try
        {
            var small = new TransformedBitmap(source, new ScaleTransform(16.0 / source.PixelWidth, 16.0 / source.PixelHeight));
            var bgra = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            var stride = bgra.PixelWidth * 4;
            var px = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(px, stride, 0);

            double r = 0, g = 0, b = 0, total = 0;
            for (var i = 0; i + 3 < px.Length; i += 4)
            {
                double pb = px[i], pg = px[i + 1], pr = px[i + 2];
                var max = Math.Max(pr, Math.Max(pg, pb));
                var min = Math.Min(pr, Math.Min(pg, pb));
                var saturation = max <= 0 ? 0 : (max - min) / max;
                var weight = 0.05 + saturation * saturation * (max / 255.0);
                r += pr * weight;
                g += pg * weight;
                b += pb * weight;
                total += weight;
            }

            if (total <= 0)
            {
                return Colors.White;
            }

            r /= total;
            g /= total;
            b /= total;
            var top = Math.Max(r, Math.Max(g, b));
            var low = Math.Min(r, Math.Min(g, b));
            if (top < 1 || (top - low) / top < 0.18)
            {
                return Color.FromRgb(235, 235, 240); // Greyish artwork: stay neutral.
            }

            var boost = 230 / top;
            return Color.FromRgb(
                (byte)Math.Clamp(r * boost, 0, 255),
                (byte)Math.Clamp(g * boost, 0, 255),
                (byte)Math.Clamp(b * boost, 0, 255));
        }
        catch
        {
            return Colors.White;
        }
    }

    private static string FriendlyAppName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return "";
        }

        var id = appId.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("applemusic") || id.Contains("apple music")) return "Apple Music";
        if (id.Contains("zunemusic") || id.Contains("mediaplayer")) return "Media Player";
        if (id.Contains("msedge")) return "Edge";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("firefox")) return "Firefox";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("foobar")) return "foobar2000";
        if (id.Contains("musicbee")) return "MusicBee";
        if (id.Contains("tidal")) return "TIDAL";
        if (id.Contains("deezer")) return "Deezer";
        if (id.Contains("youtube")) return "YouTube Music";

        var name = appId;
        var bang = name.IndexOf('!');
        if (bang > 0)
        {
            name = name[..bang];
        }

        var underscore = name.IndexOf('_');
        if (underscore > 0)
        {
            name = name[..underscore];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
        {
            name = name[(dot + 1)..];
        }

        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "";
    }
}
