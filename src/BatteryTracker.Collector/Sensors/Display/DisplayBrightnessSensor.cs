using System;
using System.Collections.Generic;
using System.Management;
using System.Threading.Channels;
using BatteryTracker.Shared.Configuration;
using BatteryTracker.Shared.Sessions;
using BatteryTracker.Shared.Telemetry;
using Serilog;

namespace BatteryTracker.Collector.Sessions;

/// <summary>
/// Samples panel brightness via WMI and estimates panel power consumption based on a nominal model.
/// </summary>
public sealed class DisplayBrightnessSensor : ISensorAdapter, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly HashSet<string> _loggedWarnings = new(StringComparer.OrdinalIgnoreCase);
    private Channel<MetricSample> _channel = Channel.CreateUnbounded<MetricSample>();
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Approximate peak display power draw in milliwatts used for the estimation model.
    /// </summary>
    private const double NominalPanelPowerMilliwatts = 4500d;

    public DisplayBrightnessSensor(ILogger logger)
    {
        _logger = logger.ForContext<DisplayBrightnessSensor>();
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
                if (!TryReadBrightness(out var brightnessPercent))
                {
                    continue;
                }

                var now = DateTimeOffset.UtcNow;
                var estimatedPower = brightnessPercent / 100d * NominalPanelPowerMilliwatts;
                var samples = new[]
                {
                    new MetricSample(now, TelemetryComponent.Display, null, TelemetryMetric.BrightnessPercent, brightnessPercent, "%", "WMI", 0.7),
                    new MetricSample(now, TelemetryComponent.Display, null, TelemetryMetric.PowerMilliwatts, estimatedPower, "mW", "PanelModel", 0.5),
                };

                foreach (var sample in samples)
                {
                    await _channel.Writer.WriteAsync(sample, cancellationToken).ConfigureAwait(false);
                }

                _logger.Verbose("Published display brightness sample ({BrightnessPercent:F1}%) for session {SessionId}.", brightnessPercent, sessionId);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Debug("Display brightness sensor pump cancelled.");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error while publishing display telemetry samples.");
        }
        finally
        {
            _channel.Writer.TryComplete();
        }
    }

    private bool TryReadBrightness(out double brightnessPercent)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var result in searcher.Get())
            {
                if (result["CurrentBrightness"] is byte value)
                {
                    brightnessPercent = value;
                    return true;
                }

                if (result["CurrentBrightness"] is uint uintValue)
                {
                    brightnessPercent = uintValue;
                    return true;
                }
            }

            LogOnce("WMI did not return any monitor brightness entries.");
        }
        catch (ManagementException ex)
        {
            LogOnce("Unable to query WMI for monitor brightness.", ex);
        }
        catch (System.UnauthorizedAccessException ex)
        {
            LogOnce("Access denied when querying WMI monitor brightness. Try running elevated.", ex);
        }

        brightnessPercent = double.NaN;
        return false;
    }

    private void LogOnce(string message, Exception? ex = null)
    {
        if (_loggedWarnings.Add(message))
        {
            if (ex is null)
            {
                _logger.Warning(message);
            }
            else
            {
                _logger.Warning(ex, message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
