using System.Globalization;
using System.Windows;
using LIMORA.Models;

namespace LIMORA.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = Copy(settings);
        DisplayModeBox.ItemsSource = Enum.GetValues<DisplayMode>();
        AnchorBox.ItemsSource = Enum.GetValues<OverlayAnchor>();
        DisplayModeBox.SelectedItem = _settings.DisplayMode;
        AnchorBox.SelectedItem = _settings.Anchor;
        AutomaticPositionBox.IsChecked = _settings.AutomaticPosition;
        ShowOverlayBox.IsChecked = _settings.ShowOverlay;
        ShowResetTimeBox.IsChecked = _settings.ShowResetTime;
        OffsetXBox.Text = _settings.OffsetX.ToString(CultureInfo.InvariantCulture);
        OffsetYBox.Text = _settings.OffsetY.ToString(CultureInfo.InvariantCulture);
        RefreshIntervalBox.Text = _settings.RefreshIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        WarningThresholdBox.Text = _settings.WarningThreshold.ToString(CultureInfo.InvariantCulture);
        CriticalThresholdBox.Text = _settings.CriticalThreshold.ToString(CultureInfo.InvariantCulture);
        StartWithWindowsBox.IsChecked = _settings.StartWithWindows;
        AutomaticPositionBox.Checked += (_, _) => SetManualFieldsEnabled(false);
        AutomaticPositionBox.Unchecked += (_, _) => SetManualFieldsEnabled(true);
        SetManualFieldsEnabled(!_settings.AutomaticPosition);
    }

    public event Action<AppSettings>? SettingsSaved;

    private void SetManualFieldsEnabled(bool enabled)
    {
        AnchorBox.IsEnabled = enabled;
        OffsetXBox.IsEnabled = enabled;
        OffsetYBox.IsEnabled = enabled;
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
        _settings.Anchor = AnchorBox.SelectedItem is OverlayAnchor anchor ? anchor : OverlayAnchor.BottomCenter;
        _settings.AutomaticPosition = AutomaticPositionBox.IsChecked == true;
        _settings.ShowOverlay = ShowOverlayBox.IsChecked == true;
        _settings.ShowResetTime = ShowResetTimeBox.IsChecked == true;
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
        OffsetX = settings.OffsetX,
        OffsetY = settings.OffsetY,
        RefreshIntervalSeconds = settings.RefreshIntervalSeconds,
        StartWithWindows = settings.StartWithWindows,
        WarningThreshold = settings.WarningThreshold,
        CriticalThreshold = settings.CriticalThreshold
    };
}
