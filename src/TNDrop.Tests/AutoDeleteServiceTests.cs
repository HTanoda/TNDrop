using System;
using System.IO;
using TNDrop.Core;
using TNDrop.Services;

public class AutoDeleteServiceTests
{
    [Theory]
    [InlineData(AutoDeletePolicy.Off, null)]
    [InlineData(AutoDeletePolicy.Hours1, 1.0)]
    [InlineData(AutoDeletePolicy.Days7, 168.0)]
    public void ToAge_maps_policy(AutoDeletePolicy p, double? hours)
    {
        var age = AutoDeleteService.ToAge(p);
        if (hours is null) Assert.Null(age);
        else Assert.Equal(TimeSpan.FromHours(hours.Value), age);
    }

    [Fact]
    public void RunOnce_purges_and_reports_count()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
        try
        {
            var store = new ItemStore(dir);
            var old = new ClipItem { Kind = ClipKind.Text, Text = "x",
                CreatedAtUtc = DateTime.UtcNow.AddHours(-2), ContentHash = 1 };
            store.TryAdd(old);
            var svc = new AutoDeleteService(store, () => AutoDeletePolicy.Hours1);
            Assert.Equal(1, svc.RunOnce());
            Assert.Empty(store.Items);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
