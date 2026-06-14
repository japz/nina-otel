using System.Threading.Channels;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

public sealed class TelemetryPipeline : ITelemetrySink, IAsyncDisposable
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly object disposalLock = new();
    private readonly object startLock = new();
    private readonly Channel<TelemetryRecord> channel;
    private readonly ITelemetryExporter exporter;
    private readonly CancellationTokenSource stopCts = new();
    private Task? disposalTask;
    private Task? worker;
    private int disposed;
    private int workerStartCount;
    private int inFlightRecords;
    private long droppedRecords;

    public TelemetryPipeline(ITelemetryExporter exporter, int capacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be positive.");
        }

        this.exporter = exporter ?? throw new ArgumentNullException(nameof(exporter));
        channel = Channel.CreateBounded<TelemetryRecord>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public long DroppedRecords => Interlocked.Read(ref droppedRecords);

    internal int WorkerStartCount => Volatile.Read(ref workerStartCount);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        EnsureWorkerStarted();
        return Task.CompletedTask;
    }

    public bool TryPublish(TelemetryRecord record)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            Interlocked.Increment(ref droppedRecords);
            return false;
        }

        if (channel.Writer.TryWrite(record))
        {
            return true;
        }

        Interlocked.Increment(ref droppedRecords);
        return false;
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask(EnsureDisposalStarted());
    }

    private Task EnsureDisposalStarted()
    {
        lock (disposalLock)
        {
            if (disposalTask != null)
            {
                return disposalTask;
            }

            Interlocked.Exchange(ref disposed, 1);
            disposalTask = DisposeCoreAsync();
            return disposalTask;
        }
    }

    private async Task DisposeCoreAsync()
    {
        channel.Writer.TryComplete();
        var workerToDrain = GetWorker();

        if (workerToDrain != null)
        {
            try
            {
                await workerToDrain.WaitAsync(DrainTimeout);
            }
            catch (TimeoutException)
            {
                RequestCancellationWithoutWaiting();
                DropInFlightRecords();
                DropReadableRecords();
                _ = DisposeStopCtsAfterWorkerCompletesAsync(workerToDrain);
                // The worker may still observe this token after a drain timeout.
                return;
            }
            catch
            {
            }
        }
        else
        {
            DropReadableRecords();
        }

        stopCts.Dispose();
    }

    private void EnsureWorkerStarted()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        lock (startLock)
        {
            if (worker != null || Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            Interlocked.Increment(ref workerStartCount);
            worker = Task.Run(() => RunAsync(stopCts.Token), CancellationToken.None);
        }
    }

    private Task? GetWorker()
    {
        lock (startLock)
        {
            return worker;
        }
    }

    private void RequestCancellationWithoutWaiting()
    {
        Task cancellationTask;

        try
        {
            cancellationTask = stopCts.CancelAsync();
        }
        catch
        {
            return;
        }

        _ = ObserveCancellationWithoutThrowingAsync(cancellationTask);
    }

    private static async Task ObserveCancellationWithoutThrowingAsync(Task cancellationTask)
    {
        try
        {
            await cancellationTask.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task DisposeStopCtsAfterWorkerCompletesAsync(Task workerToDrain)
    {
        try
        {
            await workerToDrain.ConfigureAwait(false);
        }
        catch
        {
        }

        stopCts.Dispose();
    }

    private void DropReadableRecords()
    {
        var count = 0;

        while (channel.Reader.TryRead(out _))
        {
            count++;
        }

        if (count > 0)
        {
            Interlocked.Add(ref droppedRecords, count);
        }
    }

    private void DropInFlightRecords()
    {
        var count = Interlocked.Exchange(ref inFlightRecords, 0);

        if (count > 0)
        {
            Interlocked.Add(ref droppedRecords, count);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var batch = new List<TelemetryRecord>(128);

        try
        {
            await foreach (var record in channel.Reader.ReadAllAsync(cancellationToken))
            {
                batch.Add(record);
                while (batch.Count < 128 && channel.Reader.TryRead(out var next))
                {
                    batch.Add(next);
                }

                if (!await TryExportBatchAsync(batch, cancellationToken))
                {
                    batch.Clear();
                    break;
                }

                batch.Clear();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> TryExportBatchAsync(List<TelemetryRecord> batch, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref inFlightRecords, batch.Count);

        try
        {
            await exporter.ExportAsync(batch, cancellationToken);
            Interlocked.Exchange(ref inFlightRecords, 0);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DropInFlightRecords();
            return false;
        }
        catch (Exception)
        {
            DropInFlightRecords();
            return true;
        }
    }
}
