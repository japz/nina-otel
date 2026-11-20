namespace NinaOtel.Core.Pipeline;

public sealed class TelemetryExportException : Exception
{
    public TelemetryExportException(string message)
        : base(message)
    {
    }

    internal TelemetryExportException(string message, bool isPermanent)
        : base(message)
    {
        IsPermanent = isPermanent;
    }

    internal bool IsPermanent { get; }

    public TelemetryExportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
