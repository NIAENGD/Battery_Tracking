namespace BatteryTracker.Cli;

internal sealed record PowerBreakdown(
    double SocTotalMilliwatts,
    double? CpuMilliwatts,
    double? GpuMilliwatts,
    double? DisplayMilliwatts,
    double? MotherboardMilliwatts)
{
    public static PowerBreakdown Empty { get; } = new(double.NaN, null, null, null, null);

    public bool HasAnyData => !double.IsNaN(SocTotalMilliwatts);
}
