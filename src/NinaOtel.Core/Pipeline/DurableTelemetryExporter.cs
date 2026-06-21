using System.Runtime.ExceptionServices;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Health;
using NinaOtel.Core.Options;

namespace NinaOtel.Core.Pipeline;

internal sealed class DurableTelemetryExporter : ITelemetryExporter, IDisposable
{
    private static readonly TimeSpan DefaultRecoveryInitialDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultRecoveryMaxDelay = TimeSpan.FromSeconds(5);

    private readonly ITelemetryExporter innerExporter;
    private readonly DiskTelemetrySpool spool;
    private readonly TimeSpan recoveryInitialDelay;
    private readonly TimeSpan recoveryMaxDelay;
    private readonly Action<CollectorHealthSnapshot>? reportHealth;
    private readonly Uri? endpoint;
    private readonly OtlpProtocol protocol;
    private readonly TimeProvider? timeProvider;
    private readonly object recoveryWorkerLock = new();
    private readonly SemaphoreSlim spoolReplayLock = new(1, 1);
    private readonly CancellationTokenSource disposeCts = new();
    private Task? recoveryWorker;
    private long recoveryRequestVersion;
    private int disposed;

    public DurableTelemetryExporter(ITelemetryExporter innerExporter, DiskTelemetrySpool spool)
        : this(innerExporter, spool, DefaultRecoveryInitialDelay, DefaultRecoveryMaxDelay)
    {
    }

    internal DurableTelemetryExporter(
        ITelemetryExporter innerExporter,
        DiskTelemetrySpool spool,
        TimeSpan recoveryInitialDelay,
        TimeSpan recoveryMaxDelay,
        Action<CollectorHealthSnapshot>? reportHealth = null,
        Uri? endpoint = null,
        OtlpProtocol protocol = default,
        TimeProvider? timeProvider = null)
    {
        this.innerExporter = innerExporter ?? throw new ArgumentNullException(nameof(innerExporter));
        this.spool = spool ?? throw new ArgumentNullException(nameof(spool));
        if (reportHealth != null)
        {
            ArgumentNullException.ThrowIfNull(endpoint);
            ArgumentNullException.ThrowIfNull(timeProvider);
        }

        if (recoveryInitialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(recoveryInitialDelay), "Recovery delay must be positive.");
        }

