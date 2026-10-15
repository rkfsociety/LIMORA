using Microsoft.Win32;

namespace LIMORA.Services.Codex;

public static class CodexExecutableLocator
{
    public static string? Find()
    {
        var packaged = FindPackagedDesktopCli();
        if (packaged is not null) return packaged;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? "codex.exe" : "codex");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string? FindPackagedDesktopCli()
    {
        try
        {
            var windowsApps = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var root = Path.Combine(windowsApps, "WindowsApps");
            if (!Directory.Exists(root)) return null;

            return Directory.EnumerateDirectories(root, "OpenAI.Codex_*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => Directory.GetLastWriteTimeUtc(path))
                .Select(path => Path.Combine(path, "app", "resources", "codex.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
