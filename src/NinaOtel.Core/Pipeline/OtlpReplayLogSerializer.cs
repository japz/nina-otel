using NinaOtel.Abstractions.Telemetry;
using static NinaOtel.Core.Pipeline.OtlpProtoValues;

namespace NinaOtel.Core.Pipeline;

internal static class OtlpReplayLogSerializer
{
    public static byte[] Serialize(IReadOnlyList<TelemetryRecord> records)
    {
        var writer = new OtlpProtoWriter();
        writer.WriteMessage(1, resourceLogs =>
        {
            resourceLogs.WriteMessage(1, resource =>
            {
                WriteKeyValue(resource, 1, "service.name", "NinaOtel");
                WriteKeyValue(resource, 1, "service.namespace", "nina");
                WriteKeyValue(resource, 1, "telemetry.source", "ninaotel");
            });
            resourceLogs.WriteMessage(2, scopeLogs =>
            {
                scopeLogs.WriteMessage(1, scope => scope.WriteString(1, "NinaOtel"));
                foreach (var record in records)
                {
                    scopeLogs.WriteMessage(2, log => WriteLog(log, record));
                }
            });
        });

        return writer.ToArray();
    }

    private static void WriteLog(OtlpProtoWriter log, TelemetryRecord record)
    {
        var payload = OtlpLogRecordMapper.Map(record);
        log.WriteFixed64(1, ToUnixNanoseconds(record.Timestamp));
        // Map .NET levels to the first OTLP severity number in each four-level group.
        log.WriteVarint(2, 1 + 4 * (ulong)payload.Level);
        log.WriteString(3, payload.Level.ToString());
        log.WriteMessage(5, body => body.WriteString(1, payload.Message));
        foreach (var attribute in payload.Attributes)
        {
            if (attribute.Key != "{OriginalFormat}")
            {
                WriteKeyValue(log, 6, attribute.Key, attribute.Value);
            }
        }
    }
}
