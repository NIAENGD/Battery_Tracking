using BatteryTracker.Shared.Sessions;

namespace BatteryTracker.Cli;

internal sealed record SessionStateSnapshot(Guid SessionId, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, string? Notes)
{
    public bool IsActive => CompletedAt is null;

    public static SessionStateSnapshot FromMetadata(SessionMetadata metadata)
        => new(metadata.SessionId, metadata.StartedAt, metadata.CompletedAt, metadata.Notes);
}
