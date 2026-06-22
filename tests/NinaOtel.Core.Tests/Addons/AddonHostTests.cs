using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using NinaOtel.Abstractions.Addons;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Addons;
using Xunit;

namespace NinaOtel.Core.Tests.Addons;

public sealed class AddonHostTests
{
    private static readonly TimeSpan LifecycleTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TestObservationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task StartAsync_ReturnsWithoutWaitingForHangingAddon()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        var addon = new HangingStartAddon();

        await host.StartAsync([addon], CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));

        var record = await WaitForHealthRecordAsync(sink, "hanging-start", "start_timeout");

        record.Source.Should().Be("addon.hanging-start");
        record.Name.Should().Be("ninaotel.addon.health");
        record.Priority.Should().Be(TelemetryPriority.Important);
        record.Attributes["addon.name"].Should().Be("Hanging Start");
    }

    [Fact]
    public async Task StartAsync_WhenAddonBlocksBeforeReturningTask_ReportsStartTimeout()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        using var startCanReturn = new ManualResetEventSlim(false);
        var addon = new SynchronouslyBlockingStartAddon(startCanReturn);

        try
        {
            await host.StartAsync([addon], CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));

            var record = await WaitForHealthRecordAsync(sink, "sync-blocking-start", "start_timeout");

            record.Priority.Should().Be(TelemetryPriority.Important);
            addon.StartCalls.Should().Be(1);
        }
        finally
        {
            startCanReturn.Set();
        }
    }

    [Fact]
    public async Task StartAsync_WhenValidationBlocks_ReturnsAndReportsStartTimeoutWithoutStarting()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        using var validationCanReturn = new ManualResetEventSlim(false);
        var addon = new BlockingValidationAddon(validationCanReturn);

        var startTask = Task.Run(() => host.StartAsync([addon], CancellationToken.None));

        try
        {
            await startTask.WaitAsync(TimeSpan.FromMilliseconds(250));
            var record = await WaitForHealthRecordAsync(sink, "blocking-validation", "start_timeout");

            record.Priority.Should().Be(TelemetryPriority.Important);
            addon.StartCalls.Should().Be(0);
        }
        finally
        {
            validationCanReturn.Set();
            if (!startTask.IsCompleted)
            {
                await startTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }
    }

    [Fact]
    public async Task StartAsync_AfterStopAsyncDoesNotValidateOrStartAddon()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        using var validationCanReturn = new ManualResetEventSlim(false);
        var addon = new BlockingValidationAddon(validationCanReturn);

        await host.StopAsync(CancellationToken.None);
        var startTask = Task.Run(() => host.StartAsync([addon], CancellationToken.None));

        try
        {
            await startTask.WaitAsync(TimeSpan.FromMilliseconds(250));

            addon.ValidateCalls.Should().Be(0);
            addon.StartCalls.Should().Be(0);
        }
        finally
        {
            validationCanReturn.Set();
            if (!startTask.IsCompleted)
            {
                await startTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }
    }

    [Fact]
    public async Task StartAsync_WhenMetadataBlocks_ReturnsAndReportsStartTimeout()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        using var metadataCanReturn = new ManualResetEventSlim(false);
        var addon = new BlockingMetadataAddon(metadataCanReturn);

        var startTask = Task.Run(() => host.StartAsync([addon], CancellationToken.None));

        try
        {
            await startTask.WaitAsync(TimeSpan.FromMilliseconds(250));

            var record = await WaitForHealthRecordAsync(sink, "unknown", "start_timeout");
            record.Priority.Should().Be(TelemetryPriority.Important);
            addon.ValidateCalls.Should().Be(0);
            addon.StartCalls.Should().Be(0);
        }
        finally
        {
            metadataCanReturn.Set();
            if (!startTask.IsCompleted)
            {
                await startTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }
    }

    [Fact]
    public async Task StartAsync_PassesConfiguredAddonConfigurationToValidateAndContext()
    {
        var sink = new RecordingSink();
        var configuration = new AddonConfiguration(
            configVersion: 2,
            rawForwardingEnabled: true,
            settings: new Dictionary<string, string>
            {
                ["endpoint"] = "tcp://camera",
            });
        var host = new AddonHost(
            sink,
            TimeProvider.System,
            LifecycleTimeout,
            LifecycleTimeout,
            new Dictionary<string, AddonConfiguration>
            {
                ["configured"] = configuration,
            });
        var addon = new ConfigurationRecordingAddon();

        await host.StartAsync([addon], CancellationToken.None);
        await WaitForHealthRecordAsync(sink, "configured", "started");

        addon.ValidatedConfiguration.Should().NotBeNull();
        addon.ValidatedConfiguration!.ConfigVersion.Should().Be(2);
        addon.ValidatedConfiguration.RawForwardingEnabled.Should().BeTrue();
        addon.ValidatedConfiguration.Settings["endpoint"].Should().Be("tcp://camera");
        addon.StartConfiguration.Should().BeSameAs(addon.ValidatedConfiguration);
    }

    [Fact]
    public async Task StartAsync_WhenAddonConfigurationIsDisabled_ReportsDisabledWithoutValidatingOrStarting()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(
            sink,
            TimeProvider.System,
            LifecycleTimeout,
            LifecycleTimeout,
            new Dictionary<string, AddonConfiguration>
            {
                ["configured"] = new(enabled: false),
            });
        var addon = new ConfigurationRecordingAddon();

        await host.StartAsync([addon], CancellationToken.None);

        var record = await WaitForHealthRecordAsync(sink, "configured", "disabled");

        record.Priority.Should().Be(TelemetryPriority.Routine);
        addon.ValidateCalls.Should().Be(0);
        addon.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task StopAsync_ReportsStopTimeoutAndReturns()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        var addon = new HangingStopAddon();

        await host.StartAsync([addon], CancellationToken.None);
        await WaitForHealthRecordAsync(sink, "hanging-stop", "started");

        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));

        sink.Records.ContainHealth("hanging-stop", "stop_timeout");
    }

    [Fact]
    public async Task StopAsync_WhenMultipleAddonsHang_CompletesWithinOneStopTimeout()
    {
        var stopTimeout = TimeSpan.FromMilliseconds(100);
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, stopTimeout);
        var addons = new[]
        {
            new HangingStopAddon("hanging-stop-1", "Hanging Stop 1"),
            new HangingStopAddon("hanging-stop-2", "Hanging Stop 2"),
            new HangingStopAddon("hanging-stop-3", "Hanging Stop 3"),
        };

        await host.StartAsync(addons, CancellationToken.None);
        foreach (var addon in addons)
        {
            await WaitForHealthRecordAsync(sink, addon.Metadata.Id, "started");
        }

        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(220));

        foreach (var addon in addons)
        {
            sink.Records.ContainHealth(addon.Metadata.Id, "stop_timeout");
        }
    }

    [Fact]
    public async Task StopAsync_WhenAddonBlocksBeforeReturningTask_ReportsStopTimeoutAndReturns()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        using var stopCanReturn = new ManualResetEventSlim(false);
        var addon = new SynchronouslyBlockingStopAddon(stopCanReturn);

        await host.StartAsync([addon], CancellationToken.None);
        await WaitForHealthRecordAsync(sink, "sync-blocking-stop", "started");

        var stopTask = Task.Run(() => host.StopAsync(CancellationToken.None));

        try
        {
            await stopTask.WaitAsync(TimeSpan.FromMilliseconds(250));

            sink.Records.ContainHealth("sync-blocking-stop", "stop_timeout");
            addon.StopCalls.Should().Be(1);
        }
        finally
        {
            stopCanReturn.Set();
            if (!stopTask.IsCompleted)
            {
                await stopTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }
    }

    [Fact]
    public async Task StopAsync_DoesNotWaitForBlockingShutdownTokenCallback()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        using var callbackCanReturn = new ManualResetEventSlim(false);
        var addon = new BlockingShutdownCallbackAddon(callbackCanReturn);

        await host.StartAsync([addon], CancellationToken.None);
        await WaitForHealthRecordAsync(sink, "blocking-shutdown-callback", "started");

        var stopTask = host.StopAsync(CancellationToken.None);

        try
        {
            await stopTask.WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            callbackCanReturn.Set();
            await stopTask.WaitAsync(TimeSpan.FromSeconds(1));
        }

        sink.Records.ContainHealth("blocking-shutdown-callback", "stopped");
    }

    [Fact]
    public async Task StopAsync_SkipsQueuedStartWhenShutdownWasRequestedFirst()
    {
        var sink = new RecordingSink();
        Func<Task>? queuedStart = null;
        var host = CreateHostWithStartScheduler(sink, startWork =>
        {
            queuedStart = startWork;
            return Task.CompletedTask;
        });
        var addon = new RecordingLifecycleAddon();

        await host.StartAsync([addon], CancellationToken.None);

        queuedStart.Should().NotBeNull();
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));
        await queuedStart!().WaitAsync(TimeSpan.FromMilliseconds(250));

        addon.StartCalls.Should().Be(0);
        addon.StopCalls.Should().Be(0);
        sink.Records.Any(record =>
            record.Source == "addon.queued-start" &&
            record.Attributes.TryGetValue("status", out var status) &&
            (Equals(status, "started") || Equals(status, "stopped"))).Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_WhenInnerLifecycleWorkRunsAfterShutdownDoesNotTouchAddonCallbacks()
    {
        var sink = new RecordingSink();
        Task? scheduledStart = null;
        var innerLifecycleQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInnerLifecycle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerLifecycleCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = CreateHostWithStartSchedulers(
            sink,
            startWork =>
            {
                scheduledStart = Task.Run(startWork);
                return scheduledStart;
            },
            async (lifecycleWork, cancellationToken) =>
            {
                innerLifecycleQueued.SetResult();
                await releaseInnerLifecycle.Task.WaitAsync(TimeSpan.FromSeconds(1));

                try
                {
                    await lifecycleWork();
                    innerLifecycleCompleted.SetResult();
                }
                catch (Exception ex)
                {
                    innerLifecycleCompleted.SetException(ex);
                    throw;
                }
            });
        var addon = new CallbackCountingAddon();

        await host.StartAsync([addon], CancellationToken.None);
        await innerLifecycleQueued.Task.WaitAsync(TimeSpan.FromMilliseconds(250));

        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));
        releaseInnerLifecycle.SetResult();
        await innerLifecycleCompleted.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
        if (scheduledStart is not null)
        {
            await scheduledStart.WaitAsync(TimeSpan.FromMilliseconds(250));
        }

        addon.MetadataCalls.Should().Be(0);
        addon.ValidateCalls.Should().Be(0);
        addon.StartCalls.Should().Be(0);
    }

    [Fact]
    public async Task StopAsync_WhenShutdownAfterCallbackCommit_PreventsLaterStart()
    {
        var sink = new RecordingSink();
        Task? scheduledStart = null;
        var callbacksCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackScheduleCount = 0;
        var host = CreateHostWithStartSchedulers(
            sink,
            startWork =>
            {
                scheduledStart = Task.Run(startWork);
                return scheduledStart;
            },
            static (lifecycleWork, cancellationToken) => Task.Run(lifecycleWork, cancellationToken),
            async cancellationToken =>
            {
                callbacksCommitted.SetResult();
                await Task.CompletedTask;
            },
            async (callbackWork, cancellationToken) =>
            {
                if (Interlocked.Increment(ref callbackScheduleCount) == 1)
                {
                    callbackQueued.SetResult();
                    await releaseCallback.Task.WaitAsync(TimeSpan.FromSeconds(1));
                }

                await callbackWork();
            });
        var addon = new CallbackCountingAddon();

        await host.StartAsync([addon], CancellationToken.None);
        await callbacksCommitted.Task.WaitAsync(TimeSpan.FromMilliseconds(250));
        await callbackQueued.Task.WaitAsync(TimeSpan.FromMilliseconds(250));

        var stopDuringCommittedStart = host.StopAsync(CancellationToken.None);

        await stopDuringCommittedStart.WaitAsync(TimeSpan.FromMilliseconds(250));
        releaseCallback.SetResult();
        if (scheduledStart is not null)
        {
            await scheduledStart.WaitAsync(TimeSpan.FromMilliseconds(250));
        }

        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));

        addon.MetadataCalls.Should().Be(0);
        addon.ValidateCalls.Should().Be(0);
        addon.StartCalls.Should().Be(0);
        addon.StopCalls.Should().Be(0);
        sink.Records.Any(record =>
            record.Source == "addon.callback-counting" &&
            record.Attributes.TryGetValue("status", out var status) &&
            Equals(status, "started")).Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_WhenShutdownAfterStartScheduledButBeforeStartEntered_DoesNotStopAddon()
    {
        var sink = new RecordingSink();
        Task? scheduledStart = null;
        var startCallbackQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStartCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackScheduleCount = 0;
        var host = CreateHostWithStartSchedulers(
            sink,
            startWork =>
            {
                scheduledStart = Task.Run(startWork);
                return scheduledStart;
            },
            static (lifecycleWork, cancellationToken) => Task.Run(lifecycleWork, cancellationToken),
            static _ => Task.CompletedTask,
            async (callbackWork, cancellationToken) =>
            {
                if (Interlocked.Increment(ref callbackScheduleCount) == 3)
                {
                    startCallbackQueued.SetResult();
                    await releaseStartCallback.Task.WaitAsync(TimeSpan.FromSeconds(1));
                }

                cancellationToken.ThrowIfCancellationRequested();
                await callbackWork();
            });
        var addon = new CallbackCountingAddon();

        await host.StartAsync([addon], CancellationToken.None);
        await startCallbackQueued.Task.WaitAsync(TimeSpan.FromMilliseconds(250));

        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));
        releaseStartCallback.SetResult();
        if (scheduledStart is not null)
        {
            await scheduledStart.WaitAsync(TimeSpan.FromMilliseconds(250));
        }

        addon.MetadataCalls.Should().Be(1);
        addon.ValidateCalls.Should().Be(1);
        addon.StartCalls.Should().Be(0);
        addon.StopCalls.Should().Be(0);
        sink.Records.Any(record =>
            record.Source == "addon.callback-counting" &&
            record.Attributes.TryGetValue("status", out var status) &&
            Equals(status, "stopped")).Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_AfterStopAsyncDoesNotInvokeAddonStart()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        var addon = new RecordingLifecycleAddon();

        await host.StopAsync(CancellationToken.None);
        await host.StartAsync([addon], CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        addon.StartCalls.Should().Be(0);
        addon.StopCalls.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_InvalidAddonReportsValidationFailureAndDoesNotStart()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        var addon = new InvalidAddon();

        await host.StartAsync([addon], CancellationToken.None);

        var record = await WaitForHealthRecordAsync(sink, "invalid", "validation_failed");

        addon.StartCalls.Should().Be(0);
        record.Attributes["message"].Should().Be("Missing endpoint; Disabled");
        record.Attributes["addon.name"].Should().Be("Invalid Addon");
    }

    [Fact]
    public async Task StartAsync_StartExceptionIsIsolatedAndReported()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        var addon = new ThrowingStartAddon();

        await host.StartAsync([addon], CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));

        var record = await WaitForHealthRecordAsync(sink, "throwing-start", "start_error");
        record.Attributes["message"].Should().Be("start failed");
    }

    [Fact]
    public async Task StopAsync_StopExceptionIsIsolatedAndReported()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        var addon = new ThrowingStopAddon();

        await host.StartAsync([addon], CancellationToken.None);
        await WaitForHealthRecordAsync(sink, "throwing-stop", "started");

        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));

        var record = sink.Records.SingleHealth("throwing-stop", "stop_error");
        record.Attributes["message"].Should().Be("stop failed");
    }

    [Fact]
    public async Task StartAsync_TimedOutStartTaskLaterFaults_ReportsLateStartError()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        var addon = new LateFaultingStartAddon();

        await host.StartAsync([addon], CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));
        await WaitForHealthRecordAsync(sink, "late-start-fault", "start_timeout");

        addon.FaultStart(new InvalidOperationException("late start failed"));

        var record = await WaitForHealthRecordAsync(sink, "late-start-fault", "start_error");
        record.Attributes["message"].Should().Be("late start failed");
    }

    [Fact]
    public async Task StopAsync_TimedOutStopTaskLaterFaults_ReportsLateStopError()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        var addon = new LateFaultingStopAddon();

        await host.StartAsync([addon], CancellationToken.None);
        await WaitForHealthRecordAsync(sink, "late-stop-fault", "started");
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));
        sink.Records.ContainHealth("late-stop-fault", "stop_timeout");

        addon.FaultStop(new InvalidOperationException("late stop failed"));

        var record = await WaitForHealthRecordAsync(sink, "late-stop-fault", "stop_error");
        record.Attributes["message"].Should().Be("late stop failed");
    }

    [Fact]
    public async Task StopAsync_WhenCallerCancellationRequested_ThrowsCancellation()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var addon = new HangingStopAddon();
        using var callerCts = new CancellationTokenSource();

        await host.StartAsync([addon], CancellationToken.None);
        await WaitForHealthRecordAsync(sink, "hanging-stop", "started");
        await callerCts.CancelAsync();

        var stop = () => host.StopAsync(callerCts.Token);

        await stop.Should().ThrowAsync<OperationCanceledException>();
        sink.Records.Any(record =>
            record.Source == "addon.hanging-stop" &&
            record.Attributes.TryGetValue("status", out var status) &&
            Equals(status, "stop_timeout")).Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_AfterCallerCancellationRetriesExistingStopInvocation()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var addon = new ControllableStopAddon();
        using var callerCts = new CancellationTokenSource();

        await host.StartAsync([addon], CancellationToken.None);
        await WaitForHealthRecordAsync(sink, "controllable-stop", "started");

        var firstStop = host.StopAsync(callerCts.Token);
        await addon.WaitForStopInvocationAsync();
        await callerCts.CancelAsync();
        await firstStop.Invoking(stop => stop.WaitAsync(TimeSpan.FromMilliseconds(250)))
            .Should()
            .ThrowAsync<OperationCanceledException>();

        Task? secondStop = null;
        try
        {
            secondStop = host.StopAsync(CancellationToken.None);

            await Task.Delay(TimeSpan.FromMilliseconds(100));
            secondStop.IsCompleted.Should().BeFalse("the retry should wait for the in-flight stop instead of skipping it");

            addon.CompleteStop();
            await secondStop.WaitAsync(TimeSpan.FromMilliseconds(250));
        }
        finally
        {
            addon.CompleteStop();
            if (secondStop is not null && !secondStop.IsCompleted)
            {
                await secondStop.WaitAsync(TimeSpan.FromSeconds(1));
            }
        }

        addon.StopCalls.Should().Be(1);
        sink.Records.ContainHealth("controllable-stop", "stopped");
    }

    [Fact]
    public async Task StopAsync_WhenCallerCanceledStopLaterFaults_RetryPublishesOneStopError()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var addon = new FaultableStopAddon();
        using var callerCts = new CancellationTokenSource();

        await host.StartAsync([addon], CancellationToken.None);
        await WaitForHealthRecordAsync(sink, "faultable-stop", "started");

        var firstStop = host.StopAsync(callerCts.Token);
        await addon.WaitForStopInvocationAsync();
        await callerCts.CancelAsync();
        await firstStop.Invoking(stop => stop.WaitAsync(TimeSpan.FromMilliseconds(250)))
            .Should()
            .ThrowAsync<OperationCanceledException>();

        addon.FaultStop(new InvalidOperationException("stop exploded"));
        await WaitForHealthRecordAsync(sink, "faultable-stop", "stop_error");

        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));

        sink.Records.CountHealth("faultable-stop", "stop_error").Should().Be(1);
    }

    [Fact]
    public void AddonContext_ReportHealthPublishesExpectedRecord()
    {
        var sink = new RecordingSink();
        var timestamp = DateTimeOffset.Parse("2026-06-14T01:02:03Z");
        var context = new AddonContext(sink, new FixedTimeProvider(timestamp), CancellationToken.None);

        context.ReportHealth("camera", "degraded", "Camera temperature unavailable.", TelemetryPriority.Normal);

        var record = sink.Records.Should().ContainSingle().Subject;
        record.Signal.Should().Be(TelemetrySignal.Health);
        record.Timestamp.Should().Be(timestamp);
        record.Source.Should().Be("addon.camera");
        record.Name.Should().Be("ninaotel.addon.health");
        record.Priority.Should().Be(TelemetryPriority.Normal);
        record.Attributes["addon.id"].Should().Be("camera");
        record.Attributes["status"].Should().Be("degraded");
        record.Attributes["message"].Should().Be("Camera temperature unavailable.");
    }

    [Fact]
    public async Task AddonHost_WithHealthCallbackPublishesTelemetryAndInvokesCallbackForAddonHealth()
    {
        var sink = new RecordingSink();
        var healthCallbacks = new ConcurrentQueue<AddonHealthCallback>();
        var host = new AddonHost(
            sink,
            TimeProvider.System,
            LifecycleTimeout,
            LifecycleTimeout,
            addonConfigurations: null,
            healthCallback: (addonId, status, message, priority) =>
                healthCallbacks.Enqueue(new(addonId, status, message, priority)));
        var addon = new ContextHealthAddon();

        await host.StartAsync([addon], CancellationToken.None);

        var record = await WaitForHealthRecordAsync(sink, "context-health", "waiting");
        await WaitUntilAsync(() => healthCallbacks.Any(callback =>
            callback.AddonId == "context-health" &&
            callback.Status == "waiting"));

        record.Priority.Should().Be(TelemetryPriority.Routine);
        healthCallbacks.Should().Contain(callback =>
            callback.AddonId == "context-health" &&
            callback.Status == "waiting" &&
            callback.Message == "Add-on shell loaded; source collection is not implemented yet." &&
            callback.Priority == TelemetryPriority.Routine);
    }

    [Fact]
    public async Task Lifecycle_DoesNotBlockWhenAddonStartAndStopHang()
    {
        var sink = new RecordingSink();
        var host = new AddonHost(sink, TimeProvider.System, LifecycleTimeout, LifecycleTimeout);
        var addon = new IgnoringCancellationAddon();

        await host.StartAsync([addon], CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));
        await WaitUntilAsync(() => addon.StartCalls > 0);
        await host.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(250));

        await WaitForHealthRecordAsync(sink, "ignores-cancellation", "start_timeout");
        sink.Records.ContainHealth("ignores-cancellation", "stop_timeout");
    }

    private static async Task<TelemetryRecord> WaitForHealthRecordAsync(
        RecordingSink sink,
        string addonId,
        string status)
    {
        var stopAt = DateTimeOffset.UtcNow + TestObservationTimeout;

        while (DateTimeOffset.UtcNow < stopAt)
        {
            var record = sink.Records.FirstOrDefault(IsMatchingHealthRecord(addonId, status));
            if (record is not null)
            {
                return record;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Health record '{status}' for add-on '{addonId}' was not published.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var stopAt = DateTimeOffset.UtcNow + TestObservationTimeout;

        while (DateTimeOffset.UtcNow < stopAt)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("Condition was not met before timeout.");
    }

    private static Func<TelemetryRecord, bool> IsMatchingHealthRecord(string addonId, string status)
        => record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == $"addon.{addonId}" &&
            record.Attributes.TryGetValue("addon.id", out var recordAddonId) &&
            Equals(recordAddonId, addonId) &&
            record.Attributes.TryGetValue("status", out var recordStatus) &&
            Equals(recordStatus, status);

    private static AddonHost CreateHostWithStartScheduler(
        RecordingSink sink,
        Func<Func<Task>, Task> startWorkScheduler)
    {
        var constructor = typeof(AddonHost).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(ITelemetrySink),
                typeof(TimeProvider),
                typeof(TimeSpan),
                typeof(TimeSpan),
                typeof(Func<Func<Task>, Task>),
            ],
            modifiers: null);

        constructor.Should().NotBeNull("queued start work needs deterministic scheduling in this regression test");

        return (AddonHost)constructor!.Invoke(
        [
            sink,
            TimeProvider.System,
            LifecycleTimeout,
            LifecycleTimeout,
            startWorkScheduler,
        ]);
    }

    private static AddonHost CreateHostWithStartSchedulers(
        RecordingSink sink,
        Func<Func<Task>, Task> startWorkScheduler,
        Func<Func<Task>, CancellationToken, Task> startLifecycleScheduler)
        => CreateHostWithStartSchedulers(
            sink,
            startWorkScheduler,
            startLifecycleScheduler,
            static _ => Task.CompletedTask);

    private static AddonHost CreateHostWithStartSchedulers(
        RecordingSink sink,
        Func<Func<Task>, Task> startWorkScheduler,
        Func<Func<Task>, CancellationToken, Task> startLifecycleScheduler,
        Func<CancellationToken, Task> startCallbackCommitObserver)
        => CreateHostWithStartSchedulers(
            sink,
            startWorkScheduler,
            startLifecycleScheduler,
            startCallbackCommitObserver,
            static (callbackWork, cancellationToken) => Task.Run(callbackWork, cancellationToken));

    private static AddonHost CreateHostWithStartSchedulers(
        RecordingSink sink,
        Func<Func<Task>, Task> startWorkScheduler,
        Func<Func<Task>, CancellationToken, Task> startLifecycleScheduler,
        Func<CancellationToken, Task> startCallbackCommitObserver,
        Func<Func<Task>, CancellationToken, Task> addonCallbackScheduler)
    {
        var constructor = typeof(AddonHost).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(ITelemetrySink),
                typeof(TimeProvider),
                typeof(TimeSpan),
                typeof(TimeSpan),
                typeof(Func<Func<Task>, Task>),
                typeof(Func<Func<Task>, CancellationToken, Task>),
                typeof(Func<CancellationToken, Task>),
                typeof(Func<Func<Task>, CancellationToken, Task>),
            ],
            modifiers: null);

        constructor.Should().NotBeNull("inner lifecycle work needs deterministic scheduling in this regression test");

        return (AddonHost)constructor!.Invoke(
        [
            sink,
            TimeProvider.System,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            startWorkScheduler,
            startLifecycleScheduler,
            startCallbackCommitObserver,
            addonCallbackScheduler,
        ]);
    }

    private sealed class RecordingSink : ITelemetrySink
    {
        private readonly object syncRoot = new();
        private readonly List<TelemetryRecord> records = [];

        public IReadOnlyList<TelemetryRecord> Records
        {
            get
            {
                lock (syncRoot)
                {
                    return records.ToArray();
                }
            }
        }

        public bool TryPublish(TelemetryRecord record)
        {
            lock (syncRoot)
            {
                records.Add(record);
            }

            return true;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private abstract class TestAddon(string id, string displayName) : ITelemetryAddon
    {
        public AddonMetadata Metadata { get; } = new(id, displayName, new Version(1, 0, 0), "test");

        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public virtual AddonValidationResult Validate(AddonConfiguration configuration) => AddonValidationResult.Success;

        public async Task StartAsync(IAddonContext context, CancellationToken cancellationToken)
        {
            StartCalls++;
            await StartCoreAsync(context, cancellationToken);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            await StopCoreAsync(cancellationToken);
        }

        protected virtual Task StartCoreAsync(IAddonContext context, CancellationToken cancellationToken)
            => Task.CompletedTask;

        protected virtual Task StopCoreAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class HangingStartAddon() : TestAddon("hanging-start", "Hanging Start")
    {
        protected override Task StartCoreAsync(IAddonContext context, CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class HangingStopAddon(string id = "hanging-stop", string displayName = "Hanging Stop")
        : TestAddon(id, displayName)
    {
        protected override Task StopCoreAsync(CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private sealed class ConfigurationRecordingAddon() : TestAddon("configured", "Configured")
    {
        public AddonConfiguration? ValidatedConfiguration { get; private set; }
        public AddonConfiguration? StartConfiguration { get; private set; }
        public int ValidateCalls { get; private set; }

        public override AddonValidationResult Validate(AddonConfiguration configuration)
        {
            ValidateCalls++;
            ValidatedConfiguration = configuration;
            return AddonValidationResult.Success;
        }

        protected override Task StartCoreAsync(IAddonContext context, CancellationToken cancellationToken)
        {
            StartConfiguration = context.Configuration;
            return Task.CompletedTask;
        }
    }

    private sealed class ContextHealthAddon() : TestAddon("context-health", "Context Health")
    {
        protected override Task StartCoreAsync(IAddonContext context, CancellationToken cancellationToken)
        {
            context.ReportHealth(
                Metadata.Id,
                "waiting",
                "Add-on shell loaded; source collection is not implemented yet.",
                TelemetryPriority.Routine);
            return Task.CompletedTask;
        }
    }

    private sealed record AddonHealthCallback(
        string AddonId,
        string Status,
        string Message,
        TelemetryPriority Priority);

    private sealed class RecordingLifecycleAddon() : TestAddon("queued-start", "Queued Start");

    private sealed class CallbackCountingAddon : ITelemetryAddon
    {
        public int MetadataCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }

        public AddonMetadata Metadata
        {
            get
            {
                MetadataCalls++;
                return new AddonMetadata("callback-counting", "Callback Counting", new Version(1, 0, 0), "test");
            }
        }

        public AddonValidationResult Validate(AddonConfiguration configuration)
        {
            ValidateCalls++;
            return AddonValidationResult.Success;
        }

        public Task StartAsync(IAddonContext context, CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingValidationAddon(ManualResetEventSlim validationCanReturn)
        : TestAddon("blocking-validation", "Blocking Validation")
    {
        public int ValidateCalls { get; private set; }

        public override AddonValidationResult Validate(AddonConfiguration configuration)
        {
            ValidateCalls++;
            validationCanReturn.Wait();
            return AddonValidationResult.Success;
        }
    }

    private sealed class BlockingMetadataAddon(ManualResetEventSlim metadataCanReturn) : ITelemetryAddon
    {
        public int MetadataCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public int StartCalls { get; private set; }

        public AddonMetadata Metadata
        {
            get
            {
                MetadataCalls++;
                metadataCanReturn.Wait();
                return new AddonMetadata("blocking-metadata", "Blocking Metadata", new Version(1, 0, 0), "test");
            }
        }

        public AddonValidationResult Validate(AddonConfiguration configuration)
        {
            ValidateCalls++;
            return AddonValidationResult.Success;
        }

        public Task StartAsync(IAddonContext context, CancellationToken cancellationToken)
        {
            StartCalls++;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SynchronouslyBlockingStartAddon(ManualResetEventSlim startCanReturn) : ITelemetryAddon
    {
        public AddonMetadata Metadata { get; } =
            new("sync-blocking-start", "Synchronous Blocking Start", new Version(1, 0, 0), "test");

        public int StartCalls { get; private set; }

        public AddonValidationResult Validate(AddonConfiguration configuration) => AddonValidationResult.Success;

        public Task StartAsync(IAddonContext context, CancellationToken cancellationToken)
        {
            StartCalls++;
            startCanReturn.Wait();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SynchronouslyBlockingStopAddon(ManualResetEventSlim stopCanReturn)
        : TestAddon("sync-blocking-stop", "Synchronous Blocking Stop")
    {
        protected override Task StopCoreAsync(CancellationToken cancellationToken)
        {
            stopCanReturn.Wait();
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingShutdownCallbackAddon(ManualResetEventSlim callbackCanReturn)
        : TestAddon("blocking-shutdown-callback", "Blocking Shutdown Callback")
    {
        protected override Task StartCoreAsync(IAddonContext context, CancellationToken cancellationToken)
        {
            context.ShutdownToken.Register(static state =>
            {
                var block = (ManualResetEventSlim)state!;
                block.Wait();
            }, callbackCanReturn);

            return Task.CompletedTask;
        }
    }

    private sealed class InvalidAddon() : TestAddon("invalid", "Invalid Addon")
    {
        public override AddonValidationResult Validate(AddonConfiguration configuration)
            => AddonValidationResult.Failure("Missing endpoint", "Disabled");
    }

    private sealed class ThrowingStartAddon() : TestAddon("throwing-start", "Throwing Start")
    {
        protected override Task StartCoreAsync(IAddonContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("start failed");
    }

    private sealed class ThrowingStopAddon() : TestAddon("throwing-stop", "Throwing Stop")
    {
        protected override Task StopCoreAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("stop failed");
    }

    private sealed class LateFaultingStartAddon() : TestAddon("late-start-fault", "Late Start Fault")
    {
        private readonly TaskCompletionSource startCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void FaultStart(Exception exception)
            => startCompletion.SetException(exception);

        protected override Task StartCoreAsync(IAddonContext context, CancellationToken cancellationToken)
            => startCompletion.Task;
    }

    private sealed class LateFaultingStopAddon() : TestAddon("late-stop-fault", "Late Stop Fault")
    {
        private readonly TaskCompletionSource stopCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void FaultStop(Exception exception)
            => stopCompletion.SetException(exception);

        protected override Task StopCoreAsync(CancellationToken cancellationToken)
            => stopCompletion.Task;
    }

    private sealed class IgnoringCancellationAddon() : TestAddon("ignores-cancellation", "Ignores Cancellation")
    {
        protected override Task StartCoreAsync(IAddonContext context, CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);

        protected override Task StopCoreAsync(CancellationToken cancellationToken)
            => Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
    }

    private sealed class ControllableStopAddon() : TestAddon("controllable-stop", "Controllable Stop")
    {
        private readonly TaskCompletionSource stopInvoked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource stopCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForStopInvocationAsync() => stopInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));

        public void CompleteStop() => stopCompletion.TrySetResult();

        protected override Task StopCoreAsync(CancellationToken cancellationToken)
        {
            stopInvoked.TrySetResult();
            return stopCompletion.Task;
        }
    }

    private sealed class FaultableStopAddon() : TestAddon("faultable-stop", "Faultable Stop")
    {
        private readonly TaskCompletionSource stopInvoked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource stopCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForStopInvocationAsync() => stopInvoked.Task.WaitAsync(TimeSpan.FromSeconds(1));

        public void FaultStop(Exception exception) => stopCompletion.TrySetException(exception);

        protected override Task StopCoreAsync(CancellationToken cancellationToken)
        {
            stopInvoked.TrySetResult();
            return stopCompletion.Task;
        }
    }
}

internal static class TelemetryRecordAssertions
{
    public static void ContainHealth(this IEnumerable<TelemetryRecord> records, string addonId, string status)
    {
        records.Any(IsMatchingHealthRecord(addonId, status)).Should().BeTrue(
            "a health record with status {0} should be published for add-on {1}",
            status,
            addonId);
    }

    public static TelemetryRecord SingleHealth(
        this IEnumerable<TelemetryRecord> records,
        string addonId,
        string status)
    {
        var matchingRecords = records.Where(record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == $"addon.{addonId}" &&
            record.Attributes.TryGetValue("addon.id", out var recordAddonId) &&
            Equals(recordAddonId, addonId) &&
            record.Attributes.TryGetValue("status", out var recordStatus) &&
            Equals(recordStatus, status)).ToArray();

        matchingRecords.Should().ContainSingle();
        return matchingRecords[0];
    }

    public static int CountHealth(this IEnumerable<TelemetryRecord> records, string addonId, string status)
        => records.Count(IsMatchingHealthRecord(addonId, status));

    private static Func<TelemetryRecord, bool> IsMatchingHealthRecord(string addonId, string status)
        => record =>
            record.Signal == TelemetrySignal.Health &&
            record.Source == $"addon.{addonId}" &&
            record.Attributes.TryGetValue("addon.id", out var recordAddonId) &&
            Equals(recordAddonId, addonId) &&
            record.Attributes.TryGetValue("status", out var recordStatus) &&
            Equals(recordStatus, status);
}
