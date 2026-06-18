using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

internal sealed record OtlpCompletedSpan(
    string Source,
    string Name,
    string SpanId,
    string? ParentSpanId,
    DateTimeOffset StartTimestamp,
    DateTimeOffset EndTimestamp,
    IReadOnlyDictionary<string, object?> Attributes,
    IReadOnlyList<OtlpCompletedSpanEvent> Events,
    string? TraceIdSeed = null);

internal sealed record OtlpCompletedSpanEvent(
    DateTimeOffset Timestamp,
    string Name,
    IReadOnlyDictionary<string, object?> Attributes);

internal sealed class OtlpTraceStateStore
{
    private const int DefaultMaxActiveSpans = 1024;
    private const int DefaultMaxCompletedSpans = 1024;

    private readonly int maxActiveSpans;
    private readonly int maxCompletedSpans;
    private readonly Dictionary<ActiveSpanKey, ActiveSpan> activeSpans = [];
    private readonly List<OtlpCompletedSpan> completedSpans = [];
    private readonly HashSet<CompletedSpanKey> completedSpanKeys = [];

    public OtlpTraceStateStore()
        : this(DefaultMaxActiveSpans, DefaultMaxCompletedSpans)
    {
    }

    internal OtlpTraceStateStore(int maxActiveSpans, int maxCompletedSpans)
    {
        if (maxActiveSpans <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxActiveSpans), maxActiveSpans, "Maximum active spans must be positive.");
        }

        if (maxCompletedSpans <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCompletedSpans), maxCompletedSpans, "Maximum completed spans must be positive.");
        }

        this.maxActiveSpans = maxActiveSpans;
        this.maxCompletedSpans = maxCompletedSpans;
    }

    internal int ActiveSpanCount => activeSpans.Count;

    public IReadOnlyList<OtlpCompletedSpan> Apply(IReadOnlyList<TelemetryRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        foreach (var record in records)
        {
            if (record.Signal != TelemetrySignal.Span ||
                string.IsNullOrWhiteSpace(record.SpanId) ||
                record.SpanKind is null)
            {
                continue;
            }

            switch (record.SpanKind.Value)
            {
                case SpanEventKind.Start:
                    StartSpan(record);
                    break;
                case SpanEventKind.Event:
                    AddSpanEvent(record);
                    break;
                case SpanEventKind.Stop:
                    AddCompletedSpan(StopSpan(record));
                    break;
            }
        }

        return completedSpans.ToArray();
    }

    public void MarkExportSucceeded(int exportedSpanCount)
    {
        if (exportedSpanCount <= 0 || completedSpans.Count == 0)
        {
            return;
        }

        var removeCount = Math.Min(exportedSpanCount, completedSpans.Count);
        RemoveCompletedSpanRange(0, removeCount);
    }

    private void StartSpan(TelemetryRecord record)
    {
        var spanId = NormalizeId(record.SpanId)!;
        var parentSpanId = NormalizeId(record.ParentSpanId);
        var explicitTraceId = NormalizeId(record.TraceId);
        var traceIdSeed = ResolveTraceIdSeed(record.Source, spanId, parentSpanId, explicitTraceId, record.Timestamp);
        var key = new ActiveSpanKey(record.Source, spanId, traceIdSeed);

        activeSpans[key] = new ActiveSpan(
            record.Source,
            record.Name,
            spanId,
            parentSpanId,
            traceIdSeed,
            explicitTraceId is not null,
            record.Timestamp,
            SnapshotAttributes(record.Attributes),
            []);
        TrimActiveSpansToLimit();
    }

    private void AddSpanEvent(TelemetryRecord record)
    {
        if (!TryFindActiveSpan(
                record.Source,
                NormalizeId(record.SpanId)!,
                NormalizeId(record.TraceId),
                NormalizeId(record.ParentSpanId),
                out _,
                out var span))
        {
            return;
        }

        span.Events.Add(new OtlpCompletedSpanEvent(
            record.Timestamp,
            record.Name,
            SnapshotAttributes(record.Attributes)));
    }

    private OtlpCompletedSpan? StopSpan(TelemetryRecord record)
    {
        var spanId = NormalizeId(record.SpanId)!;
        var parentSpanId = NormalizeId(record.ParentSpanId);
        var traceId = NormalizeId(record.TraceId);

        if (!TryRemoveActiveSpan(record.Source, spanId, traceId, parentSpanId, out var span))
        {
            if (HasActiveSpan(record.Source, spanId, parentSpanId))
            {
                return null;
            }

            return new OtlpCompletedSpan(
                record.Source,
                record.Name,
                spanId,
                parentSpanId,
                record.Timestamp,
                record.Timestamp,
                SnapshotAttributes(record.Attributes),
                [],
                traceId ?? parentSpanId ?? CreateSyntheticRootTraceSeed(record.Source, spanId, record.Timestamp));
        }

        var attributes = new Dictionary<string, object?>(span.Attributes, StringComparer.Ordinal);
        foreach (var attribute in record.Attributes)
        {
            attributes[attribute.Key] = attribute.Value;
        }

        return new OtlpCompletedSpan(
            span.Source,
            span.Name,
            span.SpanId,
            span.ParentSpanId,
            span.StartTimestamp,
            record.Timestamp,
            attributes,
            span.Events.ToArray(),
            traceId ?? span.TraceIdSeed);
    }

    private string ResolveTraceIdSeed(
        string source,
        string spanId,
        string? parentSpanId,
        string? explicitTraceId,
        DateTimeOffset timestamp)
    {
        if (explicitTraceId is not null)
        {
            return explicitTraceId;
        }

        if (parentSpanId is not null &&
            TryFindActiveSpan(source, parentSpanId, traceId: null, parentSpanId: null, out _, out var parent))
        {
            return parent.TraceIdSeed;
        }

        return parentSpanId ?? CreateSyntheticRootTraceSeed(source, spanId, timestamp);
    }

    private bool TryRemoveActiveSpan(
        string source,
        string spanId,
        string? traceId,
        string? parentSpanId,
        out ActiveSpan span)
    {
        if (TryFindActiveSpan(source, spanId, traceId, parentSpanId, out var key, out span))
        {
            activeSpans.Remove(key);
            return true;
        }

        return false;
    }

    private bool TryFindActiveSpan(
        string source,
        string spanId,
        string? traceId,
        string? parentSpanId,
        out ActiveSpanKey key,
        out ActiveSpan span)
    {
        if (traceId is not null)
        {
            key = new ActiveSpanKey(source, spanId, traceId);
            if (activeSpans.TryGetValue(key, out span!))
            {
                return true;
            }

            var fallbackMatches = GetActiveSpanMatches(source, spanId, parentSpanId)
                .Take(2)
                .ToArray();
            if (fallbackMatches.Length == 1 && !fallbackMatches[0].Value.TraceIdWasExplicit)
            {
                key = fallbackMatches[0].Key;
                span = fallbackMatches[0].Value;
                return true;
            }

            return false;
        }

        var matches = GetActiveSpanMatches(source, spanId, parentSpanId)
            .Take(2)
            .ToArray();

        if (matches.Length == 1)
        {
            key = matches[0].Key;
            span = matches[0].Value;
            return true;
        }

        key = default;
        span = null!;
        return false;
    }

    private bool HasActiveSpan(string source, string spanId, string? parentSpanId) =>
        GetActiveSpanMatches(source, spanId, parentSpanId).Any();

    private IEnumerable<KeyValuePair<ActiveSpanKey, ActiveSpan>> GetActiveSpanMatches(
        string source,
        string spanId,
        string? parentSpanId) =>
        activeSpans
            .Where(candidate =>
                string.Equals(candidate.Key.Source, source, StringComparison.Ordinal) &&
                string.Equals(candidate.Key.SpanId, spanId, StringComparison.Ordinal) &&
                (parentSpanId is null || string.Equals(candidate.Value.ParentSpanId, parentSpanId, StringComparison.Ordinal)))
            .OrderBy(static candidate => candidate.Value.StartTimestamp);

    private void AddCompletedSpan(OtlpCompletedSpan? span)
    {
        if (span is null)
        {
            return;
        }

        var key = CompletedSpanKey.Create(span);
        if (!completedSpanKeys.Add(key))
        {
            return;
        }

        completedSpans.Add(span);
        TrimCompletedSpansToLimit();
    }

    private void TrimActiveSpansToLimit()
    {
        var removeCount = activeSpans.Count - maxActiveSpans;
        if (removeCount <= 0)
        {
            return;
        }

        foreach (var key in activeSpans
            .OrderBy(static activeSpan => activeSpan.Value.StartTimestamp)
            .Take(removeCount)
            .Select(static activeSpan => activeSpan.Key)
            .ToArray())
        {
            activeSpans.Remove(key);
        }
    }

    private void TrimCompletedSpansToLimit()
    {
        var removeCount = completedSpans.Count - maxCompletedSpans;
        if (removeCount > 0)
        {
            RemoveCompletedSpanRange(0, removeCount);
        }
    }

    private void RemoveCompletedSpanRange(int index, int count)
    {
        if (count <= 0)
        {
            return;
        }

        for (var offset = 0; offset < count; offset++)
        {
            completedSpanKeys.Remove(CompletedSpanKey.Create(completedSpans[index + offset]));
        }

        completedSpans.RemoveRange(index, count);
    }

    private static string? NormalizeId(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();

    private static string CreateSyntheticRootTraceSeed(
        string source,
        string spanId,
        DateTimeOffset timestamp) =>
        $"{source}|{spanId}|{timestamp.ToUniversalTime():O}";

    private static IReadOnlyDictionary<string, object?> SnapshotAttributes(
        IReadOnlyDictionary<string, object?> attributes) =>
        new Dictionary<string, object?>(attributes, StringComparer.Ordinal);

    private readonly record struct ActiveSpanKey(
        string Source,
        string SpanId,
        string TraceIdSeed);

    private readonly record struct CompletedSpanKey(
        string Source,
        string SpanId,
        string? TraceIdSeed,
        DateTimeOffset StartTimestamp,
        DateTimeOffset EndTimestamp)
    {
        public static CompletedSpanKey Create(OtlpCompletedSpan span) =>
            new(span.Source, span.SpanId, span.TraceIdSeed, span.StartTimestamp, span.EndTimestamp);
    }

    private sealed record ActiveSpan(
        string Source,
        string Name,
        string SpanId,
        string? ParentSpanId,
        string TraceIdSeed,
        bool TraceIdWasExplicit,
        DateTimeOffset StartTimestamp,
        IReadOnlyDictionary<string, object?> Attributes,
        List<OtlpCompletedSpanEvent> Events);
}
