using System.Text;

namespace NinaOtel.Addons.TargetScheduler;

internal sealed class TargetSchedulerLogTailer
{
    private readonly string path;
    private readonly TimeSpan pollInterval;
    private readonly Func<string, CancellationToken, Task> lineHandler;
    private readonly Action<string> fileMissingHandler;
    private readonly CancellationTokenSource cancellation = new();
    private long? initialPosition;
    private DateTime creationTimeUtc;
    private DateTime lastWriteTimeUtc;
    private Task? worker;

    public TargetSchedulerLogTailer(
        string path,
        TimeSpan pollInterval,
        Func<string, CancellationToken, Task> lineHandler,
        Action<string> fileMissingHandler)
    {
        this.path = path ?? throw new ArgumentNullException(nameof(path));
        this.pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : TimeSpan.FromSeconds(1);
        this.lineHandler = lineHandler ?? throw new ArgumentNullException(nameof(lineHandler));
        this.fileMissingHandler = fileMissingHandler ?? throw new ArgumentNullException(nameof(fileMissingHandler));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        initialPosition ??= TryGetCurrentLength() ?? 0;
        worker ??= Task.Run(() => RunAsync(cancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellation.Cancel();

        if (worker is null)
        {
            return;
        }

        try
        {
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(path))
                {
                    fileMissingHandler(path);
                    await DelayAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await TailExistingFileAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (FileNotFoundException)
            {
                fileMissingHandler(path);
                await DelayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryNotFoundException)
            {
                fileMissingHandler(path);
                await DelayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await DelayAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TailExistingFileAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);

        stream.Seek(Math.Min(initialPosition ?? stream.Length, stream.Length), SeekOrigin.Begin);
        initialPosition = null;
        RefreshFileSnapshot();

        var pendingRecord = new StringBuilder();
        var pendingRecordSawQuietPoll = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is not null)
                {
                    if (IsLogRecordBoundary(line))
                    {
                        await FlushPendingRecordAsync(pendingRecord, cancellationToken).ConfigureAwait(false);
                        pendingRecord.Append(line);
                        pendingRecordSawQuietPoll = false;
                    }
                    else if (pendingRecord.Length > 0)
                    {
                        pendingRecord.AppendLine();
                        pendingRecord.Append(line);
                        pendingRecordSawQuietPoll = false;
                    }

                    RefreshFileSnapshot();
                    continue;
                }

                if (ShouldReopen(stream))
                {
                    await FlushPendingRecordAsync(pendingRecord, cancellationToken).ConfigureAwait(false);
                    initialPosition = 0;
                    return;
                }

                if (pendingRecord.Length > 0 && pendingRecordSawQuietPoll)
                {
                    await FlushPendingRecordAsync(pendingRecord, cancellationToken).ConfigureAwait(false);
                }
                else if (pendingRecord.Length > 0)
                {
                    pendingRecordSawQuietPoll = true;
                }

                await DelayAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await FlushPendingRecordAsync(pendingRecord, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task FlushPendingRecordAsync(StringBuilder pendingRecord, CancellationToken cancellationToken)
    {
        if (pendingRecord.Length == 0)
        {
            return;
        }

        var record = pendingRecord.ToString();
        pendingRecord.Clear();
        await lineHandler(record, cancellationToken).ConfigureAwait(false);
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
        Task.Delay(pollInterval, cancellationToken);

    private long? TryGetCurrentLength()
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ShouldReopen(FileStream stream)
    {
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            var current = new FileInfo(path);
            if (current.CreationTimeUtc != creationTimeUtc)
            {
                return true;
            }

            if (current.Length < stream.Position)
            {
                return true;
            }

            return current.Length == stream.Position && current.LastWriteTimeUtc != lastWriteTimeUtc;
        }
        catch
        {
            return true;
        }
    }

    private void RefreshFileSnapshot()
    {
        try
        {
            var current = new FileInfo(path);
            creationTimeUtc = current.Exists ? current.CreationTimeUtc : default;
            lastWriteTimeUtc = current.Exists ? current.LastWriteTimeUtc : default;
        }
        catch
        {
            creationTimeUtc = default;
            lastWriteTimeUtc = default;
        }
    }

    private static bool IsLogRecordBoundary(string line) =>
        line.Split(['|'], 6).Length == 6;
}
