using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class TelemetryPipelineTests
{
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

    private sealed class RecordingExporter : ITelemetryExporter
    {
        private readonly TaskCompletionSource exported = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int targetCount = 1;

        public List<TelemetryRecord> Records { get; } = [];

        public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
        {
            Records.AddRange(records);
            if (Records.Count >= targetCount)
            {
                exported.TrySetResult();
            }

            return Task.CompletedTask;
        }

        public async Task WaitForCountAsync(int count, TimeSpan timeout)
        {
            targetCount = count;
            using var cts = new CancellationTokenSource(timeout);
            await exported.Task.WaitAsync(cts.Token);
        }
    }
}
