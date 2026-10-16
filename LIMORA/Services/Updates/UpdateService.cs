using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LIMORA.Services.Updates;

public sealed class UpdateService : IDisposable
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/rkfsociety/LIMORA/releases/latest";
    private const long MaximumAssetSize = 512L * 1024 * 1024;
    private static readonly Regex StableTagPattern = new("^v\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant);
    private readonly HttpClient _httpClient = new() { Timeout = Timeout.InfiniteTimeSpan };

    public UpdateService()
    {
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LIMORA", CurrentVersion.ToString(3)));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version is { } version
        ? new Version(version.Major, version.Minor, Math.Max(0, version.Build))
        : new Version(0, 0, 0);

    public async Task<AvailableUpdate?> FindAvailableUpdateAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await _httpClient.GetAsync(LatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(content, cancellationToken: timeout.Token);
        var root = document.RootElement;

        var tag = root.GetProperty("tag_name").GetString();
        if (tag is null || !StableTagPattern.IsMatch(tag) || !Version.TryParse(tag[1..], out var latestVersion))
            throw new InvalidDataException("The latest LIMORA release has an invalid version tag.");

        if (latestVersion <= CurrentVersion) return null;

        var assets = root.GetProperty("assets");
        var asset = assets.EnumerateArray().FirstOrDefault(item =>
            string.Equals(item.GetProperty("name").GetString(), "LIMORA.exe", StringComparison.Ordinal));
        if (asset.ValueKind is JsonValueKind.Undefined)
            throw new InvalidDataException("The latest LIMORA release does not contain LIMORA.exe.");

        var expectedUrl = new Uri($"https://github.com/rkfsociety/LIMORA/releases/download/{tag}/LIMORA.exe");
        if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var downloadUri) ||
            downloadUri != expectedUrl)
            throw new InvalidDataException("The latest release asset URL is not the official LIMORA executable.");

        var digest = asset.GetProperty("digest").GetString();
        if (digest is null || !Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("The latest release does not provide a valid SHA-256 digest.");

        var size = asset.GetProperty("size").GetInt64();
        if (size is <= 0 or > MaximumAssetSize)
            throw new InvalidDataException("The latest release asset has an unexpected file size.");

        return new AvailableUpdate(latestVersion, downloadUri, digest[7..].ToLowerInvariant(), size);
    }

    public async Task<string> DownloadAndStageAsync(AvailableUpdate update, string executablePath)
    {
        var executableDirectory = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException("Could not locate the LIMORA installation directory.");
        var stagedPath = Path.Combine(executableDirectory, $".LIMORA-update-{Guid.NewGuid():N}.exe");

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            using var response = await _httpClient.GetAsync(update.DownloadUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var destination = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, timeout.Token);
                await destination.FlushAsync(timeout.Token);
            }

            var fileInfo = new FileInfo(stagedPath);
            if (fileInfo.Length != update.Size)
                throw new InvalidDataException("The downloaded LIMORA file size does not match the release metadata.");

            await using var verificationStream = File.OpenRead(stagedPath);
            var actualDigest = Convert.ToHexString(await SHA256.HashDataAsync(verificationStream, timeout.Token)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualDigest), Convert.FromHexString(update.Sha256)))
                throw new InvalidDataException("The downloaded LIMORA file failed SHA-256 verification.");

            return stagedPath;
        }
        catch
        {
            TryDelete(stagedPath);
            throw;
        }
    }

    public void StartInstallAfterExit(string stagedPath, string executablePath, int processId)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"LIMORA-update-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(scriptPath, UpdaterScript);

        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        var startInfo = new ProcessStartInfo(powershellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(executablePath);
        startInfo.ArgumentList.Add(stagedPath);

        try
        {
            using var helper = Process.Start(startInfo);
            if (helper is null)
                throw new InvalidOperationException("Could not start the LIMORA update helper.");
        }
        catch
        {
            TryDelete(scriptPath);
            throw;
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private const string UpdaterScript = """
        param([int]$ProcessId, [string]$ExecutablePath, [string]$StagedPath)
        $ErrorActionPreference = 'Stop'
        try {
            Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
            [System.IO.File]::Replace($StagedPath, $ExecutablePath, $null)
            Start-Process -FilePath $ExecutablePath
        }
        catch {
            Add-Type -AssemblyName PresentationFramework
            [System.Windows.MessageBox]::Show(
                "LIMORA downloaded the update but could not install it. The verified update is still available at:`n$StagedPath`n`n$($_.Exception.Message)",
                'LIMORA update', 'OK', 'Error') | Out-Null
            if (Test-Path -LiteralPath $ExecutablePath) { Start-Process -FilePath $ExecutablePath }
        }
        finally {
            Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
        }
        """;
}

public sealed record AvailableUpdate(Version Version, Uri DownloadUri, string Sha256, long Size);
