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

    /// <summary>
    /// SINGLE ORDERING AUTHORITY (v1.6 Task 5 レビュー修正): バックアップの新旧を決める鍵は
    /// **ファイル名の日時部分** であって manifest の createdUtc ではない。刈り込み
    /// (<see cref="SelectFilesToDelete"/>) と一覧の並び (<c>BackupService.ListBackups</c>) は
    /// どちらもこの関数を鍵に使う — 別々の規則で「新しい順」を決めると、一覧の 1 件目が
    /// 刈り込みで消される側だった、という食い違いが静かに起きる。
    ///
    /// 返すのは種別プレフィックスと拡張子を除いた部分 (auto は <c>yyyyMMdd</c>、manual/safety は
    /// <c>yyyyMMdd-HHmmss</c>)。プレフィックスを含めた丸ごとのファイル名で並べないのは、
    /// 序数比較だと safety- &gt; manual- &gt; auto- の順に**種別で塊になり**、日時順でなくなるため
    /// (刈り込みは同一プレフィックス内でしか比較しないので、その中では丸ごと比較と同値)。
    /// </summary>
    public static string SortKey(string fileName)
    {
        var name = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? fileName.Substring(0, fileName.Length - 4)
            : fileName;

        if (TryParseKind(fileName, out var kind) && name.Length >= KindPrefix(kind).Length)
        {
            return name.Substring(KindPrefix(kind).Length);
        }

        return name;
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

    // 並び替えの鍵は SortKey (= ファイル名の日時部分)。同一プレフィックス内での比較なので
    // 従来のファイル名まるごとの序数比較と結果は同値だが、鍵の定義を 1 つにしておくことで
    // BackupService.ListBackups の「新しい順」と必ず一致する。
    private static IEnumerable<string> SelectOverflow(List<string> names, string prefix, int keep) =>
        names
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(SortKey, StringComparer.OrdinalIgnoreCase)
            .Skip(keep);
}
