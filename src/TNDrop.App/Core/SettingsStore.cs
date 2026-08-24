using System;
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
            var settings = JsonSerializer.Deserialize<AppSettings>(json, options) ?? new AppSettings();

            // Clamp regardless of where the value came from: a settings.json hand-edited (or
            // left over from a build with a different allowed range) could carry a HistoryCapacity
            // outside [Min,Max]. TrimUnpinnedToCapacity itself takes whatever int it's given
            // as-is (tests rely on that for small capacities), so this is the one place a
            // corrupt/out-of-range persisted value gets corrected before anything reads it.
            settings.HistoryCapacity = Math.Clamp(
                settings.HistoryCapacity, AppSettings.MinHistoryCapacity, AppSettings.MaxHistoryCapacity);

            // v1.5: 同じ「どこから来た値でも読む前に直す」方針。透明度は範囲クランプ、
            // 色はパース不能ならデフォルトへ (以降の読み手は常にパース可能とみなせる)。
            settings.IndicatorOpacityPercent = Math.Clamp(
                settings.IndicatorOpacityPercent,
                AppSettings.MinIndicatorOpacityPercent, AppSettings.MaxIndicatorOpacityPercent);
            if (!IndicatorPalette.TryParseHex(settings.IndicatorColor, out _))
            {
                settings.IndicatorColor = IndicatorPalette.DefaultColorHex;
            }

            return settings;
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
