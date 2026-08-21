namespace TNDrop.Core;

public enum EdgeSide { Left, Right }
public enum TriggerAlign { Top, Center, Bottom }
public enum AutoDeletePolicy { Off, Hours1, Hours6, Hours24, Days7 }
public enum TextScale { Small, Normal, Medium, Large }
public enum IndicatorStyle { Beacon, Bar, Pulse, Corner }

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
    public bool AutoStartEnabled { get; set; } = false;
    public bool IncognitoMode { get; set; } = false;
    public AutoDeletePolicy AutoDelete { get; set; } = AutoDeletePolicy.Off;
    public bool MoveToTopOnCopy { get; set; } = true;
    public TextScale TextScale { get; set; } = TextScale.Normal;
    public IndicatorStyle IndicatorStyle { get; set; } = IndicatorStyle.Beacon;
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
}
