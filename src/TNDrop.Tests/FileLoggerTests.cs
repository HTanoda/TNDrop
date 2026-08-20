using System;
using System.IO;
using Xunit;
using TNDrop.Services;

namespace TNDrop.Tests;

public class FileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Info_writes_formatted_line_to_dated_file()
    {
        var t = new DateTime(2026, 8, 20, 9, 5, 3);
        var log = new FileLogger(_dir, () => t);
        log.Info("store", "loaded 12 items");
        var text = File.ReadAllText(Path.Combine(_dir, "app-20260820.log"));
        Assert.Contains("2026-08-20 09:05:03 [INFO] store: loaded 12 items", text);
    }

    [Fact]
    public void Rotates_file_when_date_changes()
    {
        var t = new DateTime(2026, 8, 20, 23, 59, 0);
        var log = new FileLogger(_dir, () => t);
        log.Info("a", "day1");
        t = t.AddHours(1);
        log.Info("a", "day2");
        Assert.True(File.Exists(Path.Combine(_dir, "app-20260820.log")));
        Assert.True(File.Exists(Path.Combine(_dir, "app-20260821.log")));
    }

    [Fact]
    public void Error_includes_exception_type_and_message()
    {
        var log = new FileLogger(_dir, () => new DateTime(2026, 8, 20, 1, 0, 0));
        log.Error("clip", "capture failed", new InvalidOperationException("boom"));
        var text = File.ReadAllText(Path.Combine(_dir, "app-20260820.log"));
        Assert.Contains("[ERROR] clip: capture failed", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("boom", text);
    }
}
