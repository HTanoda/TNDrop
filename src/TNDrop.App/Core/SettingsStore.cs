using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TNDrop.Services;

namespace TNDrop.Core;

public sealed class SettingsStore
{
    private readonly string _dataDir;
    private readonly string _settingsPath;

    public SettingsStore(string dataDir)
    {
        _dataDir = dataDir;
        _settingsPath = Path.Combine(_dataDir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            var settings = JsonSerializer.Deserialize<AppSettings>(json, options);
            return settings ?? new AppSettings();
        }
        catch
        {
            FileLogger.Instance?.Warn("settings", "settings.json was corrupt; using defaults");
            return new AppSettings();
        }
    }

    public void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(_dataDir);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter() }
            };
            var json = JsonSerializer.Serialize(s, options);
            File.WriteAllText(_settingsPath, json, new System.Text.UTF8Encoding(false));
        }
        catch
        {
            FileLogger.Instance?.Error("settings", "Failed to save settings");
        }
    }
}
