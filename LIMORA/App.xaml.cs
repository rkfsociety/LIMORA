using System.Diagnostics;
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
    private const string InstanceMutexName = "Local\\LIMORA";
    private const string AppDataRelaunchArgument = "--appdata-relaunch";
    private Mutex? _mutex;
    private AppServices? _services;
    private bool _ownsMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var created);
        _ownsMutex = created;
        if (!created && e.Args.Contains(AppDataRelaunchArgument, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                _ownsMutex = _mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
            }
        }

        if (!_ownsMutex)
        {
            Shutdown();
            return;
        }

        var logger = new LocalLogger();
        if (TryRelaunchFromAppData(e, logger)) return;

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

    private bool TryRelaunchFromAppData(StartupEventArgs e, LocalLogger logger)
    {
        var executablePath = Environment.ProcessPath;
#pragma warning disable IL3000 // Empty Assembly.Location identifies the published single-file executable.
        var isSingleFile = string.IsNullOrEmpty(typeof(App).Assembly.Location);
#pragma warning restore IL3000
        if (string.IsNullOrWhiteSpace(executablePath) ||
            !string.Equals(Path.GetFileName(executablePath), "LIMORA.exe", StringComparison.OrdinalIgnoreCase) ||
            !isSingleFile)
            return false;

        var installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LIMORA");
        var installedPath = Path.Combine(installDirectory, "LIMORA.exe");
        if (string.Equals(Path.GetFullPath(executablePath), Path.GetFullPath(installedPath), StringComparison.OrdinalIgnoreCase))
            return false;

        string? stagedPath = null;
        try
        {
            Directory.CreateDirectory(installDirectory);

            var sourceVersion = typeof(App).Assembly.GetName().Version ?? new Version(0, 0);
            var installedVersion = File.Exists(installedPath) &&
                                   Version.TryParse(FileVersionInfo.GetVersionInfo(installedPath).FileVersion, out var parsedVersion)
                ? parsedVersion
                : new Version(0, 0);

            if (installedVersion < sourceVersion)
            {
                stagedPath = Path.Combine(installDirectory, $"LIMORA-{Guid.NewGuid():N}.tmp");
                File.Copy(executablePath, stagedPath);
                File.Move(stagedPath, installedPath, overwrite: true);
                stagedPath = null;
            }

            var startInfo = new ProcessStartInfo(installedPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = installDirectory
            };
            if (e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
                startInfo.ArgumentList.Add("--startup");
            startInfo.ArgumentList.Add(AppDataRelaunchArgument);

            using var relaunched = Process.Start(startInfo);
            if (relaunched is null)
                throw new InvalidOperationException("Could not start LIMORA from its AppData installation.");

            _mutex!.ReleaseMutex();
            _ownsMutex = false;
            Shutdown();
            return true;
        }
        catch (Exception ex)
        {
            if (stagedPath is not null)
            {
                try { File.Delete(stagedPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            logger.Write("error", "appdata_install_failed", ex.GetType().Name);
            System.Windows.MessageBox.Show(
                $"LIMORA could not copy itself to {installDirectory}. This launch will continue from its current location.",
                "LIMORA installation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }
}
