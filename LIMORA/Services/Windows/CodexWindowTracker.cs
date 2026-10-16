using System.Drawing;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using LIMORA.Infrastructure;
using LIMORA.Models;
using LIMORA.Services.Settings;
using LIMORA.Views;

namespace LIMORA.Services.Windows;

public sealed class CodexWindowTracker : IDisposable
{
    private readonly LocalLogger _logger;
    private readonly List<nint> _hooks = [];
    private readonly WindowsNative.WinEventProc _callback;
    private readonly DispatcherTimer _coalesceTimer;
    private readonly DispatcherTimer _discoveryTimer;
    private nint _target;
    private nint _overlay;
    private bool _disposed;

    public CodexWindowTracker(LocalLogger logger)
    {
        _logger = logger;
        _callback = OnWinEvent;
        _coalesceTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(75)
        };
        _coalesceTimer.Tick += (_, _) =>
        {
            _coalesceTimer.Stop();
            PublishCurrentState();
        };
        _discoveryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _discoveryTimer.Tick += (_, _) => DiscoverTarget();
    }

    public event Action<TargetWindowState?>? StateChanged;
    public TargetWindowState? CurrentState { get; private set; }

    public void SetOverlayWindow(Window overlay)
    {
        _overlay = new System.Windows.Interop.WindowInteropHelper(overlay).Handle;
    }

    public void Start()
    {
        Hook(WindowsNative.EventObjectShow);
        Hook(WindowsNative.EventObjectHide);
        Hook(WindowsNative.EventObjectDestroy);
        Hook(WindowsNative.EventObjectLocationChange);
        Hook(WindowsNative.EventSystemMinimizeStart);
        Hook(WindowsNative.EventSystemMinimizeEnd);
        _discoveryTimer.Start();
        DiscoverTarget();
    }

    private void Hook(uint eventType)
    {
        var hook = WindowsNative.SetWinEventHook(eventType, eventType, 0, _callback, 0, 0, WindowsNative.WineventOutOfContext);
        if (hook != 0) _hooks.Add(hook);
        else _logger.Write("warning", "window_event_hook_failed", eventType.ToString("X"));
    }

    private void OnWinEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (_disposed || hwnd == 0 || objectId != WindowsNative.ObjidWindow || childId != 0) return;
        if (_target != 0 && hwnd == _target)
        {
            if (eventType is WindowsNative.EventObjectLocationChange or WindowsNative.EventSystemMinimizeStart or WindowsNative.EventSystemMinimizeEnd)
                ScheduleStateRefresh();
            else if (eventType is WindowsNative.EventObjectHide or WindowsNative.EventObjectDestroy)
                ScheduleStateRefresh();
            return;
        }

        if (eventType == WindowsNative.EventObjectShow && _target == 0)
            Application.Current.Dispatcher.BeginInvoke(DiscoverTarget, DispatcherPriority.Background);
    }

    private void ScheduleStateRefresh()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _coalesceTimer.Stop();
            _coalesceTimer.Start();
        }, DispatcherPriority.Background);
    }

    private void DiscoverTarget()
    {
        if (_disposed) return;
        var found = WindowsNative.EnumerateTopLevelWindows()
            .Where(WindowsNative.IsWindowVisible)
            .Select(hwnd => (Hwnd: hwnd, Path: GetWindowPath(hwnd)))
            .Where(item => IsCodexDesktopPath(item.Path))
            .Select(item => item.Hwnd)
            .OrderByDescending(GetWindowArea)
            .FirstOrDefault();

        if (found == 0)
        {
            if (_target != 0)
            {
                _target = 0;
                CurrentState = null;
                _logger.Write("info", "codex_window_closed", "Codex Desktop main window is not available.");
                StateChanged?.Invoke(null);
            }
            return;
        }

        if (found == _target) return;

        _target = found;
        _logger.Write("info", "codex_window_found", "A Codex Desktop window was detected.");
        PublishCurrentState();
    }

    private void PublishCurrentState()
    {
        if (_disposed) return;
        if (_target == 0 || !WindowsNative.IsWindow(_target) || !WindowsNative.IsWindowVisible(_target))
        {
            DiscoverTarget();
            return;
        }

        if (!WindowsNative.GetWindowRect(_target, out var nativeRect)) return;
        var bounds = Rectangle.FromLTRB(nativeRect.Left, nativeRect.Top, nativeRect.Right, nativeRect.Bottom);
        var minimized = WindowsNative.IsIconic(_target);
        var placement = WindowsNative.WindowPlacement.Create();
        var maximized = WindowsNative.GetWindowPlacement(_target, ref placement) && placement.ShowCmd == 3;
        var dpi = WindowsNative.GetDpiForWindow(_target);
        if (dpi == 0) dpi = 96;
        CurrentState = new TargetWindowState(_target, bounds, minimized, maximized, dpi);
        StateChanged?.Invoke(CurrentState);
    }

    private static string? GetWindowPath(nint hwnd)
    {
        WindowsNative.GetWindowThreadProcessId(hwnd, out var processId);
        return WindowsNative.GetProcessImagePath(processId);
    }

    private static bool IsCodexDesktopPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.GetFileName(path).Equals("ChatGPT.exe", StringComparison.OrdinalIgnoreCase) &&
        path.Contains("\\OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);

    private static long GetWindowArea(nint hwnd)
    {
        return WindowsNative.GetWindowRect(hwnd, out var rect) ? (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top) : 0;
    }

    public async Task<Rectangle?> FindComposerBoundsAsync(nint hwnd, Rectangle windowBounds, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => FindComposerBounds(hwnd, windowBounds), cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _logger.Write("debug", "composer_detection_unavailable", ex.GetType().Name);
            return null;
        }
    }

    private static Rectangle? FindComposerBounds(nint hwnd, Rectangle windowBounds)
    {
        var root = AutomationElement.FromHandle(hwnd);
        var condition = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
        var elements = root.FindAll(TreeScope.Descendants, condition);
        Rectangle? best = null;
        var bestScore = double.MinValue;
        var examined = Math.Min(elements.Count, 250);
        for (var index = 0; index < examined; index++)
        {
            var rectangle = elements[index].Current.BoundingRectangle;
            if (rectangle.IsEmpty || rectangle.Width < windowBounds.Width * 0.22 || rectangle.Height < 22 || rectangle.Height > 500)
                continue;
            if (rectangle.Left < windowBounds.Left || rectangle.Right > windowBounds.Right ||
                rectangle.Top < windowBounds.Top + windowBounds.Height * 0.48 || rectangle.Bottom > windowBounds.Bottom)
                continue;

            var candidate = Rectangle.FromLTRB((int)rectangle.Left, (int)rectangle.Top, (int)rectangle.Right, (int)rectangle.Bottom);
            var score = rectangle.Width + (rectangle.Top - windowBounds.Top) * 1.8;
            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
            }
        }
        return best;
    }

    public void PositionOverlay(OverlayWindow overlay, TargetWindowState state, AppSettings settings, Rectangle? composerBounds)
    {
        if (_overlay == 0 || state.IsMinimized) return;
        var scale = state.Dpi / 96d;
        var width = Math.Max(1, (int)Math.Ceiling(overlay.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(overlay.ActualHeight * scale));
        var position = OverlayPositionCalculator.Calculate(state.Bounds, new System.Drawing.Size(width, height), state.Dpi, settings, composerBounds);
        WindowsNative.SetWindowPos(_overlay, WindowsNative.HwndTopmost, position.X, position.Y, width, height,
            WindowsNative.SwpNoActivate | WindowsNative.SwpShowWindow);
    }

    public void SetClickThrough(bool enabled)
    {
        if (_overlay == 0) return;
        var style = WindowsNative.GetWindowLongPtr(_overlay, WindowsNative.GwlExStyle).ToInt64();
        style |= WindowsNative.WsExNoActivate | WindowsNative.WsExToolWindow | WindowsNative.WsExLayered;
        if (enabled) style |= WindowsNative.WsExTransparent;
        else style &= ~WindowsNative.WsExTransparent;
        WindowsNative.SetWindowLongPtr(_overlay, WindowsNative.GwlExStyle, new nint(style));
    }

    public System.Windows.Point GetOverlayPositionDip(uint dpi)
    {
        if (!WindowsNative.GetWindowRect(_overlay, out var rectangle)) return default;
        var scale = dpi / 96d;
        return new System.Windows.Point(rectangle.Left / scale, rectangle.Top / scale);
    }

    public void Dispose()
    {
        _disposed = true;
        _coalesceTimer.Stop();
        _discoveryTimer.Stop();
        foreach (var hook in _hooks) WindowsNative.UnhookWinEvent(hook);
        _hooks.Clear();
    }
}