        if (recoveryMaxDelay < recoveryInitialDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoveryMaxDelay),
                "Maximum recovery delay must be greater than or equal to the initial recovery delay.");
        }

        this.recoveryInitialDelay = recoveryInitialDelay;
        this.recoveryMaxDelay = recoveryMaxDelay;
        this.reportHealth = reportHealth;
        this.endpoint = endpoint;
        this.protocol = protocol;
        this.timeProvider = timeProvider;
    }

    public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        var replayFailure = await TryDrainSpoolAsync(cancellationToken).ConfigureAwait(false);
        if (replayFailure != null)
        {
            await AppendLiveBatchOrRethrowOriginalAsync(records, replayFailure, cancellationToken).ConfigureAwait(false);
            await ReportUnhealthyAsync(
                replayFailure,
                CollectorBufferMode.Degraded,
                cancellationToken).ConfigureAwait(false);
            EnsureRecoveryWorkerStarted();
            return;
        }

        try
        {
            await innerExporter.ExportAsync(records, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await AppendLiveBatchOrRethrowOriginalAsync(records, ex, cancellationToken).ConfigureAwait(false);
            await ReportUnhealthyAsync(
                ex,
                CollectorBufferMode.Degraded,
                cancellationToken).ConfigureAwait(false);
            EnsureRecoveryWorkerStarted();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        try
        {
            disposeCts.Cancel();
        }
        catch
        {
        }

        Task? workerToObserve;
        lock (recoveryWorkerLock)
        {
            workerToObserve = recoveryWorker;
        }

        if (workerToObserve is null || workerToObserve.IsCompleted)
        {
            DisposeOwnedResources();
        }
        else
        {
            _ = DisposeOwnedResourcesAfterWorkerCompletesAsync(workerToObserve);
        }
    }

    private async Task<Exception?> TryDrainSpoolAsync(CancellationToken cancellationToken)
    {
        await spoolReplayLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await TryDrainSpoolCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            spoolReplayLock.Release();
        }
    }

    private async Task<Exception?> TryDrainSpoolCoreAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DiskTelemetrySpool.Batch> batches;
        try
        {
            batches = await spool.ReadBatchesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DiskTelemetrySpool.BatchReadException ex)
        {
            try
            {
                spool.Quarantine(ex);
            }
            catch
            {
            }

            return ex;
        }
        catch (Exception ex)
        {
            return ex;
        }

        foreach (var batch in batches)
        {
            try
            {
                await innerExporter.ExportAsync(batch.Records, cancellationToken).ConfigureAwait(false);
                batch.Complete();
                await ReportAfterReplaySuccessAsync(batch.Records.Count, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        return null;
    }

    private async Task AppendLiveBatchOrRethrowOriginalAsync(
        IReadOnlyList<TelemetryRecord> records,
        Exception originalException,
        CancellationToken cancellationToken)
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
            ExceptionDispatchInfo.Capture(originalException).Throw();
            throw;
        }
    }

    private void EnsureRecoveryWorkerStarted()
    {
        lock (recoveryWorkerLock)
        {
            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            recoveryRequestVersion++;
            if (recoveryWorker is { IsCompleted: false })
            {
                return;
            }

            recoveryWorker = Task.Run(RunRecoveryLoopAsync, CancellationToken.None);
        }
    }

    private async Task RunRecoveryLoopAsync()
    {
        var delay = recoveryInitialDelay;

        while (Volatile.Read(ref disposed) == 0)
        {
            try
            {
                var observedRequestVersion = GetRecoveryRequestVersion();

                await Task.Delay(delay, disposeCts.Token).ConfigureAwait(false);

                var failure = await TryDrainSpoolAsync(disposeCts.Token).ConfigureAwait(false);
                if (failure is null)
                {
                    if (TryCompleteRecoveryWorker(observedRequestVersion))
                    {
                        return;
                    }

                    delay = TimeSpan.Zero;
                    continue;
                }

                await ReportUnhealthyAsync(
                    failure,
                    CollectorBufferMode.Degraded,
                    disposeCts.Token).ConfigureAwait(false);
                delay = NextRecoveryDelay(delay);
            }
            catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                delay = NextRecoveryDelay(delay);
            }
        }
    }

    private long GetRecoveryRequestVersion()
    {
        lock (recoveryWorkerLock)
        {
            return recoveryRequestVersion;
        }
    }

    private bool TryCompleteRecoveryWorker(long observedRequestVersion)
    {
        lock (recoveryWorkerLock)
        {
            if (Volatile.Read(ref disposed) != 0 || recoveryRequestVersion == observedRequestVersion)
            {
                recoveryWorker = null;
                return true;
            }

            return false;
        }
    }

    private TimeSpan NextRecoveryDelay(TimeSpan currentDelay)
    {
        if (currentDelay <= TimeSpan.Zero)
        {
            return recoveryInitialDelay;
        }

        var nextTicks = currentDelay.Ticks > recoveryMaxDelay.Ticks / 2
            ? recoveryMaxDelay.Ticks
            : currentDelay.Ticks * 2;
        return TimeSpan.FromTicks(Math.Min(nextTicks, recoveryMaxDelay.Ticks));
    }

    private async Task ReportAfterReplaySuccessAsync(int exportedRecords, CancellationToken cancellationToken)
    {
        if (reportHealth is null)
        {
            return;
        }

        var stats = await GetStatsSafelyAsync(cancellationToken).ConfigureAwait(false);
        if (stats.QueuedRecords == 0)
        {
            ReportSafely(CollectorHealthSnapshot.Healthy(
                endpoint!,
                protocol,
                exportedRecords,
                timeProvider!.GetUtcNow(),
                bufferMode: CollectorBufferMode.Healthy));
            return;
        }

        ReportSafely(CollectorHealthSnapshot.Unhealthy(
            endpoint!,
            protocol,
            "RecoveryInProgress",
            "Collector is reachable; draining queued telemetry.",
            timeProvider!.GetUtcNow(),
            exportedRecords,
            CollectorBufferMode.Recovering,
            stats.QueuedRecords,
            stats.QueuedBytes,
            stats.OldestQueuedTimestamp));
    }

    private async Task ReportUnhealthyAsync(
        Exception exception,
        CollectorBufferMode bufferMode,
        CancellationToken cancellationToken)
    {
        if (reportHealth is null)
        {
            return;
        }

        var stats = await GetStatsSafelyAsync(cancellationToken).ConfigureAwait(false);
        ReportSafely(CollectorHealthSnapshot.Unhealthy(
            endpoint!,
            protocol,
            exception.GetType().Name,
            exception.Message,
            timeProvider!.GetUtcNow(),
            bufferMode: bufferMode,
            queuedRecords: stats.QueuedRecords,
            queuedBytes: stats.QueuedBytes,
            oldestQueuedTimestamp: stats.OldestQueuedTimestamp));
    }

    private async Task<DiskTelemetrySpool.Stats> GetStatsSafelyAsync(CancellationToken cancellationToken)
    {
        if (reportHealth is null)
        {
            return DiskTelemetrySpool.Stats.Empty;
        }

        try
        {
            return await spool.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return DiskTelemetrySpool.Stats.Empty;
        }
    }

    private void ReportSafely(CollectorHealthSnapshot snapshot)
    {
        try
        {
            reportHealth?.Invoke(snapshot);
        }
        catch
        {
            // Exporter health is diagnostic state; it must never break telemetry delivery.
        }
    }

    private void DisposeOwnedResources()
    {
        if (innerExporter is IDisposable disposable)
        {
            disposable.Dispose();
        }

        disposeCts.Dispose();
    }

    private async Task DisposeOwnedResourcesAfterWorkerCompletesAsync(Task workerToObserve)
    {
        try
        {
            await workerToObserve.ConfigureAwait(false);
        }
        catch
        {
        }

        DisposeOwnedResources();
    }
}
