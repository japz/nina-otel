using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

internal sealed class DiskTelemetrySpool
{
    private const long DefaultMaxBytes = 1L * 1024 * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, SpoolAccess> AccessByPath = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(7);

    private readonly string spoolPath;
    private readonly long maxBytes;
    private readonly TimeSpan maxAge;
    private readonly Func<string, TelemetryPriority> evictionPriorityResolver;
    private readonly SpoolAccess access;
    private long droppedRecords;

    public DiskTelemetrySpool(string spoolPath)
        : this(spoolPath, DefaultMaxBytes, DefaultMaxAge)
    {
    }

    public DiskTelemetrySpool(string spoolPath, long maxBytes, TimeSpan maxAge)
        : this(spoolPath, maxBytes, maxAge, GetEvictionPriority)
    {
    }

    internal DiskTelemetrySpool(
        string spoolPath,
        long maxBytes,
        TimeSpan maxAge,
        Func<string, TelemetryPriority> evictionPriorityResolver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolPath);
        ArgumentNullException.ThrowIfNull(evictionPriorityResolver);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Max bytes must be positive.");
        }

        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), "Max age must be positive.");
        }

        this.spoolPath = Path.GetFullPath(ExpandLocalAppData(spoolPath));
        access = AccessByPath.GetOrAdd(this.spoolPath, _ => new SpoolAccess());
        this.maxBytes = maxBytes;
        this.maxAge = maxAge;
        this.evictionPriorityResolver = evictionPriorityResolver;
    }

    public async Task AppendBatchAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(spoolPath);

        var baseName = CreateSpoolFileBaseName();
        var temporaryPath = Path.Combine(spoolPath, baseName + ".tmp");
        var readyPath = new ReplayFile(Path.Combine(spoolPath, baseName), GetSignals(records), false, records.Count,
            records.Select(record => (DateTimeOffset?)record.Timestamp).Min()).ReadyPath;
        var dto = BatchDto.FromRecords(records);

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, dto, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            lock (access)
            {
                PruneExpiredFiles(DateTime.UtcNow);
                File.Move(temporaryPath, readyPath);
                EnforceMaxBytes(readyPath);
            }
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    internal Task AcquireReplayOwnershipAsync(CancellationToken token) => access.ReplayOwner.WaitAsync(token);

    internal void ReleaseReplayOwnership() => access.ReplayOwner.Release();

    internal IEnumerable<string> EnumerateReadyFiles() => Directory.Exists(spoolPath)
        ? Directory.EnumerateFiles(spoolPath, "*.ready")
        : [];

    internal IReadOnlyList<string> GetReplayPage(SpoolSignal eligible, int pageSize)
    {
        // Keep only the oldest page of names, without loading or sorting the entire directory.
        var oldest = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in EnumerateReadyFiles())
        {
            var file = ReplayFile.Parse(path);
            if ((file.Pending & eligible) == 0)
            {
                continue;
            }

            if (oldest.Count < pageSize || StringComparer.Ordinal.Compare(path, oldest.Max) < 0)
            {
                oldest.Add(path);
                if (oldest.Count > pageSize)
                {
                    oldest.Remove(oldest.Max!);
                }
            }
        }

        return oldest.ToArray();
    }

    internal bool TryReserve(string path)
    {
        lock (access)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            access.ActiveReplayFile = path;
            return true;
        }
    }

    internal void Release()
    {
        lock (access)
        {
            access.ActiveReplayFile = null;
        }
    }

    internal void FinishAttempt(string path, ReplayFile file)
    {
        lock (access)
        {
            if (file.Pending != SpoolSignal.None)
            {
                if (!string.Equals(path, file.ReadyPath, StringComparison.Ordinal))
                {
                    File.Move(path, file.ReadyPath);
                }
                return;
            }

            if (file.HasInvalidSignal)
            {
                File.Move(path, CreateSidecarPath(path, "invalid"));
            }
            else
            {
                CompleteFile(path, File.Move, File.Delete);
            }
        }
    }

    internal Stats GetStats()
    {
        var queuedBatches = 0;
        var queuedRecords = 0;
        long queuedBytes = 0;
        DateTimeOffset? oldestQueuedTimestamp = null;
        foreach (var path in EnumerateReadyFiles())
        {
            try
            {
                queuedBytes += new FileInfo(path).Length;
            }
            catch (FileNotFoundException)
            {
                continue;
            }

            var file = ReplayFile.Parse(path);
            queuedBatches++;
            queuedRecords += file.RecordCount;
            var timestamp = file.OldestTimestamp ?? file.EnqueueTime;
            if (timestamp is not null && (oldestQueuedTimestamp is null || timestamp < oldestQueuedTimestamp))
            {
                oldestQueuedTimestamp = timestamp;
            }
        }

        return new Stats(
            queuedBatches, queuedRecords, queuedBytes, oldestQueuedTimestamp, Interlocked.Read(ref droppedRecords));
    }

    internal static SpoolSignal GetSignals(IReadOnlyList<TelemetryRecord> records)
    {
        var signals = SpoolSignal.None;
        foreach (var record in records)
        {
            signals |= record.Signal switch
            {
                TelemetrySignal.Metric => SpoolSignal.Metrics,
                TelemetrySignal.Span => SpoolSignal.Logs | SpoolSignal.Traces,
                _ => SpoolSignal.Logs,
            };
        }

        return signals;
    }

    // The JSON remains untouched. A rename atomically records which signals still need delivery.
    // Legacy alpha.57 names have no metadata and are discovered the first time the payload is read.
    internal sealed record ReplayFile(
        string BasePath, SpoolSignal Pending, bool HasInvalidSignal, int RecordCount, DateTimeOffset? OldestTimestamp)
    {
        public string ReadyPath => FormattableString.Invariant(
            $"{BasePath}~{(int)Pending}~{(HasInvalidSignal ? 1 : 0)}~{RecordCount}~{OldestTimestamp?.UtcTicks ?? 0}.ready");

        public DateTimeOffset? EnqueueTime
        {
            get
            {
                var name = Path.GetFileName(BasePath);
                var separator = name.IndexOf('-');
                return separator > 0 && long.TryParse(name.AsSpan(0, separator), out var ticks)
                    ? ReadTimestamp(ticks)
                    : null;
            }
        }

        public static ReplayFile Parse(string path)
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var parts = stem.Split('~');
            var basePath = Path.Combine(Path.GetDirectoryName(path)!, parts[0]);
            if (parts.Length == 5 &&
                int.TryParse(parts[1], out var signals) &&
                signals > 0 && signals <= (int)SpoolSignal.All &&
                int.TryParse(parts[3], out var records) &&
                long.TryParse(parts[4], out var ticks))
            {
                return new ReplayFile(basePath, (SpoolSignal)signals, parts[2] == "1", records, ReadTimestamp(ticks));
            }

            return new ReplayFile(basePath, SpoolSignal.All, false, 0, null);
        }

        private static DateTimeOffset? ReadTimestamp(long ticks) =>
            ticks > 0 && ticks <= DateTimeOffset.MaxValue.Ticks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    }

    private void PruneExpiredFiles(DateTime utcNow)
    {
        var cutoff = utcNow - maxAge;
        foreach (var path in EnumerateSpoolFiles())
        {
            if (!string.Equals(path, access.ActiveReplayFile, StringComparison.Ordinal) && File.GetLastWriteTimeUtc(path) < cutoff)
            {
                if (path.EndsWith(".ready", StringComparison.Ordinal))
                {
                    TryDeleteDroppedBatch(path);
                }
                else
                {
                    TryDelete(path);
                }
            }
        }
    }

    private void EnforceMaxBytes(string newestReadyPath)
    {
        var newestFullPath = Path.GetFullPath(newestReadyPath);
        var files = EnumerateSpoolFiles()
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .ToList();
        var totalBytes = files.Sum(file => file.Length);
        if (totalBytes <= maxBytes)
        {
            return;
        }

        var candidates = files
            .Select(file =>
            {
                var isProtected = string.Equals(file.FullName, newestFullPath, StringComparison.Ordinal) ||
                    string.Equals(file.FullName, access.ActiveReplayFile, StringComparison.Ordinal);
                return new EvictionCandidate(
                    file,
                    isProtected,
                    isProtected ? TelemetryPriority.Debug : evictionPriorityResolver(file.FullName));
            })
            .OrderBy(candidate => candidate.IsProtected)
            .ThenBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.File.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var candidate in candidates)
        {
            if (totalBytes <= maxBytes)
            {
                return;
            }

            var file = candidate.File;
            if (candidate.IsProtected)
            {
                continue;
            }

            var length = file.Length;
            if (file.FullName.EndsWith(".ready", StringComparison.Ordinal))
            {
                TryDeleteDroppedBatch(file.FullName);
            }
            else
            {
                TryDelete(file.FullName);
            }

            if (!File.Exists(file.FullName))
            {
                totalBytes -= length;
            }
        }

        if (totalBytes <= maxBytes)
        {
            return;
        }

        TryDeleteDroppedBatch(newestReadyPath);
        throw new IOException($"Telemetry spool batch '{newestReadyPath}' exceeds max spool bytes '{maxBytes}'.");
    }

    private static TelemetryPriority GetEvictionPriority(string path)
    {
        if (!path.EndsWith(".ready", StringComparison.Ordinal))
        {
            return TelemetryPriority.Debug;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024);
            var dto = JsonSerializer.Deserialize<BatchDto>(stream, JsonOptions);
            if (dto is null)
            {
                return TelemetryPriority.Debug;
            }

            return dto.ToRecords()
                .Select(record => record.Priority)
                .DefaultIfEmpty(TelemetryPriority.Debug)
                .Max();
        }
        catch
        {
            return TelemetryPriority.Debug;
        }
    }

    private IEnumerable<string> EnumerateSpoolFiles()
    {
        if (!Directory.Exists(spoolPath))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(spoolPath, "*.ready")
            .Concat(Directory.EnumerateFiles(spoolPath, "*.invalid"))
            .Concat(Directory.EnumerateFiles(spoolPath, "*.sent"));
    }

    private static string ExpandLocalAppData(string path)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return path.Replace("%LOCALAPPDATA%", localAppData, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateSpoolFileBaseName() =>
        DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");

    internal static async Task<Batch> ReadBatchAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            var dto = await JsonSerializer.DeserializeAsync<BatchDto>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (dto is null)
            {
                throw new InvalidDataException($"Telemetry spool batch '{path}' is empty or invalid.");
            }

            return new Batch(path, dto.ToRecords());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            throw new BatchReadException(path, ex);
        }
    }

    private static string CreateSidecarPath(string path, string extension)
    {
        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var fileName = Path.GetFileName(path);
        return Path.Combine(directory, fileName + "-" + Guid.NewGuid().ToString("N") + "." + extension);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class SpoolAccess
    {
        public SemaphoreSlim ReplayOwner { get; } = new(1, 1);
        public string? ActiveReplayFile { get; set; }
    }

    private sealed record EvictionCandidate(FileInfo File, bool IsProtected, TelemetryPriority Priority);

    internal sealed class Batch
    {
        private readonly Action<string, string> moveFile;
        private readonly Action<string> deleteFile;
        private readonly string path;

        public Batch(string path, IReadOnlyList<TelemetryRecord> records)
            : this(path, records, File.Move, File.Delete)
        {
        }

        internal Batch(
            string path,
            IReadOnlyList<TelemetryRecord> records,
            Action<string, string> moveFile,
            Action<string> deleteFile)
        {
            this.path = path;
            this.moveFile = moveFile;
            this.deleteFile = deleteFile;
            Records = records;
        }

        public IReadOnlyList<TelemetryRecord> Records { get; }

        public string Id => ReplayFile.Parse(path).BasePath;

        public void Complete() => CompleteFile(path, moveFile, deleteFile);
    }

    private static void CompleteFile(string path, Action<string, string> moveFile, Action<string> deleteFile)
    {
        var sentPath = CreateSidecarPath(path, "sent");
        moveFile(path, sentPath);
        try
        {
            deleteFile(sentPath);
        }
        catch
        {
            // The atomic rename already took this file out of the ready queue.
        }
    }

    private void TryDeleteDroppedBatch(string path)
    {
        var droppedRecordCount = CountReadyRecordsForDrop(path);
        TryDelete(path);
        if (!File.Exists(path) && droppedRecordCount > 0)
        {
            Interlocked.Add(ref droppedRecords, droppedRecordCount);
        }
    }

    private static int CountReadyRecordsForDrop(string path)
    {
        if (!path.EndsWith(".ready", StringComparison.Ordinal))
        {
            return 0;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024);
            var dto = JsonSerializer.Deserialize<BatchDto>(stream, JsonOptions);
            return dto?.Records.Count ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    internal sealed class BatchReadException : IOException
    {
        public BatchReadException(string path, Exception innerException)
            : base($"Telemetry spool batch '{path}' could not be read.", innerException)
        {
            Path = path;
        }

        public string Path { get; }
    }

    public sealed record Stats(
        int QueuedBatches,
        int QueuedRecords,
        long QueuedBytes,
        DateTimeOffset? OldestQueuedTimestamp,
        long DroppedRecords)
    {
        public static Stats Empty { get; } = new(0, 0, 0, null, 0);
    }

    internal sealed record TraceCheckpoint(
        string BatchId, byte[] Payload, IReadOnlyList<OtlpActiveSpanSnapshot> ActiveSpans)
    {
        public static async Task<TraceCheckpoint?> LoadAsync(Batch batch, CancellationToken token)
        {
            var path = GetPath(batch);
            try
            {
                await using var stream = File.OpenRead(path);
                var dto = await JsonSerializer.DeserializeAsync<TraceCheckpointDto>(stream, JsonOptions, token)
                    .ConfigureAwait(false);
                return dto is null ? throw new InvalidDataException("Empty trace replay checkpoint.") : new(
                    dto.BatchId, dto.Payload, dto.Spans.Select(span => new OtlpActiveSpanSnapshot(
                        span.Start.ToRecord(), span.TraceIdSeed, span.Events.ToRecords(), span.DroppedEventCount)).ToArray());
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        public async Task SaveAsync(Batch batch, CancellationToken token)
        {
            var path = GetPath(batch);
            var temporaryPath = path + ".tmp";
            var dto = new TraceCheckpointDto(BatchId, Payload, ActiveSpans.Select(span => new ActiveSpanDto(
                RecordDto.FromRecord(span.Start), span.TraceIdSeed,
                BatchDto.FromRecords(span.Events), span.DroppedEventCount)).ToArray());
            try
            {
                await using (var stream = File.Create(temporaryPath))
                {
                    await JsonSerializer.SerializeAsync(stream, dto, JsonOptions, token).ConfigureAwait(false);
                }
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }

        public static void Remove(Batch batch) => TryDelete(GetPath(batch));

        private static string GetPath(Batch batch) =>
            Path.Combine(Path.GetDirectoryName(batch.Id)!, "trace-replay.checkpoint");

        private sealed record TraceCheckpointDto(string BatchId, byte[] Payload, ActiveSpanDto[] Spans);
        private sealed record ActiveSpanDto(RecordDto Start, string TraceIdSeed, BatchDto Events, int DroppedEventCount);
    }

    private sealed class BatchDto
    {
        public required List<RecordDto> Records { get; init; }

        public static BatchDto FromRecords(IReadOnlyList<TelemetryRecord> records) =>
            new()
            {
                Records = records.Select(RecordDto.FromRecord).ToList(),
            };

        public IReadOnlyList<TelemetryRecord> ToRecords() =>
            Records?.Select(record => record?.ToRecord() ??
                throw new InvalidDataException("Null telemetry record.")).ToArray() ??
            throw new InvalidDataException("Missing telemetry records.");
    }

    private sealed class RecordDto
    {
        public TelemetrySignal Signal { get; init; }
        public DateTimeOffset Timestamp { get; init; }
        public string Source { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public TelemetryPriority Priority { get; init; }
        public List<AttributeDto> Attributes { get; init; } = [];
        public double? NumericValue { get; init; }
        public string? Body { get; init; }
        public TelemetrySeverity? Severity { get; init; }
        public SpanEventKind? SpanKind { get; init; }
        public string? SpanId { get; init; }
        public string? ParentSpanId { get; init; }
        public string? TraceId { get; init; }

        public static RecordDto FromRecord(TelemetryRecord record) =>
            new()
            {
                Signal = record.Signal,
                Timestamp = record.Timestamp,
                Source = record.Source,
                Name = record.Name,
                Priority = record.Priority,
                Attributes = record.Attributes.Select(AttributeDto.FromAttribute).ToList(),
                NumericValue = record.NumericValue,
                Body = record.Body,
                Severity = record.Severity,
                SpanKind = record.SpanKind,
                SpanId = record.SpanId,
                ParentSpanId = record.ParentSpanId,
                TraceId = record.TraceId,
            };

        public TelemetryRecord ToRecord()
        {
            if (!Enum.IsDefined(Signal) || Timestamp == default || string.IsNullOrWhiteSpace(Name) ||
                string.IsNullOrWhiteSpace(Source) || Attributes is null || Attributes.Any(attribute => attribute is null))
            {
                throw new InvalidDataException("Invalid telemetry record.");
            }

            return new(
                Signal,
                Timestamp,
                Source,
                Name,
                Priority,
                new ReadOnlyDictionary<string, object?>(
                    Attributes.ToDictionary(attribute => attribute.Key, attribute => attribute.ToValue())),
                NumericValue,
                Body,
                Severity,
                SpanKind,
                SpanId,
                ParentSpanId)
            {
                TraceId = TraceId,
            };
        }
    }

    private sealed class AttributeDto
    {
        private const string NullType = "null";
        private const string StringType = "string";
        private const string BooleanType = "bool";
        private const string ByteType = "byte";
        private const string SByteType = "sbyte";
        private const string Int16Type = "short";
        private const string UInt16Type = "ushort";
        private const string Int32Type = "int";
        private const string UInt32Type = "uint";
        private const string Int64Type = "long";
        private const string UInt64Type = "ulong";
        private const string DoubleType = "double";
        private const string SingleType = "float";
        private const string DecimalType = "decimal";

        public string Key { get; init; } = string.Empty;
        public string Type { get; init; } = NullType;
        public string? StringValue { get; init; }
        public bool? BooleanValue { get; init; }
        public byte? ByteValue { get; init; }
        public sbyte? SByteValue { get; init; }
        public short? Int16Value { get; init; }
        public ushort? UInt16Value { get; init; }
        public int? Int32Value { get; init; }
        public uint? UInt32Value { get; init; }
        public long? Int64Value { get; init; }
        public ulong? UInt64Value { get; init; }
        public double? DoubleValue { get; init; }
        public float? SingleValue { get; init; }
        public decimal? DecimalValue { get; init; }

        public static AttributeDto FromAttribute(KeyValuePair<string, object?> attribute)
        {
            return attribute.Value switch
            {
                null => new AttributeDto { Key = attribute.Key, Type = NullType },
                string value => new AttributeDto { Key = attribute.Key, Type = StringType, StringValue = value },
                bool value => new AttributeDto { Key = attribute.Key, Type = BooleanType, BooleanValue = value },
                byte value => new AttributeDto { Key = attribute.Key, Type = ByteType, ByteValue = value },
                sbyte value => new AttributeDto { Key = attribute.Key, Type = SByteType, SByteValue = value },
                short value => new AttributeDto { Key = attribute.Key, Type = Int16Type, Int16Value = value },
                ushort value => new AttributeDto { Key = attribute.Key, Type = UInt16Type, UInt16Value = value },
                int value => new AttributeDto { Key = attribute.Key, Type = Int32Type, Int32Value = value },
                uint value => new AttributeDto { Key = attribute.Key, Type = UInt32Type, UInt32Value = value },
                long value => new AttributeDto { Key = attribute.Key, Type = Int64Type, Int64Value = value },
                ulong value => new AttributeDto { Key = attribute.Key, Type = UInt64Type, UInt64Value = value },
                double value => new AttributeDto { Key = attribute.Key, Type = DoubleType, DoubleValue = value },
                float value => new AttributeDto { Key = attribute.Key, Type = SingleType, SingleValue = value },
                decimal value => new AttributeDto { Key = attribute.Key, Type = DecimalType, DecimalValue = value },
                _ => throw new NotSupportedException(
                    $"Telemetry spool attribute '{attribute.Key}' has unsupported value type '{attribute.Value.GetType().FullName}'."),
            };
        }

        public object? ToValue()
        {
            return Type switch
            {
                NullType => null,
                StringType => StringValue,
                BooleanType => BooleanValue,
                ByteType => ByteValue,
                SByteType => SByteValue,
                Int16Type => Int16Value,
                UInt16Type => UInt16Value,
                Int32Type => Int32Value,
                UInt32Type => UInt32Value,
                Int64Type => Int64Value,
                UInt64Type => UInt64Value,
                DoubleType => DoubleValue,
                SingleType => SingleValue,
                DecimalType => DecimalValue,
                _ => throw new NotSupportedException($"Telemetry spool attribute type '{Type}' is not supported."),
            };
        }
    }
}
