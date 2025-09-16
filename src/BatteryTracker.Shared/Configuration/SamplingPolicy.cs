namespace BatteryTracker.Shared.Configuration;

/// <summary>
/// Describes the sampling cadence and retention guidelines for the collector service.
/// </summary>
public sealed class SamplingPolicy
{
    /// <summary>
    /// Gets or sets the default sampling interval for high priority sensors.
    /// </summary>
    public TimeSpan HighPriorityInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the sampling interval for medium priority sensors.
    /// </summary>
    public TimeSpan MediumPriorityInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the sampling interval for low priority or derived sensors.
    /// </summary>
    public TimeSpan LowPriorityInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Gets or sets the retention window for telemetry persisted in SQLite.
    /// </summary>
    public TimeSpan RetentionWindow { get; init; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Gets or sets the size of the in-memory ring buffer prior to persistence flush.
    /// </summary>
    public int BufferCapacity { get; init; } = 512;

    /// <summary>
    /// Gets or sets the maximum number of samples to batch per SQLite transaction.
    /// </summary>
    public int PersistenceBatchSize { get; init; } = 128;
}
