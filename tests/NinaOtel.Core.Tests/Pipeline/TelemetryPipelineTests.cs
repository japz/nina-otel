using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Pipeline;
using System.Reflection;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class TelemetryPipelineTests
{
    [Fact]
    public void Constructor_RejectsNullExporter()
    {
        Action construct = () => new TelemetryPipeline(null!, capacity: 10);

        construct.Should().Throw<ArgumentNullException>()
            .WithParameterName("exporter");
    }

    [Fact]
    public async Task TryPublish_DoesNotBlockWhenQueueIsFull()
    {
        var exporter = new RecordingExporter();
        await using var pipeline = new TelemetryPipeline(exporter, capacity: 1);
        var first = TelemetryRecord.Log(DateTimeOffset.UtcNow, "test", TelemetrySeverity.Information, "first", TelemetryPriority.Routine);
        var second = TelemetryRecord.Log(DateTimeOffset.UtcNow, "test", TelemetrySeverity.Information, "second", TelemetryPriority.Routine);

        pipeline.TryPublish(first).Should().BeTrue();
        pipeline.TryPublish(second).Should().BeFalse();
        pipeline.DroppedRecords.Should().Be(1);
    }

    [Fact]
    public async Task StartAsync_ExportsQueuedRecords()
    {
        var exporter = new RecordingExporter();
        await using var pipeline = new TelemetryPipeline(exporter, capacity: 10);

        await pipeline.StartAsync(CancellationToken.None);
        pipeline.TryPublish(TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "ready", TelemetryPriority.Important)).Should().BeTrue();

        await exporter.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        exporter.Records.Should().ContainSingle(r => r.Name == "ready");
    }

    [Fact]
    public async Task TryPublish_AfterDispose_ReturnsFalseAndCountsDrop()
    {
        var exporter = new RecordingExporter();
        var pipeline = new TelemetryPipeline(exporter, capacity: 10);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "late", TelemetryPriority.Routine);

        await pipeline.DisposeAsync();

        pipeline.TryPublish(record).Should().BeFalse();
        pipeline.DroppedRecords.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_CountsQueuedRecordsWhenDisposedBeforeStart()
    {
        var exporter = new RecordingExporter();
        var pipeline = new TelemetryPipeline(exporter, capacity: 10);
        var first = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "first", TelemetryPriority.Routine);
        var second = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "second", TelemetryPriority.Routine);

        pipeline.TryPublish(first).Should().BeTrue();
        pipeline.TryPublish(second).Should().BeTrue();

        await pipeline.DisposeAsync();

        pipeline.DroppedRecords.Should().Be(2);
    }

    [Fact]
    public async Task ExporterFailure_DoesNotStopWorkerAndCountsDroppedBatch()
    {
        var exporter = new FailingOnceExporter();
        var pipeline = new TelemetryPipeline(exporter, capacity: 10);
        var first = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "first", TelemetryPriority.Important);
        var second = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "second", TelemetryPriority.Routine);

        await pipeline.StartAsync(CancellationToken.None);
        pipeline.TryPublish(first).Should().BeTrue();
        await exporter.WaitForAttemptAsync(TimeSpan.FromSeconds(2));

        pipeline.TryPublish(second).Should().BeTrue();

        await exporter.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        pipeline.DroppedRecords.Should().BeGreaterThanOrEqualTo(1);
        exporter.Records.Should().ContainSingle(r => r.Name == "second");

        Func<Task> dispose = async () => await pipeline.DisposeAsync();
        await dispose.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DisposeAsync_DrainsQueuedRecordsBeforeReturning()
    {
        var exporter = new BlockingFirstExportExporter();
        var pipeline = new TelemetryPipeline(exporter, capacity: 10);
        var first = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "first", TelemetryPriority.Routine);
        var second = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "second", TelemetryPriority.Routine);
        var third = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "third", TelemetryPriority.Routine);

        await pipeline.StartAsync(CancellationToken.None);
        pipeline.TryPublish(first).Should().BeTrue();
        await exporter.WaitForFirstExportStartedAsync(TimeSpan.FromSeconds(2));

        pipeline.TryPublish(second).Should().BeTrue();
        pipeline.TryPublish(third).Should().BeTrue();

        var disposeTask = pipeline.DisposeAsync().AsTask();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        exporter.ReleaseFirstExport();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(2));

        exporter.Records.Should().Contain(r => r.Name == "first");
        exporter.Records.Should().Contain(r => r.Name == "second");
        exporter.Records.Should().Contain(r => r.Name == "third");
    }

    [Fact]
    public async Task DisposeAsync_ConcurrentCallersAwaitSameDrainWork()
    {
        var exporter = new BlockingFirstExportExporter();
        var pipeline = new TelemetryPipeline(exporter, capacity: 10);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "shared-dispose", TelemetryPriority.Routine);

        await pipeline.StartAsync(CancellationToken.None);
        pipeline.TryPublish(record).Should().BeTrue();
        await exporter.WaitForFirstExportStartedAsync(TimeSpan.FromSeconds(2));

        var firstDispose = pipeline.DisposeAsync().AsTask();
        var secondDispose = pipeline.DisposeAsync().AsTask();

        try
        {
            secondDispose.IsCompleted.Should().BeFalse("concurrent disposers should await the active drain");

            exporter.ReleaseFirstExport();
            await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            exporter.ReleaseFirstExport();
        }

        exporter.Records.Should().ContainSingle(r => r.Name == "shared-dispose");
        pipeline.DroppedRecords.Should().Be(0);
    }

    [Fact]
    public async Task DisposeAsync_ReturnsAfterDrainTimeoutEvenWhenCancellationCallbackBlocks()
    {
        var exporter = new BlockingCancellationCallbackExporter();
        var pipeline = new TelemetryPipeline(exporter, capacity: 10);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "blocked", TelemetryPriority.Routine);

        try
        {
            await pipeline.StartAsync(CancellationToken.None);
            pipeline.TryPublish(record).Should().BeTrue();
            await exporter.WaitForExportStartedAsync(TimeSpan.FromSeconds(2));

            await pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            exporter.UnblockCancellationCallback();
        }
    }

    [Fact]
    public async Task DisposeAsync_RequestsCancellationBeforeReturningAfterDrainTimeout()
    {
        var exporter = new CancellationObservingExporter();
        var pipeline = new TelemetryPipeline(exporter, capacity: 10);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "blocked", TelemetryPriority.Routine);

        try
        {
            await pipeline.StartAsync(CancellationToken.None);
            pipeline.TryPublish(record).Should().BeTrue();
            await exporter.WaitForExportStartedAsync(TimeSpan.FromSeconds(2));

            await pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

            exporter.HasCancellationBeenRequested.Should().BeTrue();
        }
        finally
        {
            exporter.ReleaseExport();
        }
    }

    [Fact]
    public async Task DisposeAsync_DisposesCancellationSourceWhenTimeoutAbandonedWorkerEventuallyExits()
    {
        var exporter = new ReleaseAfterCancellationExporter();
        var pipeline = new TelemetryPipeline(exporter, capacity: 10);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "eventual-exit", TelemetryPriority.Routine);
        var stopCts = GetStopCts(pipeline);

        await pipeline.StartAsync(CancellationToken.None);
        pipeline.TryPublish(record).Should().BeTrue();
        await exporter.WaitForExportStartedAsync(TimeSpan.FromSeconds(2));

        await pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        Action readBeforeExit = () => _ = stopCts.Token;
        readBeforeExit.Should().NotThrow<ObjectDisposedException>("the worker still owns the token after timeout");

        exporter.ReleaseExport();

        await WaitUntilAsync(() =>
        {
            try
            {
                _ = stopCts.Token;
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DisposeAsync_CountsInFlightRecordsWhenDrainTimeoutAbandonsExporter()
    {
        var exporter = new NeverCompletingExporter();
        var pipeline = new TelemetryPipeline(exporter, capacity: 10);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "in-flight", TelemetryPriority.Routine);

        await pipeline.StartAsync(CancellationToken.None);
        pipeline.TryPublish(record).Should().BeTrue();
        await exporter.WaitForExportStartedAsync(TimeSpan.FromSeconds(2));

        await pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        pipeline.DroppedRecords.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task DisposeAsync_CountsDroppedRecordsWhenDrainTimeoutCancelsInFlightAndQueuedRecords()
    {
        var exporter = new BlockingFirstExportExporter();
        var pipeline = new TelemetryPipeline(exporter, capacity: 10);
        var first = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "first", TelemetryPriority.Routine);
        var second = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "second", TelemetryPriority.Routine);
        var acceptedRecords = 0;

        await pipeline.StartAsync(CancellationToken.None);
        pipeline.TryPublish(first).Should().BeTrue();
        acceptedRecords++;

        await exporter.WaitForFirstExportStartedAsync(TimeSpan.FromSeconds(2));

        pipeline.TryPublish(second).Should().BeTrue();
        acceptedRecords++;

        await pipeline.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(
            () => pipeline.DroppedRecords >= acceptedRecords - exporter.Records.Count,
            TimeSpan.FromSeconds(2));

        exporter.Records.Should().BeEmpty();
        pipeline.DroppedRecords.Should().BeGreaterThanOrEqualTo(acceptedRecords);
    }

    [Fact]
    public async Task StartAsync_IsIdempotentForConcurrentCalls()
    {
        var exporter = new RecordingExporter();
        await using var pipeline = new TelemetryPipeline(exporter, capacity: 10);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerCount = 64;
        var readyCallers = 0;

        var startTasks = Enumerable.Range(0, callerCount)
            .Select(_ => Task.Run(async () =>
            {
                Interlocked.Increment(ref readyCallers);
                await release.Task;
                await pipeline.StartAsync(CancellationToken.None);
            }))
            .ToArray();

        await WaitUntilAsync(() => Volatile.Read(ref readyCallers) == callerCount, TimeSpan.FromSeconds(2));
        release.SetResult();
        await Task.WhenAll(startTasks).WaitAsync(TimeSpan.FromSeconds(2));

        GetWorkerStartCount(pipeline).Should().Be(1);
    }

    private static int GetWorkerStartCount(TelemetryPipeline pipeline)
    {
        var property = typeof(TelemetryPipeline).GetProperty("WorkerStartCount", BindingFlags.Instance | BindingFlags.NonPublic);

        property.Should().NotBeNull("the pipeline should expose a test-visible worker start count");
        return (int)property!.GetValue(pipeline)!;
    }

    private static CancellationTokenSource GetStopCts(TelemetryPipeline pipeline)
    {
        var field = typeof(TelemetryPipeline).GetField("stopCts", BindingFlags.Instance | BindingFlags.NonPublic);

        field.Should().NotBeNull("the pipeline should own a cancellation source");
        return (CancellationTokenSource)field!.GetValue(pipeline)!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10));
        }

        condition().Should().BeTrue();
    }

    private sealed class RecordingExporter : ITelemetryExporter
    {
        private readonly object sync = new();
        private readonly List<TelemetryRecord> records = [];
        private readonly List<(int Count, TaskCompletionSource Source)> waiters = [];

        public IReadOnlyList<TelemetryRecord> Records
        {
            get
            {
                lock (sync)
                {
                    return records.ToArray();
                }
            }
        }

        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            lock (sync)
            {
                this.records.AddRange(records);
                CompleteSatisfiedWaiters();
            }

            return Task.CompletedTask;
        }

        public async Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            TaskCompletionSource waiter;

            lock (sync)
            {
                if (records.Count >= count)
                {
                    return;
                }

                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add((count, waiter));
            }

            using var cts = new CancellationTokenSource(timeout);
            await waiter.Task.WaitAsync(cts.Token);
        }

        private void CompleteSatisfiedWaiters()
        {
            for (var index = waiters.Count - 1; index >= 0; index--)
            {
                var waiter = waiters[index];
                if (records.Count < waiter.Count)
                {
                    continue;
                }

                waiters.RemoveAt(index);
                waiter.Source.TrySetResult();
            }
        }
    }

    private sealed class FailingOnceExporter : ITelemetryExporter
    {
        private readonly RecordingExporter inner = new();
        private readonly TaskCompletionSource attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attempts;

        public IReadOnlyList<TelemetryRecord> Records => inner.Records;

        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                attempted.TrySetResult();
                throw new InvalidOperationException("first export failed");
            }

            return inner.ExportAsync(records, cancellationToken);
        }

        public async Task WaitForAttemptAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await attempted.Task.WaitAsync(cts.Token);
        }

        public Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            return inner.WaitForCountAsync(count, timeout);
        }
    }

    private sealed class BlockingCancellationCallbackExporter : ITelemetryExporter
    {
        private readonly ManualResetEventSlim cancellationCallbackCanReturn = new();
        private readonly TaskCompletionSource exportStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.UnsafeRegister(static state =>
            {
                ((ManualResetEventSlim)state!).Wait();
            }, cancellationCallbackCanReturn);

            exportStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        public void UnblockCancellationCallback()
        {
            cancellationCallbackCanReturn.Set();
        }

        public async Task WaitForExportStartedAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await exportStarted.Task.WaitAsync(cts.Token);
        }
    }

    private sealed class CancellationObservingExporter : ITelemetryExporter
    {
        private readonly TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource exportStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseExport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken observedCancellationToken;

        public bool HasCancellationBeenRequested => observedCancellationToken.IsCancellationRequested;

        public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            observedCancellationToken = cancellationToken;
            using var registration = cancellationToken.UnsafeRegister(static state =>
            {
                ((TaskCompletionSource)state!).TrySetResult();
            }, cancellationObserved);

            exportStarted.TrySetResult();
            await releaseExport.Task;
        }

        public void ReleaseExport()
        {
            releaseExport.TrySetResult();
        }

        public async Task WaitForExportStartedAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await exportStarted.Task.WaitAsync(cts.Token);
        }
    }

    private sealed class ReleaseAfterCancellationExporter : ITelemetryExporter
    {
        private readonly TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource exportStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseExport = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.UnsafeRegister(static state =>
            {
                ((TaskCompletionSource)state!).TrySetResult();
            }, cancellationObserved);

            exportStarted.TrySetResult();
            await cancellationObserved.Task;
            await releaseExport.Task;
            throw new OperationCanceledException(cancellationToken);
        }

        public void ReleaseExport()
        {
            releaseExport.TrySetResult();
        }

        public async Task WaitForExportStartedAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await exportStarted.Task.WaitAsync(cts.Token);
        }
    }

    private sealed class BlockingFirstExportExporter : ITelemetryExporter
    {
        private readonly RecordingExporter inner = new();
        private readonly TaskCompletionSource firstExportStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseFirstExport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int attempts;

        public IReadOnlyList<TelemetryRecord> Records => inner.Records;

        public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                firstExportStarted.TrySetResult();
                var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.UnsafeRegister(static state =>
                {
                    ((TaskCompletionSource)state!).TrySetResult();
                }, canceled);

                if (await Task.WhenAny(releaseFirstExport.Task, canceled.Task) == canceled.Task)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }

            await inner.ExportAsync(records, cancellationToken);
        }

        public void ReleaseFirstExport()
        {
            releaseFirstExport.TrySetResult();
        }

        public async Task WaitForFirstExportStartedAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await firstExportStarted.Task.WaitAsync(cts.Token);
        }
    }

    private sealed class NeverCompletingExporter : ITelemetryExporter
    {
        private readonly TaskCompletionSource exportStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            exportStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }

        public async Task WaitForExportStartedAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await exportStarted.Task.WaitAsync(cts.Token);
        }
    }
}
