using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;
using NinaOtel.Core.Telemetry;
using Xunit;

namespace NinaOtel.Core.Tests.Telemetry;

public sealed class CoreTelemetryFilteringSinkTests
{
    [Fact]
    public void TryPublish_WhenDefaultsEnabled_ForwardsAllSignals()
    {
        var inner = new RecordingSink();
        var sink = new CoreTelemetryFilteringSink(inner, new CoreTelemetryOptions());
        var timestamp = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        TelemetryRecord[] records =
        [
            TelemetryRecord.Metric(timestamp, "nina.camera", "camera_sensor_temperature", -5, TelemetryPriority.Normal),
            TelemetryRecord.Metric(timestamp, "nina.image", "image_mean", 1200, TelemetryPriority.Normal),
            TelemetryRecord.Span(timestamp, "nina.image", "nina.exposure", SpanEventKind.Start, "span-1", TelemetryPriority.Normal),
            TelemetryRecord.Log(timestamp, "nina", TelemetrySeverity.Information, "hello", TelemetryPriority.Normal),
            TelemetryRecord.Health(timestamp, "ninaotel", "health", TelemetryPriority.Normal),
        ];

        foreach (var record in records)
        {
            sink.TryPublish(record).Should().BeTrue();
        }

        inner.Records.Should().Equal(records);
    }

    [Fact]
    public void TryPublish_WhenEquipmentDisabled_DropsCoreEquipmentMetricsButKeepsImageAddonLogAndHealth()
    {
        var inner = new RecordingSink();
        var sink = new CoreTelemetryFilteringSink(
            inner,
            new CoreTelemetryOptions { EquipmentEnabled = false });
        var timestamp = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var cameraMetric = TelemetryRecord.Metric(timestamp, "nina.camera", "camera_sensor_temperature", -5, TelemetryPriority.Normal);
        var switchMetric = TelemetryRecord.Metric(timestamp, "nina.switch", "switch_ro_sw3", 1, TelemetryPriority.Normal);
        var imageMetric = TelemetryRecord.Metric(timestamp, "nina.image", "image_mean", 1200, TelemetryPriority.Normal);
        var phd2Metric = TelemetryRecord.Metric(timestamp, "phd2", "phd2_guide_sample_count", 10, TelemetryPriority.Normal);
        var log = TelemetryRecord.Log(timestamp, "nina", TelemetrySeverity.Warning, "warning", TelemetryPriority.Important);
        var health = TelemetryRecord.Health(timestamp, "ninaotel", "health", TelemetryPriority.Normal);

        sink.TryPublish(cameraMetric).Should().BeTrue();
        sink.TryPublish(switchMetric).Should().BeTrue();
        sink.TryPublish(imageMetric).Should().BeTrue();
        sink.TryPublish(phd2Metric).Should().BeTrue();
        sink.TryPublish(log).Should().BeTrue();
        sink.TryPublish(health).Should().BeTrue();

        inner.Records.Should().Equal(imageMetric, phd2Metric, log, health);
    }

    [Fact]
    public void TryPublish_WhenEquipmentDisabled_KeepsAddonMetricWithCoreEquipmentName()
    {
        var inner = new RecordingSink();
        var sink = new CoreTelemetryFilteringSink(
            inner,
            new CoreTelemetryOptions { EquipmentEnabled = false });
        var timestamp = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var addonMetric = TelemetryRecord.Metric(
            timestamp,
            "onstepx",
            "mount_altitude",
            42,
            TelemetryPriority.Normal,
            new Dictionary<string, object?>
            {
                ["addon.id"] = "onstepx",
            });

        sink.TryPublish(addonMetric).Should().BeTrue();

        inner.Records.Should().Equal(addonMetric);
    }

    [Fact]
    public void TryPublish_WhenImageStatsDisabled_DropsImageMetricButKeepsEquipmentAndImageWorkflowSpan()
    {
        var inner = new RecordingSink();
        var sink = new CoreTelemetryFilteringSink(
            inner,
            new CoreTelemetryOptions { ImageStatsEnabled = false });
        var timestamp = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var imageMetric = TelemetryRecord.Metric(timestamp, "nina.image", "image_mean", 1200, TelemetryPriority.Normal);
        var cameraMetric = TelemetryRecord.Metric(timestamp, "nina.camera", "camera_sensor_temperature", -5, TelemetryPriority.Normal);
        var imageSpan = TelemetryRecord.Span(timestamp, "nina.image", "nina.image_save", SpanEventKind.Stop, "span-1", TelemetryPriority.Normal);

        sink.TryPublish(imageMetric).Should().BeTrue();
        sink.TryPublish(cameraMetric).Should().BeTrue();
        sink.TryPublish(imageSpan).Should().BeTrue();

        inner.Records.Should().Equal(cameraMetric, imageSpan);
    }

    [Fact]
    public void TryPublish_WhenWorkflowTracesDisabled_DropsSpansOnly()
    {
        var inner = new RecordingSink();
        var sink = new CoreTelemetryFilteringSink(
            inner,
            new CoreTelemetryOptions { WorkflowTracesEnabled = false });
        var timestamp = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var span = TelemetryRecord.Span(timestamp, "nina.image", "nina.exposure", SpanEventKind.Start, "span-1", TelemetryPriority.Normal);
        var metric = TelemetryRecord.Metric(timestamp, "nina.camera", "camera_sensor_temperature", -5, TelemetryPriority.Normal);
        var log = TelemetryRecord.Log(timestamp, "nina", TelemetrySeverity.Information, "hello", TelemetryPriority.Normal);
        var health = TelemetryRecord.Health(timestamp, "ninaotel", "health", TelemetryPriority.Normal);

        sink.TryPublish(span).Should().BeTrue();
        sink.TryPublish(metric).Should().BeTrue();
        sink.TryPublish(log).Should().BeTrue();
        sink.TryPublish(health).Should().BeTrue();

        inner.Records.Should().Equal(metric, log, health);
    }

    [Fact]
    public void TryPublish_WhenWorkflowTracesDisabled_KeepsAddonSpans()
    {
        var inner = new RecordingSink();
        var sink = new CoreTelemetryFilteringSink(
            inner,
            new CoreTelemetryOptions { WorkflowTracesEnabled = false });
        var addonSpan = TelemetryRecord.Span(
            new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero),
            "phd2",
            "phd2.guiding",
            SpanEventKind.Start,
            "span-1",
            TelemetryPriority.Normal,
            new Dictionary<string, object?>
            {
                ["addon.id"] = "phd2",
            });

        sink.TryPublish(addonSpan).Should().BeTrue();

        inner.Records.Should().Equal(addonSpan);
    }

    [Fact]
    public void UpdateOptions_AppliesRuntimeChanges()
    {
        var inner = new RecordingSink();
        var sink = new CoreTelemetryFilteringSink(inner, new CoreTelemetryOptions());
        var timestamp = new DateTimeOffset(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);
        var before = TelemetryRecord.Metric(timestamp, "nina.camera", "camera_sensor_temperature", -5, TelemetryPriority.Normal);
        var after = before with { Timestamp = timestamp.AddSeconds(1) };

        sink.TryPublish(before).Should().BeTrue();
        sink.UpdateOptions(new CoreTelemetryOptions { EquipmentEnabled = false });
        sink.TryPublish(after).Should().BeTrue();

        inner.Records.Should().Equal(before);
    }

    private sealed class RecordingSink : ITelemetrySink
    {
        public List<TelemetryRecord> Records { get; } = [];

        public bool TryPublish(TelemetryRecord record)
        {
            Records.Add(record);
            return true;
        }
    }
}
