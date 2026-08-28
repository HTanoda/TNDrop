using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using TNDrop.Services;

namespace TNDrop.Core;

/// <summary>backups フォルダの 1 ファイル。<c>CreatedLocal</c> は manifest の createdUtc をローカル時刻にしたもの。</summary>
public sealed record BackupEntry(string FilePath, BackupKind Kind, DateTime CreatedLocal);

/// <summary>
/// 復元前の検証結果 (設計書 §5 手順 3)。<c>NotABackup</c> = TNDrop のバックアップではない、
/// <c>WrongEnvironment</c> = 形式は正しいが items.dat をこの Windows ユーザーの DPAPI で
/// 復号できない (別 PC / 別ユーザーで作られた ZIP。移行にはエクスポートを使う)。
/// </summary>
public enum BackupValidation { Ok, NotABackup, WrongEnvironment }

/// <summary>
/// リストア / インポートの失敗 (設計書 §5 手順 6)。
/// <see cref="RolledBack"/> は「退避 ZIP から元の状態へ巻き戻した」ことを表す。
/// false は 2 通りある: (a) 差し替えを始める前に中断した (取り込み元を展開できない /
/// 退避の作成に失敗した) ので現在のデータは無傷、(b) 差し替え後の巻き戻しにも失敗した
/// (致命的。退避 ZIP のパスを ERROR ログに残す)。UI はこの真偽だけで文言を決めず、
/// どの経路で来たかをメッセージ / ログと合わせて扱うこと。
/// </summary>
public sealed class BackupRestoreException : Exception
{
    public bool RolledBack { get; }

    public BackupRestoreException(string message, bool rolledBack, Exception? inner = null)
        : base(message, inner)
    {
        RolledBack = rolledBack;
    }
}

/// <summary>
/// バックアップの作成 / 一覧 / 検証 / 復元と、別 PC への移行 (エクスポート / インポート) の
/// ファイル層 (設計書 §3〜§6)。ここが触るのは backups フォルダ・tmp フォルダ・settings.json と
/// <see cref="ItemStore"/> までで、復元後の「設定の再読込 + UI 再適用 + 完了メッセージ」は
/// 呼び出し側 (App / ダイアログ) の責務。
///
/// 中間ファイルはすべて <c>dataDir\tmp\</c> 配下に作り、各操作の finally で自分が作った
/// フォルダ / ファイルだけを消す (tmp 全体を消さないのは、リストア中に作られる退避バックアップが
/// 同じ tmp を使うため — 一括削除にすると復元用の展開先を自分で消してしまう)。前回の残骸は
/// コンストラクタで一度だけ掃除する。
/// </summary>
public sealed class BackupService
{
    private const int ManifestFormat = 1;
    private const string ExportKindName = "export";
    private const string ItemsFileName = "items.dat";
    private const string ItemsJsonName = "items.json";
    private const string SettingsFileName = "settings.json";
    private const string ManifestFileName = "manifest.json";

    // manifest 専用。ItemStore.JsonOptions (履歴の (逆) シリアライズ) とは対象が別なので分ける。
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private sealed class BackupManifest
    {
        public int Format { get; set; } = ManifestFormat;
        public string AppVersion { get; set; } = "";
        public DateTime CreatedUtc { get; set; }
        public string Kind { get; set; } = "";

        /// <summary>エクスポート時のみ意味を持つ (移行元 dataDir)。インポートの blobs パス書き換えに使う。</summary>
        public string DataDir { get; set; } = "";
    }

    private readonly string _dataDir;
    private readonly ItemStore _store;
    private readonly string _tmpRoot;

