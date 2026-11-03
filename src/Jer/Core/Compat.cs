using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace Jer.Core;

/// <summary><c>Math.Clamp</c>, which .NET Framework doesn't have.</summary>
public static class MathEx
{
    public static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    public static long Clamp(long value, long min, long max) => value < min ? min : value > max ? max : value;

    public static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

    public static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
}

public static class FileUtil
{
    /// <summary>Moves <paramref name="source"/> over <paramref name="destination"/>, replacing it in one step when possible.</summary>
    public static void MoveOverwrite(string source, string destination)
    {
        if (!File.Exists(destination))
        {
            File.Move(source, destination);
            return;
        }

        try
        {
            File.Replace(source, destination, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException or UnauthorizedAccessException)
        {
            // Some drives (FAT, network shares) can't replace in place.
            File.Copy(source, destination, overwrite: true);
            File.Delete(source);
        }
    }
}

/// <summary>Facts about the running copy of Jer.</summary>
public static class AppInfo
{
    private static string? _executablePath;

    public static Version Version { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    /// <summary>"2.0.0".</summary>
    public static string VersionText { get; } = Version.ToString(3);

    public static string ExecutablePath => _executablePath ??= FindExecutablePath();

    private static string FindExecutablePath()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var path = process.MainModule?.FileName;
            if (!string.IsNullOrEmpty(path))
            {
                return path!;
            }
        }
        catch (Exception)
        {
            // Fall back to the assembly location below.
        }

        return Assembly.GetEntryAssembly()?.Location ?? "Jer.exe";
    }
}
