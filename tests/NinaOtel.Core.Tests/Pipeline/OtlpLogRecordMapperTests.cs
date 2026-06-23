using FluentAssertions;
using Microsoft.Extensions.Logging;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class OtlpLogRecordMapperTests
{
    [Fact]
    public void Map_PreservesNormalizedMetadataAndRecordAttributes()
    {
        var timestamp = new DateTimeOffset(2026, 6, 17, 20, 30, 0, TimeSpan.Zero);
        var record = new TelemetryRecord(
            TelemetrySignal.Span,
            timestamp,
            "nina.camera",
            "exposure",
            TelemetryPriority.Critical,
            new Dictionary<string, object?>
            {
                ["camera.id"] = "asi-2600",
                ["exposure.seconds"] = 120.0,
            },
            NumericValue: 42.5,
            Severity: TelemetrySeverity.Error,
            SpanKind: SpanEventKind.Start,
            SpanId: "span-1",
            ParentSpanId: "parent-1") with
            {
                TraceId = "trace-1",
            };

        var payload = OtlpLogRecordMapper.Map(record);

        payload.Level.Should().Be(LogLevel.Error);
        payload.Message.Should().Be("nina.camera span exposure Start");
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("ninaotel.signal", "Span"));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("ninaotel.source", "nina.camera"));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("ninaotel.name", "exposure"));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("ninaotel.priority", "Critical"));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>(
            "ninaotel.timestamp_unix_ms",
            timestamp.ToUnixTimeMilliseconds()));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("camera.id", "asi-2600"));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("exposure.seconds", 120.0));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("ninaotel.numeric_value", 42.5));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("ninaotel.severity", "Error"));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("ninaotel.span.kind", "Start"));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("ninaotel.span.id", "span-1"));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("ninaotel.trace.id", "trace-1"));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("ninaotel.span.parent_id", "parent-1"));
        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("{OriginalFormat}", payload.Message));
    }

    [Fact]
    public void Map_UsesBodyAsMessageAndMapsPriorityWhenSeverityIsMissing()
    {
        var record = TelemetryRecord.Log(
            DateTimeOffset.UtcNow,
            "nina.core",
            TelemetrySeverity.Information,
            "image saved",
            TelemetryPriority.Important) with
            {
                Severity = null,
            };

        var payload = OtlpLogRecordMapper.Map(record);

        payload.Level.Should().Be(LogLevel.Warning);
        payload.Message.Should().Be("image saved");
    }

    [Fact]
    public void Map_WhenRecordHasNoLegacyNameAttribute_AddsNameAttributeFromRecordName()
    {
        var record = TelemetryRecord.Log(
            DateTimeOffset.UtcNow,
            "nina.mount",
            TelemetrySeverity.Information,
            "Mount connected",
            TelemetryPriority.Normal,
            new Dictionary<string, object?>
            {
                ["mount_name"] = "EQ6-R",
            }) with
            {
                Name = "mount_connected",
            };

        var payload = OtlpLogRecordMapper.Map(record);

        payload.Attributes.Should().Contain(new KeyValuePair<string, object?>("name", "mount_connected"));
    }

    [Fact]
    public void Map_WhenRecordAlreadyHasLegacyNameAttribute_PreservesExplicitValue()
    {
        var record = TelemetryRecord.Log(
            DateTimeOffset.UtcNow,
            "nina.image",
            TelemetrySeverity.Information,
            "Image taken",
            TelemetryPriority.Normal,
            new Dictionary<string, object?>
            {
                ["name"] = "image",
            }) with
            {
                Name = "nina.image_save",
            };

        var payload = OtlpLogRecordMapper.Map(record);

        payload.Attributes
            .Where(static attribute => attribute.Key == "name")
            .Should()
            .ContainSingle()
            .Which.Value.Should().Be("image");
    }
}
