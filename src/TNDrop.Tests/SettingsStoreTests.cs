using System;
using System.IO;
using TNDrop.Core;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var s = new SettingsStore(_dir).Load();
        Assert.Equal(EdgeSide.Left, s.Edge);
        Assert.Equal(40, s.HotZonePercent);
        Assert.Equal("ja", s.Language);
    }

    // v1.2 Task E: the four new settings' coded defaults.
    [Fact]
    public void Load_returns_defaults_for_the_new_v1_2_settings_when_file_missing()
    {
        var s = new SettingsStore(_dir).Load();
        Assert.False(s.PurgeUnpinnedOnRestart);
        Assert.Equal(500, s.HistoryCapacity);
        Assert.True(s.IndicatorEnabled);
        Assert.True(s.EdgeHintEnabled);
    }

    // v1.2 Task H: the pinned accordion's open state and click-to-paste. Both default ON -- the
    // accordion because a section the user has never touched must show its contents, click-to-paste
    // because it is the headline behavior of the release.
    [Fact]
    public void Load_returns_defaults_for_the_task_h_settings_when_file_missing()
    {
        var s = new SettingsStore(_dir).Load();
        Assert.True(s.PinnedExpanded);
        Assert.True(s.PasteOnClick);
    }

    [Fact]
    public void Save_then_load_roundtrips_the_task_h_settings()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { PinnedExpanded = false, PasteOnClick = false });
        var s = store.Load();
        Assert.False(s.PinnedExpanded);
        Assert.False(s.PasteOnClick);
    }

    // Backward compat, same contract as the Task E properties above: a settings.json written
    // before Task H has neither key and must load with the coded defaults, not false.
    [Fact]
    public void Load_fills_in_task_h_defaults_for_an_older_settings_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """
            {
              "Edge": "Right",
              "HistoryCapacity": 250,
              "IndicatorEnabled": false
            }
            """);

        var s = new SettingsStore(_dir).Load();
        Assert.Equal(EdgeSide.Right, s.Edge);   // old fields still read back
        Assert.Equal(250, s.HistoryCapacity);
        Assert.False(s.IndicatorEnabled);
        Assert.True(s.PinnedExpanded);
        Assert.True(s.PasteOnClick);
    }

    [Fact]
    public void Save_then_load_roundtrips()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { Edge = EdgeSide.Right, RetractDelayMs = 500, AutoDelete = AutoDeletePolicy.Days7 });
        var s = store.Load();
        Assert.Equal(EdgeSide.Right, s.Edge);
        Assert.Equal(500, s.RetractDelayMs);
        Assert.Equal(AutoDeletePolicy.Days7, s.AutoDelete);
    }

    [Fact]
    public void Save_then_load_roundtrips_the_new_v1_2_settings()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings
        {
            PurgeUnpinnedOnRestart = true,
            HistoryCapacity = 250,
            IndicatorEnabled = false,
        });
        var s = store.Load();
        Assert.True(s.PurgeUnpinnedOnRestart);
        Assert.Equal(250, s.HistoryCapacity);
        Assert.False(s.IndicatorEnabled);
    }

    // Backward compat: a settings.json written before Task E has none of the four new
    // properties. Deserialization must fill in the coded defaults rather than fail or null them
    // out -- the same contract every other AppSettings property already relies on.
    [Fact]
    public void Load_fills_in_v1_2_defaults_for_a_pre_task_e_settings_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """
            {
              "Edge": "Right",
              "HotZonePercent": 40,
              "Language": "ja"
            }
            """);

        var s = new SettingsStore(_dir).Load();
        Assert.Equal(EdgeSide.Right, s.Edge); // old field still reads back
        Assert.False(s.PurgeUnpinnedOnRestart);
        Assert.Equal(500, s.HistoryCapacity);
        Assert.True(s.IndicatorEnabled);
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(50, 100)]
    [InlineData(1000, 1000)]
    [InlineData(5000, 1000)]
    public void Load_clamps_an_out_of_range_history_capacity(int stored, int expected)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), $$"""
            { "HistoryCapacity": {{stored}} }
            """);

        var s = new SettingsStore(_dir).Load();
        Assert.Equal(expected, s.HistoryCapacity);
    }

    [Fact]
    public void Load_returns_defaults_when_file_corrupt()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{not json!!");
        var s = new SettingsStore(_dir).Load();
        Assert.Equal(EdgeSide.Left, s.Edge);
    }

    [Fact]
    public void Enum_is_serialized_as_string()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { Edge = EdgeSide.Right });
        Assert.Contains("\"Right\"", File.ReadAllText(Path.Combine(_dir, "settings.json")));
    }

    // v1.5: インジケーター色/透明度の新設定と、自動開始・スタイルの新デフォルト。
    [Fact]
    public void Load_returns_defaults_for_the_v1_5_settings_when_file_missing()
    {
        var s = new SettingsStore(_dir).Load();
        Assert.Equal("#5AC8FA", s.IndicatorColor);
        Assert.Equal(100, s.IndicatorOpacityPercent);
        Assert.True(s.AutoStartEnabled);            // v1.4 までは false だった
        Assert.False(s.AutoStartDefaultMigrated);   // 移行は App.OnStartup 側の責務
        Assert.Equal(IndicatorStyle.Bulge, s.IndicatorStyle);
    }

    [Fact]
    public void Load_clamps_indicator_opacity_into_range()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { IndicatorOpacityPercent = 29 });
        Assert.Equal(30, store.Load().IndicatorOpacityPercent);
        store.Save(new AppSettings { IndicatorOpacityPercent = 101 });
        Assert.Equal(100, store.Load().IndicatorOpacityPercent);
    }

    [Theory]
    [InlineData("xyz")]
    [InlineData("#12")]
    [InlineData("")]
    public void Load_falls_back_to_default_color_for_unparsable_hex(string bad)
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { IndicatorColor = bad });
        Assert.Equal(IndicatorPalette.DefaultColorHex, store.Load().IndicatorColor);
    }

    [Fact]
    public void Load_keeps_a_valid_non_preset_color()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { IndicatorColor = "#123456" });
        Assert.Equal("#123456", store.Load().IndicatorColor);
    }

    // 旧バージョンの settings.json (キーなし) はコード側デフォルトで埋まる。
    // ここで AutoStartEnabled が true で埋まっても、実際の有効化は移行関数 +
    // 起動時自己修復が担う (SettingsMigrationTests を参照)。
    [Fact]
    public void Load_fills_in_v1_5_defaults_for_an_older_settings_file()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """
            {
              "Edge": "Right",
              "IndicatorStyle": "Bar"
            }
            """);

        var s = new SettingsStore(_dir).Load();
        Assert.Equal(IndicatorStyle.Bar, s.IndicatorStyle); // 既存ユーザーのスタイルは尊重
        Assert.Equal("#5AC8FA", s.IndicatorColor);
        Assert.Equal(100, s.IndicatorOpacityPercent);
        Assert.False(s.AutoStartDefaultMigrated);
    }

    // v1.5 追補: シェルフのピン止め (ヘッダーのピンボタン)。既定 false、再起動後も保持。
    [Fact]
    public void Load_returns_default_shelf_pinned_false_when_file_missing()
    {
        Assert.False(new SettingsStore(_dir).Load().ShelfPinned);
    }

    [Fact]
    public void Save_then_load_roundtrips_shelf_pinned()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { ShelfPinned = true });
        Assert.True(store.Load().ShelfPinned);
    }

    // v1.6: 自動バックアップの有効化と最後の実行日時。
    [Fact]
    public void BackupSettings_RoundTrip()
    {
        var store = new SettingsStore(_dir);
        store.Save(new AppSettings { AutoBackupEnabled = false, LastAutoBackupDate = "2026-08-29" });

        var s = store.Load();
        Assert.False(s.AutoBackupEnabled);
        Assert.Equal("2026-08-29", s.LastAutoBackupDate);
    }

    [Fact]
    public void BackupSettings_Defaults()
    {
        var s = new AppSettings();
        Assert.True(s.AutoBackupEnabled);
        Assert.Equal("", s.LastAutoBackupDate);
    }
}
