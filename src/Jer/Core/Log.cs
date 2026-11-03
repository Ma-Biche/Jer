using System;
using System.IO;
using System.Text;

namespace Jer.Core;

/// <summary>Small file logger. Writes to %APPDATA%\Jer\jer.log and never throws.</summary>
public static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(JsonStore.DataDirectory, "jer.log");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message, Exception? ex = null) => Write("WARN", Format(message, ex));

    public static void Error(string message, Exception? ex = null) => Write("ERROR", Format(message, ex));

    private static string Format(string message, Exception? ex) => ex is null ? message : $"{message}: {ex}";

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(JsonStore.DataDirectory);
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    FileUtil.MoveOverwrite(FilePath, FilePath + ".old");
                }

                File.AppendAllText(
                    FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
