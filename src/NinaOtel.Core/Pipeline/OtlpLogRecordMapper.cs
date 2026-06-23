using Microsoft.Extensions.Logging;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

internal sealed record OtlpLogRecordPayload(
    LogLevel Level,
    string Message,
    IReadOnlyList<KeyValuePair<string, object?>> Attributes);

internal static class OtlpLogRecordMapper
{
    public static OtlpLogRecordPayload Map(TelemetryRecord record)
    {
        var message = CreateLogMessage(record);
        var attributes = CreateLogAttributes(record);
        attributes.Add(new KeyValuePair<string, object?>("{OriginalFormat}", message));
        return new OtlpLogRecordPayload(MapLogLevel(record), message, attributes);
    }

    private static List<KeyValuePair<string, object?>> CreateLogAttributes(TelemetryRecord record)
    {
        var attributes = new List<KeyValuePair<string, object?>>(record.Attributes.Count + 13)
        {
            new("ninaotel.signal", record.Signal.ToString()),
            new("ninaotel.source", record.Source),
            new("ninaotel.name", record.Name),
            new("ninaotel.priority", record.Priority.ToString()),
            new("ninaotel.timestamp_unix_ms", record.Timestamp.ToUnixTimeMilliseconds()),
        };

        if (!record.Attributes.ContainsKey("name"))
        {
            attributes.Add(new KeyValuePair<string, object?>("name", record.Name));
        }

        foreach (var attribute in record.Attributes)
        {
            attributes.Add(new KeyValuePair<string, object?>(attribute.Key, attribute.Value));
        }

        if (record.NumericValue.HasValue)
        {
            attributes.Add(new KeyValuePair<string, object?>("ninaotel.numeric_value", record.NumericValue.Value));
        }

        if (record.Severity.HasValue)
        {
            attributes.Add(new KeyValuePair<string, object?>("ninaotel.severity", record.Severity.Value.ToString()));
        }

        if (record.SpanKind.HasValue)
        {
            attributes.Add(new KeyValuePair<string, object?>("ninaotel.span.kind", record.SpanKind.Value.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(record.SpanId))
        {
            attributes.Add(new KeyValuePair<string, object?>("ninaotel.span.id", record.SpanId));
        }

        if (!string.IsNullOrWhiteSpace(record.TraceId))
        {
            attributes.Add(new KeyValuePair<string, object?>("ninaotel.trace.id", record.TraceId));
        }

        if (!string.IsNullOrWhiteSpace(record.ParentSpanId))
        {
            attributes.Add(new KeyValuePair<string, object?>("ninaotel.span.parent_id", record.ParentSpanId));
        }

        return attributes;
    }

    private static string CreateLogMessage(TelemetryRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Body))
        {
            return record.Body;
        }

        return record.Signal switch
        {
            TelemetrySignal.Metric => $"{record.Source} metric {record.Name}={record.NumericValue}",
            TelemetrySignal.Span => $"{record.Source} span {record.Name} {record.SpanKind}",
            TelemetrySignal.Health => $"{record.Source} health {record.Name}",
            _ => $"{record.Source} {record.Name}",
        };
    }

    private static LogLevel MapLogLevel(TelemetryRecord record)
    {
        if (record.Severity.HasValue)
        {
            return record.Severity.Value switch
            {
                TelemetrySeverity.Trace => LogLevel.Trace,
                TelemetrySeverity.Debug => LogLevel.Debug,
                TelemetrySeverity.Information => LogLevel.Information,
                TelemetrySeverity.Warning => LogLevel.Warning,
                TelemetrySeverity.Error => LogLevel.Error,
                TelemetrySeverity.Fatal => LogLevel.Critical,
                _ => LogLevel.Information,
            };
        }

        return record.Priority switch
        {
            TelemetryPriority.Debug => LogLevel.Debug,
            TelemetryPriority.Critical => LogLevel.Error,
            TelemetryPriority.Important => LogLevel.Warning,
            _ => LogLevel.Information,
        };
    }
}
