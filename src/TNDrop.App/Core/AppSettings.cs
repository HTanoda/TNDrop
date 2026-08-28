namespace TNDrop.Core;

public enum EdgeSide { Left, Right }
public enum TriggerAlign { Top, Center, Bottom }
public enum AutoDeletePolicy { Off, Hours1, Hours6, Hours24, Days7 }
public enum TextScale { Small, Normal, Medium, Large }
public enum IndicatorStyle { Beacon, Bar, Pulse, Corner, Bulge }

public sealed class AppSettings
{
    /// <summary>Allowed range for <see cref="HistoryCapacity"/>; SettingsStore.Load clamps a
    /// loaded value into this range so a hand-edited or stale settings.json cannot produce an
    /// unbounded (or zero) history cap. The UI offers 100/250/500/1000 but any int in range is
    /// accepted -- see SettingsStore's doc comment.</summary>
    public const int MinHistoryCapacity = 100;
    public const int MaxHistoryCapacity = 1000;

    public EdgeSide Edge { get; set; } = EdgeSide.Left;
    public string? MonitorDeviceName { get; set; } = null;
    public int HotZonePercent { get; set; } = 40;
    public int TriggerProximityPx { get; set; } = 3;
    public TriggerAlign TriggerAlign { get; set; } = TriggerAlign.Center;
    public int RetractDelayMs { get; set; } = 800;
    public bool SoundsEnabled { get; set; } = true;

    /// <summary>v1.5 からデフォルト有効。既存プロファイルへの一回限りの適用は
    /// <see cref="SettingsMigration.ApplyAutoStartDefault"/> が担う。</summary>
    public bool AutoStartEnabled { get; set; } = true;
    public bool IncognitoMode { get; set; } = false;
    public AutoDeletePolicy AutoDelete { get; set; } = AutoDeletePolicy.Off;
    public bool MoveToTopOnCopy { get; set; } = true;
    public TextScale TextScale { get; set; } = TextScale.Normal;
    public IndicatorStyle IndicatorStyle { get; set; } = IndicatorStyle.Bulge;
    public string Language { get; set; } = "ja";
    public bool HoverEnabled { get; set; } = true;

    /// <summary>Whether the trigger proximity hint beacon (v1.2 Task E: "right edge, wrong
    /// height") is enabled. Name kept from v1.1 for backward-compat load; the behavior it gates
    /// grew from a static hint into a cursor-driven proximity beacon -- see
    /// EdgeTriggerWindow.SetHintEnabled.</summary>
    public bool EdgeHintEnabled { get; set; } = true;

    /// <summary>Restart-time cleanup (v1.2 Task E): when true, App.OnStartup removes every
    /// unpinned item right after loading the store, once, before anything else can read it.</summary>
    public bool PurgeUnpinnedOnRestart { get; set; } = false;

    /// <summary>Max unpinned items CapturePipeline keeps after each successful capture (v1.2 Task
    /// E) -- see ItemStore.TrimUnpinnedToCapacity. Pinned items are never counted against this
    /// and never removed by it.</summary>
    public int HistoryCapacity { get; set; } = 500;

    /// <summary>Whether IndicatorWindow.Flash is allowed to run at all (v1.2 Task E). Gated in
    /// one place -- App.FlashIndicator -- so every caller (manual capture, click-to-copy,
    /// delete-selected, the settings-window style preview) obeys the same switch. Sound is
    /// unaffected.</summary>
    public bool IndicatorEnabled { get; set; } = true;

    /// <summary>Whether the shelf's pinned accordion is expanded (v1.2 Task H). Written by
    /// ShelfWindow's header toggle through App.SetPinnedExpanded, read back by
    /// ShelfWindow.ApplySettings. Purely a view state, but persisted because a user who collapsed
    /// the section expects it to still be collapsed next time the shelf slides in.</summary>
    public bool PinnedExpanded { get; set; } = true;

    /// <summary>Whether a plain click on a Text/Link card also sends Ctrl+V to whatever app is in
    /// the foreground after re-copying it (v1.2 Task H). Only ever consulted through
    /// <see cref="TNDrop.UI.ClickPaste.ShouldPasteOnClick"/>, which adds the safety terms (own
    /// process in front, search-box focus, physical modifier keys) this flag deliberately does
    /// not encode.</summary>
    public bool PasteOnClick { get; set; } = true;

    /// <summary>Allowed range for <see cref="IndicatorOpacityPercent"/>; SettingsStore.Load
    /// clamps into this range. 下限 30 は「設定したのに全く見えない」事故防止 (v1.5)。</summary>
    public const int MinIndicatorOpacityPercent = 30;
    public const int MaxIndicatorOpacityPercent = 100;

    /// <summary>インジケーター基準色 "#RRGGBB" (v1.5)。ここから塗り/縁/リムを導出する
    /// のは IndicatorPalette.Resolve ただ 1 か所。パース不能な値は SettingsStore.Load が
    /// デフォルトに置き換えるので、読む側は常にパース可能とみなしてよい。</summary>
    public string IndicatorColor { get; set; } = IndicatorPalette.DefaultColorHex;

    /// <summary>フラッシュのピーク不透明度 (%) (v1.5)。IndicatorWindow が 1.0 の代わりに
    /// この値 /100 をピークに使う。</summary>
    public int IndicatorOpacityPercent { get; set; } = 100;

    /// <summary>自動開始デフォルト有効化 (v1.5) の一回限り移行が済んだか。
    /// <see cref="SettingsMigration.ApplyAutoStartDefault"/> だけが立てる。</summary>
    public bool AutoStartDefaultMigrated { get; set; } = false;

    /// <summary>シェルフのピン止め (v1.5 追補): true の間は自動格納のカウントダウンを
    /// 起動しない (ShelfRetract.ShouldArm)。ヘッダーのピンボタンが唯一の書き込み手で、
    /// App.SetShelfPinned 経由で永続化される。PinnedExpanded と同じ view-state パターン。</summary>
    public bool ShelfPinned { get; set; } = false;

    /// <summary>v1.6: 自動バックアップ (日次 + 終了時) を有効にするか。手動/退避には影響しない。</summary>
    public bool AutoBackupEnabled { get; set; } = true;

    /// <summary>v1.6: 最後に日次自動バックアップを取った日 ("yyyy-MM-dd"、ローカル日付)。
    /// 同日 2 回目の日次バックアップを抑止するための記録で、空文字は「まだ一度もない」。</summary>
    public string LastAutoBackupDate { get; set; } = "";
}
