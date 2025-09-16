namespace BatteryTracker.Shared.Sessions;

/// <summary>
/// Represents metadata for a tracking session persisted in the storage layer.
/// </summary>
public sealed class SessionMetadata
{
    public Guid SessionId { get; init; } = Guid.NewGuid();

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }
        = null;

    public string? User { get; init; }
        = Environment.UserName;

    public string? Notes { get; set; }
        = null;

    public string SoftwareVersion { get; init; }
        = typeof(SessionMetadata).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    public string OsBuild { get; init; }
        = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
}
