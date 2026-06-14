using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

public interface ITelemetryExporter
{
    Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken);
}
