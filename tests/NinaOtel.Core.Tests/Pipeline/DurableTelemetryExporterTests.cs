using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
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
        var exporter = new DurableTelemetryExporter(
            new ThrowingExporter(new InvalidOperationException("collector unavailable")),
            spool);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "live", TelemetryPriority.Routine);

        Func<Task> export = async () => await exporter.ExportAsync(new[] { record }, CancellationToken.None);

        await export.Should().NotThrowAsync();
        var batch = (await spool.ReadBatchesAsync(CancellationToken.None)).Should().ContainSingle().Subject;
        batch.Records.Should().ContainSingle().Which.Name.Should().Be("live");
    }

    [Fact]
    public async Task ExportAsync_WhenSpoolHasRecords_ReplaysOldestBeforeLiveBatch()
    {
        var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"));
        var inner = new RecordingExporter();
        var exporter = new DurableTelemetryExporter(inner, spool);
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
        var exporter = new DurableTelemetryExporter(inner, new DiskTelemetrySpool(spoolPath));
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
        var exporter = new DurableTelemetryExporter(inner, new DiskTelemetrySpool(spoolPath));
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
        var exporter = new DurableTelemetryExporter(inner, spool);
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
    public async Task ExportAsync_WhenCancellationRequested_PropagatesAndDoesNotSpool()
    {
        var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var exporter = new DurableTelemetryExporter(new CanceledExporter(), spool);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "live", TelemetryPriority.Routine);

        Func<Task> export = async () => await exporter.ExportAsync(new[] { record }, cts.Token);

        await export.Should().ThrowAsync<OperationCanceledException>();
        Directory.Exists(Path.Combine(root, "spool")).Should().BeFalse();
    }

    [Fact]
    public async Task ExportAsync_WhenAppendFails_RethrowsOriginalExportFailure()
    {
        var exportFailure = new InvalidOperationException("collector unavailable");
        var exporter = new DurableTelemetryExporter(
            new ThrowingExporter(exportFailure),
            new DiskTelemetrySpool(Path.Combine(root, "not-a-directory", "spool")));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "not-a-directory"), "file");
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "live", TelemetryPriority.Routine);

        var exception = await Record.ExceptionAsync(() => exporter.ExportAsync(new[] { record }, CancellationToken.None));

        exception.Should().BeSameAs(exportFailure);
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

    private sealed class CanceledExporter : ITelemetryExporter
    {
        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken) =>
            Task.FromCanceled(cancellationToken);
    }
}
