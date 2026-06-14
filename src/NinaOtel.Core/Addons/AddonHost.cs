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
    private readonly Func<Func<Task>, CancellationToken, Task> startLifecycleScheduler;
    private readonly Func<CancellationToken, Task> startCallbackCommitObserver;
    private readonly Func<Func<Task>, CancellationToken, Task> addonCallbackScheduler;
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly List<AddonRuntime> knownAddons = [];
    private bool shutdownRequested;

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
        : this(
            sink,
            timeProvider,
            startTimeout,
            stopTimeout,
            startWorkScheduler,
            static (startWork, cancellationToken) => Task.Run(startWork, cancellationToken),
            static _ => Task.CompletedTask,
            static (callbackWork, cancellationToken) => Task.Run(callbackWork, cancellationToken))
    {
    }

    private AddonHost(
        ITelemetrySink sink,
        TimeProvider timeProvider,
        TimeSpan startTimeout,
        TimeSpan stopTimeout,
        Func<Func<Task>, Task> startWorkScheduler,
        Func<Func<Task>, CancellationToken, Task> startLifecycleScheduler,
        Func<CancellationToken, Task> startCallbackCommitObserver,
        Func<Func<Task>, CancellationToken, Task> addonCallbackScheduler)
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
        this.startLifecycleScheduler =
            startLifecycleScheduler ?? throw new ArgumentNullException(nameof(startLifecycleScheduler));
        this.startCallbackCommitObserver =
            startCallbackCommitObserver ?? throw new ArgumentNullException(nameof(startCallbackCommitObserver));
        this.addonCallbackScheduler =
            addonCallbackScheduler ?? throw new ArgumentNullException(nameof(addonCallbackScheduler));
    }

    public Task StartAsync(IEnumerable<ITelemetryAddon> addons, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addons);

        foreach (var addon in addons)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runtime = new AddonRuntime(addon);

            lock (syncRoot)
            {
                if (shutdownRequested)
                {
                    continue;
                }

                knownAddons.Add(runtime);
            }

            ScheduleStart(runtime);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        AddonRuntime[] addons;
        lock (syncRoot)
        {
            shutdownRequested = true;
            addons = knownAddons.ToArray();
        }

        RequestShutdownCancellationWithoutWaiting();

        foreach (var runtime in addons)
        {
            var stopTask = GetOrStartStopTask(runtime);
            if (stopTask is null)
            {
                continue;
            }

            try
            {
                await stopTask.WaitAsync(stopTimeout, cancellationToken);
                if (TryPublishStopResult(runtime))
                {
                    PublishHealth(runtime, "stopped", "Add-on stopped.", TelemetryPriority.Routine);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObserveLateFault(stopTask, runtime, "stop_error");
                throw;
            }
            catch (TimeoutException)
            {
                ObserveLateFault(stopTask, runtime, "stop_error");
                if (TryPublishStopResult(runtime))
                {
                    PublishHealth(runtime, "stop_timeout", "Add-on stop timed out.", TelemetryPriority.Important);
                }
            }
            catch (OperationCanceledException)
            {
                ObserveLateFault(stopTask, runtime, "stop_error");
                if (TryPublishStopResult(runtime))
                {
                    PublishHealth(runtime, "stop_timeout", "Add-on stop was canceled.", TelemetryPriority.Important);
                }
            }
            catch (Exception ex)
            {
                if (TryPublishStopResult(runtime))
                {
                    PublishHealth(runtime, "stop_error", ex.Message, TelemetryPriority.Important);
                }
            }
        }
    }

    private void ScheduleStart(AddonRuntime runtime)
    {
        Task scheduledTask;

        try
        {
            scheduledTask = startWorkScheduler(() => RunStartWorkSafelyAsync(runtime));
        }
        catch (Exception ex)
        {
            PublishHealth(runtime, "start_error", ex.Message, TelemetryPriority.Important);
            return;
        }

        ObserveScheduledStartTask(scheduledTask, runtime);
    }

    private async Task RunStartWorkSafelyAsync(AddonRuntime runtime)
    {
        try
        {
            await StartOneAsync(runtime).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PublishHealth(runtime, "start_error", ex.Message, TelemetryPriority.Important);
        }
    }

    private void ObserveScheduledStartTask(Task scheduledTask, AddonRuntime runtime)
    {
        if (scheduledTask.IsFaulted)
        {
            PublishTaskFault(runtime, "start_error", scheduledTask);
            return;
        }

        if (scheduledTask.IsCompleted)
        {
            return;
        }

        _ = scheduledTask.ContinueWith(
            completedTask =>
            {
                PublishTaskFault(runtime, "start_error", completedTask);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
        lock (syncRoot)
        {
            if (shutdownRequested)
            {
                if (runtime.State == AddonRuntimeState.PendingStart)
                {
                    runtime.State = AddonRuntimeState.StartSkipped;
                }

                return;
            }

            if (runtime.State != AddonRuntimeState.PendingStart)
            {
                return;
            }

            runtime.State = AddonRuntimeState.Starting;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownCts.Token);
        timeoutCts.CancelAfter(startTimeout);
        var startTask = InvokeStartAsync(runtime, timeoutCts.Token);

        try
        {
            var result = await startTask.WaitAsync(startTimeout);
            if (result.Status == StartLifecycleStatus.ValidationFailed)
            {
                MarkStartSkipped(runtime);
                PublishHealth(runtime, "validation_failed", result.Message, TelemetryPriority.Important);
                return;
            }

            if (result.Status == StartLifecycleStatus.Skipped)
            {
                MarkStartSkipped(runtime);
                return;
            }

            lock (syncRoot)
            {
                if (runtime.State != AddonRuntimeState.Starting)
                {
                    return;
                }

                runtime.State = AddonRuntimeState.Started;
            }

            PublishHealth(runtime, "started", "Add-on started.", TelemetryPriority.Routine);
        }
        catch (TimeoutException)
        {
            MarkStartSkippedIfStartWasNotInvoked(runtime);
            ObserveLateFault(startTask, runtime, "start_error");
            PublishHealth(runtime, "start_timeout", "Add-on start timed out.", TelemetryPriority.Important);
        }
        catch (OperationCanceledException)
        {
            MarkStartSkippedIfStartWasNotInvoked(runtime);
            ObserveLateFault(startTask, runtime, "start_error");
            PublishHealth(runtime, "start_timeout", "Add-on start was canceled.", TelemetryPriority.Important);
        }
        catch (Exception ex)
        {
            MarkStartSkippedIfStartWasNotInvoked(runtime);
            PublishHealth(runtime, "start_error", ex.Message, TelemetryPriority.Important);
        }
    }

    private Task? GetOrStartStopTask(AddonRuntime runtime)
    {
        lock (syncRoot)
        {
            switch (runtime.State)
            {
                case AddonRuntimeState.PendingStart:
                    runtime.State = AddonRuntimeState.StartSkipped;
                    return null;

                case AddonRuntimeState.Starting:
                    if (!runtime.StartCallbacksCommitted)
                    {
                        runtime.State = AddonRuntimeState.StartSkipped;
                        return null;
                    }

                    if (!runtime.StartInvoked)
                    {
                        runtime.StartCancellationRequested = true;
                        return null;
                    }

                    runtime.State = AddonRuntimeState.Stopping;
                    runtime.StopTask ??= InvokeStopAsync(runtime.Addon);
                    return runtime.StopTask;

                case AddonRuntimeState.Started:
                    runtime.State = AddonRuntimeState.Stopping;
                    runtime.StopTask ??= InvokeStopAsync(runtime.Addon);
                    return runtime.StopTask;

                case AddonRuntimeState.Stopping:
                    return runtime.StopTask;

                default:
                    return null;
            }
        }
    }

    private Task<StartLifecycleResult> InvokeStartAsync(AddonRuntime runtime, CancellationToken cancellationToken)
        => InvokeScheduledStartAsync(runtime, cancellationToken);

    private async Task<StartLifecycleResult> InvokeScheduledStartAsync(
        AddonRuntime runtime,
        CancellationToken cancellationToken)
    {
        var result = StartLifecycleResult.Skipped;

        await startLifecycleScheduler(
            async () =>
            {
                result = await InvokeStartCoreAsync(runtime, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async Task<StartLifecycleResult> InvokeStartCoreAsync(
        AddonRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (!TryCommitStartCallbacks(runtime, cancellationToken))
        {
            return StartLifecycleResult.Skipped;
        }

        await startCallbackCommitObserver(cancellationToken).ConfigureAwait(false);
        if (ShouldCancelCommittedStartBeforeAddonCallbacks(runtime, cancellationToken))
        {
            return StartLifecycleResult.Skipped;
        }

        var addon = runtime.Addon;
        var metadata = await InvokeMetadataAsync(addon, cancellationToken).ConfigureAwait(false);
        SetMetadataSnapshot(runtime, metadata);
        cancellationToken.ThrowIfCancellationRequested();
        if (ShouldCancelCommittedStartBeforeAddonCallbacks(runtime, cancellationToken))
        {
            return StartLifecycleResult.Skipped;
        }

        var validation = await InvokeValidationAsync(addon, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!validation.IsValid)
        {
            var message = validation.Errors.Count == 0
                ? "Add-on validation failed."
                : string.Join("; ", validation.Errors);
            return StartLifecycleResult.ValidationFailed(message);
        }

        lock (syncRoot)
        {
            if (runtime.State != AddonRuntimeState.Starting ||
                runtime.StartInvoked ||
                runtime.StartCancellationRequested ||
                cancellationToken.IsCancellationRequested)
            {
                return StartLifecycleResult.Skipped;
            }

            runtime.StartInvoked = true;
        }

        var context = new AddonContext(sink, timeProvider, shutdownCts.Token);
        await InvokeAddonStartAsync(addon, context, cancellationToken).ConfigureAwait(false);
        return StartLifecycleResult.Started;
    }

    private Task<AddonIdentity> InvokeMetadataAsync(ITelemetryAddon addon, CancellationToken cancellationToken)
        => InvokeAddonCallbackAsync(() => CaptureMetadata(addon), cancellationToken);

    private Task<AddonValidationResult> InvokeValidationAsync(
        ITelemetryAddon addon,
        CancellationToken cancellationToken)
        => InvokeAddonCallbackAsync(addon.Validate, cancellationToken);

    private Task InvokeAddonStartAsync(
        ITelemetryAddon addon,
        IAddonContext context,
        CancellationToken cancellationToken)
        => addonCallbackScheduler(
            () => addon.StartAsync(context, cancellationToken),
            cancellationToken);

    private async Task<T> InvokeAddonCallbackAsync<T>(Func<T> callback, CancellationToken cancellationToken)
    {
        T? result = default;

        await addonCallbackScheduler(
            () =>
            {
                result = callback();
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        return result!;
    }

    private bool TryCommitStartCallbacks(AddonRuntime runtime, CancellationToken cancellationToken)
    {
        lock (syncRoot)
        {
            if (cancellationToken.IsCancellationRequested ||
                runtime.State != AddonRuntimeState.Starting ||
                shutdownRequested ||
                runtime.StartCallbacksCommitted)
            {
                return false;
            }

            runtime.StartCallbacksCommitted = true;
            return true;
        }
    }

    private bool ShouldCancelCommittedStartBeforeAddonCallbacks(
        AddonRuntime runtime,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return true;
        }

        lock (syncRoot)
        {
            return runtime.State != AddonRuntimeState.Starting ||
                runtime.StartCancellationRequested;
        }
    }

    private Task InvokeStopAsync(ITelemetryAddon addon)
        => Task.Run(async () =>
        {
            using var timeoutCts = new CancellationTokenSource(stopTimeout);
            await addon.StopAsync(timeoutCts.Token).ConfigureAwait(false);
        });

    private bool TryPublishStopResult(AddonRuntime runtime)
    {
        lock (syncRoot)
        {
            if (runtime.StopResultPublished)
            {
                return false;
            }

            runtime.StopResultPublished = true;
            runtime.State = AddonRuntimeState.Stopped;
            return true;
        }
    }

    private static AddonIdentity CaptureMetadata(ITelemetryAddon addon)
    {
        var metadata = addon.Metadata;
        var id = string.IsNullOrWhiteSpace(metadata.Id)
            ? AddonIdentity.Unknown.Id
            : metadata.Id;
        var displayName = string.IsNullOrWhiteSpace(metadata.DisplayName)
            ? null
            : metadata.DisplayName;

        return new AddonIdentity(id, displayName);
    }

    private void SetMetadataSnapshot(AddonRuntime runtime, AddonIdentity metadata)
    {
        lock (syncRoot)
        {
            runtime.Metadata = metadata;
        }
    }

    private void MarkStartSkipped(AddonRuntime runtime)
    {
        lock (syncRoot)
        {
            if (runtime.State == AddonRuntimeState.Starting)
            {
                runtime.State = AddonRuntimeState.StartSkipped;
            }
        }
    }

    private void MarkStartSkippedIfStartWasNotInvoked(AddonRuntime runtime)
    {
        lock (syncRoot)
        {
            if (runtime.State == AddonRuntimeState.Starting && !runtime.StartInvoked)
            {
                runtime.State = AddonRuntimeState.StartSkipped;
            }
        }
    }

    private void ObserveLateFault(Task? task, AddonRuntime runtime, string status)
    {
        if (task is null)
        {
            return;
        }

        if (task.IsFaulted)
        {
            PublishTaskFault(runtime, status, task);
            return;
        }

        if (task.IsCompleted)
        {
            return;
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                PublishTaskFault(runtime, status, completedTask);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PublishTaskFault(AddonRuntime runtime, string status, Task task)
    {
        var exception = task.Exception?.GetBaseException();
        PublishHealth(
            runtime,
            status,
            exception?.Message ?? "Add-on task faulted after timeout.",
            TelemetryPriority.Important);
    }

    private void PublishHealth(AddonRuntime runtime, string status, string message, TelemetryPriority priority)
    {
        try
        {
            var metadata = GetMetadataSnapshot(runtime);
            var attributes = new Dictionary<string, object?>
            {
                ["addon.id"] = metadata.Id,
                ["status"] = status,
                ["message"] = message,
            };

            if (!string.IsNullOrWhiteSpace(metadata.DisplayName))
            {
                attributes["addon.name"] = metadata.DisplayName;
            }

            sink.TryPublish(TelemetryRecord.Health(
                timeProvider.GetUtcNow(),
                $"addon.{metadata.Id}",
                "ninaotel.addon.health",
                priority,
                attributes));
        }
        catch
        {
        }
    }

    private AddonIdentity GetMetadataSnapshot(AddonRuntime runtime)
    {
        lock (syncRoot)
        {
            return runtime.Metadata ?? AddonIdentity.Unknown;
        }
    }

    private sealed class AddonRuntime(ITelemetryAddon addon)
    {
        public ITelemetryAddon Addon { get; } = addon;
        public int State = AddonRuntimeState.PendingStart;
        public AddonIdentity? Metadata;
        public bool StartCallbacksCommitted;
        public bool StartCancellationRequested;
        public bool StartInvoked;
        public Task? StopTask;
        public bool StopResultPublished;
    }

    private sealed record AddonIdentity(string Id, string? DisplayName)
    {
        public static AddonIdentity Unknown { get; } = new("unknown", null);
    }

    private sealed record StartLifecycleResult(StartLifecycleStatus Status, string Message)
    {
        public static StartLifecycleResult Started { get; } =
            new(StartLifecycleStatus.Started, string.Empty);

        public static StartLifecycleResult Skipped { get; } =
            new(StartLifecycleStatus.Skipped, string.Empty);

        public static StartLifecycleResult ValidationFailed(string message)
            => new(StartLifecycleStatus.ValidationFailed, message);
    }

    private enum StartLifecycleStatus
    {
        Started,
        ValidationFailed,
        Skipped,
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
