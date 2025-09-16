using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Channels;
using BatteryTracker.Shared.Configuration;
using BatteryTracker.Shared.Sessions;
using BatteryTracker.Shared.Telemetry;
using Serilog;

namespace BatteryTracker.Collector.Sessions;

/// <summary>
/// Publishes CPU utilization and frequency metrics using Windows performance counters.
/// </summary>
public sealed class CpuPerformanceSensor : ISensorAdapter
{
    private readonly ILogger _logger;
    private readonly HashSet<string> _loggedCounterFailures = new(StringComparer.OrdinalIgnoreCase);
    private Channel<MetricSample> _channel = Channel.CreateUnbounded<MetricSample>();
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;
    private PerformanceCounter? _utilizationCounter;
    private PerformanceCounter? _performanceCounter;
    private PerformanceCounter? _frequencyCounter;

    public CpuPerformanceSensor(ILogger logger)
    {
        _logger = logger.ForContext<CpuPerformanceSensor>();
    }

    public Task StartAsync(SessionMetadata session, SamplingPolicy policy, CancellationToken cancellationToken)
    {
        _channel = Channel.CreateUnbounded<MetricSample>();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new PeriodicTimer(policy.HighPriorityInterval);

        if (!TryInitializeCounters())
        {
            _logger.Warning("CPU performance counters are unavailable. No CPU samples will be produced during session {SessionId}.", session.SessionId);
            _channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        _ = Task.Run(() => PumpAsync(session.SessionId, _cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_timer is not null)
        {
            await _timer.DisposeAsync().ConfigureAwait(false);
            _timer = null;
        }

        _cts?.Dispose();
        _cts = null;
        DisposeCounters();
        _channel.Writer.TryComplete();
    }

    public IAsyncEnumerable<MetricSample> ReadSamplesAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    private async Task PumpAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                var samples = new List<MetricSample>(capacity: 3);

                var utilization = ReadCounter(_utilizationCounter);
                if (!double.IsNaN(utilization))
                {
                    samples.Add(new MetricSample(now, TelemetryComponent.Cpu, null, TelemetryMetric.UtilizationPercent, utilization, "%", "PDH"));
                }

                var performance = ReadCounter(_performanceCounter);
                if (!double.IsNaN(performance))
                {
                    samples.Add(new MetricSample(now, TelemetryComponent.Cpu, "Performance", TelemetryMetric.UtilizationPercent, performance, "%", "PDH"));
                }

                var frequency = ReadCounter(_frequencyCounter);
                if (!double.IsNaN(frequency))
                {
                    samples.Add(new MetricSample(now, TelemetryComponent.Cpu, null, TelemetryMetric.FrequencyMHz, frequency, "MHz", "PDH"));
                }

                foreach (var sample in samples)
                {
                    await _channel.Writer.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
                }

                _logger.Verbose("Published {Count} CPU samples for session {SessionId} at {Timestamp}.", samples.Count, sessionId, now);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("CPU performance sensor pump cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error while publishing CPU performance samples.");
        }
        finally
        {
            DisposeCounters();
            _channel.Writer.TryComplete();
        }
    }

    private bool TryInitializeCounters()
    {
        try
        {
            _utilizationCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", readOnly: true);
            _performanceCounter = new PerformanceCounter("Processor Information", "% Processor Performance", "_Total", readOnly: true);
            _frequencyCounter = new PerformanceCounter("Processor Information", "Processor Frequency", "_Total", readOnly: true);

            _ = _utilizationCounter.NextValue();
            _ = _performanceCounter.NextValue();
            _ = _frequencyCounter.NextValue();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.Warning(ex, "Failed to initialize CPU performance counters.");
            DisposeCounters();
            return false;
        }
    }

    private double ReadCounter(PerformanceCounter? counter)
    {
        if (counter is null)
        {
            return double.NaN;
        }

        try
        {
            var value = counter.NextValue();
            return double.IsNaN(value) ? double.NaN : value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (_loggedCounterFailures.Add(counter.CounterName))
            {
                _logger.Warning(ex, "CPU performance counter {Counter} is unavailable.", counter.CounterName);
            }

            return double.NaN;
        }
    }

    private void DisposeCounters()
    {
        _utilizationCounter?.Dispose();
        _performanceCounter?.Dispose();
        _frequencyCounter?.Dispose();
        _utilizationCounter = null;
        _performanceCounter = null;
        _frequencyCounter = null;
    }
}
