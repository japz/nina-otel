# NinaOtel Disk Spool First Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `DiskOnFailureEnabled` preserve failed export batches durably without rewriting the in-memory pipeline worker.

**Architecture:** Add disk-on-failure as an internal `ITelemetryExporter` decorator around the current collector-health OTLP exporter. Healthy exports remain memory-only; failed batches are appended to an internal disk spool and replayed opportunistically before later live batches.

**Tech Stack:** C# 12, .NET 8, xUnit, FluentAssertions, `System.Text.Json`, atomic file replace/move, existing `TelemetryRecord` contract.

---

## Target File Structure

- Create: `src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs`
  - Internal append-only batch spool.
  - Lazily creates the configured spool directory.
  - Writes each batch to a temporary file, then atomically moves it to a ready file.
  - Reads ready files in oldest-first filename order.
  - Deletes a ready file only after replay succeeds.
  - Uses an internal disk DTO so attribute values do not round-trip as `JsonElement`.
- Create: `src/NinaOtel.Core/Pipeline/DurableTelemetryExporter.cs`
  - Wraps an `ITelemetryExporter`.
  - Replays existing spool batches before exporting the live batch.
  - On non-cancellation export failure, appends the live batch to spool and returns success if append succeeds.
  - Propagates cancellation when the caller's token is canceled.
- Modify: `src/NinaOtel.Plugin/NinaOtelPlugin.cs`
  - Wraps the existing collector-health exporter in `DurableTelemetryExporter` only when `options.Buffer.DiskOnFailureEnabled` is true.
  - Leaves current behavior unchanged when disk-on-failure is false.
- Create: `tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs`
- Create: `tests/NinaOtel.Core.Tests/Pipeline/DurableTelemetryExporterTests.cs`
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/TelemetryPipelineTests.cs`
  - Add one pipeline-level regression that persisted failed batches are not counted as dropped when the durable decorator succeeds.

## Non-Goals For This Slice

- No always-on disk queue during healthy export.
- No full degraded/recovering state machine.
- No background replay when no new telemetry arrives.
- No max-age pruning, max-byte eviction, priority drop policy, or gauge coalescing yet.
- No UI expansion for spool path/bytes/status.
- No public spool API for add-ons.

## Task 1: Disk Spool Codec And Persistence

**Files:**
- Create: `src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs`
- Create: `tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs`

- [ ] **Step 1: Write failing tests for persistence and no constructor IO**

Add tests shaped like:

```csharp
[Fact]
public async Task Constructor_DoesNotCreateSpoolDirectory()
{
    var directory = NewTempSpoolDirectory();

    _ = new DiskTelemetrySpool(directory);

    Directory.Exists(directory).Should().BeFalse();
}

[Fact]
public async Task AppendBatchAsync_CreatesSpoolDirectoryAndPersistsRecords()
{
    var directory = NewTempSpoolDirectory();
    var spool = new DiskTelemetrySpool(directory);
    var record = CreateRecord("first");

    await spool.AppendBatchAsync([record], CancellationToken.None);

    Directory.Exists(directory).Should().BeTrue();
    Directory.EnumerateFiles(directory, "*.ready").Should().ContainSingle();
}

[Fact]
public async Task ReadBatchesAsync_AfterNewInstance_ReturnsPersistedRecords()
{
    var directory = NewTempSpoolDirectory();
    var written = CreateRecord("persisted");
    await new DiskTelemetrySpool(directory).AppendBatchAsync([written], CancellationToken.None);

    var restored = await new DiskTelemetrySpool(directory)
        .ReadBatchesAsync(CancellationToken.None)
        .ToListAsync();

    restored.Should().ContainSingle();
    restored[0].Records.Should().ContainSingle().Which.Should().BeEquivalentTo(written);
}
```

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~DiskTelemetrySpoolTests --no-restore -v:normal
```

Expected before implementation: compile fails because `DiskTelemetrySpool` does not exist.

