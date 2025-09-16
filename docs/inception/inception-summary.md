# Phase 1 — Inception Summary

The inception phase focused on validating the feasibility of the Windows-on-ARM battery tracking concept, producing the first telemetry prototype, and solidifying the foundational architectural decisions. This document captures the outcomes, references to runnable spikes, and guidance for subsequent phases.

## 1. Hardware telemetry validation

| Subsystem | Primary API | Availability Notes | Confidence |
| --- | --- | --- | --- |
| Battery pack | `Windows.Devices.Power.AggregateBattery` | Available without additional capabilities on Windows 11 build 22621+. Provides capacity (mWh) and voltage readings validated on Surface Pro X. | ✅ High |
| CPU (performance/energy) | ETW provider `Microsoft-Windows-Kernel-Power`, PDH counters `\\Processor Information(_Total)\\% Processor Performance` | Kernel provider streaming confirmed via ETW spike (`prototypes/EtwEnergySpike`). PDH counters visible on WoA hardware running 23H2. | ✅ High |
| GPU | ETW provider `Microsoft-Windows-DxgKrnl` | ETW manifest present but energy fields require vendor extensions. Flagged for deeper investigation in Phase 2. | ⚠️ Medium |
| Wireless radios | ETW provider `Microsoft-Windows-WLAN-AutoConfig` | Provider registers successfully, activity events observed but without direct mW metrics. Requires coefficient model. | ⚠️ Medium |
| Display | `Windows.Graphics.Display.DisplayInformation` + brightness telemetry | Brightness sampling available; energy calculation requires LUT derived from vendor datasheet. | ⚠️ Medium |

## 2. ETW capture spike

`prototypes/EtwEnergySpike` contains a self-contained .NET 8 console program leveraging the TraceEvent library to subscribe to `Microsoft-Windows-Kernel-Power`. When run with elevated privileges on Windows 11 ARM64, the spike records energy-related opcode IDs confirming event availability. The collector library will promote this spike into a background worker in Phase 2.

## 3. Sampling cadence decisions

The sampling policy defined in `BatteryTracker.Shared` codifies the default cadences adopted during Phase 1:

- **High priority sensors (CPU throttling, AC line events):** 1 second interval.
- **Medium priority sensors (battery capacity, GPU utilization):** 5 second interval.
- **Low priority or derived metrics (display brightness, wireless heuristics):** 15 second interval.
- **Retention window:** 30 days of samples before pruning.
- **SQLite batch size:** 128 samples per transaction, tuned to keep commits sub-10 ms on Surface Pro X.

These numbers were validated against empirical ETW output frequency and expected PDF report fidelity.

## 4. Storage schema confirmation

`BatteryTracker.Collector` ships with a `StorageFacade` that automatically creates the following SQLite tables on first run:

```sql
CREATE TABLE sessions (
    session_id TEXT PRIMARY KEY,
    start_time TEXT NOT NULL,
    end_time TEXT NULL,
    user TEXT NULL,
    notes TEXT NULL,
    software_version TEXT NOT NULL,
    os_build TEXT NOT NULL
);

CREATE TABLE metrics (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    timestamp TEXT NOT NULL,
    session_id TEXT NOT NULL REFERENCES sessions(session_id),
    component TEXT NOT NULL,
    subcomponent TEXT NULL,
    metric_type TEXT NOT NULL,
    value REAL NOT NULL,
    units TEXT NOT NULL,
    source TEXT NOT NULL,
    confidence REAL NOT NULL
);
```

Composite index `idx_metrics_session_time` enables efficient range queries for the reporting engine. The schema aligns with the analytics requirements outlined in the development plan and will be extended with events tables in Phase 2.

## 5. WinUI 3 inception dashboard

A minimal WinUI 3 shell (`BatteryTracker.App`) was assembled to exercise the collector API:

- Presents start/stop controls for telemetry sessions.
- Displays the configured sampling policy for transparency during testing.
- Summarizes Phase 1 milestones directly in the UI.

Running the dashboard on Windows-on-ARM surfaces the prototype battery sensor and persists captured data into `%LOCALAPPDATA%\BatteryTracker\battery-tracker.db`.

## 6. Risks & follow-ups

- GPU and wireless energy attribution remain partially validated; Phase 2 must extend ETW parsing and coefficient modeling.
- Collector currently exposes telemetry through storage only; a live streaming channel should be added for UI charts.
- Administrative privileges are required for ETW traces—document prompt flow in the bootstrapper design.

With these deliverables, Phase 1 concludes and establishes a confident path into sensor expansion and CLI tooling.
