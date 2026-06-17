using NinaOtel.Core.Options;

namespace NinaOtel.Core.Health;

public sealed record CollectorHealthSnapshot
{
    private CollectorHealthSnapshot(
        CollectorHealthState state,
        Uri endpoint,
        OtlpProtocol protocol,
        DateTimeOffset checkedAt,
        int exportedRecords,
        string? errorType,
        string? errorMessage)
    {
        State = state;
        Endpoint = endpoint;
        Protocol = protocol;
        CheckedAt = checkedAt;
        ExportedRecords = exportedRecords;
        ErrorType = errorType;
        ErrorMessage = errorMessage;
    }

    public CollectorHealthState State { get; }
    public Uri Endpoint { get; }
    public OtlpProtocol Protocol { get; }
    public DateTimeOffset CheckedAt { get; }
    public int ExportedRecords { get; }
    public string? ErrorType { get; }
    public string? ErrorMessage { get; }

    public static CollectorHealthSnapshot Healthy(
        Uri endpoint,
        OtlpProtocol protocol,
        int exportedRecords,
        DateTimeOffset checkedAt) =>
        new(
            CollectorHealthState.Healthy,
            endpoint,
            protocol,
            checkedAt,
            exportedRecords,
            errorType: null,
            errorMessage: null);

    public static CollectorHealthSnapshot Unhealthy(
        Uri endpoint,
        OtlpProtocol protocol,
        string errorType,
        string errorMessage,
        DateTimeOffset checkedAt,
        int exportedRecords = 0) =>
        new(
            CollectorHealthState.Unhealthy,
            endpoint,
            protocol,
            checkedAt,
            exportedRecords,
            errorType,
            errorMessage);
}
