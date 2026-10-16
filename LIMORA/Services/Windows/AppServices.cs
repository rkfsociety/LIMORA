using System.Drawing;
using System.Windows.Threading;
using LIMORA.Infrastructure;
using LIMORA.Models;
using LIMORA.Services.Codex;
using LIMORA.Services.Settings;
using LIMORA.Services.Updates;
using LIMORA.ViewModels;
using LIMORA.Views;
using DipPoint = System.Windows.Point;

namespace LIMORA.Services.Windows;

public sealed class AppServices : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly CodexAppServerRateLimitProvider _provider;
    private readonly CodexWindowTracker _tracker;
    private readonly OverlayViewModel _viewModel;
    private readonly OverlayWindow _overlay;
    private readonly LocalLogger _logger;
    private readonly UpdateService _updateService = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _notificationTimer;
    private readonly DispatcherTimer _countdownTimer;
    private readonly DispatcherTimer _composerRetryTimer;
    private TargetWindowState? _latestState;
    private Rectangle? _composerRelative;
    private System.Drawing.Size _lastWindowSize;
    private uint _lastDpi;
    private nint _lastWindow;
    private long _positionRevision;
    private int _composerRetries;
    private bool _composerSearchCompleted;
    private bool _disposed;

    public AppServices(SettingsService settingsService, AppSettings settings,
        CodexAppServerRateLimitProvider provider, CodexWindowTracker tracker,
        OverlayViewModel viewModel, OverlayWindow overlay, LocalLogger logger)
    {
        _settingsService = settingsService;
        Settings = settings;
        _provider = provider;
        _tracker = tracker;
        _viewModel = viewModel;
        _overlay = overlay;
        _logger = logger;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(Settings.RefreshIntervalSeconds) };
        _refreshTimer.Tick += (_, _) => { if (IsTargetVisible) _ = RefreshAsync(); };
        _notificationTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _notificationTimer.Tick += (_, _) => { _notificationTimer.Stop(); if (IsTargetVisible) _ = RefreshAsync(); };
        _countdownTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
        _countdownTimer.Tick += (_, _) => _viewModel.RefreshCountdowns();
        _composerRetryTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        _composerRetryTimer.Tick += (_, _) =>
        {
            _composerRetryTimer.Stop();
            if (!_composerSearchCompleted && IsTargetVisible && _composerRetries < 3) _ = SearchForComposerAsync(_latestState!, _positionRevision);
        };

        _provider.SnapshotChanged += OnSnapshotChanged;
        _provider.UpdatedNotification += OnRateLimitNotification;
        _tracker.StateChanged += OnWindowStateChanged;
        _overlay.SourceInitialized += (_, _) =>
        {
            _tracker.SetOverlayWindow(_overlay);
            _tracker.SetClickThrough(enabled: true);
        };
        _refreshTimer.Start();
        _countdownTimer.Start();
    }

    public AppSettings Settings { get; private set; }
    public TrayService? Tray { get; set; }
    public bool IsTargetVisible => _latestState is { IsMinimized: false } && WindowsNative.IsWindow(_latestState.Handle);
    public bool IsCalibrating { get; private set; }

    public void OnWindowStateChanged(TargetWindowState? state)
    {
        if (_disposed) return;
        if (state is null || state.IsMinimized)
        {
            _latestState = state;
            _composerRetryTimer.Stop();
            if (state is null)
            {
                _composerRelative = null;
                _lastWindow = 0;
                _composerSearchCompleted = false;
            }
            _viewModel.SetCodexOnline(false);
            Tray?.SetStatus("Codex offline");
            _overlay.Hide();
            _ = _provider.SuspendAsync();
            return;
        }

        var becameVisible = _latestState is null || _latestState.IsMinimized || _latestState.Handle != state.Handle;
        var geometryChanged = _lastWindow != state.Handle || _lastWindowSize != state.Bounds.Size || _lastDpi != state.Dpi;
        _latestState = state;
        _viewModel.SetCodexOnline(true);
        Tray?.SetStatus("LIMORA · loading limits");
        if (geometryChanged)
        {
            _lastWindow = state.Handle;
            _lastWindowSize = state.Bounds.Size;
            _lastDpi = state.Dpi;
            _composerRelative = null;
            _composerSearchCompleted = false;
            _composerRetries = 0;
            _positionRevision++;
        }

        _tracker.SetClickThrough(enabled: !IsCalibrating);
        if (Settings.ShowOverlay || IsCalibrating) _overlay.Show();
        else _overlay.Hide();
        Position(state, GetComposerBounds(state));

        if (geometryChanged && Settings.AutomaticPosition)
        {
            _ = SearchForComposerAsync(state, _positionRevision);
        }
        if (becameVisible || !_provider.HasCurrentSnapshot)
        {
            _ = RefreshAsync();
        }
    }

    public void ToggleOverlay()
    {
        Settings.ShowOverlay = !Settings.ShowOverlay;
        SaveSettings();
        if (!Settings.ShowOverlay && !IsCalibrating) _overlay.Hide();
        else if (IsTargetVisible)
        {
            _overlay.Show();
            Position(_latestState!, GetComposerBounds(_latestState!));
        }
    }

    public void SetDisplayMode(DisplayMode mode)
    {
        Settings.DisplayMode = mode;
        _viewModel.UpdateSettings(Settings);
        SaveSettings();
    }

    public void SetAnchor(OverlayAnchor anchor)
    {
        Settings.Anchor = anchor;
        Settings.AutomaticPosition = false;
        SaveSettings();
        if (_latestState is { IsMinimized: false } state) Position(state, null);
    }

    public void SetAutomaticPosition()
    {
        Settings.AutomaticPosition = true;
        _composerRelative = null;
        _composerSearchCompleted = false;
        _composerRetries = 0;
        _positionRevision++;
        SaveSettings();
        if (_latestState is { IsMinimized: false } state)
        {
            Position(state, null);
            _ = SearchForComposerAsync(state, _positionRevision);
        }
    }

    public void SetStartWithWindows(bool enabled)
    {
        Settings.StartWithWindows = enabled;
        SaveSettings();
    }

    public void RefreshNow() => _ = RefreshAsync();

    public async Task CheckForUpdatesAsync()
    {
        try
        {
            var update = await _updateService.FindAvailableUpdateAsync();
            if (update is null)
            {
                System.Windows.MessageBox.Show(
                    $"LIMORA is up to date (v{_updateService.CurrentVersion.ToString(3)}).",
                    "LIMORA updates", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var executablePath = Environment.ProcessPath;
            if (executablePath is null || !string.Equals(Path.GetFileName(executablePath), "LIMORA.exe", StringComparison.OrdinalIgnoreCase))
            {
                System.Windows.MessageBox.Show(
                    "Updates can only be installed from the published LIMORA.exe application.",
                    "LIMORA updates", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            var sizeInMegabytes = Math.Ceiling(update.Size / (1024d * 1024d));
            var answer = System.Windows.MessageBox.Show(
                $"LIMORA v{update.Version.ToString(3)} is available ({sizeInMegabytes:N0} MB). Download and install it now? LIMORA will restart when the download is verified.",
                "LIMORA update available", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question, System.Windows.MessageBoxResult.No);
            if (answer != System.Windows.MessageBoxResult.Yes) return;

            Tray?.ShowBalloonTip("LIMORA update", "Downloading and verifying the update. LIMORA will restart when it is ready.");
            var stagedPath = await _updateService.DownloadAndStageAsync(update, executablePath);
            _logger.Write("info", "update_download_verified", $"Verified LIMORA {update.Version.ToString(3)}.");
            _updateService.StartInstallAfterExit(stagedPath, executablePath, Environment.ProcessId);
            _logger.Write("info", "update_install_started", $"Installing LIMORA {update.Version.ToString(3)} after shutdown.");
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            if (System.Windows.Application.Current.Dispatcher.HasShutdownStarted) return;
            _logger.Write("error", "update_failed", ex.GetType().Name);
            System.Windows.MessageBox.Show(
                "LIMORA could not check or install the update. Check your internet connection, disk space, and write access to the LIMORA folder, then try again.",
                "LIMORA update", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    public void BeginCalibration()
    {
        if (!IsTargetVisible) return;
        IsCalibrating = true;
        _viewModel.IsCalibrating = true;
        _tracker.SetClickThrough(enabled: false);
        _overlay.SetCalibration(true);
        _overlay.Show();
        Position(_latestState!, GetComposerBounds(_latestState!));
    }

    public void ToggleCalibration()
    {
        if (IsCalibrating) EndCalibration();
        else BeginCalibration();
    }

    public void EndCalibration()
    {
        IsCalibrating = false;
        _viewModel.IsCalibrating = false;
        _overlay.SetCalibration(false);
        _tracker.SetClickThrough(enabled: true);
        if (!Settings.ShowOverlay) _overlay.Hide();
    }

    public void SaveCalibration(double screenLeft, double screenTop)
    {
        if (_latestState is not { } state) return;
        var scale = state.Dpi / 96d;
        var width = _overlay.ActualWidth * scale;
        var height = _overlay.ActualHeight * scale;
        var baseLeft = Settings.Anchor switch
        {
            OverlayAnchor.BottomLeft => state.Bounds.Left + 18,
            OverlayAnchor.BottomRight => state.Bounds.Right - width - 18,
            _ => state.Bounds.Left + (state.Bounds.Width - width) / 2
        };
        Settings.OffsetX = (screenLeft - baseLeft) / scale;
        Settings.OffsetY = (screenTop - (state.Bounds.Bottom - height)) / scale;
        Settings.AutomaticPosition = false;
        SaveSettings();
        EndCalibration();
        _logger.Write("info", "position_calibrated", "Overlay offsets were saved relative to the Codex window.");
    }

    public void ShowSettings()
    {
        var restoreOverlay = _overlay.IsVisible;
        if (restoreOverlay) _overlay.Hide();
        var dialog = new SettingsWindow(Settings);
        dialog.SettingsSaved += ApplySettings;
        dialog.ShowDialog();
        if (restoreOverlay && Settings.ShowOverlay && IsTargetVisible)
        {
            _overlay.Show();
            Position(_latestState!, GetComposerBounds(_latestState!));
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        Settings = settings;
        SaveSettings();
        _viewModel.UpdateSettings(Settings);
        _refreshTimer.Interval = TimeSpan.FromSeconds(Settings.RefreshIntervalSeconds);
        _positionRevision++;
        if (_latestState is { IsMinimized: false } state)
        {
            if (Settings.ShowOverlay) _overlay.Show(); else _overlay.Hide();
            Position(state, GetComposerBounds(state));
            if (Settings.AutomaticPosition) _ = SearchForComposerAsync(state, _positionRevision);
        }
    }

    private void SaveSettings()
    {
        try
        {
            _settingsService.Save(Settings);
            _settingsService.ApplyStartupRegistration(Settings.StartWithWindows, Environment.ProcessPath);
        }
        catch (Exception ex)
        {
            _logger.Write("error", "settings_save_failed", ex.GetType().Name);
        }
    }

    private void OnSnapshotChanged(LimitSnapshot? snapshot) => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;

        // RPC responses can arrive after a short-lived window event reports Codex offline.
        // Reconcile with the tracker's current state before publishing the snapshot.
        var currentState = _tracker.CurrentState;
        var codexOnline = currentState is { IsMinimized: false } &&
                          WindowsNative.IsWindow(currentState.Handle) &&
                          WindowsNative.IsWindowVisible(currentState.Handle);
        _latestState = currentState;
        _viewModel.SetCodexOnline(codexOnline);
        _viewModel.SetSnapshot(snapshot);
        Tray?.SetStatus(!codexOnline
            ? "Codex offline"
            : snapshot is null ? "LIMORA · limits unavailable" : "LIMORA · Codex limits available");
        if (!codexOnline)
        {
            _overlay.Hide();
            return;
        }

        if (Settings.ShowOverlay || IsCalibrating) _overlay.Show();
        else _overlay.Hide();
        Position(currentState!, GetComposerBounds(currentState!));
    });

    private void OnRateLimitNotification() => System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
    {
        _notificationTimer.Stop();
        _notificationTimer.Start();
    });

    private async Task RefreshAsync()
    {
        if (!IsTargetVisible || _disposed) return;
        await _provider.RefreshAsync();
    }

    private async Task SearchForComposerAsync(TargetWindowState state, long revision)
    {
        if (!IsTargetVisible || !Settings.AutomaticPosition) return;
        _composerRetries++;
        var bounds = await _tracker.FindComposerBoundsAsync(state.Handle, state.Bounds, CancellationToken.None);
        if (_disposed || revision != _positionRevision || _latestState is not { IsMinimized: false } current || current.Handle != state.Handle)
            return;

        if (bounds is { } composer)
        {
            _composerRelative = new Rectangle(composer.Left - current.Bounds.Left, composer.Top - current.Bounds.Top, composer.Width, composer.Height);
            _composerSearchCompleted = true;
            Position(current, GetComposerBounds(current));
            return;
        }

        if (_composerRetries >= 3)
        {
            _composerSearchCompleted = true;
            _logger.Write("info", "composer_detection_fallback", "The Codex composer could not be located; using the configured window anchor.");
        }
        else
        {
            _composerRetryTimer.Stop();
            _composerRetryTimer.Start();
        }
    }

    private Rectangle? GetComposerBounds(TargetWindowState state)
    {
        if (_composerRelative is not { } relative) return null;
        return new Rectangle(state.Bounds.Left + relative.Left, state.Bounds.Top + relative.Top, relative.Width, relative.Height);
    }

    private void Position(TargetWindowState state, Rectangle? composer)
    {
        if (!_overlay.IsVisible || (!Settings.ShowOverlay && !IsCalibrating)) return;
        _tracker.PositionOverlay(_overlay, state, Settings, composer);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        _notificationTimer.Stop();
        _countdownTimer.Stop();
        _composerRetryTimer.Stop();
        _provider.SnapshotChanged -= OnSnapshotChanged;
        _provider.UpdatedNotification -= OnRateLimitNotification;
        _tracker.StateChanged -= OnWindowStateChanged;
        Tray?.Dispose();
        _updateService.Dispose();
        _provider.Dispose();
        _tracker.Dispose();
    }
}
