using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BatteryTracker.Shared.Configuration;
using BatteryTracker.Shared.Sessions;
using BatteryTracker.Shared.Telemetry;
using Serilog;
using Windows.Devices.Power;

namespace BatteryTracker.Collector.Sessions;

/// <summary>
/// Polls the aggregate battery through WinRT APIs to provide state-of-charge and remaining capacity estimates.
/// </summary>
public sealed class AggregateBatterySensor : ISensorAdapter, IAsyncDisposable
{
    private readonly ILogger _logger;
    private Channel<MetricSample> _channel;
    private readonly Battery _battery;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    public AggregateBatterySensor(ILogger logger)
    {
        _logger = logger.ForContext<AggregateBatterySensor>();
        _channel = Channel.CreateUnbounded<MetricSample>();
        _battery = Battery.AggregateBattery;
    }

    public Task StartAsync(SessionMetadata session, SamplingPolicy policy, CancellationToken cancellationToken)
    {
        _channel = Channel.CreateUnbounded<MetricSample>();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new PeriodicTimer(policy.MediumPriorityInterval);
        _ = Task.Run(() => PumpAsync(session.SessionId, _cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cts?.Cancel();
        if (_timer is not null)
        {
            _timer.Dispose();
            _timer = null;
        }

        _cts?.Dispose();
        _cts = null;
        _channel.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<MetricSample> ReadSamplesAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    private async Task PumpAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        try
        {
            while (_timer is not null && await _timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var report = _battery.GetReport();
                var now = DateTimeOffset.UtcNow;
                var samples = new List<MetricSample>
                {
                    new(now, TelemetryComponent.Battery, null, TelemetryMetric.RemainingCapacityMilliwattHours, report.RemainingCapacityInMilliwattHours ?? 0, "mWh", "Windows.Devices.Power"),
                    new(now, TelemetryComponent.Battery, null, TelemetryMetric.FullChargeCapacityMilliwattHours, report.FullChargeCapacityInMilliwattHours ?? 0, "mWh", "Windows.Devices.Power"),
                };

                foreach (var sample in samples)
                {
                    await _channel.Writer.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
                }

                _logger.Verbose("Published aggregate battery report for session {SessionId} at {Timestamp}.", sessionId, now);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Battery sensor pump cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Battery sensor encountered an unexpected error.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
