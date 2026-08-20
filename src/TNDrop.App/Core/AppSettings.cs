namespace TNDrop.Core;

public enum EdgeSide { Left, Right }
public enum TriggerAlign { Top, Center, Bottom }
public enum AutoDeletePolicy { Off, Hours1, Hours6, Hours24, Days7 }
public enum TextScale { Small, Normal, Medium, Large }
public enum IndicatorStyle { Beacon, Bar, Pulse, Corner }

public sealed class AppSettings
{
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
    public bool EdgeHintEnabled { get; set; } = true;
}
