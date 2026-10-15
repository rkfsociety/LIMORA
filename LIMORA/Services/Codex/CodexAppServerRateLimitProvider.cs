using System.Text.Json;
using LIMORA.Infrastructure;
using LIMORA.Models;

namespace LIMORA.Services.Codex;

public sealed class CodexAppServerRateLimitProvider(string? executablePath, LocalLogger logger) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _clientGate = new();
    private CodexAppServerClient? _client;
    private bool _disposed;
    private volatile bool _hasCurrentSnapshot;

    public event Action<LimitSnapshot?>? SnapshotChanged;
    public event Action? UpdatedNotification;
    public bool HasCurrentSnapshot => _hasCurrentSnapshot;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (executablePath is null || _disposed) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = GetOrCreateClient();
            try
            {
                await client.StartAsync(cancellationToken).ConfigureAwait(false);
                var result = await client.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
                var parsed = RateLimitParser.Parse(result, DateTimeOffset.UtcNow);
                _hasCurrentSnapshot = parsed is not null;
                if (parsed is null)
                    logger.Write("warning", "rate_limits_unavailable", "The app-server response did not contain displayable rate-limit windows.");
                SnapshotChanged?.Invoke(parsed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Write("warning", "rate_limit_refresh_failed", ex.GetType().Name);
                ResetClient();
                _hasCurrentSnapshot = false;
                SnapshotChanged?.Invoke(null);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void RequestRefreshFromNotification()
    {
        UpdatedNotification?.Invoke();
    }

    public async Task SuspendAsync()
    {
        if (_disposed) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ResetClient();
            _hasCurrentSnapshot = false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private CodexAppServerClient GetOrCreateClient()
    {
        lock (_clientGate)
        {
            if (_client is not null) return _client;
            _client = new CodexAppServerClient(executablePath!, logger);
            _client.RateLimitsUpdated += RequestRefreshFromNotification;
            return _client;
        }
    }

    private void ResetClient()
    {
        lock (_clientGate)
        {
            _client?.Dispose();
            _client = null;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        ResetClient();
    }
}
