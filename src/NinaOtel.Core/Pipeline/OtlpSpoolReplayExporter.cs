using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Options;

namespace NinaOtel.Core.Pipeline;

internal sealed class OtlpSpoolReplayExporter : ISpoolReplayExporter
{
    private readonly OtlpDirectTransport metrics;
    private readonly OtlpDirectTransport logs;
    private readonly OtlpDirectTransport traces;
    private readonly OtlpTraceStateStore traceState = new(maxActiveSpans: 1024, maxCompletedSpans: 1024, maxEventsPerSpan: 1024);
    private DiskTelemetrySpool.TraceCheckpoint? traceCheckpoint;
    private bool loadedTraceCheckpoint;
    private bool unsavedTraceCheckpoint;

    public OtlpSpoolReplayExporter(OtlpOptions options, Func<HttpClient>? httpClientFactory = null)
    {
        metrics = new(options, "metrics", httpClientFactory);
        logs = new(options, "logs", httpClientFactory);
        traces = new(options, "traces", httpClientFactory);
    }

    public Task ExportAsync(SpoolSignal signal, DiskTelemetrySpool.Batch batch, CancellationToken cancellationToken) =>
        signal switch
        {
            SpoolSignal.Metrics => metrics.SendAsync(
                OtlpPointInTimeMetricSerializer.Serialize(batch.Records, includeLiveGauges: true), cancellationToken),
            SpoolSignal.Logs => logs.SendAsync(
                OtlpReplayLogSerializer.Serialize(batch.Records.Where(record => record.Signal != TelemetrySignal.Metric).ToArray()),
                cancellationToken),
            SpoolSignal.Traces => ExportTracesAsync(batch, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(signal)),
        };

    private async Task ExportTracesAsync(DiskTelemetrySpool.Batch batch, CancellationToken cancellationToken)
    {
        if (!loadedTraceCheckpoint)
        {
            traceCheckpoint = await DiskTelemetrySpool.TraceCheckpoint.LoadAsync(batch, cancellationToken).ConfigureAwait(false);
            if (traceCheckpoint is not null)
            {
                traceState.RestoreActiveSpans(traceCheckpoint.ActiveSpans);
            }
            loadedTraceCheckpoint = true;
        }

        if (traceCheckpoint?.BatchId != batch.Id)
        {
            // Save both the next active span state and the exact outbound payload before
            // delivery. If the process stops before acknowledging this batch, restarting
            // can resend it without applying its Start/Event/Stop records twice.
            var completed = traceState.Apply(batch.Records);
            var active = traceState.SnapshotActiveSpans();
            unsavedTraceCheckpoint = traceCheckpoint is not null || active.Count > 0;
            traceCheckpoint = new DiskTelemetrySpool.TraceCheckpoint(
                batch.Id, OtlpTraceSerializer.Serialize(completed), active);
            traceState.MarkExportSucceeded(completed.Count);
        }

        if (unsavedTraceCheckpoint)
        {
            await traceCheckpoint.SaveAsync(batch, cancellationToken).ConfigureAwait(false);
            unsavedTraceCheckpoint = false;
        }

        await traces.SendAsync(traceCheckpoint.Payload, cancellationToken).ConfigureAwait(false);
    }

    public void Acknowledge(DiskTelemetrySpool.Batch batch, SpoolSignal signals)
    {
        if ((signals & SpoolSignal.Traces) != 0 && traceCheckpoint is { ActiveSpans.Count: 0 })
        {
            DiskTelemetrySpool.TraceCheckpoint.Remove(batch);
            traceCheckpoint = null;
        }
    }

    public void Dispose()
    {
        metrics.Dispose();
        logs.Dispose();
        traces.Dispose();
    }
}
