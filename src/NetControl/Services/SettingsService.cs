using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetControl.Models;

namespace NetControl.Services;

/// <summary>Loads and saves application settings as JSON in %APPDATA%\NetControl.</summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public AppSettings Settings { get; private set; } = new();

    public event Action<AppSettings> SettingsChanged;

    public void Load()
    {
        try
        {
            if (File.Exists(AppSettings.FilePath))
            {
                var json = File.ReadAllText(AppSettings.FilePath);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
            }
        }
        catch
        {
            // Corrupted settings fall back to defaults; the app stays usable.
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppSettings.FilePath));
            File.WriteAllText(AppSettings.FilePath, JsonSerializer.Serialize(Settings, JsonOpts));
        }
        catch
        {
            // Disk errors must not crash the app; changes will be retried on next save.
        }
        SettingsChanged?.Invoke(Settings);
    }

    public void Update(Action<AppSettings> change)
    {
        change(Settings);
        Save();
    }
}
