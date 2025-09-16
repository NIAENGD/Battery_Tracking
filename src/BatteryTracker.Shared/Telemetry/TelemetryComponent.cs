namespace BatteryTracker.Shared.Telemetry;

/// <summary>
/// Defines canonical component names for telemetry entries to ensure consistent storage schema usage.
/// </summary>
public static class TelemetryComponent
{
    public const string System = "System";
    public const string Battery = "Battery";
    public const string Cpu = "CPU";
    public const string Gpu = "GPU";
    public const string Display = "Display";
    public const string Storage = "Storage";
    public const string Wireless = "Wireless";
    public const string Thermal = "Thermal";
}
