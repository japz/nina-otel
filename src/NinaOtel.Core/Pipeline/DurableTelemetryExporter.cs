using System.Runtime.ExceptionServices;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Health;
using NinaOtel.Core.Options;

namespace NinaOtel.Core.Pipeline;

internal sealed class DurableTelemetryExporter : ITelemetryExporter, IDisposable
{
    private static readonly SpoolSignal[] Signals = [SpoolSignal.Metrics, SpoolSignal.Logs, SpoolSignal.Traces];
    private static readonly TimeSpan DefaultReplayInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiagnosticInterval = TimeSpan.FromMinutes(1);
    private const int FilePageSize = 64;

    private readonly ITelemetryExporter liveExporter;
    private readonly ISpoolReplayExporter replayExporter;
    private readonly DiskTelemetrySpool spool;
    private readonly TimeSpan replayInterval;
    private readonly TimeSpan initialBackoff;
    private readonly TimeSpan maxBackoff;
    private readonly Action<CollectorHealthSnapshot>? reportHealth;
    private readonly Action<string>? diagnostic;
    private readonly Uri? endpoint;
    private readonly OtlpProtocol protocol;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim newWork = new(0, 1);
    private readonly CancellationTokenSource stopping = new();
    private readonly Task worker;
    private DiskTelemetrySpool.Stats lastStats = DiskTelemetrySpool.Stats.Empty;
    private int quarantinedBatches;
    private DateTimeOffset nextDiagnostic;
    private int disposed;

    public DurableTelemetryExporter(
        ITelemetryExporter liveExporter,
        ISpoolReplayExporter replayExporter,
        DiskTelemetrySpool spool,
        Action<CollectorHealthSnapshot>? reportHealth = null,
        Uri? endpoint = null,
        OtlpProtocol protocol = default,
        TimeProvider? timeProvider = null,
        Action<string>? diagnostic = null,
        TimeSpan? replayInterval = null,
        TimeSpan? initialBackoff = null,
        TimeSpan? maxBackoff = null)
    {
        if (reportHealth is not null)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
        }

        this.liveExporter = liveExporter ?? throw new ArgumentNullException(nameof(liveExporter));
        this.replayExporter = replayExporter ?? throw new ArgumentNullException(nameof(replayExporter));
        this.spool = spool ?? throw new ArgumentNullException(nameof(spool));
        this.reportHealth = reportHealth;
        this.endpoint = endpoint;
        this.protocol = protocol;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.diagnostic = diagnostic;
        this.replayInterval = replayInterval ?? DefaultReplayInterval;
        this.initialBackoff = initialBackoff ?? DefaultInitialBackoff;
        this.maxBackoff = maxBackoff ?? DefaultMaxBackoff;

