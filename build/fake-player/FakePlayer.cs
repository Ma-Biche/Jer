using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Windows.Forms;
using Windows.Media;
using Windows.Storage.Streams;

namespace FakePlayer;

/// <summary>
/// A pretend music app for CI. It registers with the Windows media controls (the same API browsers use),
/// so Jer sees it exactly like Spotify or YouTube in a browser, but it never plays any audio.
/// Like Spotify, it publishes the song first and its length a moment later.
/// Reacts to Jer's Play/Pause/Next/Previous buttons; also reads "next", "pause", "play" and "quit" from stdin.
/// Writes what it does to fake-player.log next to the exe.
/// </summary>
internal static class Program
{
    private static readonly (string Title, string Artist, string Album, double Seconds, double StartAt, Color Color)[] Tracks =
    {
        ("Yellow", "Coldplay", "Parachutes", 267, 30, Color.FromArgb(240, 200, 40)),
        ("Bohemian Rhapsody", "Queen", "A Night At The Opera", 355, 2, Color.FromArgb(200, 60, 60)),
        ("Separuh Aku", "NOAH", "Seperti Seharusnya", 266, 20, Color.FromArgb(60, 120, 220)),
    };

    private static SystemMediaTransportControls? _smtc;
    private static Form? _form;
    private static int _track;
    private static bool _playing = true;
    private static TimeSpan _position;
    private static DateTime _positionAt;
    private static string _logPath = "fake-player.log";

    [ComImport]
    [Guid("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface ISystemMediaTransportControlsInterop
    {
        [return: MarshalAs(UnmanagedType.IInspectable)]
        object GetForWindow(IntPtr appWindow, [In] ref Guid riid);
    }

    [STAThread]
    private static int Main()
    {
        _logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fake-player.log");
        File.WriteAllText(_logPath, "");
        try
        {
            _form = new Form
            {
                Text = "Fake player",
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = new Point(-3000, -3000),
                Size = new Size(120, 80),
                FormBorderStyle = FormBorderStyle.FixedToolWindow,
            };
            _form.Load += (_, _) => Start();
            new Thread(ReadCommands) { IsBackground = true }.Start();
            Application.Run(_form);
            return 0;
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            return 1;
        }
    }

    private static void Start()
    {
        var factory = (ISystemMediaTransportControlsInterop)WindowsRuntimeMarshal.GetActivationFactory(typeof(SystemMediaTransportControls));
        var iid = new Guid("99FA3FF4-1742-42A6-902E-087D41F965EC"); // ISystemMediaTransportControls
        _smtc = (SystemMediaTransportControls)factory.GetForWindow(_form!.Handle, ref iid);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.ButtonPressed += (_, e) => _form.BeginInvoke(new Action(() => OnButton(e.Button)));
        Log("Registered with the media controls");
        ShowTrack(0);
    }

    private static void OnButton(SystemMediaTransportControlsButton button)
    {
        Log("Button: " + button);
        switch (button)
        {
            case SystemMediaTransportControlsButton.Next:
                ShowTrack((_track + 1) % Tracks.Length);
                break;
            case SystemMediaTransportControlsButton.Previous:
                ShowTrack((_track + Tracks.Length - 1) % Tracks.Length);
                break;
            case SystemMediaTransportControlsButton.Pause:
                SetPlaying(false);
                break;
            case SystemMediaTransportControlsButton.Play:
                SetPlaying(true);
                break;
        }
    }

    private static void ShowTrack(int index)
    {
        _track = index;
        var track = Tracks[index];
        var smtc = _smtc!;
        var display = smtc.DisplayUpdater;
        display.ClearAll();
        display.Type = MediaPlaybackType.Music;
        display.MusicProperties.Title = track.Title;
        display.MusicProperties.Artist = track.Artist;
        display.MusicProperties.AlbumTitle = track.Album;
        display.Thumbnail = RandomAccessStreamReference.CreateFromStream(Artwork(track.Color, track.Title.Substring(0, 1)));
        display.Update();
        smtc.PlaybackStatus = _playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;

        // Spotify-style: the song arrives first with no length; the timeline follows a moment later.
        smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties());
        _position = TimeSpan.FromSeconds(track.StartAt);
        _positionAt = DateTime.Now;
        Log($"Now playing: {track.Artist} - {track.Title} ({track.Album})");
        var timer = new System.Windows.Forms.Timer { Interval = 1500 };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (_track == index)
            {
                PublishTimeline();
                Log($"Timeline: {track.Seconds}s, at {CurrentPosition().TotalSeconds:0.0}s");
            }
        };
        timer.Start();
    }

    private static TimeSpan CurrentPosition() => _playing ? _position + (DateTime.Now - _positionAt) : _position;

    private static void SetPlaying(bool playing)
    {
        _position = CurrentPosition();
        _positionAt = DateTime.Now;
        _playing = playing;
        _smtc!.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        PublishTimeline();
    }

    private static void PublishTimeline()
    {
        var length = TimeSpan.FromSeconds(Tracks[_track].Seconds);
        _smtc!.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            EndTime = length,
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = length,
            Position = CurrentPosition(),
        });
    }

    private static IRandomAccessStream Artwork(Color color, string letter)
    {
        using var bitmap = new Bitmap(160, 160);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(color);
            using var font = new Font("Segoe UI", 64, FontStyle.Bold);
            g.DrawString(letter, font, Brushes.White, 30, 20);
        }

        var stream = new InMemoryRandomAccessStream();
        using (var writer = stream.AsStreamForWrite())
        {
            bitmap.Save(writer, ImageFormat.Png);
            writer.Flush();
        }

        stream.Seek(0);
        return stream;
    }

    private static void ReadCommands()
    {
        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            var command = line.Trim().ToLowerInvariant();
            _form?.BeginInvoke(new Action(() =>
            {
                switch (command)
                {
                    case "next":
                        ShowTrack((_track + 1) % Tracks.Length);
                        break;
                    case "pause":
                        SetPlaying(false);
                        break;
                    case "play":
                        SetPlaying(true);
                        break;
                    case "quit":
                        Application.Exit();
                        break;
                }
            }));
        }
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(_logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }
}
