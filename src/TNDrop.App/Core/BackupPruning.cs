using System;
using System.Collections.Generic;
using System.Linq;

namespace TNDrop.Core;

/// <summary>バックアップ ZIP の種別。ファイル名プレフィックスと manifest の kind に対応する。</summary>
public enum BackupKind { Auto, Manual, Safety }

/// <summary>
/// backups フォルダの世代刈り込み規則 (設計書 §3)。auto は新しい順 7 件、safety は 3 件を
/// 保持し、manual と規則外の名前は決して削除対象にしない。ファイル名は
/// auto-YYYYMMDD.zip / safety-YYYYMMDD-HHmmss.zip の形式で、辞書順 = 時系列順に
/// なるよう設計されているため、ソートに更新日時ではなくファイル名を使う (テスト容易性と、
/// コピーで復元されたファイルの更新日時が当てにならないため)。
/// </summary>
public static class BackupPruning
{
    public const int AutoKeep = 7;
    public const int SafetyKeep = 3;

    /// <summary>
    /// SINGLE RESOLUTION for "what does this kind look like on disk" (v1.6 Task 5): the manifest's
    /// <c>kind</c> value AND the file-name prefix are both derived from the enum member's own name
    /// here, so BackupService's naming, this class's pruning rules, and <see cref="TryParseKind"/>
    /// can never disagree about which files are auto/manual/safety. Never re-hardcode "auto-" etc.
    /// </summary>
    public static string KindName(BackupKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>ファイル名プレフィックス (<c>auto-</c> 等)。<see cref="KindName"/> から導出する。</summary>
    public static string KindPrefix(BackupKind kind) => KindName(kind) + "-";

    /// <summary>
    /// ファイル名 (パスではなく名前) から種別を判定する。規則外の名前は false を返し、
    /// 一覧にも刈り込み対象にも含めない (利用者が backups フォルダに置いた無関係な ZIP を
    /// TNDrop が消したり復元候補として見せたりしないため)。
    /// </summary>
    public static bool TryParseKind(string fileName, out BackupKind kind)
    {
        foreach (var candidate in Enum.GetValues<BackupKind>())
        {
            if (fileName.StartsWith(KindPrefix(candidate), StringComparison.OrdinalIgnoreCase))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    /// <summary>削除すべきファイル名 (パスではなく名前) を返す。呼び出し側が実削除する。</summary>
    public static IReadOnlyList<string> SelectFilesToDelete(IEnumerable<string> fileNames)
    {
        var names = fileNames.ToList();
        var doomed = new List<string>();
        doomed.AddRange(SelectOverflow(names, KindPrefix(BackupKind.Auto), AutoKeep));
        doomed.AddRange(SelectOverflow(names, KindPrefix(BackupKind.Safety), SafetyKeep));
        return doomed;
    }

    private static IEnumerable<string> SelectOverflow(List<string> names, string prefix, int keep) =>
        names
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase)
            .Skip(keep);
}
