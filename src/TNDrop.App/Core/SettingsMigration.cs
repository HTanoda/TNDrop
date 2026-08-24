namespace TNDrop.Core;

/// <summary>
/// 設定の一回限り移行 (v1.5)。App.OnStartup が設定ロード直後・レジストリ自己修復より
/// 前に呼び、true が返ったら保存する。純粋関数なので WPF なしでテストできる
/// (SettingsMigrationTests)。
/// </summary>
public static class SettingsMigration
{
    /// <summary>
    /// 自動開始デフォルト有効化 (設計書パート3)。未移行プロファイルを一度だけ ON に
    /// してフラグを立てる。移行済みなら何もしない (以降のユーザー操作を尊重)。
    /// v1.4 以前で意図的に OFF にしていたユーザーも一度だけ ON に戻る -- 「現状 OFF は
    /// ほぼ全員デフォルトのまま」という推定に基づく合意済みトレードオフ。
    /// </summary>
    public static bool ApplyAutoStartDefault(AppSettings s)
    {
        if (s.AutoStartDefaultMigrated)
        {
            return false;
        }

        s.AutoStartEnabled = true;
        s.AutoStartDefaultMigrated = true;
        return true;
    }
}
