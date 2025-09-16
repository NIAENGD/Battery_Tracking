namespace BatteryTracker.Shared.Telemetry;

/// <summary>
/// Canonical metric identifiers stored within the telemetry pipeline.
/// </summary>
public enum TelemetryMetric
{
    Unknown = 0,
    PowerMilliwatts,
    VoltageMillivolts,
    CurrentMilliamps,
    RemainingCapacityMilliwattHours,
    FullChargeCapacityMilliwattHours,
    UtilizationPercent,
    TemperatureCelsius,
    EnergyConsumedMilliwattHours,
    FrequencyMHz,
    ThroughputMbps,
}
