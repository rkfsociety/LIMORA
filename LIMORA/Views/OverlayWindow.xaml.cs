using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using LIMORA.Infrastructure;
using LIMORA.ViewModels;

namespace LIMORA.Views;

public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _viewModel;
    private HwndSource? _source;

    public OverlayWindow(OverlayViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
    }

    public event Action<double, double>? CalibrationFinished;

    public void SetCalibration(bool enabled)
    {
        _viewModel.IsCalibrating = enabled;
        if (enabled) Show();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _source = (HwndSource?)PresentationSource.FromVisual(this);
        _source?.AddHook(WindowHook);
    }

    private nint WindowHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WmMouseActivate = 0x0021;
        if (message == WmMouseActivate)
        {
            handled = true;
            return new nint(WindowsNative.MaNoActivate);
        }
        return 0;
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_viewModel.IsCalibrating) return;
        try
        {
            DragMove();
            if (WindowsNative.GetWindowRect(new WindowInteropHelper(this).Handle, out var rectangle))
                CalibrationFinished?.Invoke(rectangle.Left, rectangle.Top);
        }
        catch (InvalidOperationException)
        {
            // The mouse can be released before the drag loop captures it.
        }
        e.Handled = true;
    }
}
