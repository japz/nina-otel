using System.IO;
using System.Text;
using NinaOtel.Core.Logs;

namespace NinaOtel.Plugin.Telemetry;

internal enum NinaLogTailerStartPosition
{
    Beginning,
    End,
}

public sealed class NinaLogTailer : IDisposable
{
    private static readonly NinaLogTailerReadResult EmptyReadResult = new([], HasNewLines: false);
    private const int PrefixSnapshotBytes = 256;

    private readonly string path;
    private readonly NinaLogTailerStartPosition startPosition;
    private readonly int readBufferSize;
    private readonly StringBuilder pendingText = new();
    private readonly List<string> pendingEventLines = [];
    private StreamReader? reader;
    private FileStream? stream;
    private DateTime openedCreationTimeUtc;
    private DateTime observedLastWriteTimeUtc;
    private long observedLength;
    private byte[] prefixSnapshot = [];
    private bool hasOpenedReader;
    private bool disposed;

    public NinaLogTailer(string path)
        : this(path, NinaLogTailerStartPosition.End, readBufferSize: 4096)
    {
    }

    internal NinaLogTailer(
        string path,
        NinaLogTailerStartPosition startPosition,
        int readBufferSize)
    {
        this.path = path ?? throw new ArgumentNullException(nameof(path));
        this.startPosition = startPosition;
        this.readBufferSize = readBufferSize > 0
            ? readBufferSize
            : throw new ArgumentOutOfRangeException(nameof(readBufferSize));
    }

    internal bool HasPendingEvent => pendingEventLines.Count > 0;

    internal void Prime()
    {
        if (!EnsureReader())
        {
            hasOpenedReader = true;
        }
    }

    internal NinaLogTailerReadResult ReadAvailable()
    {
        if (disposed)
        {
            return EmptyReadResult;
        }

        if (reader is not null && !IsCurrentReaderUsable())
        {
            var pendingEvents = FlushPending();
            ResetReader(clearPending: true);
            if (pendingEvents.Count > 0)
            {
                return new NinaLogTailerReadResult(pendingEvents, HasNewLines: true);
            }
        }

        if (!EnsureReader())
        {
            return EmptyReadResult;
        }

        try
        {
            var lines = ReadCompleteLines();
            UpdateObservedFileSnapshot();
            if (lines.Count == 0)
            {
                return EmptyReadResult;
            }

            return ParseLinesHoldingLastEvent(lines);
        }
        catch (IOException)
        {
            ResetReader();
            return EmptyReadResult;
        }
        catch (UnauthorizedAccessException)
        {
            ResetReader();
            return EmptyReadResult;
        }
        catch (ObjectDisposedException)
        {
            ResetReader();
            return EmptyReadResult;
        }
    }

    internal IReadOnlyList<NinaLogEvent> FlushPending()
    {
        if (disposed || pendingEventLines.Count == 0)
        {
            return [];
        }

        try
        {
            var events = NinaLogParser.ParseLines(pendingEventLines);
            pendingEventLines.Clear();
            return events;
        }
        catch
        {
            pendingEventLines.Clear();
            return [];
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ResetReader();
        pendingText.Clear();
        pendingEventLines.Clear();
    }

    private bool EnsureReader()
    {
        if (reader is not null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            openedCreationTimeUtc = File.GetCreationTimeUtc(path);
            UpdateObservedFileSnapshot();

            if (!hasOpenedReader && startPosition == NinaLogTailerStartPosition.End)
            {
                stream.Seek(0, SeekOrigin.End);
            }

            reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: Math.Max(128, readBufferSize),
                leaveOpen: false);
            hasOpenedReader = true;
            return true;
        }
        catch (IOException)
        {
            ResetReader(clearPending: true);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            ResetReader(clearPending: true);
            return false;
        }
    }

    private bool IsCurrentReaderUsable()
    {
        if (stream is null)
        {
            return false;
        }

        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                return false;
            }

            if (openedCreationTimeUtc != default &&
                fileInfo.CreationTimeUtc != openedCreationTimeUtc)
            {
                return false;
            }

