using System.Text;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Telemetry;

namespace NinaOtel.Core.Pipeline;

internal static class OtlpPointInTimeMetricSerializer
{
    private const string MeterName = "NinaOtel.Metrics";

    public static byte[] Serialize(IReadOnlyList<TelemetryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        var pointInTimeRecords = records
            .Where(IsDeferredPointInTimeMetric)
            .GroupBy(static record => record.Name, StringComparer.Ordinal)
            .ToArray();

        if (pointInTimeRecords.Length == 0)
        {
            return [];
        }

        var writer = new ProtoWriter();
        writer.WriteMessage(1, resourceMetrics =>
        {
            resourceMetrics.WriteMessage(1, resource =>
            {
                WriteKeyValue(resource, 1, "service.name", "NinaOtel");
                WriteKeyValue(resource, 1, "service.namespace", "nina");
                WriteKeyValue(resource, 1, "telemetry.source", "ninaotel");
            });
            resourceMetrics.WriteMessage(2, scopeMetrics =>
            {
                scopeMetrics.WriteMessage(1, scope => scope.WriteString(1, MeterName));
                foreach (var metricGroup in pointInTimeRecords)
                {
                    scopeMetrics.WriteMessage(2, metric =>
                    {
                        metric.WriteString(1, metricGroup.Key);
                        metric.WriteMessage(5, gauge =>
                        {
                            foreach (var record in metricGroup)
                            {
                                gauge.WriteMessage(1, point => WriteNumberDataPoint(point, record));
                            }
                        });
                    });
                }
            });
        });

        return writer.ToArray();
    }

    private static bool IsDeferredPointInTimeMetric(TelemetryRecord record)
    {
        return record.Signal == TelemetrySignal.Metric &&
            record.NumericValue is { } value &&
            !double.IsNaN(value) &&
            NinaMetricCatalog.TryGetExportKind(record.Name, out var exportKind) &&
            exportKind == NinaMetricExportKind.DeferredPointInTime;
    }

    private static void WriteNumberDataPoint(ProtoWriter point, TelemetryRecord record)
    {
        foreach (var attribute in CreateTags(record))
        {
            WriteKeyValue(point, 7, attribute.Key, attribute.Value);
        }

        point.WriteVarint(3, ToUnixNanoseconds(record.Timestamp));
        point.WriteDouble(4, record.NumericValue!.Value);
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> CreateTags(TelemetryRecord record)
    {
        var allowedAttributes = NinaMetricCatalog.GetMetricAttributeNames(
            record.Name,
            NinaMetricExportKind.DeferredPointInTime);
        if (allowedAttributes is null)
        {
            return Array.AsReadOnly(Array.Empty<KeyValuePair<string, object?>>());
        }

        var tags = new List<KeyValuePair<string, object?>>(record.Attributes.Count + 1);
        foreach (var attribute in record.Attributes.OrderBy(static attribute => attribute.Key, StringComparer.Ordinal))
        {
            if (attribute.Value is not null && allowedAttributes.Contains(attribute.Key))
            {
                tags.Add(new KeyValuePair<string, object?>(attribute.Key, attribute.Value));
            }
        }

        tags.Add(new KeyValuePair<string, object?>("ninaotel.source", record.Source));
        return tags.AsReadOnly();
    }

    private static void WriteKeyValue(ProtoWriter writer, int fieldNumber, string key, object? value)
    {
        if (value is null)
        {
            return;
        }

        writer.WriteMessage(fieldNumber, keyValue =>
        {
            keyValue.WriteString(1, key);
            keyValue.WriteMessage(2, anyValue => WriteAnyValue(anyValue, value));
        });
    }

    private static void WriteAnyValue(ProtoWriter writer, object value)
    {
        switch (value)
        {
            case string text:
                writer.WriteString(1, text);
                break;
            case bool boolean:
                writer.WriteBool(2, boolean);
                break;
            case byte number:
                writer.WriteSignedVarint(3, number);
                break;
            case sbyte number:
                writer.WriteSignedVarint(3, number);
                break;
            case short number:
                writer.WriteSignedVarint(3, number);
                break;
            case ushort number:
                writer.WriteSignedVarint(3, number);
                break;
            case int number:
                writer.WriteSignedVarint(3, number);
                break;
            case uint number:
                writer.WriteSignedVarint(3, number);
                break;
            case long number:
                writer.WriteSignedVarint(3, number);
                break;
            case ulong number when number <= long.MaxValue:
                writer.WriteSignedVarint(3, (long)number);
                break;
            case float number:
                writer.WriteDouble(4, number);
                break;
            case double number:
                writer.WriteDouble(4, number);
                break;
            case decimal number:
                writer.WriteDouble(4, (double)number);
                break;
            default:
                writer.WriteString(1, value.ToString() ?? string.Empty);
                break;
        }
    }

    private static ulong ToUnixNanoseconds(DateTimeOffset timestamp) =>
        (ulong)((timestamp.ToUniversalTime().Ticks - DateTimeOffset.UnixEpoch.Ticks) * 100);

    private sealed class ProtoWriter
    {
        private const int VarintWireType = 0;
        private const int Fixed64WireType = 1;
        private const int LengthDelimitedWireType = 2;

        private readonly List<byte> buffer = [];

        public byte[] ToArray() => [.. buffer];

        public void WriteBool(int fieldNumber, bool value) =>
            WriteVarint(fieldNumber, value ? 1UL : 0UL);

        public void WriteDouble(int fieldNumber, double value)
        {
            WriteTag(fieldNumber, Fixed64WireType);
            var bits = BitConverter.DoubleToUInt64Bits(value);
            for (var shift = 0; shift < 64; shift += 8)
            {
                buffer.Add((byte)(bits >> shift));
            }
        }

        public void WriteMessage(int fieldNumber, Action<ProtoWriter> write)
        {
            var child = new ProtoWriter();
            write(child);
            var payload = child.ToArray();
            WriteTag(fieldNumber, LengthDelimitedWireType);
            WriteRawVarint((ulong)payload.Length);
            buffer.AddRange(payload);
        }

        public void WriteSignedVarint(int fieldNumber, long value) =>
            WriteVarint(fieldNumber, unchecked((ulong)value));

        public void WriteString(int fieldNumber, string value)
        {
            var payload = Encoding.UTF8.GetBytes(value);
            WriteTag(fieldNumber, LengthDelimitedWireType);
            WriteRawVarint((ulong)payload.Length);
            buffer.AddRange(payload);
        }

        public void WriteVarint(int fieldNumber, ulong value)
        {
            WriteTag(fieldNumber, VarintWireType);
            WriteRawVarint(value);
        }

        private void WriteTag(int fieldNumber, int wireType) =>
            WriteRawVarint(((ulong)fieldNumber << 3) | (uint)wireType);

        private void WriteRawVarint(ulong value)
        {
            while (value >= 0x80)
            {
                buffer.Add((byte)(value | 0x80));
                value >>= 7;
            }

            buffer.Add((byte)value);
        }
    }
}
