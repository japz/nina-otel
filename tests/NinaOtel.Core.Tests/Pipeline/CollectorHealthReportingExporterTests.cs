using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Health;
using NinaOtel.Core.Options;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class CollectorHealthReportingExporterTests
{
    private static readonly Uri Endpoint = new("http://collector.local:4317/");

    [Fact]
    public async Task ExportAsync_WhenInnerExporterSucceeds_ReportsHealthySnapshot()
    {
        var inner = new RecordingExporter();
        var reporter = new RecordingHealthReporter();
        var exporter = new CollectorHealthReportingExporter(
            inner,
            reporter.Report,
            Endpoint,
            OtlpProtocol.Grpc,
            TimeProvider.System);
        var records = new[]
        {
            TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "ready", TelemetryPriority.Routine),
        };

        await exporter.ExportAsync(records, CancellationToken.None);

        inner.Records.Should().ContainSingle(r => r.Name == "ready");
        reporter.Snapshots.Should().ContainSingle();
        reporter.Snapshots[0].State.Should().Be(CollectorHealthState.Healthy);
        reporter.Snapshots[0].Endpoint.Should().Be(Endpoint);
        reporter.Snapshots[0].Protocol.Should().Be(OtlpProtocol.Grpc);
        reporter.Snapshots[0].ExportedRecords.Should().Be(1);
    }

    [Fact]
    public async Task ExportAsync_WhenInnerExporterFails_ReportsUnhealthySnapshotAndRethrows()
    {
        var inner = new ThrowingExporter(new InvalidOperationException("collector offline"));
        var reporter = new RecordingHealthReporter();
        var exporter = new CollectorHealthReportingExporter(
            inner,
            reporter.Report,
            Endpoint,
            OtlpProtocol.Grpc,
            TimeProvider.System);
        var records = new[]
        {
            TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "ready", TelemetryPriority.Routine),
        };

        var export = async () => await exporter.ExportAsync(records, CancellationToken.None);

        await export.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("collector offline");
        reporter.Snapshots.Should().ContainSingle();
        reporter.Snapshots[0].State.Should().Be(CollectorHealthState.Unhealthy);
        reporter.Snapshots[0].ErrorType.Should().Be(nameof(InvalidOperationException));
        reporter.Snapshots[0].ErrorMessage.Should().Be("collector offline");
    }

    [Fact]
    public async Task ExportAsync_WhenHealthReporterFails_DoesNotFailSuccessfulExport()
    {
        var inner = new RecordingExporter();
        var exporter = new CollectorHealthReportingExporter(
            inner,
            _ => throw new InvalidOperationException("ui unavailable"),
            Endpoint,
            OtlpProtocol.Grpc,
            TimeProvider.System);
        var records = new[]
        {
            TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "ready", TelemetryPriority.Routine),
        };

        var export = async () => await exporter.ExportAsync(records, CancellationToken.None);

        await export.Should().NotThrowAsync();
        inner.Records.Should().ContainSingle(r => r.Name == "ready");
    }

    private sealed class RecordingExporter : ITelemetryExporter
    {
        public List<TelemetryRecord> Records { get; } = [];

        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            Records.AddRange(records);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingExporter(Exception exception) : ITelemetryExporter
    {
        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken) =>
            Task.FromException(exception);
    }

    private sealed class RecordingHealthReporter
    {
        public List<CollectorHealthSnapshot> Snapshots { get; } = [];

        public void Report(CollectorHealthSnapshot snapshot) => Snapshots.Add(snapshot);
    }
}
