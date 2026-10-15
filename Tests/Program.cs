using System.Drawing;
using System.Text.Json;
using LIMORA.Infrastructure;
using LIMORA.Models;
using LIMORA.Services.Codex;
using LIMORA.Services.Settings;
using LIMORA.Services.Windows;
using LIMORA.ViewModels;

var tests = new (string Name, Action Run)[]
{
    ("usedPercent 0 means 100% remaining", () => Equal(100, RateLimitParser.GetRemainingPercent(0))),
    ("usedPercent 27 means 73% remaining", () => Equal(73, RateLimitParser.GetRemainingPercent(27))),
    ("usedPercent 100 means 0% remaining", () => Equal(0, RateLimitParser.GetRemainingPercent(100))),
    ("parses primary and secondary buckets", ParsesPrimaryAndSecondary),
    ("finds windows across additional limit IDs", ParsesMultipleLimitIds),
    ("formats reset times", FormatsResetTime),
    ("unavailable snapshot is restored when Codex returns online", RestoresOnlineStateAfterUnavailableSnapshot),
    ("round-trips settings", RoundTripsSettings),
    ("positions relative to the Codex window", PositionsRelativeToWindow),
    ("positions above the detected composer", PositionsByComposer),
    ("clamps the overlay inside narrow windows", ClampsNarrowWindow)
};

foreach (var test in tests)
{
    test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"Passed {tests.Length} LIMORA tests.");
if (args.Contains("--app-server-smoke", StringComparer.OrdinalIgnoreCase))
    await VerifyLocalAppServer();

static void ParsesPrimaryAndSecondary()
{
    var snapshot = RateLimitParser.Parse("""
        {"rateLimits":{"planType":"plus","primary":{"usedPercent":27,"windowDurationMins":300,"resetsAt":1800000000},"secondary":{"usedPercent":59,"windowDurationMins":10080,"resetsAt":1800600000}},"ordinaryUsageAllowed":true,"rateLimitResetCredits":{"availableCount":2}}
        """)!;
    Equal(2, snapshot.Windows.Count);
    Equal("5h", snapshot.Windows[0].Label);
    Equal(73, snapshot.Windows[0].RemainingPercent);
    Equal("Week", snapshot.Windows[1].Label);
    Equal(41, snapshot.Windows[1].RemainingPercent);
    Equal("plus", snapshot.PlanType);
    Equal(2, snapshot.ResetCreditsAvailable);
    Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), snapshot.Windows[0].ResetsAt);
}

static void ParsesMultipleLimitIds()
{
    var snapshot = RateLimitParser.Parse("""
        {"rateLimits":{"primary":{"usedPercent":5,"windowDurationMins":10080}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":5,"windowDurationMins":10080}},"codex-model":{"primary":{"usedPercent":27,"windowDurationMins":300},"secondary":{"usedPercent":59,"windowDurationMins":10080}}}}
        """)!;
    Equal(2, snapshot.Windows.Count);
    Equal(73, snapshot.Windows[0].RemainingPercent);
    Equal(41, snapshot.Windows[1].RemainingPercent);
}

static void FormatsResetTime()
{
    var now = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
    Equal("2h 14m", OverlayViewModel.FormatRemaining(now.AddHours(2).AddMinutes(14), now));
    Equal("3d 8h", OverlayViewModel.FormatRemaining(now.AddDays(3).AddHours(8), now));
    Equal("now", OverlayViewModel.FormatRemaining(now.AddSeconds(-1), now));
}

static void RestoresOnlineStateAfterUnavailableSnapshot()
{
    var viewModel = new OverlayViewModel(new AppSettings());
    viewModel.SetCodexOnline(false);
    viewModel.SetSnapshot(null);
    viewModel.SetCodexOnline(true);
    Equal("Limits unavailable", viewModel.Text);

    var snapshot = RateLimitParser.Parse("""{"rateLimits":{"primary":{"usedPercent":27,"windowDurationMins":300}}}""")!;
    viewModel.SetSnapshot(snapshot);
    True(viewModel.Text.Contains("73%", StringComparison.Ordinal), "A successful snapshot should replace the unavailable state.");
}

static void RoundTripsSettings()
{
    var settings = new AppSettings
    {
        DisplayMode = DisplayMode.Detailed,
        Anchor = OverlayAnchor.BottomRight,
        AutomaticPosition = false,
        OffsetX = 16.5,
        OffsetY = -91,
        RefreshIntervalSeconds = 60,
        StartWithWindows = true,
        ShowResetTime = false,
        WarningThreshold = 55,
        CriticalThreshold = 18
    };
    var json = SettingsSerialization.Serialize(settings);
    var roundTrip = SettingsSerialization.Deserialize(json);
    Equal(settings.DisplayMode, roundTrip.DisplayMode);
    Equal(settings.Anchor, roundTrip.Anchor);
    Equal(settings.AutomaticPosition, roundTrip.AutomaticPosition);
    Equal(settings.OffsetX, roundTrip.OffsetX);
    Equal(settings.OffsetY, roundTrip.OffsetY);
    Equal(settings.RefreshIntervalSeconds, roundTrip.RefreshIntervalSeconds);
    Equal(settings.StartWithWindows, roundTrip.StartWithWindows);
    Equal(settings.ShowResetTime, roundTrip.ShowResetTime);
}

static void PositionsRelativeToWindow()
{
    var settings = new AppSettings { AutomaticPosition = false, Anchor = OverlayAnchor.BottomCenter, OffsetY = -100 };
    var result = OverlayPositionCalculator.Calculate(new Rectangle(100, 50, 1200, 900), new Size(240, 40), 96, settings);
    Equal(new Point(580, 810), result);
}

static void PositionsByComposer()
{
    var settings = new AppSettings { AutomaticPosition = true };
    var result = OverlayPositionCalculator.Calculate(
        new Rectangle(100, 50, 1200, 900), new Size(240, 40), 96, settings,
        new Rectangle(200, 700, 900, 180));
    Equal(new Point(1048, 770), result);
}

static void ClampsNarrowWindow()
{
    var settings = new AppSettings { AutomaticPosition = true };
    var result = OverlayPositionCalculator.Calculate(
        new Rectangle(100, 50, 260, 150), new Size(240, 40), 96, settings,
        new Rectangle(100, 100, 220, 40));
    True(result.X >= 104 && result.X + 240 <= 356, "Overlay should remain in the narrow window's clamped horizontal range.");
    True(result.Y >= 54 && result.Y + 40 <= 196, "Overlay should remain in the narrow window's clamped vertical range.");
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {JsonSerializer.Serialize(expected)}, got {JsonSerializer.Serialize(actual)}.");
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task VerifyLocalAppServer()
{
    var executable = CodexExecutableLocator.Find();
    if (executable is null) throw new InvalidOperationException("No local Codex CLI was found.");
    var logger = new LocalLogger();
    using var provider = new CodexAppServerRateLimitProvider(executable, logger);
    await provider.RefreshAsync();
    Console.WriteLine($"PASS installed Codex app-server returned rate limits: {provider.HasCurrentSnapshot}");
    if (!provider.HasCurrentSnapshot) throw new InvalidOperationException("Codex app-server did not return a rate-limit snapshot.");
}
