namespace LIMORA.Models;

public sealed record LimitWindow(string Label, int RemainingPercent, DateTimeOffset? ResetsAt, long? DurationMinutes);

public sealed record LimitSnapshot(
    IReadOnlyList<LimitWindow> Windows,
    string? PlanType,
    bool? OrdinaryUsageAllowed,
    bool? SpendControlReached,
    string? RateLimitReachedType,
    int? ResetCreditsAvailable,
    DateTimeOffset UpdatedAt);

public sealed record TargetWindowState(nint Handle, System.Drawing.Rectangle Bounds, bool IsMinimized, bool IsMaximized, uint Dpi);
