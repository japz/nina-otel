using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Pipeline;
using NinaOtel.Core.Telemetry;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class OtlpPointInTimeMetricSerializerTests
{
    private static readonly DateTimeOffset CatalogMetricTimestamp =
        DateTimeOffset.UnixEpoch.AddSeconds(123).AddTicks(4567);

    [Fact]
    public void Serialize_WritesExpectedPayloadForEveryCatalogDeferredPointInTimeMetric()
    {
        var deferredMetrics = NinaMetricCatalog.All
            .Where(static metric => metric.ExportKind == NinaMetricExportKind.DeferredPointInTime)
            .ToArray();

        foreach (var metric in deferredMetrics)
        {
            var record = CreateMetricRecord(metric);
            var payload = OtlpPointInTimeMetricSerializer.Serialize([record]);

            payload.Should().NotBeEmpty(metric.Name);
            var metricPayload = ProtobufFieldScanner.FindMessages(payload, "1.2.2")
                .Should()
                .ContainSingle(
                    candidate => ProtobufFieldScanner.FindStrings(candidate, "1").Contains(metric.Name),
                    metric.Name)
                .Which;
            var dataPoint = ProtobufFieldScanner.FindMessages(metricPayload, "5.1")
                .Should()
                .ContainSingle(metric.Name)
                .Which;

            ProtobufFieldScanner.FindDoubles(dataPoint, "4")
                .Should()
                .ContainSingle(metric.Name)
                .Which
                .Should()
                .Be(record.NumericValue!.Value);
            ProtobufFieldScanner.FindVarints(dataPoint, "3")
                .Should()
                .ContainSingle(metric.Name)
                .Which
                .Should()
                .Be(ToUnixNanoseconds(record.Timestamp));

            var attributes = ReadDataPointAttributes(dataPoint);
            foreach (var expectedAttribute in CreateAllowedAttributes(metric.AttributeNames))
            {
                attributes.Should().ContainKey(expectedAttribute.Key);
                attributes[expectedAttribute.Key].Should()
                    .Be(NormalizeOtlpAnyValue(expectedAttribute.Value), expectedAttribute.Key);
            }

            attributes["ninaotel.source"].Should().Be(record.Source);
            attributes.Should().NotContainKey("error_message");
        }
    }

    [Fact]
    public void Serialize_ExcludesEveryCatalogLiveObservableGauge()
    {
        var liveGaugeRecords = NinaMetricCatalog.All
            .Where(static metric => metric.ExportKind == NinaMetricExportKind.LiveObservableGauge)
            .Select(static metric => CreateMetricRecord(metric))
            .ToArray();

        var payload = OtlpPointInTimeMetricSerializer.Serialize(liveGaugeRecords);

        payload.Should().BeEmpty();
    }

    [Fact]
    public void Serialize_PreservesDeferredMetricTimestampAsOtlpTimeUnixNano()
    {
        var timestamp = DateTimeOffset.UnixEpoch
            .AddSeconds(42)
            .AddTicks(1234567);
        var expectedUnixNano = ToUnixNanoseconds(timestamp);
        var record = TelemetryRecord.Metric(
            timestamp,
            "nina.image",
            "image_mean",
            1842.5,
            TelemetryPriority.Normal,
            new Dictionary<string, object?>
            {
                ["image_file_name"] = "M42_L_001.fit",
                ["camera_name"] = "ASI2600MM",
            });

        var payload = OtlpPointInTimeMetricSerializer.Serialize([record]);

        payload.Should().NotBeEmpty();
        ProtobufFieldScanner.FindVarints(payload, "1.2.2.5.1.3")
            .Should()
            .Contain(expectedUnixNano);
    }

    [Fact]
    public void Serialize_ExcludesLiveGaugeMetricsFromPointInTimePayload()
    {
        var payload = OtlpPointInTimeMetricSerializer.Serialize(
        [
            TelemetryRecord.Metric(
                DateTimeOffset.UnixEpoch,
                "nina.camera",
                "camera_sensor_temperature",
                -7.25,
                TelemetryPriority.Normal,
                new Dictionary<string, object?>
                {
                    ["camera_name"] = "ASI2600MM",
                }),
        ]);

        payload.Should().BeEmpty();
    }

    private static ulong ToUnixNanoseconds(DateTimeOffset timestamp) =>
        (ulong)((timestamp.ToUniversalTime().Ticks - DateTimeOffset.UnixEpoch.Ticks) * 100);

    private static TelemetryRecord CreateMetricRecord(NinaMetricDefinition metric)
    {
        var attributes = CreateAllowedAttributes(metric.AttributeNames);
        attributes["error_message"] = "dropped high-cardinality text";

        return TelemetryRecord.Metric(
            CatalogMetricTimestamp,
            $"nina.{metric.Category}",
            metric.Name,
            metric.ValueKind == "integer" ? 42.0 : 42.5,
            TelemetryPriority.Normal,
            attributes);
    }

    private static Dictionary<string, object?> CreateAllowedAttributes(IEnumerable<string> attributeNames)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var attributeName in attributeNames)
        {
            attributes[attributeName] = CreateAttributeValue(attributeName);
        }

        return attributes;
    }

    private static object CreateAttributeValue(string attributeName) =>
        attributeName switch
        {
            "host_name" => true,
            "profile_name" => "imaging-profile",
            "readout_mode" => 2,
            "exposure_duration_seconds" => 180.25,
            _ => $"{attributeName}-value",
        };

    private static IReadOnlyDictionary<string, object?> ReadDataPointAttributes(byte[] dataPoint)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var attributePayload in ProtobufFieldScanner.FindMessages(dataPoint, "7"))
        {
            var key = ProtobufFieldScanner.FindStrings(attributePayload, "1")
                .Should()
                .ContainSingle()
                .Which;
            var valuePayload = ProtobufFieldScanner.FindMessages(attributePayload, "2")
                .Should()
                .ContainSingle()
                .Which;
            attributes.Add(key, ReadAnyValue(valuePayload));
        }

        return attributes;
    }

    private static object ReadAnyValue(byte[] anyValue)
    {
        var strings = ProtobufFieldScanner.FindStrings(anyValue, "1");
        if (strings.Count > 0)
        {
            return strings.Should().ContainSingle().Which;
        }

        var booleans = ProtobufFieldScanner.FindBooleans(anyValue, "2");
        if (booleans.Count > 0)
        {
            return booleans.Should().ContainSingle().Which;
        }

        var integers = ProtobufFieldScanner.FindInt64s(anyValue, "3");
        if (integers.Count > 0)
        {
            return integers.Should().ContainSingle().Which;
        }

        var doubles = ProtobufFieldScanner.FindDoubles(anyValue, "4");
        if (doubles.Count > 0)
        {
            return doubles.Should().ContainSingle().Which;
        }

        throw new InvalidOperationException("Unsupported OTLP AnyValue payload.");
    }

    private static object? NormalizeOtlpAnyValue(object? value) =>
        value switch
        {
            byte number => (long)number,
            sbyte number => (long)number,
            short number => (long)number,
            ushort number => (long)number,
            int number => (long)number,
            uint number => (long)number,
            long number => number,
            ulong number when number <= long.MaxValue => (long)number,
            float number => (double)number,
            decimal number => (double)number,
            _ => value,
        };
}
