using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Channels;
using BatteryTracker.Shared.Configuration;
using BatteryTracker.Shared.Sessions;
using BatteryTracker.Shared.Telemetry;
using Serilog;

namespace BatteryTracker.Collector.Sessions;

/// <summary>
/// Captures GPU engine utilization and adapter power data from Windows performance counters.
/// </summary>
public sealed class GpuEngineSensor : ISensorAdapter, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly HashSet<string> _loggedWarnings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<GpuCounter> _engineCounters = new();
    private readonly List<GpuCounter> _powerCounters = new();
    private Channel<MetricSample> _channel = Channel.CreateUnbounded<MetricSample>();
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    public GpuEngineSensor(ILogger logger)
    {
        _logger = logger.ForContext<GpuEngineSensor>();
    }

    public Task StartAsync(SessionMetadata session, SamplingPolicy policy, CancellationToken cancellationToken)
    {
        _channel = Channel.CreateUnbounded<MetricSample>();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new PeriodicTimer(policy.HighPriorityInterval);

        InitializeCounters();
        if (_engineCounters.Count == 0 && _powerCounters.Count == 0)
        {
            _logger.Warning("GPU telemetry counters are not available. Extended GPU metrics will be skipped for session {SessionId}.", session.SessionId);
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
                var samples = new List<MetricSample>();
                var utilizationSnapshots = new List<double>();

                foreach (var counter in _engineCounters)
                {
                    var value = ReadCounter(counter.Counter);
                    if (double.IsNaN(value))
                    {
                        continue;
                    }

                    utilizationSnapshots.Add(value);
                    samples.Add(new MetricSample(
                        now,
                        TelemetryComponent.Gpu,
                        counter.Label,
                        TelemetryMetric.UtilizationPercent,
                        Math.Clamp(value, 0, 100),
                        "%",
                        counter.Source,
                        confidence: 0.8));
                }

                if (utilizationSnapshots.Count > 0)
                {
                    samples.Add(new MetricSample(
                        now,
                        TelemetryComponent.Gpu,
                        null,
                        TelemetryMetric.UtilizationPercent,
                        Math.Clamp(utilizationSnapshots.Average(), 0, 100),
                        "%",
                        "PDH (GPU Engine)",
                        confidence: 0.7));
                }

                if (_powerCounters.Count > 0)
                {
                    var totalPower = 0.0;
                    foreach (var counter in _powerCounters)
                    {
                        var value = ReadCounter(counter.Counter);
                        if (double.IsNaN(value))
                        {
                            continue;
                        }

                        totalPower += value;
                        samples.Add(new MetricSample(
                            now,
                            TelemetryComponent.Gpu,
                            counter.Label,
                            TelemetryMetric.PowerMilliwatts,
                            value,
                            "mW",
                            counter.Source,
                            confidence: 0.6));
                    }

                    if (totalPower > 0)
                    {
                        samples.Add(new MetricSample(
                            now,
                            TelemetryComponent.Gpu,
                            null,
                            TelemetryMetric.PowerMilliwatts,
                            totalPower,
                            "mW",
                            "PDH (GPU Adapter)",
                            confidence: 0.6));
                    }
                }

                foreach (var sample in samples)
                {
                    await _channel.Writer.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
                }

                if (samples.Count > 0)
                {
                    _logger.Verbose("Published {Count} GPU samples for session {SessionId}.", samples.Count, sessionId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("GPU engine sensor pump cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error while publishing GPU telemetry samples.");
        }
        finally
        {
            DisposeCounters();
            _channel.Writer.TryComplete();
        }
    }

    private void InitializeCounters()
    {
        DisposeCounters();

        try
        {
            if (PerformanceCounterCategory.Exists("GPU Engine"))
            {
                var category = new PerformanceCounterCategory("GPU Engine");
                foreach (var instance in category.GetInstanceNames())
                {
                    if (!instance.Contains("engtype", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        var counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, readOnly: true);
                        _ = counter.NextValue();
                        _engineCounters.Add(new GpuCounter(NormalizeInstanceName(instance), counter, "PDH"));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        LogOnce($"GPU Engine counter initialization failed for instance '{instance}'.", ex);
                    }
                }
            }
            else
            {
                _logger.Warning("GPU Engine performance counter category is not available on this system.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.Warning(ex, "Unable to enumerate GPU Engine performance counters.");
        }

        try
        {
            if (PerformanceCounterCategory.Exists("GPU Adapter"))
            {
                var category = new PerformanceCounterCategory("GPU Adapter");
                if (category.CounterExists("Power"))
                {
                    foreach (var instance in category.GetInstanceNames())
                    {
                        try
                        {
                            var counter = new PerformanceCounter("GPU Adapter", "Power", instance, readOnly: true);
                            _ = counter.NextValue();
                            _powerCounters.Add(new GpuCounter(instance, counter, "PDH"));
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
                        {
                            LogOnce($"GPU Adapter power counter failed for instance '{instance}'.", ex);
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.Warning(ex, "Unable to enumerate GPU Adapter performance counters.");
        }
    }

    private static string NormalizeInstanceName(string instance)
    {
        var parts = instance.Split('\\');
        var lastSegment = parts.Length > 0 ? parts[^1] : instance;
        return lastSegment.Replace("engtype_", string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private double ReadCounter(PerformanceCounter counter)
    {
        try
        {
            var value = counter.NextValue();
            return double.IsNaN(value) ? double.NaN : value;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            LogOnce($"GPU counter {counter.CounterName} for instance '{counter.InstanceName}' is unavailable.", ex);
            return double.NaN;
        }
    }

    private void DisposeCounters()
    {
        foreach (var counter in _engineCounters)
        {
            counter.Counter.Dispose();
        }

        foreach (var counter in _powerCounters)
        {
            counter.Counter.Dispose();
        }

        _engineCounters.Clear();
        _powerCounters.Clear();
    }

    private void LogOnce(string message, Exception ex)
    {
        if (_loggedWarnings.Add(message))
        {
            _logger.Warning(ex, message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private sealed record GpuCounter(string Label, PerformanceCounter Counter, string Source);
}
