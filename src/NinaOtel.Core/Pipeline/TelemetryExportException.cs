namespace NinaOtel.Core.Pipeline;

public sealed class TelemetryExportException : Exception
{
    public TelemetryExportException(string message)
        : base(message)
    {
    }

    public TelemetryExportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
