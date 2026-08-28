using TNDrop.Core;

public class BackupPruningTests
{
    [Fact]
    public void Auto_KeepsNewestSeven_DeletesOlder()
    {
        var files = Enumerable.Range(1, 9)
            .Select(d => $"auto-2026080{d}.zip")
            .ToList();

        var doomed = BackupPruning.SelectFilesToDelete(files);

        Assert.Equal(new[] { "auto-20260801.zip", "auto-20260802.zip" }, doomed.OrderBy(x => x));
    }

    [Fact]
    public void Auto_ExactlySeven_DeletesNothing()
    {
        var files = Enumerable.Range(1, 7).Select(d => $"auto-2026080{d}.zip");
        Assert.Empty(BackupPruning.SelectFilesToDelete(files));
    }

    [Fact]
    public void Safety_KeepsNewestThree()
    {
        var files = new[]
        {
            "safety-20260801-090000.zip",
            "safety-20260802-090000.zip",
            "safety-20260803-090000.zip",
            "safety-20260804-090000.zip",
        };
        Assert.Equal(new[] { "safety-20260801-090000.zip" }, BackupPruning.SelectFilesToDelete(files));
    }

    [Fact]
    public void Manual_NeverDeleted()
    {
        var files = Enumerable.Range(1, 30).Select(d => $"manual-202608{d:00}-120000.zip");
        Assert.Empty(BackupPruning.SelectFilesToDelete(files));
    }

    [Fact]
    public void MixedKinds_PrunedIndependently_UnknownNamesIgnored()
    {
        var files = new List<string> { "readme.txt", "desktop.ini" };
        files.AddRange(Enumerable.Range(1, 8).Select(d => $"auto-2026080{d}.zip"));
        files.AddRange(Enumerable.Range(1, 4).Select(d => $"safety-2026080{d}-090000.zip"));
        files.Add("manual-20260801-120000.zip");

        var doomed = BackupPruning.SelectFilesToDelete(files);

        Assert.Equal(
            new[] { "auto-20260801.zip", "safety-20260801-090000.zip" },
            doomed.OrderBy(x => x));
    }

    [Fact]
    public void Empty_ReturnsEmpty()
    {
        Assert.Empty(BackupPruning.SelectFilesToDelete(Array.Empty<string>()));
    }
}
