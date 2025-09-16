using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Threading.Channels;
using BatteryTracker.Shared.Configuration;
using BatteryTracker.Shared.Sessions;
using BatteryTracker.Shared.Telemetry;
using Serilog;

namespace BatteryTracker.Collector.Sessions;

/// <summary>
/// Computes throughput for wireless network interfaces by sampling byte counters.
/// </summary>
public sealed class WirelessThroughputSensor : ISensorAdapter
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, InterfaceSnapshot> _previousSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private Channel<MetricSample> _channel = Channel.CreateUnbounded<MetricSample>();
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    public WirelessThroughputSensor(ILogger logger)
    {
        _logger = logger.ForContext<WirelessThroughputSensor>();
    }

    public Task StartAsync(SessionMetadata session, SamplingPolicy policy, CancellationToken cancellationToken)
    {
        _channel = Channel.CreateUnbounded<MetricSample>();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new PeriodicTimer(policy.MediumPriorityInterval);
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

                foreach (var @interface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (@interface.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 || @interface.OperationalStatus != OperationalStatus.Up)
                    {
                        continue;
                    }

                    InterfaceSnapshot previous;
                    if (!_previousSnapshots.TryGetValue(@interface.Id, out previous))
                    {
                        previous = new InterfaceSnapshot(0, 0, now);
                    }

                    var statistics = SafeGetStatistics(@interface);
                    if (statistics is null)
                    {
                        continue;
                    }

                    var seconds = (now - previous.Timestamp).TotalSeconds;
                    if (seconds <= 0)
                    {
                        _previousSnapshots[@interface.Id] = new InterfaceSnapshot(statistics.BytesSent, statistics.BytesReceived, now);
                        continue;
                    }

                    var rxBytes = statistics.BytesReceived - previous.BytesReceived;
                    var txBytes = statistics.BytesSent - previous.BytesSent;
                    if (rxBytes < 0 || txBytes < 0)
                    {
                        rxBytes = Math.Max(0, statistics.BytesReceived);
                        txBytes = Math.Max(0, statistics.BytesSent);
                    }

                    var rxMbps = rxBytes * 8d / seconds / 1_000_000d;
                    var txMbps = txBytes * 8d / seconds / 1_000_000d;
                    var linkSpeedMbps = @interface.Speed > 0 ? @interface.Speed / 1_000_000d : double.NaN;
                    var utilization = double.IsNaN(linkSpeedMbps) || linkSpeedMbps <= 0
                        ? double.NaN
                        : Math.Clamp((rxMbps + txMbps) / linkSpeedMbps * 100d, 0d, 100d);

                    samples.Add(new MetricSample(now, TelemetryComponent.Wireless, @interface.Name + " (Rx)", TelemetryMetric.ThroughputMbps, rxMbps, "Mbps", "NetworkInterface", confidence: 0.6));
                    samples.Add(new MetricSample(now, TelemetryComponent.Wireless, @interface.Name + " (Tx)", TelemetryMetric.ThroughputMbps, txMbps, "Mbps", "NetworkInterface", confidence: 0.6));

                    if (!double.IsNaN(utilization))
                    {
                        samples.Add(new MetricSample(now, TelemetryComponent.Wireless, @interface.Name, TelemetryMetric.UtilizationPercent, utilization, "%", "NetworkInterface", confidence: 0.5));
                    }

                    _previousSnapshots[@interface.Id] = new InterfaceSnapshot(statistics.BytesSent, statistics.BytesReceived, now);
                }

                foreach (var sample in samples)
                {
                    await _channel.Writer.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
                }

                if (samples.Count > 0)
                {
                    _logger.Verbose("Published {Count} wireless telemetry samples for session {SessionId}.", samples.Count, sessionId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Wireless throughput sensor pump cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error while publishing wireless telemetry samples.");
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private static IPInterfaceStatistics? SafeGetStatistics(NetworkInterface @interface)
    {
        try
        {
            return @interface.GetIPv4Statistics();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    private sealed record InterfaceSnapshot(long BytesSent, long BytesReceived, DateTimeOffset Timestamp);
}
