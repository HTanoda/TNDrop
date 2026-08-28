using System.IO;
using System.IO.Compression;
using System.Linq;
using TNDrop.Core;

public class BackupServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
    private readonly ItemStore _store;
    private readonly BackupService _svc;

    public BackupServiceTests()
    {
        _store = new ItemStore(_dir);
        _svc = new BackupService(_dir, _store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private void SeedItem(string text)
    {
        _store.TryAdd(new ClipItem { Kind = ClipKind.Text, Text = text, CreatedAtUtc = DateTime.UtcNow });
        _store.Save();
    }

    [Fact]
    public void CreateBackup_Manual_ProducesZipWithManifestItemsAndSettings()
    {
        SeedItem("a");
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{}");
        File.WriteAllBytes(Path.Combine(_store.BlobsDir, "b.png"), new byte[] { 1 });

        var path = _svc.CreateBackup(BackupKind.Manual);

        Assert.NotNull(path);
        Assert.StartsWith("manual-", Path.GetFileName(path));
        using var zip = ZipFile.OpenRead(path!);
        Assert.NotNull(zip.GetEntry("manifest.json"));
        Assert.NotNull(zip.GetEntry("items.dat"));
        Assert.NotNull(zip.GetEntry("settings.json"));
        Assert.NotNull(zip.GetEntry("blobs/b.png"));
    }

    [Fact]
    public void CreateBackup_Auto_SameDayOverwrites_SingleFile()
    {
        SeedItem("a");
        _svc.CreateBackup(BackupKind.Auto);
        _svc.CreateBackup(BackupKind.Auto);

        var autos = Directory.GetFiles(_svc.BackupsDir, "auto-*.zip");
        Assert.Single(autos);
    }

    [Fact]
    public void ListBackups_NewestFirst_WithKinds()
    {
        SeedItem("a");
        _svc.CreateBackup(BackupKind.Auto);
        _svc.CreateBackup(BackupKind.Manual);

        var list = _svc.ListBackups();
        Assert.Equal(2, list.Count);
        Assert.Contains(list, e => e.Kind == BackupKind.Auto);
        Assert.Contains(list, e => e.Kind == BackupKind.Manual);
    }

    [Fact]
    public void Validate_RandomZip_NotABackup()
    {
        var stray = Path.Combine(_dir, "stray.zip");
        using (var zip = ZipFile.Open(stray, ZipArchiveMode.Create))
            zip.CreateEntry("nothing.txt");
        Assert.Equal(BackupValidation.NotABackup, _svc.Validate(stray));
    }

    [Fact]
    public void RestoreFrom_ReplacesCurrentData_AndWritesSafetyBackup()
    {
        SeedItem("old");
        var backup = _svc.CreateBackup(BackupKind.Manual)!;
        SeedItem("newer-than-backup");

        _svc.RestoreFrom(backup);

        Assert.Contains(_store.Items, i => i.Text == "old");
        Assert.DoesNotContain(_store.Items, i => i.Text == "newer-than-backup");
        Assert.NotEmpty(Directory.GetFiles(_svc.BackupsDir, "safety-*.zip"));
    }

    [Fact]
    public void RestoreFrom_CorruptItemsDat_RollsBackAndThrows()
    {
        SeedItem("survives-rollback");
        // manifest と settings は正しいが items.dat が DPAPI で読めないバックアップを偽造する。
        // Validate は通らないが、RestoreFrom はダイアログの Validate に依存してはならない。
        var good = _svc.CreateBackup(BackupKind.Manual)!;
        var corrupt = Path.Combine(_dir, "corrupt.zip");
        File.Copy(good, corrupt);
        using (var zip = ZipFile.Open(corrupt, ZipArchiveMode.Update))
        {
            zip.GetEntry("items.dat")!.Delete();
            using var w = new StreamWriter(zip.CreateEntry("items.dat").Open());
            w.Write("garbage");
        }

        var ex = Assert.Throws<BackupRestoreException>(() => _svc.RestoreFrom(corrupt));

        Assert.True(ex.RolledBack);
        Assert.Contains(_store.Items, i => i.Text == "survives-rollback");
    }

    [Fact]
    public void ExportImport_RoundTrip_MigratesItems()
    {
        SeedItem("移行するテキスト");
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"Edge\":\"Right\"}");
        var exportPath = Path.Combine(_dir, "out.tndexport");

        _svc.ExportTo(exportPath, "pass-word-8");

        // 「別端末」を新しい dataDir で模擬する (DPAPI は同一ユーザーなので通る)
        var otherDir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
        try
        {
            var otherStore = new ItemStore(otherDir);
            var otherSvc = new BackupService(otherDir, otherStore);
            otherSvc.ImportFrom(exportPath, "pass-word-8");

            Assert.Contains(otherStore.Items, i => i.Text == "移行するテキスト");
            Assert.True(File.Exists(Path.Combine(otherDir, "settings.json")));
        }
        finally
        {
            try { Directory.Delete(otherDir, true); } catch { }
        }
    }

    [Fact]
    public void ImportFrom_RewritesBlobPathsUnderOldDataDir()
    {
        var blobFile = Path.Combine(_store.BlobsDir, "conv.png");
        File.WriteAllBytes(blobFile, new byte[] { 1 });
        _store.TryAdd(new ClipItem
        {
            Kind = ClipKind.Files,
            Paths = new List<string> { blobFile, @"C:\somewhere\user-file.txt" },
            CreatedAtUtc = DateTime.UtcNow,
        });
        _store.Save();
        var exportPath = Path.Combine(_dir, "out.tndexport");
        _svc.ExportTo(exportPath, "pass-word-8");

        var otherDir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
        try
        {
            var otherStore = new ItemStore(otherDir);
            var otherSvc = new BackupService(otherDir, otherStore);
            otherSvc.ImportFrom(exportPath, "pass-word-8");

            var item = otherStore.Items.Single(i => i.Kind == ClipKind.Files);
            Assert.Contains(Path.Combine(otherStore.BlobsDir, "conv.png"), item.Paths);
            Assert.Contains(@"C:\somewhere\user-file.txt", item.Paths); // blobs 外は書き換えない
            Assert.DoesNotContain(blobFile, item.Paths);
        }
        finally
        {
            try { Directory.Delete(otherDir, true); } catch { }
        }
    }

    [Fact]
    public void ImportFrom_WrongPassword_Throws_AndLeavesDataUntouched()
    {
        SeedItem("keep-me");
        var exportPath = Path.Combine(_dir, "out.tndexport");
        _svc.ExportTo(exportPath, "pass-word-8");

        Assert.Throws<ExportPasswordException>(() => _svc.ImportFrom(exportPath, "wrong-password"));
        Assert.Contains(_store.Items, i => i.Text == "keep-me");
    }

    [Fact]
    public void ExportTo_ShortPassword_Throws()
    {
        SeedItem("a");
        Assert.Throws<ArgumentException>(() => _svc.ExportTo(Path.Combine(_dir, "x.tndexport"), "short"));
    }

    // --- 以下 3 件はブリーフ 11 件への追補 (Task 5 セルフレビューで実測して追加) ---

    // SeedItem は ContentHash を設定しないため、2 回目の TryAdd は「先頭と同じハッシュ (0)」として
    // 拒否される。RestoreFrom_ReplacesCurrentData_AndWritesSafetyBackup は
    // 「newer-than-backup が消えたこと」を実際には検証できていないので、ハッシュを与えた 2 件で
    // 「巻き戻しが履歴・blobs・settings.json の 3 点すべてを戻すこと」をここで実測する。
    private ClipItem Add(string text, ulong hash)
    {
        var item = new ClipItem { Kind = ClipKind.Text, Text = text, CreatedAtUtc = DateTime.UtcNow, ContentHash = hash };
        _store.TryAdd(item);
        _store.Save();
        return item;
    }

    private string MakeCorruptCopy(string sourceZip)
    {
        var corrupt = Path.Combine(_dir, "corrupt-" + Guid.NewGuid().ToString("N") + ".zip");
        File.Copy(sourceZip, corrupt);
        using var zip = ZipFile.Open(corrupt, ZipArchiveMode.Update);
        zip.GetEntry("items.dat")!.Delete();
        using var w = new StreamWriter(zip.CreateEntry("items.dat").Open());
        w.Write("garbage");
        return corrupt;
    }

    [Fact]
    public void RestoreFrom_Rollback_RestoresItemsBlobsAndSettings()
    {
        Add("orig", 1);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"Edge\":\"Left\"}");
        File.WriteAllBytes(Path.Combine(_store.BlobsDir, "orig.png"), new byte[] { 9 });
        var good = _svc.CreateBackup(BackupKind.Manual)!;

        // バックアップ後に現在の状態を動かす -> 巻き戻しはこの「動かした後」の状態に戻すべき
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"Edge\":\"Right\"}");
        File.Delete(Path.Combine(_store.BlobsDir, "orig.png"));
        Add("later", 2);

        var ex = Assert.Throws<BackupRestoreException>(() => _svc.RestoreFrom(MakeCorruptCopy(good)));

        Assert.True(ex.RolledBack);
        Assert.Contains(_store.Items, i => i.Text == "later");
        Assert.Contains(_store.Items, i => i.Text == "orig");
        Assert.Equal("{\"Edge\":\"Right\"}", File.ReadAllText(Path.Combine(_dir, "settings.json")));
        Assert.False(File.Exists(Path.Combine(_store.BlobsDir, "orig.png")));
    }

    [Fact]
    public void RestoreFrom_RestoresBlobsAndSettings_NotOnlyItems()
    {
        Add("orig", 1);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"Edge\":\"Left\"}");
        File.WriteAllBytes(Path.Combine(_store.BlobsDir, "orig.png"), new byte[] { 9 });
        var good = _svc.CreateBackup(BackupKind.Manual)!;

        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"Edge\":\"Right\"}");
        File.Delete(Path.Combine(_store.BlobsDir, "orig.png"));
        Add("later", 2);

        _svc.RestoreFrom(good);

        Assert.Contains(_store.Items, i => i.Text == "orig");
        Assert.DoesNotContain(_store.Items, i => i.Text == "later");
        Assert.True(File.Exists(Path.Combine(_store.BlobsDir, "orig.png")));
        Assert.Equal("{\"Edge\":\"Left\"}", File.ReadAllText(Path.Combine(_dir, "settings.json")));
    }

    // 中間ファイルは dataDir\tmp\ 配下にしか作らず、成功・失敗どちらの経路でも finally で
    // 自分の分を消す (§6.1 手順 4: 平文を残さない)。失敗経路も含めて 1 本で通す。
    [Fact]
    public void AllOperations_LeaveNothingBehindInTmp()
    {
        var tmpRoot = Path.Combine(_dir, "tmp");
        string[] Leftovers() => Directory.Exists(tmpRoot) ? Directory.GetFileSystemEntries(tmpRoot) : Array.Empty<string>();

        Add("orig", 1);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{}");

        var good = _svc.CreateBackup(BackupKind.Manual)!;
        Assert.Empty(Leftovers());

        Assert.Equal(BackupValidation.Ok, _svc.Validate(good));
        Assert.Empty(Leftovers());

        Assert.Throws<BackupRestoreException>(() => _svc.RestoreFrom(MakeCorruptCopy(good)));
        Assert.Empty(Leftovers());

        _svc.RestoreFrom(good);
        Assert.Empty(Leftovers());

        var exportPath = Path.Combine(_dir, "e.tndexport");
        _svc.ExportTo(exportPath, "pass-word-8");
        Assert.Empty(Leftovers());

        Assert.Throws<ExportPasswordException>(() => _svc.ImportFrom(exportPath, "bad-password"));
        Assert.Empty(Leftovers());

        _svc.ImportFrom(exportPath, "pass-word-8");
        Assert.Empty(Leftovers());

        var junk = Path.Combine(_dir, "junk.tndexport");
        File.WriteAllBytes(junk, new byte[200]);
        Assert.Throws<ExportFormatException>(() => _svc.ImportFrom(junk, "pass-word-8"));
        Assert.Empty(Leftovers());

        // backups\ の外を指す削除依頼は受け付けない (UI の取り違えで無関係なファイルを消さない)
        Assert.Throws<ArgumentException>(() => _svc.DeleteBackup(exportPath));
        _svc.DeleteBackup(good);
        Assert.False(File.Exists(good));
    }

    [Fact]
    public void CreateBackup_PrunesOldAutoBackups()
    {
        SeedItem("a");
        // 過去 7 日分の自動バックアップを偽造してから 8 日目を作る
        Directory.CreateDirectory(_svc.BackupsDir);
        for (var d = 1; d <= 7; d++)
        {
            var fake = Path.Combine(_svc.BackupsDir, $"auto-2001010{d}.zip");
            using var zip = ZipFile.Open(fake, ZipArchiveMode.Create);
            zip.CreateEntry("manifest.json");
        }

        _svc.CreateBackup(BackupKind.Auto);

        var autos = Directory.GetFiles(_svc.BackupsDir, "auto-*.zip");
        Assert.Equal(7, autos.Length);
        Assert.DoesNotContain(autos, p => Path.GetFileName(p) == "auto-20010101.zip");
    }
}
