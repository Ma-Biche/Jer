using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jer.Core;

/// <summary>Reads and writes Jer's JSON files in %APPDATA%\Jer. Writes are atomic.</summary>
public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

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
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
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
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save {fileName}", ex);
        }
    }
}
