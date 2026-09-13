using System.IO;
using System.Text.Json;

namespace CrimsonTrainer.Settings;

/// <summary>Persisted user preferences: hotkeys and the last values typed into fields.</summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CrimsonTrainer", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>binding id → serialized hotkey.</summary>
    public Dictionary<string, string> Hotkeys { get; set; } = new();

    /// <summary>field id → last text.</summary>
    public Dictionary<string, string> Values { get; set; } = new();

    /// <summary>Saved teleport waypoints.</summary>
    public List<WaypointSetting> Waypoints { get; set; } = new();

    /// <summary>Combat attribute indices (beyond Attack/Defense) that "Max Attack &amp; Defense" also maxes.</summary>
    public List<int> MaxedAttributes { get; set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings: start fresh rather than refuse to launch.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; losing preferences is not worth crashing over.
        }
    }
}

public sealed class WaypointSetting
{
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}
