using System.Security.Cryptography;
using System.Text;

namespace NinaOtel.Core.Pipeline;

internal static class OtlpTraceSerializer
{
    private const string ScopeName = "NinaOtel.Traces";
    private const int SpanKindInternal = 1;

    public static byte[] Serialize(IReadOnlyList<OtlpCompletedSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(spans);

        if (spans.Count == 0)
        {
            return [];
        }

        var writer = new ProtoWriter();
        writer.WriteMessage(1, resourceSpans =>
        {
            resourceSpans.WriteMessage(1, resource =>
            {
                WriteKeyValue(resource, 1, "service.name", "NinaOtel");
                WriteKeyValue(resource, 1, "service.namespace", "nina");
                WriteKeyValue(resource, 1, "telemetry.source", "ninaotel");
            });
            resourceSpans.WriteMessage(2, scopeSpans =>
            {
                scopeSpans.WriteMessage(1, scope => scope.WriteString(1, ScopeName));
                foreach (var span in spans)
                {
                    scopeSpans.WriteMessage(2, traceSpan => WriteSpan(traceSpan, span));
                }
            });
        });

        return writer.ToArray();
    }

    private static void WriteSpan(ProtoWriter writer, OtlpCompletedSpan span)
    {
        writer.WriteBytes(1, CreateIdentifier(span.TraceIdSeed ?? span.ParentSpanId ?? span.SpanId, length: 16));
        writer.WriteBytes(2, CreateIdentifier(span.SpanId, length: 8));
        if (!string.IsNullOrWhiteSpace(span.ParentSpanId))
        {
            writer.WriteBytes(4, CreateIdentifier(span.ParentSpanId, length: 8));
        }

        writer.WriteString(5, span.Name);
        writer.WriteVarint(6, SpanKindInternal);
        writer.WriteFixed64(7, ToUnixNanoseconds(span.StartTimestamp));
        writer.WriteFixed64(8, ToUnixNanoseconds(span.EndTimestamp));

        foreach (var attribute in CreateAttributes(span))
        {
            WriteKeyValue(writer, 9, attribute.Key, attribute.Value);
        }

        foreach (var spanEvent in span.Events)
        {
            writer.WriteMessage(11, eventWriter => WriteSpanEvent(eventWriter, spanEvent));
        }
    }

    private static void WriteSpanEvent(ProtoWriter writer, OtlpCompletedSpanEvent spanEvent)
    {
        writer.WriteFixed64(1, ToUnixNanoseconds(spanEvent.Timestamp));
        writer.WriteString(2, spanEvent.Name);
        foreach (var attribute in spanEvent.Attributes.OrderBy(static attribute => attribute.Key, StringComparer.Ordinal))
        {
            WriteKeyValue(writer, 3, attribute.Key, attribute.Value);
        }
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> CreateAttributes(OtlpCompletedSpan span)
    {
        var attributes = new List<KeyValuePair<string, object?>>(span.Attributes.Count + 1);
        foreach (var attribute in span.Attributes.OrderBy(static attribute => attribute.Key, StringComparer.Ordinal))
        {
            if (attribute.Value is not null)
            {
                attributes.Add(new KeyValuePair<string, object?>(attribute.Key, attribute.Value));
            }
        }

        attributes.Add(new KeyValuePair<string, object?>("ninaotel.source", span.Source));
        return attributes;
    }

    private static byte[] CreateIdentifier(string seed, int length)
    {
        var trimmed = seed.Trim();
        if (TryParseHex(trimmed, length, out var parsed))
        {
            return EnsureNonZero(parsed);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
        var bytes = hash.Take(length).ToArray();
        return EnsureNonZero(bytes);
    }

    private static bool TryParseHex(string value, int length, out byte[] bytes)
    {
        bytes = [];
        if (value.Length != length * 2)
        {
            return false;
        }

        var parsed = new byte[length];
        for (var i = 0; i < parsed.Length; i++)
        {
            var high = FromHex(value[i * 2]);
            var low = FromHex(value[(i * 2) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            parsed[i] = (byte)((high << 4) | low);
        }

        bytes = parsed;
        return true;
    }

    private static int FromHex(char value) =>
        value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1,
        };

    private static byte[] EnsureNonZero(byte[] bytes)
    {
        if (bytes.Any(static value => value != 0))
        {
            return bytes;
        }

        bytes[^1] = 1;
        return bytes;
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

        public void WriteBytes(int fieldNumber, byte[] value)
        {
            WriteTag(fieldNumber, LengthDelimitedWireType);
            WriteRawVarint((ulong)value.Length);
            buffer.AddRange(value);
        }

        public void WriteDouble(int fieldNumber, double value)
        {
            WriteTag(fieldNumber, Fixed64WireType);
            var bits = BitConverter.DoubleToUInt64Bits(value);
            WriteLittleEndianUInt64(bits);
        }

        public void WriteFixed64(int fieldNumber, ulong value)
        {
            WriteTag(fieldNumber, Fixed64WireType);
            WriteLittleEndianUInt64(value);
        }

        private void WriteLittleEndianUInt64(ulong value)
        {
            for (var shift = 0; shift < 64; shift += 8)
            {
                buffer.Add((byte)(value >> shift));
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
