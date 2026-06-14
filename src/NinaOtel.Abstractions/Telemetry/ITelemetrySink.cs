namespace NinaOtel.Abstractions.Telemetry;

public interface ITelemetrySink
{
    bool TryPublish(TelemetryRecord record);
}
