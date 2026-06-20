using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Pipeline;
using NinaOtel.Core.Telemetry;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class OtlpPointInTimeMetricSerializerTests
{
    [Fact]
    public void Serialize_WritesNonEmptyPayloadForEveryCatalogDeferredPointInTimeMetric()
    {
        var deferredMetrics = NinaMetricCatalog.All
            .Where(static metric => metric.ExportKind == NinaMetricExportKind.DeferredPointInTime)
            .ToArray();

        foreach (var metric in deferredMetrics)
        {
            var payload = OtlpPointInTimeMetricSerializer.Serialize([CreateMetricRecord(metric)]);

            payload.Should().NotBeEmpty(metric.Name);
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

    private static TelemetryRecord CreateMetricRecord(NinaMetricDefinition metric) =>
        TelemetryRecord.Metric(
            DateTimeOffset.UnixEpoch,
            $"nina.{metric.Category}",
            metric.Name,
            42.5,
            TelemetryPriority.Normal,
            metric.AttributeNames.ToDictionary(
                static attributeName => attributeName,
                static attributeName => (object?)$"{attributeName}-value",
                StringComparer.Ordinal));

    private static class ProtobufFieldScanner
    {
        public static IReadOnlyList<ulong> FindVarints(byte[] payload, string path)
        {
            var values = new List<ulong>();
            Scan(payload, string.Empty, path, values, depth: 0);
            return values;
        }

        private static void Scan(
            ReadOnlySpan<byte> payload,
            string currentPath,
            string targetPath,
            List<ulong> values,
            int depth)
        {
            if (depth > 16)
            {
                return;
            }

            var offset = 0;
            while (offset < payload.Length)
            {
                var key = ReadVarint(payload, ref offset);
                var fieldNumber = key >> 3;
                var wireType = key & 0x7;
                var fieldPath = string.IsNullOrEmpty(currentPath)
                    ? fieldNumber.ToString()
                    : $"{currentPath}.{fieldNumber}";

                switch (wireType)
                {
                    case 0:
                        var value = ReadVarint(payload, ref offset);
                        if (fieldPath == targetPath)
                        {
                            values.Add(value);
                        }
                        break;
                    case 1:
                        if (offset + sizeof(ulong) > payload.Length)
                        {
                            return;
                        }

                        offset += sizeof(ulong);
                        break;
                    case 2:
                        var length = checked((int)ReadVarint(payload, ref offset));
                        if (length < 0 || offset + length > payload.Length)
                        {
                            return;
                        }

                        var child = payload.Slice(offset, length);
                        try
                        {
                            Scan(child, fieldPath, targetPath, values, depth + 1);
                        }
                        catch (InvalidOperationException)
                        {
                        }

                        offset += length;
                        break;
                    default:
                        return;
                }
            }
        }

        private static ulong ReadVarint(ReadOnlySpan<byte> payload, ref int offset)
        {
            ulong result = 0;
            var shift = 0;
            while (offset < payload.Length)
            {
                var value = payload[offset++];
                result |= (ulong)(value & 0x7F) << shift;
                if ((value & 0x80) == 0)
                {
                    return result;
                }

                shift += 7;
            }

            throw new InvalidOperationException("Truncated protobuf varint.");
        }
    }
}
