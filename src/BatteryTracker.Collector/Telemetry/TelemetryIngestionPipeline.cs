using System.Threading.Channels;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BatteryTracker.Shared.Sessions;
using BatteryTracker.Shared.Telemetry;
using Serilog;

namespace BatteryTracker.Collector.Sessions;

/// <summary>
/// Buffers telemetry samples and persists them into the SQLite backing store.
/// </summary>
public sealed class TelemetryIngestionPipeline
{
    private Channel<MetricSample> _channel;
    private readonly Storage.StorageFacade _storage;
    private readonly ILogger _logger;
    private readonly int _capacity;

    public TelemetryIngestionPipeline(Storage.StorageFacade storage, ILogger logger, int capacity)
    {
        _storage = storage;
        _logger = logger.ForContext<TelemetryIngestionPipeline>();
        _capacity = capacity;
        _channel = Channel.CreateBounded<MetricSample>(_capacity);
    }

    public ValueTask EnqueueAsync(MetricSample sample, CancellationToken cancellationToken)
        => _channel.Writer.WriteAsync(sample, cancellationToken);

    public void RegisterSession(SessionMetadata session)
    {
        _storage.RegisterSession(session);
    }

    public void CompleteSession(SessionMetadata session)
    {
        _storage.CompleteSession(session);
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();

        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var batch = new List<MetricSample>();
            while (_channel.Reader.TryRead(out var sample))
            {
                batch.Add(sample);
                if (batch.Count >= _storage.BatchSize)
                {
                    break;
                }
            }

            if (batch.Count > 0)
            {
                await _storage.InsertSamplesAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.Information("Telemetry ingestion pipeline flushed and reset.");

        _channel = Channel.CreateBounded<MetricSample>(_capacity);
    }
}