- [ ] **Step 2: Implement minimal spool types**

Create `DiskTelemetrySpool` with this internal shape:

```csharp
internal sealed class DiskTelemetrySpool
{
    public DiskTelemetrySpool(string spoolPath) { ... }
    public Task AppendBatchAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken) { ... }
    public IAsyncEnumerable<DiskTelemetrySpoolBatch> ReadBatchesAsync(CancellationToken cancellationToken) { ... }
}

internal sealed class DiskTelemetrySpoolBatch
{
    public string Path { get; }
    public IReadOnlyList<TelemetryRecord> Records { get; }
    public void Complete() { ... }
}
```

Required implementation details:

- Expand `%LOCALAPPDATA%` using `Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)`.
- Do not create the directory in the constructor.
- On append, create the directory, serialize to `*.tmp`, flush/close, then move to `*.ready`.
- Generate file names with UTC ticks plus a GUID so lexical order is chronological.
- On read, enumerate `*.ready` ordered by filename.
- Delete only the completed batch file.
- Ignore malformed ready files for now by throwing from read; parser resilience can be a later slice.

- [ ] **Step 3: Add scalar attribute round-trip coverage**

Add:

```csharp
[Fact]
public async Task AppendBatchAsync_PreservesSupportedAttributeValueTypes()
{
    var directory = NewTempSpoolDirectory();
    var record = TelemetryRecord.Span(
        DateTimeOffset.Parse("2026-06-20T01:02:03Z", CultureInfo.InvariantCulture),
        "source",
        "span",
        SpanEventKind.Stop,
        "span-1",
        TelemetryPriority.Important,
        new Dictionary<string, object?>
        {
            ["string"] = "value",
            ["bool"] = true,
            ["int"] = 42,
            ["long"] = 4294967296L,
            ["double"] = 12.25d,
            ["float"] = 2.5f,
            ["null"] = null,
        },
        parentSpanId: "parent-1") with
    {
        TraceId = "trace-1",
    };

    await new DiskTelemetrySpool(directory).AppendBatchAsync([record], CancellationToken.None);

    var batch = await new DiskTelemetrySpool(directory)
        .ReadBatchesAsync(CancellationToken.None)
        .SingleAsync();

    batch.Records.Should().ContainSingle().Which.Should().BeEquivalentTo(record);
}
```

Implement an internal DTO with explicit value kind/type fields. Do not deserialize attribute values into `JsonElement`.

- [ ] **Step 4: Run spool tests**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~DiskTelemetrySpoolTests -v:minimal
```

Expected in GitHub CI: tests pass. Locally, document `NU1301` if restore fails.

## Task 2: Durable Exporter Decorator

**Files:**
- Create: `src/NinaOtel.Core/Pipeline/DurableTelemetryExporter.cs`
- Create: `tests/NinaOtel.Core.Tests/Pipeline/DurableTelemetryExporterTests.cs`

- [ ] **Step 1: Write failing tests for failure spooling and replay**

Add tests shaped like:

```csharp
[Fact]
public async Task ExportAsync_WhenInnerFails_PersistsBatchAndDoesNotThrow()
{
    var spool = new DiskTelemetrySpool(NewTempSpoolDirectory());
    var inner = new ThrowingExporter(new InvalidOperationException("collector offline"));
    var exporter = new DurableTelemetryExporter(inner, spool);
    var record = CreateRecord("failed");

    await exporter.ExportAsync([record], CancellationToken.None);

    var batches = await spool.ReadBatchesAsync(CancellationToken.None).ToListAsync();
    batches.Should().ContainSingle();
    batches[0].Records.Should().ContainSingle().Which.Name.Should().Be("failed");
}

