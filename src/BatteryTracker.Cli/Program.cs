using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BatteryTracker.Collector.Sessions;
using BatteryTracker.Shared.Sessions;
using Microsoft.Data.Sqlite;

var rootCommand = BuildRootCommand();
return await rootCommand.InvokeAsync(args).ConfigureAwait(false);

static RootCommand BuildRootCommand()
{
    var dataDirectoryOption = new Option<DirectoryInfo>(new[] { "--data-directory", "-d" },
        () => new DirectoryInfo(GetDefaultDataDirectory()),
        "Directory that stores the collector database, logs, and session metadata.");

    var root = new RootCommand("Battery Tracker command-line interface for controlling the telemetry collector.");
    root.AddGlobalOption(dataDirectoryOption);

    var startCommand = new Command("start", "Start a telemetry session and continue running until stopped.");
    var notesOption = new Option<string?>("--notes", description: "Optional notes to attach to the session record.");
    var durationOption = new Option<TimeSpan?>("--duration", description: "Automatically stop after the specified duration (e.g. 00:15:00).");
    startCommand.AddOption(notesOption);
    startCommand.AddOption(durationOption);
    startCommand.SetHandler(StartAsync, dataDirectoryOption, notesOption, durationOption);

    var stopCommand = new Command("stop", "Signal the running telemetry session to stop.");
    stopCommand.SetHandler(Stop, dataDirectoryOption);

    var statusCommand = new Command("status", "Display the status of the collector and the most recent session.");
    statusCommand.SetHandler(StatusAsync, dataDirectoryOption);

    var selfTestCommand = new Command("selftest", "Run a diagnostics session that validates core and extended telemetry.");
    var outputOption = new Option<FileInfo?>("--output", description: "Optional path for the diagnostics summary log.");
    var selfTestDurationOption = new Option<TimeSpan>("--duration", () => TimeSpan.FromSeconds(20), "Duration of the diagnostics capture.");
    selfTestCommand.AddOption(outputOption);
    selfTestCommand.AddOption(selfTestDurationOption);
    selfTestCommand.SetHandler(SelfTestAsync, dataDirectoryOption, outputOption, selfTestDurationOption);

    root.AddCommand(startCommand);
    root.AddCommand(stopCommand);
    root.AddCommand(statusCommand);
    root.AddCommand(selfTestCommand);
    return root;
}

