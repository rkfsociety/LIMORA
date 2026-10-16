using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
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
    private const string WatcherMutexName = "Local\\LIMORA-Watcher";
    private const string AppDataRelaunchArgument = "--appdata-relaunch";
    private const string WatcherArgument = "--watcher";
    private const string CodexSessionArgument = "--codex-session";
    private Mutex? _mutex;
    private Mutex? _watcherMutex;
    private AppServices? _services;
    private CodexWindowTracker? _watcherTracker;
    private DispatcherTimer? _watcherTimer;
    private bool _ownsMutex;
    private bool _ownsWatcherMutex;
    private bool _launchRequestedForCodexSession;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var logger = new LocalLogger();
        if (e.Args.Contains(WatcherArgument, StringComparer.OrdinalIgnoreCase) ||
            e.Args.Contains("--startup", StringComparer.OrdinalIgnoreCase))
        {
            StartWatcher(logger);
            return;
        }

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

        if (TryRelaunchFromAppData(e, logger)) return;

        StartWatcherProcess(logger);

        var settingsService = new SettingsService(logger);
        var settings = settingsService.Load();
        var executable = CodexExecutableLocator.Find();
        if (executable is null)
            logger.Write("warning", "codex_cli_not_found", "No compatible local Codex CLI executable was found.");

        var provider = new CodexAppServerRateLimitProvider(executable, logger);
        var tracker = new CodexWindowTracker(logger);
        var viewModel = new OverlayViewModel(settings);
        var overlay = new OverlayWindow(viewModel);
        var launchedForCodex = e.Args.Contains(CodexSessionArgument, StringComparer.OrdinalIgnoreCase);
        _services = new AppServices(settingsService, settings, provider, tracker, viewModel, overlay, logger, launchedForCodex);
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
        if (launchedForCodex && tracker.CurrentState is null)
            _services.OnWindowStateChanged(null);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        _watcherTimer?.Stop();
        _watcherTracker?.Dispose();
        if (_ownsWatcherMutex) _watcherMutex?.ReleaseMutex();
        _watcherMutex?.Dispose();
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
            foreach (var argument in new[] { WatcherArgument, "--startup", CodexSessionArgument })
                if (e.Args.Contains(argument, StringComparer.OrdinalIgnoreCase))
                    startInfo.ArgumentList.Add(argument);
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

    private void StartWatcherProcess(LocalLogger logger)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath)) return;

        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
            };
            startInfo.ArgumentList.Add(WatcherArgument);
            using var watcher = Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.Write("warning", "watcher_start_failed", ex.GetType().Name);
        }
    }

    private void StartWatcher(LocalLogger logger)
    {
        _watcherMutex = new Mutex(initiallyOwned: true, WatcherMutexName, out _ownsWatcherMutex);
        if (!_ownsWatcherMutex)
        {
            Shutdown();
            return;
        }

        try
        {
            var settingsService = new SettingsService(logger);
            var settings = settingsService.Load();
            settingsService.ApplyStartupRegistration(settings.StartWithWindows, Environment.ProcessPath);
        }
        catch (Exception ex)
        {
            logger.Write("warning", "startup_registration_failed", ex.GetType().Name);
        }

        _watcherTracker = new CodexWindowTracker(logger);
        _watcherTracker.Start();
        _watcherTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _watcherTimer.Tick += (_, _) => EnsureCodexSession(logger);
        _watcherTimer.Start();
        EnsureCodexSession(logger);
        logger.Write("info", "codex_watcher_started", "The background watcher is waiting for Codex Desktop.");
    }

    private void EnsureCodexSession(LocalLogger logger)
    {
        if (_watcherTracker?.CurrentState is null)
        {
            _launchRequestedForCodexSession = false;
            return;
        }

        if (_launchRequestedForCodexSession) return;
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath)) return;
        if (HasUiInstance(executablePath))
        {
            _launchRequestedForCodexSession = true;
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
            };
            startInfo.ArgumentList.Add(CodexSessionArgument);
            using var application = Process.Start(startInfo);
            _launchRequestedForCodexSession = application is not null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.Write("warning", "codex_session_start_failed", ex.GetType().Name);
        }
    }

    private static bool HasUiInstance(string executablePath)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executablePath)))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                var path = WindowsNative.GetProcessImagePath(checked((uint)process.Id));
                if (string.Equals(path, executablePath, StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        return false;
    }
}