    public BackupService(string dataDir, ItemStore store)
    {
        _dataDir = dataDir;
        _store = store;
        BackupsDir = Path.Combine(dataDir, "backups");
        _tmpRoot = Path.Combine(dataDir, "tmp");

        // 前回の異常終了で残った中間ファイルを起動時に一掃する。ここだけが tmp を丸ごと消す。
        try
        {
            if (Directory.Exists(_tmpRoot))
            {
                Directory.Delete(_tmpRoot, recursive: true);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("backup", $"failed to clean leftover tmp directory: {ex.GetType().Name}");
        }
    }

    public string BackupsDir { get; }

    /// <summary>
    /// データ一式 (items.dat は復号せずそのまま + blobs + settings.json + manifest.json) を
    /// ZIP 化する。成功で作成した ZIP のフルパス、失敗で null (設計書 §4: 自動バックアップの
    /// 失敗は通常動作を止めない。手動 / 退避も戻り値 null で呼び出し側が判断する)。
    /// Auto は 1 日 1 ファイル (同日は上書き)、Manual / Safety は秒までのタイムスタンプ付き。
    /// 成功後に必ず <see cref="Prune"/> を回して世代を刈り込む。
    /// </summary>
    public string? CreateBackup(BackupKind kind)
    {
        string? staging = null;
        string? tmpZip = null;

        try
        {
            Directory.CreateDirectory(BackupsDir);

            var now = DateTime.Now;
            var stamp = kind == BackupKind.Auto ? $"{now:yyyyMMdd}" : $"{now:yyyyMMdd-HHmmss}";
            var fileName = BackupPruning.KindPrefix(kind) + stamp + ".zip";
            var finalPath = Path.Combine(BackupsDir, fileName);

            staging = NewTmpDirectory("backup");
            _store.CopyDataTo(staging);
            EnsureBackupEntries(staging);
            WriteManifest(staging, BackupPruning.KindName(kind), dataDir: "");

            tmpZip = Path.Combine(EnsureTmpRoot(), $"backup-{Guid.NewGuid():N}.zip");
            ZipFile.CreateFromDirectory(staging, tmpZip);

            // 同日の auto-*.zip はここで上書きされる (= 1 日 1 ファイル)。
            File.Move(tmpZip, finalPath, overwrite: true);
            tmpZip = null;

            FileLogger.Instance?.Info("backup", $"created backup: {fileName}");
            Prune();
            return finalPath;
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("backup", $"failed to create {BackupPruning.KindName(kind)} backup: {ex.GetType().Name}");
            return null;
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteFile(tmpZip);
        }
    }

    /// <summary>新しい順。命名規則に合わない ZIP は無視する (利用者が置いた無関係なファイルを扱わない)。</summary>
    public IReadOnlyList<BackupEntry> ListBackups()
    {
        var entries = new List<BackupEntry>();

        if (!Directory.Exists(BackupsDir))
        {
            return entries;
        }

        foreach (var path in Directory.GetFiles(BackupsDir, "*.zip"))
        {
            if (!BackupPruning.TryParseKind(Path.GetFileName(path), out var kind))
            {
                continue;
            }

            entries.Add(new BackupEntry(path, kind, ReadCreatedLocal(path)));
        }

        return entries.OrderByDescending(e => e.CreatedLocal).ToList();
    }

    /// <summary>
    /// backups フォルダ配下の 1 ファイルを削除する。ダイアログの「削除」用。
    /// backups の外を指すパスは受け付けない (UI 側の取り違えで無関係なファイルを消さないため)。
    /// 削除自体の失敗は握り潰さず呼び出し側へ投げる (利用者が明示的に頼んだ操作なので黙って
    /// 成功したように見せない)。
    /// </summary>
    public void DeleteBackup(string filePath)
    {
        var full = Path.GetFullPath(filePath);
        var root = Path.GetFullPath(BackupsDir);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("path is not inside the backups directory", nameof(filePath));
        }

        File.Delete(full);
        FileLogger.Instance?.Info("backup", $"deleted backup: {Path.GetFileName(full)}");
    }

