using System.Threading.Channels;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

public sealed class TelemetryPipeline : ITelemetrySink, IAsyncDisposable
{
    private readonly Channel<TelemetryRecord> channel;
    private readonly ITelemetryExporter exporter;
    private readonly CancellationTokenSource stopCts = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? worker;
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
            SingleReader = true,
            SingleWriter = false,
        });
    }

    public long DroppedRecords => Interlocked.Read(ref droppedRecords);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (worker != null)
        {
            return started.Task.WaitAsync(cancellationToken);
        }

        worker = Task.Run(() => RunAsync(stopCts.Token), CancellationToken.None);
        started.TrySetResult();
        return started.Task.WaitAsync(cancellationToken);
    }

    public bool TryPublish(TelemetryRecord record)
    {
        if (channel.Writer.TryWrite(record))
        {
            return true;
        }

        Interlocked.Increment(ref droppedRecords);
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        channel.Writer.TryComplete();
        await stopCts.CancelAsync();

        if (worker != null)
        {
            try
            {
                await worker.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        stopCts.Dispose();
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

                await exporter.ExportAsync(batch, cancellationToken);
                batch.Clear();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
