using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jer.Core;

namespace Jer.Services;

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed class Lyrics
{
    public static readonly Lyrics None = new(Array.Empty<LyricLine>(), synced: false, instrumental: false);

    public Lyrics(IReadOnlyList<LyricLine> lines, bool synced, bool instrumental)
    {
        Lines = lines;
        IsSynced = synced;
        IsInstrumental = instrumental;
    }

    public IReadOnlyList<LyricLine> Lines { get; }
    public bool IsSynced { get; }
    public bool IsInstrumental { get; }
    public bool IsEmpty => Lines.Count == 0 && !IsInstrumental;
}

/// <summary>
/// Synced lyrics from LRCLIB (lrclib.net), a free community library.
/// Only the title, artist, album and length are sent, and only while lyrics are switched on.
/// </summary>
public sealed partial class LyricsService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly Dictionary<string, Lyrics> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record LrcLibTrack(
        long Id,
        string? TrackName,
        string? ArtistName,
        string? AlbumName,
        double Duration,
        bool Instrumental,
        string? PlainLyrics,
        string? SyncedLyrics);

    public void Seed(string title, string artist, Lyrics lyrics) => _cache[Key(title, artist)] = lyrics;

    public async Task<Lyrics> GetAsync(string title, string artist, string album, TimeSpan duration, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Lyrics.None;
        }

        var key = Key(title, artist);
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        Lyrics result;
        try
        {
            result = await FetchAsync(title, artist, album, duration, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"Lyrics lookup failed for \"{artist} - {title}\"", ex);
            return Lyrics.None; // Not cached: a network hiccup shouldn't hide lyrics for the whole session.
        }

        _cache[key] = result;
        return result;
    }

    /// <summary>Parses LRC text ("[01:02.34] line") into time-ordered lines.</summary>
    public static IReadOnlyList<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        var offset = TimeSpan.Zero;
        foreach (var raw in lrc.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var offsetMatch = OffsetTag().Match(line);
            if (offsetMatch.Success)
            {
                // Positive offset = lyrics appear earlier.
                offset = TimeSpan.FromMilliseconds(-int.Parse(offsetMatch.Groups[1].Value, CultureInfo.InvariantCulture));
                continue;
            }

            var stamps = new List<TimeSpan>();
            var index = 0;
            while (true)
            {
                var m = TimeTag().Match(line, index);
                if (!m.Success || m.Index != index)
                {
                    break;
                }

                var minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var fraction = m.Groups[3].Success ? m.Groups[3].Value : "0";
                var ms = fraction.Length switch
                {
                    1 => int.Parse(fraction, CultureInfo.InvariantCulture) * 100,
                    2 => int.Parse(fraction, CultureInfo.InvariantCulture) * 10,
                    _ => int.Parse(fraction[..3], CultureInfo.InvariantCulture),
                };
                stamps.Add(new TimeSpan(0, 0, minutes, seconds, ms));
                index = m.Index + m.Length;
            }

            if (stamps.Count == 0)
            {
                continue; // Metadata such as [ar:...] or plain text.
            }

            var text = line[index..].Trim();
            foreach (var stamp in stamps)
            {
                var time = stamp + offset;
                lines.Add(new LyricLine(time < TimeSpan.Zero ? TimeSpan.Zero : time, text));
            }
        }

        return lines.OrderBy(l => l.Time).ToList();
    }

    /// <summary>Index of the line being sung at <paramref name="position"/>, or -1 before the first line.</summary>
    public static int FindLine(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int lo = 0, hi = lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].Time <= position)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    private static string Key(string title, string artist) => $"{artist.Trim()}\u001f{title.Trim()}";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://lrclib.net/"),
            Timeout = TimeSpan.FromSeconds(12),
        };
        var version = typeof(LyricsService).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Jer/{version} (+https://github.com/Ma-Biche/Jer)");
        return client;
    }

    private async Task<Lyrics> FetchAsync(string title, string artist, string album, TimeSpan duration, CancellationToken ct)
    {
        // 1. Exact match (needs album and duration).
        if (!string.IsNullOrWhiteSpace(album) && duration > TimeSpan.Zero && !string.IsNullOrWhiteSpace(artist))
        {
            var url = $"api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}" +
                      $"&album_name={Uri.EscapeDataString(album)}&duration={(int)Math.Round(duration.TotalSeconds)}";
            var exact = await GetTrackAsync(url, ct);
            if (exact is not null && (exact.Instrumental || !string.IsNullOrWhiteSpace(exact.SyncedLyrics)))
            {
                return ToLyrics(exact);
            }
        }

        // 2. Search, then a cleaned-up search for video-style titles ("Artist - Song (Official Video)").
        var queries = new List<(string Track, string Artist)> { (title, artist) };
        var (cleanTrack, cleanArtist) = CleanUp(title, artist);
        if (!string.Equals(cleanTrack, title, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(cleanArtist, artist, StringComparison.OrdinalIgnoreCase))
        {
            queries.Add((cleanTrack, cleanArtist));
        }

        LrcLibTrack? plainOnly = null;
        foreach (var (track, by) in queries)
        {
            var url = string.IsNullOrWhiteSpace(by)
                ? $"api/search?q={Uri.EscapeDataString(track)}"
                : $"api/search?track_name={Uri.EscapeDataString(track)}&artist_name={Uri.EscapeDataString(by)}";
            var results = await SearchAsync(url, ct);
            var best = Pick(results, duration, requireSynced: true);
            if (best is not null)
            {
                return ToLyrics(best);
            }

            plainOnly ??= Pick(results, duration, requireSynced: false);
        }

        return plainOnly is not null ? ToLyrics(plainOnly) : Lyrics.None;
    }

    private static LrcLibTrack? Pick(IReadOnlyList<LrcLibTrack> results, TimeSpan duration, bool requireSynced)
    {
        var candidates = results.Where(r => requireSynced
            ? !string.IsNullOrWhiteSpace(r.SyncedLyrics)
            : r.Instrumental || !string.IsNullOrWhiteSpace(r.PlainLyrics));
        if (duration > TimeSpan.Zero)
        {
            return candidates
                .Where(r => r.Duration <= 0 || Math.Abs(r.Duration - duration.TotalSeconds) <= 4)
                .OrderBy(r => r.Duration <= 0 ? 99 : Math.Abs(r.Duration - duration.TotalSeconds))
                .FirstOrDefault();
        }

        return candidates.FirstOrDefault();
    }

    private static Lyrics ToLyrics(LrcLibTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.SyncedLyrics))
        {
            var lines = ParseLrc(track.SyncedLyrics);
            if (lines.Count > 0)
            {
                return new Lyrics(lines, synced: true, instrumental: false);
            }
        }

        if (track.Instrumental)
        {
            return new Lyrics(Array.Empty<LyricLine>(), synced: false, instrumental: true);
        }

        var plain = (track.PlainLyrics ?? "")
            .Replace("\r", "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .Select(l => new LyricLine(TimeSpan.Zero, l))
            .ToList();
        return new Lyrics(plain, synced: false, instrumental: false);
    }

    /// <summary>GET with a couple of polite retries: LRCLIB answers 503 when it is busy.</summary>
    private static async Task<HttpResponseMessage> GetWithRetryAsync(string url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var response = await Http.GetAsync(url, ct);
                var code = (int)response.StatusCode;
                if (attempt < 2 && code is 429 or 502 or 503 or 504)
                {
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), ct);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5 * (attempt + 1)), ct);
            }
            catch (TaskCanceledException) when (attempt < 2 && !ct.IsCancellationRequested)
            {
                // Request timed out; try again.
            }
        }
    }

    private static async Task<LrcLibTrack?> GetTrackAsync(string url, CancellationToken ct)
    {
        using var response = await GetWithRetryAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<LrcLibTrack>(Json, ct);
    }

    private static async Task<IReadOnlyList<LrcLibTrack>> SearchAsync(string url, CancellationToken ct)
    {
        using var response = await GetWithRetryAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return Array.Empty<LrcLibTrack>();
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<LrcLibTrack>>(Json, ct) ?? new List<LrcLibTrack>();
    }

    private static (string Track, string Artist) CleanUp(string title, string artist)
    {
        var track = title;
        var by = artist;

        // "Artist - Title" uploads, often from a channel named "Artist - Topic" or "ArtistVEVO".
        var dash = track.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            by = track[..dash];
            track = track[(dash + 3)..];
        }

        track = Noise().Replace(track, "");
        track = Featuring().Replace(track, "");
        by = by.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase);
        by = Regex.Replace(by, "VEVO$", "", RegexOptions.IgnoreCase);
        return (track.Trim(' ', '-', '|'), by.Trim());
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]$", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*(official|video|audio|lyric|lyrics|visualizer|mv|m/v|hd|4k|remaster(ed)?|live)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s+(ft\.?|feat\.?|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();
}
