using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Addons;

public sealed class AddonHost
{
    private readonly object syncRoot = new();
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan startTimeout;
    private readonly TimeSpan stopTimeout;
    private readonly Func<Func<Task>, Task> startWorkScheduler;
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly List<AddonRuntime> knownAddons = [];

    public AddonHost(
        ITelemetrySink sink,
        TimeProvider timeProvider,
        TimeSpan startTimeout,
        TimeSpan stopTimeout)
        : this(
            sink,
            timeProvider,
            startTimeout,
            stopTimeout,
            static startWork => Task.Run(startWork))
    {
    }

    private AddonHost(
        ITelemetrySink sink,
        TimeProvider timeProvider,
        TimeSpan startTimeout,
        TimeSpan stopTimeout,
        Func<Func<Task>, Task> startWorkScheduler)
    {
        if (startTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(startTimeout), startTimeout, "Start timeout must be positive.");
        }

        if (stopTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stopTimeout), stopTimeout, "Stop timeout must be positive.");
        }

        this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.startTimeout = startTimeout;
        this.stopTimeout = stopTimeout;
        this.startWorkScheduler = startWorkScheduler ?? throw new ArgumentNullException(nameof(startWorkScheduler));
    }

    public Task StartAsync(IEnumerable<ITelemetryAddon> addons, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addons);

        foreach (var addon in addons)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryValidate(addon, out var validationMessage))
            {
                PublishHealth(addon, "validation_failed", validationMessage, TelemetryPriority.Important);
                continue;
            }

            var runtime = new AddonRuntime(addon);

            lock (syncRoot)
            {
                knownAddons.Add(runtime);
            }

            _ = startWorkScheduler(() => StartOneAsync(runtime));
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        RequestShutdownCancellationWithoutWaiting();

        AddonRuntime[] addons;
        lock (syncRoot)
        {
            addons = knownAddons.ToArray();
        }

        foreach (var runtime in addons)
        {
            if (!TryBeginStop(runtime))
            {
                continue;
            }

            var addon = runtime.Addon;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(stopTimeout);
            Task? stopTask = null;

            try
            {
                stopTask = addon.StopAsync(timeoutCts.Token);
                await stopTask.WaitAsync(stopTimeout, cancellationToken);
                MarkStopped(runtime);
                PublishHealth(addon, "stopped", "Add-on stopped.", TelemetryPriority.Routine);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObserveLateFault(stopTask, addon, "stop_error");
                throw;
            }
            catch (TimeoutException)
            {
                ObserveLateFault(stopTask, addon, "stop_error");
                MarkStopped(runtime);
                PublishHealth(addon, "stop_timeout", "Add-on stop timed out.", TelemetryPriority.Important);
            }
            catch (OperationCanceledException)
            {
                ObserveLateFault(stopTask, addon, "stop_error");
                MarkStopped(runtime);
                PublishHealth(addon, "stop_timeout", "Add-on stop was canceled.", TelemetryPriority.Important);
            }
            catch (Exception ex)
            {
                MarkStopped(runtime);
                PublishHealth(addon, "stop_error", ex.Message, TelemetryPriority.Important);
            }
        }
    }

    private void RequestShutdownCancellationWithoutWaiting()
    {
        Task cancellationTask;

        try
        {
            cancellationTask = shutdownCts.CancelAsync();
        }
        catch
        {
            return;
        }

        _ = ObserveCancellationWithoutThrowingAsync(cancellationTask);
    }

    private static async Task ObserveCancellationWithoutThrowingAsync(Task cancellationTask)
    {
        try
        {
            await cancellationTask.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task StartOneAsync(AddonRuntime runtime)
    {
        if (Interlocked.CompareExchange(
                ref runtime.State,
                AddonRuntimeState.Starting,
                AddonRuntimeState.PendingStart) != AddonRuntimeState.PendingStart)
        {
            return;
        }

        var addon = runtime.Addon;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);
        timeoutCts.CancelAfter(startTimeout);
        Task? startTask = null;

        try
        {
            var context = new AddonContext(sink, timeProvider, shutdownCts.Token);
            startTask = addon.StartAsync(context, timeoutCts.Token);
            await startTask.WaitAsync(startTimeout, shutdownCts.Token);

            if (Interlocked.CompareExchange(
                    ref runtime.State,
                    AddonRuntimeState.Started,
                    AddonRuntimeState.Starting) == AddonRuntimeState.Starting)
            {
                PublishHealth(addon, "started", "Add-on started.", TelemetryPriority.Routine);
            }
        }
        catch (TimeoutException)
        {
            ObserveLateFault(startTask, addon, "start_error");
            PublishHealth(addon, "start_timeout", "Add-on start timed out.", TelemetryPriority.Important);
        }
        catch (OperationCanceledException)
        {
            ObserveLateFault(startTask, addon, "start_error");
            PublishHealth(addon, "start_timeout", "Add-on start was canceled.", TelemetryPriority.Important);
        }
        catch (Exception ex)
        {
            PublishHealth(addon, "start_error", ex.Message, TelemetryPriority.Important);
        }
    }

    private static bool TryBeginStop(AddonRuntime runtime)
    {
        while (true)
        {
            var state = Volatile.Read(ref runtime.State);
            switch (state)
            {
                case AddonRuntimeState.PendingStart:
                    if (Interlocked.CompareExchange(
                            ref runtime.State,
                            AddonRuntimeState.StartSkipped,
                            AddonRuntimeState.PendingStart) == AddonRuntimeState.PendingStart)
                    {
                        return false;
                    }

                    break;

                case AddonRuntimeState.Starting:
                case AddonRuntimeState.Started:
                    if (Interlocked.CompareExchange(
                            ref runtime.State,
                            AddonRuntimeState.Stopping,
                            state) == state)
                    {
                        return true;
                    }

                    break;

                default:
                    return false;
            }
        }
    }

    private static void MarkStopped(AddonRuntime runtime)
        => Volatile.Write(ref runtime.State, AddonRuntimeState.Stopped);

    private bool TryValidate(ITelemetryAddon addon, out string message)
    {
        try
        {
            var validation = addon.Validate();
            if (validation.IsValid)
            {
                message = "Add-on validation succeeded.";
                return true;
            }

            message = validation.Errors.Count == 0
                ? "Add-on validation failed."
                : string.Join("; ", validation.Errors);
            return false;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    private void ObserveLateFault(Task? task, ITelemetryAddon addon, string status)
    {
        if (task is null)
        {
            return;
        }

        if (task.IsFaulted)
        {
            PublishTaskFault(addon, status, task);
            return;
        }

        if (task.IsCompleted)
        {
            return;
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                PublishTaskFault(addon, status, completedTask);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PublishTaskFault(ITelemetryAddon addon, string status, Task task)
    {
        var exception = task.Exception?.GetBaseException();
        PublishHealth(
            addon,
            status,
            exception?.Message ?? "Add-on task faulted after timeout.",
            TelemetryPriority.Important);
    }

    private void PublishHealth(ITelemetryAddon addon, string status, string message, TelemetryPriority priority)
    {
        var attributes = new Dictionary<string, object?>
        {
            ["addon.id"] = addon.Metadata.Id,
            ["status"] = status,
            ["message"] = message,
        };

        if (!string.IsNullOrWhiteSpace(addon.Metadata.DisplayName))
        {
            attributes["addon.name"] = addon.Metadata.DisplayName;
        }

        sink.TryPublish(TelemetryRecord.Health(
            timeProvider.GetUtcNow(),
            $"addon.{addon.Metadata.Id}",
            "ninaotel.addon.health",
            priority,
            attributes));
    }

    private sealed class AddonRuntime(ITelemetryAddon addon)
    {
        public ITelemetryAddon Addon { get; } = addon;
        public int State = AddonRuntimeState.PendingStart;
    }

    private static class AddonRuntimeState
    {
        public const int PendingStart = 0;
        public const int Starting = 1;
        public const int Started = 2;
        public const int Stopping = 3;
        public const int Stopped = 4;
        public const int StartSkipped = 5;
    }
}