[Fact]
public async Task ExportAsync_WhenSpoolHasRecords_ReplaysOldestBeforeLiveBatch()
{
    var spool = new DiskTelemetrySpool(NewTempSpoolDirectory());
    await spool.AppendBatchAsync([CreateRecord("old")], CancellationToken.None);
    var inner = new RecordingExporter();
    var exporter = new DurableTelemetryExporter(inner, spool);

    await exporter.ExportAsync([CreateRecord("live")], CancellationToken.None);

    inner.Batches.Select(batch => batch.Single().Name).Should().Equal("old", "live");
    var remaining = await spool.ReadBatchesAsync(CancellationToken.None).ToListAsync();
    remaining.Should().BeEmpty();
}
```

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~DurableTelemetryExporterTests --no-restore -v:normal
```

Expected before implementation: compile fails because `DurableTelemetryExporter` does not exist.

- [ ] **Step 2: Implement minimal decorator**

Create:

```csharp
internal sealed class DurableTelemetryExporter : ITelemetryExporter, IDisposable
{
    public DurableTelemetryExporter(ITelemetryExporter inner, DiskTelemetrySpool spool) { ... }
    public Task ExportAsync(IReadOnlyList<TelemetryRecord> records, CancellationToken cancellationToken) { ... }
    public void Dispose() { ... }
}
```

Rules:

- Before live export, iterate `spool.ReadBatchesAsync` and export each batch.
- Call `Complete()` only after the inner export for that batch succeeds.
- If replay fails, do not delete that batch.
- If live export fails with a non-canceled exception, append the live batch to the spool and return success.
- If append fails, rethrow the original export exception so the pipeline still counts a drop.
- If cancellation is requested, propagate cancellation and do not append.
- Dispose the inner exporter if it implements `IDisposable`.

- [ ] **Step 3: Add failure and cancellation edge tests**

Add:

```csharp
[Fact]
public async Task ExportAsync_WhenReplayFails_KeepsSpooledBatchAndSpoolsLiveBatch()
{
    var spool = new DiskTelemetrySpool(NewTempSpoolDirectory());
    await spool.AppendBatchAsync([CreateRecord("old")], CancellationToken.None);
    var inner = new FailingThenRecordingExporter();
    var exporter = new DurableTelemetryExporter(inner, spool);

    await exporter.ExportAsync([CreateRecord("live")], CancellationToken.None);

    var remaining = await spool.ReadBatchesAsync(CancellationToken.None).ToListAsync();
    remaining.SelectMany(batch => batch.Records).Select(record => record.Name)
        .Should().Equal("old", "live");
}

[Fact]
public async Task ExportAsync_WhenCancellationRequested_PropagatesAndDoesNotSpool()
{
    var spool = new DiskTelemetrySpool(NewTempSpoolDirectory());
    var inner = new CancellationThrowingExporter();
    var exporter = new DurableTelemetryExporter(inner, spool);
    using var cts = new CancellationTokenSource();
    await cts.CancelAsync();

    var act = () => exporter.ExportAsync([CreateRecord("live")], cts.Token);

    await act.Should().ThrowAsync<OperationCanceledException>();
    var remaining = await spool.ReadBatchesAsync(CancellationToken.None).ToListAsync();
    remaining.Should().BeEmpty();
}
```

