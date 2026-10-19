using System.Globalization;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LIMORA.Models;

namespace LIMORA.Views;

public partial class SettingsWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = Copy(settings);
        DisplayModeBox.ItemsSource = Enum.GetValues<DisplayMode>();
        DisplayModeBox.SelectedItem = _settings.DisplayMode;
        AutomaticPositionRadio.IsChecked = _settings.AutomaticPosition;
        LeftPositionRadio.IsChecked = !_settings.AutomaticPosition &&
                                      (_settings.Anchor == OverlayAnchor.BottomLeft ||
                                       _settings.Anchor == OverlayAnchor.BottomCenter && _settings.OffsetX < 0);
        RightPositionRadio.IsChecked = !_settings.AutomaticPosition && LeftPositionRadio.IsChecked != true;
        ShowOverlayBox.IsChecked = _settings.ShowOverlay;
        ShowResetTimeBox.IsChecked = _settings.ShowResetTime;
        OverlayFontSizeSlider.Value = _settings.OverlayFontSize;
        OffsetXBox.Text = _settings.OffsetX.ToString(CultureInfo.InvariantCulture);
        OffsetYBox.Text = _settings.OffsetY.ToString(CultureInfo.InvariantCulture);
        RefreshIntervalBox.Text = _settings.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        WarningThresholdBox.Text = _settings.WarningThreshold.ToString(CultureInfo.InvariantCulture);
        CriticalThresholdBox.Text = _settings.CriticalThreshold.ToString(CultureInfo.InvariantCulture);
        StartWithWindowsBox.IsChecked = _settings.StartWithWindows;
        AutomaticPositionRadio.Checked += (_, _) => SetManualFieldsEnabled(false);
        LeftPositionRadio.Checked += (_, _) => SetManualFieldsEnabled(true);
        RightPositionRadio.Checked += (_, _) => SetManualFieldsEnabled(true);
        SetManualFieldsEnabled(!_settings.AutomaticPosition);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBarTheme();
    }

    public event Action<AppSettings>? SettingsSaved;

    private void OnOverlayFontSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverlayFontSizeValue is not null)
            OverlayFontSizeValue.Text = Math.Round(e.NewValue).ToString(CultureInfo.InvariantCulture);
    }

    private void SetManualFieldsEnabled(bool enabled)
    {
        OffsetXBox.IsEnabled = enabled;
        OffsetYBox.IsEnabled = enabled;
    }

    private void ApplyTitleBarTheme()
    {
        var handle = new WindowInteropHelper(this).Handle;
        SetDwmAttribute(handle, DwmwaUseImmersiveDarkMode, 1);
        SetDwmAttribute(handle, DwmwaCaptionColor, ToColorRef(0x18, 0x19, 0x1D));
        SetDwmAttribute(handle, DwmwaTextColor, ToColorRef(0xF1, 0xF2, 0xF4));
        SetDwmAttribute(handle, DwmwaBorderColor, ToColorRef(0x35, 0x38, 0x40));
    }

    private static int ToColorRef(byte red, byte green, byte blue) =>
        red | (green << 8) | (blue << 16);

    private static void SetDwmAttribute(nint handle, int attribute, int value)
    {
        var result = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        if (result < 0)
            Debug.WriteLine($"DwmSetWindowAttribute({attribute}) failed with HRESULT 0x{result:X8}.");
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(OffsetXBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetX) ||
            !double.TryParse(OffsetYBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetY) ||
            !int.TryParse(RefreshIntervalBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var refresh) ||
            !int.TryParse(WarningThresholdBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var warning) ||
            !int.TryParse(CriticalThresholdBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var critical))
        {
            StatusText.Text = "Enter valid numbers using a period for decimal values.";
            return;
        }
        if (refresh is < 30 or > 300 || warning is < 1 or > 99 || critical < 0 || critical >= warning)
        {
            StatusText.Text = "Refresh must be 30–300 seconds; thresholds must be 0 ≤ critical < warning ≤ 99.";
            return;
        }

        _settings.DisplayMode = DisplayModeBox.SelectedItem is DisplayMode mode ? mode : DisplayMode.Compact;
        _settings.AutomaticPosition = AutomaticPositionRadio.IsChecked == true;
        if (LeftPositionRadio.IsChecked == true) _settings.Anchor = OverlayAnchor.BottomLeft;
        if (RightPositionRadio.IsChecked == true) _settings.Anchor = OverlayAnchor.BottomRight;
        _settings.ShowOverlay = ShowOverlayBox.IsChecked == true;
        _settings.ShowResetTime = ShowResetTimeBox.IsChecked == true;
        _settings.OverlayFontSize = Math.Round(OverlayFontSizeSlider.Value);
        _settings.OffsetX = offsetX;
        _settings.OffsetY = offsetY;
        _settings.RefreshIntervalSeconds = refresh;
        _settings.WarningThreshold = warning;
        _settings.CriticalThreshold = critical;
        _settings.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        SettingsSaved?.Invoke(_settings);
        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private static AppSettings Copy(AppSettings settings) => new()
    {
        DisplayMode = settings.DisplayMode,
        Anchor = settings.Anchor,
        AutomaticPosition = settings.AutomaticPosition,
        ShowOverlay = settings.ShowOverlay,
        ShowResetTime = settings.ShowResetTime,
        OverlayFontSize = settings.OverlayFontSize,
        OffsetX = settings.OffsetX,
        OffsetY = settings.OffsetY,
        RefreshIntervalSeconds = settings.RefreshIntervalSeconds,
        StartWithWindows = settings.StartWithWindows,
        WarningThreshold = settings.WarningThreshold,
        CriticalThreshold = settings.CriticalThreshold
    };

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int valueSize);
}
