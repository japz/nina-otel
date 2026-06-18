using FluentAssertions;
using System.Diagnostics.Metrics;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class OtlpMetricStateStoreTests
{
    [Fact]
    public void Apply_StoresMetricRecordsAsObservableGaugeMeasurements()
    {
        using var meter = new Meter("ninaotel.test");
        var store = new OtlpMetricStateStore(meter);
        var record = TelemetryRecord.Metric(
            DateTimeOffset.UnixEpoch,
            "nina.camera",
            "camera_sensor_temperature",
            -7.25,
            TelemetryPriority.Normal,
            new Dictionary<string, object?>
            {
                ["camera_name"] = "ASI2600MM",
            });

        var accepted = store.Apply(
        [
            record,
            TelemetryRecord.Log(
                DateTimeOffset.UnixEpoch,
                "nina.camera",
                TelemetrySeverity.Information,
                "camera connected",
                TelemetryPriority.Routine),
        ]);

        accepted.Should().Be(1);
        store.InstrumentNames.Should().ContainSingle().Which.Should().Be("camera_sensor_temperature");
        var measurement = store.CollectMeasurements("camera_sensor_temperature").Should().ContainSingle().Subject;
        measurement.Value.Should().Be(-7.25);
        measurement.Tags.ToArray().ToDictionary(static tag => tag.Key, static tag => tag.Value)
            .Should().Contain(new KeyValuePair<string, object?>("camera_name", "ASI2600MM"))
            .And.Contain(new KeyValuePair<string, object?>("ninaotel.source", "nina.camera"));
    }

    [Fact]
    public void Apply_SkipsDeferredImageMetrics()
    {
        using var meter = new Meter("ninaotel.test");
        var store = new OtlpMetricStateStore(meter);
        var imageMetric = TelemetryRecord.Metric(
            DateTimeOffset.UnixEpoch,
            "nina.image",
            "image_mean",
            1842.5,
            TelemetryPriority.Normal,
            new Dictionary<string, object?>
            {
                ["image_file_name"] = "M42_L_001.fit",
                ["camera_name"] = "ASI2600MM",
            });

        var accepted = store.Apply([imageMetric]);

        accepted.Should().Be(0);
        store.InstrumentNames.Should().BeEmpty();
        store.CollectMeasurements("image_mean").Should().BeEmpty();
    }

    [Fact]
    public void Apply_FiltersLiveGaugeTagsToCatalogAttributes()
    {
        using var meter = new Meter("ninaotel.test");
        var store = new OtlpMetricStateStore(meter);
        var record = TelemetryRecord.Metric(
            DateTimeOffset.UnixEpoch,
            "nina.camera",
            "camera_sensor_temperature",
            -7.25,
            TelemetryPriority.Normal,
            new Dictionary<string, object?>
            {
                ["camera_name"] = "ASI2600MM",
                ["image_file_name"] = "M42_L_001.fit",
                ["target_name"] = "M42",
                ["error_message"] = "high-cardinality text",
            });

        store.Apply([record]);

        var measurement = store.CollectMeasurements("camera_sensor_temperature").Should().ContainSingle().Subject;
        var tags = measurement.Tags.ToArray().ToDictionary(static tag => tag.Key, static tag => tag.Value);
        tags.Should().Contain(new KeyValuePair<string, object?>("camera_name", "ASI2600MM"));
        tags.Should().Contain(new KeyValuePair<string, object?>("ninaotel.source", "nina.camera"));
        tags.Should().NotContainKey("image_file_name");
        tags.Should().NotContainKey("target_name");
        tags.Should().NotContainKey("error_message");
    }

    [Fact]
    public void Apply_ReplacesPreviousValueForSameMetricAndTags()
    {
        using var meter = new Meter("ninaotel.test");
        var store = new OtlpMetricStateStore(meter);
        var attributes = new Dictionary<string, object?>
        {
            ["focuser_name"] = "EAF",
        };

        store.Apply(
        [
            TelemetryRecord.Metric(
                DateTimeOffset.UnixEpoch,
                "nina.focuser",
                "focuser_position",
                1024,
                TelemetryPriority.Normal,
                attributes),
            TelemetryRecord.Metric(
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                "nina.focuser",
                "focuser_position",
                1031,
                TelemetryPriority.Normal,
                attributes),
        ]);

        var measurement = store.CollectMeasurements("focuser_position").Should().ContainSingle().Subject;
        measurement.Value.Should().Be(1031);
    }

    [Fact]
    public void Apply_RemovesPreviousValueWhenLiveGaugeReceivesNaN()
    {
        using var meter = new Meter("ninaotel.test");
        var store = new OtlpMetricStateStore(meter);
        var attributes = new Dictionary<string, object?>
        {
            ["focuser_name"] = "EAF",
        };

        store.Apply(
        [
            TelemetryRecord.Metric(
                DateTimeOffset.UnixEpoch,
                "nina.focuser",
                "focuser_position",
                1024,
                TelemetryPriority.Normal,
                attributes),
            TelemetryRecord.Metric(
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                "nina.focuser",
                "focuser_position",
                double.NaN,
                TelemetryPriority.Normal,
                attributes),
        ]);

        store.CollectMeasurements("focuser_position").Should().BeEmpty();
    }

    [Fact]
    public void Apply_KeepsSeparateMeasurementsForDifferentTags()
    {
        using var meter = new Meter("ninaotel.test");
        var store = new OtlpMetricStateStore(meter);

        store.Apply(
        [
            TelemetryRecord.Metric(
                DateTimeOffset.UnixEpoch,
                "nina.switch",
                "switch_ro_sw1",
                0.0,
                TelemetryPriority.Normal,
                new Dictionary<string, object?>
                {
                    ["switch_name"] = "PowerBox",
                    ["switch_id"] = 1,
                }),
            TelemetryRecord.Metric(
                DateTimeOffset.UnixEpoch,
                "nina.switch",
                "switch_ro_sw3",
                12.3,
                TelemetryPriority.Normal,
                new Dictionary<string, object?>
                {
                    ["switch_name"] = "PowerBox",
                    ["switch_id"] = 3,
                }),
        ]);

        store.CollectMeasurements("switch_ro_sw1").Should().ContainSingle().Which.Value.Should().Be(0.0);
        store.CollectMeasurements("switch_ro_sw3").Should().ContainSingle().Which.Value.Should().Be(12.3);
    }
}
