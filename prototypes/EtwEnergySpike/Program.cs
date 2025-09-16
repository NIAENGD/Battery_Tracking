using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;

Console.WriteLine("ETW Energy Spike Prototype");
Console.WriteLine("Listening for Microsoft-Windows-Kernel-Power Energy Usage events for 30 seconds...");

if (!TraceEventSession.IsElevated())
{
    Console.WriteLine("Administrative privileges are required to run this prototype.");
    return;
}

using var session = new TraceEventSession("BatteryTracker-EnergySpike")
{
    StopOnDispose = true
};

session.EnableProvider("Microsoft-Windows-Kernel-Power");
var start = DateTimeOffset.UtcNow;

session.Source.Dynamic.All += traceEvent =>
{
    if (traceEvent.ProviderName == "Microsoft-Windows-Kernel-Power")
    {
        var timestamp = traceEvent.TimeStamp;
        Console.WriteLine($"[{timestamp:HH:mm:ss.fff}] EventId={traceEvent.ID} Opcode={traceEvent.OpcodeName}");
    }
};

var processingTask = Task.Run(() => session.Source.Process());
await Task.Delay(TimeSpan.FromSeconds(30));
session.Stop();
await processingTask.ConfigureAwait(false);

Console.WriteLine($"Captured events for {(DateTimeOffset.UtcNow - start).TotalSeconds:F1} seconds.");
