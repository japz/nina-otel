using System.Runtime.ExceptionServices;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

internal sealed class DurableTelemetryExporter : ITelemetryExporter, IDisposable
{
    private static readonly TimeSpan DefaultRecoveryInitialDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan DefaultRecoveryMaxDelay = TimeSpan.FromSeconds(5);

    private readonly ITelemetryExporter innerExporter;
    private readonly DiskTelemetrySpool spool;
    private readonly TimeSpan recoveryInitialDelay;
    private readonly TimeSpan recoveryMaxDelay;
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
        TimeSpan recoveryMaxDelay)
    {
        this.innerExporter = innerExporter ?? throw new ArgumentNullException(nameof(innerExporter));
        this.spool = spool ?? throw new ArgumentNullException(nameof(spool));
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
    }

    public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        var replayFailure = await TryDrainSpoolAsync(cancellationToken).ConfigureAwait(false);
        if (replayFailure != null)
        {
            await AppendLiveBatchOrRethrowOriginalAsync(records, replayFailure, cancellationToken).ConfigureAwait(false);
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
