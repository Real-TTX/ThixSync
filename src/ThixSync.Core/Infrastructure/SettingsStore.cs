using System.Text.Json;
using System.Text.Json.Serialization;
using ThixSync.Core.Models;

namespace ThixSync.Core.Infrastructure;

/// <summary>
/// Reads and writes settings.json. Only the business layer uses this class. Writes go to a temporary file first and
/// replace the old one, so a crash never leaves half a file.
/// </summary>
internal static class SettingsStore
{
    private static readonly object Gate = new();
    private static AppSettings? _cache;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The settings (records: a change is a new object through <see cref="Update"/>).</summary>
    public static AppSettings Read()
    {
        lock (Gate)
        {
            return _cache ??= Load();
        }
    }

    /// <summary>Changes the settings in one step and saves them; returns the new settings.</summary>
    public static AppSettings Update(Func<AppSettings, AppSettings> change)
    {
        lock (Gate)
        {
            var settings = change(_cache ??= Load());
            Save(settings);
            _cache = settings;
            return settings;
        }
    }

    /// <summary>Drops the cache (tests).</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _cache = null;
        }
    }

    private static AppSettings Load()
    {
        var path = AppPaths.SettingsFile;
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<AppSettings>(stream, SerializerOptions) ?? new AppSettings();
        }
        catch (JsonException exception)
        {
            // Keep the broken file for the user instead of silently overwriting it.
            Log.Warning($"settings.json could not be read ({exception.Message}); using the defaults");
            File.Copy(path, path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
            return new AppSettings();
        }
    }

    private static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var path = AppPaths.SettingsFile;
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, SerializerOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
