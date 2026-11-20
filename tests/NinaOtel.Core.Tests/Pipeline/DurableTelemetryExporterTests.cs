using System.Collections.Concurrent;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Health;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class DurableTelemetryExporterTests : IAsyncDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private readonly string root = Path.Combine(Path.GetTempPath(), "nina-otel-durable-tests", Guid.NewGuid().ToString("N"));
    private DurableTelemetryExporter? exporter;

    [Fact]
    public async Task StartupReplay_IsOldestFirstAndLiveFailureCanSpoolWhileReplayIsBlocked()
    {
        var spool = new DiskTelemetrySpool(root);
        await AppendAsync(spool, Health("first"));
        await AppendAsync(spool, Health("second"));
        await File.WriteAllTextAsync(Path.Combine(root, "last-bad.ready"), "not JSON");

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replayed = new ConcurrentQueue<string>();
        var snapshots = new ConcurrentQueue<CollectorHealthSnapshot>();
        var live = new LiveExporter((_, _) => throw new HttpRequestException("collector unavailable"));
        exporter = Create(spool, live, async (_, batch, token) =>
        {
            var name = Assert.Single(batch.Records).Name;
            replayed.Enqueue(name);
            if (name == "first")
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
            }
        }, snapshots.Enqueue);

        await started.Task.WaitAsync(TestTimeout);
        await exporter.ExportAsync([Health("live")], CancellationToken.None).WaitAsync(TestTimeout);
        Assert.Equal(4, spool.GetStats().QueuedBatches);
        Assert.Empty(Directory.GetFiles(root, "*.invalid"));
        Assert.Contains(snapshots, snapshot => snapshot.BufferMode == CollectorBufferMode.Degraded);
        release.SetResult();
        await UntilAsync(() => snapshots.Any(snapshot =>
            snapshot.BufferMode == CollectorBufferMode.Healthy && snapshot.QueuedBatches == 0 &&
            snapshot.QuarantinedBatches == 1));
        Assert.Equal(["first", "second", "live"], replayed.ToArray());
    }

    [Fact]
    public async Task Replay_TransientMetricFailureDoesNotBlockLaterLogsOrResendAcknowledgedSignals()
    {
        var spool = new DiskTelemetrySpool(root);
        await AppendAsync(spool, Metric("firstMetric"), Health("firstLog"));
        await AppendAsync(spool, Metric("secondMetric"), Health("secondLog"));
        var sent = new ConcurrentQueue<(SpoolSignal, string)>();
        var attemptedMetrics = 0;
        var allowMetrics = 0;

        exporter = Create(spool, new LiveExporter(), (signal, batch, _) =>
        {
            if (signal == SpoolSignal.Metrics)
            {
                Interlocked.Increment(ref attemptedMetrics);
                if (Volatile.Read(ref allowMetrics) == 0)
                {
                    throw new HttpRequestException("test DNS outage");
                }
            }
            var name = batch.Records.Single(record =>
                signal == SpoolSignal.Metrics ? record.Signal == TelemetrySignal.Metric : record.Signal == TelemetrySignal.Health).Name;
            sent.Enqueue((signal, name));
            return Task.CompletedTask;
        }, initialBackoff: TimeSpan.FromSeconds(1));

        await UntilAsync(() => sent.Contains((SpoolSignal.Logs, "secondLog")));
        Assert.Equal(1, Volatile.Read(ref attemptedMetrics));
        Assert.Equal(2, spool.GetStats().QueuedBatches);
        Volatile.Write(ref allowMetrics, 1);
        await UntilAsync(() => !spool.EnumerateReadyFiles().Any());
        Assert.Equal(
            [(SpoolSignal.Logs, "firstLog"), (SpoolSignal.Logs, "secondLog"),
             (SpoolSignal.Metrics, "firstMetric"), (SpoolSignal.Metrics, "secondMetric")],
            sent.ToArray());
        Assert.Empty(Directory.GetFiles(root, "*.invalid"));
    }

    [Fact]
    public async Task Replay_QuarantinesPermanentFailuresAndCorruptJsonAndContinues()
    {
        var spool = new DiskTelemetrySpool(root);
        await AppendAsync(spool, Metric("invalid"), Health("goodLog"));
        await AppendAsync(spool, Metric("valid"));
        await File.WriteAllTextAsync(Path.Combine(root, "0000000000000000001-corrupt.ready"), "null");
        var sent = new ConcurrentQueue<SpoolSignal>();
        var diagnostic = new ConcurrentQueue<string>();
        var snapshots = new ConcurrentQueue<CollectorHealthSnapshot>();
        exporter = Create(spool, new LiveExporter(), (signal, batch, _) =>
        {
            if (signal == SpoolSignal.Metrics && batch.Records[0].Name == "invalid")
            {
                throw new TelemetryExportException("HTTP 400 Bad Request", isPermanent: true);
            }
            sent.Enqueue(signal);
            return Task.CompletedTask;
        }, snapshots.Enqueue, diagnostic.Enqueue);

        await UntilAsync(() => snapshots.Any(snapshot => snapshot.QueuedBatches == 0 && snapshot.QuarantinedBatches == 2));
        Assert.Single(diagnostic);
        Assert.Equal([SpoolSignal.Logs, SpoolSignal.Metrics], sent.ToArray());
        var invalid = Directory.GetFiles(root, "*.invalid");
        Assert.Equal(2, invalid.Length);
        Assert.Contains(invalid, path => File.ReadAllText(path).Contains("goodLog", StringComparison.Ordinal));
        Assert.Contains(invalid, path => File.ReadAllText(path) == "null");
    }

    [Fact]
    public async Task Replay_RespectsRateLimitBetweenFiles()
    {
        var spool = new DiskTelemetrySpool(root);
        await AppendAsync(spool, Health("first"));
        await AppendAsync(spool, Health("second"));
        var attempts = new ConcurrentQueue<long>();
        exporter = Create(spool, new LiveExporter(), (_, _, _) =>
        {
            attempts.Enqueue(System.Diagnostics.Stopwatch.GetTimestamp());
            return Task.CompletedTask;
        }, interval: TimeSpan.FromMilliseconds(200));

        await UntilAsync(() => attempts.Count == 2);
        var timestamps = attempts.ToArray();
        Assert.True(System.Diagnostics.Stopwatch.GetElapsedTime(timestamps[0], timestamps[1]) >=
            TimeSpan.FromMilliseconds(175));
    }

    [Fact]
    public async Task Dispose_CancelsAndWaitsForInFlightReplayBeforeDisposingTransports()
    {
        var spool = new DiskTelemetrySpool(root);
        await AppendAsync(spool, Health("queued"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replay = new ReplayExporter(async (_, _, token) =>
        {
            started.TrySetResult();
            await release.Task;
            token.ThrowIfCancellationRequested();
        });
        exporter = new DurableTelemetryExporter(new LiveExporter(), replay, spool);
        await started.Task.WaitAsync(TestTimeout);
        exporter.Dispose();
        Assert.False(replay.Disposed.Task.IsCompleted);
        release.TrySetResult();
        await replay.Disposed.Task.WaitAsync(TestTimeout);
        Assert.Single(spool.EnumerateReadyFiles());
    }

    [Fact]
    public async Task Export_WhenSpoolingFails_RethrowsOriginalExportFailure()
    {
        var spool = new DiskTelemetrySpool(root, maxBytes: 1, maxAge: TimeSpan.FromDays(7));
        var failure = new HttpRequestException("collector unavailable");
        var snapshots = new ConcurrentQueue<CollectorHealthSnapshot>();
        exporter = Create(spool, new LiveExporter((_, _) => throw failure), (_, _, _) => Task.CompletedTask, snapshots.Enqueue);
        var actual = await Assert.ThrowsAsync<HttpRequestException>(() =>
            exporter.ExportAsync([Health("live")], CancellationToken.None));
        Assert.Same(failure, actual);
        Assert.Contains(snapshots, snapshot => snapshot.DroppedRecords == 1);
    }

    public async ValueTask DisposeAsync()
    {
        if (exporter is not null)
        {
            exporter.Dispose();
            await exporter.ReplayCompletion.WaitAsync(TestTimeout);
        }
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DurableTelemetryExporter Create(
        DiskTelemetrySpool spool,
        ITelemetryExporter live,
        Func<SpoolSignal, DiskTelemetrySpool.Batch, CancellationToken, Task> replay,
        Action<CollectorHealthSnapshot>? health = null,
        Action<string>? diagnostic = null,
        TimeSpan? interval = null,
        TimeSpan? initialBackoff = null) =>
        new(live, new ReplayExporter(replay), spool, health, new Uri("https://example.test"), diagnostic: diagnostic,
            replayInterval: interval ?? TimeSpan.FromMilliseconds(10), initialBackoff: initialBackoff);

    private static Task AppendAsync(DiskTelemetrySpool spool, params TelemetryRecord[] records) =>
        spool.AppendBatchAsync(records, CancellationToken.None);

    private static TelemetryRecord Health(string name) =>
        TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", name, TelemetryPriority.Routine);

    private static TelemetryRecord Metric(string name) =>
        TelemetryRecord.Metric(DateTimeOffset.UtcNow, "test", name, 1, TelemetryPriority.Routine);

    internal static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TestTimeout);
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class LiveExporter(Func<IReadOnlyList<TelemetryRecord>, CancellationToken, Task>? send = null) : ITelemetryExporter
    {
        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken) =>
            send?.Invoke(records, cancellationToken) ?? Task.CompletedTask;
    }

    private sealed class ReplayExporter(Func<SpoolSignal, DiskTelemetrySpool.Batch, CancellationToken, Task> send) : ISpoolReplayExporter
    {
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExportAsync(SpoolSignal signal, DiskTelemetrySpool.Batch batch, CancellationToken cancellationToken) =>
            send(signal, batch, cancellationToken);

        public void Dispose() => Disposed.TrySetResult();
    }
}
