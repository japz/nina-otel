using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Health;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class DurableTelemetryExporterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "nina-otel-durable-exporter-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_WhenInnerFails_PersistsBatchAndDoesNotThrow()
    {
        var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"));
        using var exporter = CreateExporter(
            new ThrowingExporter(new InvalidOperationException("collector unavailable")),
            spool);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "live", TelemetryPriority.Routine);

        Func<Task> export = async () => await exporter.ExportAsync(new[] { record }, CancellationToken.None);

        await export.Should().NotThrowAsync();
        var batch = (await spool.ReadBatchesAsync(CancellationToken.None)).Should().ContainSingle().Subject;
        batch.Records.Should().ContainSingle().Which.Name.Should().Be("live");
    }

    [Fact]
    public async Task ExportAsync_WhenInnerFails_ReportsDegradedHealthWithQueuedSpoolStats()
    {
        var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"));
        var reporter = new RecordingHealthReporter();
        using var exporter = CreateExporter(
            new ThrowingExporter(new InvalidOperationException("collector unavailable")),
            spool,
            reportHealth: reporter.Report);
        var timestamp = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
        var record = TelemetryRecord.Health(timestamp, "test", "live", TelemetryPriority.Important);

        await exporter.ExportAsync(new[] { record }, CancellationToken.None);

        reporter.Snapshots.Should().ContainSingle();
        var snapshot = reporter.Snapshots[0];
        snapshot.State.Should().Be(CollectorHealthState.Unhealthy);
        snapshot.BufferMode.Should().Be(CollectorBufferMode.Degraded);
        snapshot.ErrorType.Should().Be(nameof(InvalidOperationException));
        snapshot.ErrorMessage.Should().Be("collector unavailable");
        snapshot.QueuedRecords.Should().Be(1);
        snapshot.QueuedBytes.Should().BeGreaterThan(0);
        snapshot.OldestQueuedTimestamp.Should().Be(timestamp);
    }

    [Fact]
    public async Task ExportAsync_WhenSpoolHasRecords_ReplaysOldestBeforeLiveBatch()
    {
        var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"));
        var inner = new RecordingExporter();
        using var exporter = CreateExporter(inner, spool);
        var first = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "first", TelemetryPriority.Routine);
        var second = TelemetryRecord.Health(DateTimeOffset.UtcNow.AddMilliseconds(1), "test", "second", TelemetryPriority.Routine);
        var live = TelemetryRecord.Health(DateTimeOffset.UtcNow.AddMilliseconds(2), "test", "live", TelemetryPriority.Routine);

        await spool.AppendBatchAsync(new[] { first }, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(2));
        await spool.AppendBatchAsync(new[] { second }, CancellationToken.None);

        await exporter.ExportAsync(new[] { live }, CancellationToken.None);

        inner.Batches.Should().HaveCount(3);
        inner.Batches.Select(batch => batch.Should().ContainSingle().Subject.Name)
            .Should().Equal("first", "second", "live");
        (await spool.ReadBatchesAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task ExportAsync_WhenInnerSucceeds_DoesNotCreateSpoolDirectory()
    {
        var spoolPath = Path.Combine(root, "spool");
        var inner = new RecordingExporter();
        using var exporter = CreateExporter(inner, new DiskTelemetrySpool(spoolPath));
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "live", TelemetryPriority.Routine);

        await exporter.ExportAsync(new[] { record }, CancellationToken.None);

        Directory.Exists(spoolPath).Should().BeFalse();
        inner.Batches.Should().ContainSingle();
    }

    [Fact]
    public async Task ExportAsync_WhenSpoolReadFails_QuarantinesBadFileAndSpoolsLiveBatch()
    {
        var spoolPath = Path.Combine(root, "spool");
        Directory.CreateDirectory(spoolPath);
        await File.WriteAllTextAsync(Path.Combine(spoolPath, "0000000000000000001-bad.ready"), "null");
        var inner = new RecordingExporter();
        using var exporter = CreateExporter(inner, new DiskTelemetrySpool(spoolPath));
        var live = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "live", TelemetryPriority.Routine);
        var next = TelemetryRecord.Health(DateTimeOffset.UtcNow.AddSeconds(1), "test", "next", TelemetryPriority.Routine);

        Func<Task> export = async () => await exporter.ExportAsync(new[] { live }, CancellationToken.None);

        await export.Should().NotThrowAsync();
        inner.Batches.Should().BeEmpty();
        Directory.GetFiles(spoolPath, "*.invalid").Should().ContainSingle();
        Directory.GetFiles(spoolPath, "*.ready").Should().ContainSingle();

        await exporter.ExportAsync(new[] { next }, CancellationToken.None);

        inner.Batches.Select(batch => batch.Should().ContainSingle().Subject.Name)
            .Should().Equal("live", "next");
        Directory.GetFiles(spoolPath, "*.ready").Should().BeEmpty();
    }

    [Fact]
    public async Task ExportAsync_WhenReplayFails_KeepsSpooledBatchAndSpoolsLiveBatch()
    {
        var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"));
        var inner = new ThrowOnAttemptExporter(1, new InvalidOperationException("replay failed"));
        using var exporter = CreateExporter(inner, spool);
        var replay = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "replay", TelemetryPriority.Routine);
        var live = TelemetryRecord.Health(DateTimeOffset.UtcNow.AddMilliseconds(1), "test", "live", TelemetryPriority.Routine);

        await spool.AppendBatchAsync(new[] { replay }, CancellationToken.None);

        Func<Task> export = async () => await exporter.ExportAsync(new[] { live }, CancellationToken.None);

        await export.Should().NotThrowAsync();
        var batches = await spool.ReadBatchesAsync(CancellationToken.None);
        batches.Should().HaveCount(2);
        batches.SelectMany(batch => batch.Records).Select(record => record.Name)
            .Should().Equal("replay", "live");
    }

    [Fact]
    public async Task ExportAsync_WhenInnerRecovers_DrainsSpooledBatchWithoutNewLiveExport()
    {
        var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"));
        using var inner = new RecoveringExporter(new InvalidOperationException("collector unavailable"));
        using var exporter = CreateExporter(inner, spool, TimeSpan.FromMilliseconds(10));
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "live", TelemetryPriority.Important);

        await exporter.ExportAsync(new[] { record }, CancellationToken.None);
        inner.Recover();

        await inner.WaitForCountAsync(1, TimeSpan.FromSeconds(2));
        await WaitForSpoolToDrainAsync(spool, TimeSpan.FromSeconds(2));

        inner.Batches.Should().ContainSingle()
            .Which.Should().ContainSingle()
            .Which.Name.Should().Be("live");
    }

    [Fact]
    public async Task ExportAsync_WhenInnerRecovers_ReportsHealthyAfterBackgroundDrain()
    {
        var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"));
        var reporter = new RecordingHealthReporter();
        using var inner = new RecoveringExporter(new InvalidOperationException("collector unavailable"));
        using var exporter = CreateExporter(
            inner,
            spool,
            TimeSpan.FromMilliseconds(10),
            reporter.Report);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "live", TelemetryPriority.Important);

        await exporter.ExportAsync(new[] { record }, CancellationToken.None);
        inner.Recover();

        var healthySnapshot = await WaitForHealthSnapshotAsync(
            reporter,
            snapshot => snapshot.State == CollectorHealthState.Healthy &&
                snapshot.BufferMode == CollectorBufferMode.Healthy &&
                snapshot.QueuedRecords == 0,
            TimeSpan.FromSeconds(2));

        reporter.SnapshotCopy.Should().Contain(snapshot => snapshot.BufferMode == CollectorBufferMode.Degraded);
        healthySnapshot.State.Should().Be(CollectorHealthState.Healthy);
    }

    [Fact]
    public async Task ExportAsync_WhenCancellationRequested_PropagatesAndDoesNotSpool()
    {
        var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var exporter = CreateExporter(new CanceledExporter(), spool);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "live", TelemetryPriority.Routine);

        Func<Task> export = async () => await exporter.ExportAsync(new[] { record }, cts.Token);

        await export.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(Path.Combine(root, "spool")).Should().BeFalse();
    }

    [Fact]
    public async Task ExportAsync_WhenAppendFails_RethrowsOriginalExportFailure()
    {
        var exportFailure = new InvalidOperationException("collector unavailable");
        using var exporter = CreateExporter(
            new ThrowingExporter(exportFailure),
            new DiskTelemetrySpool(Path.Combine(root, "not-a-directory", "spool")));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "not-a-directory"), "file");
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "live", TelemetryPriority.Routine);

        var exception = await Record.ExceptionAsync(() => exporter.ExportAsync(new[] { record }, CancellationToken.None));

        exception.Should().BeSameAs(exportFailure);
    }

    [Fact]
    public async Task Dispose_WhenRecoveryExportIsInFlight_DefersInnerDisposeUntilWorkerExits()
    {
        var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"));
        var inner = new BlockingRecoveringExporter(new InvalidOperationException("collector unavailable"));
        var exporter = CreateExporter(inner, spool, TimeSpan.FromMilliseconds(1));
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "live", TelemetryPriority.Important);

        await exporter.ExportAsync(new[] { record }, CancellationToken.None);
        inner.Recover();
        await inner.WaitForExportAttemptAsync(TimeSpan.FromSeconds(2));

        exporter.Dispose();

        inner.Disposed.Should().BeFalse();

        inner.ReleaseExport();
        await inner.WaitForDisposeAsync(TimeSpan.FromSeconds(2));
        inner.Disposed.Should().BeTrue();
    }

    private static DurableTelemetryExporter CreateExporter(
        ITelemetryExporter inner,
        DiskTelemetrySpool spool,
        TimeSpan? recoveryInitialDelay = null,
        Action<CollectorHealthSnapshot>? reportHealth = null)
    {
        var delay = recoveryInitialDelay ?? TimeSpan.FromMinutes(1);
        return new DurableTelemetryExporter(
            inner,
            spool,
            delay,
            delay,
            reportHealth,
            new Uri("http://collector.local:4317/"),
            OtlpProtocol.Grpc,
            TimeProvider.System);
    }

    private static async Task WaitForSpoolToDrainAsync(DiskTelemetrySpool spool, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        while (!cts.IsCancellationRequested)
        {
            if (!(await spool.ReadBatchesAsync(CancellationToken.None)).Any())
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException("Telemetry spool did not drain before the timeout elapsed.");
    }

    private static async Task<CollectorHealthSnapshot> WaitForHealthSnapshotAsync(
        RecordingHealthReporter reporter,
        Predicate<CollectorHealthSnapshot> predicate,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        while (!cts.IsCancellationRequested)
        {
            lock (reporter.Gate)
            {
                var matchingSnapshot = reporter.Snapshots.FirstOrDefault(snapshot => predicate(snapshot));
                if (matchingSnapshot is not null)
                {
                    return matchingSnapshot;
                }
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                break;
            }
        }

        throw new TimeoutException("Expected collector health snapshot was not reported.");
    }

    private sealed class RecordingHealthReporter
    {
        public object Gate { get; } = new();

        public List<CollectorHealthSnapshot> Snapshots { get; } = [];

        public IReadOnlyList<CollectorHealthSnapshot> SnapshotCopy
        {
            get
            {
                lock (Gate)
                {
                    return Snapshots.ToArray();
                }
            }
        }

        public void Report(CollectorHealthSnapshot snapshot)
        {
            lock (Gate)
            {
                Snapshots.Add(snapshot);
            }
        }
    }

    private sealed class RecordingExporter : ITelemetryExporter
    {
        public List<IReadOnlyList<TelemetryRecord>> Batches { get; } = [];

        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            Batches.Add(records.ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingExporter(Exception exception) : ITelemetryExporter
    {
        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken) =>
            Task.FromException(exception);
    }

    private sealed class ThrowOnAttemptExporter(int failingAttempt, Exception exception) : ITelemetryExporter
    {
        private int attempts;

        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref attempts) == failingAttempt)
            {
                return Task.FromException(exception);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecoveringExporter(Exception initialException) : ITelemetryExporter, IDisposable
    {
        private readonly object gate = new();
        private readonly TaskCompletionSource exported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? exception = initialException;
        private int disposed;

        public List<IReadOnlyList<TelemetryRecord>> Batches { get; } = [];

        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Volatile.Read(ref disposed) != 0)
            {
                return Task.FromException(new ObjectDisposedException(nameof(RecoveringExporter)));
            }

            var currentException = Volatile.Read(ref exception);
            if (currentException != null)
            {
                return Task.FromException(currentException);
            }

            lock (gate)
            {
                Batches.Add(records.ToArray());
            }

            exported.TrySetResult();
            return Task.CompletedTask;
        }

        public void Recover() => Volatile.Write(ref exception, null);

        public async Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);

            while (!cts.IsCancellationRequested)
            {
                lock (gate)
                {
                    if (Batches.Count >= count)
                    {
                        return;
                    }
                }

                await exported.Task.WaitAsync(timeout);
            }
        }

        public void Dispose()
        {
            Volatile.Write(ref disposed, 1);
            exported.TrySetCanceled();
        }
    }

    private sealed class BlockingRecoveringExporter(Exception initialException) : ITelemetryExporter, IDisposable
    {
        private readonly TaskCompletionSource enteredExport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseExport = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Exception? exception = initialException;

        public bool Disposed => disposed.Task.IsCompleted;

        public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var currentException = Volatile.Read(ref exception);
            if (currentException != null)
            {
                throw currentException;
            }

            enteredExport.TrySetResult();
            await releaseExport.Task.ConfigureAwait(false);
        }

        public void Recover() => Volatile.Write(ref exception, null);

        public void ReleaseExport() => releaseExport.TrySetResult();

        public async Task WaitForExportAttemptAsync(TimeSpan timeout) =>
            await enteredExport.Task.WaitAsync(timeout);

        public async Task WaitForDisposeAsync(TimeSpan timeout) =>
            await disposed.Task.WaitAsync(timeout);

        public void Dispose() => disposed.TrySetResult();
    }

    private sealed class CanceledExporter : ITelemetryExporter
    {
        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken) =>
            Task.FromCanceled(cancellationToken);
    }
}
