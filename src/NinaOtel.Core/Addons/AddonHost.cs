using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Addons;

public sealed class AddonHost
{
    private readonly object syncRoot = new();
    private readonly SemaphoreSlim lifecycleGate = new(1, 1);
    private readonly ITelemetrySink sink;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan startTimeout;
    private readonly TimeSpan stopTimeout;
    private readonly Func<Func<Task>, Task> startWorkScheduler;
    private readonly Func<Func<Task>, CancellationToken, Task> startLifecycleScheduler;
    private readonly Func<CancellationToken, Task> startCallbackCommitObserver;
    private readonly Func<Func<Task>, CancellationToken, Task> addonCallbackScheduler;
    private readonly Action<string, string, string, TelemetryPriority>? healthCallback;
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly List<AddonRuntime> knownAddons = [];
    private IReadOnlyDictionary<string, AddonConfiguration> addonConfigurations;
    private bool shutdownRequested;

    public AddonHost(
        ITelemetrySink sink,
        TimeProvider timeProvider,
        TimeSpan startTimeout,
        TimeSpan stopTimeout,
        IReadOnlyDictionary<string, AddonConfiguration>? addonConfigurations = null,
        Action<string, string, string, TelemetryPriority>? healthCallback = null)
        : this(
            sink,
            timeProvider,
            startTimeout,
            stopTimeout,
            static startWork => Task.Run(startWork),
            addonConfigurations,
            healthCallback)
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
        IReadOnlyDictionary<string, AddonConfiguration>? addonConfigurations,
        Action<string, string, string, TelemetryPriority>? healthCallback)
        : this(
            sink,
            timeProvider,
            startTimeout,
            stopTimeout,
            startWorkScheduler,
            static (startWork, cancellationToken) => Task.Run(startWork, cancellationToken),
            static _ => Task.CompletedTask,
            static (callbackWork, cancellationToken) => Task.Run(callbackWork, cancellationToken),
            addonConfigurations,
            healthCallback)
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
        : this(
            sink,
            timeProvider,
            startTimeout,
            stopTimeout,
            startWorkScheduler,
            startLifecycleScheduler,
            startCallbackCommitObserver,
            addonCallbackScheduler,
            null,
            null)
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
        Func<Func<Task>, CancellationToken, Task> addonCallbackScheduler,
        IReadOnlyDictionary<string, AddonConfiguration>? addonConfigurations,
        Action<string, string, string, TelemetryPriority>? healthCallback)
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
        this.addonConfigurations = SnapshotAddonConfigurations(addonConfigurations);
        this.startWorkScheduler = startWorkScheduler ?? throw new ArgumentNullException(nameof(startWorkScheduler));
        this.startLifecycleScheduler =
            startLifecycleScheduler ?? throw new ArgumentNullException(nameof(startLifecycleScheduler));
        this.startCallbackCommitObserver =
            startCallbackCommitObserver ?? throw new ArgumentNullException(nameof(startCallbackCommitObserver));
        this.addonCallbackScheduler =
            addonCallbackScheduler ?? throw new ArgumentNullException(nameof(addonCallbackScheduler));
        this.healthCallback = healthCallback;
    }

    public Task StartAsync(IEnumerable<ITelemetryAddon> addons, CancellationToken cancellationToken)
        => StartAsync(addons, addonConfigurations: null, cancellationToken);

    public async Task StartAsync(
        IEnumerable<ITelemetryAddon> addons,
        IReadOnlyDictionary<string, AddonConfiguration>? addonConfigurations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addons);

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (addonConfigurations is not null)
            {
                this.addonConfigurations = SnapshotAddonConfigurations(addonConfigurations);
            }

            StartAddons(addons, cancellationToken);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        AddonRuntime[] addons;
        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (syncRoot)
            {
                shutdownRequested = true;
                addons = knownAddons.ToArray();
            }

            RequestShutdownCancellationWithoutWaiting();
            await StopRuntimesAsync(addons, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(
        IEnumerable<ITelemetryAddon> addons,
        IReadOnlyDictionary<string, AddonConfiguration>? addonConfigurations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(addons);

        var updatedConfigurations = SnapshotAddonConfigurations(addonConfigurations);
        AddonRuntime[] previousAddons;

        await lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (syncRoot)
            {
                if (shutdownRequested)
                {
                    return;
                }

                previousAddons = knownAddons.ToArray();
            }

            await StopRuntimesAsync(previousAddons, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            lock (syncRoot)
            {
                if (shutdownRequested)
                {
                    return;
                }

                knownAddons.Clear();
                this.addonConfigurations = updatedConfigurations;
            }

            StartAddons(addons, cancellationToken);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private void StartAddons(IEnumerable<ITelemetryAddon> addons, CancellationToken cancellationToken)
    {
        foreach (var addon in addons)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runtime = new AddonRuntime(addon, addonConfigurations);

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
    }

    private async Task StopRuntimesAsync(IReadOnlyList<AddonRuntime> addons, CancellationToken cancellationToken)
    {
        var stopTasks = new List<Task>(addons.Count);
        foreach (var runtime in addons)
        {
            stopTasks.Add(StopOneAsync(runtime, cancellationToken));
        }

        await Task.WhenAll(stopTasks).ConfigureAwait(false);
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

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            shutdownCts.Token,
            runtime.ShutdownCts.Token);
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
        var metadataResult = await InvokeMetadataAsync(runtime, addon, cancellationToken).ConfigureAwait(false);
        if (!metadataResult.Entered)
        {
            return StartLifecycleResult.Skipped;
        }

        var metadata = metadataResult.Value!;
        SetMetadataSnapshot(runtime, metadata);
        var configuration = ResolveAddonConfiguration(runtime, metadata.Id);
        if (!configuration.Enabled)
        {
            PublishHealth(runtime, "disabled", "Add-on disabled.", TelemetryPriority.Routine);
            return StartLifecycleResult.Skipped;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ShouldCancelCommittedStartBeforeAddonCallbacks(runtime, cancellationToken))
        {
            return StartLifecycleResult.Skipped;
        }

        var validationResult = await InvokeValidationAsync(runtime, addon, configuration, cancellationToken)
            .ConfigureAwait(false);
        if (!validationResult.Entered)
        {
            return StartLifecycleResult.Skipped;
        }

        var validation = validationResult.Value!;
        cancellationToken.ThrowIfCancellationRequested();
        if (!validation.IsValid)
        {
            var message = validation.Errors.Count == 0
                ? "Add-on validation failed."
                : string.Join("; ", validation.Errors);
            return StartLifecycleResult.ValidationFailed(message);
        }

        var context = new AddonContext(sink, timeProvider, runtime.ShutdownCts.Token, configuration, healthCallback);
        var startEntered = await InvokeAddonStartAsync(runtime, context, cancellationToken).ConfigureAwait(false);
        return startEntered
            ? StartLifecycleResult.Started
            : StartLifecycleResult.Skipped;
    }

    private Task<AddonCallbackResult<AddonIdentity>> InvokeMetadataAsync(
        AddonRuntime runtime,
        ITelemetryAddon addon,
        CancellationToken cancellationToken)
        => InvokeAddonCallbackAsync(runtime, () => CaptureMetadata(addon), cancellationToken);

    private Task<AddonCallbackResult<AddonValidationResult>> InvokeValidationAsync(
        AddonRuntime runtime,
        ITelemetryAddon addon,
        AddonConfiguration configuration,
        CancellationToken cancellationToken)
        => InvokeAddonCallbackAsync(runtime, () => addon.Validate(configuration), cancellationToken);

    private async Task<bool> InvokeAddonStartAsync(
        AddonRuntime runtime,
        IAddonContext context,
        CancellationToken cancellationToken)
    {
        var startEntered = false;

        await addonCallbackScheduler(
            () =>
            {
                startEntered = TryBeginAddonCallback(runtime, cancellationToken, markStartInvoked: true);
                if (!startEntered)
                {
                    return Task.CompletedTask;
                }

                return runtime.Addon.StartAsync(context, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);

        return startEntered;
    }

    private async Task<AddonCallbackResult<T>> InvokeAddonCallbackAsync<T>(
        AddonRuntime runtime,
        Func<T> callback,
        CancellationToken cancellationToken)
    {
        var entered = false;
        T? result = default;

        await addonCallbackScheduler(
            () =>
            {
                entered = TryBeginAddonCallback(runtime, cancellationToken, markStartInvoked: false);
                if (!entered)
                {
                    return Task.CompletedTask;
                }

                result = callback();
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);

        return entered
            ? AddonCallbackResult<T>.CallbackEntered(result!)
            : AddonCallbackResult<T>.Skipped;
    }

    private bool TryBeginAddonCallback(
        AddonRuntime runtime,
        CancellationToken cancellationToken,
        bool markStartInvoked)
    {
        lock (syncRoot)
        {
            if (shutdownRequested ||
                runtime.State != AddonRuntimeState.Starting ||
                runtime.StartCancellationRequested ||
                cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            if (markStartInvoked)
            {
                if (runtime.StartInvoked)
                {
                    return false;
                }

                runtime.StartInvoked = true;
            }

            return true;
        }
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

    private async Task StopOneAsync(AddonRuntime runtime, CancellationToken cancellationToken)
    {
        RequestAddonCancellationWithoutWaiting(runtime);
        var stopTask = GetOrStartStopTask(runtime);
        if (stopTask is null)
        {
            return;
        }

        try
        {
            await stopTask.WaitAsync(stopTimeout, cancellationToken).ConfigureAwait(false);
            if (TryPublishStopResult(runtime))
            {
                PublishHealth(runtime, "stopped", "Add-on stopped.", TelemetryPriority.Routine);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ObserveLateTerminalStopFault(stopTask, runtime, "stop_error");
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

    private void RequestAddonCancellationWithoutWaiting(AddonRuntime runtime)
    {
        Task cancellationTask;

        try
        {
            cancellationTask = runtime.ShutdownCts.CancelAsync();
        }
        catch
        {
            return;
        }

        _ = ObserveCancellationWithoutThrowingAsync(cancellationTask);
    }

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

    private static AddonConfiguration ResolveAddonConfiguration(AddonRuntime runtime, string addonId)
        => runtime.AddonConfigurations.TryGetValue(addonId, out var configuration)
            ? configuration
            : AddonConfiguration.Default;

    private static IReadOnlyDictionary<string, AddonConfiguration> SnapshotAddonConfigurations(
        IReadOnlyDictionary<string, AddonConfiguration>? configurations)
    {
        if (configurations is null || configurations.Count == 0)
        {
            return new Dictionary<string, AddonConfiguration>();
        }

        return new Dictionary<string, AddonConfiguration>(configurations);
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

    private void ObserveLateTerminalStopFault(Task task, AddonRuntime runtime, string status)
    {
        if (task.IsFaulted)
        {
            PublishTerminalStopTaskFault(runtime, status, task);
            return;
        }

        if (task.IsCompleted)
        {
            return;
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                PublishTerminalStopTaskFault(runtime, status, completedTask);
            },
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void PublishTerminalStopTaskFault(AddonRuntime runtime, string status, Task task)
    {
        if (!TryPublishStopResult(runtime))
        {
            return;
        }

        var exception = task.Exception?.GetBaseException();
        PublishHealth(
            runtime,
            status,
            exception?.Message ?? "Add-on task faulted after timeout.",
            TelemetryPriority.Important);
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

            healthCallback?.Invoke(metadata.Id, status, message, priority);
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

    private sealed class AddonRuntime(
        ITelemetryAddon addon,
        IReadOnlyDictionary<string, AddonConfiguration> addonConfigurations)
    {
        public ITelemetryAddon Addon { get; } = addon;
        public IReadOnlyDictionary<string, AddonConfiguration> AddonConfigurations { get; } = addonConfigurations;
        public CancellationTokenSource ShutdownCts { get; } = new();
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

    private sealed record AddonCallbackResult<T>(bool Entered, T? Value)
    {
        public static AddonCallbackResult<T> Skipped { get; } = new(false, default);

        public static AddonCallbackResult<T> CallbackEntered(T value)
            => new(true, value);
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