    /// <summary>
    /// リストア候補の事前検証 (設計書 §5 手順 3)。manifest が読めて format が一致し、
    /// items.dat / settings.json エントリがあることを見た上で、items.dat をこの Windows ユーザーの
    /// DPAPI で復号できるかまで確認する。ZIP として開けない場合も <c>NotABackup</c>。
    /// </summary>
    public BackupValidation Validate(string zipPath)
    {
        string? tmpItems = null;

        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var manifest = ReadManifest(zip);
                var itemsEntry = zip.GetEntry(ItemsFileName);

                if (manifest is null
                    || manifest.Format != ManifestFormat
                    || itemsEntry is null
                    || zip.GetEntry(SettingsFileName) is null)
                {
                    return BackupValidation.NotABackup;
                }

                tmpItems = Path.Combine(EnsureTmpRoot(), $"validate-{Guid.NewGuid():N}.dat");
                itemsEntry.ExtractToFile(tmpItems, overwrite: true);
            }

            return ItemStore.CanDecrypt(tmpItems) ? BackupValidation.Ok : BackupValidation.WrongEnvironment;
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("backup", $"backup validation failed: {ex.GetType().Name}");
            return BackupValidation.NotABackup;
        }
        finally
        {
            TryDeleteFile(tmpItems);
        }
    }

    /// <summary>
    /// バックアップ ZIP でデータ一式を丸ごと置き換える (設計書 §5 手順 2〜6。手順 1 の確認ダイアログと
    /// 手順 5 の再適用は UI 側)。<see cref="Validate"/> の呼び出しは前提にしない — ダイアログを
    /// 通らない経路でも壊れたバックアップで履歴を失わないよう、実際の差し替えは
    /// <see cref="ItemStore.ReplaceDataFrom"/> の「読めなければ何も触らず例外」に守らせる。
    /// </summary>
    public void RestoreFrom(string zipPath)
    {
        FileLogger.Instance?.Info("backup", $"restore started: {Path.GetFileName(zipPath)}");
        string? staging = null;

        try
        {
            // 展開は退避バックアップより先。開けもしない ZIP のために safety-*.zip を 1 世代
            // 消費しない、かつ失敗しても現在のデータは無傷 (RolledBack=false の (a) 経路)。
            try
            {
                staging = ExtractToStaging(zipPath, "restore");
            }
            catch (Exception ex)
            {
                FileLogger.Instance?.Error("backup", "restore aborted: backup could not be extracted", ex);
                throw new BackupRestoreException("backup could not be extracted", rolledBack: false, ex);
            }

            RestoreFromStaging(staging);
            FileLogger.Instance?.Info("backup", "restore completed");
        }
        finally
        {
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// データ一式を復号した平文で ZIP 化し、パスワードで暗号化して <paramref name="destPath"/> へ
    /// 書き出す (設計書 §6.1)。中間の平文は tmp に置き、成功・失敗を問わず finally で消す。
    /// </summary>
    public void ExportTo(string destPath, string password)
    {
        // ExportContainer.Encrypt も同じ検査をするが、ここで先に弾いて平文のステージングを
        // そもそも作らない (短いパスワードのために平文を一瞬でもディスクに置かない)。
        if (password.Length < ExportContainer.MinPasswordLength)
        {
            throw new ArgumentException(
                $"password must be at least {ExportContainer.MinPasswordLength} characters", nameof(password));
        }

        FileLogger.Instance?.Info("backup", "export started");
        string? staging = null;
        string? tmpZip = null;

        try
        {
            staging = NewTmpDirectory("export");

            // CopyDataTo は blobs もまとめて持ってくる。items.dat (DPAPI) は移行先で復号できないので
            // 捨て、代わりに復号済み JSON を items.json として置く。
            _store.CopyDataTo(staging);
            var stagedItemsDat = Path.Combine(staging, ItemsFileName);
            if (File.Exists(stagedItemsDat))
            {
                File.Delete(stagedItemsDat);
            }

            File.WriteAllText(Path.Combine(staging, ItemsJsonName), _store.ReadDecryptedJson() ?? "[]");
            CopySettingsInto(staging);
            WriteManifest(staging, ExportKindName, dataDir: _dataDir);

            tmpZip = Path.Combine(EnsureTmpRoot(), $"export-{Guid.NewGuid():N}.zip");
            ZipFile.CreateFromDirectory(staging, tmpZip);

            File.WriteAllBytes(destPath, ExportContainer.Encrypt(File.ReadAllBytes(tmpZip), password));
            FileLogger.Instance?.Info("backup", "export completed");
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteFile(tmpZip);
        }
    }

    /// <summary>
    /// .tndexport を復号し、blobs パスを移行先向けに書き換えてから §5 の共通復元フローに渡す
    /// (設計書 §6.2)。パスワード誤り / 改ざん / 別形式は復号段階で例外になり、その時点では
    /// 現在のデータに一切触れていない。
    /// </summary>
    public void ImportFrom(string srcPath, string password)
    {
        FileLogger.Instance?.Info("backup", "import started");

        byte[] plainZip;
        try
        {
            plainZip = ExportContainer.Decrypt(File.ReadAllBytes(srcPath), password);
        }
        catch (CryptographicException ex)
        {
            // HMAC は通ったが暗号文長がブロック長の倍数でない等、AES/PKCS7 段で壊れていた場合。
            // 上位には «パスワードが違うか、ファイルが壊れています» の 1 種類として見せる。
            FileLogger.Instance?.Warn("backup", $"import container could not be decrypted: {ex.GetType().Name}");
            throw new ExportPasswordException("export container could not be decrypted");
        }

        string? staging = null;
        string? tmpZip = null;

        try
        {
            tmpZip = Path.Combine(EnsureTmpRoot(), $"import-{Guid.NewGuid():N}.zip");
            File.WriteAllBytes(tmpZip, plainZip);

            staging = NewTmpDirectory("import");
            try
            {
                ZipFile.ExtractToDirectory(tmpZip, staging, overwriteFiles: true);
            }
            catch (Exception ex)
            {
                throw new ExportFormatException($"export content could not be read: {ex.GetType().Name}");
            }

            var manifest = ReadManifest(Path.Combine(staging, ManifestFileName));
            var itemsJsonPath = Path.Combine(staging, ItemsJsonName);

            if (manifest is null
                || manifest.Format != ManifestFormat
                || !string.Equals(manifest.Kind, ExportKindName, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(itemsJsonPath))
            {
                throw new ExportFormatException("not a TNDrop export container");
            }

            // 平文 JSON のうち Files カードの Paths だけを移行先の blobs へ向け直し、
            // この PC の DPAPI で items.dat に書き戻す (以降は通常のリストアと同じ経路)。
            var rewritten = RewriteBlobPaths(File.ReadAllText(itemsJsonPath), manifest.DataDir);
            ItemStore.WriteEncryptedJson(Path.Combine(staging, ItemsFileName), rewritten);
            File.Delete(itemsJsonPath);

            RestoreFromStaging(staging);
            FileLogger.Instance?.Info("backup", "import completed");
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteFile(tmpZip);
        }
    }

    /// <summary>
    /// リストアとインポートの共通部分 (設計書 §5 手順 2 / 4 / 6): 退避 → 差し替え →
    /// 失敗したら退避 ZIP から巻き戻す。<paramref name="staging"/> は dataDir と同じ並び
    /// (items.dat + blobs\ + settings.json) に整えられた展開先。
    /// </summary>
    private void RestoreFromStaging(string staging)
    {
        var safety = CreateBackup(BackupKind.Safety);
        if (safety is null)
        {
            // 手順 2: 退避に失敗したら中断する。まだ何も触っていない。
            throw new BackupRestoreException("safety backup could not be created", rolledBack: false);
        }

        try
        {
            _store.ReplaceDataFrom(staging);
            CopySettingsFromStaging(staging);
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Error("backup", "restore failed; rolling back from the safety backup", ex);

            string? rollback = null;
            try
            {
                rollback = ExtractToStaging(safety, "rollback");
                _store.ReplaceDataFrom(rollback);
                CopySettingsFromStaging(rollback);
            }
            catch (Exception rollbackEx)
            {
                FileLogger.Instance?.Error(
                    "backup", $"rollback failed; the safety backup is kept at {safety}", rollbackEx);
                throw new BackupRestoreException("restore failed and rollback failed", rolledBack: false, ex);
            }
            finally
            {
                TryDeleteDirectory(rollback);
            }

            FileLogger.Instance?.Info("backup", "rolled back to the state before the restore");
            throw new BackupRestoreException("restore failed", rolledBack: true, ex);
        }
    }

    /// <summary>
    /// 移行元 dataDir の <c>blobs\</c> 配下を指す <see cref="ClipItem.Paths"/> エントリだけを
    /// 移行先の blobs パスへ置き換える (設計書 §6.2 手順 4)。判定は
    /// ItemStore.DeleteBlobPathIfUnderBlobsDir と同じ作法 (Path.GetFullPath で正規化してから
    /// 区切り文字付きの前方一致・大文字小文字無視) — 「blobsEvil」のような兄弟フォルダを
    /// 巻き込まないため。blobs の外にある利用者の通常ファイルは書き換えない。
    /// ImageFile / ThumbFile は blobs 内の相対ファイル名なのでそのまま移行できる (書き換え不要)。
    /// </summary>
    private string RewriteBlobPaths(string itemsJson, string oldDataDir)
    {
        List<ClipItem>? items;
        try
        {
            items = JsonSerializer.Deserialize<List<ClipItem>>(itemsJson, ItemStore.JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ExportFormatException($"export items.json could not be parsed: {ex.GetType().Name}");
        }

        if (items is null)
        {
            throw new ExportFormatException("export items.json could not be parsed");
        }

        var oldBlobsRoot = TryGetBlobsRootWithSeparator(oldDataDir);
        if (oldBlobsRoot is not null)
        {
            foreach (var item in items)
            {
                if (item.Paths.Count == 0)
                {
                    continue;
                }

                item.Paths = item.Paths.Select(p => RewriteOneBlobPath(p, oldBlobsRoot)).ToList();
            }
        }

        return JsonSerializer.Serialize(items, ItemStore.JsonOptions);
    }

    private string RewriteOneBlobPath(string path, string oldBlobsRootWithSeparator)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            // 移行元でしか成立しない形のパス。書き換えないだけで、カードはそのまま残す。
            FileLogger.Instance?.Warn("backup", $"import path check failed: {ex.GetType().Name}");
            return path;
        }

        if (!fullPath.StartsWith(oldBlobsRootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return Path.Combine(_store.BlobsDir, Path.GetFileName(fullPath));
    }

    private static string? TryGetBlobsRootWithSeparator(string dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(Path.Combine(dataDir, "blobs"));
            return root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("backup", $"import source blobs path could not be resolved: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>世代刈り込み (設計書 §3)。どれを消すかの判定は純関数 <see cref="BackupPruning"/> に任せ、
    /// ここは実削除だけを行う。1 ファイルの削除失敗は他のファイルの削除を止めない。</summary>
    private void Prune()
    {
        try
        {
            if (!Directory.Exists(BackupsDir))
            {
                return;
            }

            var names = Directory.GetFiles(BackupsDir)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!);

            foreach (var name in BackupPruning.SelectFilesToDelete(names))
            {
                try
                {
                    File.Delete(Path.Combine(BackupsDir, name));
                    FileLogger.Instance?.Info("backup", $"pruned old backup: {name}");
                }
                catch (Exception ex)
                {
                    FileLogger.Instance?.Warn("backup", $"failed to prune {name}: {ex.GetType().Name}");
                }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("backup", $"failed to prune backups: {ex.GetType().Name}");
        }
    }

    private DateTime ReadCreatedLocal(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var manifest = ReadManifest(zip);
            if (manifest is not null && manifest.CreatedUtc != default)
            {
                return manifest.CreatedUtc.ToLocalTime();
            }
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("backup", $"could not read manifest of {Path.GetFileName(zipPath)}: {ex.GetType().Name}");
        }

        return File.GetLastWriteTime(zipPath);
    }

    private static BackupManifest? ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestFileName);
        if (entry is null)
        {
            return null;
        }

        using var stream = entry.Open();
        return JsonSerializer.Deserialize<BackupManifest>(stream, ManifestJsonOptions);
    }

    private static BackupManifest? ReadManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(manifestPath);
            return JsonSerializer.Deserialize<BackupManifest>(stream, ManifestJsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteManifest(string stagingDir, string kind, string dataDir)
    {
        var manifest = new BackupManifest
        {
            Format = ManifestFormat,
            AppVersion = AppVersion.Display,
            CreatedUtc = DateTime.UtcNow,
            Kind = kind,
            DataDir = dataDir
        };

        File.WriteAllText(
            Path.Combine(stagingDir, ManifestFileName),
            JsonSerializer.Serialize(manifest, ManifestJsonOptions));
    }

    private void CopySettingsInto(string stagingDir)
    {
        var settings = Path.Combine(_dataDir, SettingsFileName);
        if (File.Exists(settings))
        {
            File.Copy(settings, Path.Combine(stagingDir, SettingsFileName), overwrite: true);
        }
    }

    /// <summary>
    /// INVARIANT: <b>すべてのバックアップ ZIP は manifest.json + items.dat + settings.json を必ず含む</b> —
    /// つまり <see cref="CreateBackup"/> が作ったものは常に <see cref="Validate"/> を通る。
    ///
    /// 真新しいプロファイル (履歴を 1 件も保存していない = items.dat が無い、設定画面を一度も
    /// 開いていない = settings.json が無い) では <see cref="ItemStore.CopyDataTo"/> も
    /// <see cref="CopySettingsInto"/> も何もコピーしないため、放置すると「自分で作ったのに
    /// 復元候補として NotABackup 扱いになるバックアップ」が生まれる。欠けている側を
    /// 「空だが正しい」内容で埋めてこの不変条件を守る:
    /// items.dat は空配列 <c>[]</c> を DPAPI で暗号化したもの (=履歴 0 件。Validate の
    /// CanDecrypt も通り、復元すると履歴が空になる)、settings.json は <c>{}</c>
    /// (SettingsStore.Load は解釈できない JSON を既定値にフォールバックするので、
    /// 復元後は既定設定になる)。
    /// </summary>
    private void EnsureBackupEntries(string stagingDir)
    {
        CopySettingsInto(stagingDir);

        var stagedSettings = Path.Combine(stagingDir, SettingsFileName);
        if (!File.Exists(stagedSettings))
        {
            File.WriteAllText(stagedSettings, "{}");
        }

        var stagedItems = Path.Combine(stagingDir, ItemsFileName);
        if (!File.Exists(stagedItems))
        {
            ItemStore.WriteEncryptedJson(stagedItems, "[]");
        }
    }

    private void CopySettingsFromStaging(string stagingDir)
    {
        var staged = Path.Combine(stagingDir, SettingsFileName);
        if (!File.Exists(staged))
        {
            return;
        }

        Directory.CreateDirectory(_dataDir);
        File.Copy(staged, Path.Combine(_dataDir, SettingsFileName), overwrite: true);
    }

    private string ExtractToStaging(string zipPath, string purpose)
    {
        var staging = NewTmpDirectory(purpose);
        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
        return staging;
    }

    private string EnsureTmpRoot()
    {
        Directory.CreateDirectory(_tmpRoot);
        return _tmpRoot;
    }

    private string NewTmpDirectory(string purpose)
    {
        var path = Path.Combine(EnsureTmpRoot(), $"{purpose}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("backup", $"failed to delete temp directory: {ex.GetType().Name}");
        }
    }

    private static void TryDeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Instance?.Warn("backup", $"failed to delete temp file: {ex.GetType().Name}");
        }
    }
}
