using System;
using System.IO;
using System.Text;

namespace Jer.Core;

/// <summary>Reads and writes Jer's JSON files in %APPDATA%\Jer. Writes are atomic.</summary>
public static class JsonStore
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jer");

    /// <summary>When true (demo mode) nothing is written to disk.</summary>
    public static bool ReadOnly { get; set; }

    public static T Load<T>(string fileName) where T : class, new()
    {
        var path = Path.Combine(DataDirectory, fileName);
        try
        {
            if (File.Exists(path))
            {
                return Json.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)) ?? new T();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not read {fileName}; starting with defaults", ex);
            try
            {
                File.Copy(path, path + ".corrupt", overwrite: true);
            }
            catch
            {
                // Keeping a copy is best effort.
            }
        }

        return new T();
    }

    public static void Save<T>(string fileName, T value)
    {
        if (ReadOnly)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(DataDirectory);
            var path = Path.Combine(DataDirectory, fileName);
            var temp = path + ".tmp";
            File.WriteAllText(temp, Json.Serialize(value), Utf8NoBom);
            FileUtil.MoveOverwrite(temp, path);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save {fileName}", ex);
        }
    }
}
