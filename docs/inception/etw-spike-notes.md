# ETW Energy Spike Notes

The `EtwEnergySpike` prototype validates access to energy-related ETW providers on Windows-on-ARM hardware.

## Usage

```powershell
# Requires elevated PowerShell or Windows Terminal
cd <repo-root>
dotnet run --project prototypes/EtwEnergySpike/EtwEnergySpike.csproj
```

The spike listens for `Microsoft-Windows-Kernel-Power` events for 30 seconds and prints the observed opcode IDs and timestamps. This confirms that the collector can stream ETW data without relying on desktop tooling such as Windows Performance Analyzer.

## Observations

- The provider emits telemetry at ~1 Hz when the system is under moderate CPU load, aligning with the 1 second high-priority cadence.
- On AC power, opcode `0x66` (Energy Usage) surfaces aggregated energy counters per SOC component; on battery, opcode `0x65` (Rundown) includes charge estimates.
- Running the spike without administrative privileges results in an access denied error—Phase 2 must surface an elevation prompt in the CLI/bootstrapper.
- No thermal throttling events were captured during the initial run; stress testing is needed to confirm throttling telemetry presence.

These notes complement the detailed inception summary and serve as evidence for the telemetry availability checklist.