        // Include a queue left by a previous process, even if no live export has run.
        worker = Task.Run(RunReplayAsync);
    }

    internal Task ReplayCompletion => worker;

    public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();
        if (records.Count == 0)
        {
            return;
        }

        try
        {
            await liveExporter.ExportAsync(records, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            try
            {
                await spool.AppendBatchAsync(records, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                ReportHealth(RefreshStats(), failure);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            ReportHealth(RefreshStats(), failure);
            SignalNewWork();
            return;
        }

        // A successful live request does not erase a backlog or its recovery status.
        var stats = Volatile.Read(ref lastStats);
        if (stats.QueuedBatches == 0)
        {
            ReportHealth(stats, exportedRecords: records.Count);
        }
    }

    private async Task RunReplayAsync()
    {
        var token = stopping.Token;
        try
        {
            // A settings reload can create the next exporter before the old one has stopped.
            await spool.AcquireReplayOwnershipAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await DrainAsync(token).ConfigureAwait(false);
        }
        finally
        {
            spool.ReleaseReplayOwnership();
        }
    }

    private async Task DrainAsync(CancellationToken token)
    {
        var retries = new Dictionary<SpoolSignal, Retry>();
        var nextHealth = DateTimeOffset.MinValue;
        var hadBacklog = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                var eligible = GetEligibleSignals(retries, now);
                if (now >= nextHealth && reportHealth is not null)
                {
                    var stats = RefreshStats();
                    if (hadBacklog || stats.QueuedBatches > 0)
                    {
                        ReportHealth(stats, retries.Values.FirstOrDefault()?.Failure);
                    }
                    hadBacklog = stats.QueuedBatches > 0;
                    nextHealth = now + HealthInterval;
                }

                var paths = eligible == SpoolSignal.None ? [] : spool.GetReplayPage(eligible, FilePageSize);
                if (paths.Count == 0)
                {
                    if (hadBacklog)
                    {
                        var stats = RefreshStats();
                        ReportHealth(stats, stats.QueuedBatches > 0 ? retries.Values.FirstOrDefault()?.Failure : null);
                        hadBacklog = stats.QueuedBatches > 0;
                    }

                    var retryAt = retries.Values.Select(retry => retry.At).DefaultIfEmpty(DateTimeOffset.MaxValue).Min();
                    var wait = retryAt == DateTimeOffset.MaxValue
                        ? Timeout.InfiniteTimeSpan
                        : retryAt - timeProvider.GetUtcNow();
                    if (wait == Timeout.InfiniteTimeSpan || wait > TimeSpan.Zero)
                    {
                        await newWork.WaitAsync(wait, token).ConfigureAwait(false);
                    }
                    continue;
                }

                // Do not resume a signal halfway through a page: its oldest failed file
                // must be selected again before any later file can export that signal.
                var pageSignals = eligible;
                foreach (var path in paths)
                {
                    now = timeProvider.GetUtcNow();
                    var currentSignals = GetEligibleSignals(retries, now);
                    if ((currentSignals & ~pageSignals) != 0)
                    {
                        break;
                    }
                    pageSignals &= currentSignals;
                    if (pageSignals == SpoolSignal.None || token.IsCancellationRequested)
                    {
                        break;
                    }
                    if ((DiskTelemetrySpool.ReplayFile.Parse(path).Pending & pageSignals) == 0)
                    {
                        continue;
                    }

                    hadBacklog = true;
                    await ReplayOneAsync(path, pageSignals, retries, token).ConfigureAwait(false);
                    await Task.Delay(replayInterval, token).ConfigureAwait(false);

                    if (reportHealth is not null && timeProvider.GetUtcNow() >= nextHealth)
                    {
                        ReportHealth(RefreshStats(), retries.Values.FirstOrDefault()?.Failure);
                        nextHealth = timeProvider.GetUtcNow() + HealthInterval;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception failure)
            {
                // I/O failures are retryable; never quarantine a file solely because
                // a read, rename or delivery acknowledgement failed on disk.
                ReportHealth(RefreshStats(), failure);
                try
                {
                    await Task.Delay(maxBackoff, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
            }
        }
    }

    private async Task ReplayOneAsync(
        string path,
        SpoolSignal eligible,
        Dictionary<SpoolSignal, Retry> retries,
        CancellationToken token)
    {
        if (!spool.TryReserve(path))
        {
            return;
        }

        try
        {
            var file = DiskTelemetrySpool.ReplayFile.Parse(path);
            DiskTelemetrySpool.Batch batch;
            try
            {
                batch = await DiskTelemetrySpool.ReadBatchAsync(path, token).ConfigureAwait(false);
            }
            catch (DiskTelemetrySpool.BatchReadException)
            {
                spool.FinishAttempt(path, file with { Pending = SpoolSignal.None, HasInvalidSignal = true });
                ReportQuarantine("unreadable JSON batch");
                return;
            }

            var pending = file.Pending & DiskTelemetrySpool.GetSignals(batch.Records);
            var invalid = file.HasInvalidSignal;
            string? invalidReason = null;
            var attempts = Signals.Where(signal => (signal & pending & eligible) != 0)
                .Select(async signal => (Signal: signal, Failure: await TryExportAsync(signal, batch, token).ConfigureAwait(false)));
            foreach (var (signal, failure) in await Task.WhenAll(attempts).ConfigureAwait(false))
            {
                if (failure is TelemetryExportException { IsPermanent: true })
                {
                    invalid = true;
                    invalidReason = failure.Message;
                }
                else if (failure is not null)
                {
                    var previous = retries.GetValueOrDefault(signal);
                    var backoff = previous is null ? initialBackoff :
                        TimeSpan.FromTicks(previous.Backoff.Ticks + Math.Min(previous.Backoff.Ticks, maxBackoff.Ticks - previous.Backoff.Ticks));
                    retries[signal] = new Retry(timeProvider.GetUtcNow() + backoff, backoff, failure);
                    continue;
                }

                retries.Remove(signal);
                pending &= ~signal;
            }

            spool.FinishAttempt(path, file with
            {
                Pending = pending,
                HasInvalidSignal = invalid,
                RecordCount = batch.Records.Count,
                OldestTimestamp = batch.Records.Select(record => (DateTimeOffset?)record.Timestamp).Min(),
            });
            replayExporter.Acknowledge(batch, file.Pending & ~pending & eligible);
            if (invalidReason is not null && !file.HasInvalidSignal)
            {
                ReportQuarantine(invalidReason);
            }
        }
        finally
        {
            spool.Release();
        }
    }

    private async Task<Exception?> TryExportAsync(SpoolSignal signal, DiskTelemetrySpool.Batch batch, CancellationToken token)
    {
        try
        {
            await replayExporter.ExportAsync(signal, batch, token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static SpoolSignal GetEligibleSignals(Dictionary<SpoolSignal, Retry> retries, DateTimeOffset now)
    {
        var eligible = SpoolSignal.All;
        foreach (var (signal, retry) in retries)
        {
            if (retry.At > now)
            {
                eligible &= ~signal;
            }
        }

        return eligible;
    }

    private void SignalNewWork()
    {
        try
        {
            newWork.Release();
        }
        catch (SemaphoreFullException)
        {
            // A pending wake already covers this append.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private DiskTelemetrySpool.Stats RefreshStats()
    {
        if (reportHealth is not null)
        {
            try
            {
                Volatile.Write(ref lastStats, spool.GetStats());
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return Volatile.Read(ref lastStats);
    }

    private void ReportHealth(DiskTelemetrySpool.Stats stats, Exception? failure = null, int exportedRecords = 0)
    {
        if (reportHealth is null || Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        try
        {
            var checkedAt = timeProvider.GetUtcNow();
            var dropped = (int)Math.Min(stats.DroppedRecords, int.MaxValue);
            var invalid = Volatile.Read(ref quarantinedBatches);
            var snapshot = failure is not null
                ? CollectorHealthSnapshot.Unhealthy(endpoint!, protocol, failure.GetType().Name, failure.Message, checkedAt,
                    bufferMode: CollectorBufferMode.Degraded, queuedRecords: stats.QueuedRecords, queuedBytes: stats.QueuedBytes,
                    oldestQueuedTimestamp: stats.OldestQueuedTimestamp, droppedRecords: dropped,
                    queuedBatches: stats.QueuedBatches, quarantinedBatches: invalid)
                : CollectorHealthSnapshot.Healthy(endpoint!, protocol, exportedRecords, checkedAt,
                    bufferMode: stats.QueuedBatches == 0 ? CollectorBufferMode.Healthy : CollectorBufferMode.Recovering,
                    queuedRecords: stats.QueuedRecords, queuedBytes: stats.QueuedBytes, oldestQueuedTimestamp: stats.OldestQueuedTimestamp,
                    droppedRecords: dropped, queuedBatches: stats.QueuedBatches, quarantinedBatches: invalid);
            reportHealth(snapshot);
        }
        catch
        {
            // Diagnostics must not affect delivery.
        }
    }

    private void ReportQuarantine(string reason)
    {
        var count = Interlocked.Increment(ref quarantinedBatches);
        var now = timeProvider.GetUtcNow();
        if (now < nextDiagnostic)
        {
            return;
        }
        nextDiagnostic = now + DiagnosticInterval;
        try
        {
            diagnostic?.Invoke($"NinaOtel spool quarantined {count} batch(es) this session; latest: {reason}.");
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            stopping.Cancel();
            _ = DisposeAfterReplayAsync();
        }
    }

    private async Task DisposeAfterReplayAsync()
    {
        try
        {
            await worker.ConfigureAwait(false);
        }
        finally
        {
            replayExporter.Dispose();
            (liveExporter as IDisposable)?.Dispose();
            newWork.Dispose();
            stopping.Dispose();
        }
    }

    private sealed record Retry(DateTimeOffset At, TimeSpan Backoff, Exception Failure);
}
