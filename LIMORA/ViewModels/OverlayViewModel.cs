using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using LIMORA.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace LIMORA.ViewModels;

public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private AppSettings _settings;
    private LimitSnapshot? _snapshot;
    private bool _codexOnline;
    private bool _calibrating;
    private string _text = "Codex offline";
    private Brush _foreground = new SolidColorBrush(Color.FromRgb(169, 169, 176));
    private string _tooltip = "Waiting for Codex Desktop.";

    public OverlayViewModel(AppSettings settings) => _settings = settings;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Text { get => _text; private set => Set(ref _text, value); }
    public double OverlayFontSize => _settings.OverlayFontSize;
    public Brush Foreground { get => _foreground; private set => Set(ref _foreground, value); }
    public string Tooltip { get => _tooltip; private set => Set(ref _tooltip, value); }
    public bool IsCalibrating { get => _calibrating; set { if (Set(ref _calibrating, value)) OnPropertyChanged(nameof(CalibrationAccent)); } }
    public Brush CalibrationAccent => IsCalibrating ? new SolidColorBrush(Color.FromRgb(169, 124, 242)) : new SolidColorBrush(Color.FromArgb(102, 126, 126, 136));

    public void SetCodexOnline(bool online)
    {
        _codexOnline = online;
        if (_snapshot is null) Text = online ? "Limits unavailable" : "Codex offline";
        if (!online) Tooltip = "Codex Desktop is not available.";
    }

    public void SetSnapshot(LimitSnapshot? snapshot)
    {
        _snapshot = snapshot;
        Rebuild();
    }

    public void UpdateSettings(AppSettings settings)
    {
        _settings = settings;
        OnPropertyChanged(nameof(OverlayFontSize));
        Rebuild();
    }
    public void RefreshCountdowns() => Rebuild();

    private void Rebuild()
    {
        if (_snapshot is null || _snapshot.Windows.Count == 0)
        {
            Text = _codexOnline ? "Limits unavailable" : "Codex offline";
            Foreground = new SolidColorBrush(Color.FromRgb(169, 169, 176));
            Tooltip = _codexOnline ? "Codex is online, but rate-limit data is unavailable." : "Waiting for Codex Desktop.";
            return;
        }

        var windows = _snapshot.Windows;
        Text = _settings.DisplayMode switch
        {
            DisplayMode.Bars => string.Join("   ", windows.Select(window => $"{ShortLabel(window.Label)} {MakeBar(window.RemainingPercent)} {window.RemainingPercent}%")),
            DisplayMode.Detailed when _settings.ShowResetTime => string.Join("    ", windows.Select(window =>
                $"{window.Label} {window.RemainingPercent}%" + (window.ResetsAt is { } reset ? $" · reset {FormatResetTime(reset)}" : string.Empty))),
            _ => string.Join("  ·  ", windows.Select(window => $"{window.Label} {window.RemainingPercent}%"))
        };

        var lowest = windows.Min(window => window.RemainingPercent);
        var color = lowest <= _settings.CriticalThreshold
            ? Color.FromRgb(242, 139, 130)
            : lowest <= _settings.WarningThreshold
                ? Color.FromRgb(230, 181, 102)
                : Color.FromRgb(184, 216, 189);
        Foreground = new SolidColorBrush(color);
        var plan = string.IsNullOrWhiteSpace(_snapshot.PlanType) ? "" : $"Plan: {_snapshot.PlanType}. ";
        Tooltip = plan + string.Join("; ", windows.Select(window =>
            $"{window.Label}: {window.RemainingPercent}% remaining" + (window.ResetsAt is { } reset ? $", resets at {FormatResetTime(reset)}" : "")));
        if (_snapshot.ResetCreditsAvailable is int credits)
            Tooltip += $". Reset credits: {credits}.";
        if (_snapshot.OrdinaryUsageAllowed is false)
            Tooltip += " The service reports that ordinary usage is currently blocked.";
        if (_snapshot.SpendControlReached is true)
            Tooltip += " The service reports that a spend control has been reached.";
        if (_snapshot.RateLimitReachedType is { } reached)
            Tooltip += $" Rate-limit state: {reached}.";
    }

    private static string ShortLabel(string label) => label.Equals("Week", StringComparison.OrdinalIgnoreCase) ? "W" : label;

    private static string MakeBar(int remaining)
    {
        const int width = 10;
        var filled = (int)Math.Round(remaining / 100d * width, MidpointRounding.AwayFromZero);
        return new string('█', filled) + new string('░', width - filled);
    }

    public static string FormatRemaining(DateTimeOffset resetAt, DateTimeOffset? now = null)
    {
        var remaining = resetAt - (now ?? DateTimeOffset.Now);
        if (remaining <= TimeSpan.Zero) return "now";
        if (remaining.TotalDays >= 1) return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h {remaining.Minutes:D2}m";
        return $"{Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";
    }

    public static string FormatResetTime(DateTimeOffset resetAt) => resetAt.ToLocalTime().ToString("dd.MM HH:mm");

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
