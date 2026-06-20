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
}
