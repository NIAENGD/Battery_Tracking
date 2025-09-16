using System.Text.Json;

namespace BatteryTracker.Cli;

internal sealed class SessionStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _filePath;

    public SessionStateStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _filePath = Path.Combine(dataDirectory, "session-state.json");
    }

    public async Task WriteAsync(SessionStateSnapshot snapshot, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await File.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SessionStateSnapshot?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<SessionStateSnapshot>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
    }
}
