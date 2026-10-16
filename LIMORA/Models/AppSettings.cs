namespace LIMORA.Models;

public enum DisplayMode { Compact, Bars, Detailed }
public enum OverlayAnchor { BottomCenter, BottomRight, BottomLeft }

public sealed class AppSettings
{
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Compact;
    public OverlayAnchor Anchor { get; set; } = OverlayAnchor.BottomCenter;
    public bool AutomaticPosition { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public bool ShowResetTime { get; set; } = true;
    public double OverlayFontSize { get; set; } = 16;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; } = -100;
    public int RefreshIntervalSeconds { get; set; } = 45;
    public bool StartWithWindows { get; set; }
    public int WarningThreshold { get; set; } = 50;
    public int CriticalThreshold { get; set; } = 20;

    public void Normalize()
    {
        if (!Enum.IsDefined(DisplayMode)) DisplayMode = DisplayMode.Compact;
        if (!Enum.IsDefined(Anchor)) Anchor = OverlayAnchor.BottomCenter;
        OverlayFontSize = double.IsFinite(OverlayFontSize) ? Math.Clamp(OverlayFontSize, 12, 24) : 16;
        RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 30, 300);
        WarningThreshold = Math.Clamp(WarningThreshold, 1, 99);
        CriticalThreshold = Math.Clamp(CriticalThreshold, 0, WarningThreshold - 1);
        OffsetX = double.IsFinite(OffsetX) ? Math.Clamp(OffsetX, -4000, 4000) : 0;
        OffsetY = double.IsFinite(OffsetY) ? Math.Clamp(OffsetY, -4000, 4000) : -100;
    }
}
