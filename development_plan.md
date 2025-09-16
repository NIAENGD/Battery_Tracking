# Windows on ARM Battery Tracking Platform — Development Plan

## 1. Project goals and success criteria
- Provide a Windows-on-ARM (WoA) native desktop utility that can capture, persist, and visualize battery and power-related telemetry for the system and its major subsystems (CPU, GPU, display, storage, wireless radios, etc.).
- Allow users to launch the application through a `.bat` bootstrapper that exposes simple commands (`start`, `stop`, `status`, `export`).
- Maintain continuous logging when tracking is enabled, minimizing overhead (<5% CPU usage, <100 MB RAM) and automatically buffering data during transient disconnects.
- Export human-readable PDF reports that contain timelines, summary statistics, and contextual metadata for a selected time window.
- Ship as an arm64-native, self-contained package installable without external dependencies beyond the Windows 11-on-ARM baseline.

## 2. Platform & framework research
| Option | Pros | Cons | WoA arm64 support verdict |
| --- | --- | --- | --- |
| **.NET 8 + Windows App SDK (WinUI 3) with C#** | First-class WinRT/Win32 interop; access to Windows power APIs; modern UI; strong arm64 support; rich ecosystem for charting (ScottPlot, LiveCharts2) and PDF (QuestPDF). | Larger runtime footprint; WinUI 3 UI optional if CLI only. | ✅ Fully supported starting with Windows App SDK 1.4+.
| **.NET 8 console + C#/C++ P/Invoke** | Lightweight headless collector; can integrate with Windows Performance Counters (PDH), Power Profile API, ETW. | Requires manual CLI/UX; more boilerplate for UI. | ✅ .NET 8 arm64 self-contained builds.
| **Python 3.11 (native ARM build) + psutil + matplotlib + reportlab** | Rapid development; rich data science tooling. | Many third-party wheels still x86-only (e.g., GPU telemetry libs); packaging for WoA is immature; higher runtime cost. | ⚠️ Limited — requires manual ARM wheel builds, increases risk.
| **Electron/Node.js** | Cross-platform UI; packaging via npm. | Heavyweight; lacks direct access to low-level Windows APIs without native modules; arm64 Node modules limited. | ⚠️ Partial support, high maintenance.
| **C++/WinRT + DirectX tooling** | Maximum performance; direct ETW integration; control over native APIs. | Higher development cost; steeper learning curve for PDF/rendering; less rapid iteration. | ✅ Supported; but requires more effort.