static async Task StartAsync(DirectoryInfo dataDirectory, string? notes, TimeSpan? duration)
{
    Directory.CreateDirectory(dataDirectory.FullName);
    var stopSignalName = GetStopSignalName(dataDirectory.FullName);
    using var stopSignal = new EventWaitHandle(false, EventResetMode.ManualReset, stopSignalName, out _);
    stopSignal.Reset();

    await using var host = CollectorHost.CreateDefault(dataDirectory.FullName);
    var sessionStateStore = new SessionStateStore(dataDirectory.FullName);

    using var cancellationSource = new CancellationTokenSource();
    ConsoleCancelEventHandler? cancelHandler = null;
    cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        if (!cancellationSource.IsCancellationRequested)
        {
            Console.WriteLine("Cancellation requested. Stopping telemetry session...");
            cancellationSource.Cancel();
        }
    };
    Console.CancelKeyPress += cancelHandler;

    try
    {
        var session = await host.StartSessionAsync(notes, cancellationSource.Token).ConfigureAwait(false);
        await sessionStateStore.WriteAsync(SessionStateSnapshot.FromMetadata(session), cancellationSource.Token).ConfigureAwait(false);

        Console.WriteLine($"Started telemetry session {session.SessionId} at {session.StartedAt:G}.");
        Console.WriteLine("Press Ctrl+C or run `batterytracker stop` to end the session.");
        if (duration is { } positiveDuration && positiveDuration > TimeSpan.Zero)
        {
            Console.WriteLine($"The collector will automatically stop after {positiveDuration}.");
        }

        var waitForStopTask = WaitForStopSignalAsync(stopSignal, cancellationSource.Token);
        Task? autoStopTask = null;
        if (duration is { } timeout && timeout > TimeSpan.Zero)
        {
            autoStopTask = Task.Delay(timeout, cancellationSource.Token);
        }

        try
        {
            if (autoStopTask is null)
            {
                await waitForStopTask.ConfigureAwait(false);
            }
            else
            {
                var completed = await Task.WhenAny(waitForStopTask, autoStopTask).ConfigureAwait(false);
                if (completed == autoStopTask)
                {
                    Console.WriteLine($"Elapsed {duration} — stopping session.");
                    cancellationSource.Cancel();
                }

                await waitForStopTask.ConfigureAwait(false);
            }
        }
        catch (TaskCanceledException)
        {
            // Cancellation already handled by Ctrl+C handler logging above.
        }

        await host.StopSessionAsync().ConfigureAwait(false);
        await sessionStateStore.WriteAsync(SessionStateSnapshot.FromMetadata(session), CancellationToken.None).ConfigureAwait(false);
        stopSignal.Reset();
        Console.WriteLine($"Session {session.SessionId} stopped at {session.CompletedAt:G}.");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("Start cancelled before telemetry session could initialize.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Failed to run telemetry session: {ex.Message}");
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

static void Stop(DirectoryInfo dataDirectory)
{
    var stopSignalName = GetStopSignalName(dataDirectory.FullName);
    try
    {
        using var stopSignal = EventWaitHandle.OpenExisting(stopSignalName);
        if (stopSignal.Set())
        {
            Console.WriteLine("Stop signal dispatched. Collector will flush and exit.");
        }
        else
        {
            Console.WriteLine("Stop signal was already set. Collector should stop shortly.");
        }
    }
    catch (WaitHandleCannotBeOpenedException)
    {
        Console.WriteLine("No running telemetry session was found for the specified data directory.");
    }
    catch (UnauthorizedAccessException)
    {
        Console.Error.WriteLine("Access denied when signaling the collector. Ensure the process has sufficient privileges.");
    }
}

static async Task StatusAsync(DirectoryInfo dataDirectory)
{
    var sessionStateStore = new SessionStateStore(dataDirectory.FullName);
    try
    {
        var state = await sessionStateStore.ReadAsync().ConfigureAwait(false);
        if (state is null)
        {
            Console.WriteLine("No telemetry sessions have been recorded yet.");
            return;
        }

        if (state.IsActive)
        {
            Console.WriteLine($"Collector appears to be running. Session {state.SessionId} started at {state.StartedAt:G}.");
        }
        else
        {
            Console.WriteLine($"Collector is idle. Last session {state.SessionId} ran from {state.StartedAt:G} to {state.CompletedAt:G}.");
        }

        if (!string.IsNullOrWhiteSpace(state.Notes))
        {
            Console.WriteLine($"Notes: {state.Notes}");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Unable to read collector session state: {ex.Message}");
    }
}

static async Task SelfTestAsync(DirectoryInfo dataDirectory, FileInfo? output, TimeSpan duration)
{
    if (duration <= TimeSpan.Zero)
    {
        duration = TimeSpan.FromSeconds(20);
    }

    Directory.CreateDirectory(dataDirectory.FullName);
    var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    var runDirectory = Path.Combine(dataDirectory.FullName, $"selftest-{timestamp}");
    Directory.CreateDirectory(runDirectory);

    var logPath = output?.FullName ?? Path.Combine(runDirectory, $"selftest-{timestamp}.log");
    var logDirectory = Path.GetDirectoryName(logPath);
    if (!string.IsNullOrWhiteSpace(logDirectory))
    {
        Directory.CreateDirectory(logDirectory);
    }

    await using var host = CollectorHost.CreateDefault(runDirectory);
    await using var logStream = new StreamWriter(File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.Read));
    logStream.AutoFlush = true;

    void Log(string message)
    {
        var stamp = DateTimeOffset.Now.ToString("u", CultureInfo.InvariantCulture);
        var formatted = $"[{stamp}] {message}";
        Console.WriteLine(formatted);
        logStream.WriteLine(formatted);
    }

    Log($"Starting telemetry self-test for {duration:c}. Data captured in {runDirectory}.");

    SessionMetadata? session = null;
    try
    {
        session = await host.StartSessionAsync("Telemetry self-test diagnostics").ConfigureAwait(false);
        Log($"Session {session.SessionId} started at {session.StartedAt:O}.");
        await Task.Delay(duration).ConfigureAwait(false);
    }
    finally
    {
        if (session is not null)
        {
            try
            {
                await host.StopSessionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log($"Failed to stop telemetry session cleanly: {ex.Message}");
                throw;
            }

            Log($"Session {session.SessionId} stopped at {session.CompletedAt:O}.");
        }
    }

    if (session is not null)
    {
        var databasePath = Path.Combine(runDirectory, "battery-tracker.db");
        var summaries = await ReadMetricSummariesAsync(databasePath, session.SessionId).ConfigureAwait(false);
        if (summaries.Count == 0)
        {
            Log("No telemetry samples were captured during the self-test run.");
        }
        else
        {
            Log("Telemetry summary (component/metric):");
            foreach (var summary in summaries)
            {
                Log($"  {summary.Component} :: {summary.Metric} -> count={summary.Count}, min={summary.Min:F2}, max={summary.Max:F2}, avg={summary.Average:F2}");
            }
        }

        var collectorLog = Path.Combine(runDirectory, "logs", "collector.log");
        Log($"Collector log: {collectorLog}");
    }

    Log($"Diagnostics summary saved to {logPath}.");
}

static async Task<IReadOnlyList<MetricSummary>> ReadMetricSummariesAsync(string databasePath, Guid sessionId)
{
    var summaries = new List<MetricSummary>();
    if (!File.Exists(databasePath))
    {
        return summaries;
    }

    await using var connection = new SqliteConnection($"Data Source={databasePath}");
    await connection.OpenAsync().ConfigureAwait(false);

    await using var command = connection.CreateCommand();
    command.CommandText = @"
SELECT component, metric_type, COUNT(*) AS count, MIN(value), MAX(value), AVG(value)
FROM metrics
WHERE session_id = $sessionId
GROUP BY component, metric_type
ORDER BY component, metric_type;";
    command.Parameters.AddWithValue("$sessionId", sessionId.ToString());

    await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
    while (await reader.ReadAsync().ConfigureAwait(false))
    {
        var component = reader.GetString(0);
        var metric = reader.GetString(1);
        var count = reader.GetInt64(2);
        var min = !reader.IsDBNull(3) ? reader.GetDouble(3) : double.NaN;
        var max = !reader.IsDBNull(4) ? reader.GetDouble(4) : double.NaN;
        var average = !reader.IsDBNull(5) ? reader.GetDouble(5) : double.NaN;
        summaries.Add(new MetricSummary(component, metric, count, min, max, average));
    }

    return summaries;
}

private sealed record MetricSummary(string Component, string Metric, long Count, double Min, double Max, double Average);

static async Task WaitForStopSignalAsync(EventWaitHandle stopSignal, CancellationToken cancellationToken)
{
    var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var registration = ThreadPool.RegisterWaitForSingleObject(stopSignal, static (state, _) =>
    {
        ((TaskCompletionSource<bool>)state!).TrySetResult(true);
    }, completionSource, Timeout.Infinite, true);
    using var cancellationRegistration = cancellationToken.Register(static state =>
    {
        ((TaskCompletionSource<bool>)state!).TrySetCanceled();
    }, completionSource);

    try
    {
        await completionSource.Task.ConfigureAwait(false);
    }
    finally
    {
        registration.Unregister(null);
    }
}

static string GetDefaultDataDirectory()
{
    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryTracker");
}

static string GetStopSignalName(string dataDirectory)
{
    var normalized = Path.GetFullPath(dataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
    using var sha256 = SHA256.Create();
    var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(normalized));
    var token = Convert.ToHexString(hash.AsSpan(0, 8));
    return $"Local\\BatteryTracker.StopSignal.{token}";
}
