using System.Threading.Channels;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

public sealed class TelemetryPipeline : ITelemetrySink, IAsyncDisposable
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly object startLock = new();
    private readonly Channel<TelemetryRecord> channel;
    private readonly ITelemetryExporter exporter;
    private readonly CancellationTokenSource stopCts = new();
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

        this.exporter = exporter;
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

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
                CancelWithoutWaiting();
                DropInFlightRecords();
                DropReadableRecords();
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

    private void CancelWithoutWaiting()
    {
        _ = Task.Run(CancelWithoutThrowingAsync);
    }

    private async Task CancelWithoutThrowingAsync()
    {
        try
        {
            await stopCts.CancelAsync();
        }
        catch
        {
        }
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
