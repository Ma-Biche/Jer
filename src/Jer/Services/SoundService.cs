using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Jer.Core;

namespace Jer.Services;

/// <summary>Soft chimes, synthesized at startup so Jer ships no audio files.</summary>
public sealed class SoundService
{
    private const uint SND_ASYNC = 0x0001;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    // Unmanaged copies: PlaySound(SND_ASYNC) keeps reading the buffer after the call returns.
    private readonly IntPtr _chime;
    private readonly IntPtr _ping;

    public SoundService()
    {
        _chime = ToUnmanaged(Synthesize(new[] { (0.00, 880.00), (0.16, 1318.51) }, 1.6));
        _ping = ToUnmanaged(Synthesize(new[] { (0.00, 1046.50), (0.11, 1567.98) }, 1.1));
    }

    /// <summary>Pomodoro finished.</summary>
    public void PlayChime() => Play(_chime);

    /// <summary>Reminders (to-do, water).</summary>
    public void PlayPing() => Play(_ping);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);

    private static void Play(IntPtr wav)
    {
        try
        {
            PlaySound(wav, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not play sound", ex);
        }
    }

    private static IntPtr ToUnmanaged(byte[] data)
    {
        var ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return ptr;
    }

    /// <summary>Bell-like tones: a few decaying harmonics per note, 16-bit mono PCM WAV.</summary>
    private static byte[] Synthesize((double Start, double Frequency)[] notes, double seconds)
    {
        const int rate = 44100;
        var count = (int)(rate * seconds);
        var samples = new double[count];
        foreach (var (start, frequency) in notes)
        {
            for (var i = 0; i < count; i++)
            {
                var t = i / (double)rate - start;
                if (t < 0)
                {
                    continue;
                }

                var attack = Math.Min(1.0, t / 0.004);
                var body = Math.Sin(2 * Math.PI * frequency * t) * Math.Exp(-t * 3.4);
                var shimmer = 0.35 * Math.Sin(2 * Math.PI * frequency * 2.0 * t) * Math.Exp(-t * 7.0);
                var air = 0.12 * Math.Sin(2 * Math.PI * frequency * 3.01 * t) * Math.Exp(-t * 11.0);
                samples[i] += attack * (body + shimmer + air);
            }
        }

        var peak = 1e-9;
        foreach (var s in samples)
        {
            peak = Math.Max(peak, Math.Abs(s));
        }

        // Fade out the tail to avoid a click.
        var fade = rate / 20;
        for (var i = 0; i < fade && i < count; i++)
        {
            samples[count - 1 - i] *= i / (double)fade;
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.ASCII);
        var dataBytes = count * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);
        foreach (var s in samples)
        {
            w.Write((short)Math.Round(s / peak * 0.32 * short.MaxValue));
        }

        w.Flush();
        return ms.ToArray();
    }
}
