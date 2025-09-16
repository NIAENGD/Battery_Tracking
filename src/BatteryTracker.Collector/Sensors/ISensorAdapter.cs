using BatteryTracker.Shared.Configuration;
using BatteryTracker.Shared.Sessions;
using BatteryTracker.Shared.Telemetry;

namespace BatteryTracker.Collector.Sessions;

/// <summary>
/// Contract implemented by individual telemetry sensors.
/// </summary>
public interface ISensorAdapter
{
    /// <summary>
    /// Starts the adapter using the provided session metadata and sampling policy.
    /// </summary>
    Task StartAsync(SessionMetadata session, SamplingPolicy policy, CancellationToken cancellationToken);

    /// <summary>
    /// Requests the adapter to stop collecting data and release resources.
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Streams telemetry samples as they are collected.
    /// </summary>
    IAsyncEnumerable<MetricSample> ReadSamplesAsync(CancellationToken cancellationToken);
}
