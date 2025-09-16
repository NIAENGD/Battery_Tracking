using System;
using System.IO;
using System.Threading.Tasks;
using BatteryTracker.Collector.Sessions;
using BatteryTracker.Shared.Configuration;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BatteryTracker.App.ViewModels;

public partial class DashboardViewModel : ObservableObject, IAsyncDisposable
{
    private readonly CollectorHost _collectorHost;
    private readonly SamplingPolicy _policy;

    [ObservableProperty]
    private bool _isSessionRunning;

    [ObservableProperty]
    private DateTimeOffset? _sessionStartTime;

    [ObservableProperty]
    private string _statusMessage = "Ready to start telemetry tracking.";

    public DashboardViewModel()
    {
        var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryTracker");
        _collectorHost = CollectorHost.CreateDefault(dataDirectory);
        _policy = new SamplingPolicy();
        StartSessionCommand = new AsyncRelayCommand(StartSessionAsync, () => !IsSessionRunning);
        StopSessionCommand = new AsyncRelayCommand(StopSessionAsync, () => IsSessionRunning);
    }

    public IAsyncRelayCommand StartSessionCommand { get; }

    public IAsyncRelayCommand StopSessionCommand { get; }

    public string SamplingPolicyDescription =>
        $"High: {_policy.HighPriorityInterval.TotalSeconds:F0}s | Medium: {_policy.MediumPriorityInterval.TotalSeconds:F0}s | Low: {_policy.LowPriorityInterval.TotalSeconds:F0}s";

    private async Task StartSessionAsync()
    {
        if (IsSessionRunning)
        {
            return;
        }

        try
        {
            var session = await _collectorHost.StartSessionAsync().ConfigureAwait(false);
            SessionStartTime = session.StartedAt;
            IsSessionRunning = true;
            StatusMessage = $"Session {session.SessionId} started at {session.StartedAt:G}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to start session: {ex.Message}";
        }
        finally
        {
            UpdateCommands();
        }
    }

    private async Task StopSessionAsync()
    {
        if (!IsSessionRunning)
        {
            return;
        }

        try
        {
            await _collectorHost.StopSessionAsync().ConfigureAwait(false);
            IsSessionRunning = false;
            StatusMessage = "Session stopped and telemetry flushed to storage.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to stop session: {ex.Message}";
        }
        finally
        {
            UpdateCommands();
        }
    }

    private void UpdateCommands()
    {
        (StartSessionCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (StopSessionCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }

    public async ValueTask DisposeAsync()
    {
        await _collectorHost.StopSessionAsync().ConfigureAwait(false);
        await _collectorHost.DisposeAsync().ConfigureAwait(false);
    }
}
