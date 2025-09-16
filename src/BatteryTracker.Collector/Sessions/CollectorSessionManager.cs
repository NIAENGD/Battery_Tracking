using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BatteryTracker.Shared.Configuration;
using BatteryTracker.Shared.Sessions;
using BatteryTracker.Shared.Telemetry;
using Serilog;

namespace BatteryTracker.Collector.Sessions;

/// <summary>
/// Coordinates lifecycle for telemetry tracking sessions. Responsible for orchestrating adapters
/// and forwarding captured samples to the persistence pipeline.
/// </summary>
public sealed class CollectorSessionManager
{
    private readonly SamplingPolicy _samplingPolicy;
    private readonly TelemetryIngestionPipeline _pipeline;
    private readonly IReadOnlyCollection<ISensorAdapter> _adapters;
    private readonly ILogger _logger;
    private readonly object _syncRoot = new();
    private CancellationTokenSource? _sessionCts;
    private SessionMetadata? _activeSession;

    public CollectorSessionManager(
        SamplingPolicy samplingPolicy,
        TelemetryIngestionPipeline pipeline,
        IEnumerable<ISensorAdapter> adapters,
        ILogger logger)
    {
        _samplingPolicy = samplingPolicy;
        _pipeline = pipeline;
        _adapters = adapters.ToList();
        _logger = logger.ForContext<CollectorSessionManager>();
    }

    public SessionMetadata? ActiveSession => _activeSession;

    /// <summary>
    /// Starts a new telemetry tracking session. Any existing session will be stopped before the new
    /// session begins.
    /// </summary>
    public async Task<SessionMetadata> StartAsync(string? notes = null, CancellationToken cancellationToken = default)
    {
        SessionMetadata? priorSession = null;
        lock (_syncRoot)
        {
            if (_sessionCts is not null)
            {
                priorSession = _activeSession;
            }

            _sessionCts?.Cancel();
            _sessionCts?.Dispose();
            _sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeSession = new SessionMetadata
            {
                Notes = notes,
                StartedAt = DateTimeOffset.UtcNow,
            };
            _pipeline.RegisterSession(_activeSession);
        }

        if (priorSession is not null)
        {
            _logger.Warning("A prior session {SessionId} was active and has been stopped before starting a new session.", priorSession.SessionId);
        }

        _logger.Information("Starting telemetry session {SessionId} with {AdapterCount} adapters.", _activeSession!.SessionId, _adapters.Count);

        foreach (var adapter in _adapters)
        {
            await adapter.StartAsync(_activeSession, _samplingPolicy, _sessionCts!.Token).ConfigureAwait(false);
        }

        _ = Task.Run(() => PumpAsync(_sessionCts!.Token), CancellationToken.None);
        return _activeSession;
    }

    /// <summary>
    /// Stops the currently running session and flushes outstanding telemetry.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        SessionMetadata? session;
        CancellationTokenSource? cts;
        lock (_syncRoot)
        {
            session = _activeSession;
            cts = _sessionCts;
            _activeSession = null;
            _sessionCts = null;
        }

        if (cts is null || session is null)
        {
            _logger.Information("Stop requested but no active telemetry session was running.");
            return;
        }

        _logger.Information("Stopping telemetry session {SessionId}.", session.SessionId);
        cts.Cancel();

        foreach (var adapter in _adapters)
        {
            await adapter.StopAsync().ConfigureAwait(false);
        }

        session.CompletedAt = DateTimeOffset.UtcNow;
        _pipeline.CompleteSession(session);
        await _pipeline.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var sample in _adapters.Select(adapter => adapter.ReadSamplesAsync(cancellationToken))
                .Merge(cancellationToken)
                .ConfigureAwait(false))
            {
                await _pipeline.EnqueueAsync(sample, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Telemetry pump cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected failure while pumping telemetry samples.");
        }
    }
}