**Recommendation:** Use a two-tier architecture built primarily in **.NET 8 (C#)**:
1. A headless **Data Collector service** (console app/library) that handles ETW sessions, Windows.Power telemetry, and storage.
2. A **Command Host CLI** invoked by `.bat` to control tracking, inspect status, and trigger report generation.
3. Optional UI shell (future) can reuse the same service layer if needed.

## 3. High-level architecture
```
+----------------------+        +----------------------+        +-----------------------+
| .bat Bootstrapper    | -----> | Command Host (CLI)   | -----> | Collector Service API |
+----------------------+        +----------------------+        +-----------------------+
                                                                |  - ETW session mgr    |
                                                                |  - Sensor adapters    |
                                                                |  - Data buffer/cache  |
                                                                +-----------+-----------+
                                                                            |
                                                                            v
                                                                +-----------------------+
                                                                | Storage (SQLite/Parquet) |
                                                                +-----------+-----------+
                                                                            |
                                                                            v
                                                                +-----------------------+
                                                                | Reporting Engine      |
                                                                |  - Analysis pipeline  |
                                                                |  - Visualization (ScottPlot) |
                                                                |  - PDF composer (QuestPDF)   |
                                                                +-----------------------+
```

### Key components
1. **Bootstrap `.bat`** — Sets environment variables, checks prerequisites, and launches the CLI (self-contained `BatteryTracker.exe`).
2. **Command Host CLI** — Implements verb-based interface via `System.CommandLine` (`start`, `stop`, `status`, `export`, `config`). Talks to collector via named pipes or REST over `http://localhost` (Kestrel minimal API).
3. **Collector Service** — Long-running process hosting:
   - **Session Manager:** Controls ETW traces (`Microsoft-Windows-Kernel-Power`, `Microsoft-Windows-DxgKrnl`, `Microsoft-Windows-WLAN-AutoConfig`), registers PDH counters, polls WinRT battery APIs.
   - **Sensor Adapters:** Pluggable modules per subsystem (CPU, GPU, Display, Storage, Network, Battery pack).
   - **Metrics Buffer:** Ring buffer for high-frequency metrics (1–5 s granularity), flushes to persistent storage.
4. **Storage Layer:** Lightweight embedded DB (SQLite via `Microsoft.Data.Sqlite`) storing normalized tables (metrics, metadata, sessions). Consider Parquet export for data science.
5. **Reporting Engine:** Queries data, computes KPIs (average wattage, mWh consumed, per-component contribution), renders charts (line plots, stacked area, histograms) using ScottPlot, exports PDF with QuestPDF template containing summary + visuals.

## 4. Windows ARM-specific telemetry strategy
| Component | Primary data source | Backup/notes |
| --- | --- | --- |
| Battery (pack-level) | WinRT `Windows.Devices.Power.Battery.AggregateBattery.Report`. Accessible via CsWinRT projections in .NET. | Fallback: `GetSystemPowerStatus` Win32 API.
| CPU usage & frequency | PDH counters (`\Processor Information(_Total)\% Processor Performance`, `\Processor Energy`) + `Windows.System.Power.PowerManager` for power throttling. | If PDH energy counters unavailable on Snapdragon, estimate using `EnergyEstimationUtilization` ETW events.
| CPU energy (estimated) | ETW provider `Microsoft-Windows-Kernel-Power` (`ThermalZoneRundown`, `PowerDeviceRundown`). Qualcomm exposes SOC energy via E3. Use `EnergyUsage` API through WinRT (requires `Windows 11 21H2+`). | Validate hardware support with `powercfg /energy` and `powercfg /batteryreport`.
| GPU utilization & power | PDH counters `\GPU Engine(*)\Utilization Percentage` and `\GPU Adapter(*)\Power`. On WoA (Adreno), rely on `Microsoft-Windows-DxgKrnl` ETW events. | Provide vendor plug-in for additional telemetry if Qualcomm provides SDK (e.g., QDSS).
| Display panel | WMI class `WmiMonitorBrightness`, `Display Configuration API` for brightness levels. Combine with panel power model (brightness-to-power LUT). | Use EDID + heuristics when direct telemetry missing.
| Storage | ETW `Microsoft-Windows-StorPort`, NVMe driver events; combine with I/O counters to estimate power (mW per MB/s). | Lower priority, optional.
| Wireless (Wi-Fi/BT) | ETW `Microsoft-Windows-WLAN-AutoConfig`, `Microsoft-Windows-Bluetooth-BthLEEnum`. Track activity periods, approximate consumption. | Provide configurable coefficients.

### External tooling/packages to leverage
- **Windows Performance Toolkit (WPT)** — Use `wpr.exe` profiles as reference for ETW events capturing energy metrics.
- **PowerShell `Get-Counter`** — For validating PDH counter availability on target hardware.
- **Qualcomm Systems Power Monitor** — If accessible, evaluate SDK to integrate vendor-specific sensors.
- **openhardwaremonitor/hwinfo** — Investigate APIs for additional telemetry; ensure licensing compatibility.

## 5. Data model & storage structure
- `sessions` table: `session_id`, `start_time`, `end_time`, `user`, `notes`, `software_version`, `os_build`.
- `metrics` table: `timestamp`, `session_id`, `component`, `subcomponent`, `metric_type`, `value`, `units`, `source`, `confidence`.
- `events` table: `timestamp`, `event_type` (e.g., `AC_CONNECTED`), `payload` (JSON).
- Indexing: composite index on `(session_id, timestamp)` for fast range queries.
- Rolling log retention policy configurable (e.g., 30 days) with automatic pruning.

## 6. Control flow & lifecycle
1. User runs `StartTracking.bat`.
2. Batch script ensures `BatteryTracker.exe` is present, launches CLI with forwarded arguments.
3. `BatteryTracker.exe start`:
   - CLI ensures collector service is running (launches if not).
   - Issues `/start` command via IPC, collector begins sampling.
   - Status updates logged to console and `logs/tracker.log` (Serilog).
4. `BatteryTracker.exe stop` halts sampling, flushes buffers, marks session end.
5. `BatteryTracker.exe export --session <id> --output <file.pdf>` generates PDF by querying stored data.
6. Optional `BatteryTracker.exe status` prints active session info, sample rates, file locations.

## 7. PDF reporting & visualization
- Use **ScottPlot** to render PNG charts (power over time, stacked component contribution, histograms, battery percentage).
- Compose PDF with **QuestPDF** (supports .NET arm64). Layout sections:
  1. Cover page with metadata.
  2. Session summary (duration, total mWh consumed, average wattage, top energy consumers).
  3. Timeline plots (overall power, CPU vs GPU vs display).
  4. Event markers (power source changes, throttling events).
  5. Appendix: raw statistics table.
- Provide theming and option to attach raw CSV.

## 8. Configuration & extensibility
- `appsettings.json` (or YAML) for sample intervals, retention, telemetry toggles, coefficient overrides.
- Plugin interface via `ISensorAdapter` (StartAsync/StopAsync/ReadAsync) enabling vendor-specific modules.
- Logging via Serilog with rolling file and console sinks.
- Telemetry endpoint for optional Prometheus exporter (future).

## 9. Packaging & deployment
- Build `BatteryTracker.exe` as self-contained arm64 release (`dotnet publish -c Release -r win-arm64 --self-contained`).
- Include `StartTracking.bat` and configuration files in distributable ZIP.
- Provide installer script (optional) using PowerShell to place files under `%ProgramFiles%\BatteryTracker` and register firewall exception.
- Code-sign executable and PDF output to satisfy SmartScreen.

## 10. Implementation roadmap
1. **Inception (Week 1)**
   - Validate telemetry availability on target Snapdragon reference device.
   - Spike prototypes for ETW capture (`TraceEvent`/`Microsoft.Diagnostics.Tracing.TraceEvent`).
   - Decide on sampling intervals and confirm storage schema.
2. **Core collector (Weeks 2-4)**
   - Implement session manager, CPU & battery adapters.
   - Persist metrics to SQLite; implement CLI `start/stop/status`.
   - Add integration tests with mocked sensors.
3. **Extended telemetry (Weeks 5-6)**
   - Add GPU, display, wireless adapters; refine estimation models.
   - Implement configuration management and logging.
4. **Reporting & PDF (Weeks 7-8)**
   - Build analytics layer (aggregations, KPIs).
   - Create visualization templates and QuestPDF export.
   - Provide CLI `export` command.
5. **Stabilization (Weeks 9-10)**
   - Performance tuning, reduce overhead, improve error handling.
   - Add unit tests, end-to-end validation scripts, documentation.
   - Package release, write user guide.

## 11. Testing strategy
- **Unit tests:** Sensor adapters with dependency injection and simulated data.
- **Integration tests:** Run collector on WoA hardware using GitHub Actions self-hosted runner or Azure DevOps pipeline with Windows ARM agent.
- **Performance tests:** Measure CPU/memory overhead, sampling jitter.
- **Battery drain validation:** Compare aggregated mWh with `powercfg /batteryreport` results for accuracy.
- **PDF regression tests:** Golden-master comparison of exported PDFs (render to images and diff).
- **Installer smoke tests:** Validate `.bat` bootstrapper on clean WoA VM.

## 12. Tooling & automation
- **Build system:** GitHub Actions (windows-latest-arm64) for CI, running `dotnet build/test/publish`.
- **Static analysis:** Roslyn analyzers, StyleCop.
- **Code coverage:** Coverlet.
- **Observability:** Optional Application Insights for crash telemetry.
- **Documentation:** DocFX site or Markdown docs packaged alongside release.

## 13. Risks & mitigations
| Risk | Impact | Mitigation |
| --- | --- | --- |
| Limited access to power counters on Snapdragon hardware | Incomplete energy attribution | Work closely with Qualcomm documentation; implement estimation via utilization × calibrated coefficients; allow user-provided calibration.
| Elevated permissions required for ETW sessions | Deployment friction | Run collector with admin prompt when needed; use least-privilege ETW sessions; document requirements in `.bat` script.
| ARM64 third-party dependency gaps | Blocking features (PDF/chart) | Choose libraries with verified arm64 support; maintain minimal native dependencies.
| Data volume growth | Storage bloat | Implement retention policy and compression (SQLite `VACUUM`, optional Parquet export).
| User confusion with CLI | Adoption risk | Provide descriptive help, sample `.bat` commands, optional simple GUI in future milestone.

## 14. Documentation deliverables
- `README.md` — quickstart, prerequisites, CLI usage.
- `docs/architecture.md` — diagrams, data flow.
- `docs/telemetry-sources.md` — detailed sensor mappings, calibrations.
- `docs/report-samples/` — example PDFs and raw data exports.
- `scripts/StartTracking.bat` — entry point script with inline help.

---
This plan balances native Windows-on-ARM compatibility, reliable access to system telemetry, and maintainable reporting workflows while leaving room for vendor-specific enhancements as Qualcomm expands tooling support.
