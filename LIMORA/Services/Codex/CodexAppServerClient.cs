using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using LIMORA.Infrastructure;

namespace LIMORA.Services.Codex;

internal sealed class CodexAppServerClient(string executablePath, LocalLogger logger) : IDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _requestId;
    private Process? _process;
    private StreamWriter? _stdin;
    private bool _initialized;

    public event Action? RateLimitsUpdated;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && _initialized) return;
        DisposeProcess();

        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("analytics.enabled=false");

        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) OnOutput(e.Data); };
        process.ErrorDataReceived += (_, _) => { };
        process.Exited += (_, _) => FailPending(new IOException("Codex app-server exited."));
        if (!process.Start()) throw new InvalidOperationException("Could not start the Codex app-server.");
        _process = process;
        _stdin = process.StandardInput;
        _stdin.AutoFlush = true;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var response = await RequestAsync("initialize", new
        {
            clientInfo = new { name = "limora", title = "LIMORA", version = "1.0.0" },
            capabilities = new { experimentalApi = false, optOutNotificationMethods = Array.Empty<string>() }
        }, cancellationToken).ConfigureAwait(false);
        if (TryGet(response, "error", out var error))
            throw new InvalidOperationException($"Codex app-server initialization failed ({GetErrorCode(error)}).");
        await WriteNotificationAsync("initialized", new { }, cancellationToken).ConfigureAwait(false);
        _initialized = true;
        logger.Write("info", "app_server_connected", "Connected to the local Codex app-server protocol.");
    }

    public Task<JsonElement> ReadRateLimitsAsync(CancellationToken cancellationToken) =>
        RequestAsync("account/rateLimits/read", new { excludeResetCreditDetails = true }, cancellationToken);

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new { method, id, params_ = parameters }, cancellationToken).ConfigureAwait(false);
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
            if (TryGet(response, "error", out var error))
                throw new InvalidOperationException($"Codex app-server request failed ({GetErrorCode(error)}).");
            return TryGet(response, "result", out var result) ? result.Clone() : response.Clone();
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task WriteNotificationAsync(string method, object parameters, CancellationToken cancellationToken) =>
        WriteAsync(new { method, params_ = parameters }, cancellationToken);

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var writer = _stdin ?? throw new IOException("Codex app-server input is closed.");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(message));
        var root = document.RootElement;
        var envelope = new Dictionary<string, object?> { ["method"] = root.GetProperty("method").GetString() };
        if (root.TryGetProperty("id", out var id)) envelope["id"] = id.Clone();
        if (root.TryGetProperty("params_", out var parameters)) envelope["params"] = parameters.Clone();
        var normalized = JsonSerializer.Serialize(envelope);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await writer.WriteLineAsync(normalized.AsMemory(), cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    private void OnOutput(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (TryGet(root, "id", out var idElement) && idElement.TryGetInt64(out var id) && _pending.TryGetValue(id, out var pending))
                pending.TrySetResult(root.Clone());
            else if (TryGet(root, "method", out var method) && method.ValueKind == JsonValueKind.String &&
                     method.GetString() == "account/rateLimits/updated")
                RateLimitsUpdated?.Invoke();
        }
        catch (JsonException)
        {
            logger.Write("warning", "app_server_invalid_message", "Codex app-server returned a non-JSON protocol line.");
        }
    }

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }

    private static string GetErrorCode(JsonElement error) =>
        TryGet(error, "code", out var code) ? code.ToString() : "unknown";

    private void FailPending(Exception exception)
    {
        foreach (var completion in _pending.Values)
            completion.TrySetException(exception);
    }

    public void Dispose()
    {
        _initialized = false;
        DisposeProcess();
        _writeGate.Dispose();
    }

    private void DisposeProcess()
    {
        var process = _process;
        _process = null;
        _stdin = null;
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        process.Dispose();
    }
}
