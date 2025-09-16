using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using BatteryTracker.Collector.Sessions;

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

    root.AddCommand(startCommand);
    root.AddCommand(stopCommand);
    root.AddCommand(statusCommand);
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
