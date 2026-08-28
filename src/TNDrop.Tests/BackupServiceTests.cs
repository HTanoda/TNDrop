using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
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

    // ContentHash は本文から導出する (v1.6 最終レビュー修正 Fix 5)。既定の 0 のままだと
    // 2 件目の TryAdd が「先頭と同じハッシュ」として**黙って拒否される**ため、2 件を撒く
    // テストが「消えたこと」を検証しているつもりで空振りする (実際に
    // RestoreFrom_ReplacesCurrentData_AndWritesSafetyBackup がそうなっていた)。導出は
    // ItemStore.BuildFileItems が本番で使っているのと同じ Fnv1a。
    private void SeedItem(string text)
    {
        _store.TryAdd(new ClipItem
        {
            Kind = ClipKind.Text,
            Text = text,
            CreatedAtUtc = DateTime.UtcNow,
            ContentHash = ItemStore.Fnv1a(Encoding.UTF8.GetBytes(text)),
        });
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

        // 並び順の権威はファイル名の日時部分 (BackupPruning.SortKey) — manifest の createdUtc では
        // ない。同日なら auto-yyyyMMdd より manual-yyyyMMdd-HHmmss が後 (= 新しい) と決まる。
        Assert.Equal(BackupKind.Manual, list[0].Kind);
        Assert.Equal(
            list.Select(e => BackupPruning.SortKey(Path.GetFileName(e.FilePath))).OrderByDescending(k => k, StringComparer.OrdinalIgnoreCase),
            list.Select(e => BackupPruning.SortKey(Path.GetFileName(e.FilePath))));
    }

    // 刈り込み (ファイル名順) と一覧が別々の鍵で「新しい順」を決めると食い違う。
    // manifest の createdUtc が今なのにファイル名は 1999 年、という ZIP (持ち回りのコピー) を
    // 置いて、一覧がファイル名側に従う = 刈り込みが最古とみなすものを先頭に出さないことを見る。
    [Fact]
    public void ListBackups_OrdersByFileName_NotByManifestCreatedUtc()
    {
        SeedItem("a");
        var recent = _svc.CreateBackup(BackupKind.Manual)!;
        var staleName = "manual-19990101-000000.zip";
        File.Copy(recent, Path.Combine(_svc.BackupsDir, staleName)); // manifest の createdUtc は「今」

        var list = _svc.ListBackups();

        Assert.Equal(2, list.Count);
        Assert.Equal(staleName, Path.GetFileName(list[^1].FilePath));
        Assert.Equal(Path.GetFileName(recent), Path.GetFileName(list[0].FilePath));
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
        // SeedItem がハッシュを本文から導出するようになったので、この 2 件目は実際に履歴へ入る
        // (以前は ContentHash=0 同士の重複として拒否され、下の DoesNotContain が空振りしていた)。
        Assert.Contains(_store.Items, i => i.Text == "newer-than-backup");

        _svc.RestoreFrom(backup);

        Assert.Contains(_store.Items, i => i.Text == "old");
        Assert.DoesNotContain(_store.Items, i => i.Text == "newer-than-backup");
        Assert.NotEmpty(Directory.GetFiles(_svc.BackupsDir, "safety-*.zip"));
    }

    // レビュー修正 (fix round 1, item 3): ItemStore.ReplaceDataFrom の契約では
    // InvalidDataException = 「取り込み元が読めず、何も触っていない」。したがってここは
    // 巻き戻しを走らせず即座に失敗する (現在の blobs を消して貼り直す破壊的な往復を、
    // 最も起きやすい失敗のたびに行わない)。AbortedClean = 「差し替え前に中断、データは無傷」。
    [Fact]
    public void RestoreFrom_CorruptItemsDat_FailsFastAndLeavesDataUntouched()
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

        Assert.Equal(RestoreFailure.AbortedClean, ex.Failure);
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

    // 上の FailsFast の全面版: 壊れたバックアップを選んでも履歴・blobs・settings.json の
    // 3 点すべてが「復元を始める前」のまま残ることを実測する (blobs を消して貼り直す
    // 破壊的な巻き戻しが走っていないことの確認でもある)。
    [Fact]
    public void RestoreFrom_CorruptBackup_LeavesItemsBlobsAndSettingsUntouched()
    {
        Add("orig", 1);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"Edge\":\"Left\"}");
        File.WriteAllBytes(Path.Combine(_store.BlobsDir, "orig.png"), new byte[] { 9 });
        var good = _svc.CreateBackup(BackupKind.Manual)!;

        // バックアップ後に現在の状態を動かす -> 失敗した復元はこの「動かした後」の状態を保つべき
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{\"Edge\":\"Right\"}");
        File.Delete(Path.Combine(_store.BlobsDir, "orig.png"));
        File.WriteAllBytes(Path.Combine(_store.BlobsDir, "later.png"), new byte[] { 7 });
        Add("later", 2);

        var ex = Assert.Throws<BackupRestoreException>(() => _svc.RestoreFrom(MakeCorruptCopy(good)));

        Assert.Equal(RestoreFailure.AbortedClean, ex.Failure);
        Assert.Contains(_store.Items, i => i.Text == "later");
        Assert.Contains(_store.Items, i => i.Text == "orig");
        Assert.Equal("{\"Edge\":\"Right\"}", File.ReadAllText(Path.Combine(_dir, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_store.BlobsDir, "later.png")));
        Assert.False(File.Exists(Path.Combine(_store.BlobsDir, "orig.png")));
    }

    // レビュー修正 (fix round 2): 巻き戻しが実際に成功する経路。前進側の最後の手順である
    // settings.json のコピーだけを失敗させる (宛先を別ハンドルで掴んで書き込みを拒否する)。
    // 巻き戻しは items.dat + blobs だけを戻し settings.json には触らないので、同じロックに
    // 二度目でぶつかることなく完了し、Failure=RolledBack になる。
    [Fact]
    public void RestoreFrom_SettingsCopyFails_RollsBackItemsAndLeavesSettingsIntact()
    {
        var settingsPath = Path.Combine(_dir, "settings.json");

        Add("in-backup", 1);
        File.WriteAllText(settingsPath, "{\"Edge\":\"Left\"}");
        var backup = _svc.CreateBackup(BackupKind.Manual)!;

        // 復元対象とは別の「現在の状態」を作る
        Add("pre-restore", 2);
        File.WriteAllText(settingsPath, "{\"Edge\":\"Right\"}");
        File.WriteAllBytes(Path.Combine(_store.BlobsDir, "pre-restore.png"), new byte[] { 5 });

        BackupRestoreException ex;
        // FileShare.Read: 退避バックアップ側の「settings.json を読んで staging へコピー」は通しつつ、
        // 前進側の「settings.json へ上書き」だけを IOException にする。FileShare.None にすると
        // 退避作成そのものが失敗し (CreateBackup が null)、差し替え前中断の経路に落ちて
        // 巻き戻しを踏めない。
        using (var _ = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ex = Assert.Throws<BackupRestoreException>(() => _svc.RestoreFrom(backup));
        }

        Assert.Equal(RestoreFailure.RolledBack, ex.Failure);
        // 履歴・blobs は復元前の状態に戻っている
        Assert.Contains(_store.Items, i => i.Text == "pre-restore");
        Assert.Contains(_store.Items, i => i.Text == "in-backup");
        Assert.True(File.Exists(Path.Combine(_store.BlobsDir, "pre-restore.png")));
        // settings.json は一度も書き換わっていない (だからこそ巻き戻しで戻す必要が無い)
        Assert.Equal("{\"Edge\":\"Right\"}", File.ReadAllText(settingsPath));
    }

    // レビュー修正 (fix round 1, item 1): 時計が巻き戻っている環境では、作ったばかりの ZIP が
    // 名前順で保持枠から外れて自分の Prune に消されうる。消えたパスを返すと
    // 「safety is null なら中断」ガードをすり抜け、退避が無いまま差し替えてしまう。
    [Fact]
    public void CreateBackup_NeverReturnsAPathItsOwnPruneDeleted()
    {
        Add("keep", 1);
        var good = _svc.CreateBackup(BackupKind.Manual)!; // manual は刈り込み対象外

        // 未来日付の退避が保持枠 (3 件) を埋めている状態を作る
        foreach (var stamp in new[] { "99990101-000000", "99990102-000000", "99990103-000000" })
        {
            File.WriteAllBytes(Path.Combine(_svc.BackupsDir, $"safety-{stamp}.zip"), new byte[] { 0 });
        }

        var safety = _svc.CreateBackup(BackupKind.Safety);
        Assert.True(safety is null || File.Exists(safety), "CreateBackup returned a path that no longer exists");

        // 退避を作れない状態での復元は、現在のデータに触れる前に中断しなければならない
        var ex = Assert.Throws<BackupRestoreException>(() => _svc.RestoreFrom(good));
        Assert.Equal(RestoreFailure.AbortedClean, ex.Failure);
        Assert.Contains(_store.Items, i => i.Text == "keep");
    }

    // レビュー修正 (fix round 1, item 2): Save() の File.Replace 中のクラッシュや items.dat の
    // 外部削除で items.bak しか残っていない状態でも、バックアップは履歴を含まなければならない
    // (空の items.dat を作ると、その退避での巻き戻しが Load() なら救えた履歴を静かに消す)。
    [Fact]
    public void CreateBackup_WithOnlyItemsBak_BacksUpTheRecoverableHistory()
    {
        Add("only-in-bak", 1);
        var itemsDat = Path.Combine(_dir, "items.dat");
        File.Move(itemsDat, Path.Combine(_dir, "items.bak"));
        Assert.False(File.Exists(itemsDat));

        var backup = _svc.CreateBackup(BackupKind.Manual);

        Assert.NotNull(backup);
        Assert.Equal(BackupValidation.Ok, _svc.Validate(backup!));

        var otherDir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
        try
        {
            var otherStore = new ItemStore(otherDir);
            var otherSvc = new BackupService(otherDir, otherStore);
            otherSvc.RestoreFrom(backup!);
            Assert.Contains(otherStore.Items, i => i.Text == "only-in-bak");
        }
        finally
        {
            try { Directory.Delete(otherDir, true); } catch { }
        }
    }

    // 上と同じ「items.bak しか残っていない」状態の、エクスポート側 (v1.6 最終レビュー修正 Fix 3)。
    // ReadDecryptedJson が null を返すと ExportTo はそれを "[]" に潰すため、復旧可能な履歴が
    // **空のエクスポートファイル**になり、それを取り込んだ移行先の履歴が消える。
    [Fact]
    public void ExportTo_WithOnlyItemsBak_ExportsTheRecoverableHistory()
    {
        Add("only-in-bak", 1);
        File.Move(Path.Combine(_dir, "items.dat"), Path.Combine(_dir, "items.bak"));

        var exportPath = Path.Combine(_dir, "out.tndexport");
        _svc.ExportTo(exportPath, "pass-word-8");

        var otherDir = Path.Combine(Path.GetTempPath(), "tndrop-test-" + Guid.NewGuid());
        try
        {
            var otherStore = new ItemStore(otherDir);
            var otherSvc = new BackupService(otherDir, otherStore);
            otherSvc.ImportFrom(exportPath, "pass-word-8");

            Assert.Contains(otherStore.Items, i => i.Text == "only-in-bak");
        }
        finally
        {
            try { Directory.Delete(otherDir, true); } catch { }
        }
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

    // 不変条件: CreateBackup が作ったものは必ず Validate を通り、復元もできる。
    // 真新しいプロファイル (items.dat も settings.json も未作成、blobs は空) が最も危ない —
    // 何も同梱されず、後から「自分で作ったのに NotABackup」になりうる。
    [Fact]
    public void CreateBackup_OnFreshDataDir_IsValidAndRestorable()
    {
        Assert.False(File.Exists(Path.Combine(_dir, "items.dat")));
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json")));
        Assert.Empty(Directory.GetFiles(_store.BlobsDir));

        var path = _svc.CreateBackup(BackupKind.Manual);

        Assert.NotNull(path);
        using (var zip = ZipFile.OpenRead(path!))
        {
            Assert.NotNull(zip.GetEntry("manifest.json"));
            Assert.NotNull(zip.GetEntry("items.dat"));
            Assert.NotNull(zip.GetEntry("settings.json"));
        }

        Assert.Equal(BackupValidation.Ok, _svc.Validate(path!));

        _svc.RestoreFrom(path!);
        Assert.Empty(_store.Items);
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
