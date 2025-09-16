using System.Threading.Channels;
using BatteryTracker.Collector.Sessions;
using BatteryTracker.Shared.Configuration;
using BatteryTracker.Shared.Sessions;
using BatteryTracker.Shared.Telemetry;
using Microsoft.Data.Sqlite;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace BatteryTracker.Collector.Tests;

public sealed class CollectorSessionManagerTests
{
    [Fact]
    public async Task StartAndStopSession_PersistsSamplesToStorage()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var databasePath = Path.Combine(tempDirectory, "collector-tests.db");

        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new NullSink())
            .CreateLogger();

        var storage = new Storage.StorageFacade(databasePath, batchSize: 16, logger);
        var sessionId = Guid.Empty;
        var expectedSamples = new[]
        {
            new MetricSample(DateTimeOffset.UtcNow, TelemetryComponent.System, null, TelemetryMetric.PowerMilliwatts, 1.5, "mW", "test"),
            new MetricSample(DateTimeOffset.UtcNow, TelemetryComponent.System, null, TelemetryMetric.PowerMilliwatts, 2.5, "mW", "test"),
            new MetricSample(DateTimeOffset.UtcNow, TelemetryComponent.System, null, TelemetryMetric.PowerMilliwatts, 3.5, "mW", "test"),
        };

        try
        {
            var pipeline = new TelemetryIngestionPipeline(storage, logger, capacity: 64);
            var samplingPolicy = new SamplingPolicy
            {
                HighPriorityInterval = TimeSpan.FromMilliseconds(10),
                MediumPriorityInterval = TimeSpan.FromMilliseconds(20),
                LowPriorityInterval = TimeSpan.FromMilliseconds(30),
            };

            var sensor = new FakeSensorAdapter(expectedSamples);
            var sessionManager = new CollectorSessionManager(samplingPolicy, pipeline, new[] { sensor }, logger);

            var session = await sessionManager.StartAsync().ConfigureAwait(false);
            sessionId = session.SessionId;

            await Task.Delay(200).ConfigureAwait(false);

            await sessionManager.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await storage.DisposeAsync().ConfigureAwait(false);
        }

        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        using (var metricsCommand = connection.CreateCommand())
        {
            metricsCommand.CommandText = "SELECT COUNT(*) FROM metrics WHERE session_id = $sessionId;";
            metricsCommand.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            var count = Convert.ToInt32(metricsCommand.ExecuteScalar());
            Assert.Equal(expectedSamples.Length, count);
        }

        using (var sessionCommand = connection.CreateCommand())
        {
            sessionCommand.CommandText = "SELECT end_time FROM sessions WHERE session_id = $sessionId;";
            sessionCommand.Parameters.AddWithValue("$sessionId", sessionId.ToString());
            var endTime = sessionCommand.ExecuteScalar() as string;
            Assert.False(string.IsNullOrWhiteSpace(endTime));
        }

        Directory.Delete(tempDirectory, recursive: true);
    }

    private sealed class NullSink : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
        }
    }

    private sealed class FakeSensorAdapter : ISensorAdapter
    {
        private readonly IReadOnlyList<MetricSample> _samples;
        private Channel<MetricSample> _channel = Channel.CreateUnbounded<MetricSample>();
        private CancellationTokenSource? _cts;

        public FakeSensorAdapter(IReadOnlyList<MetricSample> samples)
        {
            _samples = samples;
        }

        public Task StartAsync(SessionMetadata session, SamplingPolicy policy, CancellationToken cancellationToken)
        {
            _channel = Channel.CreateUnbounded<MetricSample>();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    foreach (var sample in _samples)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(5), token).ConfigureAwait(false);
                        var emitted = sample with { Timestamp = DateTimeOffset.UtcNow };
                        await _channel.Writer.WriteAsync(emitted, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                }
                finally
                {
                    _channel.Writer.TryComplete();
                }
            }, CancellationToken.None);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<MetricSample> ReadSamplesAsync(CancellationToken cancellationToken)
            => _channel.Reader.ReadAllAsync(cancellationToken);
    }
}
