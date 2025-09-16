using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BatteryTracker.Shared.Configuration;
using BatteryTracker.Shared.Sessions;
using Serilog;

namespace BatteryTracker.Collector.Sessions;

/// <summary>
/// Provides a simplified composition root used by the CLI and UI to host the collector service during phase 1 experimentation.
/// </summary>
public sealed class CollectorHost : IAsyncDisposable
{
    private readonly Storage.StorageFacade _storage;
    private readonly TelemetryIngestionPipeline _pipeline;
    private readonly CollectorSessionManager _sessionManager;
    private readonly ILogger _logger;
    private readonly IDisposable? _loggerLifetime;

    private CollectorHost(Storage.StorageFacade storage, TelemetryIngestionPipeline pipeline, CollectorSessionManager sessionManager, ILogger logger, IDisposable? loggerLifetime)
    {
        _storage = storage;
        _pipeline = pipeline;
        _sessionManager = sessionManager;
        _logger = logger.ForContext<CollectorHost>();
        _loggerLifetime = loggerLifetime;
    }

    public static CollectorHost CreateDefault(string dataDirectory, ILogger? logger = null)
    {
        var ownsLogger = logger is null;
        var log = logger ?? new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(dataDirectory, "logs", "collector.log"), rollingInterval: RollingInterval.Day)
            .CreateLogger();

        Directory.CreateDirectory(dataDirectory);
        Directory.CreateDirectory(Path.Combine(dataDirectory, "logs"));
        var databasePath = Path.Combine(dataDirectory, "battery-tracker.db");
        var samplingPolicy = new SamplingPolicy();
        var storage = new Storage.StorageFacade(databasePath, samplingPolicy.PersistenceBatchSize, log);
        var pipeline = new TelemetryIngestionPipeline(storage, log, samplingPolicy.BufferCapacity);
        var adapters = new List<ISensorAdapter>
        {
            new AggregateBatterySensor(log),
            new CpuPerformanceSensor(log),
            new GpuEngineSensor(log),
            new DisplayBrightnessSensor(log),
            new WirelessThroughputSensor(log),
        };
        var sessionManager = new CollectorSessionManager(samplingPolicy, pipeline, adapters, log);
        return new CollectorHost(storage, pipeline, sessionManager, log, ownsLogger ? log : logger as IDisposable);
    }

    public SessionMetadata? ActiveSession => _sessionManager.ActiveSession;

    public Task<SessionMetadata> StartSessionAsync(string? notes = null, CancellationToken cancellationToken = default)
        => _sessionManager.StartAsync(notes, cancellationToken);

    public Task StopSessionAsync(CancellationToken cancellationToken = default)
        => _sessionManager.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _sessionManager.StopAsync().ConfigureAwait(false);
        await _storage.DisposeAsync().ConfigureAwait(false);
        _loggerLifetime?.Dispose();
    }
}
