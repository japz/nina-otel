using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class ReloadableTelemetryExporterTests
{
    [Fact]
    public async Task ExportAsync_UsesReplacementForFutureExports()
    {
        var first = new RecordingExporter("first");
        var second = new RecordingExporter("second");
        using var exporter = new ReloadableTelemetryExporter(first);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "ready", TelemetryPriority.Routine);

        await exporter.ExportAsync([record], CancellationToken.None);
        exporter.Update(second);
        await exporter.ExportAsync([record], CancellationToken.None);

        first.Batches.Should().ContainSingle().Which.Should().Be("first");
        second.Batches.Should().ContainSingle().Which.Should().Be("second");
    }

    [Fact]
    public void Dispose_DisposesInitialAndReplacementExporters()
    {
        var first = new RecordingExporter("first");
        var second = new RecordingExporter("second");
        var exporter = new ReloadableTelemetryExporter(first);

        exporter.Update(second);
        exporter.Dispose();

        first.Disposed.Should().BeTrue();
        second.Disposed.Should().BeTrue();
    }

    [Fact]
    public void Update_DisposesPreviousExporterWhenNoExportIsInFlight()
    {
        var first = new RecordingExporter("first");
        var second = new RecordingExporter("second");
        using var exporter = new ReloadableTelemetryExporter(first);

        exporter.Update(second);

        first.Disposed.Should().BeTrue();
        second.Disposed.Should().BeFalse();
    }

    [Fact]
    public async Task Update_DefersPreviousExporterDisposalUntilInFlightExportCompletes()
    {
        var first = new BlockingExporter();
        var second = new RecordingExporter("second");
        using var exporter = new ReloadableTelemetryExporter(first);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "ready", TelemetryPriority.Routine);

        var exportTask = exporter.ExportAsync([record], CancellationToken.None);
        await first.WaitForExportStartedAsync(TimeSpan.FromSeconds(2));

        exporter.Update(second);
        first.Disposed.Should().BeFalse("the previous exporter is still serving an in-flight export");

        first.ReleaseExport();
        await exportTask.WaitAsync(TimeSpan.FromSeconds(2));

        first.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task ExportAsync_AfterDisposeThrowsObjectDisposedException()
    {
        var inner = new RecordingExporter("inner");
        var exporter = new ReloadableTelemetryExporter(inner);
        var record = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "ready", TelemetryPriority.Routine);

        exporter.Dispose();

        Func<Task> export = () => exporter.ExportAsync([record], CancellationToken.None);
        await export.Should().ThrowAsync<ObjectDisposedException>();
    }

    private sealed class RecordingExporter(string name) : ITelemetryExporter, IDisposable
    {
        public List<string> Batches { get; } = [];
        public bool Disposed { get; private set; }

        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            Batches.Add(name);
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class BlockingExporter : ITelemetryExporter, IDisposable
    {
        private readonly TaskCompletionSource exportStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseExport = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            exportStarted.TrySetResult();
            await releaseExport.Task.WaitAsync(cancellationToken);
        }

        public void ReleaseExport() => releaseExport.TrySetResult();

        public async Task WaitForExportStartedAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await exportStarted.Task.WaitAsync(cts.Token);
        }

        public void Dispose() => Disposed = true;
    }
}
