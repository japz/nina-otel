namespace NinaOtel.Abstractions.Telemetry;

public enum TelemetrySignal
{
    Metric,
    Log,
    Span,
    Health,
}

public enum TelemetryPriority
{
    Debug = 0,
    Routine = 1,
    Normal = 2,
    Important = 3,
    Critical = 4,
}

public enum TelemetrySeverity
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Fatal,
}

public enum SpanEventKind
{
    Start,
    Event,
    Stop,
}

public sealed record TelemetryRecord(
    TelemetrySignal Signal,
    DateTimeOffset Timestamp,
    string Source,
    string Name,
    TelemetryPriority Priority,
    IReadOnlyDictionary<string, object?> Attributes,
    double? NumericValue = null,
    string? Body = null,
    TelemetrySeverity? Severity = null,
    SpanEventKind? SpanKind = null,
    string? SpanId = null,
    string? ParentSpanId = null)
{
    public static TelemetryRecord Metric(
        DateTimeOffset timestamp,
        string source,
        string name,
        double value,
        TelemetryPriority priority,
        IReadOnlyDictionary<string, object?>? attributes = null)
        => new(
            TelemetrySignal.Metric,
            timestamp,
            source,
            name,
            priority,
            attributes ?? EmptyAttributes,
            NumericValue: value);

    public static TelemetryRecord Log(
        DateTimeOffset timestamp,
        string source,
        TelemetrySeverity severity,
        string body,
        TelemetryPriority priority,
        IReadOnlyDictionary<string, object?>? attributes = null)
        => new(
            TelemetrySignal.Log,
            timestamp,
            source,
            "log",
            priority,
            attributes ?? EmptyAttributes,
            Body: body,
            Severity: severity);

    public static TelemetryRecord Health(
        DateTimeOffset timestamp,
        string source,
        string name,
        TelemetryPriority priority,
        IReadOnlyDictionary<string, object?>? attributes = null)
        => new(
            TelemetrySignal.Health,
            timestamp,
            source,
            name,
            priority,
            attributes ?? EmptyAttributes);

    public static TelemetryRecord Span(
        DateTimeOffset timestamp,
        string source,
        string name,
        SpanEventKind kind,
        string spanId,
        TelemetryPriority priority,
        IReadOnlyDictionary<string, object?>? attributes = null,
        string? parentSpanId = null)
        => new(
            TelemetrySignal.Span,
            timestamp,
            source,
            name,
            priority,
            attributes ?? EmptyAttributes,
            SpanKind: kind,
            SpanId: spanId,
            ParentSpanId: parentSpanId);

    private static readonly IReadOnlyDictionary<string, object?> EmptyAttributes =
        new Dictionary<string, object?>();
}
