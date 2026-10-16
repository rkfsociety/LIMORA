using System.Drawing;
using Forms = System.Windows.Forms;
using LIMORA.Models;

namespace LIMORA.Services.Windows;

public sealed class TrayService : IDisposable
{
    private readonly AppServices _services;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _showOverlayItem;
    private readonly Forms.ToolStripMenuItem _startWithWindowsItem;
    private readonly Forms.ToolStripMenuItem _calibrateItem;
    private readonly Forms.ToolStripMenuItem _checkForUpdatesItem;
    private readonly Forms.ToolStripMenuItem _automaticPositionItem;
    private readonly Dictionary<DisplayMode, Forms.ToolStripMenuItem> _displayItems = [];
    private readonly Dictionary<OverlayAnchor, Forms.ToolStripMenuItem> _anchorItems = [];
    private readonly Forms.ContextMenuStrip _menu;
    private readonly MemoryStream _iconStream;

    public TrayService(AppServices services)
    {
        _services = services;
        _showOverlayItem = new Forms.ToolStripMenuItem("Show limits") { CheckOnClick = true };
        _showOverlayItem.Click += (_, _) => _services.ToggleOverlay();

        var displayMenu = new Forms.ToolStripMenuItem("Display mode");
        foreach (var mode in Enum.GetValues<DisplayMode>())
        {
            var item = new Forms.ToolStripMenuItem(mode.ToString()) { CheckOnClick = true };
            item.Click += (_, _) => _services.SetDisplayMode(mode);
            _displayItems[mode] = item;
            displayMenu.DropDownItems.Add(item);
        }

        var positionMenu = new Forms.ToolStripMenuItem("Position");
        _automaticPositionItem = new Forms.ToolStripMenuItem("Automatic") { CheckOnClick = true };
        _automaticPositionItem.Click += (_, _) => _services.SetAutomaticPosition();
        positionMenu.DropDownItems.Add(_automaticPositionItem);
        positionMenu.DropDownItems.Add(new Forms.ToolStripSeparator());
        foreach (var anchor in Enum.GetValues<OverlayAnchor>())
        {
            var item = new Forms.ToolStripMenuItem(anchor.ToString()) { CheckOnClick = true };
            item.Click += (_, _) => _services.SetAnchor(anchor);
            _anchorItems[anchor] = item;
            positionMenu.DropDownItems.Add(item);
        }

        _calibrateItem = new Forms.ToolStripMenuItem("Calibrate position");
        _calibrateItem.Click += (_, _) => _services.ToggleCalibration();
        var refreshItem = new Forms.ToolStripMenuItem("Refresh now");
        refreshItem.Click += (_, _) => _services.RefreshNow();
        var settingsItem = new Forms.ToolStripMenuItem("Settings…");
        settingsItem.Click += (_, _) => _services.ShowSettings();
        _checkForUpdatesItem = new Forms.ToolStripMenuItem("Check for updates");
        _checkForUpdatesItem.Click += CheckForUpdates;
        _startWithWindowsItem = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        _startWithWindowsItem.Click += (_, _) => _services.SetStartWithWindows(_startWithWindowsItem.Checked);
        var exitItem = new Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => System.Windows.Application.Current.Shutdown();

        _menu = new Forms.ContextMenuStrip();
        _menu.Items.Add(new Forms.ToolStripMenuItem("LIMORA") { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_showOverlayItem);
        _menu.Items.Add(displayMenu);
        _menu.Items.Add(positionMenu);
        _menu.Items.Add(_calibrateItem);
        _menu.Items.Add(refreshItem);
        _menu.Items.Add(settingsItem);
        _menu.Items.Add(_checkForUpdatesItem);
        _menu.Items.Add(_startWithWindowsItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(exitItem);
        _menu.Opening += (_, _) => SyncMenuState();

        var iconResource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/limora.ico"))
                           ?? throw new InvalidOperationException("The LIMORA icon resource is missing.");
        _iconStream = new MemoryStream();
        iconResource.Stream.CopyTo(_iconStream);
        iconResource.Stream.Dispose();
        _iconStream.Position = 0;

        _icon = new Forms.NotifyIcon
        {
            Icon = new Icon(_iconStream),
            Text = "LIMORA · Codex limits",
            ContextMenuStrip = _menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => _services.ShowSettings();
    }

    private void SyncMenuState()
    {
        _showOverlayItem.Checked = _services.Settings.ShowOverlay;
        _startWithWindowsItem.Checked = _services.Settings.StartWithWindows;
        _calibrateItem.Checked = _services.IsCalibrating;
        _calibrateItem.Text = _services.IsCalibrating ? "Finish calibration" : "Calibrate position";
        _automaticPositionItem.Checked = _services.Settings.AutomaticPosition;
        foreach (var pair in _displayItems) pair.Value.Checked = pair.Key == _services.Settings.DisplayMode;
        foreach (var pair in _anchorItems)
        {
            pair.Value.Checked = !_services.Settings.AutomaticPosition && pair.Key == _services.Settings.Anchor;
            pair.Value.Enabled = !_services.Settings.AutomaticPosition;
        }
    }

    public void SetStatus(string text)
    {
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowBalloonTip(string title, string text) =>
        _icon.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.Info);

    private async void CheckForUpdates(object? sender, EventArgs e)
    {
        _checkForUpdatesItem.Enabled = false;
        _checkForUpdatesItem.Text = "Checking for updates…";
        try
        {
            await _services.CheckForUpdatesAsync();
        }
        finally
        {
            _checkForUpdatesItem.Text = "Check for updates";
            _checkForUpdatesItem.Enabled = true;
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _iconStream.Dispose();
        _menu.Dispose();
    }
}
