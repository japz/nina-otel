using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using NinaOtel.Abstractions.Telemetry;

namespace NinaOtel.Core.Pipeline;

internal sealed class DiskTelemetrySpool
{
    private const long DefaultMaxBytes = 1L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(7);

    private readonly string spoolPath;
    private readonly long maxBytes;
    private readonly TimeSpan maxAge;

    public DiskTelemetrySpool(string spoolPath)
        : this(spoolPath, DefaultMaxBytes, DefaultMaxAge)
    {
    }

    public DiskTelemetrySpool(string spoolPath, long maxBytes, TimeSpan maxAge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolPath);
        if (maxBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Max bytes must be positive.");
        }

        if (maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAge), "Max age must be positive.");
        }

        this.spoolPath = ExpandLocalAppData(spoolPath);
        this.maxBytes = maxBytes;
        this.maxAge = maxAge;
    }

    public async Task AppendBatchAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(spoolPath);
        PruneExpiredFiles(DateTime.UtcNow);

        var baseName = CreateSpoolFileBaseName();
        var temporaryPath = Path.Combine(spoolPath, baseName + ".tmp");
        var readyPath = Path.Combine(spoolPath, baseName + ".ready");
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

            File.Move(temporaryPath, readyPath);
            EnforceMaxBytes(readyPath);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public async Task<IReadOnlyList<Batch>> ReadBatchesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(spoolPath))
        {
            return Array.Empty<Batch>();
        }

        var batches = new List<Batch>();
        foreach (var path in Directory.EnumerateFiles(spoolPath, "*.ready").OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            batches.Add(await ReadBatchAsync(path, cancellationToken).ConfigureAwait(false));
        }

        return batches;
    }

    public async Task<Stats> GetStatsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Directory.Exists(spoolPath))
        {
            return Stats.Empty;
        }

        var readyFiles = Directory.EnumerateFiles(spoolPath, "*.ready")
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .OrderBy(file => file.FullName, StringComparer.Ordinal)
            .ToArray();
        var queuedBytes = readyFiles.Sum(file => file.Length);
        var queuedRecords = 0;
        DateTimeOffset? oldestQueuedTimestamp = null;

        foreach (var file in readyFiles)
        {
            Batch batch;
            try
            {
                batch = await ReadBatchAsync(file.FullName, cancellationToken).ConfigureAwait(false);
            }
            catch (BatchReadException)
            {
                continue;
            }

            queuedRecords += batch.Records.Count;
            foreach (var record in batch.Records)
            {
                if (oldestQueuedTimestamp is null || record.Timestamp < oldestQueuedTimestamp)
                {
                    oldestQueuedTimestamp = record.Timestamp;
                }
            }
        }

        return new Stats(
            readyFiles.Length,
            queuedRecords,
            queuedBytes,
            oldestQueuedTimestamp);
    }

    public void Quarantine(BatchReadException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (!File.Exists(exception.Path))
        {
            return;
        }

        File.Move(exception.Path, CreateSidecarPath(exception.Path, "invalid"));
    }

    private void PruneExpiredFiles(DateTime utcNow)
    {
        var cutoff = utcNow - maxAge;
        foreach (var path in EnumerateSpoolFiles())
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff)
            {
                TryDelete(path);
            }
        }
    }

    private void EnforceMaxBytes(string newestReadyPath)
    {
        var newestFullPath = Path.GetFullPath(newestReadyPath);
        var files = EnumerateSpoolFiles()
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists)
            .Select(file => new EvictionCandidate(
                file,
                string.Equals(file.FullName, newestFullPath, StringComparison.Ordinal),
                GetEvictionPriority(file.FullName)))
            .OrderBy(candidate => candidate.IsNewestProtected)
            .ThenBy(candidate => candidate.Priority)
            .ThenBy(candidate => candidate.File.FullName, StringComparer.Ordinal)
            .ToList();
        var totalBytes = files.Sum(candidate => candidate.File.Length);

        foreach (var candidate in files)
        {
            if (totalBytes <= maxBytes)
            {
                return;
            }

            var file = candidate.File;
            if (candidate.IsNewestProtected)
            {
                continue;
            }

            var length = file.Length;
            TryDelete(file.FullName);
            if (!File.Exists(file.FullName))
            {
                totalBytes -= length;
            }
        }

        if (totalBytes <= maxBytes)
        {
            return;
        }

        TryDelete(newestReadyPath);
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

    private static async Task<Batch> ReadBatchAsync(string path, CancellationToken cancellationToken)
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
        catch (Exception ex)
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

    private sealed record EvictionCandidate(FileInfo File, bool IsNewestProtected, TelemetryPriority Priority);

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

        public void Complete()
        {
            var sentPath = CreateSidecarPath(path, "sent");
            moveFile(path, sentPath);
            try
            {
                deleteFile(sentPath);
            }
            catch
            {
            }
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
        DateTimeOffset? OldestQueuedTimestamp)
    {
        public static Stats Empty { get; } = new(0, 0, 0, null);
    }

    private sealed class BatchDto
    {
        public List<RecordDto> Records { get; init; } = [];

        public static BatchDto FromRecords(IReadOnlyList<TelemetryRecord> records) =>
            new()
            {
                Records = records.Select(RecordDto.FromRecord).ToList(),
            };

        public IReadOnlyList<TelemetryRecord> ToRecords() =>
            Records.Select(record => record.ToRecord()).ToArray();
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

        public TelemetryRecord ToRecord() =>
            new(
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
