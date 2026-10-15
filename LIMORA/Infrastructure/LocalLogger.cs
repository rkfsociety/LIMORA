using System.Text.Json;

namespace LIMORA.Infrastructure;

public sealed class LocalLogger
{
    private readonly object _gate = new();
    private readonly string _logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LIMORA", "logs");

    public void Write(string level, string eventName, string message)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var entry = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                level,
                @event = eventName,
                message
            });
            lock (_gate)
                File.AppendAllText(Path.Combine(_logDirectory, $"limora-{DateTime.Now:yyyy-MM-dd}.jsonl"), entry + Environment.NewLine);
        }
        catch
        {
            // Logging must not interrupt the overlay if the local log directory is unavailable.
        }
    }
}
