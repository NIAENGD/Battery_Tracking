namespace BatteryTracker.Shared.Telemetry;

/// <summary>
/// Represents a single telemetry sample captured from a sensor adapter.
/// </summary>
/// <param name="Timestamp">UTC timestamp for the sample.</param>
/// <param name="Component">High-level component name (CPU, GPU, Battery).</param>
/// <param name="Subcomponent">Optional fine-grained identifier (core, adapter, rail).</param>
/// <param name="Metric">Measurement type such as PowerMilliwatts, UtilizationPercent.</param>
/// <param name="Value">Recorded numeric value.</param>
/// <param name="Units">Unit string describing the measurement.</param>
/// <param name="Source">Underlying telemetry provider or API used.</param>
/// <param name="Confidence">Confidence score from 0.0 to 1.0 describing reliability of the value.</param>
public sealed record MetricSample(
    DateTimeOffset Timestamp,
    string Component,
    string? Subcomponent,
    TelemetryMetric Metric,
    double Value,
    string Units,
    string Source,
    double Confidence = 1.0
);
