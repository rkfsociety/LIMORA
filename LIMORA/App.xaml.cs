using System.Threading;
using System.Windows;
using LIMORA.Infrastructure;
using LIMORA.Services.Codex;
using LIMORA.Services.Settings;
using LIMORA.Services.Windows;
using LIMORA.ViewModels;
using LIMORA.Views;

namespace LIMORA.App;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private AppServices? _services;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(initiallyOwned: true, "Local\\LIMORA", out var created);
        _ownsMutex = created;
        if (!created)
        {
            Shutdown();
            return;
        }

        var logger = new LocalLogger();
        var settingsService = new SettingsService(logger);
        var settings = settingsService.Load();
        var executable = CodexExecutableLocator.Find();
        if (executable is null)
            logger.Write("warning", "codex_cli_not_found", "No compatible local Codex CLI executable was found.");

        var provider = new CodexAppServerRateLimitProvider(executable, logger);
        var tracker = new CodexWindowTracker(logger);
        var viewModel = new OverlayViewModel(settings);
        var overlay = new OverlayWindow(viewModel);
        _services = new AppServices(settingsService, settings, provider, tracker, viewModel, overlay, logger);
        var tray = new TrayService(_services);
        _services.Tray = tray;

        overlay.CalibrationFinished += (left, top) => _services.SaveCalibration(left, top);
        overlay.Show();
        overlay.Hide();

        try
        {
            settingsService.ApplyStartupRegistration(settings.StartWithWindows, Environment.ProcessPath);
        }
        catch (Exception ex)
        {
            logger.Write("warning", "startup_registration_failed", ex.GetType().Name);
        }

        tracker.Start();
        if (e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
            logger.Write("info", "started_with_windows", "LIMORA started from the current-user startup entry.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
