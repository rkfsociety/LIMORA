using System.Text.Json;
using LIMORA.Models;

namespace LIMORA.Services.Codex;

public static class RateLimitParser
{
    public static LimitSnapshot? Parse(string json, DateTimeOffset? now = null)
    {
        using var document = JsonDocument.Parse(json);
        return Parse(document.RootElement, now);
    }

    public static int GetRemainingPercent(int usedPercent) => Math.Clamp(100 - usedPercent, 0, 100);

    public static LimitSnapshot? Parse(JsonElement response, DateTimeOffset? now = null)
    {
        if (response.ValueKind != JsonValueKind.Object)
            return null;

        var candidates = new List<(string Id, JsonElement Snapshot)>();
        if (TryGet(response, "rateLimitsByLimitId", out var multi) && multi.ValueKind == JsonValueKind.Object)
        {
            foreach (var bucket in multi.EnumerateObject().OrderBy(x => x.Name.Equals("codex", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                if (bucket.Value.ValueKind == JsonValueKind.Object)
                    candidates.Add((bucket.Name, bucket.Value));
        }

        if (TryGet(response, "rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            candidates.Insert(0, ("default", legacy));

        if (candidates.Count == 0)
            return null;

        var windows = new List<(string Id, LimitWindow Window)>();
        var fallbackPrimary = (LimitWindow?)null;
        var fallbackSecondary = (LimitWindow?)null;
        string? plan = null;
        bool? spendControl = null;
        string? reachedType = null;

        foreach (var (id, bucket) in candidates)
        {
            plan ??= GetString(bucket, "planType");
            spendControl ??= GetNullableBoolean(bucket, "spendControlReached");
            reachedType ??= GetString(bucket, "rateLimitReachedType");

            AddWindow(bucket, "primary", id, isPrimary: true, ref fallbackPrimary, ref fallbackSecondary, windows);
            AddWindow(bucket, "secondary", id, isPrimary: false, ref fallbackPrimary, ref fallbackSecondary, windows);
        }

        var fiveHour = windows.FirstOrDefault(w => w.Window.DurationMinutes == 300);
        var weekly = windows.Where(w => w.Window.DurationMinutes >= 10_000)
            .OrderBy(w => fiveHour.Window is not null && w.Id == fiveHour.Id ? 0 : 1)
            .ThenByDescending(w => w.Window.DurationMinutes)
            .Select(w => w.Window)
            .FirstOrDefault();
        var selected = new List<LimitWindow>();
        if (fiveHour.Window is not null) selected.Add(fiveHour.Window with { Label = "5h" });
        if (weekly is not null) selected.Add(weekly with { Label = weekly.DurationMinutes == 10_080 ? "Week" : FormatDurationLabel(weekly.DurationMinutes!.Value) });

        if (selected.Count == 0)
        {
            if (fallbackPrimary is not null) selected.Add(fallbackPrimary);
            if (fallbackSecondary is not null && (selected.Count == 0 || fallbackSecondary != fallbackPrimary)) selected.Add(fallbackSecondary);
        }
        else if (selected.Count == 1 && weekly is null && fallbackSecondary is not null &&
                 fallbackSecondary.DurationMinutes is not >= 10_000 &&
                 fallbackSecondary.DurationMinutes != selected[0].DurationMinutes)
        {
            selected.Add(fallbackSecondary);
        }

        if (selected.Count == 0)
            return null;

        var baseBucket = candidates.FirstOrDefault(c => c.Id.Equals("codex", StringComparison.OrdinalIgnoreCase)).Snapshot;
        if (baseBucket.ValueKind != JsonValueKind.Object)
            baseBucket = candidates[0].Snapshot;

        int? resetCredits = null;
        if (TryGet(response, "rateLimitResetCredits", out var creditSummary))
            resetCredits = GetInt32(creditSummary, "availableCount");

        return new LimitSnapshot(
            selected,
            plan,
            GetNullableBoolean(response, "ordinaryUsageAllowed"),
            spendControl,
            reachedType,
            resetCredits,
            now ?? DateTimeOffset.UtcNow);
    }

    public static string FormatDurationLabel(long minutes)
    {
        if (minutes == 10_080) return "Week";
        if (minutes >= 1_440 && minutes % 1_440 == 0) return $"{minutes / 1_440}d";
        if (minutes >= 60 && minutes % 60 == 0) return $"{minutes / 60}h";
        return $"{minutes}m";
    }

    private static void AddWindow(JsonElement bucket, string name, string id, bool isPrimary,
        ref LimitWindow? fallbackPrimary, ref LimitWindow? fallbackSecondary, List<(string Id, LimitWindow Window)> windows)
    {
        if (!TryGet(bucket, name, out var window) || window.ValueKind != JsonValueKind.Object)
            return;
        var used = GetInt32(window, "usedPercent");
        if (used is null) return;
        var minutes = GetInt64(window, "windowDurationMins");
        DateTimeOffset? reset = null;
        var epoch = GetInt64(window, "resetsAt");
        if (epoch is not null)
        {
            try { reset = DateTimeOffset.FromUnixTimeSeconds(epoch.Value); }
            catch (ArgumentOutOfRangeException) { }
        }

        var label = minutes is not null ? FormatDurationLabel(minutes.Value) : (isPrimary ? "5h" : "Week");
        var parsed = new LimitWindow(label, GetRemainingPercent(used.Value), reset, minutes);
        windows.Add((id, parsed));
        if (isPrimary && fallbackPrimary is null) fallbackPrimary = parsed;
        if (!isPrimary && fallbackSecondary is null) fallbackSecondary = parsed;
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static int? GetInt32(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static long? GetInt64(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.TryGetInt64(out var result) ? result : null;

    private static string? GetString(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetNullableBoolean(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
}
