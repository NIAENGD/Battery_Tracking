using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BatteryTracker.Shared.Sessions;
using BatteryTracker.Shared.Telemetry;
using Microsoft.Data.Sqlite;
using Serilog;

namespace BatteryTracker.Collector.Storage;

/// <summary>
/// Provides a simplified abstraction around SQLite storage interactions for telemetry samples and session metadata.
/// </summary>
public sealed class StorageFacade : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ILogger _logger;

    public StorageFacade(string databasePath, int batchSize, ILogger logger)
    {
        BatchSize = batchSize;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        _logger = logger.ForContext<StorageFacade>();
        InitializeSchema();
    }

    public int BatchSize { get; }

    public async Task InsertSamplesAsync(IReadOnlyCollection<MetricSample> samples, CancellationToken cancellationToken)
    {
        if (samples.Count == 0)
        {
            return;
        }

        await using var transaction = await _connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = _connection.CreateCommand();
        command.CommandText = @"
INSERT INTO metrics (
    timestamp,
    session_id,
    component,
    subcomponent,
    metric_type,
    value,
    units,
    source,
    confidence)
VALUES ($timestamp, $sessionId, $component, $subcomponent, $metricType, $value, $units, $source, $confidence);
";
        var timestampParam = command.CreateParameter();
        timestampParam.ParameterName = "$timestamp";
        command.Parameters.Add(timestampParam);

        var sessionIdParam = command.CreateParameter();
        sessionIdParam.ParameterName = "$sessionId";
        command.Parameters.Add(sessionIdParam);

        var componentParam = command.CreateParameter();
        componentParam.ParameterName = "$component";
        command.Parameters.Add(componentParam);

        var subcomponentParam = command.CreateParameter();
        subcomponentParam.ParameterName = "$subcomponent";
        command.Parameters.Add(subcomponentParam);

        var metricTypeParam = command.CreateParameter();
        metricTypeParam.ParameterName = "$metricType";
        command.Parameters.Add(metricTypeParam);

        var valueParam = command.CreateParameter();
        valueParam.ParameterName = "$value";
        command.Parameters.Add(valueParam);

        var unitsParam = command.CreateParameter();
        unitsParam.ParameterName = "$units";
        command.Parameters.Add(unitsParam);

        var sourceParam = command.CreateParameter();
        sourceParam.ParameterName = "$source";
        command.Parameters.Add(sourceParam);

        var confidenceParam = command.CreateParameter();
        confidenceParam.ParameterName = "$confidence";
        command.Parameters.Add(confidenceParam);

        foreach (var sample in samples)
        {
            timestampParam.Value = sample.Timestamp.UtcDateTime;
            sessionIdParam.Value = _activeSessionId?.ToString() ?? string.Empty;
            componentParam.Value = sample.Component;
            subcomponentParam.Value = sample.Subcomponent ?? (object)DBNull.Value;
            metricTypeParam.Value = sample.Metric.ToString();
            valueParam.Value = sample.Value;
            unitsParam.Value = sample.Units;
            sourceParam.Value = sample.Source;
            confidenceParam.Value = sample.Confidence;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _logger.Debug("Persisted {Count} telemetry samples to SQLite.", samples.Count);
    }

    private Guid? _activeSessionId;

    public void RegisterSession(SessionMetadata session)
    {
        _activeSessionId = session.SessionId;
        using var command = _connection.CreateCommand();
        command.CommandText = @"
INSERT INTO sessions (session_id, start_time, user, notes, software_version, os_build)
VALUES ($sessionId, $startTime, $user, $notes, $softwareVersion, $osBuild)
ON CONFLICT(session_id) DO NOTHING;
";
        command.Parameters.AddWithValue("$sessionId", session.SessionId.ToString());
        command.Parameters.AddWithValue("$startTime", session.StartedAt.UtcDateTime);
        command.Parameters.AddWithValue("$user", session.User ?? string.Empty);
        command.Parameters.AddWithValue("$notes", session.Notes ?? string.Empty);
        command.Parameters.AddWithValue("$softwareVersion", session.SoftwareVersion);
        command.Parameters.AddWithValue("$osBuild", session.OsBuild);
        command.ExecuteNonQuery();
        _logger.Information("Registered telemetry session {SessionId} starting at {StartTime}.", session.SessionId, session.StartedAt);
    }

    public void CompleteSession(SessionMetadata session)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
UPDATE sessions
SET end_time = $endTime
WHERE session_id = $sessionId;
";
        command.Parameters.AddWithValue("$endTime", session.CompletedAt?.UtcDateTime);
        command.Parameters.AddWithValue("$sessionId", session.SessionId.ToString());
        command.ExecuteNonQuery();
        _logger.Information("Completed telemetry session {SessionId} at {EndTime}.", session.SessionId, session.CompletedAt);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private void InitializeSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
CREATE TABLE IF NOT EXISTS sessions (
    session_id TEXT PRIMARY KEY,
    start_time TEXT NOT NULL,
    end_time TEXT NULL,
    user TEXT NULL,
    notes TEXT NULL,
    software_version TEXT NOT NULL,
    os_build TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS metrics (
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

CREATE INDEX IF NOT EXISTS idx_metrics_session_time
    ON metrics(session_id, timestamp);
";
        command.ExecuteNonQuery();
    }
}
