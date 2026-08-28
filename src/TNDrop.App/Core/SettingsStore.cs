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
    private readonly string _tmpPath;

    public SettingsStore(string dataDir)
    {
        _dataDir = dataDir;
        _settingsPath = Path.Combine(_dataDir, "settings.json");
        _tmpPath = Path.Combine(_dataDir, "settings.tmp");
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

    /// <summary>
    /// settings.json を書き出す。
    ///
    /// <para>v1.6 最終レビュー修正: 宛先へ直接 <c>File.WriteAllText</c> せず、同じフォルダの
    /// <c>settings.tmp</c> に全部書いてから差し替える (<see cref="ItemStore.Save"/> の
    /// items.tmp → File.Replace と同じ形)。v1.6 の日次自動バックアップは
    /// <c>LastAutoBackupDate</c> の更新のために**このファイルを毎日書き換える**ので、
    /// 書き込み途中のクラッシュ (電源断・強制終了) で settings.json が切り詰められる機会が
    /// 増えた。直接書きだと、その中断が「壊れた settings.json」= 全設定の消失 (Load は壊れた
    /// JSON を既定値へフォールバックする) を意味する。差し替えなら、落ちても settings.json は
    /// 常に「前回の完全な内容」か「今回の完全な内容」のどちらかになる。</para>
    /// </summary>
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

            File.WriteAllText(_tmpPath, json, new System.Text.UTF8Encoding(false));

            if (File.Exists(_settingsPath))
            {
                File.Replace(_tmpPath, _settingsPath, null);
            }
            else
            {
                File.Move(_tmpPath, _settingsPath);
            }
        }
        catch
        {
            FileLogger.Instance?.Error("settings", "Failed to save settings");
        }
    }
}
