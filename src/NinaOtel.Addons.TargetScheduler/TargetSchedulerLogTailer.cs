using NinaOtel.Abstractions.Logs;
using System.Text;

namespace NinaOtel.Addons.TargetScheduler;

internal sealed class TargetSchedulerLogTailer
{
    private readonly LogSourceWatcher watcher;
    private readonly TimeSpan pollInterval;
    private readonly Func<string, string, CancellationToken, Task> lineHandler;
    private readonly Action<string> fileMissingHandler;
    private readonly CancellationTokenSource cancellation = new();
    private long? firstOpenPosition;
    private bool isFirstOpen = true;
    private DateTime creationTimeUtc;
    private DateTime lastWriteTimeUtc;
    private Task? worker;

    public TargetSchedulerLogTailer(
        LogSourceWatcher watcher,
        TimeSpan pollInterval,
        Func<string, string, CancellationToken, Task> lineHandler,
        Action<string> fileMissingHandler)
    {
        this.watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        this.pollInterval = pollInterval > TimeSpan.Zero ? pollInterval : TimeSpan.FromSeconds(1);
        this.lineHandler = lineHandler ?? throw new ArgumentNullException(nameof(lineHandler));
        this.fileMissingHandler = fileMissingHandler ?? throw new ArgumentNullException(nameof(fileMissingHandler));
    }

    public string? CurrentPath => watcher.CurrentPath;

    public string NotFoundMessage => watcher.NotFoundMessage;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        watcher.Refresh();

        // The file that already exists when we start is read from its end; a file that shows up
        // later (a new run of the source app, or a rotation) is read from the beginning.
        var currentPath = watcher.CurrentPath;
        firstOpenPosition = currentPath is null ? 0 : TryGetCurrentLength(currentPath) ?? 0;
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
                watcher.Refresh();
                var path = watcher.CurrentPath;
                if (path is null || !File.Exists(path))
                {
                    fileMissingHandler(watcher.NotFoundMessage);
                    await DelayAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await TailExistingFileAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (FileNotFoundException)
            {
                fileMissingHandler(watcher.NotFoundMessage);
                await DelayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DirectoryNotFoundException)
            {
                fileMissingHandler(watcher.NotFoundMessage);
                await DelayAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await DelayAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TailExistingFileAsync(string path, CancellationToken cancellationToken)
    {
        var startPosition = isFirstOpen ? firstOpenPosition ?? 0 : 0;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream);

        stream.Seek(Math.Min(startPosition, stream.Length), SeekOrigin.Begin);
        isFirstOpen = false;
        RefreshFileSnapshot(path);

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
                        await FlushPendingRecordAsync(pendingRecord, path, cancellationToken).ConfigureAwait(false);
                        pendingRecord.Append(line);
                        pendingRecordSawQuietPoll = false;
                    }
                    else if (pendingRecord.Length > 0)
                    {
                        pendingRecord.AppendLine();
                        pendingRecord.Append(line);
                        pendingRecordSawQuietPoll = false;
                    }

                    RefreshFileSnapshot(path);
                    continue;
                }

                if (watcher.Refresh() || ShouldReopen(path, stream))
                {
                    // Rotation or a replaced file: hand over what is pending, then read the
                    // next file from the beginning.
                    await FlushPendingRecordAsync(pendingRecord, path, cancellationToken).ConfigureAwait(false);
                    isFirstOpen = false;
                    return;
                }

                if (pendingRecord.Length > 0 && pendingRecordSawQuietPoll)
                {
                    await FlushPendingRecordAsync(pendingRecord, path, cancellationToken).ConfigureAwait(false);
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
            await FlushPendingRecordAsync(pendingRecord, path, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task FlushPendingRecordAsync(
        StringBuilder pendingRecord,
        string path,
        CancellationToken cancellationToken)
    {
        if (pendingRecord.Length == 0)
        {
            return;
        }

        var record = pendingRecord.ToString();
        pendingRecord.Clear();
        await lineHandler(record, path, cancellationToken).ConfigureAwait(false);
    }

    private Task DelayAsync(CancellationToken cancellationToken) =>
        Task.Delay(pollInterval, cancellationToken);

    private long? TryGetCurrentLength(string path)
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

    private bool ShouldReopen(string path, FileStream stream)
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

    private void RefreshFileSnapshot(string path)
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
