using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Telemetry;
using static NinaOtel.Core.Pipeline.OtlpProtoValues;

namespace NinaOtel.Core.Pipeline;

internal static class OtlpPointInTimeMetricSerializer
{
    private const string MeterName = "NinaOtel.Metrics";
    private static readonly HashSet<string> LegacyPhd2Attributes = new(StringComparer.Ordinal)
    {
        "addon.id", "guider_name", "source.file", "phd2.session_start",
    };

    public static byte[] Serialize(IReadOnlyList<TelemetryRecord> records, bool includeLiveGauges = false)
    {
        ArgumentNullException.ThrowIfNull(records);

        var pointInTimeRecords = records
            .Where(record => IsPointInTimeMetric(record, includeLiveGauges))
            .GroupBy(static record => record.Name, StringComparer.Ordinal)
            .ToArray();

        if (pointInTimeRecords.Length == 0)
        {
            return [];
        }

        var writer = new OtlpProtoWriter();
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

    private static bool IsPointInTimeMetric(TelemetryRecord record, bool includeLiveGauges)
    {
        return record.Signal == TelemetrySignal.Metric &&
            record.NumericValue is { } value &&
            !double.IsNaN(value) &&
            (NinaMetricCatalog.TryGetExportKind(record.Name, out var exportKind)
                ? includeLiveGauges || exportKind == NinaMetricExportKind.DeferredPointInTime
                : includeLiveGauges && IsLegacyPhd2Metric(record.Name));
    }

    private static bool IsLegacyPhd2Metric(string name) => name is
        "phd2_guide_rms_ra_arcsec" or "phd2_guide_rms_dec_arcsec" or "phd2_guide_rms_total_arcsec";

    private static void WriteNumberDataPoint(OtlpProtoWriter point, TelemetryRecord record)
    {
        foreach (var attribute in CreateTags(record))
        {
            WriteKeyValue(point, 7, attribute.Key, attribute.Value);
        }

        point.WriteFixed64(3, ToUnixNanoseconds(record.Timestamp));
        point.WriteDouble(4, record.NumericValue!.Value);
    }

    private static IReadOnlyList<KeyValuePair<string, object?>> CreateTags(TelemetryRecord record)
    {
        var allowedAttributes = NinaMetricCatalog.TryGetExportKind(record.Name, out var kind)
            ? NinaMetricCatalog.GetMetricAttributeNames(record.Name, kind)
            : LegacyPhd2Attributes;
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
}
