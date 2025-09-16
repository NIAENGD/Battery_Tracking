# Battery Tracking — Windows on ARM Prototype

Battery Tracking is a Windows App SDK (WinUI 3) solution targeting Windows-on-ARM (WoA) devices. The Phase 1 inception milestone delivers the architectural foundation, telemetry prototypes, and developer documentation necessary to begin full collector implementation.

## Solution structure

```
BatteryTracker.sln
├── src/
│   ├── BatteryTracker.App/               # WinUI 3 dashboard shell (net8.0-windows)
│   ├── BatteryTracker.Cli/               # System.CommandLine host for start/stop/status control
│   ├── BatteryTracker.Collector/         # Collector service primitives (sessions, storage, sensors)
│   └── BatteryTracker.Shared/            # Cross-cutting models, configuration, telemetry contracts
├── tests/
│   └── BatteryTracker.Collector.Tests/   # Integration tests with mocked sensor adapters
├── prototypes/
│   └── EtwEnergySpike/                   # TraceEvent spike that validates ETW energy providers
└── docs/
    └── inception/inception-summary.md    # Phase 1 deliverable report
```

## Phase 1 (Inception) highlights

- ✅ Verified availability of core Windows power telemetry on WoA hardware.
- ✅ Implemented a prototype `AggregateBatterySensor` that streams WinRT battery metrics.
- ✅ Established SQLite schema and ingestion pipeline for metrics and session metadata.
- ✅ Added a WinUI 3 dashboard to exercise collector start/stop flows.
- ✅ Captured findings, sampling policy, and open risks in `docs/inception/inception-summary.md`.

## Phase 2 (Core collector) highlights

- ✅ Introduced a `CpuPerformanceSensor` that samples Windows performance counters for utilization and frequency telemetry.
- ✅ Delivered a verb-based CLI (`BatteryTracker.Cli`) that can start, stop, and inspect collector sessions from a terminal.
- ✅ Added an integration test harness that validates session persistence with mocked sensor adapters.

## Getting started (Windows on ARM)

> **Prerequisites**
>
> - Windows 11 on ARM64
> - .NET 8 SDK (arm64)
> - Visual Studio 2022 17.8+ with "Windows App SDK" workload
> - Administrator permissions for ETW energy traces

1. Clone the repository on the WoA device.
2. Open `BatteryTracker.sln` in Visual Studio (arm64) or run `dotnet restore` from an arm64 developer prompt.
3. Set `BatteryTracker.App` as the startup project and press **F5**.
4. Use the dashboard to start and stop telemetry sessions. Data persists to `%LOCALAPPDATA%\BatteryTracker\battery-tracker.db`.
5. Run the ETW spike with elevated privileges:
   ```powershell
   dotnet run --project prototypes/EtwEnergySpike/EtwEnergySpike.csproj
   ```

## Command-line collector usage

The CLI defaults to `%LOCALAPPDATA%\BatteryTracker` for data, logs, and session state. All commands accept `--data-directory` to override the location.

```powershell
# Start a new session, optionally attaching notes and an automatic stop timer.
batterytracker start --notes "Perf sweep" --duration 00:10:00

# Signal the currently running collector to flush and exit.
batterytracker stop

# Show whether the collector is running and summarize the most recent session.
batterytracker status
```

## Next steps

Phase 3 will focus on GPU/display/wireless adapters, richer diagnostics in the dashboard, and extending the reporting pipeline (PDF/visualizations).
