using System.Globalization;
using FluentAssertions;
using NinaOtel.Abstractions.Telemetry;
using NinaOtel.Core.Pipeline;
using Xunit;

namespace NinaOtel.Core.Tests.Pipeline;

public sealed class DiskTelemetrySpoolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "nina-otel-spool-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Constructor_DoesNotCreateSpoolDirectory()
    {
        var spoolPath = Path.Combine(root, "spool");

        _ = new DiskTelemetrySpool(spoolPath);

        Directory.Exists(spoolPath).Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithLimits_DoesNotCreateSpoolDirectory()
    {
        var spoolPath = Path.Combine(root, "spool");

        _ = new DiskTelemetrySpool(spoolPath, maxBytes: 1024, maxAge: TimeSpan.FromDays(1));

        Directory.Exists(spoolPath).Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WhenMaxBytesIsNotPositive_ThrowsArgumentOutOfRangeException(long maxBytes)
    {
        var spoolPath = Path.Combine(root, "spool");

        Action create = () => _ = new DiskTelemetrySpool(spoolPath, maxBytes, TimeSpan.FromDays(1));

        create.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maxBytes");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_WhenMaxAgeIsNotPositive_ThrowsArgumentOutOfRangeException(long maxAgeTicks)
    {
        var spoolPath = Path.Combine(root, "spool");

        Action create = () => _ = new DiskTelemetrySpool(spoolPath, 1024, TimeSpan.FromTicks(maxAgeTicks));

        create.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maxAge");
    }

    [Fact]
    public async Task AppendBatchAsync_CreatesSpoolDirectoryAndPersistsRecords()
    {
        var spoolPath = Path.Combine(root, "spool");
        var spool = new DiskTelemetrySpool(spoolPath);
        var record = TelemetryRecord.Health(
            DateTimeOffset.Parse("2026-06-20T10:15:00Z", CultureInfo.InvariantCulture),
            "collector",
            "offline",
            TelemetryPriority.Important,
            new Dictionary<string, object?> { ["reason"] = "test" });

        await spool.AppendBatchAsync(new[] { record }, CancellationToken.None);

        Directory.Exists(spoolPath).Should().BeTrue();
        Directory.GetFiles(spoolPath, "*.tmp").Should().BeEmpty();
        Directory.GetFiles(spoolPath, "*.ready").Should().ContainSingle();

        var batches = await spool.ReadBatchesAsync(CancellationToken.None);
        batches.Should().ContainSingle();
        batches[0].Records.Should().ContainSingle().Which.Should().BeEquivalentTo(record);
    }

    [Fact]
    public async Task ReadBatchesAsync_AfterNewInstance_ReturnsPersistedRecords()
    {
        var spoolPath = Path.Combine(root, "spool");
        var firstSpool = new DiskTelemetrySpool(spoolPath);
        var record = TelemetryRecord.Log(
            DateTimeOffset.Parse("2026-06-20T10:16:00Z", CultureInfo.InvariantCulture),
            "pipeline",
            TelemetrySeverity.Warning,
            "collector unavailable",
            TelemetryPriority.Normal,
            new Dictionary<string, object?> { ["attempt"] = 1 });

        await firstSpool.AppendBatchAsync(new[] { record }, CancellationToken.None);

        var secondSpool = new DiskTelemetrySpool(spoolPath);
        var batches = await secondSpool.ReadBatchesAsync(CancellationToken.None);

        batches.Should().ContainSingle();
        batches[0].Records.Should().ContainSingle().Which.Should().BeEquivalentTo(record);
    }

    [Fact]
    public async Task Complete_DeletesOnlyDeliveredBatch()
    {
        var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"));
        var first = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "first", TelemetryPriority.Routine);
        var second = TelemetryRecord.Health(DateTimeOffset.UtcNow.AddMilliseconds(1), "test", "second", TelemetryPriority.Routine);

        await spool.AppendBatchAsync(new[] { first }, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(2));
        await spool.AppendBatchAsync(new[] { second }, CancellationToken.None);

        var batches = await spool.ReadBatchesAsync(CancellationToken.None);
        batches.Should().HaveCount(2);

        batches[0].Complete();

        var remaining = await spool.ReadBatchesAsync(CancellationToken.None);
        remaining.Should().ContainSingle();
        remaining[0].Records.Should().ContainSingle().Which.Name.Should().Be("second");
    }

    [Fact]
    public async Task AppendBatchAsync_PrunesFilesOlderThanMaxAge()
    {
        var spoolPath = Path.Combine(root, "spool");
        Directory.CreateDirectory(spoolPath);
        var expiredReady = Path.Combine(spoolPath, "0000000000000000001-old.ready");
        var expiredInvalid = Path.Combine(spoolPath, "0000000000000000002-old.invalid");
        var expiredSent = Path.Combine(spoolPath, "0000000000000000003-old.sent");
        var recentReady = Path.Combine(spoolPath, "0000000000000000004-recent.ready");
        await File.WriteAllTextAsync(expiredReady, "{}");
        await File.WriteAllTextAsync(expiredInvalid, "{}");
        await File.WriteAllTextAsync(expiredSent, "{}");
        await File.WriteAllTextAsync(recentReady, "{}");
        File.SetLastWriteTimeUtc(expiredReady, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(expiredInvalid, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(expiredSent, DateTime.UtcNow.AddDays(-8));
        File.SetLastWriteTimeUtc(recentReady, DateTime.UtcNow);
        var spool = new DiskTelemetrySpool(spoolPath, maxBytes: 1024 * 1024, maxAge: TimeSpan.FromDays(7));

        await spool.AppendBatchAsync(new[] { CreateHealthRecord("new") }, CancellationToken.None);

        File.Exists(expiredReady).Should().BeFalse();
        File.Exists(expiredInvalid).Should().BeFalse();
        File.Exists(expiredSent).Should().BeFalse();
        File.Exists(recentReady).Should().BeTrue();
        Directory.GetFiles(spoolPath, "*.ready").Should().HaveCount(2);
    }

    [Fact]
    public async Task AppendBatchAsync_WhenSpoolExceedsMaxBytes_DeletesOldestFilesFirst()
    {
        var spoolPath = Path.Combine(root, "spool");
        var maxBytes = 9_000;
        var spool = new DiskTelemetrySpool(spoolPath, maxBytes, TimeSpan.FromDays(7));

        await spool.AppendBatchAsync(new[] { CreateLargeRecord("first") }, CancellationToken.None);
        await spool.AppendBatchAsync(new[] { CreateLargeRecord("second") }, CancellationToken.None);
        await spool.AppendBatchAsync(new[] { CreateLargeRecord("third") }, CancellationToken.None);

        var batches = await spool.ReadBatchesAsync(CancellationToken.None);

        batches.SelectMany(batch => batch.Records).Select(record => record.Name)
            .Should().Equal("second", "third");
        TotalSpoolBytes(spoolPath).Should().BeLessThanOrEqualTo(maxBytes);
    }

    [Fact]
    public async Task AppendBatchAsync_WhenSpoolExceedsMaxBytes_DropsRoutineBatchBeforeOlderImportantBatch()
    {
        var spoolPath = Path.Combine(root, "spool");
        var maxBytes = 9_000;
        var spool = new DiskTelemetrySpool(spoolPath, maxBytes, TimeSpan.FromDays(7));

        await spool.AppendBatchAsync(new[] { CreateLargeRecord("important-old", TelemetryPriority.Important) }, CancellationToken.None);
        await spool.AppendBatchAsync(new[] { CreateLargeRecord("routine-newer", TelemetryPriority.Routine) }, CancellationToken.None);
        await spool.AppendBatchAsync(new[] { CreateLargeRecord("critical-newest", TelemetryPriority.Critical) }, CancellationToken.None);

        var batches = await spool.ReadBatchesAsync(CancellationToken.None);

        batches.SelectMany(batch => batch.Records).Select(record => record.Name)
            .Should().Equal("important-old", "critical-newest");
        TotalSpoolBytes(spoolPath).Should().BeLessThanOrEqualTo(maxBytes);
    }

    [Fact]
    public async Task AppendBatchAsync_WhenSpoolBytesAreWithinLimit_DoesNotReadExistingReadyFilesForEvictionPriority()
    {
        var spoolPath = Path.Combine(root, "spool");
        var priorityResolverCalls = 0;
        var spool = new DiskTelemetrySpool(
            spoolPath,
            maxBytes: 1024 * 1024,
            maxAge: TimeSpan.FromDays(7),
            _ =>
            {
                priorityResolverCalls++;
                throw new InvalidOperationException("Priority resolver should not run when spool bytes are within limit.");
            });

        await spool.AppendBatchAsync(new[] { CreateLargeRecord("existing") }, CancellationToken.None);
        await spool.AppendBatchAsync(new[] { CreateLargeRecord("new") }, CancellationToken.None);

        priorityResolverCalls.Should().Be(0);
        Directory.GetFiles(spoolPath, "*.ready").Should().HaveCount(2);
    }

    [Fact]
    public async Task AppendBatchAsync_WhenSpoolExceedsMaxBytes_DoesNotReadProtectedNewestBatchForEvictionPriority()
    {
        var spoolPath = Path.Combine(root, "spool");
        var resolvedPaths = new List<string>();
        var spool = new DiskTelemetrySpool(
            spoolPath,
            maxBytes: 6_000,
            maxAge: TimeSpan.FromDays(7),
            path =>
            {
                var newestReadyPath = Directory.GetFiles(spoolPath, "*.ready")
                    .OrderBy(readyPath => readyPath, StringComparer.Ordinal)
                    .Last();
                if (string.Equals(path, newestReadyPath, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Priority resolver should not run for the protected newest batch.");
                }

                resolvedPaths.Add(path);
                return TelemetryPriority.Debug;
            });

        await spool.AppendBatchAsync(new[] { CreateLargeRecord("old") }, CancellationToken.None);
        await spool.AppendBatchAsync(new[] { CreateLargeRecord("newest") }, CancellationToken.None);

        var batches = await spool.ReadBatchesAsync(CancellationToken.None);

        batches.SelectMany(batch => batch.Records).Select(record => record.Name)
            .Should().Equal("newest");
        resolvedPaths.Should().ContainSingle();
        TotalSpoolBytes(spoolPath).Should().BeLessThanOrEqualTo(6_000);
    }

    [Fact]
    public async Task AppendBatchAsync_WhenNewestBatchHasLowerPriority_KeepsNewestBatchProtected()
    {
        var spoolPath = Path.Combine(root, "spool");
        var maxBytes = 9_000;
        var spool = new DiskTelemetrySpool(spoolPath, maxBytes, TimeSpan.FromDays(7));

        await spool.AppendBatchAsync(new[] { CreateLargeRecord("important-old", TelemetryPriority.Important) }, CancellationToken.None);
        await spool.AppendBatchAsync(new[] { CreateLargeRecord("normal-middle", TelemetryPriority.Normal) }, CancellationToken.None);
        await spool.AppendBatchAsync(new[] { CreateLargeRecord("routine-newest", TelemetryPriority.Routine) }, CancellationToken.None);

        var batches = await spool.ReadBatchesAsync(CancellationToken.None);

        batches.SelectMany(batch => batch.Records).Select(record => record.Name)
            .Should().Equal("important-old", "routine-newest");
        TotalSpoolBytes(spoolPath).Should().BeLessThanOrEqualTo(maxBytes);
    }

    [Fact]
    public async Task AppendBatchAsync_WhenBatchHasMixedPriorities_RanksBatchByHighestPriorityRecord()
    {
        var spoolPath = Path.Combine(root, "spool");
        var maxBytes = 9_000;
        var spool = new DiskTelemetrySpool(spoolPath, maxBytes, TimeSpan.FromDays(7));

        await spool.AppendBatchAsync(
            new[]
            {
                CreateLargeRecord("routine-in-mixed", TelemetryPriority.Routine, payloadSize: 256),
                CreateLargeRecord("critical-in-mixed", TelemetryPriority.Critical),
            },
            CancellationToken.None);
        await spool.AppendBatchAsync(new[] { CreateLargeRecord("important-single", TelemetryPriority.Important) }, CancellationToken.None);
        await spool.AppendBatchAsync(new[] { CreateLargeRecord("normal-newest", TelemetryPriority.Normal) }, CancellationToken.None);

        var batches = await spool.ReadBatchesAsync(CancellationToken.None);

        batches.SelectMany(batch => batch.Records).Select(record => record.Name)
            .Should().Equal("routine-in-mixed", "critical-in-mixed", "normal-newest");
        TotalSpoolBytes(spoolPath).Should().BeLessThanOrEqualTo(maxBytes);
    }

    [Fact]
    public async Task AppendBatchAsync_WhenNewBatchAloneExceedsMaxBytes_DeletesItAndThrowsIOException()
    {
        var spoolPath = Path.Combine(root, "spool");
        var spool = new DiskTelemetrySpool(spoolPath, maxBytes: 32, maxAge: TimeSpan.FromDays(7));

        Func<Task> append = async () => await spool.AppendBatchAsync(
            new[] { CreateLargeRecord("oversized") },
            CancellationToken.None);

        await append.Should().ThrowAsync<IOException>();
        Directory.GetFiles(spoolPath, "*.ready").Should().BeEmpty();
    }

    [Fact]
    public async Task AppendBatchAsync_PreservesSupportedAttributeValueTypes()
    {
        var spool = new DiskTelemetrySpool(Path.Combine(root, "spool"));
        var attributes = new Dictionary<string, object?>
        {
            ["string"] = "value",
            ["bool"] = true,
            ["byte"] = (byte)1,
            ["sbyte"] = (sbyte)-2,
            ["short"] = (short)7,
            ["ushort"] = (ushort)8,
            ["int"] = 42,
            ["uint"] = 43U,
            ["long"] = 1234567890123L,
            ["ulong"] = 1234567890124UL,
            ["double"] = 1.25d,
            ["float"] = 2.5f,
            ["decimal"] = 3.75m,
            ["null"] = null,
        };
        var record = TelemetryRecord.Span(
            DateTimeOffset.Parse("2026-06-20T10:17:00Z", CultureInfo.InvariantCulture),
            "capture",
            "exposure",
            SpanEventKind.Stop,
            "span-1",
            TelemetryPriority.Critical,
            attributes,
            parentSpanId: "parent-1") with
        {
            TraceId = "trace-1",
        };

        await spool.AppendBatchAsync(new[] { record }, CancellationToken.None);

        var batch = (await spool.ReadBatchesAsync(CancellationToken.None)).Should().ContainSingle().Subject;
        var persisted = batch.Records.Should().ContainSingle().Subject;

        persisted.Should().BeEquivalentTo(record);
        persisted.Attributes["string"].Should().BeOfType<string>().Which.Should().Be("value");
        persisted.Attributes["bool"].Should().BeOfType<bool>().Which.Should().BeTrue();
        persisted.Attributes["byte"].Should().BeOfType<byte>().Which.Should().Be(1);
        persisted.Attributes["sbyte"].Should().BeOfType<sbyte>().Which.Should().Be(-2);
        persisted.Attributes["short"].Should().BeOfType<short>().Which.Should().Be(7);
        persisted.Attributes["ushort"].Should().BeOfType<ushort>().Which.Should().Be(8);
        persisted.Attributes["int"].Should().BeOfType<int>().Which.Should().Be(42);
        persisted.Attributes["uint"].Should().BeOfType<uint>().Which.Should().Be(43U);
        persisted.Attributes["long"].Should().BeOfType<long>().Which.Should().Be(1234567890123L);
        persisted.Attributes["ulong"].Should().BeOfType<ulong>().Which.Should().Be(1234567890124UL);
        persisted.Attributes["double"].Should().BeOfType<double>().Which.Should().Be(1.25d);
        persisted.Attributes["float"].Should().BeOfType<float>().Which.Should().Be(2.5f);
        persisted.Attributes["decimal"].Should().BeOfType<decimal>().Which.Should().Be(3.75m);
        persisted.Attributes.Should().ContainKey("null").WhoseValue.Should().BeNull();
    }

    [Fact]
    public async Task GetStatsAsync_WhenSpoolHasReadyBatches_ReturnsQueuedRecordCountsBytesAndOldestTimestamp()
    {
        var spoolPath = Path.Combine(root, "spool");
        var spool = new DiskTelemetrySpool(spoolPath);
        var oldest = DateTimeOffset.Parse("2026-06-20T10:18:00Z", CultureInfo.InvariantCulture);
        var newer = oldest.AddMinutes(1);

        await spool.AppendBatchAsync(
            new[]
            {
                TelemetryRecord.Health(oldest, "test", "oldest", TelemetryPriority.Routine),
                TelemetryRecord.Health(newer, "test", "newer", TelemetryPriority.Routine),
            },
            CancellationToken.None);
        await spool.AppendBatchAsync(
            new[] { TelemetryRecord.Health(newer.AddMinutes(1), "test", "latest", TelemetryPriority.Routine) },
            CancellationToken.None);

        var stats = await spool.GetStatsAsync(CancellationToken.None);

        stats.QueuedBatches.Should().Be(2);
        stats.QueuedRecords.Should().Be(3);
        stats.QueuedBytes.Should().BeGreaterThan(0);
        stats.OldestQueuedTimestamp.Should().Be(oldest);
    }

    [Fact]
    public async Task GetStatsAsync_WhenReadyFileCannotBeRead_StillReportsReadyFileBacklog()
    {
        var spoolPath = Path.Combine(root, "spool");
        Directory.CreateDirectory(spoolPath);
        var invalidPath = Path.Combine(spoolPath, "0000000000000000001-invalid.ready");
        await File.WriteAllTextAsync(invalidPath, "null");
        var spool = new DiskTelemetrySpool(spoolPath);
        var timestamp = DateTimeOffset.Parse("2026-06-20T10:19:00Z", CultureInfo.InvariantCulture);
        await spool.AppendBatchAsync(
            new[] { TelemetryRecord.Health(timestamp, "test", "valid", TelemetryPriority.Routine) },
            CancellationToken.None);

        var stats = await spool.GetStatsAsync(CancellationToken.None);

        stats.QueuedBatches.Should().Be(2);
        stats.QueuedRecords.Should().Be(1);
        stats.QueuedBytes.Should().BeGreaterThan(new FileInfo(invalidPath).Length);
        stats.OldestQueuedTimestamp.Should().Be(timestamp);
    }

    [Fact]
    public async Task ReadBatchesAsync_WhenReadyFileDeserializesToNull_ThrowsBatchReadException()
    {
        var spoolPath = Path.Combine(root, "spool");
        Directory.CreateDirectory(spoolPath);
        await File.WriteAllTextAsync(Path.Combine(spoolPath, "0000000000000000001-null.ready"), "null");
        var spool = new DiskTelemetrySpool(spoolPath);

        Func<Task> read = async () => await spool.ReadBatchesAsync(CancellationToken.None);

        await read.Should().ThrowAsync<DiskTelemetrySpool.BatchReadException>();
    }

    [Fact]
    public void Complete_WhenDeleteFails_RemovesBatchFromReadySet()
    {
        var spoolPath = Path.Combine(root, "spool");
        Directory.CreateDirectory(spoolPath);
        var path = Path.Combine(spoolPath, "0000000000000000001.ready");
        File.WriteAllText(path, "{}");
        var batch = new DiskTelemetrySpool.Batch(
            path,
            Array.Empty<TelemetryRecord>(),
            File.Move,
            _ => throw new IOException("delete denied"));

        Action complete = batch.Complete;

        complete.Should().NotThrow();
        File.Exists(path).Should().BeFalse();
        Directory.GetFiles(spoolPath, "*.ready").Should().BeEmpty();
        Directory.GetFiles(spoolPath, "*.sent").Should().ContainSingle();
    }

    private static TelemetryRecord CreateHealthRecord(string name) =>
        TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", name, TelemetryPriority.Routine);

    private static TelemetryRecord CreateLargeRecord(
        string name,
        TelemetryPriority priority = TelemetryPriority.Routine,
        int payloadSize = 3_000) =>
        TelemetryRecord.Health(
            DateTimeOffset.UtcNow,
            "test",
            name,
            priority,
            new Dictionary<string, object?> { ["payload"] = new string('x', payloadSize) });

    private static long TotalSpoolBytes(string spoolPath) =>
        Directory.EnumerateFiles(spoolPath, "*.*")
            .Where(path =>
                path.EndsWith(".ready", StringComparison.Ordinal) ||
                path.EndsWith(".invalid", StringComparison.Ordinal) ||
                path.EndsWith(".sent", StringComparison.Ordinal))
            .Sum(path => new FileInfo(path).Length);
}
