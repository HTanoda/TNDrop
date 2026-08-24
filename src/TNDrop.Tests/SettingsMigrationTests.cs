using TNDrop.Core;

// v1.5 設計書パート3: 自動開始のデフォルト有効化は「一回限りの移行」。
// フラグが立っていなければ一度だけ ON にしてフラグを立てる。フラグが立って
// いれば以降のユーザー操作 (OFF に戻した等) をそのまま尊重する。
public class SettingsMigrationTests
{
    [Fact]
    public void First_run_enables_autostart_and_sets_the_flag()
    {
        var s = new AppSettings { AutoStartEnabled = false, AutoStartDefaultMigrated = false };
        Assert.True(SettingsMigration.ApplyAutoStartDefault(s));
        Assert.True(s.AutoStartEnabled);
        Assert.True(s.AutoStartDefaultMigrated);
    }

    [Fact]
    public void Migrated_profile_keeps_the_users_choice()
    {
        var s = new AppSettings { AutoStartEnabled = false, AutoStartDefaultMigrated = true };
        Assert.False(SettingsMigration.ApplyAutoStartDefault(s));
        Assert.False(s.AutoStartEnabled);
    }

    [Fact]
    public void Fresh_install_defaults_still_get_the_flag_stamped()
    {
        var s = new AppSettings(); // AutoStartEnabled=true, flag=false
        Assert.True(SettingsMigration.ApplyAutoStartDefault(s));
        Assert.True(s.AutoStartEnabled);
        Assert.True(s.AutoStartDefaultMigrated);
    }
}
