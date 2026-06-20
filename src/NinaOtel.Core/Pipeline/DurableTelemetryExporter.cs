using System.Runtime.ExceptionServices;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

internal sealed class DurableTelemetryExporter : ITelemetryExporter, IDisposable
{
    private readonly ITelemetryExporter innerExporter;
    private readonly DiskTelemetrySpool spool;

    public DurableTelemetryExporter(ITelemetryExporter innerExporter, DiskTelemetrySpool spool)
    {
        this.innerExporter = innerExporter ?? throw new ArgumentNullException(nameof(innerExporter));
        this.spool = spool ?? throw new ArgumentNullException(nameof(spool));
    }

    public async Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

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

            await AppendLiveBatchOrRethrowOriginalAsync(records, ex, cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await AppendLiveBatchOrRethrowOriginalAsync(records, ex, cancellationToken).ConfigureAwait(false);
            return;
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
                await AppendLiveBatchOrRethrowOriginalAsync(records, ex, cancellationToken).ConfigureAwait(false);
                return;
            }
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
        }
    }

    public void Dispose()
    {
        if (innerExporter is IDisposable disposable)
        {
            disposable.Dispose();
        }
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
}
