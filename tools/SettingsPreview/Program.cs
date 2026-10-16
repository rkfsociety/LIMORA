using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LIMORA.Models;
using LIMORA.Views;

internal static class Program
{
    private const uint PwRenderFullContent = 0x00000002;

    [STAThread]
    private static int Main(string[] args)
    {
        var outputPath = System.IO.Path.GetFullPath(args.Length > 0
            ? args[0]
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LIMORA-settings-preview.png"));

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var window = new SettingsWindow(new AppSettings())
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
            ShowActivated = false,
            ShowInTaskbar = false
        };

        try
        {
            // The real settings view is created off-screen; LIMORA's application startup is not run.
            window.Show();
            window.UpdateLayout();
            app.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            var handle = new WindowInteropHelper(window).Handle;
            if (!GetWindowRect(handle, out var bounds))
                throw new InvalidOperationException("Could not read the preview window bounds.");

            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var deviceContext = graphics.GetHdc();
                try
                {
                    if (!PrintWindow(handle, deviceContext, PwRenderFullContent))
                        throw new InvalidOperationException("Windows could not render the off-screen settings view.");
                }
                finally
                {
                    graphics.ReleaseHdc(deviceContext);
                }
            }

            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(outputPath)!);
            bitmap.Save(outputPath, ImageFormat.Png);
            Console.WriteLine(outputPath);
            return 0;
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out WindowRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
