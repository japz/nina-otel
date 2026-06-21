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
        string? errorMessage,
        CollectorBufferMode bufferMode,
        int queuedRecords,
        long queuedBytes,
        DateTimeOffset? oldestQueuedTimestamp,
        int droppedRecords)
    {
        State = state;
        Endpoint = endpoint;
        Protocol = protocol;
        CheckedAt = checkedAt;
        ExportedRecords = exportedRecords;
        ErrorType = errorType;
        ErrorMessage = errorMessage;
        BufferMode = bufferMode;
        QueuedRecords = queuedRecords;
        QueuedBytes = queuedBytes;
        OldestQueuedTimestamp = oldestQueuedTimestamp;
        DroppedRecords = droppedRecords;
    }

    public CollectorHealthState State { get; }
    public Uri Endpoint { get; }
    public OtlpProtocol Protocol { get; }
    public DateTimeOffset CheckedAt { get; }
    public int ExportedRecords { get; }
    public string? ErrorType { get; }
    public string? ErrorMessage { get; }
    public CollectorBufferMode BufferMode { get; }
    public int QueuedRecords { get; }
    public long QueuedBytes { get; }
    public DateTimeOffset? OldestQueuedTimestamp { get; }
    public int DroppedRecords { get; }

    public static CollectorHealthSnapshot Healthy(
        Uri endpoint,
        OtlpProtocol protocol,
        int exportedRecords,
        DateTimeOffset checkedAt,
        CollectorBufferMode bufferMode = CollectorBufferMode.Healthy,
        int queuedRecords = 0,
        long queuedBytes = 0,
        DateTimeOffset? oldestQueuedTimestamp = null,
        int droppedRecords = 0) =>
        new(
            CollectorHealthState.Healthy,
            endpoint,
            protocol,
            checkedAt,
            exportedRecords,
            errorType: null,
            errorMessage: null,
            bufferMode,
            queuedRecords,
            queuedBytes,
            oldestQueuedTimestamp,
            droppedRecords);

    public static CollectorHealthSnapshot Unhealthy(
        Uri endpoint,
        OtlpProtocol protocol,
        string errorType,
        string errorMessage,
        DateTimeOffset checkedAt,
        int exportedRecords = 0,
        CollectorBufferMode bufferMode = CollectorBufferMode.Unknown,
        int queuedRecords = 0,
        long queuedBytes = 0,
        DateTimeOffset? oldestQueuedTimestamp = null,
        int droppedRecords = 0) =>
        new(
            CollectorHealthState.Unhealthy,
            endpoint,
            protocol,
            checkedAt,
            exportedRecords,
            errorType,
            errorMessage,
            bufferMode,
            queuedRecords,
            queuedBytes,
            oldestQueuedTimestamp,
            droppedRecords);
}