            if (observedLastWriteTimeUtc != default &&
                fileInfo.LastWriteTimeUtc != observedLastWriteTimeUtc &&
                fileInfo.Length <= observedLength)
            {
                return false;
            }

            if (prefixSnapshot.Length > 0 &&
                fileInfo.Length >= prefixSnapshot.Length &&
                !PrefixMatchesSnapshot())
            {
                return false;
            }

            return fileInfo.Length == stream.Length &&
                fileInfo.Length >= stream.Position;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void UpdateObservedFileSnapshot()
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists)
            {
                return;
            }

            observedLength = fileInfo.Length;
            observedLastWriteTimeUtc = fileInfo.LastWriteTimeUtc;
            if (prefixSnapshot.Length == 0 && fileInfo.Length > 0)
            {
                prefixSnapshot = ReadPrefixSnapshot();
            }
        }
        catch
        {
            // Snapshot state is only used to decide when to reopen; failure should be fail-open.
        }
    }

    private bool PrefixMatchesSnapshot()
    {
        try
        {
            var currentPrefix = ReadPrefixSnapshot(prefixSnapshot.Length);
            return currentPrefix.AsSpan().SequenceEqual(prefixSnapshot);
        }
        catch
        {
            return false;
        }
    }

    private byte[] ReadPrefixSnapshot(int maxBytes = PrefixSnapshotBytes)
    {
        using var prefixStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[Math.Min(maxBytes, (int)Math.Min(prefixStream.Length, int.MaxValue))];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = prefixStream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0)
            {
                break;
            }

            offset += read;
        }

        return offset == buffer.Length ? buffer : buffer[..offset];
    }

    private IReadOnlyList<string> ReadCompleteLines()
    {
        var buffer = new char[readBufferSize];
        while (true)
        {
            var read = reader!.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            pendingText.Append(buffer, 0, read);
            if (read < buffer.Length)
            {
                break;
            }
        }

        var text = pendingText.ToString();
        var lastNewline = text.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            return [];
        }

        var completeText = text[..(lastNewline + 1)];
        var remainder = text[(lastNewline + 1)..];
        pendingText.Clear();
        pendingText.Append(remainder);

        var lines = new List<string>();
        using var lineReader = new StringReader(completeText);
        while (lineReader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }

    private NinaLogTailerReadResult ParseLinesHoldingLastEvent(IReadOnlyList<string> lines)
    {
        pendingEventLines.AddRange(lines);

        IReadOnlyList<NinaLogEvent> events;
        try
        {
            events = NinaLogParser.ParseLines(pendingEventLines);
        }
        catch
        {
            pendingEventLines.Clear();
            return new NinaLogTailerReadResult([], HasNewLines: true);
        }

        if (events.Count == 0)
        {
            pendingEventLines.Clear();
            return new NinaLogTailerReadResult([], HasNewLines: true);
        }

        if (events.Count == 1)
        {
            return new NinaLogTailerReadResult([], HasNewLines: true);
        }

        var readyEvents = events.Take(events.Count - 1).ToArray();
        ReplacePendingEventLines(events[^1]);
        return new NinaLogTailerReadResult(readyEvents, HasNewLines: true);
    }

    private void ReplacePendingEventLines(NinaLogEvent logEvent)
    {
        pendingEventLines.Clear();
        pendingEventLines.AddRange(logEvent.RawLine.Split(
            ["\r\n", "\n"],
            StringSplitOptions.None));
    }

    private void ResetReader(bool clearPending = false)
    {
        try
        {
            reader?.Dispose();
            if (reader is null)
            {
                stream?.Dispose();
            }
        }
        catch
        {
            // Log tailing must never fail plugin shutdown or retry paths.
        }

        reader = null;
        stream = null;
        openedCreationTimeUtc = default;
        observedLastWriteTimeUtc = default;
        observedLength = 0;
        prefixSnapshot = [];
        if (clearPending)
        {
            pendingText.Clear();
            pendingEventLines.Clear();
        }
    }
}

internal sealed record NinaLogTailerReadResult(
    IReadOnlyList<NinaLogEvent> Events,
    bool HasNewLines);