- [ ] **Step 4: Run decorator tests**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter FullyQualifiedName~DurableTelemetryExporterTests -v:minimal
```

Expected in GitHub CI: tests pass. Locally, document `NU1301` if restore fails.

## Task 3: Wire Durable Exporter Into Plugin And Pipeline Regression

**Files:**
- Modify: `src/NinaOtel.Plugin/NinaOtelPlugin.cs`
- Modify: `tests/NinaOtel.Core.Tests/Pipeline/TelemetryPipelineTests.cs`

- [ ] **Step 1: Write failing pipeline regression**

Add:

```csharp
[Fact]
public async Task ExporterFailure_WithDurableExporter_DoesNotCountPersistedBatchAsDropped()
{
    var spool = new DiskTelemetrySpool(NewTempSpoolDirectory());
    var inner = new FailingOnceExporter();
    var durable = new DurableTelemetryExporter(inner, spool);
    var pipeline = new TelemetryPipeline(durable, capacity: 10);
    var first = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "first", TelemetryPriority.Important);
    var second = TelemetryRecord.Health(DateTimeOffset.UtcNow, "test", "second", TelemetryPriority.Routine);

    await pipeline.StartAsync(CancellationToken.None);
    pipeline.TryPublish(first).Should().BeTrue();
    await inner.WaitForAttemptAsync(TimeSpan.FromSeconds(2));
    pipeline.TryPublish(second).Should().BeTrue();
    await inner.WaitForCountAsync(2, TimeSpan.FromSeconds(2));

    pipeline.DroppedRecords.Should().Be(0);
    inner.Records.Select(record => record.Name).Should().Contain(["first", "second"]);
}
```

If helper visibility makes this exact test awkward, use a local always-failing exporter and assert the spool contains `first` plus `DroppedRecords == 0` after the first attempt.

- [ ] **Step 2: Wire plugin creation**

Change `NinaOtelPlugin.CreateCollectorExporter` from returning only `CollectorHealthReportingExporter` to:

```csharp
var collectorExporter = new CollectorHealthReportingExporter(
    new OtlpTelemetryExporter(options),
    NinaOtelOptionsViewModel.UpdateCollectorHealth,
    options.Otlp.Endpoint,
    options.Otlp.Protocol,
    timeProvider);

return options.Buffer.DiskOnFailureEnabled
    ? new DurableTelemetryExporter(
        collectorExporter,
        new DiskTelemetrySpool(options.Buffer.SpoolPath))
    : collectorExporter;
```

Keep construction lazy: `DiskTelemetrySpool` must not create directories until append/read.

- [ ] **Step 3: Run focused pipeline tests**

Run:

```bash
DOTNET=$(mise which dotnet); "$DOTNET" test tests/NinaOtel.Core.Tests/NinaOtel.Core.Tests.csproj --filter "FullyQualifiedName~TelemetryPipelineTests|FullyQualifiedName~DurableTelemetryExporterTests|FullyQualifiedName~DiskTelemetrySpoolTests" -v:minimal
```

Expected in GitHub CI: tests pass. Locally, document `NU1301` if restore fails.

## Task 4: Verification And Release

**Files:**
- Modify: `src/NinaOtel.Plugin/Properties/AssemblyInfo.cs`

- [ ] **Step 1: Run formatting check**

Run:

```bash
git diff --check
```

Expected: exit `0`, no output.

- [ ] **Step 2: Commit behavior**

```bash
git add src/NinaOtel.Core/Pipeline/DiskTelemetrySpool.cs \
  src/NinaOtel.Core/Pipeline/DurableTelemetryExporter.cs \
  src/NinaOtel.Plugin/NinaOtelPlugin.cs \
  tests/NinaOtel.Core.Tests/Pipeline/DiskTelemetrySpoolTests.cs \
  tests/NinaOtel.Core.Tests/Pipeline/DurableTelemetryExporterTests.cs \
  tests/NinaOtel.Core.Tests/Pipeline/TelemetryPipelineTests.cs
git commit -m "Add disk spool exporter for failed telemetry batches"
```

- [ ] **Step 3: Bump alpha**

Update `src/NinaOtel.Plugin/Properties/AssemblyInfo.cs`:

```csharp
[assembly: AssemblyVersion("0.1.0.34")]
[assembly: AssemblyFileVersion("0.1.0.34")]
[assembly: AssemblyInformationalVersion("0.1.0-alpha.35")]
```

Commit:

```bash
git add src/NinaOtel.Plugin/Properties/AssemblyInfo.cs
git commit -m "Bump NinaOtel to alpha 35"
```

- [ ] **Step 4: Push and release**

Push `main`, wait for GitHub `Build`, tag `v0.1.0-alpha.35`, push tag, wait for GitHub `Release`, and verify:

- release URL exists
- `manifest.json` build is `34`
- `NinaOtel.Plugin.zip` SHA256 matches manifest checksum

