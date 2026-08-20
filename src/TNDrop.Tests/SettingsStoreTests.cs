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
}
