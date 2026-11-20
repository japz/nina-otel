using System.Security.Cryptography;
using System.Text;
using static NinaOtel.Core.Pipeline.OtlpProtoValues;

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

        var writer = new OtlpProtoWriter();
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

    private static void WriteSpan(OtlpProtoWriter writer, OtlpCompletedSpan span)
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

        if (span.DroppedEventCount > 0)
        {
            writer.WriteVarint(12, (uint)span.DroppedEventCount);
        }
    }

    private static void WriteSpanEvent(OtlpProtoWriter writer, OtlpCompletedSpanEvent spanEvent)
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
}
