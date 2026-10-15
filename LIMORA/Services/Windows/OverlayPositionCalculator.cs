using System.Drawing;
using LIMORA.Models;

namespace LIMORA.Services.Windows;

public static class OverlayPositionCalculator
{
    public static Point Calculate(Rectangle window, Size overlay, uint dpi, AppSettings settings, Rectangle? composer = null)
    {
        var scale = Math.Max(1, dpi) / 96d;
        int x;
        int y;
        if (settings.AutomaticPosition && composer is { } found)
        {
            var minX = window.Left + Math.Min(12, Math.Max(0, (window.Width - overlay.Width) / 2));
            var maxX = Math.Max(minX, window.Right - overlay.Width - 12);
            x = Math.Clamp(found.Right - overlay.Width, minX, maxX);
            y = found.Top - overlay.Height - 8;
        }
        else
        {
            x = settings.Anchor switch
            {
                OverlayAnchor.BottomLeft => window.Left + 18,
                OverlayAnchor.BottomRight => window.Right - overlay.Width - 18,
                _ => window.Left + (window.Width - overlay.Width) / 2
            };
            x += (int)Math.Round(settings.OffsetX * scale);
            y = window.Bottom - overlay.Height + (int)Math.Round(settings.OffsetY * scale);
        }

        x = Math.Clamp(x, window.Left + 4, Math.Max(window.Left + 4, window.Right - overlay.Width - 4));
        y = Math.Clamp(y, window.Top + 4, Math.Max(window.Top + 4, window.Bottom - overlay.Height - 4));
        return new Point(x, y);
    }
}
